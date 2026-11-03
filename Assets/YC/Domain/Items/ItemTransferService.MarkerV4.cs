using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    public sealed partial class ItemTransferService
    {
        public static string MovementV4Digest(object value) => ItemMovementV4.ContractVersion + ":sha256:" + CanonicalHash(value == null ? JValue.CreateNull() : JToken.FromObject(value));
        internal static bool MarkerInventoryZone(ZoneInstanceState zone) => zone != null &&
            new[] { "core.zone.player_supply", "core.zone.card_cube_storage", "core.zone.board_marker_track", "core.zone.box_reserve", "core.zone.box_retired", "core.zone.effect_staging" }.Contains(zone.TemplateId) &&
            zone.VisibilityPolicyId != ItemZoneVisibility.SecretRetired;
        internal static bool IsPlayerMarkerInventory(GameState state, ItemTransferRequest request) => request?.ItemIds != null &&
            new[] { "core.player_marker_storage", "core.cube_storage" }.Contains(request.OperationProfileId) &&
            MarkerInventoryZone(state.ItemZones.Zones.Find(z => z.ZoneId == request.ExpectedSource?.ZoneId)) &&
            MarkerInventoryZone(state.ItemZones.Zones.Find(z => z.ZoneId == request.Destination?.ZoneId)) &&
            request.ItemIds.All(id => state.ItemZones.Items.Any(i => i.ItemId == id && i.TypeId == "core.player_marker" && i.OwnerSubject.Kind == ItemSubjectKind.Player));

        public bool TryPlanMarkersV4(EffectExecutionContext context, out ItemMovementPlanV4 plan, out string reason)
        {
            plan = null; var state = context.State;
            reason = ItemMovementV4.ValidateArguments(context.Node.NormalizedArguments); if (reason.Length > 0) return false;
            if (state.FindPlayer(context.Node.PlayerId) == null) { reason = "invalid_player"; return false; }
            if (!query.TryValidate(state, out reason)) return false;
            if(!ExpectedEmptyDestinationsHold(state,context.Node.NormalizedArguments)){reason="destination_rejected";return false;}
            var intent = new ItemMovementPlanV4 { OwnerEffectId = context.Node.EffectId, ExecutingPlayerId = context.Node.PlayerId,
                ArgumentsDigest = MovementV4Digest(context.Node.NormalizedArguments) };
            foreach (var leg in ItemMovementV4.Legs(context.Node.NormalizedArguments))
            {
                var selector = Value(leg, "selector"); var expected = Value(selector, "expectedSource");
                var from = query.FindZone(state, Text(expected, "zoneId"));
                if (from == null) { reason = "source_zone_changed"; return false; }
                if (!MarkerInventoryZone(from)) { reason = "marker_endpoint_requires_lifecycle"; return false; }
                if (Value(expected, "slotId") != null && !from.LegalSlotIds.Contains(Text(expected, "slotId"))) { reason = "source_zone_changed"; return false; }
                var destination = Value(leg, "destination");
                if (Text(destination, "zoneId").Length > 0 && !MarkerInventoryZone(query.FindZone(state, Text(destination, "zoneId")))) { reason = "marker_endpoint_requires_lifecycle"; return false; }
                if (Value(selector,"itemIds").Items.Count < Number(leg,"requiredCount")) { reason="insufficient_items";return false; }
                foreach (var id in Value(selector, "itemIds").Items.Select(v => v.StringValue))
                {
                    var item = query.FindItem(state, id);
                    if (item == null) { reason = "unknown_item"; return false; }
                    if (item.TypeId != "core.player_marker" || item.OwnerSubject.Kind != ItemSubjectKind.Player) { reason = "marker_item_type_rejected"; return false; }
                    var supply = query.FindZone(state, item.OriginalSupplyZoneId);
                    if (supply?.TemplateId != "core.zone.player_supply" || supply.OwnerPlayerId != item.OwnerSubject.PlayerId) { reason = "original_supply_invalid"; return false; }
                    bool current = item.ZoneId == from.ZoneId;
                    if (!current && !IsPendingMarkerSource(context, item, from)) { reason = "source_zone_changed"; return false; }
                    if (current && Value(expected, "slotId") != null && item.SlotId != Text(expected, "slotId")) { reason = "source_zone_changed"; return false; }
                    string toId = Text(destination, "kind") == "original_supply" ? item.OriginalSupplyZoneId : Text(destination, "zoneId");
                    var to = query.FindZone(state, toId);
                    if (!MarkerInventoryZone(to)) { reason = "marker_endpoint_requires_lifecycle"; return false; }
                    var ordering = templates.Find(to.TemplateId).OrderingKind;
                    bool slotted = ordering == ZoneOrderingKind.Track || ordering == ZoneOrderingKind.FixedSlots;
                    var insertion = Value(leg, "insertion");
                    if (!query.Accepts(to, item) || (slotted ? Text(insertion, "kind") != "slot" || !to.LegalSlotIds.Contains(Text(insertion, "slotId")) : Text(insertion, "kind") != "unordered"))
                    { reason = "destination_rejected"; return false; }
                    if (to.TemplateId == "core.zone.player_supply" && to.OwnerPlayerId != item.OwnerSubject.PlayerId ||
                        to.TemplateId == "core.zone.board_marker_track" && to.OwnerPlayerId > 0 && to.OwnerPlayerId != item.OwnerSubject.PlayerId) { reason = "marker_owner_destination_mismatch"; return false; }
                    string slot = current ? item.SlotId : Text(expected, "slotId");
                    var fact = new MarkerMoveItemFactV4 { ItemId = id, TypeId = item.TypeId, OriginalSupplyZoneId = item.OriginalSupplyZoneId,
                        Owner = GameStateCloneService.DeepClone(item.OwnerSubject), Controller = GameStateCloneService.DeepClone(item.ControllerSubject), ControllerAfter = GameStateCloneService.DeepClone(item.ControllerSubject),
                        From = new ItemPositionRef { ZoneId = from.ZoneId, SlotId = slot },
                        To = new ItemPositionRef { ZoneId = toId, SlotId = Text(Value(leg, "insertion"), "slotId") } };
                    intent.Items.Add(fact); intent.Transfers.Add(MarkerRequest(intent.ExecutingPlayerId, leg, fact));
                }
            }
            // 意向中每个来源及真实owner/原供应在等待前封存；来源尚在途时只接受实际登记的延迟去向。
            plan = intent; reason = ""; return true;
        }
        private static bool IsPendingMarkerSource(EffectExecutionContext context, ItemInstanceState item, ZoneInstanceState source)
        {
            var pending = context.State.EffectRuntime.DeferredResponses.Where(r => r.Status == DeferredResponseStatus.Pending && r.ReservedItemId == item.ItemId &&
                r.ReservationZoneId == item.ZoneId && r.AnsweringPlayerId == context.Node.PlayerId &&
                (source.ParentKind == ZoneParentKind.Item && r.SourceCardInstanceId == source.ParentId || r.OriginalSupplyZoneId == source.ZoneId)).ToList();
            return pending.Count == 1 && context.Registry.TryGetDeferredResponseDestination(pending[0].DestinationHandlerId, out var registration) &&
                registration.TryValidateDeferredDestination(context.State, pending[0], out _);
        }
        private static ItemTransferRequest MarkerRequest(int player, NormalizedValue leg, MarkerMoveItemFactV4 fact) => new ItemTransferRequest {
            ExecutingPlayerId = player, ItemIds = new List<string> { fact.ItemId }, ExpectedSource = GameStateCloneService.DeepClone(fact.From),
            Destination = new ZoneRef { ZoneId = fact.To.ZoneId }, InsertionKind = Text(Value(leg, "insertion"), "kind"), DestinationSlotId = fact.To.SlotId,
            OperationProfileId = "core.player_marker_storage", CarrierPolicyId = "preserve", RequiredCount = 0 };
        internal static bool MatchesMarkerPlanV4(GameState state, EffectNodeRuntimeState node, ItemMovementPlanV4 plan)
        {
            if(ItemMovementV4.Legs(node.NormalizedArguments).Any(l=>Value(Value(l,"selector"),"itemIds").Items.Count<Number(l,"requiredCount")))return false;
            var ids = ItemMovementV4.Legs(node.NormalizedArguments).SelectMany(l => Value(Value(l, "selector"), "itemIds").Items.Select(v => v.StringValue)).ToList();
            if (plan.Items.Any(f => f == null || f.Owner == null || f.Controller == null || f.ControllerAfter == null || f.From == null || f.To == null) || !ids.SequenceEqual(plan.Items.Select(f => f.ItemId))) return false;
            var requests = new List<ItemTransferRequest>(); int index = 0;
            foreach (var leg in ItemMovementV4.Legs(node.NormalizedArguments))
            foreach (var id in Value(Value(leg, "selector"), "itemIds").Items.Select(v => v.StringValue))
            {
                var fact = plan.Items[index++]; var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == id);
                var expected = Value(Value(leg, "selector"), "expectedSource"); var destination = Value(leg, "destination");
                if (item?.TypeId != "core.player_marker" || fact.TypeId != item.TypeId || item.OwnerSubject.Kind != ItemSubjectKind.Player ||
                    !JToken.DeepEquals(JToken.FromObject(fact.Owner), JToken.FromObject(item.OwnerSubject)) ||
                    !JToken.DeepEquals(JToken.FromObject(fact.Controller), JToken.FromObject(item.ControllerSubject)) ||
                    !JToken.DeepEquals(JToken.FromObject(fact.ControllerAfter), JToken.FromObject(item.ControllerSubject)) ||
                    fact.OriginalSupplyZoneId != item.OriginalSupplyZoneId || fact.From.ZoneId != Text(expected, "zoneId") ||
                    Value(expected, "slotId") != null && fact.From.SlotId != Text(expected, "slotId") ||
                    fact.To.ZoneId != (Text(destination, "kind") == "original_supply" ? fact.OriginalSupplyZoneId : Text(destination, "zoneId")) ||
                    fact.To.SlotId != Text(Value(leg, "insertion"), "slotId")) return false;
                requests.Add(MarkerRequest(plan.ExecutingPlayerId, leg, fact));
            }
            return MovementV4Digest(requests) == MovementV4Digest(plan.Transfers);
        }
        public ItemTransferValidationResult ValidateMarkersV4(EffectExecutionContext context, ItemMovementPlanV4 plan)
        {
            if (!query.TryValidate(context.State, out var reason)) return Invalid(reason);
            if(!ExpectedEmptyDestinationsHold(context.State,context.Node.NormalizedArguments))return Invalid("destination_rejected");
            foreach (var request in plan.Transfers)
            {
                ValidateExecutionGuards(context, request);
                var valid = ValidateCore(context.State, request, false, false, false);
                if (!valid.IsValid) return valid;
            }
            var preview = GameStateCloneService.DeepClone(context.State);
            var moved = ApplyMarkerPlan(preview, plan);
            foreach (var leg in ItemMovementV4.Legs(context.Node.NormalizedArguments))
            {
                var ids = Value(Value(leg, "selector"), "itemIds").Items.Select(v => v.StringValue).ToList();
                if (moved.MovedItemIds.Count(ids.Contains) < Number(leg, "requiredCount")) return Invalid("insufficient_items");
            }
            if (!query.TryValidate(preview, out reason)) return Invalid(reason);
            return new ItemTransferValidationResult { IsValid = true };
        }
        private static ItemTransferResult ApplyMarkerPlan(GameState state, ItemMovementPlanV4 plan)
        {
            var result = new ItemTransferResult();
            foreach (var request in plan.Transfers)
            {
                var item = state.ItemZones.Items.Single(i => i.ItemId == request.ItemIds[0]);
                if (item.ZoneId == request.Destination.ZoneId && item.SlotId == request.DestinationSlotId) continue;
                var moved = Apply(state, request);
                result.MovedItemIds.AddRange(moved.MovedItemIds); result.PositionChanges.AddRange(moved.PositionChanges);
            }
            result.MovedCount = result.MovedItemIds.Count; result.Changed = result.MovedCount > 0; return result;
        }
        private static List<MarkerMoveOrderFactV4> MarkerOrders(GameState state, ItemMovementPlanV4 plan) => plan.Transfers
            .SelectMany(t => new[] { t.ExpectedSource.ZoneId, t.Destination.ZoneId }).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => { var order = state.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == id); return new MarkerMoveOrderFactV4 { ZoneId = id, HasOrder = order != null,
                Revision = order?.OrderRevision ?? 0, ItemIds = order == null ? state.ItemZones.Items.Where(i => i.ZoneId == id).Select(i => i.ItemId).ToList() : new List<string>(order.ItemIds) }; }).ToList();
        private static List<MarkerMoveUsageFactV4> MarkerBindings(GameState state) => UsageTracks.Project(state).OrderBy(v => v.Binding.BindingId, StringComparer.Ordinal)
            .Select(v => new MarkerMoveUsageFactV4 { BindingId = v.Binding.BindingId, MarkerItemId = v.MarkerItemId, ZoneId = v.ZoneId, SlotId = v.SlotId }).ToList();
        public void RelocateMarkersV4(EffectExecutionContext context, ItemMovementPlanV4 plan)
        {
            // 已经整组预演；这里只在当前Host工作副本内提交一次，外层内核统一发布RuleCommit。
            var receipt = new TransferReceiptV4 { OwnerEffectId = context.Node.EffectId, ExecutingPlayerId = context.Node.PlayerId, ArgumentsDigest = plan.ArgumentsDigest,
                OperationId = StableIdFactory.Create("item-move-v4", context.Node.EffectId), Items = GameStateCloneService.DeepClone(plan.Items),
                OrdersBefore = MarkerOrders(context.State, plan), BindingsBefore = MarkerBindings(context.State) };
            receipt.Result = ApplyMarkerPlan(context.State, plan);
            UsageTracks.RegisterDirectPlacements(context, receipt.Items);
            receipt.OrdersAfter = MarkerOrders(context.State, plan); receipt.BindingsAfter = MarkerBindings(context.State);
            foreach (var sourceId in plan.Transfers.Select(t => t.ExpectedSource.ZoneId).Distinct(StringComparer.Ordinal))
            {
                var zone = context.State.ItemZones.Zones.Single(z => z.ZoneId == sourceId);
                if (zone.ParentKind != ZoneParentKind.Item || zone.Configuration?.EmptyExitPolicyId != "core.discard_carrier_when_empty" || context.State.ItemZones.Items.Any(i => i.ZoneId == sourceId)) continue;
                var card = context.State.ItemZones.Items.Single(i => i.ItemId == zone.ParentId);
                if (!context.State.ItemZones.Zones.Any(z => z.ZoneId == card.ZoneId && z.TemplateId == "core.zone.character_persistent")) continue;
                if (receipt.CarrierExits.Any(e => e.CardItemId == card.ItemId)) continue;
                receipt.CarrierExits.Add(new MarkerCarrierExitV4 { CardItemId = card.ItemId, SourceStorageZoneId = sourceId, PlayerId = card.ControllerSubject.PlayerId,
                    FromZoneId = card.ZoneId, ToZoneId = FindZone(context.State, "discard", card.ControllerSubject.PlayerId).ZoneId });
            }
            receipt.ReceiptId = StableIdFactory.Create("transfer-receipt-v4", context.Node.EffectId, MarkerReceiptDigest(receipt));
            context.Node.PositionChanges.AddRange(GameStateCloneService.DeepClone(receipt.Result.PositionChanges));
            plan.Receipt = receipt;
        }
        private static string MarkerReceiptDigest(TransferReceiptV4 r) => MovementV4Digest(new { r.ContractVersion, r.OperationId, r.OwnerEffectId, r.ExecutingPlayerId,
            r.ArgumentsDigest, r.Outcome, r.Items, r.OrdersBefore, r.OrdersAfter, r.BindingsBefore, r.BindingsAfter, r.EventIds, r.CarrierExits, r.LifecycleItems, r.Result });
        internal static bool ValidateMarkerReceiptV4(GameState state, EffectNodeRuntimeState node, ItemMovementPlanV4 plan)
        {
            var r = plan.Receipt;
            if (r.ContractVersion != ItemMovementV4.ContractVersion || r.OwnerEffectId != node.EffectId || r.ExecutingPlayerId != node.PlayerId || r.ArgumentsDigest != plan.ArgumentsDigest ||
                r.OperationId != StableIdFactory.Create("item-move-v4", node.EffectId) || r.Items == null || r.Result?.MovedItemIds == null || r.Result.PositionChanges == null ||
                r.OrdersBefore == null || r.OrdersAfter == null || r.BindingsBefore == null || r.BindingsAfter == null || r.EventIds == null || r.EventIds.Count != 0 || r.Outcome != "completed" || r.LifecycleItems == null || r.LifecycleItems.Count != 0 || r.CarrierExits == null ||
                MovementV4Digest(r.Items) != MovementV4Digest(plan.Items) || r.ReceiptId != StableIdFactory.Create("transfer-receipt-v4", node.EffectId, MarkerReceiptDigest(r))) return false;
            foreach(var leg in ItemMovementV4.Legs(node.NormalizedArguments))
                if(Flag(Value(leg,"destination"),"mustBeEmpty"))
                {
                    var before=r.OrdersBefore.SingleOrDefault(o=>o.ZoneId==Text(Value(leg,"destination"),"zoneId"));
                    if(before!=null&&before.ItemIds.Count!=0||before==null&&r.Result.MovedCount>0)return false;
                }
            var moved = plan.Items.Where(f => f.From.ZoneId != f.To.ZoneId || f.From.SlotId != f.To.SlotId).ToList();
            var changes = moved.Select(f => new ItemPositionChange { ItemId = f.ItemId, From = f.From, To = f.To }).ToList();
            if (!r.Result.MovedItemIds.SequenceEqual(moved.Select(f => f.ItemId)) || r.Result.MovedCount != moved.Count || r.Result.Changed != (moved.Count > 0) ||
                MovementV4Digest(changes) != MovementV4Digest(r.Result.PositionChanges) || MovementV4Digest(changes) != MovementV4Digest(node.PositionChanges)) return false;
            foreach (var leg in ItemMovementV4.Legs(node.NormalizedArguments))
            {
                var ids = Value(Value(leg, "selector"), "itemIds").Items.Select(v => v.StringValue).ToList();
                if (moved.Count(f => ids.Contains(f.ItemId)) < Number(leg, "requiredCount")) return false;
            }
            // 正式Lua续接可把后继挂在同一scope根；必须有真实分发收据，不能冒充自动离场。
            var automaticIds = node.ChildEffectIds.Where(id => !state.EffectRuntime.DispatchReceipts.Any(d => d.Succeeded && d.AttachedEffectIds.Contains(id))).ToList();
            if (automaticIds.Count != r.CarrierExits.Count || r.CarrierExits.Select(e => e.CardItemId).Distinct(StringComparer.Ordinal).Count() != r.CarrierExits.Count ||
                node.FlowStage == "v4_carrier_exit" && r.CarrierExits.Count == 0) return false;
            for (int index = 0; index < r.CarrierExits.Count; index++)
            {
                var exit = r.CarrierExits[index]; var source = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == exit?.SourceStorageZoneId);
                var child = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == automaticIds[index]);
                if (exit == null || source?.ParentId != exit.CardItemId || source.ParentKind != ZoneParentKind.Item || source.Configuration?.EmptyExitPolicyId != "core.discard_carrier_when_empty" ||
                    !r.Items.Any(f => f.From.ZoneId == source.ZoneId) || r.OrdersAfter.SingleOrDefault(o => o.ZoneId == source.ZoneId)?.ItemIds.Count != 0 ||
                    !state.ItemZones.Zones.Any(z => z.ZoneId == exit.FromZoneId && z.TemplateId == "core.zone.character_persistent" && z.OwnerPlayerId == exit.PlayerId) ||
                    FindZone(state, "discard", exit.PlayerId).ZoneId != exit.ToZoneId || child?.ParentEffectId != node.EffectId || child.PlayerId != exit.PlayerId ||
                    child.EffectId != StableIdFactory.Create("effect", node.EffectId, "child", "child_effect", node.ChildEffectIds.IndexOf(child.EffectId).ToString(CultureInfo.InvariantCulture),
                        LuaDomainEffectTypeIds.MoveCharacterCard, node.EffectId + ":empty:" + exit.CardItemId) ||
                    child.EffectTypeId != LuaDomainEffectTypeIds.MoveCharacterCard || MovementV4Digest(child.NormalizedArguments) != MovementV4Digest(ItemMovementV4.ExitSpec(exit).NormalizedArguments)) return false;
            }
            return node.Status != EffectNodeStatus.Completed && node.PendingOutcome != EffectPendingOutcome.Completed ||
                MovementV4Digest(node.NormalizedResult) == MovementV4Digest(ItemMovementV4.Result(r));
        }
    }
}
