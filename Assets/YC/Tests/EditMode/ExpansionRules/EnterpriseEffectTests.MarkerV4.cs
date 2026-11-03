using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class EnterpriseEffectTests
    {
        internal static NormalizedValue MarkerLegV4(string id, string source, string target, string slot = null, string sourceSlot = null, int required = 1)
        {
            var from = Object("zoneId", NormalizedValue.CreateString(source));
            if (sourceSlot != null) from.Properties.Add(new NormalizedValueEntry { Name = "slotId", Value = NormalizedValue.CreateString(sourceSlot) });
            from.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
            return Object("selector", Object("mode", NormalizedValue.CreateString("explicit"), "itemIds", Strings(new[] { id }), "expectedSource", from),
                "destination", target == null ? Object("kind", NormalizedValue.CreateString("original_supply")) : Object("zoneId", NormalizedValue.CreateString(target)),
                "insertion", slot == null ? Object("kind", NormalizedValue.CreateString("unordered")) : Object("kind", NormalizedValue.CreateString("slot"), "slotId", NormalizedValue.CreateString(slot)),
                "operationProfileId", NormalizedValue.CreateString("core.player_marker_storage"), "requiredCount", NormalizedValue.CreateInteger(required));
        }
        internal static EffectSpec MarkerSpecV4(int actor, params NormalizedValue[] legs)
        {
            var args = Object("apiVersion", NormalizedValue.CreateInteger(4), "player", NormalizedValue.CreateString(actor.ToString()),
                "operation", NormalizedValue.CreateString(legs.Length == 1 ? "transfer" : "transfer_batch"));
            if (legs.Length == 1) args.Properties.AddRange(legs[0].Clone().Properties);
            else { args.Properties.Add(new NormalizedValueEntry { Name = "transfers", Value = NormalizedValue.CreateArray(legs) }); args.Properties.Add(new NormalizedValueEntry { Name = "expectedOrders", Value = NormalizedValue.CreateArray(new NormalizedValue[0]) }); }
            args.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
            return EffectSpec.Create(TypeId, args, actor);
        }
        private static EffectRegistry MarkerRegistryV4() { var registry = Registry(); ItemTransferEffectExecutor.Register(registry); return registry; }
        [Test]
        public void V4OtherActorMovesEnterpriseMarkerAcrossSlotsAndOutWithoutAwardingArrivals()
        {
            var state = State(); var registry = MarkerRegistryV4(); string enterprise = ItemId(state, Business);
            var track = state.ItemZones.Zones.Single(z => z.ParentId == enterprise && z.OwnerPlayerId == 2);
            var marker = state.ItemZones.Items.Single(i => i.ZoneId == track.ZoneId);
            var moved = Run(state, MarkerSpecV4(1, MarkerLegV4(marker.ItemId, track.ZoneId, track.ZoneId, "level_4", "level_0")), registry);
            Assert.That(moved.PlayerId, Is.EqualTo(1)); Assert.That(Text(moved.NormalizedResult, "originalOwner"), Is.EqualTo("p2"));
            Assert.That(Level(state, Business, 2), Is.EqualTo(4)); Assert.That(marker.OwnerSubject.PlayerId, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == EnterpriseEffectExecutor.LevelChangedEvent), Is.False);
            Assert.That(state.Players.All(p => p.Score == 0 && p.Resources.GoldVoucher == 0), Is.True);
            state = Restore(state, registry); marker = state.ItemZones.Items.Single(i => i.ItemId == marker.ItemId);
            var returned = Run(state, MarkerSpecV4(1, MarkerLegV4(marker.ItemId, marker.ZoneId, null)), registry);
            Assert.That(Level(state, Business, 2), Is.EqualTo(-1)); Assert.That(Number(returned.NormalizedResult, "movedCount"), Is.EqualTo(1));
            state = Restore(state, registry); Assert.That(Level(state, Business, 2), Is.EqualTo(-1));
        }
        [TestCase(true)][TestCase(false)]
        public void V4AtomicReplacementUsesFinalLogicalTrackStateInEitherLegOrder(bool incomingFirst)
        {
            var state = State(); var registry = MarkerRegistryV4();
            var track = state.ItemZones.Zones.Single(z => z.ParentId == ItemId(state, Business) && z.OwnerPlayerId == 2);
            var old = state.ItemZones.Items.Single(i => i.ZoneId == track.ZoneId);
            var next = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 2 && i.ZoneId == i.OriginalSupplyZoneId);
            var incoming = MarkerLegV4(next.ItemId, next.ZoneId, track.ZoneId, "level_3"); var outgoing = MarkerLegV4(old.ItemId, old.ZoneId, null);
            var node = Run(state, MarkerSpecV4(1, incomingFirst ? new[] { incoming, outgoing } : new[] { outgoing, incoming }), registry);
            Assert.That(Number(node.NormalizedResult, "movedCount"), Is.EqualTo(2)); Assert.That(Level(state, Business, 2), Is.EqualTo(3));
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == track.ZoneId), Is.EqualTo(1));
            Assert.That(node.ItemMovementV4.Receipt.Items.Count, Is.EqualTo(2));
            Assert.That(node.ItemMovementV4.Receipt.OrdersBefore, Is.Not.Empty); Assert.That(node.ItemMovementV4.Receipt.OrdersAfter, Is.Not.Empty);
            Restore(state, registry);
        }
        [TestCase("duplicate")][TestCase("wrong_owner")][TestCase("wrong_slot")][TestCase("source")]
        public void V4InvalidMarkerBatchLeavesEveryRealItemInItsOriginalPosition(string failure)
        {
            var state = State(); var registry = MarkerRegistryV4();
            var track = state.ItemZones.Zones.Single(z => z.ParentId == ItemId(state, Business) && z.OwnerPlayerId == 2);
            var current = state.ItemZones.Items.Single(i => i.ZoneId == track.ZoneId);
            var next = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 2 && i.ZoneId == i.OriginalSupplyZoneId);
            var target = failure == "wrong_owner" ? state.ItemZones.Zones.Single(z => z.ParentId == ItemId(state, Research) && z.OwnerPlayerId == 1) : track;
            var bad = MarkerLegV4(next.ItemId, failure == "source" ? FindZone(state, "supply", 1).ZoneId : next.ZoneId, target.ZoneId, failure == "wrong_slot" ? "level_99" : "level_4");
            var before = state.ItemZones.Items.ToDictionary(i => i.ItemId, i => i.ZoneId + ":" + i.SlotId);
            var engine = new EffectTreeExecutor(state, registry); string root = engine.CreateRoot(MarkerSpecV4(1,
                MarkerLegV4(current.ItemId, current.ZoneId, current.ZoneId, "level_2"), bad));
            engine.RunUntilQuiescent();
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(state.ItemZones.Items.All(i => before[i.ItemId] == i.ZoneId + ":" + i.SlotId), Is.True);
            Assert.That(engine.GetNode(root).PositionChanges, Is.Empty);
        }
        [TestCase(0)][TestCase(1)]
        public void V4SameTrackSameSlotCountsOnlyActualMovement(int required)
        {
            var state = State(); var registry = MarkerRegistryV4();
            var track = state.ItemZones.Zones.Single(z => z.ParentId == ItemId(state, Business) && z.OwnerPlayerId == 2);
            var marker = state.ItemZones.Items.Single(i => i.ZoneId == track.ZoneId);
            var node = Run(state, MarkerSpecV4(1, MarkerLegV4(marker.ItemId, track.ZoneId, track.ZoneId, "level_0", required: required)), registry);
            Assert.That(node.Status, Is.EqualTo(required == 0 ? EffectNodeStatus.Completed : EffectNodeStatus.Failed));
            if (required == 0) Assert.That(Text(node.NormalizedResult, "outcome"), Is.EqualTo("completed"));
            else Assert.That(node.FailureReason, Is.EqualTo("insufficient_items"));
            Assert.That(node.PositionChanges, Is.Empty); Assert.That(Level(state, Business, 2), Is.Zero);
            Restore(state, registry);
        }
        [TestCase("owner")][TestCase("source")][TestCase("count")][TestCase("receipt")][TestCase("order")][TestCase("args")][TestCase("raw_missing")]
        public void V4MarkerReceiptCannotRestoreWithChangedFactsOrMissingRawFields(string corruption)
        {
            var state = State(); var registry = MarkerRegistryV4();
            var track = state.ItemZones.Zones.Single(z => z.ParentId == ItemId(state, Business) && z.OwnerPlayerId == 2);
            var marker = state.ItemZones.Items.Single(i => i.ZoneId == track.ZoneId);
            var moved = Run(state, MarkerSpecV4(1, MarkerLegV4(marker.ItemId, track.ZoneId, track.ZoneId, "level_3", "level_0")), registry);
            var archive = HostRecoveryService.CreateArchive(state); var node = archive.Snapshot.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == moved.EffectId);
            if (corruption == "owner") node.ItemMovementV4.Receipt.Items[0].Owner.PlayerId = 1;
            if (corruption == "source") node.ItemMovementV4.Items[0].From.SlotId = "level_2";
            if (corruption == "count") node.ItemMovementV4.Receipt.Result.MovedCount++;
            if (corruption == "receipt") node.ItemMovementV4.Receipt = null;
            if (corruption == "order") node.ItemMovementV4.Receipt.OrdersBefore[0].Revision++;
            if (corruption == "args") Value(node.NormalizedArguments, "player").StringValue = "2";
            var json = HostRecoveryService.SerializeArchive(archive);
            if (corruption == "raw_missing")
            {
                var raw = JObject.Parse(json); var property = raw.Descendants().OfType<JProperty>().First(p => p.Name == "ItemMovementV4" && p.Value is JObject o && (string)o["OwnerEffectId"] == node.EffectId);
                ((JObject)property.Value["Receipt"]).Remove("EventIds"); json = raw.ToString();
            }
            Assert.That(HostRecoveryService.RecoverJson(json, registry).Succeeded, Is.False);
        }
    }
}
