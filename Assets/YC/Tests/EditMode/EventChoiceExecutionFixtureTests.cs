using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    /// <summary>事件新合同的隔离执行夹具，不计正常对局或画面验收。</summary>
    public sealed class EventChoiceExecutionFixtureTests
    {
        private const string CardId = "fixture.event";

        private static EventCardDefinition Card(int count, bool reverse = false)
        {
            var card = new EventCardDefinition { CardId = CardId, Name = "夹具事件", Description = "夹具描述",
                Color = EventColor.Green, ResourceType = ResourceType.Originium, RepresentativeResourceType = ResourceType.Originium,
                EventChoiceSchemaVersion = 1 };
            for (int i = 0; i < count; i++) card.Choices.Add(new EventCardChoice {
                ChoiceId = "choice_" + i, Description = "选项" + i, Script = "fixture/" + i + ".lua", Version = "1.0.0",
                Button = new EventCardChoiceButton { TopPercent = i * 100.0 / count, BottomPercent = (i + 1) * 100.0 / count } });
            if (reverse) card.Choices.Reverse();
            if (count == 0) card.ImmediateResolve = new EventCardImmediateResolve { Script = "fixture/immediate.lua", Version = "1.0.0" };
            return card;
        }

        private static GameState State(EventCardDefinition card)
        {
            var state = new GameState { GameId = "event-choice-fixture", MapId = StaticMapDefinitions.FourPlayerMapId,
                Round = 1, ActionRound = 1, Phase = GamePhase.ActionRound1, StartPlayerId = 1, CurrentPlayerId = 1,
                ContentDefinitions = new GameContentDefinitions { Events = new List<EventCardDefinition> { card } },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, InfluenceSupply = 30, Resources = new ResourceSet() } } };
            state.Decks.EventDeckGreen.Add(card.CardId);
            return state;
        }

        private static EffectRegistry Registry(EventCardDefinition card, string selected)
        {
            var registry = new EffectRegistry();
            var map = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var influence = new InfluenceService(map);
            ResourceEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, influence);
            EventCardEffectExecutor.Register(registry, map, influence, new EventDeckService(321), new ResourceTokenService());
            var dispatcher = new LuaEffectEventDispatcher(registry);
            var ids = card.Choices.Count == 0 ? new[] { "" } : card.Choices.Select(choice => choice.ChoiceId).ToArray();
            foreach (string id in ids)
            {
                string route = id.Length == 0 ? StableIdFactory.Create("event-choice-immediate", card.CardId)
                    : EventCardOptionIdFactory.CreateChoice(card.CardId, id);
                string handler = StableIdFactory.Create("fixture-event-handler", card.CardId, id);
                string source = id == selected
                    ? "return function(ctx) if ctx.payload.choiceId ~= '" + id + "' then error('错误选择身份') end " +
                      "return { Effect.GainResource({recipient=ctx.playerId, resourceType='Originium', amount=3}) } end"
                    : "return function(ctx) error('未选择的脚本不应执行') end";
                var definition = new LuaHandlerDefinition(handler, EventCardEffectEventTypeIds.Resolved, route,
                    new LuaScriptDefinition(card.CardId, handler, handler, "1.0.0", source, LuaContentHasher.ComputeSha256(source)),
                    EffectHandlerRole.Primary);
                typeof(EventCardLuaCatalog).GetMethod("RegisterChoiceHandler", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { registry, dispatcher, definition, card.CardId });
            }
            return registry;
        }

        [TestCase("example.events.support", "first", 0, 3, 3, 0)]
        [TestCase("example.events.support", "second", 3, 1, 0, 0)]
        [TestCase("example.events.dream", "first", 0, 4, 0, 0)]
        [TestCase("example.events.dream", "second", 0, 0, 4, 1)]
        public void TrialEventChoicesUseTheirOwnFrozenScripts(string cardId, string choiceId, int rock, int iron, int gold, int score)
        {
            string project = Directory.GetParent(UnityEngine.Application.dataPath).FullName;
            var source = DiyPackSnapshot.Load(Path.Combine(project, "tools", "packaging", "examples", "事件试验"));
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var configuration = new GameRuleConfiguration { RuleModeId = "standard", MapId = "map-four-players", PlayerCount = 4,
                EnabledExpansionIds = new List<string> { "core", "example.events.green" } };
            var selected = DiyContentResolution.Resolve(configuration.RuleModeId, configuration.EnabledExpansionIds, configuration.MapId,
                new[] { source }, (string)core.Manifest["version"], core.ContentHash, RuleModeReadiness.ImplementedCapabilities, GameplayOptionsCatalog.CoreOnly(core));
            configuration.LockedContentPacks = selected.CreateRuleLocks();
            var catalog = ActiveContentCatalogBuilder.Build(core, configuration, selected);
            var card = catalog.ContentDefinitions.Events.Single(value => value.CardId == cardId);
            var state = State(card); state.ContentPackHash = catalog.ContentPackHash;
            var registry = new EffectRegistry { ContentPackHash = catalog.ContentPackHash };
            var map = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            ResourceEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, new InfluenceService(map));
            EventCardEffectExecutor.Register(registry, map, new InfluenceService(map), new EventDeckService(321), new ResourceTokenService());
            EventCardLuaCatalog.Register(registry, catalog.FrozenPack);
            var engine = new EffectTreeExecutor(state, registry);
            string root = engine.CreateRoot(EventCardEffectSpecFactory.Resolve(1, EventColor.Green, "A-01"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            string candidate = EventCardOptionIdFactory.CreateChoice(cardId, choiceId);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(candidate), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            var player = state.FindPlayer(1);
            Assert.That(player.Resources.Originium, Is.EqualTo(rock));
            Assert.That(player.Resources.Iron, Is.EqualTo(iron));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(gold));
            Assert.That(player.Score, Is.EqualTo(score));
        }

        [TestCase(1, 0)] [TestCase(2, 1)] [TestCase(3, 1)]
        [TestCase(4, 0)] [TestCase(4, 1)] [TestCase(4, 2)] [TestCase(4, 3)]
        [TestCase(6, 0)] [TestCase(6, 1)] [TestCase(6, 2)] [TestCase(6, 3)] [TestCase(6, 4)] [TestCase(6, 5)]
        public void SelectedStableIdRunsOnlyItsLuaAfterSnapshotClone(int count, int selectedIndex)
        {
            var card = Card(count);
            string choiceId = "choice_" + selectedIndex;
            var state = State(card);
            var registry = Registry(card, choiceId);
            var engine = new EffectTreeExecutor(state, registry);
            string primary = InfluenceService.GetLocationSlotId("A-01", 0);
            string root = engine.CreateRoot(EventCardEffectSpecFactory.Resolve(1, EventColor.Green, "A-01", primary));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(request.CandidateIds, Has.Count.EqualTo(count));
            Assert.That(request.CandidateSetVersion, Is.EqualTo(2));
            string candidate = EventCardOptionIdFactory.CreateChoice(CardId, choiceId);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(EventCardOptionIdFactory.CreateChoice("other.event", choiceId)), out _), Is.False);
            state = GameStateCloneService.DeepClone(state);
            engine = new EffectTreeExecutor(state, registry);
            request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(candidate), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
            Assert.That(state.Map.Influences.Count, Is.EqualTo(1));
            Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(primary));
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(candidate), out _), Is.False);
        }

        [Test]
        public void ReorderingDoesNotChangeCandidateIdentityOrBoundLua()
        {
            var first = Card(2);
            var second = Card(2, true);
            Assert.That(EventCardOptionIdFactory.CreateChoice(first.CardId, first.Choices[0].ChoiceId),
                Is.EqualTo(EventCardOptionIdFactory.CreateChoice(second.CardId, second.Choices[1].ChoiceId)));
            var state = State(second); var registry = Registry(second, "choice_0"); var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(EventCardEffectSpecFactory.Resolve(1, EventColor.Green, "A-01"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(EventCardOptionIdFactory.CreateChoice(CardId, "choice_0")), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
        }

        [Test]
        public void OfficialQuarryFirstChoiceGrantsThreeOriginiumShards()
        {
            var pack = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var card = pack.CreateEvents().Single(value => value.CardId == "event_green_01");
            var state = State(card);
            var registry = new EffectRegistry();
            var map = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var influence = new InfluenceService(map);
            ResourceEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, influence);
            EventCardEffectExecutor.Register(registry, map, influence, new EventDeckService(321), new ResourceTokenService());
            EventCardLuaCatalog.Register(registry, pack);
            var engine = new EffectTreeExecutor(state, registry);
            string root = engine.CreateRoot(EventCardEffectSpecFactory.Resolve(1, EventColor.Green, "A-01"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(EventCardOptionIdFactory.CreateChoice(card.CardId, "choice_01")), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
        }

        [Test]
        public void PendingRestoreRejectsOldExecutorAndForeignOrChangedCandidates()
        {
            var card = Card(2); var state = State(card); var registry = Registry(card, "choice_0");
            var engine = new EffectTreeExecutor(state, registry);
            string root = engine.CreateRoot(EventCardEffectSpecFactory.Resolve(1, EventColor.Green, "A-01"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var node = state.EffectRuntime.EffectNodes.Single(value => value.EffectId == root);
            Assert.That(EventCardEffectExecutor.TryValidateSaved(state, node, out string diagnostic), Is.True, diagnostic);
            node.DefinitionVersion = "1.0.0";
            Assert.That(EventCardEffectExecutor.TryValidateSaved(state, node, out diagnostic), Is.False);
            Assert.That(diagnostic, Does.Contain("原版本"));
            node.DefinitionVersion = EventCardEffectExecutor.DefinitionVersion;
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            request.CandidateIds[0] = EventCardOptionIdFactory.CreateChoice("other.event", "choice_0");
            Assert.That(EventCardEffectExecutor.TryValidateSaved(state, node, out diagnostic), Is.False);
            Assert.That(diagnostic, Does.Contain("candidates_mismatch"));
            node.Status = EffectNodeStatus.Completed; node.DefinitionVersion = "1.0.0";
            Assert.That(EventCardEffectExecutor.TryValidateSaved(state, node, out diagnostic), Is.True,
                "已完成历史节点不应重新解释或重复执行。");
        }

        [Test]
        public void ZeroChoicesInvokeOnlyExplicitImmediateHandler()
        {
            var card = Card(0); var state = State(card); var registry = Registry(card, "");
            var engine = new EffectTreeExecutor(state, registry);
            string root = engine.CreateRoot(EventCardEffectSpecFactory.Resolve(1, EventColor.Green, "A-01"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(value => value.Status == "open"), Is.False);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
        }

        [TestCase(false)] [TestCase(true)]
        public void InfluencePreselectionPrecedesPrimaryAndRewardAndSurvivesClone(bool noLegalTarget)
        {
            var pack = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var definitions = (List<ExternalContentDefinition>)typeof(ExternalContentPack).GetField("activeDefinitions", flags).GetValue(pack);
            var scripts = (Dictionary<string, string>)typeof(ExternalContentPack).GetField("scripts", flags).GetValue(pack);
            string primary = InfluenceService.GetLocationSlotId("A-01", 0), target = InfluenceService.GetLocationSlotId("A-01", 1);
            const string body = "lua/fixture/preselect.lua", completion = "lua/fixture/preselect.completed.lua", handler = "fixture.preselect.complete";
            string candidates = "'" + primary + "'" + (noLegalTarget ? "" : ",'" + target + "'");
            scripts.Add(body, "return function(ctx) return { Effect.Choice({ player=ctx.playerId, options={" + candidates + "}, " +
                "interactionType='effect.influence.place.target', resultContext={source='fixture'}, completionHandlerId='" + handler + "' }) } end");
            scripts.Add(completion, "return function(ctx) if ctx.payload.outcome ~= 'completed' then return {} end " +
                "return { Effect.GainResource({recipient=ctx.playerId, resourceType='Originium', amount=6}), " +
                "Effect.PlaceInfluence({executingPlayer=ctx.playerId, ownerSubject=ctx.playerId, influenceSource='fixture.pending', " +
                "targetSlotId=ctx.payload.normalizedResult.value, causeKind='other_intrinsic_flow'}) } end");
            definitions.Add(new ExternalContentDefinition { DefinitionId = CardId, RuntimeId = CardId, ContentType = "event", Version = "1.0.0",
                DisplayName = "影响力预选夹具", AbilityHandlers = new List<ExternalAbilityHandler> {
                    new ExternalAbilityHandler { HandlerId = handler, Script = completion, Version = "1.0.0" } },
                Data = new JObject { ["description"] = "夹具", ["color"] = 0, ["resourceType"] = 0, ["representativeResourceType"] = 0,
                    ["eventChoiceSchemaVersion"] = 1, ["choices"] = new JArray(new JObject {
                        ["choiceId"] = "go", ["description"] = "选择位置", ["button"] = new JObject { ["topPercent"] = 0, ["bottomPercent"] = 100 },
                        ["script"] = body, ["version"] = "1.0.0", ["completionHandlerId"] = handler }) } });
            var card = pack.CreateEvents().Single(value => value.CardId == CardId);
            var state = State(card); var registry = new EffectRegistry();
            var map = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()); var influence = new InfluenceService(map);
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); InfluenceEffectExecutor.Register(registry, influence);
            EventCardEffectExecutor.Register(registry, map, influence, new EventDeckService(321), new ResourceTokenService());
            EventCardLuaCatalog.Register(registry, pack);
            var engine = new EffectTreeExecutor(state, registry);
            string root = engine.CreateRoot(EventCardEffectSpecFactory.Resolve(1, EventColor.Green, "A-01", primary));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(EventCardOptionIdFactory.CreateChoice(CardId, "go")), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.Map.Influences, Is.Empty, "尚未选目标时不能提前放置基础影响力。");
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.Zero);
            if (noLegalTarget)
            {
                Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Failed));
                Assert.That(state.EffectRuntime.InteractionRequests.Any(value => value.Status == "open"), Is.False);
                return;
            }
            request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(request.CandidateIds, Is.EqualTo(new[] { target }), "基础槽位不能作为额外目标。");
            var node = state.EffectRuntime.EffectNodes.Single(value => value.EffectId == root);
            Assert.That(EventCardEffectExecutor.TryValidateSaved(state, node, out reason), Is.True, reason);
            state = GameStateCloneService.DeepClone(state); engine = new EffectTreeExecutor(state, registry);
            request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(target), out reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(6));
            Assert.That(state.Map.Influences.Select(value => value.SlotId), Is.EquivalentTo(new[] { primary, target }));
            var placements = state.EffectRuntime.RuleEvents.Where(value => value.EventType == InfluenceEventTypeIds.InfluencePlaced).ToList();
            Assert.That(placements, Has.Count.EqualTo(2));
            Assert.That(ItemTransferEffectExecutor.Value(placements[0].Payload, "toSlot").ReferenceId, Is.EqualTo(primary));
            Assert.That(ItemTransferEffectExecutor.Value(placements[1].Payload, "toSlot").ReferenceId, Is.EqualTo(target));
        }

        [TestCase(72)] [TestCase(180)]
        public void InfluencePreselectionKeepsAllLegalTargetsBeyondSixtyFourAcrossClone(int candidateCount)
        {
            var pack = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var definitions = (List<ExternalContentDefinition>)typeof(ExternalContentPack).GetField("activeDefinitions", flags).GetValue(pack);
            var scripts = (Dictionary<string, string>)typeof(ExternalContentPack).GetField("scripts", flags).GetValue(pack);
            var mapDefinition = new GameMapDefinition { MapId = "fixture.event.many", MinPlayers = 1, MaxPlayers = 4,
                Locations = new List<MapLocationDefinition> { new MapLocationDefinition { LocationId = "A-01", RegionId = "A" },
                    new MapLocationDefinition { LocationId = "A-02", RegionId = "A" } },
                Regions = new List<MapRegionDefinition> { new MapRegionDefinition { RegionId = "A" } },
                Routes = Enumerable.Range(0, candidateCount).Select(index => new MapRouteDefinition { RouteId = "fixture.route." + index.ToString("D3"),
                    FromLocationId = "A-01", ToLocationId = "A-02", RegionId = "A", InfluenceSlotCount = 1 }).ToList() };
            var map = new MapQueryService(mapDefinition); var influence = new InfluenceService(map);
            var state = State(Card(1)); state.MapId = mapDefinition.MapId;
            var candidates = influence.GetLegalPlacementSlotIds(state, 1).OrderBy(id => id, StringComparer.Ordinal).ToList();
            Assert.That(candidates, Has.Count.EqualTo(candidateCount));
            const string body = "lua/fixture/many-preselect.lua", completion = "lua/fixture/many-preselect.completed.lua", handler = "fixture.many-preselect.complete";
            scripts.Add(body, "return function(ctx) return {Effect.Choice({player=ctx.playerId, options={" +
                string.Join(",", candidates.Select(id => "'" + id + "'")) + "}, interactionType='effect.influence.place.target', " +
                "resultContext={source='fixture'}, completionHandlerId='" + handler + "'})} end");
            scripts.Add(completion, "return function(ctx) return {Effect.PlaceInfluence({executingPlayer=ctx.playerId, ownerSubject=ctx.playerId, " +
                "influenceSource='fixture.pending', targetSlotId=ctx.payload.normalizedResult.value, causeKind='other_intrinsic_flow'})} end");
            definitions.Add(new ExternalContentDefinition { DefinitionId = CardId, RuntimeId = CardId, ContentType = "event", Version = "1.0.0",
                DisplayName = "多候选隔离夹具", AbilityHandlers = new List<ExternalAbilityHandler> { new ExternalAbilityHandler { HandlerId = handler, Script = completion, Version = "1.0.0" } },
                Data = new JObject { ["description"] = "夹具", ["color"] = 0, ["resourceType"] = 0, ["representativeResourceType"] = 0,
                    ["eventChoiceSchemaVersion"] = 1, ["choices"] = new JArray(new JObject { ["choiceId"] = "go", ["description"] = "选择位置",
                        ["button"] = new JObject { ["topPercent"] = 0, ["bottomPercent"] = 100 }, ["script"] = body, ["version"] = "1.0.0", ["completionHandlerId"] = handler }) } });
            var card = pack.CreateEvents().Single(value => value.CardId == CardId); state.ContentDefinitions.Events = new List<EventCardDefinition> { card };
            var registry = new EffectRegistry(); ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, influence);
            EventCardEffectExecutor.Register(registry, map, influence, new EventDeckService(321), new ResourceTokenService());
            EventCardLuaCatalog.Register(registry, pack);
            var engine = new EffectTreeExecutor(state, registry);
            string root = engine.CreateRoot(EventCardEffectSpecFactory.Resolve(1, EventColor.Green, "A-01"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(EventCardOptionIdFactory.CreateChoice(CardId, "go")), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(request.CandidateIds, Is.EqualTo(candidates));
            state = GameStateCloneService.DeepClone(state); engine = new EffectTreeExecutor(state, registry);
            request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(candidates.Last()), out reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.Map.Influences.Single().SlotId, Is.EqualTo(candidates.Last()));
        }

        [Test]
        public void LargeMapCandidatesDoNotRemoveUnrelatedLuaTableLimits()
        {
            const string source = "return function(ctx) local effect=Effect.ActivateFacilityEntry({player=ctx.playerId,instanceId='fixture'}); " +
                "effect.fields={}; for i=1,129 do effect.fields[i]=i end; return {effect} end";
            var script = new LuaScriptDefinition("fixture.budget", "fixture.budget", "fixture.budget", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var result = new MoonSharpLuaRuntimeHost().Invoke(script, new LuaInvocationContext("fixture.budget", "AbilityInvoked", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(LuaFailureCode.TableEntryLimitExceeded));
        }

        [TestCase("no_target")] [TestCase("decline")] [TestCase("pay")]
        public void PaidInfluenceChecksAvailabilityBeforePaymentAndKeepsPaymentBeforeTarget(string mode)
        {
            var pack = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var definitions = (List<ExternalContentDefinition>)typeof(ExternalContentPack).GetField("activeDefinitions", flags).GetValue(pack);
            var scripts = (Dictionary<string, string>)typeof(ExternalContentPack).GetField("scripts", flags).GetValue(pack);
            const string path = "lua/fixture/paid-influence.lua";
            scripts.Add(path, "return function(ctx) return { Effect.Condition({ leftEffects={Effect.PayResource({player=ctx.playerId, cost={GoldVoucher=4}})}, " +
                "rightEffects={Effect.PlaceInfluence({executingPlayer=ctx.playerId, ownerSubject=ctx.playerId, influenceSource='fixture.paid', causeKind='other_intrinsic_flow'})} }) } end");
            definitions.Add(new ExternalContentDefinition { DefinitionId = CardId, RuntimeId = CardId, ContentType = "event", Version = "1.0.0",
                DisplayName = "付费影响力夹具", Data = new JObject { ["description"] = "夹具", ["color"] = 0, ["resourceType"] = 0,
                    ["representativeResourceType"] = 0, ["eventChoiceSchemaVersion"] = 1, ["choices"] = new JArray(new JObject {
                        ["choiceId"] = "go", ["description"] = "付费放置", ["button"] = new JObject { ["topPercent"] = 0, ["bottomPercent"] = 100 },
                        ["script"] = path, ["version"] = "1.0.0" }) } });
            var card = pack.CreateEvents().Single(value => value.CardId == CardId); var state = State(card);
            state.FindPlayer(1).Resources.GoldVoucher = 8;
            if (mode == "no_target") state.FindPlayer(1).InfluenceSupply = 0;
            var registry = new EffectRegistry(); var map = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var influence = new InfluenceService(map); ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, influence);
            EventCardEffectExecutor.Register(registry, map, influence, new EventDeckService(321), new ResourceTokenService());
            EventCardLuaCatalog.Register(registry, pack);
            var engine = new EffectTreeExecutor(state, registry); string primary = InfluenceService.GetLocationSlotId("A-01", 0);
            string root = engine.CreateRoot(EventCardEffectSpecFactory.Resolve(1, EventColor.Green, "A-01", primary));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(EventCardOptionIdFactory.CreateChoice(CardId, "go")), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            if (mode == "no_target")
            {
                Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Failed));
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(8));
                Assert.That(state.Map.Influences, Is.Empty);
                Assert.That(state.EffectRuntime.InteractionRequests.Any(value => value.Status == "open"), Is.False);
                return;
            }
            Assert.That(state.Map.Influences.Select(value => value.SlotId), Is.EqualTo(new[] { primary }));
            request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(request.InteractionTypeId, Is.EqualTo("action.decline_effect"));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(8));
            state = GameStateCloneService.DeepClone(state); engine = new EffectTreeExecutor(state, registry);
            request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(mode == "pay" ? "continue" : "decline"), out reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            if (mode == "pay")
            {
                request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
                Assert.That(request.InteractionTypeId, Is.EqualTo("effect.influence.place.target"));
                Assert.That(request.CandidateIds, Does.Not.Contain(primary));
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
                string target = request.CandidateIds[0];
                state = GameStateCloneService.DeepClone(state); engine = new EffectTreeExecutor(state, registry);
                request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
                Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                    NormalizedValue.CreateString(target), out reason), Is.True, reason);
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            }
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(mode == "pay" ? 4 : 8));
            Assert.That(state.Map.Influences.Count, Is.EqualTo(mode == "pay" ? 2 : 1));
        }

        [TestCase("fixture.event_a", 5)] [TestCase("fixture.event_b", 9)]
        public void RegisteredChoiceRestoresSeparateCompletionScriptWithSharedHandlerName(string cardId, int reward)
        {
            // 仅在独立内存内容包中注入夹具；不解除正式读取保护，不修改安装文件。
            var pack = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var definitions = (List<ExternalContentDefinition>)typeof(ExternalContentPack).GetField("activeDefinitions", flags).GetValue(pack);
            var scripts = (Dictionary<string, string>)typeof(ExternalContentPack).GetField("scripts", flags).GetValue(pack);
            const string bodyPath = "lua/fixture/event-choice.lua", handlerId = "fixture.shared.done";
            scripts.Add(bodyPath, "return function(ctx) if ctx.payload.choiceId ~= 'go' then error('choice_id') end " +
                "return { Effect.Choice({ player=ctx.playerId, options={'continue'}, minSelections=1, maxSelections=1, completionHandlerId='fixture.shared.done' }) } end");
            foreach (var fixture in new[] { (Id: "fixture.event_a", Reward: 5), (Id: "fixture.event_b", Reward: 9) })
            {
                string path = "lua/fixture/" + fixture.Id + ".lua";
                scripts.Add(path, "return function(ctx) return { Effect.GainResource({recipient=ctx.playerId, resourceType='Originium', amount=" + fixture.Reward + "}) } end");
                definitions.Add(new ExternalContentDefinition { DefinitionId = fixture.Id, RuntimeId = fixture.Id, ContentType = "event",
                    DisplayName = "续接夹具", Version = "1.0.0", AbilityHandlers = new List<ExternalAbilityHandler> {
                        new ExternalAbilityHandler { HandlerId = handlerId, Script = path, Version = "2.0.0" } },
                    Data = new JObject { ["description"] = "夹具", ["color"] = 0, ["resourceType"] = 0,
                        ["representativeResourceType"] = 0, ["eventChoiceSchemaVersion"] = 1,
                        ["choices"] = new JArray(new JObject { ["choiceId"] = "go", ["description"] = "继续",
                            ["button"] = new JObject { ["topPercent"] = 10, ["bottomPercent"] = 90 },
                            ["script"] = bodyPath, ["version"] = "1.0.0", ["completionHandlerId"] = handlerId }) } });
            }
            var card = pack.CreateEvents().Single(value => value.CardId == cardId);
            var state = State(card);
            var registry = new EffectRegistry();
            var map = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var influence = new InfluenceService(map);
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, influence);
            EventCardEffectExecutor.Register(registry, map, influence, new EventDeckService(321), new ResourceTokenService());
            EventCardLuaCatalog.Register(registry, pack);
            var engine = new EffectTreeExecutor(state, registry);
            string root = engine.CreateRoot(EventCardEffectSpecFactory.Resolve(1, EventColor.Green, "A-01"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(EventCardOptionIdFactory.CreateChoice(cardId, "go")), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(request.CandidateIds, Does.Contain("continue"));
            state = GameStateCloneService.DeepClone(state);
            engine = new EffectTreeExecutor(state, registry);
            request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString("continue"), out reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(reward));
            Assert.That(state.EffectRuntime.DispatchReceipts.Count(receipt => receipt.Succeeded &&
                receipt.SubscriptionId.StartsWith("event-choice-continuation_", StringComparison.Ordinal)), Is.EqualTo(1));
        }
    }
}
