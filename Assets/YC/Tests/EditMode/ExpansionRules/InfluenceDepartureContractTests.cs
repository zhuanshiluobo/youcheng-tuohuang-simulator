using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>006机制隔离，人工输入不计正常菜单／正式卡正文验收。</summary>
    public sealed class InfluenceDepartureContractTests
    {
        private IsolatedTestArtifacts artifacts;
        private GameState state;
        private EffectRegistry registry;
        private GameMapDefinition map;
        [SetUp] public void Begin()
        {
            artifacts = IsolatedTestArtifacts.BeginCase();
            map = StaticMapDefinitions.CreateThreePlayerMap();
            registry = new EffectRegistry { ContentPackHash = "qy006.isolated" };
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); ItemTransferEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, new InfluenceService(new MapQueryService(map)));
            ContentPrimitiveEffectExecutor.Register(registry, new MapQueryService(map), registry.InfluenceService);
            var source = new GameState { GameId = "qy006.isolated", MapId = map.MapId, ContentPackHash = "qy006.isolated", Round = 1, ActionRound = 1,
                Phase = GamePhase.ActionRound1, CurrentPlayerId = 1, StartPlayerId = 1, LaunchPlayerOrder = new List<int> { 1, 2 },
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, ContentPackHash = "qy006.isolated", PlayerCount = 2 },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "隔离甲", Resources = new ResourceSet { GoldVoucher = 20 } }, new PlayerState { PlayerId = 2, Name = "隔离乙" } } };
            var recipe = new List<ItemRecipeEntry> { new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", PerPlayer = true, Quantity = 4, InitialZoneRole = "supply" } };
            foreach (var location in map.Locations.Where(l => !l.IsRedZone)) recipe.Add(new ItemRecipeEntry { EntryId = "resource_" + location.LocationId, DefinitionId = "core.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/" + location.LocationId });
            state = new ItemZoneStateFactory().CreateForSetup(source, map, recipe);
            foreach (var location in map.Locations.Where(l => !l.IsRedZone)) state.Map.OpenLocationIds.Add(location.LocationId);
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
        }
        [TearDown] public void End()
        { if (state != null) artifacts.WriteLabeledText("最终真实状态", JsonConvert.SerializeObject(state)); artifacts.Finish(); }
        private EffectTreeExecutor Engine() => new EffectTreeExecutor(state, registry);
        private EffectNodeRuntimeState Run(EffectSpec spec)
        { string id = Engine().CreateRoot(spec); Quiesce(); return state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id); }
        private void Quiesce()
        { var r = Engine().RunUntilQuiescent(); if (r.Faulted) artifacts.WriteLabeledText("首次故障状态", JsonConvert.SerializeObject(state)); Assert.That(r.Faulted, Is.False, state.EffectRuntime.LastFaultMessage); }
        private string Slot(int index) => InfluenceService.GetLocationSlotId(map.Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount >= 2).LocationId, index);
        private string Place(int index = 0) => Value(Run(InfluenceEffectSpecFactory.PlaceInfluence(1, Slot(index))).NormalizedResult, "influenceId").ReferenceId;
        private void Restore(string label)
        { artifacts.WriteLabeledText(label + "原始状态", JsonConvert.SerializeObject(state)); var r = artifacts.Recover(state, label, registry); Assert.That(r.Succeeded, Is.True, r.Diagnostic); state = r.State; }
        [Test] public void ReplaceEmitsRemovedBeforePlacedAndReplacedWithOneDeparture()
        {
            string item = Place(); var n = Run(InfluenceEffectSpecFactory.ReplaceInfluence(1, item));
            Assert.That(n.Status, Is.EqualTo(EffectNodeStatus.Completed));
            var d = state.EffectRuntime.InfluenceDepartures.Single();
            var events = state.EffectRuntime.RuleEvents.Where(e => e.SourceEffectId == n.EffectId || e.EventType == "InfluencePlaced" && e.SourceEffectId != state.EffectRuntime.EffectNodes.First().EffectId).Where(e => e.EventType.StartsWith("Influence")).ToList();
            Assert.That(events.Select(e => e.EventType), Is.EqualTo(new[] { "InfluenceRemoved", "InfluencePlaced", "InfluenceReplaced" }));
            Assert.That(Text(events[0].Payload, "departureId"), Is.EqualTo(Text(events[2].Payload, "departureId")));
            Assert.That(d.Status, Is.EqualTo("returned")); Restore("替换完成");
        }
        [Test] public void SameItemLaterDepartureGetsAnotherIdentity()
        {
            string item = Place(); Run(InfluenceEffectSpecFactory.RemoveInfluence(1, item));
            string again = Place(); Assert.That(again, Is.EqualTo(item)); Run(InfluenceEffectSpecFactory.RemoveInfluence(1, again));
            Assert.That(state.EffectRuntime.InfluenceDepartures.Select(d => d.DepartureId).Distinct().Count(), Is.EqualTo(2)); Restore("同物再次离场");
        }
        [Test] public void StrictTwoRemovalsAreVisibleTogetherBeforeFirstResponse()
        {
            string one = Place(0), two = Place(1);
            var n = Run(InfluenceEffectExecutor.StrictRemove(1, new[] { one, two }, 2));
            Assert.That(n.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Number(n.NormalizedResult, "actualRemovedCount"), Is.EqualTo(2));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.SourceEffectId == n.EffectId && e.EventType == "InfluenceRemoved"), Is.EqualTo(2));
            Assert.That(state.EffectRuntime.InfluenceDepartureBatches.Single().DepartureIds.Count, Is.EqualTo(2)); Restore("严格两枚完成");
        }
        [Test] public void StrictInvalidSecondTargetChangesNothingAndEmitsNoEvent()
        {
            string one = Place(); var n = Run(InfluenceEffectExecutor.StrictRemove(1, new[] { one, "missing" }, 2));
            Assert.That(n.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(n.PositionChanges, Is.Empty);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == one).SlotId, Is.EqualTo(Slot(0))); Assert.That(state.EffectRuntime.InfluenceDepartures, Is.Empty);
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.SourceEffectId == n.EffectId && e.EventType == "InfluenceRemoved"), Is.Zero); Restore("严格失败零变更");
        }
        [Test] public void ExplicitBatchSealsAfterRealDynamicProducers()
        {
            string one = Place(0), two = Place(1);
            var n = Run(InfluenceDepartureBatchExecutor.Create(1, new[] { InfluenceEffectSpecFactory.RemoveInfluence(1, one), InfluenceEffectSpecFactory.RemoveInfluence(1, two) }));
            Assert.That(n.Status, Is.EqualTo(EffectNodeStatus.Completed)); var batch = state.EffectRuntime.InfluenceDepartureBatches.Single();
            Assert.That(batch.Sealed, Is.True); Assert.That(batch.DepartureIds.Count, Is.EqualTo(2)); Assert.That(batch.ProducerSteps.Count, Is.EqualTo(2)); Restore("显式生产封口");
        }
        [Test] public void FirstRemovedObserverAlreadySeesBothStrictTargetsOffMap()
        {
            string one=Place(0),two=Place(1); int observations=0;
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("qy006.atomic.observer","InfluenceRemoved","","qy006.atomic.observer",context=>
            { Assert.That(context.State.Map.Influences.Any(i=>i.InfluenceId==one || i.InfluenceId==two),Is.False); observations++; return new List<EffectSpec>(); },EffectHandlerRole.Observer));
            Run(InfluenceEffectExecutor.StrictRemove(1,new[]{one,two},2)); Assert.That(observations,Is.EqualTo(2)); Restore("严格提交后才观察");
        }
        [Test] public void LegacyV1WaitingTargetRestoresAndKeepsItsEventProtocol()
        {
            string item=Place(); var spec=InfluenceEffectSpecFactory.RemoveInfluence(1,item);
            spec.NormalizedArguments.Properties.RemoveAll(p=>p.Name=="targetInfluence"); spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry{Name="candidateScope",Value=Strings(new[]{item})}); spec.NormalizedArguments.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));
            string id=Engine().CreateRoot(spec);
            // 隔离构造旧档帧，正常新建入口继续拒绝旧版本 spec。
            state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id).DefinitionVersion="v1";
            Quiesce(); Restore("旧v1选目标待答"); var request=state.EffectRuntime.InteractionRequests.Single(q=>q.Status=="open");
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,NormalizedValue.CreateString(item),out string reason),Is.True,reason); Quiesce();
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id).DefinitionVersion,Is.EqualTo("v1")); Assert.That(state.EffectRuntime.InfluenceDepartures,Is.Empty); Restore("旧v1未补新离场事件");
        }
        [Test] public void EmptyExplicitProductionBatchSealsWithoutAResponse()
        {
            var n=Run(InfluenceDepartureBatchExecutor.Create(1,new[]{InfluenceEffectExecutor.StrictRemove(1,new[]{"missing1","missing2"},2)}));
            var batch=state.EffectRuntime.InfluenceDepartureBatches.Single(); Assert.That(batch.Sealed,Is.True); Assert.That(batch.DepartureIds,Is.Empty); Assert.That(state.EffectRuntime.DeferredResponses,Is.Empty); Restore("零生产封口");
        }
    }
}
