using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class CharacterLifecycleTests
    {
        private static IsolatedTestArtifacts artifacts;
        private static GameState observed;
        private static ExternalContentPack frozenPack;
        [SetUp] public void LoadCoreDefinitions()
        {
            observed = null; artifacts = IsolatedTestArtifacts.BeginCase();
            var pack = frozenPack = ExternalContentPack.Current;
            artifacts.WriteLabeledText("只读安装角色输入", JsonConvert.SerializeObject(new { pack.RootPath, pack.ContentHash, copiedContent = false }));
            if (!CharacterCardDatabase.IsInitialized) CharacterCardDatabase.Initialize(pack.CreateCharacters());
        }
        [TearDown] public void FinishEvidence()
        {
            if (observed != null) artifacts.WriteLabeledText("当前真实状态", JsonConvert.SerializeObject(observed));
            artifacts.Finish();
        }
        private static GameState State(int operators = 4, int players = 2, int companyMarkers = 0)
        {
            var map = new GameMapDefinition { MapId = "test.character_map", MinPlayers = 2, MaxPlayers = 4,
                Regions = new List<MapRegionDefinition> { new MapRegionDefinition { RegionId = "harbor_region" } },
                Locations = new List<MapLocationDefinition> { new MapLocationDefinition { LocationId = "harbor", RegionId = "harbor_region", CanDockCity = true, InfluenceSlotCount = 2 } } };
            var source = new GameState { GameId = "game.character", ContentPackHash = "content.character", MapId = map.MapId,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = players, ContentPackHash = "content.character" },
                Players = Enumerable.Range(1, players).Select(id => new PlayerState { PlayerId = id, Name = "玩家" + id, Resources = new ResourceSet { GoldVoucher = id == 1 ? 8 : 0 } }).ToList() };
            if (companyMarkers > 0)
            {
                source.ContentDefinitions = new GameContentDefinitions { Characters = frozenPack.CreateCharacters().ToList(), Facilities = frozenPack.CreateFacilities().ToList() };
                source.ContentDefinitions.Components = frozenPack.Definitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition {
                    DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }).ToList();
            }
            source.RandomRuntime = GameplayRandomRuntime.Create(source.GameId);
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, new[] {
                new ItemRecipeEntry { EntryId = "initial_characters", DefinitionId = "texas", TypeId = "core.basic_character_card", Quantity = 3, PerPlayer = true, InitialZoneRole = "hand" },
                new ItemRecipeEntry { EntryId = "generated_character", DefinitionId = "texas", TypeId = "core.generated_character_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" },
                new ItemRecipeEntry { EntryId = "operator_pool", DefinitionId = "texas", TypeId = "core.operator_card", Quantity = operators, InitialZoneRole = "operator_deck" },
                new ItemRecipeEntry { EntryId = "supply", DefinitionId = "component.marker", TypeId = "core.player_marker", Quantity = 6, PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "stored_facilities", DefinitionId = "facility.fixture", TypeId = "core.facility_card", Quantity = 2, InitialZoneRole = "facility_deck" },
                new ItemRecipeEntry { EntryId = "company_components", DefinitionId = "infected.component.company_marker", TypeId = "infected.company_marker", Quantity = companyMarkers, InitialZoneRole = "company_markers" } });
            state.Round = 1; state.ActionRound = 1; state.Phase = GamePhase.ActionRound1; state.CurrentPlayerId = 1; state.StartPlayerId = 1;
            return observed = state;
        }
        private static EffectRegistry Registry(string strategyType = "normal", string tacticType = "normal", Func<int, int> random = null, Func<EffectEventHandlerContext, IList<EffectSpec>> tactic = null, bool settlementSource = false)
        {
            var registry = new EffectRegistry(); ResourceEffectExecutor.Register(registry); ItemTransferEffectExecutor.Register(registry); DeckShuffleEffectExecutor.Register(registry, random); CharacterCoverEffectExecutor.Register(registry); CharacterLifecycleService.Register(registry);
            var definition = CharacterCardDatabase.Get("texas");
            foreach (string mode in new[] { "strategy", "tactic" })
            {
                string ability = mode == "strategy" ? definition.StrategyAbilityId : definition.TacticAbilityId;
                registry.CharacterActivationSubscriptions[ability] = "test." + ability;
                registry.CharacterAbilityContracts[ability] = new CharacterModeContract { Mode = mode, AbilityId = ability, SkillType = mode == "strategy" ? strategyType : tacticType,
                    SettlementActionId = mode == "tactic" && settlementSource ? CharacterSettlementOptionQuery.DoubleUseActionId : "", TitleKey = "character.double_use.title", DescriptionKey = "character.double_use.description" };
                bool isTactic = mode == "tactic";
                registry.RegisterEventHandler(new EffectEventHandlerRegistration("test." + ability, CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType, ability, "test." + ability,
                    context => isTactic && tactic != null ? tactic(context) : new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, isTactic ? 3 : 2) }, EffectHandlerRole.Primary));
            }
            registry.Register("test.recycle", context => CharacterLifecycleService.Recycle(context, 1, true, context.State.ItemZones.Items.Where(item => item.ZoneId == FindZone(context.State, "covered", 1).ZoneId).Select(item => item.ItemId).ToArray()));
            registry.Register("test.cleanup_event", context => EffectStepResult.Completed().AddEvent(new EffectEventRequest { EventType = "PlayerCleanupStarted", PlayerId = 1 }));
            registry.Register("test.granted_cover", context => CharacterLifecycleService.Cover(context, Text(context.Node.NormalizedArguments, "cardId"), true, acceptedTypeId: "core.generated_character_card", reusable: true));
            return registry;
        }
        private static List<string> Ids(GameState state, string local, int player = -1) => new ItemZoneQuery().GetItemIds(state, FindZone(state, local, player).ZoneId).ToList();
        private static EffectNodeRuntimeState Run(GameState state, EffectRegistry registry, EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(spec); var report = engine.RunUntilQuiescent();
            Assert.That(report.Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state); return engine.GetNode(id);
        }
        private static void Valid(GameState state) { Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason); Assert.That(CharacterLifecycleService.TryValidate(state, out reason), Is.True, reason); }
        private static InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.Last(request => request.Status == "open");
        private static void AnswerRequest(GameState state, EffectRegistry registry, string value = null, bool decline = false)
        {
            var request = Open(state); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision, decline ? NormalizedValue.CreateBoolean(false) : NormalizedValue.CreateString(value), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state);
        }
        private static void Cover(GameState state, EffectRegistry registry, string card)
        {
            Run(state, registry, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); AnswerRequest(state, registry, card);
        }
        private static GameState Restore(GameState state, EffectRegistry registry)
        {
            var result = artifacts.Recover(state, "R2角色恢复", registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic); Valid(result.State); return observed = result.State;
        }
        private static CharacterUseRuntimeState Use(GameState state, EffectRegistry registry, string card, string mode = "strategy", string order = "")
        { Run(state, registry, CharacterLifecycleService.CreateUseSpec(1, card, mode, order)); return state.CharacterUses.Last(); }

        [TestCase("normal", "discard")][TestCase("persistent", "persistent")][TestCase("one_shot", "retired")]
        public void ModeLifecycleUsesOneActualInstance(string skillType, string destination)
        {
            var state = State(); var registry = Registry(tacticType: skillType); var all = state.ItemZones.Items.Select(item => item.ItemId).ToArray(); string card = Ids(state, "hand", 1)[0]; Cover(state, registry, card);
            var use = Use(state, registry, card, "tactic"); Assert.That(use.Finished, Is.True); Assert.That(Ids(state, destination, destination == "retired" ? -1 : 1), Does.Contain(card));
            Assert.That(state.ItemZones.Items.Select(item => item.ItemId), Is.EquivalentTo(all)); Assert.That(Ids(state, "covered", 1), Is.Empty); Assert.That(use.CompletedModes, Is.EqualTo(new[] { "tactic" }));
        }
        [Test] public void NormalModeDoesNotInheritOtherModesOneShotContract()
        {
            var state = State(); var registry = Registry(tacticType: "one_shot"); string card = Ids(state, "hand", 1)[0]; Cover(state, registry, card); Use(state, registry, card);
            Assert.That(Ids(state, "discard", 1), Does.Contain(card)); Assert.That(Ids(state, "retired"), Is.Empty);
        }
        [TestCase("persistent", "normal", "persistent")][TestCase("normal", "persistent", "persistent")][TestCase("normal", "one_shot", "retired")][TestCase("persistent", "one_shot", "retired")][TestCase("one_shot", "persistent", "retired")]
        public void DoubleUseRecoversAfterFirstModeWithoutMovingCard(string strategyType, string tacticType, string destination)
        {
            var state = State(); var registry = Registry(strategyType, tacticType); string card = Ids(state, "hand", 1)[0]; Cover(state, registry, card);
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(CharacterLifecycleService.CreateUseSpec(1, card, "both", "strategy-first"));
            for (int step = 0; step < 100 && (state.CharacterUses.Count == 0 || state.CharacterUses[0].CompletedModes.Count == 0); step++) Assert.That(engine.Advance(), Is.True);
            Assert.That(state.CharacterUses[0].CompletedModes, Is.EqualTo(new[] { "strategy" })); Assert.That(Ids(state, "covered", 1), Does.Contain(card));
            state = Restore(state, registry); Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(5)); Assert.That(Ids(state, destination, destination == "retired" ? -1 : 1), Does.Contain(card)); Assert.That(state.CharacterUses[0].CompletedModes.Count, Is.EqualTo(2)); Valid(state);
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(5));
        }
        [Test] public void AdditionalCoverBlocksOnlyNewInstanceUntilNextActionRound()
        {
            var state = State(); var registry = Registry(); var cards = Ids(state, "hand", 1); Cover(state, registry, cards[0]);
            Run(state, registry, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, Object("apiVersion", NormalizedValue.CreateInteger(2), "player", NormalizedValue.CreateString("1"), "cardInstanceId", NormalizedValue.CreateString(cards[1]), "coverMode", NormalizedValue.CreateString("additional"), "blockedActionRoundSequence", NormalizedValue.CreateInteger(1)), 1));
            Assert.That(Ids(state, "covered", 1), Is.EquivalentTo(cards.Take(2))); Assert.That(state.CharacterCoverSlots.Count, Is.EqualTo(2));
            Assert.That(CharacterLifecycleService.GetAvailability(state, 1, cards[0]).CanUse, Is.True); Assert.That(CharacterLifecycleService.GetAvailability(state, 1, cards[1]).DisabledReasonCode, Is.EqualTo("character_instance_blocked"));
            state = Restore(state, registry); state.ActionRound = 2; state.Phase = GamePhase.ActionRound2;
            Assert.That(CharacterLifecycleService.GetAvailability(state, 1, cards[1]).CanUse, Is.True); Assert.That(CharacterLifecycleService.GetActionRoundSequence(state), Is.EqualTo(2));
        }
        [Test] public void GeneratedCharacterHasHandAndUseEligibility()
        {
            var state = State(); var registry = Registry(); string card = state.ItemZones.Items.First(item => item.TypeId == "core.generated_character_card" && item.ControllerSubject.PlayerId == 1).ItemId;
            Assert.That(state.FindPlayer(1).HandCardIds, Does.Contain(card)); Cover(state, registry, card); Assert.That(CharacterLifecycleService.GetAvailability(state, 1, card).CanUse, Is.True); Use(state, registry, card); Assert.That(Ids(state, "discard", 1), Does.Contain(card));
        }
        [Test] public void RecycleIsIdempotentAndLeavesPersistentCards()
        {
            var state = State(); var registry = Registry(tacticType: "persistent"); var cards = Ids(state, "hand", 1); Cover(state, registry, cards[0]); Use(state, registry, cards[0], "tactic"); Cover(state, registry, cards[1]); Use(state, registry, cards[1]); Cover(state, registry, cards[2]);
            var first = Run(state, registry, EffectSpec.Create("test.recycle", playerId: 1)); var second = Run(state, registry, EffectSpec.Create("test.recycle", playerId: 1));
            Assert.That(Number(first.NormalizedResult, "movedCount"), Is.EqualTo(2)); Assert.That(Number(second.NormalizedResult, "movedCount"), Is.Zero); Assert.That(Ids(state, "persistent", 1), Is.EqualTo(new[] { cards[0] }));
        }
        [Test] public void TransferDuringModeKeepsCardInNewHoldersHandAfterOldFinish()
        {
            var registry = Registry(tactic: context => new List<EffectSpec> { EffectSpec.Create(CharacterLifecycleService.TransferTypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "cardInstanceId", NormalizedValue.CreateString(Value(context.OwnerNode.NormalizedArguments, "cardInstanceId").ReferenceId), "recipientPlayer", NormalizedValue.CreateString("2")), 1) });
            var state = State(); string card = Ids(state, "hand", 1)[0]; Cover(state, registry, card); var use = Use(state, registry, card, "tactic");
            Assert.That(Ids(state, "hand", 2), Does.Contain(card)); Assert.That(use.ExecutingPlayerId, Is.EqualTo(1)); Assert.That(state.ItemZones.Items.Single(item => item.ItemId == card).ControllerSubject.PlayerId, Is.EqualTo(2));
            Assert.That(state.ItemZones.Items.Single(item => item.ItemId == card).OwnerSubject.PlayerId, Is.EqualTo(1)); Assert.That(Ids(state, "discard", 1), Is.Empty); Assert.That(state.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == OperatorDispatchFlow.TypeId), Is.False);
            var spectator = GameStateViewProjector.Project(state, new GameStateViewer { Role = GameStateViewerRole.Spectator });
            var moved = spectator.Events.Single(value => value.EventType == "character.transferred"); Assert.That(moved.Payload.ToDeterministicString(), Does.Not.Contain(card)); Assert.That(moved.HostOnlyPayload.Kind, Is.EqualTo(NormalizedValueKind.Null));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).ItemZones.Items.Any(item => item.ItemId == card), Is.True);
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(Ids(state, "hand", 2), Does.Contain(card));
        }
        [TestCase(2)][TestCase(3)] public void DispatchAcceptsActualTopCandidatesAndRecoversBeforeReturn(int count)
        {
            int randomCalls = 0; var registry = Registry(random: maximum => { randomCalls++; return 0; }); var state = State(); var top = new ItemZoneQuery().GetTopItemIds(state, FindZone(state, "operator_deck").ZoneId, count); var all = state.ItemZones.Items.Select(item => item.ItemId).ToArray();
            Run(state, registry, OperatorDispatchFlow.CreateSpec(1, count)); Assert.That(Open(state).CandidateIds, Is.EquivalentTo(top));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).Interactions, Is.Empty); Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).ItemZones.Items.Any(item => top.Contains(item.ItemId)), Is.False);
            var savedCandidates = Open(state).CandidateIds.ToArray(); state = Restore(state, registry); Assert.That(Open(state).CandidateIds, Is.EqualTo(savedCandidates)); string selected = top[1]; AnswerRequest(state, registry, selected);
            Assert.That(Ids(state, "hand", 1), Does.Contain(selected)); Assert.That(Open(state).InteractionTypeId, Is.EqualTo("operator.dispatch.return")); var deckOrder = Ids(state, "operator_deck").ToArray(); int callsBefore = randomCalls;
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(Ids(state, "operator_deck"), Is.EqualTo(deckOrder)); Assert.That(randomCalls, Is.EqualTo(callsBefore));
            string outgoing = Ids(state, "hand", 1).First(id => id != selected); AnswerRequest(state, registry, outgoing); Assert.That(Ids(state, "retired"), Does.Contain(outgoing)); Assert.That(state.ItemZones.Items.Select(item => item.ItemId), Is.EquivalentTo(all));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).Events.Any(value => value.EventType == "OperatorDispatchReturnShown"), Is.True);
        }
        [TestCase(1, 3)][TestCase(2, 3)] public void ShortDispatchDeckNeverInventsCandidates(int actual, int requested)
        {
            var state = State(actual); var registry = Registry(); Run(state, registry, OperatorDispatchFlow.CreateSpec(1, requested)); Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(actual)); AnswerRequest(state, registry, decline: true); Assert.That(Ids(state, "operator_deck").Count, Is.EqualTo(actual)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
        }
        [Test] public void EmptyDispatchDeckDoesNotAwardDeclineScore()
        {
            var state = State(0); var registry = Registry(); var node = Run(state, registry, OperatorDispatchFlow.CreateSpec(1, 3)); Assert.That(Text(node.NormalizedResult, "reasonCode"), Is.EqualTo("empty_deck")); Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(state.EffectRuntime.InteractionRequests.Any(request => request.Status == "open"), Is.False);
        }
        [Test] public void DispatchDeclineRestoresDeckAndAwardsScoreOnlyOnce()
        {
            int calls = 0; var registry = Registry(random: maximum => { calls++; return 0; }); var state = State(); var original = Ids(state, "operator_deck").ToArray(); Run(state, registry, OperatorDispatchFlow.CreateSpec(1, 3)); AnswerRequest(state, registry, decline: true);
            Assert.That(Ids(state, "operator_deck"), Is.EquivalentTo(original)); Assert.That(calls, Is.EqualTo(3)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(calls, Is.EqualTo(3)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
        }
        [Test] public void DirectReplacementDoesNotDrawShuffleOrAwardDecline()
        {
            int calls = 0; var registry = Registry(random: maximum => { calls++; return 0; }); var state = State(); string incoming = Ids(state, "operator_deck")[2], outgoing = Ids(state, "hand", 1)[1];
            Run(state, registry, EffectSpec.Create(OperatorDispatchFlow.ReplaceTypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1")), 1)); AnswerRequest(state, registry, incoming); state = Restore(state, registry); AnswerRequest(state, registry, outgoing);
            Assert.That(Ids(state, "hand", 1), Does.Contain(incoming)); Assert.That(Ids(state, "retired"), Does.Contain(outgoing)); Assert.That(calls, Is.Zero); Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(state.ItemZones.Zones.Any(zone => zone.OwnerEffectId.Length > 0), Is.False);
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).Events.Any(value => value.EventType == "OperatorDispatchReturnShown" && Text(value.Payload, "itemId") == outgoing), Is.True);
        }
        [Test] public void CurrentUseInsertionConsumesOnceAndInvalidatesOnlyItsModeRequest()
        {
            var registry = Registry(tacticType: "persistent", settlementSource: true); var state = State(); var cards = Ids(state, "hand", 1); Cover(state, registry, cards[0]); Use(state, registry, cards[0], "tactic"); Cover(state, registry, cards[1]);
            // 为正式插入命令建立实际主行动窗口；不伪造CurrentOperationContext。
            var chainBuilder = new RoundMainChainBuilder(); var order = new List<int> { 1, 2 };
            var chain = chainBuilder.Build(state.GameId, chainBuilder.CreateRoundExecutionId(state.GameId, 1, order), 1, order, 1);
            var window = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && n.PlayerId == 1 && n.ActionRound == 1);
            foreach (var node in chain.Nodes.Where(n => n.MainlineIndex < window.MainlineIndex)) node.Status = EffectNodeStatus.Completed;
            var runtime = state.EffectRuntime; runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = 1;
            runtime.CurrentRoundStartPlayerId = 1; runtime.PlayerOrderSnapshot = order; runtime.FirstMainNodeId = chain.FirstNodeId;
            runtime.MainNodes.AddRange(chain.Nodes); runtime.ActiveMainNodeId = window.NodeId; window.Status = EffectNodeStatus.Ready;
            Assert.That(new RoundExecutionService(registry).Advance(state).IsValid, Is.True);
            var session = new GameSession(state); session.RegisterHandler(new UseCharacterCardCommandHandler(new CharacterCardService(), registry)); session.RegisterHandler(new InvokeSettlementOptionCommandHandler(registry)); session.RegisterHandler(new AnswerInteractionCommandHandler(registry));
            var command = new GameCommand { Kind = GameCommandKind.UseCharacterCard, PlayerId = 1, TargetId = cards[1] }; var beginResult = session.Submit(command);
            artifacts.WriteLabeledText("R2原角色使用命令结果", JsonConvert.SerializeObject(beginResult)); Assert.That(beginResult.Succeeded, Is.True);
            var current = state.CharacterUses.Last(); var old = Open(state); var options = CharacterSettlementOptionQuery.GetOptions(state, 1, current.UseId); Assert.That(options.Count, Is.EqualTo(1)); Assert.That(CharacterSettlementOptionQuery.GetOptions(state, 2, current.UseId), Is.Empty);
            int actions = state.FindPlayer(1).RemainingMainActionsThisTurn; var invocation = EffectInteractionCommands.InvokeSettlementOption(1, options[0].OptionId, current.UseId, options[0].StateRevision);
            Assert.That(invocation.Parameters.ContainsKey("useId"), Is.True); Assert.That(invocation.Parameters.ContainsKey("currentUseId"), Is.False); Assert.That(GameCommandDto.FromCommand(invocation).ToCommand().Parameters, Is.EquivalentTo(invocation.Parameters));
            var invokeResult = session.Submit(invocation); artifacts.WriteLabeledText("R2原插入命令结果", JsonConvert.SerializeObject(invokeResult));
            Assert.That(invokeResult.Succeeded, Is.True); Assert.That(old.Status, Is.EqualTo("invalidated")); Assert.That(current.SourceConsumed, Is.True); Assert.That(current.DoubleUseGranted, Is.True); Assert.That(Open(state).InteractionTypeId, Is.EqualTo(CharacterLifecycleService.OrderInteractionTypeId));
            int count = state.EffectRuntime.EffectNodes.Count; Assert.That(session.Submit(invocation).Succeeded, Is.True); invocation.CommandId = "another_click"; Assert.That(session.Submit(invocation).Succeeded, Is.True); Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(count));
            Assert.That(state.CharacterUses.Last().UseId, Is.EqualTo(current.UseId)); Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(actions)); Assert.That(Ids(state, "discard", 1), Does.Contain(cards[0]));
            state = Restore(state, registry); AnswerRequest(state, registry, "tactic-first"); Assert.That(state.CharacterUses.Last().CompletedModes, Is.EqualTo(new[] { "tactic", "strategy" })); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(8));
        }
        [Test] public void SettlementProjectionIsReadOnlyAndCannotBypassWrongPlayerOrRevision()
        {
            var registry = Registry(tacticType: "persistent", settlementSource: true); var state = State(); var cards = Ids(state, "hand", 1); Cover(state, registry, cards[0]); Use(state, registry, cards[0], "tactic"); Cover(state, registry, cards[1]); Use(state, registry, cards[1], "");
            var before = GameStateCloneService.DeepClone(state); var option = CharacterSettlementOptionQuery.GetOptions(state, 1, state.CharacterUses.Last().UseId).Single(); var projected = GameStateViewProjector.ProjectForPlayer(state, 1);
            Assert.That(GameStateCloneService.AreEquivalent(state, before), Is.True); Assert.That(SettlementOptionView.FromOption(projected.CharacterSettlementOptions.Single()).SourceItemId, Is.EqualTo(cards[0]));
            var session = new GameSession(state); session.RegisterHandler(new InvokeSettlementOptionCommandHandler(registry));
            Assert.That(session.Submit(EffectInteractionCommands.InvokeSettlementOption(2, option.OptionId, option.TargetUseId, option.StateRevision)).Succeeded, Is.False);
            Assert.That(session.Submit(EffectInteractionCommands.InvokeSettlementOption(1, option.OptionId, option.TargetUseId, option.StateRevision - 1)).Succeeded, Is.False); Assert.That(Ids(state, "persistent", 1), Does.Contain(cards[0]));
        }
        [Test] public void BothModesPayTheirOwnCostsAndFailedPaymentCannotRunReward()
        {
            var registry = Registry(tactic: context => new List<EffectSpec> { EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(1, ResourceType.GoldVoucher, 20) }, new[] { ResourceEffectSpecFactory.GainScore(1, 100) }) });
            var state = State(); string card = Ids(state, "hand", 1)[0]; Cover(state, registry, card); Use(state, registry, card, "both", "strategy-first");
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(8)); Assert.That(state.CharacterUses.Single().CompletedModes.Count, Is.EqualTo(2)); Assert.That(state.EffectRuntime.EffectNodes.Any(node => node.FailureReason.Length > 0), Is.True);
        }
        [Test] public void StorageKeepsActualOwnersCountsAndHasNoInitialDepositCapacityLimit()
        {
            var state = State(); var registry = Registry(tacticType: "persistent"); string card = Ids(state, "hand", 1)[0];
            var declaration = new CardZoneDefinition { LocalZoneId = "stored_cubes", ZoneTemplateId = "core.zone.card_cube_storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered,
                EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.return_contents_before_exit", VisibilityPolicyId = ItemZoneVisibility.Public };
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, card, declaration, out string storage, out string reason), Is.True, reason);
            var markers = Ids(state, "supply", 1).Take(5).ToArray(); Cover(state, registry, card); Use(state, registry, card, "tactic");
            Run(state, registry, MoveSpec(1, markers, FindZone(state, "supply", 1).ZoneId, storage, "unordered", 5, "core.cube_storage"));
            state = Restore(state, registry); Assert.That(new ItemZoneQuery().GetItemIds(state, storage).Count, Is.EqualTo(5)); Assert.That(markers.All(id => state.ItemZones.Items.Single(item => item.ItemId == id).OwnerSubject.PlayerId == 1), Is.True);
            registry.Register("test.leave_source", context => { string error = CharacterLifecycleService.MoveCard(context, card, FindZone(context.State, "discard", 1).ZoneId); return error.Length == 0 ? EffectStepResult.Completed() : EffectStepResult.Failed(error); });
            Run(state, registry, EffectSpec.Create("test.leave_source", playerId: 1)); Assert.That(new ItemZoneQuery().GetItemIds(state, storage), Is.Empty); Assert.That(Ids(state, "supply", 1).Count, Is.EqualTo(6)); Assert.That(CharacterLifecycleService.IsSubscriptionActive(state, card, CharacterCardDatabase.Get("texas").TacticAbilityId), Is.False);
        }
        [Test] public void SourceRetirementDoesNotCancelAlreadyRegisteredCleanupTask()
        {
            var state = State(); var registry = Registry(); string ability = CharacterCardDatabase.Get("texas").StrategyAbilityId;
            TimingHandlerRegistry.RegisterPlayerCleanupAfterResolveOnce(registry, CharacterAbilityEffectExecutor.ActivationEffectTypeId, ability, "1.0.0", "hash.cleanup", context => new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(1, 7) });
            string card = Ids(state, "hand", 1)[0]; Cover(state, registry, card); Use(state, registry, card); Run(state, registry, MoveSpec(1, new[] { card }, FindZone(state, "discard", 1).ZoneId, FindZone(state, "retired").ZoneId, "unordered", 1, "core.character_card_transfer"));
            Assert.That(state.EffectRuntime.EffectNodes.SelectMany(node => node.TimingBindings).Count(binding => binding.Status == "registered"), Is.EqualTo(1)); state = Restore(state, registry);
            Run(state, registry, EffectSpec.Create("test.cleanup_event", playerId: 1)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(9)); state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(9));
        }
        [TestCase("Flow.DispatchOperator({apiVersion=1,player=ctx.playerId,drawCount=3,excludedReturnItemIds={}})", OperatorDispatchFlow.TypeId)]
        [TestCase("Flow.ReplaceHandWithOperator({apiVersion=1,player=ctx.playerId})", OperatorDispatchFlow.ReplaceTypeId)]
        [TestCase("Effect.SetCharacterDoubleUseRule({apiVersion=1,player=ctx.playerId,scope='action_round',expiresActionRoundSequence=1})", LuaDomainEffectTypeIds.SetCharacterDoubleUseRule)]
        [TestCase("Effect.TransferCharacterCard({apiVersion=1,player=ctx.playerId,cardInstanceId='card',recipientPlayer='2'})", CharacterLifecycleService.TransferTypeId)]
        [TestCase("Effect.CoverCharacterCard({apiVersion=2,player=ctx.playerId,cardInstanceId='card',coverMode='additional',blockedActionRoundSequence=1})", CharacterCoverEffectExecutor.EffectTypeId)]
        public void LuaConstructorsCompileToUniqueProductionRegistrations(string expression, string type)
        {
            string source = "return function(ctx) return {" + expression + "} end"; var script = new LuaScriptDefinition("content.character", "ability.character", "handler.character", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(script, new LuaInvocationContext("event.character", "test.event", "1", 0, "1.0.0", 2, 0, 0)); Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic); Assert.That(specs.Single().EffectTypeId, Is.EqualTo(type));
            var registry = Registry(); CharacterLifecycleService.Register(registry); Assert.That(registry.Get(type).DefinitionVersion,
                Is.EqualTo(type == CharacterLifecycleService.TransferTypeId || type == LuaDomainEffectTypeIds.SetCharacterDoubleUseRule ? CharacterLifecycleService.DefinitionVersion : "v1"));
        }
        [TestCase("drawCount=4")][TestCase("seed=11")][TestCase("player='2'")]
        public void DispatchCannotAcceptWrongCountRandomnessOrExecutingPlayer(string extra)
        {
            var state = State(); var registry = Registry(); var spec = OperatorDispatchFlow.CreateSpec(1, 3);
            if (extra.StartsWith("drawCount")) Value(spec.NormalizedArguments, "drawCount").IntegerValue = 4;
            else if (extra.StartsWith("player")) Value(spec.NormalizedArguments, "player").StringValue = "2";
            else spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "seed", Value = NormalizedValue.CreateInteger(11) });
            Assert.That(Run(state, registry, spec).Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(Ids(state, "operator_deck").Count, Is.EqualTo(4));
        }

        [Test] public void PersistentObserversTriggerOncePerActualInstanceAndStopOnExit()
        {
            var state = State(); var registry = Registry(tacticType: "persistent"); string ability = CharacterCardDatabase.Get("texas").TacticAbilityId; var cards = Ids(state, "hand", 1);
            CharacterLifecycleService.RegisterPersistentObserver(registry, ability, "PlayerCleanupStarted", "1.0.0", "hash.persistent", context => {
                string source = Value(context.Event.HostOnlyPayload, "cardInstanceRef").ReferenceId;
                Assert.That(CharacterLifecycleService.IsSubscriptionActive(context.State, source, ability), Is.True);
                return new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 7) };
            });
            Cover(state, registry, cards[0]); Use(state, registry, cards[0], "tactic"); Cover(state, registry, cards[1]); Use(state, registry, cards[1], "tactic");
            state = Restore(state, registry); Run(state, registry, EffectSpec.Create("test.cleanup_event", playerId: 1)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(20));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(20));
            Run(state, registry, MoveSpec(1, new[] { cards[0] }, FindZone(state, "persistent", 1).ZoneId, FindZone(state, "discard", 1).ZoneId, "unordered", 1, "core.character_card_transfer"));
            state.Round = 2; Run(state, registry, EffectSpec.Create("test.cleanup_event", playerId: 1)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(27));
        }
        [Test] public void FacilityStorageRestoresInstancesAndReturnsThemBeforeCarrierExit()
        {
            var state = State(); var registry = Registry(tacticType: "persistent"); string card = Ids(state, "hand", 1)[0]; var facilities = Ids(state, "facility_deck").ToArray();
            var declaration = new CardZoneDefinition { LocalZoneId = "stored_facilities", ZoneTemplateId = "core.zone.card_facility_storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Card }, AcceptedTypeIds = new List<string> { "core.facility_card" }, CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered,
                EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.return_contents_before_exit", VisibilityPolicyId = ItemZoneVisibility.Public };
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, card, declaration, out string storage, out string reason), Is.True, reason);
            Cover(state, registry, card); Use(state, registry, card, "tactic"); Run(state, registry, MoveSpec(1, facilities, FindZone(state, "facility_deck").ZoneId, storage, "unordered", 2, "core.facility_card_transfer"));
            state = Restore(state, registry); Assert.That(new ItemZoneQuery().GetItemIds(state, storage), Is.EquivalentTo(facilities));
            registry.Register("test.return_carrier", context => { string error = CharacterLifecycleService.MoveCard(context, card, FindZone(context.State, "retired").ZoneId); return error.Length == 0 ? EffectStepResult.Completed() : EffectStepResult.Failed(error); });
            Run(state, registry, EffectSpec.Create("test.return_carrier", playerId: 1)); Assert.That(Ids(state, "facility_deck"), Is.EquivalentTo(facilities)); Assert.That(new ItemZoneQuery().GetItemIds(state, storage), Is.Empty);
        }
        [Test] public void DispatchRecoversSelectedHandCardBeforeUnselectedCandidatesReturn()
        {
            int calls = 0; var state = State(); var registry = Registry(random: maximum => { calls++; return 0; }); Run(state, registry, OperatorDispatchFlow.CreateSpec(1, 3)); string selected = Open(state).CandidateIds[1];
            var request = Open(state); var engine = new EffectTreeExecutor(state, registry); Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString(selected), out string reason), Is.True, reason);
            for (int step = 0; step < 100 && !Ids(state, "hand", 1).Contains(selected); step++) Assert.That(engine.Advance(), Is.True);
            Assert.That(calls, Is.Zero); Assert.That(Ids(state, "operator_deck").Count, Is.EqualTo(1)); state = Restore(state, registry); Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(Ids(state, "hand", 1).Count(id => id == selected), Is.EqualTo(1)); Assert.That(Ids(state, "operator_deck").Count, Is.EqualTo(3)); Assert.That(calls, Is.EqualTo(2)); Assert.That(Open(state).InteractionTypeId, Is.EqualTo("operator.dispatch.return"));
        }
        [Test] public void PaidInsertionRestoresBetweenConsumptionAndGrantWithoutConsumingAgain()
        {
            var state = State(); var registry = Registry(tacticType: "persistent", settlementSource: true); var cards = Ids(state, "hand", 1); Cover(state, registry, cards[0]); Use(state, registry, cards[0], "tactic"); Cover(state, registry, cards[1]); Use(state, registry, cards[1], "");
            string useId = state.CharacterUses.Last().UseId; var option = CharacterSettlementOptionQuery.GetOptions(state, 1, useId).Single(); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TryInvokeCharacterSettlementOption(1, option.OptionId, useId, option.StateRevision, out _, out string reason), Is.True, reason);
            for (int step = 0; step < 100 && !state.CharacterUses.Last().SourceConsumed; step++) Assert.That(engine.Advance(), Is.True);
            Assert.That(state.CharacterUses.Last().DoubleUseGranted, Is.False); state = Restore(state, registry); engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TryInvokeCharacterSettlementOption(1, option.OptionId, useId, option.StateRevision, out bool duplicate, out reason), Is.True, reason); Assert.That(duplicate, Is.True);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False); Assert.That(Open(state).InteractionTypeId, Is.EqualTo(CharacterLifecycleService.OrderInteractionTypeId));
            Assert.That(state.EffectRuntime.EffectNodes.Count(node => node.EffectTypeId == CharacterLifecycleService.ConsumeSourceTypeId), Is.EqualTo(1)); AnswerRequest(state, registry, "strategy-first"); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(8));
        }
        [Test] public void FailedInsertionPaymentRestoresSingleModeChoiceAndNeverGrantsFreeDoubleUse()
        {
            var state = State(); var registry = Registry(tacticType: "persistent", settlementSource: true); var cards = Ids(state, "hand", 1); Cover(state, registry, cards[0]); Use(state, registry, cards[0], "tactic"); Cover(state, registry, cards[1]); Use(state, registry, cards[1], "");
            state.StartPlayerId = 2;
            var option = CharacterSettlementOptionQuery.GetOptions(state, 1, state.CharacterUses.Last().UseId).Single(); var engine = new EffectTreeExecutor(state, registry); Assert.That(engine.TryInvokeCharacterSettlementOption(1, option.OptionId, option.TargetUseId, option.StateRevision, out _, out string reason), Is.True, reason);
            // 复验前来源被另一个合法业务转交，消费子式必须拒绝并恢复单发请求。
            for (int step = 0; step < 100 && !state.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == CharacterLifecycleService.ConsumeSourceTypeId); step++) Assert.That(engine.Advance(), Is.True);
            var consume = state.EffectRuntime.EffectNodes.Single(node => node.EffectTypeId == CharacterLifecycleService.ConsumeSourceTypeId);
            engine.CreateChild(consume.EffectId, EffectSpec.Create(CharacterLifecycleService.TransferTypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "cardInstanceId", NormalizedValue.CreateString(cards[0]), "recipientPlayer", NormalizedValue.CreateString("2")), 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False); Assert.That(state.CharacterUses.Last().SourceConsumed, Is.False); Assert.That(state.CharacterUses.Last().DoubleUseGranted, Is.False); Assert.That(Ids(state, "hand", 2), Does.Contain(cards[0])); Assert.That(Open(state).CandidateIds, Is.EquivalentTo(new[] { "strategy", "tactic" }));
            AnswerRequest(state, registry, "strategy"); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(5)); Assert.That(state.CharacterUses.Last().CompletedModes, Is.EqualTo(new[] { "strategy" }));
        }
        [TestCase("SourceConsumed")][TestCase("CompletedModes")][TestCase("Modes")]
        public void RecoveryRejectsMissingOriginalUseFacts(string field)
        {
            var state = State(); var registry = Registry(); string card = Ids(state, "hand", 1)[0]; Cover(state, registry, card); Use(state, registry, card, "");
            var json = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state))); ((JObject)json["Snapshot"]["State"]["CharacterUses"][0]).Remove(field);
            var recovered = HostRecoveryService.RecoverJson(json.ToString(), registry); Assert.That(recovered.Succeeded, Is.False); Assert.That(recovered.PausedFault, Is.True);
        }
        [Test] public void InvalidRecycleScopeDoesNotPartiallyMoveEarlierValidCard()
        {
            var state = State(); var registry = Registry(); string card = Ids(state, "hand", 1)[0]; Cover(state, registry, card); Use(state, registry, card); string opponent = Ids(state, "hand", 2)[0];
            registry.Register("test.invalid_recycle", context => CharacterLifecycleService.Recycle(context, 1, false, new[] { card, opponent }));
            Assert.That(Run(state, registry, EffectSpec.Create("test.invalid_recycle", playerId: 1)).Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(Ids(state, "discard", 1), Does.Contain(card));
        }
        [TestCase(false)][TestCase(true)] public void DispatchReturnSourcesAndCurrentUseExclusionComeFromItsEntry(bool excludeCurrent)
        {
            var state = State(); string card = Ids(state, "hand", 1)[0];
            var registry = Registry(tactic: context => new List<EffectSpec> { OperatorDispatchFlow.CreateSpec(1, 3, excludeCurrent ? new[] { card } : Array.Empty<string>(), new[] { FindZone(context.State, "hand", 1).ZoneId, FindZone(context.State, "covered", 1).ZoneId }) });
            Cover(state, registry, card); Use(state, registry, card, "tactic"); AnswerRequest(state, registry, Open(state).CandidateIds[0]);
            Assert.That(Open(state).CandidateIds.Contains(card), Is.EqualTo(!excludeCurrent)); state = Restore(state, registry);
            string outgoing = excludeCurrent ? Open(state).CandidateIds.First(id => id != card) : card; AnswerRequest(state, registry, outgoing);
            Assert.That(Ids(state, "retired"), Does.Contain(outgoing)); Assert.That(state.CharacterUses.Single().Finished, Is.True); Assert.That(Ids(state, "discard", 1).Contains(card), Is.EqualTo(excludeCurrent));
        }
        [Test] public void ReusableRestrictedSlotDoesNotGrowAndRejectsOtherCharacterTypes()
        {
            var state = State(); var registry = Registry(); string generated = state.ItemZones.Items.Single(item => item.TypeId == "core.generated_character_card" && item.ControllerSubject.PlayerId == 1).ItemId;
            var first = EffectSpec.Create("test.granted_cover", Object("cardId", NormalizedValue.CreateString(generated)), 1); first.SourceId = "ability.extra_shape_slot";
            Run(state, registry, first); string slot = state.CharacterCoverSlots.Single().SlotId; Use(state, registry, generated); state.ActionRound = 2; state.Phase = GamePhase.ActionRound2;
            Run(state, registry, first); Assert.That(state.CharacterCoverSlots.Count, Is.EqualTo(1)); Assert.That(state.CharacterCoverSlots[0].SlotId, Is.EqualTo(slot)); Use(state, registry, generated);
            first.NormalizedArguments = Object("cardId", NormalizedValue.CreateString(Ids(state, "hand", 1)[0])); Assert.That(Run(state, registry, first).Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(state.CharacterCoverSlots.Count, Is.EqualTo(1));
        }
        [Test] public void ActionRoundDoubleUsePermissionExpiresBySequenceAndDoesNotConsumePersistentSource()
        {
            var state = State(); state.StartPlayerId = 2; var registry = Registry(); string card = Ids(state, "hand", 1)[0];
            Run(state, registry, EffectSpec.Create(LuaDomainEffectTypeIds.SetCharacterDoubleUseRule, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "scope", NormalizedValue.CreateString("action_round"), "expiresActionRoundSequence", NormalizedValue.CreateInteger(1)), 1));
            Cover(state, registry, card); var use = Use(state, registry, card, "both", "strategy-first"); Assert.That(use.SourceConsumed, Is.False); Assert.That(use.DoubleUseSourceItemId, Is.Empty); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(5));
            state = Restore(state, registry); state.ActionRound = 2; state.Phase = GamePhase.ActionRound2; card = Ids(state, "hand", 1)[0]; Cover(state, registry, card);
            int count = state.CharacterUses.Count; Assert.That(Run(state, registry, CharacterLifecycleService.CreateUseSpec(1, card, "both", "strategy-first")).Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(state.CharacterUses.Count, Is.EqualTo(count)); Assert.That(Ids(state, "covered", 1), Does.Contain(card));
        }
        [TestCase("character")][TestCase("facility")] public void FacilityBindingRestoresAndReleasesWhenEitherRealInstanceLeaves(string leaving)
        {
            var state = State(); var registry = Registry(tacticType: "persistent"); string card = Ids(state, "hand", 1)[0], facility = Ids(state, "facility_deck")[0]; Cover(state, registry, card); Use(state, registry, card, "tactic");
            registry.Register("test.place_and_bind", context => {
                var cell = FindZone(context.State, "city_cell_0", 1); var item = context.State.ItemZones.Items.Single(value => value.ItemId == facility);
                var request = new ItemTransferRequest { ItemIds = new List<string> { facility }, ExecutingPlayerId = 1, ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId }, Destination = new ZoneRef { ZoneId = cell.ZoneId }, InsertionKind = "unordered", OperationProfileId = "core.facility_card_transfer", RequiredCount = 1 };
                new ItemTransferService().RelocateItems(context, request); return CharacterLifecycleService.BindFacility(context, card, facility);
            });
            Run(state, registry, EffectSpec.Create("test.place_and_bind", playerId: 1)); state = Restore(state, registry); Assert.That(state.FacilityCharacterBindings.Single().Active, Is.True);
            if (leaving == "character") Run(state, registry, MoveSpec(1, new[] { card }, FindZone(state, "persistent", 1).ZoneId, FindZone(state, "discard", 1).ZoneId, "unordered", 1, "core.character_card_transfer"));
            else
            {
                registry.Register("test.remove_bound_facility", context => {
                    var request = new ItemTransferRequest { ItemIds = new List<string> { facility }, ExecutingPlayerId = 1, ExpectedSource = new ItemPositionRef { ZoneId = FindZone(context.State, "city_cell_0", 1).ZoneId }, Destination = new ZoneRef { ZoneId = FindZone(context.State, "facility_deck").ZoneId }, InsertionKind = "bottom", OperationProfileId = "core.facility_card_transfer", RequiredCount = 1 };
                    new ItemTransferService().RelocateItems(context, request); return EffectStepResult.Completed();
                });
                Run(state, registry, EffectSpec.Create("test.remove_bound_facility", playerId: 1));
            }
            Assert.That(state.FacilityCharacterBindings.Single().Active, Is.False); state = Restore(state, registry); Assert.That(state.FacilityCharacterBindings.Single().Active, Is.False);
        }
        [TestCase("persistent", 5)][TestCase("normal", 0)] public void ModeDepositStaysWithPersistentCarrierAndReturnsOnlyOnRealExit(string skillType, int stored)
        {
            var state = State(); string card = Ids(state, "hand", 1)[0]; var markers = Ids(state, "supply", 1).Take(5).ToArray();
            var declaration = new CardZoneDefinition { LocalZoneId = "stored_cubes", ZoneTemplateId = "core.zone.card_cube_storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered,
                EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.return_contents_before_exit", VisibilityPolicyId = ItemZoneVisibility.Public };
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, card, declaration, out string storage, out string reason), Is.True, reason);
            var registry = Registry(tacticType: skillType, tactic: context => new List<EffectSpec> { MoveSpec(1, markers, FindZone(context.State, "supply", 1).ZoneId, storage, "unordered", 5, "core.cube_storage") });
            Cover(state, registry, card); var use = Use(state, registry, card, "tactic"); Assert.That(use.Finished, Is.True); Assert.That(new ItemZoneQuery().GetItemIds(state, storage).Count, Is.EqualTo(stored)); Assert.That(Ids(state, "supply", 1).Count, Is.EqualTo(6 - stored));
            state = Restore(state, registry); Assert.That(new ItemZoneQuery().GetItemIds(state, storage).Count, Is.EqualTo(stored));
        }
        [TestCase(2, false, 3)][TestCase(3, false, 2)][TestCase(4, false, 2)][TestCase(3, true, 3)][TestCase(4, true, 3)] public void OrdinaryDispatchCountUsesFrozenPlayersAndExpansion(int players, bool infected, int expected)
        {
            var state = State(players: players); if (infected) state.RuleConfiguration.EnabledExpansionIds.Add("infected"); var registry = Registry(); Run(state, registry, OperatorDispatchFlow.CreateSpec(state, 1)); Assert.That(Open(state).CandidateIds.Count, Is.EqualTo(expected));
        }
        [Test] public void MissingModeContractCannotSilentlyBecomeNormalOrCreatePartialUse()
        {
            var state = State(); var registry = Registry(tacticType: "persistent"); string card = Ids(state, "hand", 1)[0]; Cover(state, registry, card); registry.CharacterAbilityContracts.Remove(CharacterCardDatabase.Get("texas").TacticAbilityId);
            var node = Run(state, registry, CharacterLifecycleService.CreateUseSpec(1, card, "tactic")); Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(node.FailureReason, Is.EqualTo("character_mode_contract_missing")); Assert.That(state.CharacterUses, Is.Empty); Assert.That(Ids(state, "covered", 1), Does.Contain(card));
        }
        [Test] public void StorageReturnsCompanyCubesToTheirActualPoolOnCarrierExit()
        {
            var state = State(companyMarkers: 2); var registry = Registry(tacticType: "persistent"); string card = Ids(state, "hand", 1)[0], marker = Ids(state, "company_markers")[0];
            var declaration = new CardZoneDefinition { LocalZoneId = "stored_cubes", ZoneTemplateId = "core.zone.card_cube_storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered,
                EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.return_contents_before_exit", VisibilityPolicyId = ItemZoneVisibility.Public };
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, card, declaration, out string storage, out string reason), Is.True, reason); Cover(state, registry, card); Use(state, registry, card, "tactic");
            Run(state, registry, MoveSpec(1, new[] { marker }, FindZone(state, "company_markers").ZoneId, storage, "unordered", 1, "core.cube_storage")); state = Restore(state, registry);
            registry.Register("test.company_carrier_exit", context => { string error = CharacterLifecycleService.MoveCard(context, card, FindZone(context.State, "retired").ZoneId); return error.Length == 0 ? EffectStepResult.Completed() : EffectStepResult.Failed(error); });
            Run(state, registry, EffectSpec.Create("test.company_carrier_exit", playerId: 1)); Assert.That(Ids(state, "company_markers").Count, Is.EqualTo(2)); Assert.That(Ids(state, "company_markers"), Does.Contain(marker)); Assert.That(Ids(state, "supply", 1).Count, Is.EqualTo(6)); Assert.That(state.ItemZones.Items.Single(item => item.ItemId == marker).OwnerSubject.CompanyId, Is.EqualTo("stonehoove_security"));
        }
    }
}
