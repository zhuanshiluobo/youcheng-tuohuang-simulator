using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Application.Sessions;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class ExpansionSetupFlowTests
    {
        private static ExternalContentPack installed;
        private static ExternalContentPack Installed() => installed ?? (installed = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets", "StreamingAssets", "Content", "core")));
        private static GameRuleConfiguration Configuration(int players = 3, params string[] expansions) => new GameRuleConfiguration {
            MapId = StaticMapDefinitions.FourPlayerMapId, PlayerCount = players,
            EnabledExpansionIds = expansions.Length == 0 ? new List<string> { "core" } : expansions.ToList() };
        private static GameState State(int players, out EffectRegistry registry, int basics = 5, bool enterprise = false, bool allCityStyles = false)
        {
            var config = Configuration(players); var catalog = ActiveContentCatalogBuilder.Build(Installed(), config); config.ContentPackHash = catalog.ContentPackHash;
            var recipe = catalog.SetupRecipe; var definitions = catalog.ContentDefinitions;
            foreach (string id in new[] { "test.mountain", "test.mlynar", "test.amiya" }.Take(basics - 5))
            {
                recipe.Add(id, id, "core.basic_character_card", "hand", true);
                var definition = GameStateCloneService.DeepClone(definitions.Characters[0]); definition.CardId = id; definition.TemplateId = id; definitions.Characters.Add(definition);
            }
            recipe.InitialBasicCharacterCount = basics;
            if (allCityStyles)
            {
                recipe.CityStyleLevelOneDrawCount = definitions.CityStyles.Count(d => d.Level == 1);
                recipe.CityStyleLevelTwoDrawCount = definitions.CityStyles.Count(d => d.Level == 2);
            }
            var boards = new EnterpriseDefinitions();
            if (enterprise)
            {
                recipe.EnterpriseEntrance = true;
                foreach (string id in new[] { "test.rhodes", "test.rhine", "test.penguin", "test.blacksteel", "test.wild", "test.karlan", "test.raythean", "test.columbia" })
                {
                    recipe.Add(id, id, "core.board_card", "reserve");
                    definitions.EnterpriseDefinitionIds.Add(id);
                    var board = new EnterpriseDefinition { DefinitionId = id, RuntimeId = id, DepartmentRewardPolicy = id == "test.rhine" ? "active_department" : "",
                        Levels = Enumerable.Range(0, 6).Select(level => new EnterpriseLevelDefinition { Level = level, Kind = level == 0 ? "empty" : "reward", AbilityId = level == 0 ? "" : id + ".reward." + level }).ToList() };
                    boards.Register(board);
                    foreach (int level in Enumerable.Range(1, 5)) boards.BindAbility(id + ".reward." + level, context => new[] { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 1) });
                }
                foreach (string id in new[] { "test.structural", "test.energy", "test.engineering" })
                {
                    recipe.Entries.Add(new SetupRecipeEntry { EntryId = id, DefinitionId = id, TypeId = "core.board_card", InitialZoneRole = "departments", Quantity = 1, Condition = new SetupRecipeCondition { Kind = "enterprise_selected", RuntimeId = "test.rhine" } });
                    boards.Register(new DepartmentDefinition { DefinitionId = id, EnterpriseRuntimeId = "test.rhine", Rewards = Enumerable.Range(2, 4).Select(level => new DepartmentRewardDefinition { Level = level, AbilityId = id + ".reward." + level }).ToList() });
                    foreach (int level in Enumerable.Range(2, 4)) boards.BindAbility(id + ".reward." + level, context => new[] { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 1) });
                }
                recipe.Entries.Add(new SetupRecipeEntry { EntryId = "test.operators", DefinitionId = definitions.Characters[0].CardId, TypeId = "core.operator_card", InitialZoneRole = "operator_deck", Quantity = 8, Condition = new SetupRecipeCondition { Kind = "enterprise_selected", RuntimeId = "test.rhodes" } });
            }
            boards.Freeze();
            registry = new EffectRegistry(); catalog.Register(registry); ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            if (enterprise) registry.EnterpriseDefinitions = boards;
            registry.SetupRecipe = GameStateCloneService.DeepClone(recipe); registry.ContentDefinitions = GameStateCloneService.DeepClone(definitions);
            var seats = Enumerable.Range(1, players).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "玩家" + id }).ToList();
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, seats, config, 321, recipe, definitions);
            state.EffectRuntime.PlayerOrderSnapshot = seats.Select(s => s.PlayerId).ToList();
            return state;
        }
        private static RoundExecutionService Mainline(GameState state, EffectRegistry registry)
        {
            var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)); var influence = new YC.Domain.Influence.InfluenceService(map);
            InfluenceEffectExecutor.Register(registry, influence);
            EventCardEffectExecutor.Register(registry, map, influence, new EventDeckService(), new ResourceTokenService());
            PlayerEntranceEffectExecutor.Register(registry, map); RoundStartedRedZoneRule.Register(registry, map);
            return new RoundExecutionService(registry);
        }
        private static EffectNodeRuntimeState Run(GameState state, EffectRegistry registry, EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(spec); var result = engine.RunUntilQuiescent();
            Assert.That(result.Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state); return engine.GetNode(id);
        }
        private static void Valid(GameState state) { Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason); }
        private static InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open");
        private static void Answer(GameState state, EffectRegistry registry, params string[] values)
        {
            var request = Open(state); Assert.That(request, Is.Not.Null);
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                values.Length == 1 ? NormalizedValue.CreateString(values[0]) : Strings(values), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); Valid(state);
        }
        private static GameState Restore(GameState state, EffectRegistry registry)
        {
            var result = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic); return result.State;
        }
        private static List<string> In(GameState state, string role, int player = -1) => new ItemZoneQuery().GetItemIds(state, FindZone(state, role, player).ZoneId).ToList();
        private static GameSession Session(GameState state, EffectRegistry registry, RoundExecutionService mainline = null)
        {
            var session = new GameSession(state);
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry, mainline));
            return session;
        }
        private static GameCommand AnswerCommand(InteractionRequest request, IEnumerable<string> selected, bool decline = false)
        {
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId, OptionIds = selected.ToList() };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (decline) command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = bool.FalseString;
            return command;
        }
        private static EffectNodeRuntimeState StartPreparation(GameState state, EffectRegistry registry, out RoundExecutionService mainline)
        {
            mainline = Mainline(state, registry);
            var started = mainline.StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            return state.EffectRuntime.EffectNodes.Single(node => node.EffectTypeId == ExpansionSetupFlow.PreparationTypeId);
        }
        private static void AnswerCurrentRequest(GameState state, EffectRegistry registry, RoundExecutionService mainline,
            InteractionRequest request, params string[] selected)
        {
            request = state.EffectRuntime.InteractionRequests.Single(item => item.Status == "open" &&
                item.AnsweringPlayerId == request.AnsweringPlayerId && item.GetStableInteractionId() == request.GetStableInteractionId());
            Assert.That(request.StateRevision, Is.EqualTo(state.EffectRuntime.StateRevision));
            var command = AnswerCommand(request, selected);
            if (request.AnswerSchema == "resource_allocation")
            {
                string total = request.CandidateIds.Single(id => id.StartsWith("total:", StringComparison.Ordinal)).Substring(6);
                command.OptionIds.Clear(); command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = "Originium=" + total;
            }
            else if (selected.Length == 0)
                command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = bool.TrueString;
            var result = Session(state, registry, mainline).Submit(command);
            Assert.That(result.Succeeded, Is.True, request.InteractionTypeId + " / " + result.Validation?.Reason);
            Valid(state);
        }
        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void FormalCoreRecipeCreatesFiveCardsAndPreservesChosenMap(int players)
        {
            var state = State(players, out var registry); Valid(state);
            Assert.That(state.MapId, Is.EqualTo(StaticMapDefinitions.FourPlayerMapId)); Assert.That(state.Players.Count, Is.EqualTo(players));
            foreach (var player in state.Players) Assert.That(In(state, "hand", player.PlayerId).Count, Is.EqualTo(5));
            Assert.That(state.ItemZones.Items.Count(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.facility_supply_slot")), Is.EqualTo(6));
            Assert.That(In(state, "city_styles").Count, Is.EqualTo(6)); Assert.That(In(state, "enterprises"), Is.Empty);
            var eventIds = In(state, "event_red").Select(id => state.ItemZones.Items.Single(i => i.ItemId == id).DefinitionId);
            Assert.That(eventIds.Count(), Is.EqualTo(6), "所选四人地图必须保留完整红牌池。");
        }
        [Test]
        public void CatalogBuildDoesNotMutateInstalledDirectoryOrPriorCatalog()
        {
            var installed = Installed(); string original = installed.ContentPackHash; var before = JsonConvert.SerializeObject(installed.ActiveDefinitions);
            var first = ActiveContentCatalogBuilder.Build(installed, Configuration(3));
            var otherMap = Configuration(3); otherMap.MapId = StaticMapDefinitions.ThreePlayerMapId;
            var second = ActiveContentCatalogBuilder.Build(installed, otherMap);
            Assert.That(installed.ContentPackHash, Is.EqualTo(original)); Assert.That(JsonConvert.SerializeObject(installed.ActiveDefinitions), Is.EqualTo(before));
            Assert.That(first.ContentPackHash, Is.Not.EqualTo(second.ContentPackHash)); Assert.That(first.ContentPackHash, Is.Not.EqualTo(original));
            var clone = first.SetupRecipe; clone.Entries.Clear(); Assert.That(first.SetupRecipe.Entries, Is.Not.Empty);
            var changed = Configuration(3); changed.ConfigurationRevision = 77;
            Assert.That(ActiveContentCatalogBuilder.Build(installed, changed).ContentPackHash, Is.EqualTo(first.ContentPackHash));
        }
        [TestCase("infected")]
        public void MissingFormalExpansionContentBlocksLaunch(string expansion)
        {
            var config = Configuration(3, expansion == "enterprise" ? new[] { "core", "enterprise" } : new[] { "core", "enterprise", "infected" });
            Assert.That(() => ActiveContentCatalogBuilder.Build(Installed(), config), Throws.InvalidOperationException.With.Message.EqualTo("content_not_ready"));
        }
        [Test]
        public void RuleAndFingerprintMismatchesAreExplicit()
        {
            var config = Configuration(); config.ContentPackHash = "wrong";
            Assert.That(() => ActiveContentCatalogBuilder.Build(Installed(), config), Throws.InvalidOperationException.With.Message.EqualTo("content_version_mismatch"));
            config.ContentPackHash = ""; config.RuleModeId = "entrepreneur";
            Assert.That(() => ActiveContentCatalogBuilder.Build(Installed(), config), Throws.InvalidOperationException.With.Message.EqualTo("rule_mode_not_implemented"));
        }
        [TestCase(5, false)] [TestCase(7, false)] [TestCase(8, false)] [TestCase(8, true)]
        public void InitialDiscardKeepsUnselectedFiveAndRecoveryDoesNotRepeatRetirement(int basics, bool retireAmiya)
        {
            var state = State(3, out var registry, basics); var node = Run(state, registry, EffectSpec.Create(ExpansionSetupFlow.RetentionTypeId, playerId: 1));
            if (basics > 5)
            {
                state = Restore(state, registry); var request = Open(state); var candidates = request.CandidateIds;
                Assert.That(request.InteractionTypeId, Is.EqualTo("setup.basic_character.discard"));
                Assert.That(request.PromptKey, Is.EqualTo(request.InteractionTypeId));
                Assert.That(candidates, Is.EquivalentTo(In(state, "hand", 1)));
                Assert.That(request.MinSelections, Is.EqualTo(basics - 5));
                Assert.That(request.MaxSelections, Is.EqualTo(basics - 5));
                Assert.That(request.AllowDecline, Is.False);
                string amiya = state.ItemZones.Items.FirstOrDefault(i => i.OwnerSubject.PlayerId == 1 && i.DefinitionId == "test.amiya")?.ItemId;
                var selected = candidates.Where(id => id != amiya).Take(basics - 5).ToList();
                if (retireAmiya) selected[0] = amiya;
                var session = Session(state, registry);
                var command = AnswerCommand(request, selected);
                var result = session.Submit(command);
                Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
                Assert.That(In(state, "secret_retired"), Is.EquivalentTo(selected), "提交的是弃牌集合，不能回盒其补集。");
                var repeated = session.Submit(command);
                Assert.That(repeated.Succeeded, Is.False, "已完成的交互应拒绝重复回答，且不得再次移牌。");
                Assert.That(repeated.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
                Assert.That(In(state, "secret_retired"), Is.EquivalentTo(selected));
                Assert.That(In(state, "hand", 1).Contains(amiya), Is.EqualTo(!retireAmiya && amiya != null));
            }
            else Assert.That(Open(state), Is.Null, "基础5张不应出现弃牌交互。");
            Assert.That(CharacterLifecycleService.GetRetainedBasicCharacterCount(state, 1), Is.EqualTo(5));
            Assert.That(In(state, "secret_retired").Count, Is.EqualTo(basics - 5));
            var retired = In(state, "secret_retired"); state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(In(state, "secret_retired"), Is.EquivalentTo(retired));
            Assert.That(new EffectTreeExecutor(state, registry).GetNode(node.EffectId).FlowStage, Is.EqualTo("finished"));
        }
        [TestCase("keep_count")] [TestCase("duplicate")] [TestCase("foreign_card")]
        [TestCase("wrong_player")] [TestCase("stale_revision")] [TestCase("decline")]
        public void InvalidDiscardAnswersLeaveThePendingSelectionAndCardsUntouched(string invalidKind)
        {
            var state = State(3, out var registry, 8);
            Run(state, registry, ExpansionSetupFlow.RetentionSpec(1));
            var request = Open(state); var hand = In(state, "hand", 1);
            var selected = request.CandidateIds.Take(3).ToList();
            if (invalidKind == "keep_count") selected = request.CandidateIds.Take(5).ToList();
            if (invalidKind == "duplicate") selected[1] = selected[0];
            if (invalidKind == "foreign_card") selected[0] = In(state, "hand", 2)[0];
            var command = AnswerCommand(request, selected, invalidKind == "decline");
            if (invalidKind == "wrong_player") command.PlayerId = 2;
            if (invalidKind == "stale_revision") command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = (request.StateRevision + 1).ToString();
            var result = Session(state, registry).Submit(command);
            Assert.That(result.Succeeded, Is.False, invalidKind);
            Assert.That(In(state, "hand", 1), Is.EquivalentTo(hand));
            Assert.That(In(state, "secret_retired"), Is.Empty);
            Assert.That(Open(state).GetStableInteractionId(), Is.EqualTo(request.GetStableInteractionId()));
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active));
        }

        private static InteractionRequest MakeLegacyKeepRequest(GameState state)
        {
            // 重建旧档保存的协议形状；生产路径永远只生成discard请求。
            var request = Open(state);
            request.InteractionTypeId = ExpansionSetupFlow.LegacyBasicCharacterKeepInteraction;
            request.PromptKey = request.InteractionTypeId;
            request.MinSelections = request.MaxSelections = 5;
            state.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId).FlowStage = "choose_basic_characters";
            return request;
        }

        [Test]
        public void RestoredLegacyKeepRequestIsExplicitlyRejectedWithoutReversingItsAnswer()
        {
            var state = State(2, out var registry, 7);
            Run(state, registry, ExpansionSetupFlow.RetentionSpec(1)); MakeLegacyKeepRequest(state);
            state = Restore(state, registry);
            var request = Open(state); var hand = In(state, "hand", 1);
            var result = Session(state, registry).Submit(AnswerCommand(request, request.CandidateIds.Take(5)));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.Reason, Does.Contain("legacy_basic_character_keep_request_unsupported"));
            Assert.That(In(state, "hand", 1), Is.EquivalentTo(hand));
            Assert.That(In(state, "secret_retired"), Is.Empty);
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(ExpansionSetupFlow.LegacyBasicCharacterKeepInteraction));
        }

        [TestCase(false)] [TestCase(true)]
        public void PreviouslyAcceptedLegacyKeepAnswerResumesWithItsOriginalMeaning(bool selectionAlreadyProcessed)
        {
            var state = State(2, out var registry, 7);
            var node = Run(state, registry, ExpansionSetupFlow.RetentionSpec(1));
            var request = MakeLegacyKeepRequest(state); var initial = In(state, "hand", 1);
            var retained = request.CandidateIds.Take(5).ToArray();
            var engine = new EffectTreeExecutor(state, registry);
            // 仅构造修复前已接受的历史答案，随后走真实Host恢复和同一执行器。
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, Strings(retained), out string reason), Is.True, reason);
            if (selectionAlreadyProcessed)
                for (int step = 0; step < 30 && engine.GetNode(node.EffectId).FlowStage != "retire_unselected_basic"; step++) Assert.That(engine.Advance(), Is.True);
            if (selectionAlreadyProcessed) Assert.That(engine.GetNode(node.EffectId).FlowStage, Is.EqualTo("retire_unselected_basic"));
            state = Restore(state, registry);
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(In(state, "hand", 1), Is.EquivalentTo(retained));
            Assert.That(In(state, "secret_retired"), Is.EquivalentTo(initial.Except(retained)));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(In(state, "hand", 1), Is.EquivalentTo(retained));
            Assert.That(In(state, "secret_retired"), Is.EquivalentTo(initial.Except(retained)));
        }

        [Test]
        public void AcceptedDiscardResumesBeforeRetirementAndMovesOnlyTheSelectedInstancesOnce()
        {
            var state = State(2, out var registry, 8);
            var node = Run(state, registry, ExpansionSetupFlow.RetentionSpec(1));
            var request = Open(state); var selected = request.CandidateIds.Take(3).ToArray();
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, Strings(selected), out string reason), Is.True, reason);
            for (int step = 0; step < 30 && engine.GetNode(node.EffectId).FlowStage != "retire_selected_basic"; step++) Assert.That(engine.Advance(), Is.True);
            Assert.That(engine.GetNode(node.EffectId).FlowStage, Is.EqualTo("retire_selected_basic"));
            Assert.That(In(state, "secret_retired"), Is.Empty);
            state = Restore(state, registry);
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(In(state, "secret_retired"), Is.EquivalentTo(selected));
            Assert.That(CharacterLifecycleService.GetRetainedBasicCharacterCount(state, 1), Is.EqualTo(5));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(In(state, "secret_retired"), Is.EquivalentTo(selected));
        }

        [TestCase("selected")] [TestCase("unselected")] [TestCase("already_initialized")]
        public void ConditionalOperatorInitializationShufflesOnceBeforeUpgradesAndSurvivesRecovery(string scenario)
        {
            bool alreadyInitialized = scenario == "already_initialized", selectRhodes = scenario != "unselected";
            var state = State(2, out var originalRegistry, enterprise: true);
            var recipe = GameStateCloneService.DeepClone(state.SetupRecipe);
            if (alreadyInitialized)
            {
                // 感染者调度边界：16张干员以always装配，已由Assemble完成初始洗牌。
                var operators = recipe.Entries.Single(entry => entry.EntryId == "test.operators");
                operators.Quantity = 16;
                operators.Condition = new SetupRecipeCondition { Kind = "always" };
                state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1,
                    Enumerable.Range(1, 2).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "玩家" + id }).ToList(),
                    state.RuleConfiguration, 321, recipe, originalRegistry.ContentDefinitions);
                state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            }
            var randomBounds = new List<int>();
            var registry = new EffectRegistry();
            DeckShuffleEffectExecutor.Register(registry, max => { randomBounds.Add(max); return 0; });
            ExpansionSetupFlow.Register(registry, max => max - 1);
            ActiveContentCatalogBuilder.Build(Installed(), Configuration(2)).Register(registry);
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            registry.EnterpriseDefinitions = originalRegistry.EnterpriseDefinitions;
            registry.SetupRecipe = recipe; registry.ContentDefinitions = originalRegistry.ContentDefinitions;
            var initialOrder = In(state, "operator_deck");
            var expectedInput = state.ItemZones.Items.Where(item => item.TypeId == "core.operator_card").Select(item => item.ItemId).ToList();
            string zoneId = FindZone(state, "operator_deck").ZoneId;
            int initialRevision = state.ItemZones.Orders.Single(order => order.ZoneId == zoneId).OrderRevision;
            Assert.That(initialOrder.Count, Is.EqualTo(alreadyInitialized ? 16 : 0));
            string rootId = StartPreparation(state, registry, out var mainline).EffectId;
            var engine = mainline.CreateCommandExecutor(state);
            for (int step = 0; engine.GetNode(rootId).FlowStage != "initial_upgrades" && step < 300; step++)
            {
                RoundExecutionService.RefreshConcurrentInteractionRevisions(state);
                var request = Open(state);
                if (request != null)
                {
                    var selected = request.CandidateIds.OrderByDescending(id =>
                        (state.ItemZones.Items.Single(item => item.ItemId == id).DefinitionId == "test.rhodes") == selectRhodes)
                        .Take(request.MinSelections).ToList();
                    Assert.That(request.StateRevision, Is.EqualTo(state.EffectRuntime.StateRevision));
                    Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                        selected.Count == 1 ? NormalizedValue.CreateString(selected[0]) : Strings(selected), out string reason), Is.True, reason);
                }
                Assert.That(engine.Advance(), Is.True, state.EffectRuntime.LastFaultMessage);
            }
            Assert.That(engine.GetNode(rootId).FlowStage, Is.EqualTo("initial_upgrades"));
            Assert.That(In(state, "enterprises").Any(id => state.ItemZones.Items.Single(item => item.ItemId == id).DefinitionId == "test.rhodes"), Is.EqualTo(selectRhodes));
            bool shouldShuffle = selectRhodes && !alreadyInitialized;
            var childIds = engine.GetNode(rootId).ChildEffectIds.ToArray();
            var selections = engine.GetNode(rootId).ChildSchedulingGroups.Single(group => group.StableGroupKey == "initial_enterprise_selections");
            Assert.That(selections.Mode, Is.EqualTo(EffectChildSchedulingMode.Independent));
            Assert.That(selections.ChildEffectIds.Select(id => engine.GetNode(id).PlayerId), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(selections.ChildEffectIds.Select(id => engine.GetNode(id).Status), Has.All.EqualTo(EffectNodeStatus.Completed));
            var upgradeSteps = childIds.Except(selections.ChildEffectIds).ToArray();
            var shuffleIds = upgradeSteps.Where(id => engine.GetNode(id).EffectTypeId == DeckShuffleEffectExecutor.TypeId).ToArray();
            Assert.That(shuffleIds.Length, Is.EqualTo(shouldShuffle ? 1 : 0));
            Assert.That(upgradeSteps.Length, Is.EqualTo(shouldShuffle ? 5 : 4));
            Assert.That(childIds.Length, Is.EqualTo(selections.ChildEffectIds.Count + upgradeSteps.Length));
            Assert.That(randomBounds, Is.Empty);
            if (shouldShuffle)
            {
                Assert.That(upgradeSteps[0], Is.EqualTo(shuffleIds[0]), "干员初始洗牌必须排在四项初始企业升级之前。");
                Assert.That(In(state, "operator_deck"), Is.EqualTo(expectedInput));
            }
            state = Restore(state, registry); engine = mainline.CreateCommandExecutor(state);
            Assert.That(engine.GetNode(rootId).ChildEffectIds, Is.EqualTo(childIds));
            if (shouldShuffle)
            {
                for (int step = 0; engine.GetNode(shuffleIds[0]).ShuffleInputItemIds.Count == 0 && step < 100; step++)
                    Assert.That(engine.Advance(), Is.True, state.EffectRuntime.LastFaultMessage);
                Assert.That(engine.GetNode(shuffleIds[0]).ShuffleInputItemIds, Is.EqualTo(expectedInput));
                Assert.That(randomBounds, Is.EqualTo(new[] { 8, 7, 6, 5, 4, 3, 2 }));
                Assert.That(engine.GetNode(rootId).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
                var enterpriseQuery = new EnterpriseStateQuery(registry.EnterpriseDefinitions);
                foreach (string board in In(state, "enterprises"))
                    foreach (int player in new[] { 1, 2 }) Assert.That(enterpriseQuery.GetLevel(state, player, board), Is.Zero);
                // 第二个恢复点位于洗牌业务已提交、初始升级尚未发生时。
                state = Restore(state, registry); engine = mainline.CreateCommandExecutor(state);
            }
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(rootId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            var expectedOrder = shouldShuffle ? expectedInput.Skip(1).Concat(expectedInput.Take(1)).ToList() : initialOrder;
            Assert.That(In(state, "operator_deck"), Is.EqualTo(expectedOrder));
            Assert.That(new ItemZoneQuery().GetTopItemIds(state, zoneId, 3), Is.EqualTo(expectedOrder.Take(3)));
            Assert.That(state.ItemZones.Orders.Single(order => order.ZoneId == zoneId).OrderRevision, Is.EqualTo(initialRevision + (shouldShuffle ? 1 : 0)));
            if (!selectRhodes) Assert.That(In(state, "reserve").Intersect(expectedInput), Is.EquivalentTo(expectedInput));
            // 单步执行器保留洗牌恢复点；归档前由正式主链完成活动入场玩家投影。
            var advance = mainline.Advance(state);
            Assert.That(advance.IsValid, Is.True, advance.Reason);
            state = Restore(state, registry); engine = mainline.CreateCommandExecutor(state);
            for (int repeat = 0; repeat < 2; repeat++) Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(engine.GetNode(rootId).ChildEffectIds, Is.EqualTo(childIds));
            Assert.That(randomBounds.Count, Is.EqualTo(shouldShuffle ? 7 : 0));
            Assert.That(In(state, "operator_deck"), Is.EqualTo(expectedOrder));
            Assert.That(state.ItemZones.Orders.Single(order => order.ZoneId == zoneId).OrderRevision, Is.EqualTo(initialRevision + (shouldShuffle ? 1 : 0)));
            Valid(state);
        }

        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void EnterpriseEntrancePublishesFourBoardsAndRealCooperationMarkers(int players)
        {
            var state = State(players, out var registry, 7, true);
            string preparationId = StartPreparation(state, registry, out var mainline).EffectId;
            var preparation = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == preparationId);
            var group = preparation.ChildSchedulingGroups.Single(item => item.StableGroupKey == "initial_enterprise_selections");
            Assert.That(group.Mode, Is.EqualTo(EffectChildSchedulingMode.Independent));
            Assert.That(group.ChildEffectIds.Select(id => state.EffectRuntime.EffectNodes.Single(node => node.EffectId == id).PlayerId),
                Is.EqualTo(Enumerable.Range(1, players)));
            Assert.That(state.EffectRuntime.InteractionRequests.Where(item => item.Status == "open" &&
                item.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction).Select(item => item.AnsweringPlayerId),
                Is.EquivalentTo(Enumerable.Range(1, players)));
            int selections = 0;
            while (state.EffectRuntime.EffectNodes.Single(node => node.EffectId == preparationId).Status != EffectNodeStatus.Completed)
            {
                var request = Open(state); Assert.That(request, Is.Not.Null);
                foreach (int viewer in Enumerable.Range(1, players).Where(p => p != request.AnsweringPlayerId))
                    Assert.That(GameStateViewProjector.ProjectForPlayer(state, viewer).Interactions.Any(r => r.InteractionId == request.GetStableInteractionId()), Is.False);
                string interactionId = request.GetStableInteractionId(); var candidates = request.CandidateIds.ToArray();
                state = Restore(state, registry);
                Assert.That(mainline.Advance(state).IsValid, Is.True);
                request = state.EffectRuntime.InteractionRequests.Single(item => item.Status == "open" && item.GetStableInteractionId() == interactionId);
                Assert.That(request.CandidateIds, Is.EqualTo(candidates));
                var selected = request.CandidateIds.OrderByDescending(id => state.ItemZones.Items.Any(i => i.ItemId == id && new[] { "test.rhine", "test.rhodes" }.Contains(i.DefinitionId))).Take(request.MinSelections).ToArray();
                AnswerCurrentRequest(state, registry, mainline, request, selected); selections++;
                var remainingTasks = group.ChildEffectIds.Select(id => state.EffectRuntime.EffectNodes.Single(node => node.EffectId == id)).ToArray();
                if (remainingTasks.Any(task => task.Status != EffectNodeStatus.Completed)) Assert.That(In(state, "enterprises"), Is.Empty);
                Assert.That(selections < 8, Is.True);
            }
            var boards = In(state, "enterprises"); Assert.That(boards.Count, Is.EqualTo(4));
            var query = new EnterpriseStateQuery(registry.EnterpriseDefinitions);
            foreach (int player in Enumerable.Range(1, players)) Assert.That(boards.Sum(id => query.GetLevel(state, player, id)), Is.EqualTo(players == 2 ? 2 : 1));
            Assert.That(state.ItemZones.Items.Count(i => i.TypeId == "core.player_marker" && state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.board_marker_track")), Is.EqualTo(players * 4));
            var boardDefinitions = boards.Select(id => state.ItemZones.Items.Single(i => i.ItemId == id).DefinitionId).ToList();
            Assert.That(In(state, "operator_deck").Count, Is.EqualTo(boardDefinitions.Contains("test.rhodes") ? 8 : 0));
            Assert.That(In(state, "departments").Count, Is.EqualTo(boardDefinitions.Contains("test.rhine") ? 3 : 0));
            Assert.That(state.ItemZones.Relations.Count(r => r.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation), Is.EqualTo(boardDefinitions.Contains("test.rhine") ? 1 : 0));
            if (players == 3)
            {
                preparation = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == preparationId);
                Assert.That(Value(preparation.ContinuationData, "candidateEnterpriseItemIds").Items.Select(v => v.StringValue), Does.Not.Contain(Text(preparation.ContinuationData, "randomEnterpriseItemId")));
            }
        }

        [Test]
        public void OtherPlayersSecretRetirementKeepsOwnLiveRetentionCandidatesVisibleAfterRecovery()
        {
            var state = State(2, out var registry, 7);
            Run(state, registry, ExpansionSetupFlow.RetentionSpec(1));
            Answer(state, registry, Open(state).CandidateIds.Take(2).ToArray());
            string[] secretInstances = In(state, "secret_retired").ToArray();
            Assert.That(secretInstances.Length, Is.EqualTo(2));
            Run(state, registry, ExpansionSetupFlow.RetentionSpec(2));
            state = Restore(state, registry);
            var request = Open(state);
            var owner = GameStateViewProjector.ProjectForPlayer(state, 2);
            var visible = owner.Interactions.SingleOrDefault(r => r.InteractionId == request.GetStableInteractionId());
            Assert.That(visible, Is.Not.Null, "他人回盒的是独立实例，不能按同一角色定义抹掉本人的合法候选。");
            Assert.That(visible.CandidateIds, Is.EqualTo(request.CandidateIds));
            Assert.That(visible.PromptParameters, Is.Not.Null);
            var other = GameStateViewProjector.ProjectForPlayer(state, 1);
            Assert.That(other.Interactions.Any(r => r.InteractionId == request.GetStableInteractionId()), Is.False);
            Assert.That(other.WaitingForPlayerIds, Does.Contain(2));
            foreach (var view in new[] { owner, other })
            foreach (string secret in secretInstances)
                Assert.That(JsonConvert.SerializeObject(view), Does.Not.Contain(secret));
            Answer(state, registry, visible.CandidateIds.Take(2).ToArray());
            Assert.That(CharacterLifecycleService.GetRetainedBasicCharacterCount(state, 2), Is.EqualTo(5));
        }
        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void RedZonePreparationResumesUpgradeBeforeSupplyRetirement(int players)
        {
            var state = State(players, out var registry, 7, true); Run(state, registry, EffectSpec.Create(ExpansionSetupFlow.PreparationTypeId, playerId: 1));
            while (Open(state) != null) Answer(state, registry, Open(state).CandidateIds.Take(Open(state).MinSelections).ToArray());
            state.Round = players == 4 ? 4 : players == 3 ? 5 : 6;
            var before = state.Players.ToDictionary(p => p.PlayerId, p => In(state, "enterprises").Sum(id => new EnterpriseStateQuery(registry.EnterpriseDefinitions).GetLevel(state, p.PlayerId, id)));
            var mainline = Mainline(state, registry);
            var created = mainline.CreateRound(state, state.Round, 1); Assert.That(created.IsValid, Is.True, created.Reason);
            var redLocations = StaticMapDefinitions.Resolve(state.MapId).Locations.Where(l => l.IsRedZone).Select(l => l.LocationId);
            Assert.That(redLocations.All(id => state.Map.OpenLocationIds.Contains(id)), Is.True);
            for (int player = 1; player <= players; player++)
            {
                Assert.That(Open(state).InteractionTypeId, Is.EqualTo("round.enterprise.upgrade")); Assert.That(Open(state).AnsweringPlayerId, Is.EqualTo(player));
                state = Restore(state, registry); Answer(state, registry, Open(state).CandidateIds[0]);
            }
            if (players == 2)
            {
                for (int player = 1; player <= 2; player++) { Assert.That(Open(state).InteractionTypeId, Is.EqualTo(ExpansionSetupFlow.SupplyRetirementInteraction)); Answer(state, registry, Open(state).CandidateIds[0]); }
                Assert.That(In(state, "retired").Count, Is.EqualTo(2));
            }
            foreach (var p in state.Players) Assert.That(In(state, "enterprises").Sum(id => new EnterpriseStateQuery(registry.EnterpriseDefinitions).GetLevel(state, p.PlayerId, id)), Is.EqualTo(before[p.PlayerId] + 1));
            Assert.That(mainline.Advance(state).IsValid, Is.True); Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
            state = Restore(state, registry); Assert.That(mainline.Advance(state).IsValid, Is.True);
            Assert.That(Open(state)?.InteractionTypeId, Is.EqualTo(CharacterCoverEffectExecutor.InteractionTypeId));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "RoundStarted"), Is.EqualTo(1));
            foreach (var p in state.Players) Assert.That(In(state, "enterprises").Sum(id => new EnterpriseStateQuery(registry.EnterpriseDefinitions).GetLevel(state, p.PlayerId, id)), Is.EqualTo(before[p.PlayerId] + 1));
        }
        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void FormalCoreEntranceReachesCoverThroughSingleMainline(int players)
        {
            var state = State(players, out var registry); var mainline = Mainline(state, registry);
            Assert.That(mainline.StartEntrance(state).IsValid, Is.True);
            for (int count = 0; count < 80 && state.Phase != GamePhase.CharacterCover; count++)
            {
                var request = Open(state); Assert.That(request, Is.Not.Null, state.EffectRuntime.LastFaultMessage + " / " + state.Phase);
                if (request.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId && players == 2) Assert.That(request.CandidateIds, Does.Not.Contain("B-01"));
                AnswerCurrentRequest(state, registry, mainline, request,
                    request.CandidateIds.Take(Math.Max(request.MinSelections, request.InteractionTypeId == "setup.influence.optional" ? 1 : 0)).ToArray());
                var advanced = mainline.Advance(state); Assert.That(advanced.IsValid, Is.True, advanced.Reason);
            }
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(state.EffectRuntime.MainNodes.Count(n => n.NodeTypeId == RoundMainlineNodeTypeIds.RoundStarted), Is.EqualTo(1));
            Assert.That(state.Map.ResourceTokens.Count, Is.EqualTo(players));
            if (players == 2) { Assert.That(state.FindPlayer(1).Resources.GoldVoucher >= 10, Is.True); Assert.That(state.FindPlayer(2).Resources.GoldVoucher >= 18, Is.True);
                Assert.That(state.EffectRuntime.EffectNodes.Where(n => n.SourceId == "setup.initial_gold" && n.PlayerId == 2).Sum(n => Number(n.NormalizedArguments, "amount")), Is.EqualTo(18));
                Assert.That(state.FindPlayer(2).Score >= 3, Is.True); Assert.That(state.Map.Influences.Count, Is.EqualTo(1)); }
        }
        [TestCase(2, false, false)] [TestCase(2, false, true)]
        [TestCase(2, true, false)] [TestCase(2, true, true)]
        [TestCase(3, false, false)] [TestCase(3, true, false)]
        [TestCase(4, false, false)] [TestCase(4, true, false)]
        public void FormalOpeningCommandsDiscardThenReachFirstCoverWithInitialGoldOnlyOnce(int players, bool enterprise, bool placeInitialInfluence)
        {
            var config = Configuration(players, enterprise ? new[] { "core", "enterprise" } : new[] { "core" });
            config.MapId = players == 4 ? StaticMapDefinitions.FourPlayerMapId : StaticMapDefinitions.ThreePlayerMapId;
            var catalog = ActiveContentCatalogBuilder.Build(Installed(), config); config.ContentPackHash = catalog.ContentPackHash;
            var registry = new EffectRegistry(); ExpansionSetupFlow.Register(registry, upper => 0); catalog.Register(registry);
            ResourceEffectExecutor.Register(registry);
            var seats = Enumerable.Range(1, players).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "玩家" + id }).ToList();
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, seats, config, 321, catalog.SetupRecipe, catalog.ContentDefinitions);
            var mainline = Mainline(state, registry);
            Assert.That(mainline.StartEntrance(state).IsValid, Is.True);
            Assert.That(state.EffectRuntime.MainNodes.Count(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup), Is.EqualTo(1));
            var session = Session(state, registry, mainline);
            int discardedPlayers = 0, optionalInfluenceRequests = 0, submitted = 0;
            for (; submitted < 120 && state.Phase != GamePhase.CharacterCover; submitted++)
            {
                var request = Open(state); Assert.That(request, Is.Not.Null, state.EffectRuntime.LastFaultMessage);
                bool discard = request.InteractionTypeId == ExpansionSetupFlow.BasicCharacterInteraction;
                bool optionalInfluence = request.InteractionTypeId == "setup.influence.optional";
                if (request.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId && players == 2)
                    Assert.That(request.CandidateIds, Does.Not.Contain("B-01"));
                if (state.Phase == GamePhase.Entrance)
                    Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.SourceId == "setup.initial_gold"), Is.False, "入场事件与所有玩家弃牌完成前不得额外补发开局金券。");
                if (discard)
                {
                    Assert.That(enterprise, Is.True);
                    Assert.That(request.CandidateIds, Has.Count.EqualTo(7));
                    Assert.That(request.MinSelections, Is.EqualTo(2)); Assert.That(request.MaxSelections, Is.EqualTo(2));
                    var originalCandidates = request.CandidateIds.ToArray();
                    string interactionId = request.GetStableInteractionId();
                    state = Restore(state, registry); session = Session(state, registry, mainline);
                    Assert.That(mainline.Advance(state).IsValid, Is.True);
                    request = state.EffectRuntime.InteractionRequests.Single(item => item.Status == "open" &&
                        item.AnsweringPlayerId == request.AnsweringPlayerId && item.GetStableInteractionId() == interactionId);
                    Assert.That(request.CandidateIds, Is.EqualTo(originalCandidates));
                }
                if (optionalInfluence)
                {
                    optionalInfluenceRequests++;
                    Assert.That(players, Is.EqualTo(2)); Assert.That(request.AnsweringPlayerId, Is.EqualTo(2));
                    Assert.That(request.MinSelections, Is.Zero); Assert.That(request.MaxSelections, Is.EqualTo(1));
                    Assert.That(request.AllowDecline, Is.True);
                }
                var selected = request.CandidateIds.Take(optionalInfluence ? (placeInitialInfluence ? 1 : 0) : request.MinSelections).ToArray();
                Assert.That(request.StateRevision, Is.EqualTo(state.EffectRuntime.StateRevision));
                var command = AnswerCommand(request, selected, optionalInfluence && !placeInitialInfluence);
                if (request.AnswerSchema == "resource_allocation")
                {
                    string total = request.CandidateIds.Single(id => id.StartsWith("total:", StringComparison.Ordinal)).Substring(6);
                    command.OptionIds.Clear(); command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = "Originium=" + total;
                }
                else if (selected.Length == 0 && !optionalInfluence)
                    command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = bool.TrueString;
                int influenceBefore = state.Map.Influences.Count;
                var result = session.Submit(command);
                Assert.That(result.Succeeded, Is.True, request.InteractionTypeId + " / " + result.Validation.Reason);
                if (optionalInfluence) Assert.That(state.Map.Influences.Count, Is.EqualTo(influenceBefore + (placeInitialInfluence ? 1 : 0)));
                if (discard)
                {
                    discardedPlayers++;
                    Assert.That(In(state, "secret_retired"), Is.SupersetOf(selected));
                    Assert.That(In(state, "hand", request.AnsweringPlayerId), Does.Not.Contain(selected[0]));
                    var task = state.EffectRuntime.EffectNodes.Single(node => node.EffectTypeId == RoundExecutionService.PlayerEntranceTaskTypeId &&
                        node.PlayerId == request.AnsweringPlayerId);
                    Assert.That(task.Status, Is.EqualTo(EffectNodeStatus.Completed));
                    Assert.That(task.FlowStage, Is.EqualTo("finished"));
                    if (state.Phase == GamePhase.Entrance)
                    {
                        var picker = state.EffectRuntime.InteractionRequests.SingleOrDefault(item => item.Status == "open" &&
                            item.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId);
                        Assert.That(state.CurrentPlayerId, Is.EqualTo(picker?.AnsweringPlayerId ?? -1));
                    }
                }
            }
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover)); Assert.That(state.CurrentPlayerId, Is.EqualTo(-1));
            Assert.That(state.EffectRuntime.InteractionRequests.Where(item => item.Status == "open" &&
                item.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId).Select(item => item.AnsweringPlayerId),
                Is.EquivalentTo(Enumerable.Range(1, players)));
            Assert.That(state.EffectRuntime.MainNodes.Single(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup).Status,
                Is.EqualTo(EffectNodeStatus.Completed));
            var entranceTasks = state.EffectRuntime.EffectNodes.Where(node => node.EffectTypeId == RoundExecutionService.PlayerEntranceTaskTypeId).ToArray();
            Assert.That(entranceTasks, Has.Length.EqualTo(players));
            Assert.That(entranceTasks.Select(node => node.Status),
                Has.All.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.MapId, Is.EqualTo(config.MapId));
            Assert.That(discardedPlayers, Is.EqualTo(enterprise ? players : 0));
            Assert.That(optionalInfluenceRequests, Is.EqualTo(players == 2 ? 1 : 0));
            Assert.That(In(state, "secret_retired"), Has.Count.EqualTo(enterprise ? players * 2 : 0));
            foreach (var player in state.Players) Assert.That(CharacterLifecycleService.GetRetainedBasicCharacterCount(state, player.PlayerId), Is.EqualTo(5));
            var expectedGold = players == 2 ? new[] { 10, 18 } : players == 3 ? new[] { 10, 12, 18 } : new[] { 10, 12, 14, 18 };
            var initialGoldNodes = state.EffectRuntime.EffectNodes.Where(n => n.SourceId == "setup.initial_gold").ToList();
            Assert.That(initialGoldNodes, Has.Count.EqualTo(players));
            for (int player = 1; player <= players; player++)
            {
                var grant = initialGoldNodes.Single(n => n.PlayerId == player);
                Assert.That(grant.Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(Number(grant.NormalizedArguments, "amount"), Is.EqualTo(expectedGold[player - 1]));
            }
            var goldBeforeRecovery = state.Players.Select(p => p.Resources.GoldVoucher).ToArray();
            var scoresBeforeRecovery = state.Players.Select(p => p.Score).ToArray();
            state = Restore(state, registry);
            Assert.That(mainline.Advance(state).IsValid, Is.True);
            Assert.That(state.Players.Select(p => p.Resources.GoldVoucher), Is.EqualTo(goldBeforeRecovery));
            Assert.That(state.Players.Select(p => p.Score), Is.EqualTo(scoresBeforeRecovery));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.SourceId == "setup.initial_gold"), Is.EqualTo(players));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(-1));
            Console.WriteLine("正式开局：" + (enterprise ? "企业" : "基础") + "，人数=" + players + "，后手影响力=" + placeInitialInfluence +
                "，受理命令=" + submitted + "，弃牌玩家=" + discardedPlayers + "，初始金券=" + string.Join("/", expectedGold) +
                "，最终阶段=" + state.Phase + "，当前玩家=" + state.CurrentPlayerId + "，恢复后金券/分数未重复增加。");
        }
        [Test]
        public void RetiredCardsAreHiddenFromEveryPlayerIncludingOriginalHolder()
        {
            var state = State(3, out var registry, 8); Run(state, registry, ExpansionSetupFlow.RetentionSpec(1)); Answer(state, registry, Open(state).CandidateIds.Take(3).ToArray());
            var secrets = In(state, "secret_retired");
            foreach (int viewer in Enumerable.Range(1, 3))
            {
                string projected = JsonConvert.SerializeObject(GameStateViewProjector.ProjectForPlayer(state, viewer));
                foreach (string id in secrets) Assert.That(projected, Does.Not.Contain(id));
            }
        }
        [Test]
        public void ExtraCollectionLeavesPhaseNormalQualificationAndStageGoldUntouched()
        {
            var state = State(3, out var registry); state.Phase = GamePhase.ActionRound1; state.CurrentPlayerId = 1;
            state.FindPlayer(1).Resources.GoldVoucher = 7; state.FindPlayer(1).ResourceCollectionStartGoldVoucher = 3; state.FindPlayer(1).HasCollectedResourcesThisRound = true;
            var node = Run(state, registry, EffectSpec.Create(ResourceCollectionEffectExecutor.TypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "scope", NormalizedValue.CreateString("extra")), 1));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(7)); Assert.That(state.FindPlayer(1).ResourceCollectionStartGoldVoucher, Is.EqualTo(3)); Assert.That(state.FindPlayer(1).HasCollectedResourcesThisRound, Is.True);
            Assert.That(Value(node.ContinuationData, "collectionStartGoldVoucherAmounts").Items.Count, Is.EqualTo(3));
        }
        [Test]
        public void ExtraCollectionRewardsRealEntranceTokenAndResumesWithoutDoubleReward()
        {
            var state = State(3, out var registry); var mainline = Mainline(state, registry);
            Assert.That(mainline.StartEntrance(state).IsValid, Is.True);
            while (state.Phase != GamePhase.CharacterCover)
            {
                var request = Open(state); Assert.That(request, Is.Not.Null);
                Answer(state, registry, request.CandidateIds.Take(request.MinSelections).ToArray()); Assert.That(mainline.Advance(state).IsValid, Is.True);
            }
            state.Phase = GamePhase.ActionRound2; state.FindPlayer(1).HasCollectedResourcesThisRound = true;
            state.FindPlayer(1).ResourceCollectionStartGoldVoucher = 0;
            string location = state.FindPlayer(1).CityLocationId;
            var token = state.Map.ResourceTokens.Single(t => t.LocationId == location);
            int before = state.FindPlayer(1).Resources.Get(token.ResourceType);
            var spec = EffectSpec.Create(ResourceCollectionEffectExecutor.TypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "scope", NormalizedValue.CreateString("extra")), 1);
            var node = Run(state, registry, spec); Assert.That(Open(state).CandidateIds, Does.Contain(location));
            state = Restore(state, registry); Answer(state, registry, location);
            Assert.That(new EffectTreeExecutor(state, registry).GetNode(node.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Resources.Get(token.ResourceType), Is.EqualTo(before + token.Amount));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound2)); Assert.That(state.FindPlayer(1).HasCollectedResourcesThisRound, Is.True);
            Assert.That(state.FindPlayer(1).ResourceCollectionStartGoldVoucher, Is.Zero);
            var resources = JsonConvert.SerializeObject(state.FindPlayer(1).Resources);
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent();
            Assert.That(JsonConvert.SerializeObject(state.FindPlayer(1).Resources), Is.EqualTo(resources));
        }
        [Test]
        public void CollectArgumentsRejectVersionAndGlobalPhaseVariants()
        {
            Assert.That(ResourceCollectionEffectExecutor.ValidateArguments(Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "scope", NormalizedValue.CreateString("extra"))), Is.Empty);
            Assert.That(ResourceCollectionEffectExecutor.ValidateArguments(Object("apiVersion", NormalizedValue.CreateInteger(2), "player", NormalizedValue.CreateString("1"), "scope", NormalizedValue.CreateString("extra"))), Is.Not.Empty);
            Assert.That(ResourceCollectionEffectExecutor.ValidateArguments(Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "scope", NormalizedValue.CreateString("normal"))), Is.Not.Empty);
        }
        [Test]
        public void LuaCollectCompilesCanonicalPlayerAndRunsThroughRegisteredExecutor()
        {
            const string source = "return function(ctx) return { Effect.Collect({apiVersion=1,player=ctx.playerId,scope='extra'}) } end";
            var script = new LuaScriptDefinition("test.collection", "test.collection", "test.collection", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var invoked = new MoonSharpLuaRuntimeHost().Invoke(script, new LuaInvocationContext("test.event", "test.event", "p1", 0, "1.0.0", 3, 0, 0));
            Assert.That(invoked.IsSuccess, Is.True, invoked.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invoked, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            Assert.That(Text(specs.Single().NormalizedArguments, "player"), Is.EqualTo("1"));
            var state = State(3, out var registry); Assert.That(Run(state, registry, specs.Single()).Status, Is.EqualTo(EffectNodeStatus.Completed));
        }
        [Test]
        public void DeclarationPublishesCommittedFacilityInstancesAndMarkerExactlyOnce()
        {
            var state = State(3, out var registry, allCityStyles: true); state.Phase = GamePhase.ActionRound1; state.CurrentPlayerId = 1;
            registry.Register("test.prepare_declaration", context =>
            {
                foreach (var placement in new[] { new { Id = "building_007", Cell = 0 }, new { Id = "building_032", Cell = 1 } })
                {
                    var item = context.State.ItemZones.Items.Single(i => i.DefinitionId == placement.Id);
                    new ItemTransferService().RelocateItems(context, new ItemTransferRequest { ExecutingPlayerId = 1, ItemIds = new List<string> { item.ItemId }, ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId },
                        Destination = new ZoneRef { ZoneId = FindZone(context.State, "city_cell_" + placement.Cell, 1).ZoneId }, InsertionKind = "unordered", RequiredCount = 1, OperationProfileId = "core.facility_card_transfer", CarrierPolicyId = "preserve" });
                }
                FacilityInstanceStateService.EnsureAll(context.State);
                return EffectStepResult.Completed();
            });
            Assert.That(Run(state, registry, EffectSpec.Create("test.prepare_declaration", playerId: 1)).Status, Is.EqualTo(EffectNodeStatus.Completed));
            var validation = new DeclareCityStyleService().Validate(state, 1, CityStyleDatabase.MilitaryIndustrialArea, new[] { 0, 1 });
            Assert.That(validation.IsValid, Is.True, validation.Reason + " / " + string.Join(";", state.Map.Facilities.Select(f => f.PlayerId + ":" + f.CityBoardSlotIndex + ":" + f.FacilityCardId)));
            int observed = 0;
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.declaration.observer", "CityStyleDeclared", "*", "test.declaration.observer", context => {
                observed++; var declaration = context.State.FindPlayer(1).DeclaredCityStyles.Single();
                Assert.That(context.State.FindPlayer(1).Score, Is.EqualTo(2)); Assert.That(context.State.FindPlayer(1).InfluenceSupply, Is.EqualTo(28));
                Assert.That(Value(context.Event.Payload, "usedFacilityItemIds").Items.Select(v => v.StringValue), Is.EquivalentTo(declaration.UsedFacilityIds));
                Assert.That(declaration.UsedFacilityIds.All(id => context.State.Map.Facilities.Any(f => f.ContentInstanceId == id && f.PlayerId == 1)), Is.True);
                return new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(1, 1) };
            }));
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(CityStyleDeclarationEffectExecutor.Create(1, CityStyleDatabase.MilitaryIndustrialArea, new[] { 0, 1 }));
            for (int count = 0; count < 20 && state.FindPlayer(1).DeclaredCityStyles.Count == 0; count++) Assert.That(engine.Advance(), Is.True,
                state.EffectRuntime.LastFaultMessage + " / " + string.Join(";", state.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == CityStyleDeclarationEffectExecutor.TypeId).Select(n => n.FailureReason)));
            state = Restore(state, registry); Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(observed, Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(3));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "CityStyleDeclared"), Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).DeclaredCityStyles.Single().MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Unused));
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(observed, Is.EqualTo(1));
        }
        [Test]
        public void BasicLimitIncludesDiscardButExcludesOperatorsAndGeneratedCards()
        {
            var state = State(3, out var registry); Run(state, registry, ExpansionSetupFlow.RetentionSpec(1));
            var card = In(state, "hand", 1)[0]; Run(state, registry, MoveSpec(1, new[] { card }, FindZone(state, "hand", 1).ZoneId, FindZone(state, "discard", 1).ZoneId, "unordered", 1, "core.character_card_transfer"));
            registry.Register("test.grant_non_basic", context => {
                foreach (string type in new[] { "core.operator_card", "core.generated_character_card" })
                    if (!new ItemZoneStateFactory().TryCreateGrantedItem(context.State, context.Node.EffectId, type, "liskarm", type, FindZone(context.State, "hand", 1).ZoneId, ItemSubjectRef.Player(1), out _, out string reason)) throw new InvalidOperationException(reason);
                return EffectStepResult.Completed();
            });
            Run(state, registry, EffectSpec.Create("test.grant_non_basic", playerId: 1));
            Assert.That(CharacterLifecycleService.GetRetainedBasicCharacterCount(state, 1), Is.EqualTo(5));
            registry.Register("test.grant_sixth_basic", context => {
                if (!new ItemZoneStateFactory().TryCreateGrantedItem(context.State, context.Node.EffectId, "new_basic", "liskarm", "core.basic_character_card", FindZone(context.State, "hand", 1).ZoneId, ItemSubjectRef.Player(1), out _, out string reason)) throw new InvalidOperationException(reason);
                return EffectStepResult.Completed();
            });
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(EffectSpec.Create("test.grant_sixth_basic", playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.True); Assert.That(state.EffectRuntime.LastFaultMessage, Does.Contain("basic_character_limit_exceeded"));
            Assert.That(CharacterLifecycleService.GetRetainedBasicCharacterCount(state, 1), Is.EqualTo(5));
        }
        [Test]
        public void RoundCleanupRecyclesUnusedCoverAndDiscardThroughLifecycle()
        {
            var state = State(3, out var registry); Mainline(state, registry); Run(state, registry, ExpansionSetupFlow.RetentionSpec(1));
            Run(state, registry, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(state, registry, Open(state).CandidateIds[0]);
            string discarded = In(state, "hand", 1)[0];
            Assert.That(Run(state, registry, MoveSpec(1, new[] { discarded }, FindZone(state, "hand", 1).ZoneId, FindZone(state, "discard", 1).ZoneId, "unordered", 1, "core.character_card_transfer")).Status, Is.EqualTo(EffectNodeStatus.Completed));
            var recycled = Run(state, registry, EffectSpec.Create(RoundExecutionService.PlayerCharacterRecyclingTypeId, playerId: 1));
            Assert.That(recycled.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(In(state, "hand", 1).Count, Is.EqualTo(5));
            Assert.That(In(state, "covered", 1), Is.Empty); Assert.That(In(state, "discard", 1), Is.Empty);
            state = Restore(state, registry); new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(In(state, "hand", 1).Count, Is.EqualTo(5));
        }
        [TestCase(0)] [TestCase(-1)]
        public void BuildingNonPositiveScoreFacilityPreservesScoreBeforeEntryEffect(int expectedScore)
        {
            var state = State(3, out var registry); state.Phase = GamePhase.ActionRound1; state.CurrentPlayerId = 1; state.FindPlayer(1).Resources.GoldVoucher = 100;
            FacilityBuildEffectExecutor.Register(registry); FacilityEntryEffectExecutor.Register(registry);
            string facilityId = state.ContentDefinitions.Facilities.First(f => f.Score == expectedScore && !f.ReserveOnly).FacilityId;
            string facilityItemId = state.ItemZones.Items.Single(i => i.DefinitionId == facilityId).ItemId;
            var built = Run(state, registry, FacilityBuildEffectSpecFactory.Build(1, facilityItemId, 0, BuildFacilityService.PaymentModeGold, false, "test.non_positive_score", false));
            Assert.That(built.Status, Is.EqualTo(EffectNodeStatus.Completed).Or.EqualTo(EffectNodeStatus.Blocked), built.FailureReason);
            Assert.That(state.Map.Facilities.Any(f => f.PlayerId == 1 && f.CityBoardSlotIndex == 0 && f.FacilityCardId == facilityId), Is.True);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(expectedScore));
        }
        [Test]
        public void AtomicBasicExchangeValidatesFinalCountAndKeepsIntermediateCountObservable()
        {
            var state = State(3, out var registry); Run(state, registry, ExpansionSetupFlow.RetentionSpec(1));
            registry.Register("test.exchange_basics", context => {
                string outgoing = In(context.State, "hand", 1)[0];
                Assert.That(new ItemZoneStateFactory().TryCreateGrantedItem(context.State, context.Node.EffectId, "replacement_basic", "liskarm", "core.basic_character_card", FindZone(context.State, "hand", 1).ZoneId, ItemSubjectRef.Player(1), out _, out string reason), Is.True, reason);
                Assert.That(CharacterLifecycleService.GetRetainedBasicCharacterCount(context.State, 1), Is.EqualTo(6));
                new ItemTransferService().RelocateItems(context, new ItemTransferRequest { ExecutingPlayerId = 1, ItemIds = new List<string> { outgoing }, ExpectedSource = new ItemPositionRef { ZoneId = FindZone(context.State, "hand", 1).ZoneId }, Destination = new ZoneRef { ZoneId = FindZone(context.State, "secret_retired").ZoneId },
                    InsertionKind = "unordered", OperationProfileId = "core.character_card_transfer", CarrierPolicyId = "preserve", RequiredCount = 1 });
                return EffectStepResult.Completed();
            });
            Run(state, registry, EffectSpec.Create("test.exchange_basics", playerId: 1)); Assert.That(CharacterLifecycleService.GetRetainedBasicCharacterCount(state, 1), Is.EqualTo(5));
        }
        private static ExternalContentPack TestPack()
        {
            // 只克隆内存测试目录，不写正式manifest或正式内容文件。
            return (ExternalContentPack)typeof(ExternalContentPack).GetMethod("CreateActiveSnapshot", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Installed(), new object[] { new[] { "core" }, "" });
        }
        [Test]
        public void HostRecoveryRejectsMissingFrozenContentAndRecipeTampering()
        {
            var state = State(3, out var registry);
            var archive = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            ((JObject)archive["Snapshot"]["State"]).Remove("SetupRecipe");
            Assert.That(HostRecoveryService.RecoverJson(archive.ToString(), registry).Diagnostic, Does.Contain("content_snapshot_missing"));
            archive = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            archive["Snapshot"]["State"]["SetupRecipe"]["OperatorDrawCount"] = 99;
            Assert.That(HostRecoveryService.RecoverJson(archive.ToString(), registry).Diagnostic, Does.Contain("setup_recipe_restore_mismatch"));
        }
        [Test]
        public void UnsupportedCapabilitiesAndDeclaredConflictsBlockLaunch()
        {
            var pack = TestPack(); var manifest = pack.Manifest; manifest["requiredCapabilities"] = new JArray("missing.rules.v1"); typeof(ExternalContentPack).GetProperty("Manifest").SetValue(pack, manifest);
            Assert.That(() => ActiveContentCatalogBuilder.Build(pack, Configuration()), Throws.InvalidOperationException.With.Message.EqualTo("content_not_ready"));
            pack = TestPack(); manifest = pack.Manifest;
            manifest["expansionDependencies"] = new JArray(new JObject { ["expansionId"] = "enterprise", ["requires"] = new JArray("core"), ["conflicts"] = new JArray("core") }); typeof(ExternalContentPack).GetProperty("Manifest").SetValue(pack, manifest);
            Assert.That(() => ActiveContentCatalogBuilder.Build(pack, Configuration(3, "core", "enterprise")), Throws.InvalidOperationException.With.Message.EqualTo("expansion_conflict"));
        }
        private static void AddReplacement(ExternalContentPack pack, string id, bool enabled = true, bool changeRules = true)
        {
            var source = pack.Definitions.Single(d => d.DefinitionId == "event_red_03");
            var leaf = JsonConvert.DeserializeObject<ExternalContentDefinition>(JsonConvert.SerializeObject(source)); leaf.DefinitionId = id; leaf.Enabled = enabled;
            leaf.Replaces = new ExternalReplacementReference { DefinitionId = "event_red_03", ContentType = "event", ExpansionId = "core" };
            if (changeRules) leaf.Data["choiceRewards"][1]["goldVoucher"] = 9;
            ((List<ExternalContentDefinition>)typeof(ExternalContentPack).GetField("definitions", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pack)).Add(leaf);
        }
        [Test]
        public void EffectiveReplacementKeepsOneRuntimeSlotAndActualDefinitionId()
        {
            var pack = TestPack(); AddReplacement(pack, "test.event.replacement"); var catalog = ActiveContentCatalogBuilder.Build(pack, Configuration());
            Assert.That(catalog.Definitions.Single(d => d.RuntimeId == "event_red_03").DefinitionId, Is.EqualTo("test.event.replacement"));
            Assert.That(catalog.SetupRecipe.Entries.Any(e => e.DefinitionId == "test.event.replacement"), Is.True);
            Assert.That(catalog.ContentDefinitions.Events.Single(e => e.CardId == "test.event.replacement").ChoiceRewards[1].GoldVoucher, Is.EqualTo(9));
        }
        [Test]
        public void DisabledReplacementDoesNotChangeFingerprintOrSubscribe()
        {
            var pack = TestPack(); AddReplacement(pack, "test.event.disabled", false);
            var catalog = ActiveContentCatalogBuilder.Build(pack, Configuration());
            Assert.That(catalog.ContentPackHash, Is.EqualTo(ActiveContentCatalogBuilder.Build(Installed(), Configuration()).ContentPackHash));
            var registry = new EffectRegistry(); catalog.Register(registry); Assert.That(registry.HasEventHandler("lua:event-card:test.event.disabled"), Is.False);
        }
        [Test]
        public void ConflictingReplacementsBlockCatalog()
        {
            var pack = TestPack(); AddReplacement(pack, "test.event.replacement_a"); AddReplacement(pack, "test.event.replacement_b");
            Assert.That(() => ActiveContentCatalogBuilder.Build(pack, Configuration()), Throws.InvalidOperationException.With.Message.EqualTo("replacement_conflict"));
        }
        [Test]
        public void ArtworkReplacementCannotPretendToReplaceEventRules()
        {
            var pack = TestPack(); AddReplacement(pack, "test.event.artwork_only", changeRules: false);
            Assert.That(() => ActiveContentCatalogBuilder.Build(pack, Configuration()), Throws.InvalidOperationException.With.Message.EqualTo("replacement_rules_missing"));
        }
    }
}
