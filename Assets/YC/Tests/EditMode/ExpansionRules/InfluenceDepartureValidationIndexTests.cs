using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class InfluenceDepartureValidationIndexTests
    {
        private delegate bool Validation(GameState state, out string reason);
        private static string Result(Validation validation, GameState state)
        {
            try { return validation(state, out string reason) + "|" + reason; }
            catch (Exception error) { return error.GetType().FullName + "|" + error.Message; }
        }
        private static GameState State()
        {
            var state = new GameState(); var runtime = state.EffectRuntime;
            var departure = new InfluenceDepartureRecord { SourceEffectId = "source", ItemId = "cube", CauseKind = "remove", OriginalSupplyZoneId = "supply",
                OriginalSubject = new ItemSubjectRef(), OriginalPosition = new ItemPositionRef { ZoneId = "original", SlotId = "slot" },
                Status = "returned", CustodyZoneId = "custody", DestinationZoneId = "supply", EventIds = new List<string> { "event" } };
            departure.DepartureId = StableIdFactory.Create("departure", departure.SourceEffectId, "0");
            runtime.InfluenceDepartures.Add(departure);
            runtime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "source", EffectTypeId = InfluenceEffectTypeIds.RemoveInfluence,
                PositionChanges = new List<ItemPositionChange> { new ItemPositionChange { ItemId = "cube", From = new ItemPositionRef { ZoneId = "original", SlotId = "slot" } } } });
            runtime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "destination", PositionChanges = new List<ItemPositionChange> {
                new ItemPositionChange { ItemId = "cube", From = new ItemPositionRef { ZoneId = "custody" }, To = new ItemPositionRef { ZoneId = "supply" } } } });
            state.ItemZones.Items.Add(new ItemInstanceState { ItemId = "cube", OriginalSupplyZoneId = "supply", ZoneId = "supply" });
            state.ItemZones.Zones.Add(new ZoneInstanceState { ZoneId = "supply" });
            runtime.RuleEvents.Add(new RuleEvent { EventId = "event", SourceEffectId = "source", EventType = InfluenceEventTypeIds.InfluenceRemoved,
                Payload = Object("departureId", NormalizedValue.CreateString(departure.DepartureId), "eventProtocolVersion", NormalizedValue.CreateInteger(2)) });
            var batch = new InfluenceDepartureBatch { OwnerEffectId = "source", Sealed = true, ProducerSteps = new List<string> { "step" }, FinishedSteps = new List<string> { "step" } };
            batch.BatchId = StableIdFactory.Create("departure_batch", batch.OwnerEffectId); departure.BatchId = batch.BatchId; batch.DepartureIds.Add(departure.DepartureId);
            batch.Offers.Add(new DepartureBatchOffer { EventId = "event", SubscriptionId = "subscription", Body = new EffectSpecRuntimeState(),
                EventIds = new List<string> { "event" }, DepartureIds = new List<string> { departure.DepartureId } });
            runtime.DispatchReceipts.Add(new DispatchReceipt { EventId = "event", SubscriptionId = "subscription" }); runtime.InfluenceDepartureBatches.Add(batch);
            return state;
        }
        [Test]
        public void IndexKeepsOriginalShortCircuitDuplicateAndMalformedHistoryOutcomes()
        {
            Assert.That(InfluenceDepartureProtocol.Validate(State(), out var reason), Is.True, reason);
            var mutations = new Dictionary<string, Action<GameState>> {
                { "正常", s => { } },
                { "缺离场表", s => s.EffectRuntime.InfluenceDepartures = null },
                { "缺批次表", s => s.EffectRuntime.InfluenceDepartureBatches = null },
                { "空记录", s => s.EffectRuntime.InfluenceDepartures[0] = null },
                { "错误版本", s => s.EffectRuntime.InfluenceDepartures[0].ProtocolVersion = 7 },
                { "重复记录", s => s.EffectRuntime.InfluenceDepartures.Add(GameStateCloneService.DeepClone(s.EffectRuntime.InfluenceDepartures[0])) },
                { "重复来源先匹配", s => s.EffectRuntime.EffectNodes.Add(GameStateCloneService.DeepClone(s.EffectRuntime.EffectNodes[0])) },
                { "空节点在末尾", s => s.EffectRuntime.EffectNodes.Add(null) },
                { "空节点在开头", s => s.EffectRuntime.EffectNodes.Insert(0, null) },
                { "空物品在末尾", s => s.ItemZones.Items.Add(null) },
                { "空物品在开头", s => s.ItemZones.Items.Insert(0, null) },
                { "缺物品表", s => s.ItemZones.Items = null },
                { "缺实体区", s => s.ItemZones = null },
                { "缺区域表", s => s.ItemZones.Zones = null },
                { "缺事件表", s => s.EffectRuntime.RuleEvents = null },
                { "空事件在末尾", s => s.EffectRuntime.RuleEvents.Add(null) },
                { "重复事件", s => s.EffectRuntime.RuleEvents.Insert(0, new RuleEvent { EventId = "event", SourceEffectId = "other" }) },
                { "响应缺失", s => s.EffectRuntime.InfluenceDepartures[0].ResponseIds.Add("missing") },
                { "供应区不存在", s => s.ItemZones.Zones.Clear() },
                { "去向不符", s => s.EffectRuntime.InfluenceDepartures[0].DestinationZoneId = "other" },
                { "历史位置含空值", s => s.EffectRuntime.EffectNodes[1].PositionChanges.Add(null) },
                { "历史位置缺失", s => s.EffectRuntime.EffectNodes[1].PositionChanges = null },
                { "批次生产步骤重复", s => s.EffectRuntime.InfluenceDepartureBatches[0].ProducerSteps.Add("step") },
                { "批次离场顺序错误", s => s.EffectRuntime.InfluenceDepartureBatches[0].DepartureIds.Clear() },
                { "offer重复成员", s => s.EffectRuntime.InfluenceDepartureBatches[0].Offers[0].DepartureIds.Add("missing") },
                { "offer事件缺失", s => s.EffectRuntime.InfluenceDepartureBatches[0].Offers[0].EventIds[0] = "missing" },
                { "收据缺失", s => s.EffectRuntime.DispatchReceipts.Clear() }
            };
            foreach (var mutation in mutations)
            {
                var state = State(); mutation.Value(state);
                Assert.That(Result(InfluenceDepartureProtocol.Validate, state), Is.EqualTo(Result(LegacyInfluenceDepartureValidationReference.Validate, state)), mutation.Key);
            }
        }
        [Test]
        public void SameRevisionNextValidationRebuildsIndexesAndDoesNotMutateInput()
        {
            var state = State(); var baseline = GameStateCloneService.DeepClone(state);
            Assert.That(InfluenceDepartureProtocol.Validate(state, out _), Is.True);
            Assert.That(GameStateCloneService.AreEquivalent(state, baseline), Is.True);
            state.ItemZones.Items[0].OriginalSupplyZoneId = "changed";
            Assert.That(InfluenceDepartureProtocol.Validate(state, out _), Is.False);
        }
    }
}
