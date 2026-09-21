using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class NMC010CharacterCoverAndAbilityEditModeTests
    {
        [Test]
        public void Cover_CreatesOrderedTasksAndProjectsPrivateCardOnlyToOwner()
        {
            var state = CreateCoverState(2);
            var registry = new EffectRegistry();
            var round = new RoundExecutionService(registry);
            Assert.That(round.CreateRound(state, 1, 1).IsValid, Is.True);

            var firstRequest = state.EffectRuntime.InteractionRequests.Find(request =>
                request != null && request.Status == "open" &&
                request.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId);
            Assert.That(firstRequest, Is.Not.Null);
            Assert.That(firstRequest.AnsweringPlayerId, Is.EqualTo(1));
            Assert.That(firstRequest.CandidateIds, Does.Contain(state.FindPlayer(1).HandCardIds[0]));

            string firstCard = state.FindPlayer(1).HandCardIds[0];
            var firstHandler = new CoverCharacterCardCommandHandler(new CharacterCardService(), registry);
            Assert.That(firstHandler.Handle(state, CoverCommand(1, firstCard)).Succeeded, Is.True);

            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            var secondRequest = state.EffectRuntime.InteractionRequests.Find(request =>
                request != null && request.Status == "open" &&
                request.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId);
            Assert.That(secondRequest, Is.Not.Null);
            Assert.That(secondRequest.AnsweringPlayerId, Is.EqualTo(2));

            string secondCard = state.FindPlayer(2).HandCardIds[0];
            Assert.That(firstHandler.Handle(state, CoverCommand(2, secondCard)).Succeeded, Is.True);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
            var activeWindow = state.EffectRuntime.MainNodes.Find(n => n.NodeId == state.EffectRuntime.ActiveMainNodeId);
            Assert.That(activeWindow.ExecutionEffectId, Is.Not.Empty, "盖牌结束必须启动行动窗口执行节点，而不只是更新阶段投影");
            Assert.That(new CharacterCardPanelPresenter().BuildView(state, 1).CanUse, Is.True,
                "内部主链完成交互不能屏蔽角色牌入口");

            var view = GameStateViewProjector.Project(state, GameStateViewer.Player(2));
            var covered = view.Events.Find(ruleEvent => ruleEvent.EventType == "CharacterCardCovered" && ruleEvent.PlayerId == 1);
            Assert.That(covered, Is.Not.Null);
            Assert.That(covered.Payload.ToDeterministicString(), Does.Contain("hidden"));
            Assert.That(covered.HostOnlyPayload.Kind, Is.EqualTo(NormalizedValueKind.Null));
        }

        [Test]
        public void Cover_MainlineInteractionKeepsLocalHandDraggableAndAdvancesToNextPlayer()
        {
            var state = CreateCoverState(2);
            var registry = new EffectRegistry();
            var round = new RoundExecutionService(registry);
            Assert.That(round.CreateRound(state, 1, 1).IsValid, Is.True);

            var presenter = new CharacterCardPanelPresenter();
            var firstView = presenter.BuildView(state, 1);
            Assert.That(firstView.CanCover, Is.True);
            Assert.That(firstView.HandCards, Has.All.Matches<CharacterCardHandItemViewModel>(card => card.CanCover));

            var firstCard = state.FindPlayer(1).HandCardIds[0];
            var submit = presenter.CreateCoverCommand(1, firstCard);
            var session = new GameSession(state);
            session.RegisterHandler(new CoverCharacterCardCommandHandler(new CharacterCardService(), registry));
            var result = session.Submit(submit);

            Assert.That(result.Succeeded, Is.True, result.Validation == null ? string.Empty : result.Validation.Reason);
            Assert.That(state.FindPlayer(1).CoveredCharacterCardId, Is.EqualTo(firstCard));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));

            var secondView = presenter.BuildView(state, 2);
            Assert.That(secondView.CanCover, Is.True);
            Assert.That(secondView.HandCards, Has.All.Matches<CharacterCardHandItemViewModel>(card => card.CanCover));
        }

        [Test]
        public void ElysiumStrategy_UsesLuaRouteAndGenericResourceInteraction()
        {
            var state = CreateActionStateWithElysium();
            var registry = new EffectRegistry();
            new RoundExecutionService(registry);
            ResourceEffectExecutor.Register(registry);
            CharacterCardLuaCatalog.Register(registry);

            string cardId = state.FindPlayer(1).CoveredCharacterCardId;
            var handler = new UseCharacterCardCommandHandler(new CharacterCardService(), registry);
            var use = handler.Handle(state, UseCommand(1, cardId, CharacterEffectModes.Strategy));

            Assert.That(use.Succeeded, Is.True, use.Validation == null ? string.Empty : use.Validation.Reason);
            var interaction = state.EffectRuntime.InteractionRequests.Find(request =>
                request != null && request.Status == "open" &&
                request.InteractionTypeId == "lua.choice");
            Assert.That(interaction, Is.Not.Null);
            Assert.That(interaction.CandidateIds, Is.EqualTo(new[] { "originium" }));
            Assert.That(state.FindPlayer(1).CoveredCharacterCardId, Is.EqualTo(cardId));

            var answer = new GameCommand
            {
                Kind = GameCommandKind.AnswerInteraction,
                PlayerId = 1,
                TargetId = "originium"
            };
            answer.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = interaction.InteractionId;
            answer.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] =
                interaction.StateRevision.ToString();
            answer.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = "originium";
            var answered = new AnswerInteractionCommandHandler(registry).Handle(state, answer);

            Assert.That(answered.Succeeded, Is.True, answered.Validation == null ? string.Empty : answered.Validation.Reason);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(6));
            Assert.That(state.FindPlayer(1).CoveredCharacterCardId, Is.Empty);
            Assert.That(state.FindPlayer(1).DiscardCardIds, Does.Contain(cardId));
            Assert.That(state.PendingCharacterEffect, Is.Null);
        }

        [TestCase("originium", 1)]
        [TestCase("originium-shard", 2)]
        [TestCase("iron", 3)]
        public void ElysiumChoice_RestoresVersionedContinuationAndCannotReplay(string resource, int kind)
        {
            var state = CreateActionStateWithElysium();
            var resources = state.FindPlayer(1).Resources;
            resources.Originium = resources.OriginiumShard = resources.Iron = 2;
            var registry = CreateCharacterAbilityRegistry();
            var use = new UseCharacterCardCommandHandler(new CharacterCardService(), registry).Handle(state,
                UseCommand(1, state.FindPlayer(1).CoveredCharacterCardId, CharacterEffectModes.Strategy));
            Assert.That(use.Succeeded, Is.True);
            var request = FindOpenInteraction(state, "lua.choice");
            Assert.That(request.CandidateIds, Is.EqualTo(new[] { "originium", "originium-shard", "iron" }));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(2));
            state = GameStateCloneService.DeepClone(state);
            registry = CreateCharacterAbilityRegistry();
            request = FindOpenInteraction(state, "lua.choice");
            var replay = EffectInteractionCommands.Answer(
                YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1), 1, new[] { resource });
            var answer = Answer(state, registry, request, resource);
            Assert.That(answer.Succeeded, Is.True, answer.Validation == null ? "" : answer.Validation.Reason);
            resources = state.FindPlayer(1).Resources;
            Assert.That(new[] { resources.Originium, resources.OriginiumShard, resources.Iron },
                Is.EqualTo(new[] { kind == 1 ? 6 : 2, kind == 2 ? 6 : 2, kind == 3 ? 6 : 2 }));
            Assert.That(state.EffectRuntime.EffectNodes.Exists(n => n.EffectTypeId == CharacterAbilityEffectExecutor.AbilityEffectTypeId), Is.False);
            Assert.That(new AnswerInteractionCommandHandler(registry).Handle(state, replay).Succeeded, Is.False);
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(state.FindPlayer(1).Resources.Get(kind == 1 ? ResourceType.Originium : kind == 2 ? ResourceType.OriginiumShard : ResourceType.Iron), Is.EqualTo(6));
        }

        [Test]
        public void LiskarmStrategy_UsesIndependentPlacementInteractionsAndRecomputesCandidates()
        {
            var state = CreateActionStateWithTemplate(CharacterCardDatabase.Liskarm);
            var player = state.FindPlayer(1);
            player.InfluenceSupply = 2;
            var registry = CreateCharacterAbilityRegistry();
            var cardId = player.CoveredCharacterCardId;

            var use = new UseCharacterCardCommandHandler(new CharacterCardService(), registry)
                .Handle(state, UseCommand(1, cardId, CharacterEffectModes.Strategy));
            Assert.That(use.Succeeded, Is.True, use.Validation == null ? string.Empty : use.Validation.Reason);

            var parent = state.EffectRuntime.EffectNodes.Find(node =>
                node != null && node.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId);
            Assert.That(parent, Is.Not.Null);
            Assert.That(parent.ChildEffectIds, Has.Count.EqualTo(2));
            for (var i = 0; i < parent.ChildEffectIds.Count; i++)
            {
                var child = state.EffectRuntime.EffectNodes.Find(node => node.EffectId == parent.ChildEffectIds[i]);
                Assert.That(child, Is.Not.Null);
                Assert.That(child.EffectTypeId, Is.EqualTo(InfluenceEffectTypeIds.PlaceInfluence));
            }

            var first = FindOpenInteraction(state, "effect.influence.place.target");
            Assert.That(first, Is.Not.Null);
            Assert.That(first.PromptKey, Is.EqualTo("effect.influence.place.choose_target"));
            Assert.That(first.CandidateIds, Has.All.Matches<string>(candidate => !candidate.Contains("|")));

            var firstSlot = first.CandidateIds[0];
            Assert.That(Answer(state, registry, first, firstSlot).Succeeded, Is.True);
            Assert.That(state.Map.Influences, Has.Count.EqualTo(1));
            Assert.That(player.InfluenceSupply, Is.EqualTo(1));

            var second = FindOpenInteraction(state, "effect.influence.place.target");
            Assert.That(second, Is.Not.Null);
            Assert.That(second.PromptKey, Is.EqualTo("effect.influence.place.choose_target"));
            Assert.That(second.CandidateIds, Has.All.Matches<string>(candidate => !candidate.Contains("|")));
            Assert.That(second.CandidateIds, Does.Not.Contain(firstSlot));

            var secondSlot = second.CandidateIds[0];
            Assert.That(Answer(state, registry, second, secondSlot).Succeeded, Is.True);
            Assert.That(state.Map.Influences, Has.Count.EqualTo(2));
            Assert.That(player.InfluenceSupply, Is.Zero);
            Assert.That(state.DelayedCharacterEffects, Is.Empty);
            Assert.That(player.CoveredCharacterCardId, Is.Empty);
            Assert.That(player.UsedCharacterThisRound, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LiskarmCleanup_PersistsBindingAndRemovesOwnInfluenceExactlyOnce(bool noRemainingInfluence)
        {
            var state = CreateActionStateWithTemplate(CharacterCardDatabase.Liskarm);
            var registry = CreateCharacterAbilityRegistry();
            var use = new UseCharacterCardCommandHandler(new CharacterCardService(), registry).Handle(state,
                UseCommand(1, state.FindPlayer(1).CoveredCharacterCardId, CharacterEffectModes.Strategy));
            Assert.That(use.Succeeded, Is.True);
            for (int i = 0; i < 2; i++)
            {
                var placement = FindOpenInteraction(state, "effect.influence.place.target");
                Assert.That(Answer(state, registry, placement, placement.CandidateIds[0]).Succeeded, Is.True);
            }
            var activation = state.EffectRuntime.EffectNodes.Find(node =>
                node.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId);
            Assert.That(activation.TimingBindings, Has.Count.EqualTo(1));
            Assert.That(activation.TimingBindings[0].Status, Is.EqualTo("registered"));
            var incompatible = GameStateCloneService.DeepClone(state);
            incompatible.EffectRuntime.EffectNodes.Find(node => node.EffectId == activation.EffectId)
                .TimingBindings[0].ContentHash = "changed-script";
            Assert.That(new EffectTreeExecutor(incompatible, registry).RunUntilQuiescent().Faulted, Is.True);
            Assert.That(incompatible.EffectRuntime.LastFaultCode, Is.EqualTo("timing_binding_invalid"));
            Assert.That(incompatible.Map.Influences, Has.Count.EqualTo(2));
            var missingHandler = GameStateCloneService.DeepClone(state);
            Assert.That(new EffectTreeExecutor(missingHandler, new EffectRegistry()).RunUntilQuiescent().Faulted, Is.True);
            Assert.That(missingHandler.EffectRuntime.LastFaultCode, Is.EqualTo("timing_binding_invalid"));
            if (noRemainingInfluence) state.Map.Influences.Clear();
            state.Map.Influences.Add(new InfluencePlacement
            {
                InfluenceId = "opponent-marker", PlayerId = 2, SlotId = "opponent-slot"
            });
            state = GameStateCloneService.DeepClone(state);
            registry = CreateCharacterAbilityRegistry();
            registry.Register(new EffectRegistration("test.cleanup", context =>
                EffectStepResult.Completed().AddEvent(new EffectEventRequest
                {
                    EventType = "PlayerCleanupStarted", PlayerId = 1,
                    OwnerNodeId = context.Node.EffectId, SourceEffectId = context.Node.EffectId
                })));
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(new EffectSpec("test.cleanup") { PlayerId = 1 });
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var removal = FindOpenInteraction(state, "effect.influence.remove.target");
            if (noRemainingInfluence) Assert.That(removal, Is.Null);
            else
            {
                Assert.That(removal, Is.Not.Null);
                Assert.That(removal.CandidateIds, Has.Count.EqualTo(2));
                Assert.That(removal.CandidateIds, Does.Not.Contain("opponent-marker"));
                // 在玩家尚未回答时恢复，再推进不得重复运行 Lua 或复制移除请求。
                state = GameStateCloneService.DeepClone(state);
                executor = new EffectTreeExecutor(state, registry);
                Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
                Assert.That(state.EffectRuntime.InteractionRequests.FindAll(request =>
                    request.Status == "open" && request.InteractionTypeId == "effect.influence.remove.target"), Has.Count.EqualTo(1));
                Assert.That(Answer(state, registry, removal, removal.CandidateIds[0]).Succeeded, Is.True);
            }
            Assert.That(state.Map.Influences, Has.Count.EqualTo(noRemainingInfluence ? 1 : 2));
            activation = state.EffectRuntime.EffectNodes.Find(node =>
                node.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId);
            Assert.That(activation.TimingBindings[0].Status, Is.EqualTo("completed"));
            executor.CreateRoot(new EffectSpec("test.cleanup") { PlayerId = 1 });
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(FindOpenInteraction(state, "effect.influence.remove.target"), Is.Null);
            Assert.That(state.Map.Influences, Has.Count.EqualTo(noRemainingInfluence ? 1 : 2));
        }

        [Test]
        public void RegisteredCharacterFamilies_UseGenericInteractionWithoutPendingState()
        {
            var texas = CreateActionStateWithTemplate(CharacterCardDatabase.Texas);
            texas.Decks.FacilitySupply.Add("f1");
            var texasRegistry = CreateCharacterAbilityRegistry();
            var texasUse = new UseCharacterCardCommandHandler(new CharacterCardService(), texasRegistry)
                .Handle(texas, UseCommand(1, texas.FindPlayer(1).CoveredCharacterCardId, CharacterEffectModes.Strategy));
            Assert.That(texasUse.Succeeded, Is.True, texasUse.Validation == null ? string.Empty : texasUse.Validation.Reason);
            var facility = FindOpenInteraction(texas, CharacterAbilityEffectExecutor.TargetInteractionTypeId);
            Assert.That(facility, Is.Not.Null);
            Assert.That(Answer(texas, texasRegistry, facility, facility.CandidateIds[0]).Succeeded, Is.True);
            Assert.That(texas.PendingCharacterEffect, Is.Null);
            Assert.That(texas.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(12));

            var cannot = CreateActionStateWithTemplate(CharacterCardDatabase.Cannot);
            cannot.FindPlayer(1).Resources.Originium = 1;
            cannot.FindPlayer(1).Resources.Iron = 0;
            var cannotRegistry = CreateCharacterAbilityRegistry();
            var cannotUse = new UseCharacterCardCommandHandler(new CharacterCardService(), cannotRegistry)
                .Handle(cannot, UseCommand(1, cannot.FindPlayer(1).CoveredCharacterCardId, CharacterEffectModes.Tactic));
            Assert.That(cannotUse.Succeeded, Is.True, cannotUse.Validation == null ? string.Empty : cannotUse.Validation.Reason);
            var requisition = FindOpenInteraction(cannot, "lua.choice");
            Assert.That(requisition, Is.Not.Null);
            Assert.That(Answer(cannot, cannotRegistry, requisition, "iron").Succeeded, Is.True);
            Assert.That(cannot.PendingCharacterEffect, Is.Null);
            Assert.That(cannot.FindPlayer(1).Score, Is.EqualTo(1));

            var tinMan = CreateActionStateWithTemplate(CharacterCardDatabase.TinMan);
            tinMan.FindPlayer(1).Resources.GoldVoucher = 12;
            var tinManRegistry = CreateCharacterAbilityRegistry();
            var tinManUse = new UseCharacterCardCommandHandler(new CharacterCardService(), tinManRegistry)
                .Handle(tinMan, UseCommand(1, tinMan.FindPlayer(1).CoveredCharacterCardId, CharacterEffectModes.Strategy));
            Assert.That(tinManUse.Succeeded, Is.True, tinManUse.Validation == null ? string.Empty : tinManUse.Validation.Reason);
            var purchase = FindOpenInteraction(tinMan, "action.decline_effect");
            Assert.That(purchase, Is.Not.Null);
            var purchaseAnswer = Answer(tinMan, tinManRegistry, purchase, "continue");
            Assert.That(purchaseAnswer.Succeeded, Is.True, purchaseAnswer.Validation == null ? string.Empty : purchaseAnswer.Validation.Reason);
            var finish = FindOpenInteraction(tinMan, "action.decline_effect");
            Assert.That(finish, Is.Not.Null);
            Assert.That(Answer(tinMan, tinManRegistry, finish, "decline").Succeeded, Is.True);
            Assert.That(tinMan.PendingCharacterEffect, Is.Null);
            Assert.That(tinMan.FindPlayer(1).UsedCharacterThisRound, Is.True);
        }

        [Test]
        public void CannotTactic_OneRequisitionAnswerSettlesAllPlayersAndCannotReplay()
        {
            var state = CreateActionStateWithTemplate(CharacterCardDatabase.Cannot);
            state.FindPlayer(1).Resources.Iron = 2;
            state.FindPlayer(1).Resources.GoldVoucher = 0;
            state.FindPlayer(1).Score = 0;
            state.FindPlayer(2).Resources.Iron = 53;
            var registry = CreateCharacterAbilityRegistry();
            var cardId = state.FindPlayer(1).CoveredCharacterCardId;
            var use = new UseCharacterCardCommandHandler(new CharacterCardService(), registry)
                .Handle(state, UseCommand(1, cardId, CharacterEffectModes.Tactic));
            Assert.That(use.Succeeded, Is.True);
            var request = FindOpenInteraction(state, "lua.choice");
            Assert.That(request, Is.Not.Null);
            state = GameStateCloneService.DeepClone(state);
            registry = CreateCharacterAbilityRegistry();
            request = FindOpenInteraction(state, "lua.choice");
            var replay = EffectInteractionCommands.Answer(
                YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1), 1, new[] { "iron" });
            var resolved = Answer(state, registry, request, "iron");
            Assert.That(resolved.Succeeded, Is.True, resolved.Validation == null ? "" : resolved.Validation.Reason);
            Assert.That(FindOpenInteraction(state, "lua.choice"), Is.Null);
            Assert.That(state.EffectRuntime.InteractionRequests.FindAll(item =>
                item.PromptKey == "character.cannot.tactic.choose_resource"), Has.Count.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.Zero);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(106));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).CoveredCharacterCardId, Is.Empty);
            Assert.That(state.FindPlayer(1).DiscardCardIds, Does.Contain(cardId));
            Assert.That(new AnswerInteractionCommandHandler(registry).Handle(state, replay).Succeeded, Is.False);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(106));
        }

        [Test]
        public void BothCharacterEffects_UsesPersistedOrderAndCompletesThroughSequence()
        {
            var state = CreateActionStateWithTemplate(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            player.Resources.GoldVoucher = 30;
            player.DiscardCardIds.Add("discarded-event-1");
            var registry = CreateCharacterAbilityRegistry();
            string cardId = player.CoveredCharacterCardId;
            var command = UseCommand(1, cardId, CharacterEffectModes.Both);
            command.Parameters[UseCharacterCardCommandHandler.EffectOrderParameter] = CharacterEffectOrders.StrategyFirst;

            var use = new UseCharacterCardCommandHandler(new CharacterCardService(), registry).Handle(state, command);
            Assert.That(use.Succeeded, Is.True, use.Validation == null ? string.Empty : use.Validation.Reason);
            var firstPurchase = FindOpenInteraction(state, "action.decline_effect");
            Assert.That(firstPurchase, Is.Not.Null);
            Assert.That(Answer(state, registry, firstPurchase, "continue").Succeeded, Is.True);

            var finishPurchase = FindOpenInteraction(state, "action.decline_effect");
            Assert.That(finishPurchase, Is.Not.Null);
            Assert.That(Answer(state, registry, finishPurchase, "decline").Succeeded, Is.True);

            var recall = FindOpenInteraction(state, CharacterAbilityEffectExecutor.ChoiceInteractionTypeId);
            Assert.That(recall, Is.Not.Null);
            Assert.That(Answer(state, registry, recall, CharacterEffectChoiceIds.GainGold).Succeeded, Is.True);
            Assert.That(player.UsedCharacterThisRound, Is.True);
            Assert.That(player.CoveredCharacterCardId, Is.Empty);
            Assert.That(player.HandCardIds, Does.Contain("discarded-event-1"));
            Assert.That(state.PendingCharacterEffect, Is.Null);
        }

        private static GameState CreateCoverState(int playerCount)
        {
            var state = new GameState
            {
                GameId = "nmc010-cover",
                Phase = GamePhase.CharacterCover,
                Round = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState { PlayerId = 1, Color = PlayerColor.Red },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Blue }
                }
            };
            CharacterCardDatabase.InitializePlayerHand(state.FindPlayer(1));
            CharacterCardDatabase.InitializePlayerHand(state.FindPlayer(2));
            return state;
        }

        private static GameState CreateActionStateWithElysium()
        {
            var state = CreateActionStateWithTemplate(CharacterCardDatabase.Elysium);
            var player = state.FindPlayer(1);
            player.Resources.Originium = 2;
            player.Resources.OriginiumShard = 5;
            player.Resources.Iron = 5;
            return state;
        }

        private static GameState CreateActionStateWithTemplate(string templateId)
        {
            var state = CreateCoverState(1);
            var player = state.FindPlayer(1);
            string cardId = player.HandCardIds.Find(id => CharacterCardDatabase.Get(id).TemplateId == templateId);
            player.HandCardIds.Remove(cardId);
            player.CoveredCharacterCardId = cardId;
            state.Phase = GamePhase.ActionRound1;
            state.ActionRound = 1;
            state.CurrentPlayerId = 1;
            return state;
        }

        private static EffectRegistry CreateCharacterAbilityRegistry()
        {
            var registry = new EffectRegistry();
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            InfluenceEffectExecutor.Register(registry, new InfluenceService(mapQuery));
            new RoundExecutionService(registry);
            ResourceEffectExecutor.Register(registry);
            CharacterCardLuaCatalog.Register(registry);
            return registry;
        }

        private static InteractionRequest FindOpenInteraction(GameState state, string interactionTypeId)
        {
            return state.EffectRuntime.InteractionRequests.Find(request =>
                request != null && request.Status == "open" &&
                request.InteractionTypeId.StartsWith(interactionTypeId, System.StringComparison.Ordinal));
        }

        private static CommandResult Answer(
            GameState state,
            EffectRegistry registry,
            InteractionRequest request,
            string candidateId)
        {
            var answer = EffectInteractionCommands.Answer(
                YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1), 1, new[] { candidateId });
            return new AnswerInteractionCommandHandler(registry).Handle(state, answer);
        }

        private static GameCommand CoverCommand(int playerId, string cardId)
        {
            var command = new GameCommand
            {
                Kind = GameCommandKind.CoverCharacterCard,
                PlayerId = playerId,
                TargetId = cardId
            };
            command.Parameters[CoverCharacterCardCommandHandler.CardIdParameter] = cardId;
            return command;
        }

        private static GameCommand UseCommand(int playerId, string cardId, string mode)
        {
            var command = new GameCommand
            {
                Kind = GameCommandKind.UseCharacterCard,
                PlayerId = playerId,
                TargetId = cardId
            };
            command.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            command.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] = mode;
            return command;
        }
    }
}
