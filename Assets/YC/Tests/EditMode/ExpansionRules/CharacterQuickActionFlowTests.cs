using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
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
using YC.Presentation.Workflows;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class CharacterQuickActionFlowTests
    {
        private ExternalContentPack pack;
        private EffectRegistry registry;
        private RoundExecutionService rounds;
        private readonly CharacterCardPanelPresenter presenter = new CharacterCardPanelPresenter();

        [OneTimeSetUp] public void LoadDefinitions()
        {
            pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            CharacterCardDatabase.Initialize(pack.CreateCharacters());
        }

        // 行为完整测试能力：两模式分别经过真实待选再记分；不能靠最终资源掩盖提前启动。
        private GameState Prepare(int round = 1, int start = 1, bool nonFirst = false, string strategyType = "normal", bool source = false, bool completionWait = false, bool partialSecond = false)
        {
            registry = new EffectRegistry(); rounds = new RoundExecutionService(registry);
            ResourceEffectExecutor.Register(registry); CharacterLifecycleService.Register(registry);
            registry.Register("test.character.choice", context => string.IsNullOrEmpty(context.Node.FlowStage)
                ? EffectStepResult.Continue("answered").AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = "character.ability.choice", AnsweringPlayerId = context.Node.PlayerId,
                    Visibility = "owner", MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_id", CandidateIds = { "confirm" }
                }) : EffectStepResult.Completed(), EffectExecutorKind.IntrinsicFlow);
            var definition = CharacterCardDatabase.Get("texas");
            foreach (string mode in new[] { "strategy", "tactic" })
            {
                bool tactic = mode == "tactic";
                string ability = tactic ? definition.TacticAbilityId : definition.StrategyAbilityId;
                registry.CharacterActivationSubscriptions[ability] = "test.quick." + ability;
                registry.CharacterAbilityContracts[ability] = new CharacterModeContract { Mode = mode, AbilityId = ability,
                    SkillType = tactic && source ? "persistent" : tactic && partialSecond ? "one_shot" : tactic ? "normal" : strategyType,
                    SettlementActionId = tactic && source ? CharacterSettlementOptionQuery.DoubleUseActionId : "" };
                registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.quick." + ability,
                    CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType, ability, "test.quick." + ability,
                    context => tactic && partialSecond ? new List<EffectSpec> {
                        ResourceEffectSpecFactory.Gain(context.Event.PlayerId, ResourceType.GoldVoucher, 10),
                        ResourceEffectSpecFactory.Pay(context.Event.PlayerId, ResourceType.GoldVoucher, 3),
                        EffectSpec.Create("test.character.choice", playerId: context.Event.PlayerId),
                        ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 30) } :
                        new List<EffectSpec> { EffectSpec.Create("test.character.choice", playerId: context.Event.PlayerId),
                        ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, tactic ? 3 : 2) }, EffectHandlerRole.Primary));
            }
            if (completionWait) registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.quick.completion",
                "EffectCompleted", "*", "test.quick.completion",
                context => context.OwnerNode.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId
                    ? new List<EffectSpec> { EffectSpec.Create("test.character.choice", playerId: context.Event.PlayerId) } : new List<EffectSpec>(), EffectHandlerRole.Observer));
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var state = new ItemZoneStateFactory().CreateForSetup(new GameState {
                GameId = "game.character_quick", MapId = map.MapId, ContentPackHash = pack.ContentHash,
                Round = round, StartPlayerId = start, MaxRounds = 8,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 3, ContentPackHash = pack.ContentHash },
                Players = Enumerable.Range(1, 3).Select(id => new PlayerState { PlayerId = id, Name = "玩家" + id }).ToList()
            }, map, new[] { new ItemRecipeEntry { EntryId = "characters", DefinitionId = "texas", TypeId = "core.basic_character_card",
                Quantity = 2, PerPlayer = true, InitialZoneRole = "hand" } });
            Assert.That(rounds.CreateRound(state, round, start).IsValid, Is.True);
            for (int guard = 0; state.Phase == GamePhase.CharacterCover && guard < 12; guard++)
            {
                var command = presenter.CreateCoverCommand(state.CurrentPlayerId, state.FindPlayer(state.CurrentPlayerId).HandCardIds[0]);
                Assert.That(new CoverCharacterCardCommandHandler(new CharacterCardService(), registry).Handle(state, command).Succeeded, Is.True);
            }
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
            if (nonFirst) Assert.That(rounds.CompleteMainAction(state, start).IsValid, Is.True);
            return state;
        }
        private GameSession Session(GameState state)
        {
            var session = new GameSession(state);
            session.RegisterHandler(new UseCharacterCardCommandHandler(new CharacterCardService(), registry));
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry));
            session.RegisterHandler(new InvokeSettlementOptionCommandHandler(registry));
            return session;
        }
        private CharacterUseRuntimeState Current(GameState state) => state.CharacterUses.Last();
        private InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.Last(request => request.Status == "open" && request.InteractionTypeId != RoundExecutionService.MainlineCompletionInteractionTypeId);
        private string Zone(GameState state, string card) => state.ItemZones.Zones.Single(zone => zone.ZoneId == state.ItemZones.Items.Single(item => item.ItemId == card).ZoneId).TemplateId;
        private void Run(GameState state)
        {
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(CharacterLifecycleService.TryValidate(state, out string error), Is.True, error);
        }
        private void Answer(GameState state, string choice)
        {
            var request = Open(state); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                NormalizedValue.CreateString(choice), out string error), Is.True, error);
            Run(state);
        }
        private GameState Restore(GameState state)
        {
            var result = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic); return result.State;
        }
        private GameCommand Begin(GameState state)
        {
            var view = presenter.BuildView(state, state.CurrentPlayerId);
            var command = presenter.CreateBeginUseCommand(state.CurrentPlayerId, view.CoveredCardId);
            Assert.That(Session(state).Submit(command).Succeeded, Is.True);
            return command;
        }

        [TestCase(1, 1, "strategy")][TestCase(2, 2, "tactic")][TestCase(3, 3, "strategy")]
        public void StartPlayerContinuesOnSameUseAfterWholeFirstSubtreeAndRecovery(int round, int start, string first)
        {
            var state = Prepare(round, start, completionWait: true);
            string window = state.EffectRuntime.ActiveMainNodeId;
            int actions = state.FindPlayer(start).RemainingMainActionsThisTurn;
            Begin(state); var use = Current(state); string useId = use.UseId;
            var owner = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == use.OwnerEffectId);
            var quick = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == owner.ParentEffectId);
            Assert.That(quick.EffectTypeId, Is.EqualTo(CharacterLifecycleService.QuickActionTypeId));
            Assert.That(quick.ParentEffectId, Is.EqualTo(state.EffectRuntime.MainNodes.Single(node => node.NodeId == window).ExecutionEffectId));
            Assert.That(owner.ChildEffectIds, Is.Empty); Assert.That(quick.ChildSchedulingGroups, Is.Empty);
            Answer(state, first);
            Assert.That(Current(state).CompletedModes, Is.Empty);
            Assert.That(state.EffectRuntime.EffectNodes.Count(node => node.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId), Is.EqualTo(1));
            state = Restore(state); Run(state); Answer(state, "confirm");
            Assert.That(Current(state).CompletedModes, Is.EqualTo(new[] { first }));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("character.ability.choice"), "完成响应仍须先结束");
            Assert.That(state.EffectRuntime.EffectNodes.Count(node => node.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId), Is.EqualTo(1));
            Answer(state, "confirm");
            string second = first == "strategy" ? "tactic" : "strategy";
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("character.ability.choice"));
            Assert.That(Zone(state, use.CardInstanceId), Is.EqualTo("core.zone.character_covered"));
            int score = state.FindPlayer(start).Score;
            var requestId = Open(state).GetStableInteractionId();
            state = Restore(state); Run(state);
            Assert.That(Open(state).GetStableInteractionId(), Is.EqualTo(requestId)); Assert.That(state.FindPlayer(start).Score, Is.EqualTo(score));
            var projected = presenter.BuildView(state, start);
            Assert.That(projected.CurrentUseId, Is.EqualTo(useId));
            Assert.That(projected.CurrentUseSelection, Is.Null);
            Answer(state, "confirm"); Answer(state, "confirm");
            Assert.That(Current(state).UseId, Is.EqualTo(useId)); Assert.That(Current(state).CompletedModes, Is.EqualTo(new[] { first, second }));
            Assert.That(state.CharacterUses.Count, Is.EqualTo(1)); Assert.That(state.FindPlayer(start).Score, Is.EqualTo(5));
            Assert.That(Zone(state, use.CardInstanceId), Is.EqualTo("core.zone.character_discard"));
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.EventType == "CharacterUseFinished"), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.ActiveMainNodeId, Is.EqualTo(window)); Assert.That(state.FindPlayer(start).RemainingMainActionsThisTurn, Is.EqualTo(actions));
            state = Restore(state); string snapshot = JsonConvert.SerializeObject(state); Run(state);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(snapshot));
        }

        [TestCase("normal", "core.zone.character_discard")][TestCase("persistent", "core.zone.character_persistent")][TestCase("one_shot", "core.zone.box_retired")]
        public void ExplicitFinishUsesOnlyCompletedModeLifecycle(string type, string destination)
        {
            var state = Prepare(strategyType: type); Begin(state); Answer(state, "strategy"); Answer(state, "confirm");
            Assert.That(Current(state).Finished, Is.False);
            var request = Open(state);
            Assert.That(new EffectTreeExecutor(state, registry).TryStopCharacterMode(state.CurrentPlayerId, Current(state).UseId, request.GetStableInteractionId(), request.StateRevision, "stop.mode", out _), Is.True); Run(state);
            Assert.That(Current(state).Finished, Is.True); Assert.That(Current(state).CompletedModes, Is.EqualTo(new[] { "strategy" }));
            Assert.That(Zone(state, Current(state).CardInstanceId), Is.EqualTo(destination)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2));
        }

        [Test] public void NonFirstCannotForgeContinuationPermission()
        {
            var state = Prepare(nonFirst: true); Begin(state);
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(new[] { "strategy", "tactic", "cancel" }));
            Assert.That(Current(state).OfferSecondEffect, Is.True); Assert.That(Current(state).CanOfferSecondEffect, Is.False);
            Answer(state, "strategy"); Answer(state, "confirm");
            Assert.That(Current(state).Finished, Is.True); Assert.That(Current(state).CompletedModes, Is.EqualTo(new[] { "strategy" }));
        }

        [TestCase("strategy-first")][TestCase("tactic-first")]
        public void ExplicitBothFreezesOrderAndDoesNotOfferAnotherDecision(string order)
        {
            var state = Prepare(); Begin(state); Answer(state, "both"); Answer(state, order);
            Answer(state, "confirm"); state = Restore(state); Run(state); Answer(state, "confirm");
            Assert.That(Current(state).Finished, Is.True);
            Assert.That(Current(state).CompletedModes, Is.EqualTo(order == "strategy-first" ? new[] { "strategy", "tactic" } : new[] { "tactic", "strategy" }));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(request => request.InteractionTypeId == CharacterLifecycleService.SecondModeInteractionTypeId), Is.False);
        }

        [TestCase(false)][TestCase(true)] public void CancelBeforeActivationReleasesOnlyThisUseAndNewBeginCreatesNewUse(bool chooseOrder)
        {
            var state = Prepare(); string window = state.EffectRuntime.ActiveMainNodeId;
            int actions = state.FindPlayer(1).RemainingMainActionsThisTurn; var begin = Begin(state); string id = Current(state).UseId;
            if (chooseOrder) Answer(state, "both");
            var request = Open(state); Answer(state, "cancel");
            Assert.That(Current(state).SelectionCancelled && Current(state).Finished, Is.True);
            Assert.That(state.FindPlayer(1).UsedCharacterThisRound || state.FindPlayer(1).UsedCharacterThisTurn, Is.False);
            Assert.That(Zone(state, Current(state).CardInstanceId), Is.EqualTo("core.zone.character_covered"));
            Assert.That(state.EffectRuntime.RuleEvents.Any(value => value.EventType == "CharacterUseFinished"), Is.False);
            state = Restore(state); Run(state);
            var handler = new UseCharacterCardCommandHandler(new CharacterCardService(), registry);
            int count = state.EffectRuntime.EffectNodes.Count;
            Assert.That(handler.Handle(state, begin).Succeeded, Is.False, "旧 begin 不能在取消后重放");
            Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(count));
            Assert.That(new EffectTreeExecutor(state, registry).TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString("strategy"), out _), Is.False);
            Begin(state); Assert.That(Current(state).UseId, Is.Not.EqualTo(id));
            Assert.That(state.CharacterUses.Count(use => !use.Finished), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.ActiveMainNodeId, Is.EqualTo(window)); Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(actions));
        }

        [Test] public void RepeatedOpenProjectionAndDuplicateBeginDoNotCreateSecondTree()
        {
            var state = Prepare(); var begin = Begin(state); var before = JsonConvert.SerializeObject(state);
            var first = presenter.BuildView(state, 1); var second = presenter.BuildView(state, 1);
            Assert.That(first.CurrentUseId, Is.EqualTo(second.CurrentUseId)); Assert.That(first.CurrentUseSelection.InteractionId, Is.EqualTo(second.CurrentUseSelection.InteractionId));
            Assert.That(new UseCharacterCardCommandHandler(new CharacterCardService(), registry).Handle(state, begin).Succeeded, Is.False);
            Assert.That(Session(state).Submit(presenter.CreateBeginUseCommand(1, first.CoveredCardId)).Succeeded, Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }

        [Test] public void ActionRoundPermissionAllowsNonFirstSequentialUseButLockStillRejectsBegin()
        {
            var state = Prepare(nonFirst: true); int player = state.CurrentPlayerId;
            var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(EffectSpec.Create(LuaDomainEffectTypeIds.SetCharacterDoubleUseRule,
                Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(player.ToString()),
                    "scope", NormalizedValue.CreateString("action_round"), "expiresActionRoundSequence", NormalizedValue.CreateInteger(CharacterLifecycleService.GetActionRoundSequence(state))), player));
            Run(state); state.FindPlayer(player).CharacterCardLockedThisTurn = true;
            var card = state.FindPlayer(player).CoveredCharacterCardId;
            Assert.That(Session(state).Submit(presenter.CreateBeginUseCommand(player, card)).Succeeded, Is.False);
            state.FindPlayer(player).CharacterCardLockedThisTurn = false; Begin(state); Answer(state, "strategy"); Answer(state, "confirm");
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("character.ability.choice"));
            Answer(state, "confirm"); Assert.That(Current(state).Finished, Is.True);
        }

        [Test] public void LegacyUseKeepsSingleModeCompletionAcrossRecovery()
        {
            var state = Prepare(); string card = state.FindPlayer(1).CoveredCharacterCardId;
            new EffectTreeExecutor(state, registry).CreateRoot(CharacterLifecycleService.CreateUseSpec(1, card, "strategy")); Run(state);
            Assert.That(Current(state).SelectionProtocolVersion, Is.EqualTo(1)); state = Restore(state); Answer(state, "confirm");
            Assert.That(Current(state).Finished, Is.True); Assert.That(Current(state).CompletedModes, Is.EqualTo(new[] { "strategy" }));
        }

        private GameState PrepareInsertion()
        {
            var state = Prepare(nonFirst: true, source: true); int player = state.CurrentPlayerId;
            // 测试持续来源使用真实生命周期进入持续区，再通过正式盖牌Effect开启下一张。
            var sourceCard = state.FindPlayer(player).CoveredCharacterCardId;
            new EffectTreeExecutor(state, registry).CreateRoot(CharacterLifecycleService.CreateUseSpec(player, sourceCard, "tactic")); Run(state); Answer(state, "confirm");
            var card = state.ItemZones.Items.First(item => item.ZoneId == FindZone(state, "hand", player).ZoneId).ItemId;
            new EffectTreeExecutor(state, registry).CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: player)); Run(state); Answer(state, card);
            Begin(state); return state;
        }

        [Test] public void PaidInsertionRestoresConsumptionOnSameUseAndRemovesCancellation()
        {
            var state = PrepareInsertion(); int player = state.CurrentPlayerId; var use = Current(state);
            var old = Open(state); var option = CharacterSettlementOptionQuery.GetOptions(state, player, use.UseId).Single();
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TryInvokeCharacterSettlementOption(player, option.OptionId, use.UseId, option.StateRevision, out _, out string error), Is.True, error);
            for (int guard = 0; !Current(state).SourceConsumed && guard < 100; guard++) Assert.That(engine.Advance(), Is.True);
            Assert.That(Current(state).SourceConsumed, Is.True); Assert.That(Current(state).DoubleUseGranted, Is.False);
            state = Restore(state); Run(state);
            Assert.That(Current(state).UseId, Is.EqualTo(use.UseId)); Assert.That(Current(state).DoubleUseGranted, Is.True);
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(new[] { "strategy-first", "tactic-first" }));
            engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(Open(state).GetStableInteractionId(), player, Open(state).StateRevision, NormalizedValue.CreateString("cancel"), out _), Is.False);
            Assert.That(engine.TrySubmitInteraction(old.GetStableInteractionId(), player, old.StateRevision, NormalizedValue.CreateString("strategy"), out _), Is.False);
            Assert.That(engine.TryInvokeCharacterSettlementOption(player, option.OptionId, use.UseId, option.StateRevision, out bool duplicate, out error), Is.True, error);
            Assert.That(duplicate, Is.True); Answer(state, "tactic-first"); Answer(state, "confirm"); Answer(state, "confirm");
            Assert.That(Current(state).CompletedModes, Is.EqualTo(new[] { "tactic", "strategy" }));
            Assert.That(state.EffectRuntime.EffectNodes.Count(node => node.EffectTypeId == CharacterLifecycleService.ConsumeSourceTypeId), Is.EqualTo(1));
            Assert.That(Zone(state, option.SourceItemId), Is.EqualTo("core.zone.character_discard"));
        }

        [Test] public void FailedInsertionDoesNotGrantSecondModeAndCanCancelBeforeActivation()
        {
            var state = PrepareInsertion(); int player = state.CurrentPlayerId; var use = Current(state);
            var option = CharacterSettlementOptionQuery.GetOptions(state, player, use.UseId).Single(); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TryInvokeCharacterSettlementOption(player, option.OptionId, use.UseId, option.StateRevision, out _, out _), Is.True);
            for (int guard = 0; !state.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == CharacterLifecycleService.ConsumeSourceTypeId) && guard < 100; guard++) Assert.That(engine.Advance(), Is.True);
            var consume = state.EffectRuntime.EffectNodes.Single(node => node.EffectTypeId == CharacterLifecycleService.ConsumeSourceTypeId);
            engine.CreateChild(consume.EffectId, EffectSpec.Create(CharacterLifecycleService.TransferTypeId, Object("apiVersion", NormalizedValue.CreateInteger(1),
                "player", NormalizedValue.CreateString(player.ToString()), "cardInstanceId", NormalizedValue.CreateString(option.SourceItemId), "recipientPlayer", NormalizedValue.CreateString("1")), player));
            Run(state); Assert.That(Current(state).DoubleUseGranted || Current(state).SourceConsumed, Is.False);
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(new[] { "strategy", "tactic", "cancel" }));
            Answer(state, "cancel"); Assert.That(Current(state).SelectionCancelled, Is.True); state = Restore(state);
        }

        [Test] public void RecoveryRejectsContradictoryCancelledUse()
        {
            var state = Prepare(); Begin(state); Answer(state, "cancel");
            var archive = Newtonsoft.Json.Linq.JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            archive["Snapshot"]["State"]["CharacterUses"][0]["SourceConsumed"] = true;
            Current(state).SourceConsumed = true;
            Assert.That(CharacterLifecycleService.TryValidate(state, out _), Is.False);
            Assert.That(HostRecoveryService.RecoverJson(archive.ToString(), registry).Succeeded, Is.False);
        }

        [TestCase("start")][TestCase("hub")][TestCase("amiya")]
        public void AutomaticSecondModeKeepsWindowUseAndWaitsForFirstResponses(string source)
        {
            var state = source == "amiya" ? PrepareInsertion() : Prepare(nonFirst: source == "hub");
            int player = state.CurrentPlayerId;
            if (source == "hub")
            {
                new EffectTreeExecutor(state, registry).CreateRoot(EffectSpec.Create(LuaDomainEffectTypeIds.SetCharacterDoubleUseRule,
                    Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(player.ToString()),
                        "scope", NormalizedValue.CreateString("action_round"), "expiresActionRoundSequence", NormalizedValue.CreateInteger(CharacterLifecycleService.GetActionRoundSequence(state))), player)); Run(state);
            }
            if (source != "amiya") Begin(state);
            var use = Current(state); string id = use.UseId, window = state.EffectRuntime.ActiveMainNodeId;
            var useOwner = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == use.OwnerEffectId);
            var quick = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == useOwner.ParentEffectId);
            Assert.That(quick.ParentEffectId, Is.EqualTo(state.EffectRuntime.MainNodes.Single(node => node.NodeId == window).ExecutionEffectId));
            if (source == "hub")
            {
                // 旧v2已进入首选择的存档：开放second_mode由Host迁移，不能丢页卡死。
                use.SelectionProtocolVersion = 2;
                foreach (var node in new[] { useOwner, quick }) node.NormalizedArguments.Properties.Single(entry => entry.Name == "selectionProtocolVersion").Value = NormalizedValue.CreateInteger(2);
                state = Restore(state);
            }
            if (source == "amiya")
            {
                var option = CharacterSettlementOptionQuery.GetOptions(state, player, id).Single();
                Assert.That(new EffectTreeExecutor(state, registry).TryInvokeCharacterSettlementOption(player, option.OptionId, id, option.StateRevision, out _, out _), Is.True);
                Run(state); Answer(state, "strategy-first");
            }
            else Answer(state, "strategy");
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.first.response", "EffectCompleted", "*", "test.first.response",
                context => context.OwnerNode.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId && Text(context.OwnerNode.NormalizedArguments, "useId") == id &&
                    Text(context.OwnerNode.NormalizedArguments, "mode") == "strategy" ? new List<EffectSpec> { EffectSpec.Create("test.character.choice", playerId: player) } : new List<EffectSpec>(), EffectHandlerRole.Observer));
            Answer(state, "confirm");
            Assert.That(state.EffectRuntime.EffectNodes.Count(node => node.ParentEffectId == Current(state).OwnerEffectId && node.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId), Is.EqualTo(1));
            state = Restore(state);
            if (source == "hub")
            {
                var firstResponse = Open(state); var engine = new EffectTreeExecutor(state, registry);
                Assert.That(engine.TrySubmitInteraction(firstResponse.GetStableInteractionId(), player, firstResponse.StateRevision, NormalizedValue.CreateString("confirm"), out _), Is.True);
                for (int guard = 0; !state.EffectRuntime.InteractionRequests.Any(request => request.Status == "open" && request.InteractionTypeId == CharacterLifecycleService.SecondModeInteractionTypeId) && guard < 100; guard++)
                    Assert.That(engine.Advance(), Is.True);
                Assert.That(Current(state).SelectionProtocolVersion, Is.EqualTo(2));
                state = Restore(state); // 不手动Run，恢复入口必须消除旧中间页。
            }
            else Answer(state, "confirm");
            Assert.That(Current(state).UseId, Is.EqualTo(id)); Assert.That(Current(state).ModeOrder, Is.EqualTo(new[] { "strategy", "tactic" }));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(request => request.Status == "open" && request.InteractionTypeId == CharacterLifecycleService.SecondModeInteractionTypeId), Is.False);
            Assert.That(Current(state).SelectionProtocolVersion, Is.EqualTo(3));
            Answer(state, "confirm"); Assert.That(Current(state).Finished, Is.True);
            Assert.That(state.EffectRuntime.ActiveMainNodeId, Is.EqualTo(window));
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.EventType == "CharacterUseFinished" && Text(value.Payload, "useId") == id), Is.EqualTo(1));
            if (source == "amiya") Assert.That(state.EffectRuntime.EffectNodes.Count(node => node.EffectTypeId == CharacterLifecycleService.ConsumeSourceTypeId), Is.EqualTo(1));
        }

        [Test] public void StopRemainingModeKeepsCommittedResultsAndRecoversWithoutReplaying()
        {
            var state = Prepare(partialSecond: true); int gold = state.FindPlayer(1).Resources.GoldVoucher;
            string window = state.EffectRuntime.ActiveMainNodeId; Begin(state); Answer(state, "strategy"); Answer(state, "confirm");
            var ability = presenter.GetPendingModeStop(state, 1); Assert.That(ability, Is.Not.Null);
            var command = presenter.CreateStopRemainingModeCommand(ability, 1, state.EffectRuntime.StateRevision);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 7));
            Assert.That(Session(state).Submit(command).Succeeded, Is.True);
            Assert.That(Current(state).SkippedModes, Is.EqualTo(new[] { "tactic" })); Assert.That(Current(state).CompletedModes, Is.EqualTo(new[] { "strategy" }));
            Assert.That(Current(state).Finished, Is.True); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 7));
            Assert.That(Zone(state, Current(state).CardInstanceId), Is.EqualTo("core.zone.box_retired"));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1).CharacterUses.Any(use => use.UseId == Current(state).UseId && use.StopCommandId == command.CommandId && use.Finished), Is.True,
                "停止确认必须在玩家投影中可见，结束按钮才可正常收尾");
            // 同一次恢复用例覆盖新增企业分类元数据的旧档兼容，不另扩测试组。
            state.SetupRecipe = new GameSetupRecipe();
            state.SetupRecipe.Add("characters", "texas", "core.basic_character_card", "hand", true, 5);
            state.ContentDefinitions = GameContentDefinitions.CreateRegisteredStandard();
            state.ContentDefinitions.EnterpriseDefinitionIds.Add("enterprise.test");
            registry.ContentPackHash = state.ContentPackHash;
            registry.SetupRecipe = GameStateCloneService.DeepClone(state.SetupRecipe);
            registry.ContentDefinitions = GameStateCloneService.DeepClone(state.ContentDefinitions);
            var archive = Newtonsoft.Json.Linq.JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            var content = (Newtonsoft.Json.Linq.JObject)archive["Snapshot"]["State"]["ContentDefinitions"];
            content.Remove("EnterpriseDefinitionIds"); string originalArchive = archive.ToString();
            var compatible = HostRecoveryService.RecoverJson(originalArchive, registry);
            Assert.That(compatible.Succeeded, Is.True, compatible.Diagnostic);
            Assert.That(compatible.State.ContentDefinitions.EnterpriseDefinitionIds, Is.EqualTo(new[] { "enterprise.test" }));
            Assert.That(archive.ToString(), Is.EqualTo(originalArchive));
            content["EnterpriseDefinitionIds"] = new Newtonsoft.Json.Linq.JArray("enterprise.wrong");
            Assert.That(HostRecoveryService.RecoverJson(archive.ToString(), registry).Succeeded, Is.False);
            state = Restore(state); string snapshot = JsonConvert.SerializeObject(state);
            Assert.That(new UseCharacterCardCommandHandler(new CharacterCardService(), registry).Handle(state, command).Succeeded, Is.True);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(snapshot));
            Assert.That(rounds.EndCurrentPlayerWindow(state, 1).IsValid, Is.False);
            Assert.That(state.EffectRuntime.ActiveMainNodeId, Is.EqualTo(window));
            Assert.That(rounds.CompleteMainAction(state, 1).IsValid, Is.True);
            Assert.That(state.EffectRuntime.ActiveMainNodeId, Is.Not.EqualTo(window));
        }

        [Test] public void StopRemainingModePreservesDispatchedOtherPlayerResponse()
        {
            var state = Prepare(); Begin(state); Answer(state, "strategy"); Answer(state, "confirm");
            var ownRequest = Open(state); string useId = Current(state).UseId;
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.external.response", "test.external", "*", "test.external.response",
                context => new List<EffectSpec> { EffectSpec.Create("test.character.choice", playerId: 2), ResourceEffectSpecFactory.GainScore(2, 4) }, EffectHandlerRole.Observer));
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TryPublishEvent(ownRequest.OwnerEffectId, new EffectEventRequest { EventType = "test.external", PlayerId = 1, ResponseKind = RuleEventResponseKind.Effects }, out _, out _), Is.True);
            Run(state); var external = state.EffectRuntime.InteractionRequests.Single(request => request.Status == "open" && request.AnsweringPlayerId == 2);
            Assert.That(engine.CanStopCharacterMode(1, useId, external.GetStableInteractionId(), state.EffectRuntime.StateRevision), Is.False);
            rounds.MarkMainActionComplete(state, 1);
            Assert.That(engine.TryStopCharacterMode(1, useId, ownRequest.GetStableInteractionId(), state.EffectRuntime.StateRevision, "stop.with.response", out string reason), Is.True, reason);
            Run(state); Assert.That(Current(state).Finished, Is.False);
            Assert.That(rounds.EndCurrentPlayerWindow(state, 1).IsValid, Is.False);
            state = Restore(state);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.GetStableInteractionId() == external.GetStableInteractionId());
            Assert.That(request.Status, Is.EqualTo("open"));
            Assert.That(new EffectTreeExecutor(state, registry).TrySubmitInteraction(request.GetStableInteractionId(), 2, request.StateRevision, NormalizedValue.CreateString("confirm"), out _), Is.True);
            Run(state); Assert.That(Current(state).Finished, Is.True);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2)); Assert.That(state.FindPlayer(2).Score, Is.EqualTo(4));
            Assert.That(rounds.EndCurrentPlayerWindow(state, 1).IsValid, Is.True);
        }

        [TestCase("SelectionProtocolVersion")][TestCase("OfferSecondEffect")][TestCase("CanOfferSecondEffect")]
        public void RecoveryCannotDowngradeNewSelectionProtocolByDroppingFacts(string field)
        {
            var state = Prepare(); Begin(state); Answer(state, "strategy"); Answer(state, "confirm");
            var archive = Newtonsoft.Json.Linq.JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            ((Newtonsoft.Json.Linq.JObject)archive["Snapshot"]["State"]["CharacterUses"][0]).Remove(field);
            Assert.That(HostRecoveryService.RecoverJson(archive.ToString(), registry).Succeeded, Is.False);
        }
    }
}
