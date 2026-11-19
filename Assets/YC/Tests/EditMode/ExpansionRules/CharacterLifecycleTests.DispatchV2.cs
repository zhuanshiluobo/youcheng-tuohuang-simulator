using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class CharacterLifecycleTests
    {
        private static void DispatchPatch(EffectRegistry registry, string id, string body)
        {
            string source = "return function(ctx) " + body + " end";
            var script = new LuaScriptDefinition(id, id, id, "1", source, LuaContentHasher.ComputeSha256(source));
            new LuaEffectEventDispatcher(registry).Register(new LuaHandlerDefinition(id, OperatorDispatchFlow.StartingEvent, "*", script));
        }

        [Test]
        public void DispatchStartingUsesOnlyActivePersistentCharacterInstances()
        {
            var json = JObject.Parse(File.ReadAllText(Path.Combine(frozenPack.RootPath, "character/texas.json")));
            var ability = (JObject)json["abilities"][1];
            ability["skillType"] = "persistent"; ability["observedEvents"] = new JArray(OperatorDispatchFlow.StartingEvent);
            ability["observerSubjectPolicy"] = "self";
            string path = (string)ability["script"];
            var pack = frozenPack.CreateWithOverrides(new Dictionary<string, string> { ["character/texas.json"] = json.ToString(),
                [path] = "return function(ctx) if ctx.eventType=='OperatorDispatchStarting' then assert(ctx.payload.cardInstanceRef); return {drawCountDelta=1} end; return {} end" });
            var state = DispatchState(8); var registry = new EffectRegistry(); state.ContentDefinitions.Characters = pack.CreateCharacters().ToList();
            state.ContentPackHash = pack.ContentHash; state.RuleConfiguration.ContentPackHash = pack.ContentHash;
            ResourceEffectExecutor.Register(registry); ItemTransferEffectExecutor.Register(registry); DeckShuffleEffectExecutor.Register(registry);
            CharacterCoverEffectExecutor.Register(registry); CharacterLifecycleService.Register(registry);
            CharacterCardLuaCatalog.Register(registry, pack);
            string card = Ids(state, "hand", 1)[0]; Cover(state, registry, card); Use(state, registry, card, "tactic");
            Assert.That(CharacterLifecycleService.IsSubscriptionActive(state, card, (string)ability["abilityId"]), Is.True);
            Run(state, registry, DispatchSpec(state, 2)); Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(3));
            AnswerRequest(state, registry, decline: true);
            Run(state, registry, MoveSpec(1, new[] { card }, FindZone(state, "persistent", 1).ZoneId, FindZone(state, "retired").ZoneId, "unordered", 1, "core.character_card_transfer"));
            Run(state, registry, DispatchSpec(state, 2)); Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(2));
        }

        [Test]
        public void DispatchStartingUsesDeclaredAbilitySourcesWithoutInvokingTheirEffectBody()
        {
            var json = JObject.Parse(File.ReadAllText(Path.Combine(frozenPack.RootPath, "character/texas.json")));
            ((JArray)json["abilitySources"]).Add(new JObject { ["protocolVersion"] = 1, ["abilityId"] = "texas.dispatch-count", ["kind"] = "event",
                ["handlerRef"] = "texas.dispatch-count.handler", ["declarationVersion"] = "1.0.0", ["authorizationPolicy"] = "holder",
                ["effectiveZones"] = new JArray("core.zone.character_hand"), ["observedEvents"] = new JArray(OperatorDispatchFlow.StartingEvent),
                ["queryKinds"] = new JArray(), ["allowedOperations"] = new JArray(), ["presentationRef"] = "texas", ["actions"] = new JArray() });
            if (json["abilityHandlers"] == null) json["abilityHandlers"] = new JArray();
            ((JArray)json["abilityHandlers"]).Add(new JObject { ["handlerId"] = "texas.dispatch-count.handler", ["script"] = "lua/test-dispatch-count.lua", ["version"] = "1.0.0" });
            var pack = frozenPack.CreateWithOverrides(new Dictionary<string, string> { ["character/texas.json"] = json.ToString(),
                ["lua/test-dispatch-count.lua"] = "return function(ctx) assert(Effect==nil and ctx.payload.sourceItemId); return {drawCountDelta=1} end" });
            var state = DispatchState(20); var registry = Registry();
            state.ContentDefinitions.AbilitySources = AbilitySourceLuaCatalog.CreateDeclarations(pack); registry.ContentDefinitions = state.ContentDefinitions;
            AbilitySourceLuaCatalog.Register(registry, pack);
            int sources = registry.QuerySources(state, -1, "event", OperatorDispatchFlow.StartingEvent).Count;
            Assert.That(sources, Is.GreaterThan(0));
            var node = Run(state, registry, DispatchSpec(state, 2)); Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(2 + sources));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == AbilitySourceEffectExecutor.TypeId), Is.False);
            Assert.That(state.SetupRecipe.OperatorDrawCount, Is.EqualTo(2));
        }

        [TestCase(2, false, 3)][TestCase(3, false, 2)][TestCase(3, true, 3)]
        public void DispatchV2UsesRealExpansionRecipeAndFormalRewardLua(int players, bool infected, int expected)
        {
            var configuration = new GameRuleConfiguration { MapId = YC.Domain.Maps.StaticMapDefinitions.ThreePlayerMapId,
                PlayerCount = players, EnabledExpansionIds = infected ? new List<string> { "core", "enterprise", "infected" } : new List<string> { "core", "enterprise" } };
            var catalog = ActiveContentCatalogBuilder.Build(frozenPack, configuration);
            Assert.That(catalog.SetupRecipe.OperatorDrawCount, Is.EqualTo(expected));
            var state = DispatchState(8, players); state.SetupRecipe.OperatorDrawCount = catalog.SetupRecipe.OperatorDrawCount;
            var registry = Registry();
            string source = frozenPack.GetScript("lua/enterprises/enterprise.rhodes_island.level_3.reward.lua");
            var script = new LuaScriptDefinition("dispatch.formal", "dispatch.formal", "dispatch.formal", "2", source, LuaContentHasher.ComputeSha256(source));
            new LuaEffectEventDispatcher(registry).Register(new LuaHandlerDefinition("dispatch.formal", "test.dispatch.formal", "*", script, EffectHandlerRole.Observer));
            registry.Register("test.dispatch.formal", _ => EffectStepResult.Completed().AddEvent(new EffectEventRequest { EventType = "test.dispatch.formal", PlayerId = 1 }));
            Run(state, registry, EffectSpec.Create("test.dispatch.formal", playerId: 1));
            Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(expected));
            Assert.That(Text(state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == OperatorDispatchFlow.TypeId).NormalizedArguments, "deckZoneId"),
                Is.EqualTo(FindZone(state, "operator_deck").ZoneId));
            AnswerRequest(state, registry, decline: true);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(Ids(state, "operator_deck").Count, Is.EqualTo(8));
            state = Restore(state, registry);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
        }

        [Test]
        public void DispatchV2DeckLookupExposesMetadataWithoutHiddenCardsOrOrder()
        {
            var state = DispatchState(); var registry = Registry();
            const string source = "return function(ctx) local z=Global.Content.FindZones({localZoneId='operator_deck'})[1]; assert(z and z.itemCount==4); assert(z.topItemId==nil and z.orderDigest==nil and z.orderRevision==nil); assert(#Global.Content.FindInstances({zoneId=z.id})==0); return {} end";
            var script = new LuaScriptDefinition("dispatch.query", "dispatch.query", "dispatch.query", "2", source, LuaContentHasher.ComputeSha256(source));
            new LuaEffectEventDispatcher(registry).Register(new LuaHandlerDefinition("dispatch.query", "test.dispatch.query", "*", script, EffectHandlerRole.Observer));
            registry.Register("test.dispatch.query", _ => EffectStepResult.Completed().AddEvent(new EffectEventRequest { EventType = "test.dispatch.query", PlayerId = 1 }));
            Run(state, registry, EffectSpec.Create("test.dispatch.query", playerId: 1));
            Assert.That(Ids(state, "operator_deck").Count, Is.EqualTo(4));
        }

        [TestCase(1, false)][TestCase(4, true)][TestCase(5, false)]
        public void DispatchV2ReadsConfiguredCountWithoutHardcodedExpansionOverride(int count, bool infected)
        {
            var state = DispatchState(8); if (infected) state.RuleConfiguration.EnabledExpansionIds.Add("infected");
            var registry = Registry(); Run(state, registry, DispatchSpec(state, count));
            Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(count));
            var start = state.EffectRuntime.RuleEvents.Single(e => e.EventType == OperatorDispatchFlow.StartingEvent);
            Assert.That(Number(start.Payload, "baseDrawCount"), Is.EqualTo(count));
        }

        [Test]
        public void DispatchV2UsesExplicitSecondDeckAndReturnsUnselectedToThatDeck()
        {
            var state = DispatchState(6); var registry = Registry(); var primary = FindZone(state, "operator_deck");
            var other = GameStateCloneService.DeepClone(primary); other.ZoneId += ".alternate"; other.LocalZoneId = "alternate_operators"; other.Configuration.LocalZoneId = other.LocalZoneId;
            state.ItemZones.Zones.Add(other); state.ItemZones.Orders.Add(new ZoneOrderState { ZoneId = other.ZoneId });
            var moved = Ids(state, "operator_deck").Take(3).ToArray();
            Run(state, registry, MoveSpec(1, moved, primary.ZoneId, other.ZoneId, "bottom", 3, "core.character_card_transfer"));
            var unchanged = Ids(state, "operator_deck").ToArray(); state.SetupRecipe.OperatorDrawCount = 2;
            Run(state, registry, OperatorDispatchFlow.CreateSpec(1, other.ZoneId));
            Assert.That(Open(state).CandidateIds, Is.SubsetOf(moved));
            AnswerRequest(state, registry, decline: true);
            Assert.That(new ItemZoneQuery().GetItemIds(state, other.ZoneId), Is.EquivalentTo(moved));
            Assert.That(Ids(state, "operator_deck"), Is.EqualTo(unchanged));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
        }

        [Test]
        public void DispatchV2EmptyDeckScoresOnceAndRecoversWithoutAnOpenRequest()
        {
            var state = DispatchState(0); var registry = Registry(); var node = Run(state, registry, DispatchSpec(state, 4));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Flag(node.NormalizedResult, "changed"), Is.True);
            Assert.That(Flag(node.NormalizedResult, "accepted"), Is.False);
            Assert.That(Number(node.NormalizedResult, "scoreAwarded"), Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == OperatorDispatchFlow.StartingEvent), Is.EqualTo(1));
        }

        [Test]
        public void DispatchV2EmptyDeckCompletesConditionLeftAndExecutesRight()
        {
            var state = DispatchState(0); var registry = Registry();
            var condition = EffectSpec.Create(EffectTypeIds.Condition, Object(), 1);
            condition.NestedEffectGroups.Add(new List<EffectSpec> { DispatchSpec(state, 3) });
            condition.NestedEffectGroups.Add(new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(1, 2) });
            var node = Run(state, registry, condition);
            Assert.That(Flag(node.NormalizedResult, "conditionMet"), Is.True);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(3));
        }

        [Test]
        public void DispatchV2EmptyDeckRecoversBetweenScoreSchedulingAndPayment()
        {
            var state = DispatchState(0); var registry = Registry(); var engine = new EffectTreeExecutor(state, registry);
            string id = engine.CreateRoot(DispatchSpec(state, 3));
            for (int step = 0; step < 100 && engine.GetNode(id).FlowStage != "empty_deck_scored"; step++) Assert.That(engine.Advance(), Is.True);
            Assert.That(engine.GetNode(id).FlowStage, Is.EqualTo("empty_deck_scored"));
            Assert.That(state.FindPlayer(1).Score, Is.Zero);
            state = Restore(state, registry); Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
        }

        [Test]
        public void DispatchV2StartingPatchesMergeWithoutEffectChildrenOrOrdinaryEventResponses()
        {
            var state = DispatchState(8); var registry = Registry();
            DispatchPatch(registry, "dispatch.plus", "assert(ctx.eventType=='OperatorDispatchStarting' and ctx.payload.baseDrawCount==2); assert(Effect==nil and Flow==nil); return {drawCountDelta=3}");
            DispatchPatch(registry, "dispatch.minus", "return {drawCountDelta=-1}");
            DispatchPatch(registry, "dispatch.noop", "return {}");
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("dispatch.ordinary", OperatorDispatchFlow.StartingEvent, "*", "dispatch.ordinary",
                _ => throw new AssertionException("纯前置修正不得分发普通效果响应"), EffectHandlerRole.Observer));
            var node = Run(state, registry, DispatchSpec(state, 2));
            Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(4));
            Assert.That(Number(node.ContinuationData, "drawCountDelta"), Is.EqualTo(2));
            Assert.That(state.EffectRuntime.RuleEvents.Single(e => e.EventType == OperatorDispatchFlow.StartingEvent).ResponseKind,
                Is.EqualTo(RuleEventResponseKind.OperatorDispatchPatches));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == "effect.operator.modify_draw_count" || n.EffectTypeId == AbilitySourceEffectExecutor.TypeId), Is.False);
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(4));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == OperatorDispatchFlow.StartingEvent), Is.EqualTo(1));
        }

        [TestCase(0)][TestCase(6)]
        public void DispatchV2FreezesPatchResultBeforeDrawAndDoesNotReinvokeAfterRecovery(int operators)
        {
            var state = DispatchState(operators); var registry = Registry(); int calls = 0;
            registry.RegisterOperatorDispatchCountPolicy("dispatch.once", "1", (c, p) => { calls++; return new[] { 2 }; });
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(DispatchSpec(state, 2));
            for (int step = 0; step < 100 && engine.GetNode(id).FlowStage != "before_draw"; step++) Assert.That(engine.Advance(), Is.True);
            Assert.That(engine.GetNode(id).FlowStage, Is.EqualTo("before_draw"));
            Assert.That(Ids(state, "operator_deck").Count, Is.EqualTo(operators));
            Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
            Assert.That(calls, Is.EqualTo(1));
            state = Restore(state, registry); Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(calls, Is.EqualTo(1));
            if (operators == 0) Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            else Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(4));
        }

        [TestCase("{drawCountDelta=-2}")][TestCase("{drawCountDelta=99}")][TestCase("{drawCountDelta=100}")]
        [TestCase("{drawCountDelta=1.5}")][TestCase("{drawCountDelta='1'}")][TestCase("{drawCount=3}")]
        [TestCase("{effects={}}")][TestCase("{memory={}}")][TestCase("{{kind='effect_spec',effectTypeId='effect.resource.gain_score'}}")]
        [TestCase("nil")]
        public void DispatchV2RejectsInvalidPurePatchWithoutDrawingOrScoring(string result)
        {
            var state = DispatchState(); var registry = Registry(); DispatchPatch(registry, "dispatch.invalid", "return " + result);
            var node = Run(state, registry, DispatchSpec(state, 2));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(Ids(state, "operator_deck").Count, Is.EqualTo(4));
            Assert.That(state.FindPlayer(1).Score, Is.Zero);
            Assert.That(node.ChildEffectIds, Is.Empty);
            Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
        }

        [Test]
        public void DispatchV2PatchQueriesCannotChangeStateOrOtherQueries()
        {
            var state = DispatchState(); var registry = Registry();
            registry.RegisterOperatorDispatchCountPolicy("a", "1", (context, payload) => { context.State.FindPlayer(1).Score = 99; return new[] { 1 }; });
            registry.RegisterOperatorDispatchCountPolicy("b", "1", (context, payload) => { Assert.That(context.State.FindPlayer(1).Score, Is.Zero); return new[] { -1 }; });
            Run(state, registry, DispatchSpec(state, 2));
            Assert.That(state.FindPlayer(1).Score, Is.Zero);
            Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(2));
        }

        [Test]
        public void DispatchCountEffectIsNotAnApiOrExecutableNode()
        {
            const string code = "return function(ctx) return {Effect.ModifyOperatorDispatchDrawCount({apiVersion=1,player=ctx.playerId,dispatchEffectId='x',operation='add',amount=1})} end";
            var script = new LuaScriptDefinition("dispatch.retired", "dispatch.retired", "dispatch.retired", "1", code, LuaContentHasher.ComputeSha256(code));
            Assert.That(new MoonSharpLuaRuntimeHost().Invoke(script, new LuaInvocationContext("event", "test", "p1", 0, "1", 2, 0, 0)).IsSuccess, Is.False);
            var registry = Registry(); Assert.That(registry.TryGet("effect.operator.modify_draw_count", out _), Is.False);
            Assert.That(new EffectTreeExecutor(DispatchState(), registry).TryCreateRoot(EffectSpec.Create("effect.operator.modify_draw_count", playerId: 1), "", "", out _), Is.False);
        }

        [TestCase("missing")][TestCase("facility_deck")][TestCase("hand")]
        public void DispatchV2InvalidDeckCannotProduceEmptyDeckScore(string deck)
        {
            var state = DispatchState(); var registry = Registry(); state.SetupRecipe.OperatorDrawCount = 2;
            string zone = deck == "missing" ? "missing" : FindZone(state, deck, deck == "hand" ? 1 : -1).ZoneId;
            Assert.That(Run(state, registry, OperatorDispatchFlow.CreateSpec(1, zone)).Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(state.FindPlayer(1).Score, Is.Zero);
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == OperatorDispatchFlow.StartingEvent), Is.False);
        }

        [TestCase("apiVersion=1,player=ctx.playerId,drawCount=3")]
        [TestCase("apiVersion=2,player=ctx.playerId")]
        [TestCase("apiVersion=2,player=ctx.playerId,deckZoneId='deck',drawCount=3")]
        public void DispatchV2LuaRejectsLegacyCountAndMissingExplicitDeck(string fields)
        {
            string code = "return function(ctx) return {Flow.DispatchOperator({" + fields + "})} end";
            var script = new LuaScriptDefinition("dispatch.invalid", "dispatch.invalid", "dispatch.invalid", "1", code, LuaContentHasher.ComputeSha256(code));
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(script, new LuaInvocationContext("event", "test", "1", 0, "1", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.False);
        }
    }
}
