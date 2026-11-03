using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class NMC010CharacterCoverAndAbilityEditModeTests
    {
        [Test]
        public void Cover_CreatesOrderedTasksAndProjectsPrivateCardOnlyToOwner()
        {
            var state = CreateCurrentCoverState(out var registry);
            var handler = new CoverCharacterCardCommandHandler(new CharacterCardService(), registry);
            var firstRequest = FindOpenInteraction(state, CharacterCoverEffectExecutor.InteractionTypeId);
            Assert.That(firstRequest.AnsweringPlayerId, Is.EqualTo(1));
            string firstCard = firstRequest.CandidateIds[0];
            var first = CoverCommand(1, firstCard); BindCurrentCoverRequest(state, first);
            var result = handler.Handle(state, first);
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            Assert.That(new YC.Presentation.Workflows.LocalPlayerResolver().Resolve(state, 1, true), Is.EqualTo(2));
            var secondRequest = FindOpenInteraction(state, CharacterCoverEffectExecutor.InteractionTypeId);
            Assert.That(secondRequest.AnsweringPlayerId, Is.EqualTo(2));
            var second = CoverCommand(2, secondRequest.CandidateIds[0]); BindCurrentCoverRequest(state, second);
            Assert.That(handler.Handle(state, second).Succeeded, Is.True);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover), "正式三人对局须等待第三位玩家");
            Assert.That(new YC.Presentation.Workflows.LocalPlayerResolver().Resolve(state, 1, true), Is.EqualTo(3));
            var thirdRequest = FindOpenInteraction(state, CharacterCoverEffectExecutor.InteractionTypeId);
            var third = CoverCommand(3, thirdRequest.CandidateIds[0]); BindCurrentCoverRequest(state, third);
            Assert.That(handler.Handle(state, third).Succeeded, Is.True);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
            var window = state.EffectRuntime.MainNodes.Single(n => n.NodeId == state.EffectRuntime.ActiveMainNodeId);
            Assert.That(window.ExecutionEffectId, Is.Not.Empty);
            Assert.That(new CharacterCardPanelPresenter().BuildView(state, 1).CanUse, Is.True);
            var other = GameStateViewProjector.Project(state, GameStateViewer.Player(2)).Events
                .Single(e => e.EventType == "CharacterCardCovered" && e.PlayerId == 1);
            Assert.That(other.Payload.ToDeterministicString(), Does.Contain("hidden"));
            Assert.That(other.HostOnlyPayload.Kind, Is.EqualTo(NormalizedValueKind.Null));
            var own = GameStateViewProjector.Project(state, GameStateViewer.Player(1)).Events
                .Single(e => e.EventType == "CharacterCardCovered" && e.PlayerId == 1);
            Assert.That(own.HostOnlyPayload.ToDeterministicString(), Does.Contain(firstCard));
        }

        [Test]
        public void Cover_MainlineInteractionKeepsLocalHandDraggableAndAdvancesToNextPlayer()
        {
            var state = CreateCurrentCoverState(out var registry);
            var presenter = new CharacterCardPanelPresenter();
            var firstView = presenter.BuildView(state, 1);
            var request = FindOpenInteraction(state, CharacterCoverEffectExecutor.InteractionTypeId);
            Assert.That(firstView.CanCover, Is.True);
            Assert.That(firstView.HandCards, Is.Not.Empty);
            foreach (var card in firstView.HandCards)
                Assert.That(card.CanCover, Is.EqualTo(request.CandidateIds.Contains(card.CardId)));
            var firstCard = request.CandidateIds[0];
            var submit = presenter.CreateCoverCommand(1, firstCard);
            BindCurrentCoverRequest(state, submit);
            var session = new GameSession(state);
            session.RegisterHandler(new CoverCharacterCardCommandHandler(new CharacterCardService(), registry));
            var result = session.Submit(submit);
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            Assert.That(state.CharacterCoverSlots.Single(s => s.PlayerId == 1).CardInstanceId, Is.EqualTo(firstCard));
            Assert.That(new YC.Presentation.Workflows.LocalPlayerResolver().Resolve(state, 1, true), Is.EqualTo(2));
            var secondView = presenter.BuildView(state, 2);
            var secondRequest = FindOpenInteraction(state, CharacterCoverEffectExecutor.InteractionTypeId);
            Assert.That(secondView.CanCover, Is.True);
            foreach (var card in secondView.HandCards)
                Assert.That(card.CanCover, Is.EqualTo(secondRequest.CandidateIds.Contains(card.CardId)));
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
                request.InteractionTypeId == "effect.choice.execute");
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
            var request = FindOpenInteraction(state, "effect.choice.execute");
            Assert.That(request.CandidateIds, Is.EqualTo(new[] { "originium", "originium-shard", "iron" }));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(2));
            state = GameStateCloneService.DeepClone(state);
            registry = CreateCharacterAbilityRegistry();
            request = FindOpenInteraction(state, "effect.choice.execute");
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

        // 指定库存只用于隔离资格与恢复检查，不计正常对局覆盖。
        [TestCase("originium", ResourceType.Originium)]
        [TestCase("originium-shard", ResourceType.OriginiumShard)]
        [TestCase("iron", ResourceType.Iron)]
        public void CurrentElysiumRealBranchRestoresAndGrantsOnce(string candidate, ResourceType resource)
        {
            var state = CreateCurrentCharacterState(CharacterCardDatabase.Elysium, out var registry, out var card);
            var wallet = state.FindPlayer(1).Resources;
            wallet.Originium = wallet.OriginiumShard = wallet.Iron = 2;
            var action = registry.QueryActions(state, 1).Single(option => option.SourceRef.ItemId == card);
            var command = AbilityActionCommand.Create(1, action); command.CommandId = "elysium-current-strategy";
            command.Parameters["mode"] = "strategy"; command.Parameters["offerSecondEffect"] = "false";
            var use = AbilityActionCommand.Handle(state, registry, command);
            Assert.That(use.Succeeded, Is.True, use.Validation?.Reason);
            var q = FindOpenInteraction(state, "effect.choice.execute");
            var owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == q.OwnerEffectId);
            Assert.That(owner.NestedGroupSizes, Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(owner.NestedEffects.All(n => n.EffectTypeId == ResourceEffectTypeIds.Gain &&
                ItemTransferEffectExecutor.Number(n.NormalizedArguments, "amount") == 4 &&
                ItemTransferEffectExecutor.Number(n.NormalizedArguments, "apiVersion") == 2), Is.True);
            Assert.That(owner.ContinuationHandlerId, Is.Empty);
            Assert.That(owner.ChildEffectIds, Is.Empty);
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); state = recovered.State;
            q = FindOpenInteraction(state, "effect.choice.execute");
            var replay = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(q, 1), 1, new[] { candidate });
            Assert.That(Answer(state, registry, q, candidate).Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.Get(resource), Is.EqualTo(6));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.ParentEffectId == owner.EffectId && n.EffectTypeId == ResourceEffectTypeIds.Gain), Is.EqualTo(1));
            Assert.That(new AnswerInteractionCommandHandler(registry).Handle(state, replay).Succeeded, Is.False);
            recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(new EffectTreeExecutor(recovered.State, registry).RunUntilQuiescent().Faulted, Is.False,
                recovered.State.EffectRuntime.LastFaultCode + ":" + recovered.State.EffectRuntime.LastFaultMessage);
            Assert.That(recovered.State.FindPlayer(1).Resources.Get(resource), Is.EqualTo(6));
        }

        [Test]
        public void CurrentElysiumStaleMinimumBranchCannotGrantFour()
        {
            var state = CreateCurrentCharacterState(CharacterCardDatabase.Elysium, out var registry, out var card);
            var wallet = state.FindPlayer(1).Resources;
            wallet.Originium = 1; wallet.OriginiumShard = wallet.Iron = 3;
            var action = registry.QueryActions(state, 1).Single(option => option.SourceRef.ItemId == card);
            var command = AbilityActionCommand.Create(1, action); command.CommandId = "elysium-stale-minimum";
            command.Parameters["mode"] = "strategy"; command.Parameters["offerSecondEffect"] = "false";
            Assert.That(AbilityActionCommand.Handle(state, registry, command).Succeeded, Is.True);
            var q = FindOpenInteraction(state, "effect.choice.execute");
            Assert.That(q.CandidateIds, Is.EqualTo(new[] { "originium" }));
            // 隔离模拟构树后正式库存变化；执行子效果必须再次比较，不能只依赖候选快照。
            state.FindPlayer(1).Resources.Iron = 0;
            Answer(state, registry, q, "originium");
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(1));
            var gain = state.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == q.OwnerEffectId && n.EffectTypeId == ResourceEffectTypeIds.Gain);
            Assert.That(gain.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(gain.FailureReason, Is.EqualTo("resource_gain_not_minimum"));
        }

        [TestCase(false)][TestCase(true)]
        public void CurrentElysiumIncompatibleSavedBranchIdentityRejected(bool oldCompletion)
        {
            var state = CreateCurrentCharacterState(CharacterCardDatabase.Elysium, out var registry, out var card);
            var wallet = state.FindPlayer(1).Resources;
            wallet.Originium = wallet.OriginiumShard = wallet.Iron = 2;
            var action = registry.QueryActions(state, 1).Single(option => option.SourceRef.ItemId == card);
            var command = AbilityActionCommand.Create(1, action); command.CommandId = "elysium-old-identity";
            command.Parameters["mode"] = "strategy"; command.Parameters["offerSecondEffect"] = "false";
            Assert.That(AbilityActionCommand.Handle(state, registry, command).Succeeded, Is.True);
            var q = FindOpenInteraction(state, "effect.choice.execute");
            var archive = HostRecoveryService.CreateArchive(state);
            var owner = archive.Snapshot.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == q.OwnerEffectId);
            if (oldCompletion)
            {
                Assert.That(registry.HasEventHandler("characters.elysium.strategy.answer"), Is.False);
                owner.ContinuationHandlerId = "characters.elysium.strategy.answer";
                owner.ContinuationDefinitionVersion = "1.3.0";
            }
            else owner.NestedEffects[0].DefinitionVersion = "1.0.0";
            // 隔离损坏归档；不冒充历史原始存档样本。
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), registry);
            Assert.That(recovered.Succeeded, Is.False, recovered.Diagnostic);
            if (!oldCompletion) Assert.That(recovered.Diagnostic, Does.Contain("resource_gain_definition_version_mismatch"));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == q.OwnerEffectId).ChildEffectIds, Is.Empty);
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
            var state = CreateCurrentLiskarmState(out var registry, out var cardId);
            var action = registry.QueryActions(state, 1).Single(option => option.SourceRef.ItemId == cardId);
            var useCommand = AbilityActionCommand.Create(1, action);
            useCommand.CommandId = "liskarm-current-strategy";
            useCommand.Parameters["mode"] = "strategy";
            useCommand.Parameters["offerSecondEffect"] = "false";
            var use = AbilityActionCommand.Handle(state, registry, useCommand);
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
            var preparation = new EffectTreeExecutor(state, registry);
            if (noRemainingInfluence)
            {
                foreach (var own in state.Map.Influences.Where(value => value.PlayerId == 1).ToArray())
                {
                    preparation.CreateRoot(InfluenceEffectSpecFactory.RemoveInfluence(1, own.InfluenceId));
                    Assert.That(preparation.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                }
            }
            preparation.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(2, string.Empty));
            Assert.That(preparation.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var opponentPlacement = FindOpenInteraction(state, "effect.influence.place.target");
            var opponentAnswer = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(opponentPlacement, 2), 2,
                new[] { opponentPlacement.CandidateIds[0] });
            Assert.That(new AnswerInteractionCommandHandler(registry).Handle(state, opponentAnswer).Succeeded, Is.True);
            string opponentMarker = state.Map.Influences.Single(value => value.PlayerId == 2).InfluenceId;
            state = GameStateCloneService.DeepClone(state);
            registry = CreateCurrentCharacterRegistry(state);
            state.Phase = GamePhase.Cleanup;
            registry.Register(new EffectRegistration("test.cleanup", context =>
                EffectStepResult.Completed().AddEvent(new EffectEventRequest
                {
                    EventType = "PlayerCleanupStarted", PlayerId = 1,
                    OwnerNodeId = context.Node.EffectId, SourceEffectId = context.Node.EffectId
                })));
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(new EffectSpec("test.cleanup") { PlayerId = 1 });
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(FindOpenInteraction(state, "effect.influence.remove.target"), Is.Null, "收尾正文必须等待结算条目，不能自动执行。");
            Assert.That(state.Map.Influences.Count, Is.EqualTo(noRemainingInfluence ? 1 : 3));
            var cleanup = registry.GetSettlementOptions(state, 1).Single(option => option.LocalId == "character.liskarm.strategy.cleanup_order");
            Assert.That(cleanup.IsEnabled, Is.True, cleanup.DisabledReasonCode);
            var invoke = new GameCommand { Kind = GameCommandKind.InvokeSettlementOption, PlayerId = 1, CommandId = "liskarm-cleanup-contract" };
            invoke.Parameters["optionId"] = cleanup.OptionId;
            invoke.Parameters["useId"] = cleanup.TargetUseId;
            invoke.Parameters["expectedRevision"] = cleanup.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var invoked = new InvokeSettlementOptionCommandHandler(registry).Handle(state, invoke);
            Assert.That(invoked.Succeeded, Is.True, invoked.Validation?.Reason);
            var removal = FindOpenInteraction(state, "effect.influence.remove.target");
            if (noRemainingInfluence) Assert.That(removal, Is.Null);
            else
            {
                Assert.That(removal, Is.Not.Null);
                Assert.That(removal.CandidateIds, Has.Count.EqualTo(2));
                Assert.That(removal.CandidateIds, Does.Not.Contain(opponentMarker));
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
            // 规则隔离前置：用回合服务进入真实非先手窗口，避免把协议3自动双发误当作单模式。
            var texas = CreateCurrentNonStarterState(CharacterCardDatabase.Texas, out var texasRegistry, out var texasCard, out var actor);
            int texasGold = texas.FindPlayer(actor).Resources.GoldVoucher;
            Assert.That(UseCurrentCharacter(texas, texasRegistry, actor, texasCard, CharacterEffectModes.Strategy).Succeeded, Is.True);
            var facility = FindOpenInteraction(texas, "facility.supply.select");
            Assert.That(facility, Is.Not.Null);
            Assert.That(texas.FindPlayer(actor).Resources.GoldVoucher, Is.EqualTo(texasGold + 12), "先取得金券，再选择真实设施回底");
            var selected = facility.CandidateIds[0];
            Assert.That(texas.ItemZones.Items.Any(i => i.ItemId == selected), Is.True, "不再注入虚构f1");
            Assert.That(Answer(texas, texasRegistry, facility, selected).Succeeded, Is.True);
            Assert.That(texas.CharacterUses.Single(u => u.CardInstanceId == texasCard).Finished, Is.True);
            Assert.That(texas.PendingCharacterEffect, Is.Null);

            var cannot = CreateCurrentNonStarterState(CharacterCardDatabase.Cannot, out var cannotRegistry, out var cannotCard, out actor);
            cannot.FindPlayer(actor).Resources.Originium = 1;
            cannot.FindPlayer(actor).Resources.Iron = 0;
            var wallets = cannot.Players.ToDictionary(p => p.PlayerId, p => new[] { p.Resources.Iron, p.Resources.GoldVoucher });
            int score = cannot.FindPlayer(actor).Score;
            Assert.That(UseCurrentCharacter(cannot, cannotRegistry, actor, cannotCard, CharacterEffectModes.Tactic).Succeeded, Is.True);
            var requisition = FindOpenInteraction(cannot, "effect.choice.execute");
            Assert.That(requisition, Is.Not.Null);
            Assert.That(Answer(cannot, cannotRegistry, requisition, "iron").Succeeded, Is.True);
            foreach (var player in cannot.Players)
            {
                Assert.That(player.Resources.Iron, Is.Zero);
                Assert.That(player.Resources.GoldVoucher, Is.EqualTo(wallets[player.PlayerId][1] + 2 * wallets[player.PlayerId][0]));
            }
            Assert.That(cannot.FindPlayer(actor).Score, Is.EqualTo(score + 1));
            Assert.That(cannot.CharacterUses.Single(u => u.CardInstanceId == cannotCard).Finished, Is.True);
            Assert.That(cannot.PendingCharacterEffect, Is.Null);

            var tinMan = CreateCurrentNonStarterState(CharacterCardDatabase.TinMan, out var tinRegistry, out var tinCard, out actor);
            var playerState = tinMan.FindPlayer(actor); playerState.Resources.GoldVoucher = 12;
            int pure = playerState.Resources.PureOriginium;
            Assert.That(UseCurrentCharacter(tinMan, tinRegistry, actor, tinCard, CharacterEffectModes.Strategy).Succeeded, Is.True);
            var purchase = FindOpenInteraction(tinMan, "action.decline_effect");
            Assert.That(purchase, Is.Not.Null);
            Assert.That(Answer(tinMan, tinRegistry, purchase, "continue").Succeeded, Is.True);
            var finish = FindOpenInteraction(tinMan, "action.decline_effect");
            Assert.That(finish, Is.Not.Null);
            Assert.That(Answer(tinMan, tinRegistry, finish, "decline").Succeeded, Is.True);
            Assert.That(playerState.Resources.GoldVoucher, Is.Zero);
            Assert.That(playerState.Resources.PureOriginium, Is.EqualTo(pure + 1));
            Assert.That(tinMan.CharacterUses.Single(u => u.CardInstanceId == tinCard).Finished, Is.True);
            Assert.That(tinMan.PendingCharacterEffect, Is.Null);
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
            var request = FindOpenInteraction(state, "effect.choice.execute");
            Assert.That(request, Is.Not.Null);
            state = GameStateCloneService.DeepClone(state);
            registry = CreateCharacterAbilityRegistry();
            request = FindOpenInteraction(state, "effect.choice.execute");
            var replay = EffectInteractionCommands.Answer(
                YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1), 1, new[] { "iron" });
            var resolved = Answer(state, registry, request, "iron");
            Assert.That(resolved.Succeeded, Is.True, resolved.Validation == null ? "" : resolved.Validation.Reason);
            Assert.That(FindOpenInteraction(state, "effect.choice.execute"), Is.Null);
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
        public void CannotTactic_CurrentSourceRestoresRealExchangesAndScoresOnlyOnce()
        {
            var state = CreateCurrentCharacterState(CharacterCardDatabase.Cannot, out var registry, out var card);
            foreach (var player in state.Players) { player.Resources.Iron = player.PlayerId == 2 ? 51 : 0; player.Resources.GoldVoucher = 0; }
            var option = registry.QueryActions(state, 1).Single(a => a.SourceRef.ItemId == card);
            var command = AbilityActionCommand.Create(1, option); command.CommandId = "cannot-current-tactic";
            command.Parameters["mode"] = "tactic"; command.Parameters["offerSecondEffect"] = "false";
            Assert.That(AbilityActionCommand.Handle(state, registry, command).Succeeded, Is.True);
            var q = FindOpenInteraction(state, "effect.choice.execute");
            var node = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == q.OwnerEffectId);
            Assert.That(node.NestedGroupSizes, Is.EqualTo(new[] { 3, 3, 3 }));
            Assert.That(node.NestedEffects.All(n => n.EffectTypeId == ResourceExchangeFlow.TypeId), Is.True);
            Assert.That(node.ContinuationHandlerId, Is.EqualTo("characters.cannot.tactic.completed"));
            var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); state = restored.State;
            Assert.That(Answer(state, registry, FindOpenInteraction(state, "effect.choice.execute"), "iron").Succeeded, Is.True);
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.Zero);
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(102));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            Assert.That(new EffectTreeExecutor(restored.State, registry).RunUntilQuiescent().Faulted, Is.False, restored.State.EffectRuntime.LastFaultMessage);
            Assert.That(restored.State.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(102));
            Assert.That(restored.State.FindPlayer(1).Score, Is.EqualTo(1));
        }

        [Test]
        public void BothCharacterEffects_UsesPersistedOrderAndCompletesThroughSequence()
        {
            var state = CreateCurrentCharacterState(CharacterCardDatabase.TinMan, out var registry, out var cardId);
            var player = state.FindPlayer(1); player.Resources.GoldVoucher = 30;
            int pure = player.Resources.PureOriginium; int score = player.Score;
            string recoveredCard = MoveRealHandCardToDiscard(state, registry, 1);
            var result = UseCurrentCharacter(state, registry, 1, cardId, CharacterEffectModes.Both, CharacterEffectOrders.StrategyFirst);
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            var use = state.CharacterUses.Single(u => u.CardInstanceId == cardId);
            Assert.That(use.SelectionProtocolVersion, Is.EqualTo(3));
            Assert.That(use.ModeOrder, Is.EqualTo(new[] { "strategy", "tactic" }));
            var firstPurchase = FindOpenInteraction(state, "action.decline_effect");
            Assert.That(firstPurchase, Is.Not.Null);
            var useId = use.UseId;
            // 状态序列化往返的隔离续接；不计作Host冷恢复或正常页面验收。
            state = GameStateCloneService.DeepClone(state);
            registry = CreateCurrentCharacterRegistry(state); player = state.FindPlayer(1);
            Assert.That(state.CharacterUses.Single(u => u.UseId == useId).ModeOrder, Is.EqualTo(new[] { "strategy", "tactic" }));
            firstPurchase = FindOpenInteraction(state, "action.decline_effect");
            Assert.That(Answer(state, registry, firstPurchase, "continue").Succeeded, Is.True);
            var finishPurchase = FindOpenInteraction(state, "action.decline_effect");
            Assert.That(finishPurchase, Is.Not.Null);
            Assert.That(Answer(state, registry, finishPurchase, "decline").Succeeded, Is.True);
            var recall = FindOpenInteraction(state, "character.ability.choice.reward");
            Assert.That(recall, Is.Not.Null);
            Assert.That(Answer(state, registry, recall, CharacterEffectChoiceIds.GainGold).Succeeded, Is.True);
            use = state.CharacterUses.Single(u => u.UseId == useId);
            Assert.That(use.Finished, Is.True);
            Assert.That(use.CompletedModes, Is.EqualTo(new[] { "strategy", "tactic" }));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(23));
            Assert.That(player.Resources.PureOriginium, Is.EqualTo(pure + 1));
            Assert.That(player.Score, Is.EqualTo(score + 1));
            var hand = state.ItemZones.Zones.Single(z => z.OwnerPlayerId == 1 && z.LocalZoneId == "hand");
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == recoveredCard).ZoneId, Is.EqualTo(hand.ZoneId));
            Assert.That(state.CharacterCoverSlots.Any(s => s.CardInstanceId == cardId), Is.False);
            Assert.That(state.PendingCharacterEffect, Is.Null);
        }

        private static string MoveRealHandCardToDiscard(GameState state, EffectRegistry registry, int actor)
        {
            var hand = state.ItemZones.Zones.Single(z => z.OwnerPlayerId == actor && z.LocalZoneId == "hand");
            var discard = state.ItemZones.Zones.Single(z => z.OwnerPlayerId == actor && z.LocalZoneId == "discard");
            Assert.That(state.ItemZones.Items.Any(i => i.ZoneId == discard.ZoneId), Is.False);
            var card = state.ItemZones.Items.First(i => i.ZoneId == hand.ZoneId && i.TypeId == "core.basic_character_card");
            var args = ItemTransferEffectExecutor.Object("apiVersion", NormalizedValue.CreateInteger(4),
                "player", NormalizedValue.CreateString(actor.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                "operation", NormalizedValue.CreateString("transfer"),
                "selector", ItemTransferEffectExecutor.Object("mode", NormalizedValue.CreateString("explicit"),
                    "itemIds", ItemTransferEffectExecutor.Strings(new[] { card.ItemId }),
                    "expectedSource", ItemTransferEffectExecutor.Object("zoneId", NormalizedValue.CreateString(hand.ZoneId))),
                "destination", ItemTransferEffectExecutor.Object("zoneId", NormalizedValue.CreateString(discard.ZoneId)),
                "insertion", ItemTransferEffectExecutor.Object("kind", NormalizedValue.CreateString("unordered")),
                "operationProfileId", NormalizedValue.CreateString("core.character_card_transfer"), "requiredCount", NormalizedValue.CreateInteger(1));
            var engine = new EffectTreeExecutor(state, registry);
            var root = engine.CreateRoot(EffectSpec.Create(ItemTransferEffectExecutor.TypeId, args, actor));
            var run = engine.RunUntilQuiescent();
            Assert.That(run.Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed), engine.GetNode(root).FailureReason);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == card.ItemId).ZoneId, Is.EqualTo(discard.ZoneId));
            return card.ItemId;
        }

        private static CommandResult UseCurrentCharacter(GameState state, EffectRegistry registry, int actor,
            string cardId, string mode, string order = "")
        {
            var option = registry.QueryActions(state, actor).Single(o => o.SourceRef.ItemId == cardId);
            var command = AbilityActionCommand.Create(actor, option);
            command.CommandId = "nmc010-current-" + actor + "-" + mode;
            command.Parameters["mode"] = mode;
            if (order.Length > 0) command.Parameters["order"] = order;
            return AbilityActionCommand.Handle(state, registry, command);
        }

        private static GameState CreateCurrentNonStarterState(string template, out EffectRegistry registry,
            out string cardId, out int actor)
        {
            var state = CreateCurrentCharacterState(template, out registry, out _);
            var advance = new RoundExecutionService(registry).CompleteMainAction(state, 1);
            Assert.That(advance.IsValid, Is.True, advance.Reason);
            actor = state.CurrentPlayerId;
            Assert.That(actor, Is.EqualTo(2));
            Assert.That(actor, Is.Not.EqualTo(state.StartPlayerId));
            int localActor = actor;
            cardId = state.CharacterCoverSlots.Single(s => s.PlayerId == localActor).CardInstanceId;
            return state;
        }

        private static void BindCurrentCoverRequest(GameState state, GameCommand command)
        {
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" &&
                r.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId && r.AnsweringPlayerId == command.PlayerId);
            Assert.That(request.CandidateIds, Does.Contain(command.TargetId));
            command.CommandId = "nmc010-cover-" + request.GetStableInteractionId();
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

        private static GameState CreateCurrentLiskarmState(out EffectRegistry registry, out string cardId)
            => CreateCurrentCharacterState(CharacterCardDatabase.Liskarm, out registry, out cardId);

        private static GameState CreateCurrentCharacterState(string template, out EffectRegistry registry, out string cardId)
        {
            var state = CreateCurrentCoverState(out registry);
            var cover = new CoverCharacterCardCommandHandler(new CharacterCardService(), registry);
            cardId = null;
            foreach (var seat in state.Players.ToArray())
            {
                var card = state.ItemZones.Items.First(item => item.ControllerSubject.PlayerId == seat.PlayerId &&
                    item.DefinitionId == template && state.ItemZones.Zones.Any(zone =>
                        zone.ZoneId == item.ZoneId && zone.TemplateId == "core.zone.character_hand"));
                var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open" &&
                    value.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId && value.AnsweringPlayerId == seat.PlayerId);
                var command = new GameCommand { Kind = GameCommandKind.CoverCharacterCard, PlayerId = seat.PlayerId, TargetId = card.ItemId };
                command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
                command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var covered = cover.Handle(state, command);
                Assert.That(covered.Succeeded, Is.True, covered.Validation?.Reason);
                if (seat.PlayerId == 1) cardId = card.ItemId;
            }
            return state;
        }

        private static GameState CreateCurrentCoverState(out EffectRegistry registry)
        {
            var seats = new List<PlayerSeat> {
                new PlayerSeat { PlayerId = 1, Color = PlayerColor.Red },
                new PlayerSeat { PlayerId = 2, Color = PlayerColor.Blue },
                new PlayerSeat { PlayerId = 3, Color = PlayerColor.Green } };
            var configuration = new GameRuleConfiguration { PlayerCount = 3, MapId = StaticMapDefinitions.ThreePlayerMapId };
            var catalog = ActiveContentCatalogBuilder.Build(ExternalContentPack.Current, configuration);
            configuration.ContentPackHash = catalog.ContentPackHash;
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, seats, configuration, 71,
                catalog.SetupRecipe, catalog.ContentDefinitions);
            registry = CreateCurrentCharacterRegistry(state);
            var started = new RoundExecutionService(registry).CreateRound(state, 1, 1);
            Assert.That(started.IsValid, Is.True, started.Reason);
            return state;
        }

        private static EffectRegistry CreateCurrentCharacterRegistry(GameState state)
        {
            var registry = new EffectRegistry();
            var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId));
            var influence = new InfluenceService(map);
            InfluenceEffectExecutor.Register(registry, influence);
            var travel = new TravelCostService(map);
            CityMoveEffectExecutor.Register(registry, map, influence, new CityMovementService(map, influence, travel), travel, new ResourceTokenService());
            new RoundExecutionService(registry);
            ResourceEffectExecutor.Register(registry);
            LuaContentCatalog.Register(registry, ActiveContentCatalogBuilder.Build(ExternalContentPack.Current, state.RuleConfiguration));
            return registry;
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
                YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, request.AnsweringPlayerId),
                request.AnsweringPlayerId, new[] { candidateId });
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
