using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Setup;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class NMC005RoundExecutionEditModeTests
    {
        [TestCase(2, 10)]
        [TestCase(3, 13)]
        [TestCase(4, 16)]
        public void MainChain_UsesFixedTopologyStableIdsAndPlayerSnapshot(int playerCount, int expectedNodeCount)
        {
            var order = CreatePlayerOrder(playerCount);
            var builder = new RoundMainChainBuilder();
            var first = builder.Build("nmc005-chain", "round-execution-fixed", 3, order, order[0]);
            var rebuilt = builder.Build("nmc005-chain", "round-execution-fixed", 3, order, order[0]);

            Assert.That(first.Nodes, Has.Count.EqualTo(expectedNodeCount));
            Assert.That(first.PlayerOrderSnapshot, Is.EqualTo(order));
            Assert.That(first.FirstNodeId, Is.EqualTo(first.Nodes[0].NodeId));
            Assert.That(first.Nodes[0].NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.RoundStarted));
            Assert.That(first.Nodes[1].NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.CharacterCover));
            Assert.That(first.Nodes[first.Nodes.Count - 1].NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.RoundEnded));

            var actionRoundOne = new List<int>();
            var actionRoundTwo = new List<int>();
            var cleanupPlayers = new List<int>();
            for (var i = 0; i < first.Nodes.Count; i++)
            {
                var node = first.Nodes[i];
                Assert.That(node.RoundExecutionId, Is.EqualTo(first.RoundExecutionId));
                Assert.That(node.NodeId, Is.EqualTo(rebuilt.Nodes[i].NodeId));
                if (i > 0)
                {
                    Assert.That(node.PreviousNodeId, Is.EqualTo(first.Nodes[i - 1].NodeId));
                    Assert.That(first.Nodes[i - 1].NextNodeId, Is.EqualTo(node.NodeId));
                }

                if (node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && node.ActionRound == 1)
                {
                    actionRoundOne.Add(node.PlayerId);
                }
                else if (node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && node.ActionRound == 2)
                {
                    actionRoundTwo.Add(node.PlayerId);
                }
                else if (node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow)
                {
                    cleanupPlayers.Add(node.PlayerId);
                }
            }

            Assert.That(first.Nodes[0].PreviousNodeId, Is.Empty);
            Assert.That(first.Nodes[first.Nodes.Count - 1].NextNodeId, Is.Empty);
            Assert.That(actionRoundOne, Is.EqualTo(order));
            Assert.That(actionRoundTwo, Is.EqualTo(order));
            Assert.That(cleanupPlayers, Is.EqualTo(order));
        }

        [Test]
        public void SetupCompletion_EntersRoundStartedThenEmptyResponseAdvancesToCharacterCover()
        {
            var state = new GameState
            {
                Phase = GamePhase.Entrance,
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
            var handler = PlayerEntranceMainlineTests.Handler(new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder()));

            var first = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = 1,
                TargetId = "city-a"
            });
            var second = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = 2,
                TargetId = "harbor-c"
            });

            Assert.That(first.Succeeded, Is.True, first.Validation == null ? string.Empty : first.Validation.Reason);
            Assert.That(second.Succeeded, Is.True, second.Validation == null ? string.Empty : second.Validation.Reason);
            Assert.That(state.Round, Is.EqualTo(1));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(state.EffectRuntime.ActiveMainNodeId, Is.Not.Empty);
            Assert.That(state.EffectRuntime.MainNodes.Find(node => node.NodeId == state.EffectRuntime.ActiveMainNodeId).NodeTypeId,
                Is.EqualTo(RoundMainlineNodeTypeIds.CharacterCover));
            Assert.That(state.EffectRuntime.RuleEvents, Has.Some.Matches<RuleEvent>(ruleEvent => ruleEvent.EventType == "RoundStarted"));
            Assert.That(state.EffectRuntime.InteractionRequests, Has.None.Matches<InteractionRequest>(request =>
                request.InteractionTypeId.StartsWith(RoundExecutionService.MainlineCompletionInteractionTypeId, StringComparison.Ordinal) &&
                request.OwnerEffectId == state.EffectRuntime.MainNodes[0].ExecutionEffectId));
        }

        [Test]
        public void FullRound_CompletesPerPlayerWindowsAndCreatesNextRound()
        {
            var state = CreateState(2);
            state.FindPlayer(1).CoveredCharacterCardId = "character.p1";
            state.FindPlayer(2).CoveredCharacterCardId = "character.p2";
            var service = new RoundExecutionService();

            AssertValid(service.CreateRound(state, 1, 1));
            // 已从持久化状态恢复的玩家不重新询问盖放；统一盖放主节点直接完成。
            Assert.That(GetActiveNode(state).NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.PlayerActionWindow));

            for (var actionRound = 1; actionRound <= 2; actionRound++)
            {
                for (var i = 1; i <= 2; i++)
                {
                    AssertValid(service.CompleteMainAction(state, i));
                }
            }

            Assert.That(GetActiveNode(state).NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.Collection));
            state.FindPlayer(1).HasCollectedResourcesThisRound = true;
            AssertValid(service.CompleteResourceCollection(state, 1));
            Assert.That(GetActiveNode(state).NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.Collection));
            state.FindPlayer(2).HasCollectedResourcesThisRound = true;
            AssertValid(service.CompleteResourceCollection(state, 2));

            // 空收尾窗口由主链自动完成，完成采集后直接进入下一回合的盖放节点。
            Assert.That(GetActiveNode(state).NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.CharacterCover));

            Assert.That(state.Round, Is.EqualTo(2));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(GetActiveNode(state).NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.CharacterCover));
            Assert.That(state.EffectRuntime.CurrentRoundNumber, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.ActiveMainNodeId, Is.Not.Empty);
        }

        [Test]
        public void NextRoundCreation_IsRejectedWhileEffectOrInteractionRemainsOpen()
        {
            var state = CreateState(2);
            state.EffectRuntime.CurrentRoundNumber = 1;
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState
            {
                EffectId = "nmc005-open-effect",
                EffectTypeId = "event_card.option",
                Status = EffectNodeStatus.Blocked
            });
            state.EffectRuntime.InteractionRequests.Add(new InteractionRequest
            {
                InteractionId = "nmc005-open-interaction",
                RequestId = "nmc005-open-interaction",
                InteractionTypeId = "event_card.option",
                OwnerEffectId = "nmc005-open-effect",
                AnsweringPlayerId = 1,
                Status = "open",
                CandidateIds = new List<string> { "event_green_01:option:0" },
                MinSelections = 1,
                MaxSelections = 1,
                StateRevision = 1
            });

            var service = new RoundExecutionService();
            var result = service.ValidateNextRoundCreation(state);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo(CommandErrorCode.PendingChoiceRequired));
            Assert.That(result.Reason, Does.Contain("nmc005-open-interaction"));
            Assert.That(state.EffectRuntime.CurrentRoundNumber, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests[0].Status, Is.EqualTo("open"));
            Assert.That(state.EffectRuntime.EffectNodes[0].Status, Is.EqualTo(EffectNodeStatus.Blocked));

            var createNext = service.CreateRound(state, 2, 1);
            Assert.That(createNext.IsValid, Is.False);
            Assert.That(state.EffectRuntime.CurrentRoundNumber, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.MainNodes, Is.Empty);
        }

        [Test]
        public void RoundStartedHandlerFault_PausesWithoutAdvancingMainChain()
        {
            var registry = new EffectRegistry();
            registry.RegisterEventHandler(new EffectEventHandlerRegistration(
                "nmc005.fault",
                "RoundStarted",
                "",
                "nmc005.fault.handler",
                context => throw new InvalidOperationException("nmc005 test fault"),
                EffectHandlerRole.Primary));
            var service = new RoundExecutionService(
                new TurnOrderService(),
                new CharacterCardService(),
                new MainActionBudgetService(),
                new SpecialActionLifecycleService(),
                registry);
            var state = CreateState(2);

            var result = service.CreateRound(state, 1, 1);

            Assert.That(result.IsValid, Is.False);
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(state.EffectRuntime.ActiveMainNodeId, Is.Not.Empty);
            Assert.That(GetActiveNode(state).NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.RoundStarted));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.RoundStart));
        }

        [Test]
        public void InternalMainlineCompletion_IsNotReportedAsPlayerPendingChoice()
        {
            var state = CreateState(2);
            state.EffectRuntime.InteractionRequests.Add(new InteractionRequest
            {
                InteractionId = "mainline-completion",
                RequestId = "mainline-completion",
                InteractionTypeId = "round.mainline.complete:7",
                AnsweringPlayerId = 1,
                Status = "open"
            });

            Assert.That(state.HasOpenInteraction(), Is.True);
            Assert.That(state.HasOpenActionableInteraction(), Is.False);
            Assert.That(state.HasPendingChoice(), Is.False);
        }

        [Test]
        public void CharacterAbilityInteraction_RemainsPlayerPendingChoice()
        {
            var state = CreateState(2);
            state.EffectRuntime.InteractionRequests.Add(new InteractionRequest
            {
                InteractionId = "ability-choice",
                RequestId = "ability-choice",
                InteractionTypeId = CharacterAbilityEffectExecutor.ChoiceInteractionTypeId + ".awaiting",
                AnsweringPlayerId = 1,
                Status = "open"
            });

            Assert.That(state.HasOpenActionableInteraction(), Is.True);
            Assert.That(state.HasPendingChoice(), Is.True);
        }

        private static GameState CreateState(int playerCount)
        {
            var state = new GameState
            {
                GameId = "nmc005-round",
                Phase = GamePhase.Setup,
                StartPlayerId = 1,
                Players = new List<PlayerState>()
            };
            for (var i = 1; i <= playerCount; i++)
            {
                state.Players.Add(new PlayerState { PlayerId = i, Color = (PlayerColor)(i - 1) });
            }

            return state;
        }

        private static List<int> CreatePlayerOrder(int count)
        {
            var order = new List<int>();
            for (var i = 1; i <= count; i++)
            {
                order.Add(i);
            }

            return order;
        }

        private static MainlineNodeRuntimeState GetActiveNode(GameState state)
        {
            return state.EffectRuntime.MainNodes.Find(node => node.NodeId == state.EffectRuntime.ActiveMainNodeId);
        }

        private static void AssertValid(ValidationResult result)
        {
            Assert.That(result.IsValid, Is.True, result.Reason);
        }
    }
}

