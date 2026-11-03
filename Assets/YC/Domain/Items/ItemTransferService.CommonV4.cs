using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    public sealed partial class ItemTransferService
    {
        private static bool ExpectedEmptyDestinationsHold(GameState state,NormalizedValue args)=>ItemMovementV4.Legs(args).All(leg=>
            !Flag(Value(leg,"destination"),"mustBeEmpty")||!state.ItemZones.Items.Any(i=>i.ZoneId==Text(Value(leg,"destination"),"zoneId")));
        internal static MarkerMoveOrderFactV4 CaptureOrderV4(GameState state,string id)=>OrderFact(state,id);
        internal static List<MarkerMoveUsageFactV4> CaptureBindingsV4(GameState state)=>MarkerBindings(state);
        internal static string ReceiptDigestV4(TransferReceiptV4 receipt)=>MarkerReceiptDigest(receipt);
        private bool TryCommonGuard(EffectExecutionContext context, ItemTransferRequest request, out string reason)
        {
            reason = "";
            var from = query.FindZone(context.State, request.ExpectedSource.ZoneId); var to = query.FindZone(context.State, request.Destination.ZoneId);
            // 先核已知源区的生命周期，缺少原始供应不能掩盖地图token的非法库存请求。
            if (request.OperationProfileId == "core.token_storage" && from != null &&
                !new[] { "core.zone.box_reserve", "core.zone.box_retired", "core.zone.effect_staging" }.Contains(from.TemplateId))
            { reason = "token_storage_requires_lifecycle"; return false; }
            if (from == null || to == null) { reason = "source_zone_changed"; return false; }
            if (ItemMovementV4.StyleReturnOperation(context.Node.NormalizedArguments) && request.ItemIds.Any(id => !CityStyleMarkerReturn.EligibleSource(context.State, context.Node.PlayerId, id, from.ZoneId)))
            { reason = "city_style_marker_return_not_permitted"; return false; }
            // 普通库存转移不猜测部署/盖放/地图/建设授权；这些策略由各自operation继续补齐。
            if (new[] { from, to }.Any(z => new[] { "core.zone.influence_slots", "core.zone.score_track", "core.zone.covered_cubes", FacilityLayerQuery.CellTemplate, FacilityLayerQuery.CoveredTemplate }.Contains(z.TemplateId)))
            { if(!FacilityPlacementMovement.Allows(context,request)){reason="operation_profile_rejected";return false;} }
            if (request.OperationProfileId != "core.player_marker_storage" && new[] { from, to }.Any(z => z.TemplateId == "core.zone.board_marker_track")) { reason = "operation_profile_rejected"; return false; }
            if (request.OperationProfileId == "core.adjacent_location_token_move")
            {
                if (!ItemMovementV4.MapTokenOperation(context.Node.NormalizedArguments)) { reason = "operation_profile_rejected"; return false; }
                reason = ItemMovementV4.ValidateMapTokenMove(context);
                if (reason.Length > 0) return false;
            }
            if (request.OperationProfileId == "core.character_card_transfer" && request.ItemIds.Any(id => context.State.ItemZones.Items.Any(i => i.ItemId == id && i.ControllerSubject.Kind == ItemSubjectKind.Player && i.ControllerSubject.PlayerId != context.Node.PlayerId)))
            { reason = "transfer_permission_denied"; return false; }
            try { ValidateExecutionGuards(context, request); }
            catch (InvalidOperationException error) { reason = error.Message; return false; }
            return true;
        }
        private static ItemTransferRequest CommonRequest(GameState state, int player, NormalizedValue leg, IEnumerable<string> selected)
        {
            var ids = selected.ToList(); var selector = Value(leg, "selector"); var item = ids.Count == 0 ? null : state.ItemZones.Items.SingleOrDefault(i => i.ItemId == ids[0]);
            string source = Text(selector, "mode") == "explicit" ? Text(Value(selector, "expectedSource"), "zoneId") : Text(selector, "mode") == "top" ? Text(selector, "zoneId") : item?.ZoneId ?? "";
            string destination = Text(Value(leg, "destination"), "kind") == "original_supply" ? item?.OriginalSupplyZoneId ?? "" : Text(Value(leg, "destination"), "zoneId");
            return new ItemTransferRequest { ExecutingPlayerId = player, ItemIds = ids, ExpectedSource = new ItemPositionRef { ZoneId = source,
                SlotId = Text(selector, "mode") == "explicit" ? Text(Value(selector, "expectedSource"), "slotId") : "" }, Destination = new ZoneRef { ZoneId = destination },
                InsertionKind = Text(Value(leg, "insertion"), "kind"), DestinationSlotId = Text(Value(leg, "insertion"), "slotId"), OperationProfileId = Text(leg, "operationProfileId"), CarrierPolicyId = "preserve", RequiredCount = 0 };
        }
        private static MarkerMoveOrderFactV4 OrderFact(GameState state, string id)
        {
            var order = state.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == id);
            return new MarkerMoveOrderFactV4 { ZoneId = id, HasOrder = order != null, Revision = order?.OrderRevision ?? 0,
                ItemIds = order == null ? state.ItemZones.Items.Where(i => i.ZoneId == id).Select(i => i.ItemId).ToList() : order.ItemIds.ToList() };
        }
        private static ItemSubjectRef DestinationController(GameState state, ItemInstanceState item, string target)
        {
            var z = state.ItemZones.Zones.Single(v => v.ZoneId == target);
            return z.TemplateId.StartsWith("core.zone.character_", StringComparison.Ordinal) || z.TemplateId == "core.zone.player_rewards" || z.TemplateId == FacilityLayerQuery.CellTemplate || z.TemplateId == FacilityLayerQuery.CoveredTemplate ? ItemSubjectRef.Player(z.OwnerPlayerId) : GameStateCloneService.DeepClone(item.ControllerSubject);
        }
        internal bool TryPlanCommonV4(EffectExecutionContext context, out CommonItemMovementPlanV4 plan, out string reason)
        {
            plan = null; reason = ""; var state = context.State; var args = ItemMovementV4.CommonArguments(state, context.Node);
            if (!ExpectedEmptyDestinationsHold(state,args)) { reason="destination_rejected";return false; }
            if (state.FindPlayer(context.Node.PlayerId) == null) { reason = "invalid_player"; return false; }
            if (!query.TryValidate(state, out reason)) return false;
            var pending = new CommonItemMovementPlanV4 { OwnerEffectId = context.Node.EffectId, ExecutingPlayerId = context.Node.PlayerId, ArgumentsDigest = MovementV4Digest(context.Node.NormalizedArguments),
                Outcome = "completed" };
            if(!FacilityPlacementMovement.TryBind(context,out var placement,out reason))return false;pending.FacilityPlacement=placement;
            foreach (var leg in ItemMovementV4.Legs(args))
            {
                var selector = Value(leg, "selector"); string mode = Text(selector, "mode"); List<string> ids;
                if (mode == "explicit") ids = Value(selector, "itemIds").Items.Select(v => v.StringValue).ToList();
                else
                {
                    var source = query.FindZone(state, Text(selector, "zoneId"));
                    if (source == null || templates.Find(source.TemplateId).OrderingKind != ZoneOrderingKind.Deck || !CanAccess(state, source, context.Node.PlayerId, true)) { reason = "top_source_rejected"; return false; }
                    if (types.Find(Text(selector, "itemTypeId")) == null) { reason = "unknown_item_type"; return false; }
                    ids = query.GetTopItemIds(state, source.ZoneId, Number(selector, "count")).ToList();
                    if (ids.Any(id => !types.IsAssignableTo(state.ItemZones.Items.Single(i => i.ItemId == id).TypeId, Text(selector, "itemTypeId")))) { reason = "top_item_type_rejected"; return false; }
                    pending.BoundOrders.Add(OrderFact(state, source.ZoneId));
                }
                if (ids == null || ids.Count < Number(leg, "requiredCount")) { reason = "insufficient_items"; return false; }
                if (ids.Count == 0)
                {
                    var empty = CommonRequest(state, context.Node.PlayerId, leg, ids);
                    if (Text(Value(leg, "destination"), "kind") != "original_supply")
                    {
                        if (!TryCommonGuard(context, empty, out reason)) return false;
                        var target = query.FindZone(state, empty.Destination.ZoneId);
                        if (target.TemplateId == FacilityLayerQuery.SupplyTemplate && state.ItemZones.Items.Any(i => i.ZoneId == target.ZoneId)) { reason = "destination_rejected"; return false; }
                        var valid = ValidateCore(state, empty, true, false, false); if (!valid.IsValid) { reason = valid.ReasonCode; return false; }
                    }
                }
                pending.LegSizes.Add(ids.Count);
                foreach (string id in ids)
                {
                    var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == id);
                    if (item == null) { reason = "unknown_item"; return false; }
                    var transfer = CommonRequest(state, context.Node.PlayerId, leg, new[] { id });
                    if (item.ZoneId != transfer.ExpectedSource.ZoneId || transfer.ExpectedSource.SlotId.Length > 0 && item.SlotId != transfer.ExpectedSource.SlotId) { reason = "source_zone_changed"; return false; }
                    if (!TryCommonGuard(context, transfer, out reason)) return false;
                    if (Text(Value(leg, "destination"), "kind") == "original_supply" && (item.OriginalSupplyZoneId.Length == 0 || item.OwnerSubject.Kind == ItemSubjectKind.Player &&
                        state.ItemZones.Zones.Single(z => z.ZoneId == transfer.Destination.ZoneId).OwnerPlayerId > 0 &&
                        state.ItemZones.Zones.Single(z => z.ZoneId == transfer.Destination.ZoneId).OwnerPlayerId != item.OwnerSubject.PlayerId)) { reason = "original_supply_invalid"; return false; }
                    pending.Items.Add(new MarkerMoveItemFactV4 { ItemId = id, TypeId = item.TypeId, OriginalSupplyZoneId = item.OriginalSupplyZoneId, Owner = GameStateCloneService.DeepClone(item.OwnerSubject),
                        Controller = GameStateCloneService.DeepClone(item.ControllerSubject), ControllerAfter = DestinationController(state, item, transfer.Destination.ZoneId),
                        From = new ItemPositionRef { ZoneId = item.ZoneId, SlotId = item.SlotId }, To = new ItemPositionRef { ZoneId = transfer.Destination.ZoneId, SlotId = transfer.DestinationSlotId } });
                    var previous = pending.Transfers.LastOrDefault();
                    bool combine = previous != null && previous.ExpectedSource.ZoneId == transfer.ExpectedSource.ZoneId &&
                        previous.ExpectedSource.SlotId == transfer.ExpectedSource.SlotId && previous.Destination.ZoneId == transfer.Destination.ZoneId && previous.InsertionKind == transfer.InsertionKind &&
                        previous.DestinationSlotId == transfer.DestinationSlotId && previous.OperationProfileId == transfer.OperationProfileId;
                    // 每个原声明保持独立段；同段连续相同端点组合，保留top插入的原调用顺序。
                    int currentLegStart = pending.LegSizes.Take(pending.LegSizes.Count - 1).Sum();
                    if (combine && pending.Items.Count - 1 > currentLegStart) previous.ItemIds.Add(id); else pending.Transfers.Add(transfer);
                }
            }
            plan = pending; return true;
        }
        private static List<string> CommonTouchedZones(GameState state, EffectNodeRuntimeState node, CommonItemMovementPlanV4 plan, IEnumerable<MarkerMoveItemFactV4> lifecycle) =>
            plan.Transfers.SelectMany(t => new[] { t.ExpectedSource.ZoneId, t.Destination.ZoneId }).Concat(plan.BoundOrders.Select(o => o.ZoneId))
                .Concat(ItemMovementV4.Legs(ItemMovementV4.CommonArguments(state, node)).SelectMany(l => new[] { Text(Value(l, "destination"), "zoneId"), Text(Value(Value(l, "selector"), "expectedSource"), "zoneId") }))
                .Concat(lifecycle.SelectMany(f => new[] { f.From.ZoneId, f.To.ZoneId })).Where(id => !string.IsNullOrEmpty(id) && state.ItemZones.Zones.Any(z => z.ZoneId == id)).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList();
        private static bool SameOrder(GameState state, MarkerMoveOrderFactV4 fact) => MovementV4Digest(OrderFact(state, fact.ZoneId)) == MovementV4Digest(fact);
        private static bool NoChange(GameState state, ItemTransferRequest request)
        {
            if (request.ExpectedSource.ZoneId != request.Destination.ZoneId) return false;
            if (request.InsertionKind == "unordered" || request.InsertionKind == "slot") return request.ItemIds.All(id => state.ItemZones.Items.Single(i => i.ItemId == id).SlotId == request.DestinationSlotId);
            var order = state.ItemZones.Orders.Single(o => o.ZoneId == request.ExpectedSource.ZoneId).ItemIds;
            var next = order.Where(id => !request.ItemIds.Contains(id)).ToList();
            if (request.InsertionKind == "top") next.InsertRange(0, request.ItemIds); else next.AddRange(request.ItemIds);
            return next.SequenceEqual(order);
        }
        private static ItemTransferResult MergeResults(IEnumerable<ItemPositionChange> changes)
        {
            var list = changes.ToList(); return new ItemTransferResult { Changed = list.Count > 0, MovedCount = list.Count, MovedItemIds = list.Select(c => c.ItemId).ToList(), PositionChanges = GameStateCloneService.DeepClone(list) };
        }
        internal bool TryCommitCommonV4(EffectExecutionContext context, CommonItemMovementPlanV4 plan, out List<EffectEventRequest> events, out string reason)
        {
            events = new List<EffectEventRequest>(); reason = ""; var state = context.State;
            if (!ExpectedEmptyDestinationsHold(state,ItemMovementV4.CommonArguments(state,context.Node))) { reason="destination_rejected";return false; }
            if(plan.FacilityPlacement!=null&&!FacilityPlacementMovement.Revalidate(context,plan.FacilityPlacement,out reason))return false;
            if (plan.BoundOrders.Any(o => !SameOrder(state, o))) { reason = "source_order_changed"; return false; }
            foreach (var expected in Value(context.Node.NormalizedArguments, "expectedOrders")?.Items ?? new List<NormalizedValue>())
            {
                // 空顶部表示没有额外的顶部断言；完整digest/revision始终复验。空目标另由mustBeEmpty约束。
                string id = Text(expected, "zoneId"); var order = OrderFact(state,id);
                if (!plan.Transfers.Any(t => t.ExpectedSource.ZoneId == id || t.Destination.ZoneId == id) || !state.ItemZones.Zones.Any(z=>z.ZoneId==id) || order.Revision != Number(expected, "orderRevision") ||
                    (Text(expected,"expectedTopItemId").Length>0 && (order.HasOrder?order.ItemIds.FirstOrDefault()??"":"") != Text(expected, "expectedTopItemId")) || FacilityLayerQuery.Digest(state, id) != Text(expected, "digest")) { reason = "transfer_batch_order_changed"; return false; }
            }
            foreach (var fact in plan.Items)
            {
                var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == fact.ItemId);
                if (item == null || item.ZoneId != fact.From.ZoneId || item.SlotId != fact.From.SlotId || MovementV4Digest(item.ControllerSubject) != MovementV4Digest(fact.Controller)) { reason = "source_zone_changed"; return false; }
            }
            var preview = GameStateCloneService.DeepClone(state); var work = new EffectExecutionContext(preview, preview.EffectRuntime.EffectNodes.Single(n => n.EffectId == context.Node.EffectId), context.Registry);
            foreach (var transfer in plan.Transfers.Where(t => t.OperationProfileId != "core.character_card_transfer"))
            {
                if (!TryCommonGuard(work, transfer, out reason)) return false;
                var valid = ValidateCore(preview, transfer, false, false, plan.FacilityPlacement!=null); if (!valid.IsValid) { reason = valid.ReasonCode; return false; }
            }
            var main = new List<ItemPositionChange>();
            foreach (var transfer in plan.Transfers)
            {
                if (!TryCommonGuard(work, transfer, out reason)) return false;
                if (NoChange(preview, transfer)) continue;
                if (transfer.OperationProfileId == "core.character_card_transfer")
                {
                    var result = CharacterCardMovement.Execute(work, transfer);
                    if (result.PendingOutcome != EffectPendingOutcome.Completed) { reason = result.FailureReason; return false; }
                    var moved = Value(result.NormalizedResult, "movedItemIds").Items.Select(v => v.StringValue).ToList();
                    main.AddRange(moved.Select(id => work.Node.PositionChanges.Last(c => c.ItemId == id)));
                    events.AddRange(result.Events);
                }
                else
                {
                    var moved = Apply(preview, transfer); work.Node.PositionChanges.AddRange(GameStateCloneService.DeepClone(moved.PositionChanges)); main.AddRange(moved.PositionChanges);
                }
            }
            int offset = 0, legIndex = 0;
            foreach (var leg in ItemMovementV4.Legs(ItemMovementV4.CommonArguments(state, context.Node)))
            {
                var ids = plan.Items.Skip(offset).Take(plan.LegSizes[legIndex]).Select(f => f.ItemId).ToList();
                if (plan.Outcome == "completed" && main.Count(c => ids.Contains(c.ItemId)) < Number(leg, "requiredCount")) { reason = "insufficient_items"; return false; }
                offset += plan.LegSizes[legIndex++];
            }
            if (!query.TryValidate(preview, out reason)) return false;
            if (ItemMovementV4.MapTokenOperation(context.Node.NormalizedArguments) && main.Count == 1)
                events.Add(ItemMovementV4.MapTokenEvent(context.Node, plan.Items.Single(), state));
            var receipt = new TransferReceiptV4 { OwnerEffectId = context.Node.EffectId, ExecutingPlayerId = context.Node.PlayerId, ArgumentsDigest = plan.ArgumentsDigest,
                OperationId = StableIdFactory.Create("item-move-v4", context.Node.EffectId), Items = GameStateCloneService.DeepClone(plan.Items), Result = MergeResults(main), Outcome = plan.Outcome,
                BindingsBefore = MarkerBindings(state), BindingsAfter = MarkerBindings(preview), EventIds = events.Select(e => e.EventId).ToList() };
            UsageTracks.RegisterDirectPlacements(work, receipt.Items);
            receipt.BindingsAfter = MarkerBindings(preview);
            receipt.LifecycleItems = work.Node.PositionChanges.Where(c => !plan.Items.Any(f => f.ItemId == c.ItemId)).Select(c => {
                var before = state.ItemZones.Items.Single(i => i.ItemId == c.ItemId); var after = preview.ItemZones.Items.Single(i => i.ItemId == c.ItemId);
                return new MarkerMoveItemFactV4 { ItemId = c.ItemId, TypeId = before.TypeId, OriginalSupplyZoneId = before.OriginalSupplyZoneId, Owner = GameStateCloneService.DeepClone(before.OwnerSubject),
                    Controller = GameStateCloneService.DeepClone(before.ControllerSubject), ControllerAfter = GameStateCloneService.DeepClone(after.ControllerSubject), From = GameStateCloneService.DeepClone(c.From), To = GameStateCloneService.DeepClone(c.To) }; }).ToList();
            var zones = CommonTouchedZones(state, context.Node, plan, receipt.LifecycleItems);
            receipt.OrdersBefore = zones.Select(id => OrderFact(state, id)).ToList(); receipt.OrdersAfter = zones.Select(id => OrderFact(preview, id)).ToList();
            foreach (string sourceId in plan.Transfers.Select(t => t.ExpectedSource.ZoneId).Distinct())
            {
                var zone = preview.ItemZones.Zones.Single(z => z.ZoneId == sourceId);
                if (zone.ParentKind != ZoneParentKind.Item || zone.Configuration?.EmptyExitPolicyId != "core.discard_carrier_when_empty" || preview.ItemZones.Items.Any(i => i.ZoneId == sourceId)) continue;
                var card = preview.ItemZones.Items.Single(i => i.ItemId == zone.ParentId);
                if (preview.ItemZones.Zones.Any(z => z.ZoneId == card.ZoneId && z.TemplateId == "core.zone.character_persistent") && !receipt.CarrierExits.Any(e => e.CardItemId == card.ItemId))
                    receipt.CarrierExits.Add(new MarkerCarrierExitV4 { CardItemId = card.ItemId, SourceStorageZoneId = sourceId, PlayerId = card.ControllerSubject.PlayerId, FromZoneId = card.ZoneId, ToZoneId = FindZone(preview, "discard", card.ControllerSubject.PlayerId).ZoneId });
            }
            receipt.ReceiptId = StableIdFactory.Create("transfer-receipt-v4", context.Node.EffectId, MarkerReceiptDigest(receipt));
            work.Node.CommonItemMovementV4.Receipt = receipt;
            GameStateCloneService.CopyTo(state, preview); return true;
        }
    }
}
