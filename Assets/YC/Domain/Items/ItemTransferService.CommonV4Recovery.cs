using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    public sealed partial class ItemTransferService
    {
        private static bool CommonPlanMatches(GameState state, EffectNodeRuntimeState node, CommonItemMovementPlanV4 plan)
        {
            var legs = ItemMovementV4.Legs(ItemMovementV4.CommonArguments(state, node)).ToList();
            if (plan.ContractVersion != ItemMovementV4.ContractVersion || plan.OwnerEffectId != node.EffectId || plan.ExecutingPlayerId != node.PlayerId ||
                plan.ArgumentsDigest != MovementV4Digest(node.NormalizedArguments) || plan.LegSizes == null || plan.LegSizes.Count != legs.Count || plan.LegSizes.Any(n => n < 0 || n > 99) ||
                plan.Items == null || plan.Items.Count > 99 || plan.LegSizes.Sum() != plan.Items.Count || plan.Items.Any(f => f == null || f.Owner == null || f.Controller == null || f.ControllerAfter == null || f.From == null || f.To == null) ||
                plan.Items.Select(f => f.ItemId).Distinct(StringComparer.Ordinal).Count() != plan.Items.Count || plan.Transfers == null || plan.BoundOrders == null ||
                plan.Outcome != "completed") return false;
            var expected = new List<ItemTransferRequest>(); int offset = 0, topOrders = 0;
            for (int index = 0; index < legs.Count; index++)
            {
                var leg = legs[index]; var facts = plan.Items.Skip(offset).Take(plan.LegSizes[index]).ToList(); offset += facts.Count;
                var selector = Value(leg, "selector"); string mode = Text(selector, "mode"); var ids = facts.Select(f => f.ItemId).ToList();
                if(plan.Outcome=="completed"&&ids.Count<Number(leg,"requiredCount"))return false;
                if (mode == "explicit" && !ids.SequenceEqual(Value(selector, "itemIds").Items.Select(v => v.StringValue))) return false;
                if (mode == "top")
                {
                    topOrders++; var order = plan.BoundOrders.SingleOrDefault(o => o.ZoneId == Text(selector, "zoneId"));
                    if (order == null || !order.HasOrder || order.Revision < 0 || order.ItemIds == null || order.ItemIds.Distinct().Count() != order.ItemIds.Count ||
                        !ids.SequenceEqual(order.ItemIds.Take(Number(selector, "count"))) || facts.Any(f => f.From.ZoneId != order.ZoneId || !ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(f.TypeId, Text(selector, "itemTypeId")))) return false;
                }
                var ownRequests=new List<ItemTransferRequest>();
                foreach (var fact in facts)
                {
                    var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == fact.ItemId);
                    if (item == null || item.TypeId != fact.TypeId || MovementV4Digest(item.OwnerSubject) != MovementV4Digest(fact.Owner) || item.OriginalSupplyZoneId != fact.OriginalSupplyZoneId) return false;
                    var from = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == fact.From.ZoneId); var to = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == fact.To.ZoneId);
                    if (from == null || to == null || mode == "explicit" && (fact.From.ZoneId != Text(Value(selector, "expectedSource"), "zoneId") ||
                        Value(Value(selector, "expectedSource"), "slotId") != null && fact.From.SlotId != Text(Value(selector, "expectedSource"), "slotId")) ||
                        fact.To.ZoneId != (Text(Value(leg, "destination"), "kind") == "original_supply" ? fact.OriginalSupplyZoneId : Text(Value(leg, "destination"), "zoneId")) || fact.To.SlotId != Text(Value(leg, "insertion"), "slotId")) return false;
                    var original = GameStateCloneService.DeepClone(item); original.ControllerSubject = fact.Controller;
                    if (MovementV4Digest(fact.ControllerAfter) != MovementV4Digest(DestinationController(state, original, fact.To.ZoneId)) ||
                        Text(leg, "operationProfileId") == "core.character_card_transfer" && (fact.Controller.Kind == ItemSubjectKind.Player && fact.Controller.PlayerId != node.PlayerId ||
                        from.TemplateId.StartsWith("core.zone.character_", StringComparison.Ordinal) && (fact.Controller.Kind != ItemSubjectKind.Player || fact.Controller.PlayerId != from.OwnerPlayerId))) return false;
                    var transfer = new ItemTransferRequest { ExecutingPlayerId = node.PlayerId, ItemIds = new List<string> { fact.ItemId },
                        ExpectedSource = new ItemPositionRef { ZoneId = fact.From.ZoneId, SlotId = mode == "explicit" ? Text(Value(selector, "expectedSource"), "slotId") : "" },
                        Destination = new ZoneRef { ZoneId = fact.To.ZoneId }, InsertionKind = Text(Value(leg, "insertion"), "kind"), DestinationSlotId = fact.To.SlotId, OperationProfileId = Text(leg, "operationProfileId"), CarrierPolicyId = "preserve", RequiredCount = 0 };
                    var previous = ownRequests.LastOrDefault();
                    if (previous != null && previous.ExpectedSource.ZoneId == transfer.ExpectedSource.ZoneId && previous.ExpectedSource.SlotId == transfer.ExpectedSource.SlotId &&
                        previous.Destination.ZoneId == transfer.Destination.ZoneId && previous.InsertionKind == transfer.InsertionKind && previous.DestinationSlotId == transfer.DestinationSlotId) previous.ItemIds.Add(fact.ItemId);
                    else ownRequests.Add(transfer);
                }
                expected.AddRange(ownRequests);
            }
            return plan.BoundOrders.Count == topOrders && MovementV4Digest(expected) == MovementV4Digest(plan.Transfers) && ItemMovementV4.MapTokenFactsValid(state, node, plan) && ItemMovementV4.StyleReturnFactsValid(state, node, plan) && FacilityPlacementMovement.ValidateHistory(state,node,plan);
        }
        private static bool ValidFact(GameState state, MarkerMoveItemFactV4 f) => f != null && f.Owner != null && f.Controller != null && f.ControllerAfter != null && f.From != null && f.To != null &&
            state.ItemZones.Items.Any(i => i.ItemId == f.ItemId && i.TypeId == f.TypeId && i.OriginalSupplyZoneId == f.OriginalSupplyZoneId && MovementV4Digest(i.OwnerSubject) == MovementV4Digest(f.Owner)) &&
            state.ItemZones.Zones.Any(z => z.ZoneId == f.From.ZoneId) && state.ItemZones.Zones.Any(z => z.ZoneId == f.To.ZoneId);
        internal static bool ValidateCommonV4(GameState state, EffectNodeRuntimeState node, out string reason)
        {
            reason = "item_move_v4_state_invalid";
            var plan = node.CommonItemMovementV4;
            if (node.ItemMovementV4 != null || ItemMovementV4.ValidateArguments(node.NormalizedArguments).Length > 0) return false;
            if (plan == null)
            {
                if (node.PositionChanges.Count != 0 || node.Status == EffectNodeStatus.Completed) return false;
                if (node.FlowStage == "") return true;
                return false;
            }
            if (!CommonPlanMatches(state, node, plan) || !new[] { "v4_bound", "v4_committed", "v4_finished", "awaiting_deferred_dependencies" }.Contains(node.FlowStage)) return false;
            var r = plan.Receipt;
            if (r == null) return node.Status != EffectNodeStatus.Completed && node.FlowStage != "v4_finished" && node.FlowStage != "v4_committed" && node.PositionChanges.Count == 0;
            reason = "item_move_v4_receipt_invalid";
            if (r.ContractVersion != ItemMovementV4.ContractVersion || r.OwnerEffectId != node.EffectId || r.ExecutingPlayerId != node.PlayerId || r.ArgumentsDigest != plan.ArgumentsDigest || r.Outcome != plan.Outcome ||
                r.OperationId != StableIdFactory.Create("item-move-v4", node.EffectId) || r.Result?.MovedItemIds == null || r.Result.PositionChanges == null || r.Items == null || r.LifecycleItems == null ||
                r.OrdersBefore == null || r.OrdersAfter == null || r.BindingsBefore == null || r.BindingsAfter == null || r.EventIds == null || r.CarrierExits == null ||
                MovementV4Digest(r.Items) != MovementV4Digest(plan.Items) || r.Items.Concat(r.LifecycleItems).Any(f => !ValidFact(state, f)) ||
                r.ReceiptId != StableIdFactory.Create("transfer-receipt-v4", node.EffectId, MarkerReceiptDigest(r))) return false;
            var allFacts = r.Items.Concat(r.LifecycleItems).ToList();
            foreach(var leg in ItemMovementV4.Legs(ItemMovementV4.CommonArguments(state,node)))
                if(Flag(Value(leg,"destination"),"mustBeEmpty"))
                {
                    var before=r.OrdersBefore.SingleOrDefault(o=>o.ZoneId==Text(Value(leg,"destination"),"zoneId"));
                    if(before!=null&&before.ItemIds.Count!=0||before==null&&r.Result.MovedCount>0)return false;
                }
            if (allFacts.Select(f => f.ItemId).Distinct().Count() != allFacts.Count || r.EventIds.Distinct().Count() != r.EventIds.Count) return false;
            foreach (var life in r.LifecycleItems)
            {
                var source = state.ItemZones.Zones.Single(z => z.ZoneId == life.From.ZoneId); var dest = state.ItemZones.Zones.Single(z => z.ZoneId == life.To.ZoneId);
                if (source.ParentKind != ZoneParentKind.Item || source.Configuration?.CarrierExitPolicyId != "core.return_contents_before_exit" ||
                    !plan.Items.Any(f => f.ItemId == source.ParentId && ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(f.TypeId, "core.character_card")) ||
                    !(life.To.ZoneId == life.OriginalSupplyZoneId && state.ItemZones.Items.Single(i => i.ItemId == life.ItemId).PhysicalKind == ItemPhysicalKind.Cube || life.TypeId == "core.facility_card" && dest.LocalZoneId == "facility_deck")) return false;
            }
            var expectedZones = CommonTouchedZones(state, node, plan, r.LifecycleItems);
            if (!expectedZones.SequenceEqual(r.OrdersBefore.Select(o => o.ZoneId)) || !expectedZones.SequenceEqual(r.OrdersAfter.Select(o => o.ZoneId)) ||
                r.OrdersBefore.Concat(r.OrdersAfter).Any(o => o.ItemIds == null || o.Revision < 0 || o.ItemIds.Distinct().Count() != o.ItemIds.Count || o.ItemIds.Any(id => !state.ItemZones.Items.Any(i => i.ItemId == id))) ||
                plan.BoundOrders.Any(o => MovementV4Digest(o) != MovementV4Digest(r.OrdersBefore.SingleOrDefault(x => x.ZoneId == o.ZoneId)))) return false;
            var checkedOrderZones=new HashSet<string>(StringComparer.Ordinal);
            foreach(var expected in Value(node.NormalizedArguments,"expectedOrders")?.Items??new List<NormalizedValue>())
            {
                string id=Text(expected,"zoneId");var before=r.OrdersBefore.SingleOrDefault(o=>o.ZoneId==id);
                if(!checkedOrderZones.Add(id)||before==null||Number(expected,"orderRevision")!=before.Revision||(Text(expected,"expectedTopItemId").Length>0&&Text(expected,"expectedTopItemId")!=(before.HasOrder?before.ItemIds.FirstOrDefault()??"":""))||Text(expected,"digest")!=FacilityPlacementMovement.OrderDigest(before))return false;
            }
            // 用已封存的真实前态顺序和逐枚事实演算，不从当前移动后的源区重新规划。
            var replay = GameStateCloneService.DeepClone(state);
            foreach (var fact in allFacts) { var item = replay.ItemZones.Items.Single(i => i.ItemId == fact.ItemId); item.ZoneId = fact.From.ZoneId; item.SlotId = fact.From.SlotId; item.ControllerSubject = GameStateCloneService.DeepClone(fact.Controller); }
            foreach (var before in r.OrdersBefore)
            {
                var order = replay.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == before.ZoneId);
                if (before.HasOrder != (order != null)) return false;
                if (order != null) { order.ItemIds = before.ItemIds.ToList(); order.OrderRevision = before.Revision; }
                else if (!before.ItemIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(replay.ItemZones.Items.Where(i => i.ZoneId == before.ZoneId).Select(i => i.ItemId).OrderBy(id => id, StringComparer.Ordinal)))
                {
                    // 其他实例之后可被合法移动；历史无序成员不与当前全区成员绑定，仅逐枚证据复验。
                    if (before.ItemIds.Any(id => allFacts.Any(f => f.ItemId == id && f.From.ZoneId != before.ZoneId))) return false;
                }
            }
            foreach (var f in r.LifecycleItems)
                Apply(replay, new ItemTransferRequest { ItemIds = new List<string> { f.ItemId }, ExpectedSource = f.From, Destination = new ZoneRef { ZoneId = f.To.ZoneId }, InsertionKind = state.ItemZones.Orders.Any(o => o.ZoneId == f.To.ZoneId) ? "bottom" : "unordered", DestinationSlotId = f.To.SlotId });
            var computed = new List<ItemPositionChange>();
            foreach (var transfer in plan.Transfers) if (!NoChange(replay, transfer)) computed.AddRange(Apply(replay, transfer).PositionChanges);
            if (MovementV4Digest(MergeResults(computed)) != MovementV4Digest(r.Result)) return false;
            foreach (var after in r.OrdersAfter.Where(o => o.HasOrder)) if (!SameOrder(replay, after)) return false;
            if (node.PositionChanges.Count != r.Result.PositionChanges.Count + r.LifecycleItems.Count || node.PositionChanges.Select(c => c.ItemId).Distinct().Count() != node.PositionChanges.Count ||
                node.PositionChanges.Any(c => !allFacts.Any(f => f.ItemId == c.ItemId && MovementV4Digest(f.From) == MovementV4Digest(c.From) && MovementV4Digest(f.To) == MovementV4Digest(c.To)))) return false;
            int offset = 0, index = 0;
            foreach (var leg in ItemMovementV4.Legs(ItemMovementV4.CommonArguments(state, node)))
            {
                var ids = plan.Items.Skip(offset).Take(plan.LegSizes[index]).Select(f => f.ItemId).ToList();
                if (plan.Outcome == "completed" && r.Result.MovedItemIds.Count(ids.Contains) < Number(leg, "requiredCount")) return false;
                offset += plan.LegSizes[index++];
            }
            if (r.EventIds.Any(id => !state.EffectRuntime.RuleEvents.Any(e => e.EventId == id && e.SourceEffectId == node.EffectId))) return false;
            if (!ValidateCommonExits(state, node, r)) return false;
            if (ItemMovementV4.MapTokenOperation(node.NormalizedArguments))
            {
                var expectedEvent = ItemMovementV4.MapTokenEvent(node, plan.Items.Single(), state);
                var actualEvent = state.EffectRuntime.RuleEvents.SingleOrDefault(e => e.EventId == expectedEvent.EventId);
                if (r.EventIds.Count != 1 || r.EventIds[0] != expectedEvent.EventId || actualEvent == null || actualEvent.EventType != expectedEvent.EventType ||
                    actualEvent.OwnerNodeId != node.EffectId || actualEvent.TargetEntityId != expectedEvent.TargetEntityId || actualEvent.PlayerId != node.PlayerId ||
                    actualEvent.Visibility != expectedEvent.Visibility || actualEvent.DefinitionVersion != expectedEvent.DefinitionVersion ||
                    MovementV4Digest(actualEvent.Payload) != MovementV4Digest(expectedEvent.Payload)) return false;
            }
            if ((node.Status == EffectNodeStatus.Completed || node.PendingOutcome == EffectPendingOutcome.Completed) && MovementV4Digest(node.NormalizedResult) != MovementV4Digest(ItemMovementV4.Result(r, node.NormalizedArguments))) return false;
            reason = ""; return true;
        }
        private static bool ValidateCommonExits(GameState state, EffectNodeRuntimeState node, TransferReceiptV4 r)
        {
            var automatic = node.ChildEffectIds.Where(id => !state.EffectRuntime.DispatchReceipts.Any(d => d.Succeeded && d.AttachedEffectIds.Contains(id))).ToList();
            if (automatic.Count != r.CarrierExits.Count || r.CarrierExits.Select(e => e.CardItemId).Distinct().Count() != r.CarrierExits.Count) return false;
            for (int index = 0; index < automatic.Count; index++)
            {
                var e = r.CarrierExits[index]; var source = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == e.SourceStorageZoneId); var child = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == automatic[index]);
                if (source?.ParentKind != ZoneParentKind.Item || source.ParentId != e.CardItemId || source.Configuration?.EmptyExitPolicyId != "core.discard_carrier_when_empty" ||
                    !r.Items.Any(f => f.From.ZoneId == source.ZoneId) || r.OrdersAfter.Single(o => o.ZoneId == source.ZoneId).ItemIds.Count != 0 ||
                    !state.ItemZones.Zones.Any(z => z.ZoneId == e.FromZoneId && z.TemplateId == "core.zone.character_persistent" && z.OwnerPlayerId == e.PlayerId) || FindZone(state, "discard", e.PlayerId).ZoneId != e.ToZoneId ||
                    child?.ParentEffectId != node.EffectId || child.PlayerId != e.PlayerId || child.EffectTypeId != LuaDomainEffectTypeIds.MoveCharacterCard ||
                    child.EffectId != StableIdFactory.Create("effect", node.EffectId, "child", "child_effect", node.ChildEffectIds.IndexOf(child.EffectId).ToString(CultureInfo.InvariantCulture), LuaDomainEffectTypeIds.MoveCharacterCard, node.EffectId + ":empty:" + e.CardItemId) ||
                    MovementV4Digest(child.NormalizedArguments) != MovementV4Digest(ItemMovementV4.ExitSpec(e).NormalizedArguments)) return false;
            }
            return true;
        }
    }
}
