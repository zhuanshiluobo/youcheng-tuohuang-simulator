using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class ItemTransferEffectTests
    {
        private static IsolatedTestArtifacts artifacts;
        private static GameState observed;
        [SetUp] public void LoadProductionFacilityDefinitions()
        {
            observed = null; artifacts = IsolatedTestArtifacts.BeginCase();
            var pack = ExternalContentPack.Current;
            artifacts.WriteLabeledText("只读安装设施输入", JsonConvert.SerializeObject(new { pack.RootPath, pack.ContentHash, copiedContent = false }));
            if (!FacilityCardDatabase.IsInitialized) FacilityCardDatabase.InitializeExternal(pack.CreateFacilities());
        }
        [TearDown] public void FinishEvidence()
        {
            if (observed != null) artifacts.WriteLabeledText("当前真实状态", JsonConvert.SerializeObject(observed));
            artifacts.Finish();
        }
        private static GameState State(int facilities = 9, string entryDefinition = null)
        {
            var map = new GameMapDefinition { MapId = "test.transfer_map", MinPlayers = 2, MaxPlayers = 4,
                Regions = new List<MapRegionDefinition> { new MapRegionDefinition { RegionId = "harbor_region" } },
                Locations = new List<MapLocationDefinition> { new MapLocationDefinition { LocationId = "harbor", RegionId = "harbor_region", CanDockCity = true, InfluenceSlotCount = 2 } } };
            var source = new GameState { GameId = "game.transfer", MapId = map.MapId, ContentPackHash = "content.transfer", RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = "content.transfer" },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "甲", Resources = new ResourceSet { GoldVoucher = 100, Originium = 100, OriginiumShard = 100, Iron = 100 } }, new PlayerState { PlayerId = 2, Name = "乙" } } };
            source.Round = 1; source.ActionRound = 1; source.Phase = GamePhase.ActionRound1; source.CurrentPlayerId = source.StartPlayerId = 1;
            source.RandomRuntime = GameplayRandomRuntime.Create(source.GameId);
            var recipe = new List<ItemRecipeEntry> { new ItemRecipeEntry { EntryId = "initial_characters", DefinitionId = "texas", TypeId = "core.basic_character_card", Quantity = 2, PerPlayer = true, InitialZoneRole = "hand" },
                new ItemRecipeEntry { EntryId = "supply", DefinitionId = "component.marker", TypeId = "core.player_marker", Quantity = 4, PerPlayer = true, InitialZoneRole = "supply" } };
            for (int index = 0; index < facilities; index++) recipe.Add(new ItemRecipeEntry { EntryId = "facility_" + index, DefinitionId = index == 0 && entryDefinition != null ? entryDefinition : "building_" + (index + 1).ToString("000"), TypeId = "core.facility_card", Quantity = 1, InitialZoneRole = "facility_deck" });
            return observed = new ItemZoneStateFactory().CreateForSetup(source, map, recipe);
        }
        private static EffectRegistry Registry(Func<int, int> next = null)
        {
            var registry = new EffectRegistry(); ItemTransferEffectExecutor.Register(registry); DeckShuffleEffectExecutor.Register(registry, next); RepeatEffectExecutor.Register(registry); ResourceEffectExecutor.Register(registry); FacilityEntryEffectExecutor.Register(registry); FacilitySelectionEffectExecutor.Register(registry); return registry;
        }
        private static ZoneInstanceState Zone(GameState state, string local, int player = -1) => FindZone(state, local, player);
        private static List<string> Ids(GameState state, string local, int player = -1) => new ItemZoneQuery().GetItemIds(state, Zone(state, local, player).ZoneId).ToList();
        private static EffectNodeRuntimeState Run(GameState state, EffectSpec spec, EffectRegistry registry = null)
        {
            var engine = new EffectTreeExecutor(state, registry ?? Registry()); string id = engine.CreateRoot(spec); var report = engine.RunUntilQuiescent(); Assert.That(report.Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state); return engine.GetNode(id);
        }
        private static void Valid(GameState state) => Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.True, reason);
        private static GameState Restore(GameState state, EffectRegistry registry)
        {
            var result = artifacts.Recover(state, "R2真实转移恢复", registry); Assert.That(result.Succeeded, Is.True, result.Diagnostic); Valid(result.State); return observed = result.State;
        }
        private static void Fill(GameState state, int count = 6) { for (int index = 0; index < count; index++) Run(state, RefillSpec(1, Zone(state, "facility_supply_" + index).ZoneId)); }
        private static InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.Last(request => request.Status == "open");
        private static void AnswerRequest(GameState state, EffectRegistry registry, string value)
        {
            var request = Open(state); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, NormalizedValue.CreateString(value), out var reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state);
        }
        private static EffectSpec Shuffle(GameState state) => EffectSpec.Create(DeckShuffleEffectExecutor.TypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "deckZone", Object("zoneId", NormalizedValue.CreateString(Zone(state, "facility_deck").ZoneId))), 1);

        [Test] public void RequiredCountRejectsWholeGroupAndConditionReward()
        {
            var state = State(); var ids = Ids(state, "hand", 1); string source = Zone(state, "hand", 1).ZoneId;
            var move = MoveSpec(1, ids, source, Zone(state, "discard", 1).ZoneId, "unordered", 3, "core.character_card_transfer");
            int score = state.FindPlayer(1).Score; Run(state, EffectSpec.Condition(new[] { move }, new[] { ResourceEffectSpecFactory.GainScore(1, 5) }));
            Assert.That(Ids(state, "hand", 1), Is.EqualTo(ids)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score)); Assert.That(state.EffectRuntime.EffectNodes.Any(node => node.FailureReason == "insufficient_items"), Is.True);
        }
        [TestCase("duplicate_item_id")][TestCase("unknown_item")][TestCase("source_zone_changed")]
        public void InvalidGroupLeavesEveryMemberInPlace(string failure)
        {
            var state = State(); var ids = Ids(state, "hand", 1); var selected = new List<string>(ids); string source = Zone(state, "hand", 1).ZoneId;
            if (failure == "duplicate_item_id") selected[1] = selected[0]; else if (failure == "unknown_item") selected[1] = "unknown"; else source = Zone(state, "discard", 1).ZoneId;
            var node = Run(state, MoveSpec(1, selected, source, Zone(state, "discard", 1).ZoneId, "unordered", 0, "core.character_card_transfer")); Assert.That(node.FailureReason, Is.EqualTo(failure)); Assert.That(Ids(state, "hand", 1), Is.EqualTo(ids));
        }
        [Test] public void WrongTypedWrapperRejectsCharacterInFacilityMove()
        {
            var state = State(); var spec = MoveSpec(1, Ids(state, "hand", 1), Zone(state, "hand", 1).ZoneId, Zone(state, "discard", 1).ZoneId, "unordered", 0, "core.character_card_transfer");
            spec.EffectTypeId = LuaDomainEffectTypeIds.MoveFacilityCard; Value(spec.NormalizedArguments, "apiVersion").IntegerValue = 2;
            Assert.That(Run(state, spec).FailureReason, Is.EqualTo("destination_rejected"));
        }
        [Test] public void OtherPlayersPrivateSourceCannotBeTransferred()
        {
            var state = State(); var node = Run(state, MoveSpec(1, Ids(state, "hand", 2), Zone(state, "hand", 2).ZoneId, Zone(state, "retired").ZoneId, "unordered", 0, "core.character_card_transfer")); Assert.That(node.FailureReason, Is.EqualTo("transfer_permission_denied")); Assert.That(Ids(state, "hand", 2).Count, Is.EqualTo(2));
        }
        [Test] public void EmptySuccessDoesNotBlockIndependentSibling()
        {
            var state = State(); var move = MoveSpec(1, Array.Empty<string>(), Zone(state, "hand", 1).ZoneId, Zone(state, "discard", 1).ZoneId, "unordered", 0, "core.character_card_transfer");
            var parent = EffectSpec.Create("test.sequence", playerId: 1); parent.Children.Add(move); parent.Children.Add(ResourceEffectSpecFactory.GainScore(1, 2)); var registry = Registry(); registry.Register("test.sequence", context => EffectStepResult.Completed().AddChildren(parent.Children));
            Run(state, parent, registry); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2)); var node = state.EffectRuntime.EffectNodes.Single(value => value.EffectTypeId == TypeId); Assert.That(Number(node.NormalizedResult, "movedCount"), Is.Zero);
        }
        [Test] public void BatchInsertionAtTopPreservesRequestedOrderAndHostChanges()
        {
            var state = State(); var allItems = state.ItemZones.Items.Select(item => item.ItemId).ToArray(); var selected = Ids(state, "facility_deck").Take(3).Reverse().ToArray();
            Run(state, MoveSpec(1, selected, Zone(state, "facility_deck").ZoneId, Zone(state, "retired").ZoneId, "unordered", 3));
            var deckBefore = Ids(state, "facility_deck");
            var node = Run(state, MoveSpec(1, selected, Zone(state, "retired").ZoneId, Zone(state, "facility_deck").ZoneId, "top", 3));
            Assert.That(Ids(state, "facility_deck"), Is.EqualTo(selected.Concat(deckBefore))); Assert.That(node.PositionChanges.Select(change => change.ItemId), Is.EqualTo(selected));
            state = Restore(state, Registry()); Assert.That(state.EffectRuntime.EffectNodes.Single(value => value.EffectId == node.EffectId).PositionChanges.All(change => change.To.ZoneId == Zone(state, "facility_deck").ZoneId), Is.True);
            Assert.That(state.ItemZones.Items.Select(item => item.ItemId), Is.EquivalentTo(allItems));
        }
        [Test] public void RefillExistingSlotDoesNotChangeDeckOrOccupant()
        {
            var state = State(); Fill(state, 1); var deck = Ids(state, "facility_deck"); var supply = Ids(state, "facility_supply_0");
            var engine = new EffectTreeExecutor(state, Registry()); engine.CreateRoot(RefillSpec(1, Zone(state, "facility_supply_0").ZoneId)); Assert.That(engine.RunUntilQuiescent().Faulted, Is.True); Assert.That(Ids(state, "facility_deck"), Is.EqualTo(deck)); Assert.That(Ids(state, "facility_supply_0"), Is.EqualTo(supply));
        }
        [TestCase(0)][TestCase(1)][TestCase(3)] public void ShortDeckLeavesSixStableSlotsWithoutPlaceholders(int count)
        {
            var state = State(count); var slots = state.ItemZones.Zones.Where(zone => zone.TemplateId == "core.zone.facility_supply_slot").Select(zone => zone.ZoneId).ToArray(); Fill(state);
            Assert.That(state.ItemZones.Items.Count(item => item.TypeId == "core.facility_card"), Is.EqualTo(count)); Assert.That(slots.Length, Is.EqualTo(6)); Assert.That(state.Decks.FacilitySupply.Count, Is.EqualTo(6)); Assert.That(state.Decks.FacilitySupply.Count(id => id.Length == 0), Is.EqualTo(6 - count)); Assert.That(Ids(state, "facility_deck"), Is.Empty);
        }
        [TestCase(0)][TestCase(1)] public void EmptyOrSingleDeckSucceedsWithoutRandomOrRevision(int count)
        {
            var state = State(count); int rolls = 0; var registry = Registry(max => { rolls++; return 0; }); var order = state.ItemZones.Orders.Single(value => value.ZoneId == Zone(state, "facility_deck").ZoneId); var node = Run(state, Shuffle(state), registry);
            Assert.That(rolls, Is.Zero); Assert.That(order.OrderRevision, Is.Zero); Assert.That(Flag(node.NormalizedResult, "changed"), Is.False); Assert.That(Number(node.NormalizedResult, "cardCount"), Is.EqualTo(count));
        }
        [Test] public void ShuffleCompletionRecoveryNeverRollsAgain()
        {
            var state = State(); int calls = 0; var registry = Registry(max => { calls++; return 0; }); var node = Run(state, Shuffle(state), registry); var order = Ids(state, "facility_deck"); int revision = state.ItemZones.Orders.Single(value => value.ZoneId == Zone(state, "facility_deck").ZoneId).OrderRevision;
            Assert.That(calls, Is.EqualTo(8)); Assert.That(node.ShuffleInputItemIds.Count, Is.EqualTo(9)); state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(calls, Is.EqualTo(8)); Assert.That(Ids(state, "facility_deck"), Is.EqualTo(order)); Assert.That(state.ItemZones.Orders.Single(value => value.ZoneId == Zone(state, "facility_deck").ZoneId).OrderRevision, Is.EqualTo(revision));
        }
        [Test] public void ShuffleNonDeckIsRejectedWithoutMutation()
        {
            var state = State(); var spec = Shuffle(state); Value(Value(spec.NormalizedArguments, "deckZone"), "zoneId").StringValue = Zone(state, "hand", 1).ZoneId; Assert.That(Run(state, spec).FailureReason, Is.EqualTo("orderingKind_not_deck"));
        }
        [Test] public void LegacyFacilityWrapperRefillsOnlyOriginalSlotAndReturnsOldCardId()
        {
            var state = State(); Fill(state); var original = state.Decks.FacilitySupply.ToArray(); string selected = Ids(state, "facility_supply_2")[0]; string definition = state.ItemZones.Items.Single(item => item.ItemId == selected).DefinitionId; string top = Ids(state, "facility_deck")[0]; var registry = Registry();
            var spec = EffectSpec.Create(LuaDomainEffectTypeIds.MoveFacilityCard, Object("sourceZone", NormalizedValue.CreateString("supply"), "destinationZone", NormalizedValue.CreateString("deck_bottom")), 1);
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(spec); engine.RunUntilQuiescent(); AnswerRequest(state, registry, selected); Assert.That(Ids(state, "facility_supply_2"), Is.EqualTo(new[] { top })); Assert.That(state.EffectRuntime.EffectNodes.Single(node => node.EffectId == id).NormalizedResult.StringValue, Is.EqualTo(definition)); for (int i = 0; i < 6; i++) if (i != 2) Assert.That(state.Decks.FacilitySupply[i], Is.EqualTo(original[i]));
        }
        [Test] public void VersionedFacilityWrapperDoesNotImplicitlyRefill()
        {
            var state = State(); Fill(state, 1); var spec = MoveSpec(1, Ids(state, "facility_supply_0"), Zone(state, "facility_supply_0").ZoneId, Zone(state, "facility_deck").ZoneId, "bottom", 1); spec.EffectTypeId = LuaDomainEffectTypeIds.MoveFacilityCard; Value(spec.NormalizedArguments, "apiVersion").IntegerValue = 2; Run(state, spec); Assert.That(Ids(state, "facility_supply_0"), Is.Empty);
        }
        [Test] public void LegacyCharacterRecallUsesSameInstancesAndIntegerResult()
        {
            var state = State(); var ids = Ids(state, "hand", 1); Run(state, MoveSpec(1, ids, Zone(state, "hand", 1).ZoneId, Zone(state, "discard", 1).ZoneId, "unordered", 0, "core.character_card_transfer")); var node = Run(state, EffectSpec.Create(LuaDomainEffectTypeIds.MoveCharacterCard, Object("sourceZone", NormalizedValue.CreateString("discard"), "destinationZone", NormalizedValue.CreateString("hand")), 1)); Assert.That(node.NormalizedResult.Kind, Is.EqualTo(NormalizedValueKind.Integer)); Assert.That(node.NormalizedResult.IntegerValue, Is.EqualTo(2)); Assert.That(Ids(state, "hand", 1), Is.EquivalentTo(ids));
        }
        [Test] public void RefreshRecoveryAfterClearingUsesFrozenOriginalSetAndOneShuffle()
        {
            var state = State(); Fill(state); var original = state.ItemZones.Items.Select(item => item.ItemId).ToArray(); var oldSupply = Enumerable.Range(0, 6).SelectMany(index => Ids(state, "facility_supply_" + index)).ToArray(); int rolls = 0; var registry = Registry(max => { rolls++; return 0; }); var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(RefreshSpec(1));
            for (int steps = 0; steps < 300 && !(engine.GetNode(id).FlowStage == "returned" && Enumerable.Range(0, 6).All(index => Ids(state, "facility_supply_" + index).Count == 0)); steps++) engine.Advance();
            Assert.That(rolls, Is.Zero); state = Restore(state, registry); Assert.That(Value(state.EffectRuntime.EffectNodes.Single(node => node.EffectId == id).ContinuationData, "selectedItemIds").Items.Select(value => value.StringValue), Is.EqualTo(oldSupply)); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Valid(state); Assert.That(rolls, Is.EqualTo(8)); Assert.That(state.ItemZones.Items.Select(item => item.ItemId), Is.EquivalentTo(original)); Assert.That(state.Decks.FacilitySupply.Count(value => value.Length > 0), Is.EqualTo(6));
        }
        private static EffectSpec Repeat(int count, EffectSpec body, bool early = false)
        {
            var spec = EffectSpec.Create(LuaDomainEffectTypeIds.Repeat, Object("apiVersion", NormalizedValue.CreateInteger(1), "count", NormalizedValue.CreateInteger(count), "allowEarlyStop", NormalizedValue.CreateBoolean(early)), 1); spec.NestedEffectGroups.Add(new List<EffectSpec> { body }); return spec;
        }
        [Test] public void RepeatBuildsNextChoiceOnlyAfterPreviousRefillAndRecoversIndex()
        {
            var state = State(); Fill(state, 1); var registry = Registry();
            registry.Register("test.take_supply", context =>
            {
                if (context.Node.FlowStage == "taken") return EffectStepResult.Completed();
                string slot = Zone(context.State, "facility_supply_0").ZoneId; var ids = Ids(context.State, "facility_supply_0");
                if (context.Node.FlowStage.Length == 0) { var request = new EffectInteractionSpec { InteractionTypeId = "lua.choice", AnsweringPlayerId = 1, Visibility = "owner", PromptKey = "test.take", MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_id" }; request.CandidateIds.AddRange(ids); return EffectStepResult.Continue("choose").AddInteraction(request); }
                return EffectStepResult.Continue("taken").AddChild(MoveSpec(1, new[] { Answer(context) }, slot, Zone(context.State, "retired").ZoneId, "unordered", 1)).AddChild(RefillSpec(1, slot));
            }, EffectExecutorKind.IntrinsicFlow);
            var engine = new EffectTreeExecutor(state, registry); string parent = engine.CreateRoot(Repeat(2, EffectSpec.Create("test.take_supply", playerId: 1))); engine.RunUntilQuiescent(); string first = Open(state).CandidateIds[0]; AnswerRequest(state, registry, first); Assert.That(Open(state).CandidateIds, Does.Not.Contain(first)); string second = Open(state).CandidateIds[0]; state = Restore(state, registry); Assert.That(Number(state.EffectRuntime.EffectNodes.Single(node => node.EffectId == parent).ContinuationData, "completedCount"), Is.EqualTo(1)); AnswerRequest(state, registry, second); Assert.That(Number(state.EffectRuntime.EffectNodes.Single(node => node.EffectId == parent).NormalizedResult, "completedCount"), Is.EqualTo(2)); Assert.That(Ids(state, "retired"), Is.EquivalentTo(new[] { first, second }));
        }
        [Test] public void RepeatCanStopWithoutUndoingCompletedIterations()
        {
            var state = State(); var registry = Registry(); Run(state, Repeat(3, ResourceEffectSpecFactory.GainScore(1, 2), true), registry); AnswerRequest(state, registry, "continue"); state = Restore(state, registry); AnswerRequest(state, registry, "stop"); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2)); Assert.That(Number(state.EffectRuntime.EffectNodes.Single(node => node.EffectTypeId == LuaDomainEffectTypeIds.Repeat).NormalizedResult, "completedCount"), Is.EqualTo(1));
        }
        [TestCase(0)][TestCase(-1)][TestCase(3000)] public void RepeatEnforcesCountAndBudget(int count)
        {
            var state = State(); var node = Run(state, Repeat(count, ResourceEffectSpecFactory.GainScore(1, 1))); Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(node.Status, Is.EqualTo(count == 0 ? EffectNodeStatus.Completed : EffectNodeStatus.Failed));
        }
        [Test] public void NormalBuildRecoversPaidInstanceOriginalSlotAndAllSubstepResults()
        {
            var state = State(); Fill(state); var registry = Registry(); string selected = Ids(state, "facility_supply_2")[0]; string top = Ids(state, "facility_deck")[0]; var definition = FacilityCardDatabase.Get(state.ItemZones.Items.Single(item => item.ItemId == selected).DefinitionId); int money = state.FindPlayer(1).Resources.GoldVoucher;
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(FacilityBuildEffectSpecFactory.Build(1, selected, -1, "gold", false, "test.build", false)); var report = engine.RunUntilQuiescent(); Assert.That(report.Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(money - definition.GoldVoucherCost)); Assert.That(Ids(state, "facility_supply_2"), Is.EqualTo(new[] { top })); state = Restore(state, registry); AnswerRequest(state, registry, "0"); Assert.That(Ids(state, "city_cell_0", 1), Is.EqualTo(new[] { selected })); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(money - definition.GoldVoucherCost)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(definition.Score)); var node = state.EffectRuntime.EffectNodes.Single(value => value.EffectId == id); Assert.That(Flag(node.ContinuationData, "paid"), Is.True); Assert.That(Flag(node.ContinuationData, "refilled"), Is.True); var childTypes = node.ChildEffectIds.ConvertAll(childId => state.EffectRuntime.EffectNodes.Single(child => child.EffectId == childId).EffectTypeId); Assert.That(childTypes, Is.EquivalentTo(new[] { FacilityBuildEffectExecutor.PaymentTypeId, ItemTransferEffectExecutor.TypeId, ItemTransferEffectExecutor.RefillTypeId, ItemTransferEffectExecutor.TypeId })); state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(Ids(state, "city_cell_0", 1), Is.EqualTo(new[] { selected }));
        }
        [Test] public void CancelledDeckBuildStillAllowsFollowingShuffleAndPrivateCandidates()
        {
            var state = State(); int rolls = 0; var registry = Registry(max => { rolls++; return 0; }); registry.Register("test.deck_action", context => EffectStepResult.Completed().AddChild(EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, Object("sourceZone", NormalizedValue.CreateString("deck"), "allowDecline", NormalizedValue.CreateBoolean(true)), 1)).AddChild(Shuffle(context.State)));
            Run(state, EffectSpec.Create("test.deck_action", playerId: 1), registry); var request = Open(state); Assert.That(InteractionRequestProjector.ProjectForPlayer(request, 1).VisibleToViewer, Is.True); Assert.That(InteractionRequestProjector.ProjectForPlayer(request, 2).CandidateIds, Is.Empty); Assert.That(request.AllowDecline, Is.True);
            string requestId = request.GetStableInteractionId(); state = Restore(state, registry); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(requestId, 1, NormalizedValue.CreateBoolean(false), out string reason), Is.True, reason); Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(rolls, Is.EqualTo(8)); Assert.That(engine.TrySubmitInteraction(requestId, 1, NormalizedValue.CreateBoolean(false), out _), Is.False); engine.RunUntilQuiescent(); Assert.That(rolls, Is.EqualTo(8)); Assert.That(state.Map.Facilities.Count, Is.Zero);
        }
        [Test] public void LuaTransferShuffleAndRepeatCompileIntoRegisteredExecutors()
        {
            string source = "return function(ctx) return { Effect.MoveItem({apiVersion=1,player=ctx.playerId,itemRefs={},expectedSource={zoneId='source'},destination={zoneId='target'},insertion={kind='unordered'},operationProfileId='core.character_card_transfer'}), Effect.ShuffleDeck({apiVersion=1,player=ctx.playerId,deckZone={zoneId='deck'}}), Effect.Repeat({apiVersion=1,count=2,effects={Effect.GainScore({recipient=ctx.playerId,amount=1})}}) } end";
            var definition = new LuaScriptDefinition("content.transfer", "ability.transfer", "handler.transfer", "1.0.0", source, LuaContentHasher.ComputeSha256(source)); var result = new MoonSharpLuaRuntimeHost().Invoke(definition, new LuaInvocationContext("event.transfer", "event.test", "1", 0, "1.0.0", 2, 0, 0)); Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(result, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic); var registry = Registry(); Assert.That(specs.All(spec => registry.TryGet(spec.EffectTypeId, out _)), Is.True); Assert.That(specs[2].NestedEffectGroups.Single().Count, Is.EqualTo(1));
        }
        [Test] public void UnchangedItemsCannotSatisfyRequiredPaymentCount()
        {
            var state = State(); var hand = Ids(state, "hand", 1); string source = Zone(state, "hand", 1).ZoneId;
            var node = Run(state, MoveSpec(1, hand, source, source, "unordered", 1, "core.character_card_transfer"));
            Assert.That(node.FailureReason, Is.EqualTo("insufficient_items")); Assert.That(Ids(state, "hand", 1), Is.EqualTo(hand));
        }
        [Test] public void PublicTransferCannotBypassFacilityBuildPaymentAndEntry()
        {
            var state = State(); Fill(state, 1); string selected = Ids(state, "facility_supply_0")[0];
            var node = Run(state, MoveSpec(1, new[] { selected }, Zone(state, "facility_supply_0").ZoneId, Zone(state, "city_cell_0", 1).ZoneId, "unordered", 1));
            Assert.That(node.FailureReason, Is.EqualTo("operation_profile_rejected")); Assert.That(Ids(state, "city_cell_0", 1), Is.Empty); Assert.That(Ids(state, "facility_supply_0"), Is.EqualTo(new[] { selected }));
        }
        [Test] public void OwnCharacterTransferPreservesOriginalOwnerAndChangesCurrentHolder()
        {
            var state = State(); string selected = Ids(state, "hand", 1)[0];
            var node = Run(state, MoveSpec(1, new[] { selected }, Zone(state, "hand", 1).ZoneId, Zone(state, "hand", 2).ZoneId, "unordered", 1, "core.character_card_transfer"));
            var item = state.ItemZones.Items.Single(value => value.ItemId == selected);
            Assert.That(Number(node.NormalizedResult, "movedCount"), Is.EqualTo(1)); Assert.That(item.OwnerSubject.PlayerId, Is.EqualTo(1)); Assert.That(item.ControllerSubject.PlayerId, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.RuleEvents.All(ruleEvent => ruleEvent.EventType == "EffectCompleted"), Is.True); Assert.That(Ids(state, "hand", 2), Does.Contain(selected));
        }
        [Test] public void AttachedStoragePermissionsFollowCurrentHolderAndReturnMarkerToOriginalPlayer()
        {
            var state = State(); string card = Ids(state, "hand", 1)[0]; string marker = Ids(state, "supply", 1)[0];
            var declaration = new CardZoneDefinition { LocalZoneId = "stored_markers", ZoneTemplateId = "core.zone.card_cube_storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" }, CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents", VisibilityPolicyId = ItemZoneVisibility.Owner };
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, card, declaration, out string storage, out string reason), Is.True, reason);
            Run(state, MoveSpec(1, new[] { marker }, Zone(state, "supply", 1).ZoneId, storage, "unordered", 1, "core.player_marker_storage"));
            Run(state, MoveSpec(1, new[] { card }, Zone(state, "hand", 1).ZoneId, Zone(state, "hand", 2).ZoneId, "unordered", 1, "core.character_card_transfer"));
            var oldOwner = Run(state, MoveSpec(1, new[] { marker }, storage, Zone(state, "supply", 1).ZoneId, "unordered", 1, "core.player_marker_storage")); Assert.That(oldOwner.FailureReason, Is.EqualTo("transfer_permission_denied"));
            var currentHolder = Run(state, MoveSpec(2, new[] { marker }, storage, Zone(state, "supply", 1).ZoneId, "unordered", 1, "core.player_marker_storage"));
            Assert.That(Number(currentHolder.NormalizedResult, "movedCount"), Is.EqualTo(1)); Assert.That(Ids(state, "supply", 1), Does.Contain(marker)); Assert.That(state.ItemZones.Items.Single(item => item.ItemId == marker).OwnerSubject.PlayerId, Is.EqualTo(1));
        }
        [Test] public void InternalBusinessTransferReturnsOpponentMarkerToItsOwnSupply()
        {
            var state = State(); var registry = Registry(); string marker = Ids(state, "supply", 2)[0];
            var influence = state.ItemZones.Zones.First(zone => zone.TemplateId == "core.zone.influence_slots");
            registry.Register("test.marker_deployment", context =>
            {
                var request = new ItemTransferRequest { ExecutingPlayerId = 2, ItemIds = new List<string> { marker }, ExpectedSource = new ItemPositionRef { ZoneId = Zone(context.State, "supply", 2).ZoneId }, Destination = new ZoneRef { ZoneId = influence.ZoneId }, InsertionKind = "slot", DestinationSlotId = influence.LegalSlotIds[0], OperationProfileId = "core.influence_cube", RequiredCount = 1 };
                return EffectStepResult.Completed(NormalizedValue.CreateInteger(new ItemTransferService().RelocateItems(context, request).MovedCount));
            });
            registry.Register("test.marker_return", context =>
            {
                var request = new ItemTransferRequest { ExecutingPlayerId = 1, ItemIds = new List<string> { marker }, ExpectedSource = new ItemPositionRef { ZoneId = influence.ZoneId }, Destination = new ZoneRef { ZoneId = Zone(context.State, "supply", 2).ZoneId }, InsertionKind = "unordered", OperationProfileId = "core.influence_cube", RequiredCount = 1 };
                return EffectStepResult.Completed(NormalizedValue.CreateInteger(new ItemTransferService().RelocateItems(context, request).MovedCount));
            });
            Run(state, EffectSpec.Create("test.marker_deployment", playerId: 2), registry);
            var node = Run(state, EffectSpec.Create("test.marker_return", playerId: 1), registry); Assert.That(node.NormalizedResult.IntegerValue, Is.EqualTo(1)); Assert.That(Ids(state, "supply", 2), Does.Contain(marker)); Assert.That(new ItemZoneQuery().GetItemIds(state, influence.ZoneId), Is.Empty);
        }
        [Test] public void LegacyAwaitingFacilityNodeKeepsOldAnswerAndStringResult()
        {
            var state = State(); Fill(state, 1); var registry = Registry(); string selected = Ids(state, "facility_supply_0")[0]; string definition = state.ItemZones.Items.Single(item => item.ItemId == selected).DefinitionId;
            var node = Run(state, EffectSpec.Create(LuaDomainEffectTypeIds.MoveFacilityCard, Object("sourceZone", NormalizedValue.CreateString("supply"), "destinationZone", NormalizedValue.CreateString("deck_bottom")), 1), registry);
            node.DefinitionVersion = "1.0.0"; var request = Open(state); request.CandidateIds = new List<string> { definition }; request.CandidateSetId = ""; request.CandidateSetVersion = 0; request.CandidateResolutionId = "";
            state = Restore(state, registry); AnswerRequest(state, registry, definition);
            var completed = state.EffectRuntime.EffectNodes.Single(value => value.EffectId == node.EffectId); Assert.That(completed.DefinitionVersion, Is.EqualTo("v1")); Assert.That(completed.NormalizedResult.StringValue, Is.EqualTo(definition)); Assert.That(Ids(state, "facility_supply_0"), Does.Not.Contain(selected));
        }
        [Test] public void OldUnimplementedRepeatIsNotReinterpretedOnRecovery()
        {
            var state = State(); var registry = Registry(); var node = Run(state, Repeat(2, ResourceEffectSpecFactory.GainScore(1, 1), true), registry); node.DefinitionVersion = "1.0.0";
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(recovered.Succeeded, Is.False); Assert.That(recovered.Diagnostic, Does.Contain(ItemZoneStateMigrator.Unsupported)); Assert.That(state.FindPlayer(1).Score, Is.Zero);
        }
        [Test] public void MissingHostResultFieldsAreRejectedInsteadOfDefaulted()
        {
            var state = State(); Run(state, Shuffle(state)); var json = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            ((JObject)json["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"][0]).Remove("ShuffleInputItemIds");
            var recovered = HostRecoveryService.RecoverJson(json.ToString(), Registry()); Assert.That(recovered.Succeeded, Is.False); Assert.That(recovered.Diagnostic, Does.Contain("Host"));
        }
        [Test] public void BuildResumesBetweenPaymentAndTakeAndRunsRealEntryOnlyOnce()
        {
            string definitionId = FacilityCardDatabase.All.First(definition => definition.EffectId == "gain_iron_4").FacilityId;
            var state = State(entryDefinition: definitionId); var registry = Registry(); FacilityLuaCatalog.Register(registry);
            state.ContentPackHash = ExternalContentPack.Current.ContentHash; state.RuleConfiguration.ContentPackHash = state.ContentPackHash;
            var allItems = state.ItemZones.Items.Select(item => item.ItemId).ToArray(); Fill(state); string selected = Ids(state, "facility_supply_0")[0];
            var definition = FacilityCardDatabase.Get(definitionId); int gold = state.FindPlayer(1).Resources.GoldVoucher; int iron = state.FindPlayer(1).Resources.Iron;
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(FacilityBuildEffectSpecFactory.Build(1, selected, 0, "gold", false, "test.paid_entry", false));
            for (int step = 0; step < 32 && !state.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == FacilityBuildEffectExecutor.PaymentTypeId && node.Status == EffectNodeStatus.Completed); step++) Assert.That(engine.Advance(), Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - definition.GoldVoucherCost)); Assert.That(Ids(state, "facility_supply_0"), Is.EqualTo(new[] { selected }));
            state = Restore(state, registry); Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(Ids(state, "city_cell_0", 1), Is.EqualTo(new[] { selected })); Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(iron + 4));
            Assert.That(state.EffectRuntime.DispatchReceipts.Count, Is.GreaterThan(0)); var receipts = state.EffectRuntime.DispatchReceipts.Select(receipt => receipt.ReceiptId).ToArray();
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - definition.GoldVoucherCost)); Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(iron + 4));
            Assert.That(state.EffectRuntime.DispatchReceipts.Select(receipt => receipt.ReceiptId), Is.EqualTo(receipts)); Assert.That(state.ItemZones.Items.Select(item => item.ItemId), Is.EquivalentTo(allItems));
        }
        [Test] public void SecretDeckCandidatesCarrySavedDefinitionsOnlyToAuthorizedViewer()
        {
            var state = State(); var registry = Registry();
            Run(state, EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, Object("sourceZone", NormalizedValue.CreateString("deck"), "allowDecline", NormalizedValue.CreateBoolean(true)), 1), registry);
            var request = Open(state); Assert.That(request.CandidateSetId, Is.Not.Empty); Assert.That(request.CandidateSetVersion, Is.EqualTo(1)); Assert.That(request.CandidateResolutionId, Is.EqualTo(request.CandidateSetId));
            var projection = InteractionRequestProjector.ProjectForPlayer(request, 1); var definitions = Value(projection.PromptParameters, "candidateDefinitionIds");
            Assert.That(definitions.Items.Count, Is.EqualTo(request.CandidateIds.Count));
            for (int index = 0; index < request.CandidateIds.Count; index++) if (request.CandidateIds[index] != "choice.skip") Assert.That(FacilityCardDatabase.Get(definitions.Items[index].StringValue), Is.Not.Null);
            Assert.That(InteractionRequestProjector.ProjectForPlayer(request, 2).PromptParameters.Properties, Is.Empty);
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).Interactions, Is.Empty);
            state = Restore(state, registry); Assert.That(Open(state).CandidateIds, Is.EqualTo(request.CandidateIds)); Assert.That(Value(Open(state).PromptParameters, "candidateDefinitionIds").Items.Select(value => value.StringValue), Is.EqualTo(definitions.Items.Select(value => value.StringValue)));
        }
        [Test] public void ChangedSourceRefreshesBuildChoiceWithoutPaymentOrFallback()
        {
            // 刷新请求的正例需保留另一真实供应候选；只有一个时移走后应合法结束。
            var state = State(); Fill(state, 2); var registry = Registry(); registry.ContentPackHash = state.ContentPackHash; string selected = Ids(state, "facility_supply_0")[0]; int gold = state.FindPlayer(1).Resources.GoldVoucher;
            Run(state, EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, Object("sourceZone", NormalizedValue.CreateString("supply"), "allowDecline", NormalizedValue.CreateBoolean(true)), 1), registry);
            string requestId = Open(state).GetStableInteractionId(); var engine = new EffectTreeExecutor(state, registry);
            var input = Open(state);
            Assert.That(engine.TryPrepareEffectBuildPayment(1, input.OwnerEffectId, requestId, input.CandidateSetId, input.CandidateSetVersion, selected,
                Object("expectedSourceZoneId", NormalizedValue.CreateString(Zone(state, "facility_supply_0").ZoneId), "paymentMode", NormalizedValue.CreateString("gold")), state.EffectRuntime.StateRevision, "R2正式建设付款预览", out string reason), Is.True, reason);
            string paymentId = Open(state).OwnerEffectId;
            // 隔离竞态：经过真实转移入口移走已选实例，再由正式付款与取消入口复验。
            Run(state, MoveSpec(1, new[] { selected }, Zone(state, "facility_supply_0").ZoneId, Zone(state, "retired").ZoneId, "unordered", 1), registry);
            Assert.That(ResourcePaymentPreparationService.CanConfirm(state, engine.GetNode(paymentId)), Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold)); Assert.That(state.Map.Facilities, Is.Empty); Valid(state);
            artifacts.WriteLabeledText("R2选中来源已变化的正式付款", JsonConvert.SerializeObject(state));
            Assert.That(engine.TryCancelPreparedEffectBuildPayment(1, paymentId, state.EffectRuntime.StateRevision, "R2返回真实建设候选", out reason), Is.True, reason);
            Assert.That(Open(state).GetStableInteractionId(), Is.Not.EqualTo(requestId)); Assert.That(Open(state).CandidateIds, Does.Not.Contain(selected));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold)); Assert.That(state.Map.Facilities, Is.Empty); Valid(state);
        }
        [TestCase("Effect.MoveItem({apiVersion=1})")]
        [TestCase("Effect.ShuffleDeck({apiVersion=1})")]
        [TestCase("Effect.Repeat({apiVersion=1,count=1})")]
        public void IncompletePrimitiveLogsOnceAndCannotUnlockConditionReward(string constructor)
        {
            string source = "return function(ctx) return { " + constructor + " } end";
            var definition = new LuaScriptDefinition("content.missing", "ability.missing", "handler.missing", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(definition, new LuaInvocationContext("event.missing", "event.test", "1", 0, "1.0.0", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic); Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            var state = State(); var registry = Registry(); Run(state, EffectSpec.Condition(specs, new[] { ResourceEffectSpecFactory.GainScore(1, 10) }), registry);
            Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(state.Logs.Count(log => log.Message.Contains("reasonCode=incomplete_effect_skipped")), Is.EqualTo(1));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.Logs.Count(log => log.Message.Contains("reasonCode=incomplete_effect_skipped")), Is.EqualTo(1));
        }
        [TestCase("count=4294967296")][TestCase("count=1.5")]
        public void RepeatCannotTruncateAnUnboundedOrFractionalCount(string count)
        {
            string source = "return function(ctx) return {Effect.Repeat({apiVersion=1," + count + ",effects={}})} end";
            var definition = new LuaScriptDefinition("content.count", "ability.count", "handler.count", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            Assert.That(new MoonSharpLuaRuntimeHost().Invoke(definition, new LuaInvocationContext("event.count", "event.test", "1", 0, "1.0.0", 2, 0, 0)).IsSuccess, Is.False);
        }
        [TestCase("apiVersion=2")][TestCase("requiredCount=-1")][TestCase("insertion={kind='replace'}")][TestCase("seed=123")]
        public void LuaRejectsChangedSchemaOrClientRandomness(string change)
        {
            string source = "return function(ctx) return { Effect.MoveItem({apiVersion=1,player=ctx.playerId,itemRefs={},expectedSource={zoneId='source'},destination={zoneId='target'},insertion={kind='unordered'},operationProfileId='core.character_card_transfer'," + change + "}) } end";
            var definition = new LuaScriptDefinition("content.transfer", "ability.transfer", "handler.transfer", "1.0.0", source, LuaContentHasher.ComputeSha256(source)); Assert.That(new MoonSharpLuaRuntimeHost().Invoke(definition, new LuaInvocationContext("event.transfer", "event.test", "1", 0, "1.0.0", 2, 0, 0)).IsSuccess, Is.False);
        }
    }
}
