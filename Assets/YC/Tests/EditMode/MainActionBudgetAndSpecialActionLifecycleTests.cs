using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class MainActionBudgetAndSpecialActionLifecycleTests
    {
        [Test]
        public void MainActionBudget_GrantsTwoAdditionalActions_AndFailedCommandDoesNotSpend()
        {
            var state = CreateActionState();
            AddResourceToken(state, "city-a");
            AddResourceToken(state, "harbor-c");
            var handler = new DeployInfluenceCommandHandler(CreateInfluenceService());
            var budget = new MainActionBudgetService();

            var first = handler.Handle(state, DeployTo(InfluenceService.GetRouteSlotId("route-a-b", 0)));

            Assert.That(first.Succeeded, Is.True, first.Validation.Reason);
            AssertBudget(state, 0, 1, true);

            budget.GrantAdditionalMainActions(state, 1, 2);
            AssertBudget(state, 2, 1, false);

            var failed = handler.Handle(state, DeployTo("missing-slot"));

            Assert.That(failed.Succeeded, Is.False);
            AssertBudget(state, 2, 1, false);

            var second = handler.Handle(state, DeployTo(InfluenceService.GetLocationSlotId("city-a", 0)));

            Assert.That(second.Succeeded, Is.True, second.Validation.Reason);
            AssertBudget(state, 1, 2, false);

            var third = handler.Handle(state, DeployTo(InfluenceService.GetLocationSlotId("harbor-c", 0)));

            Assert.That(third.Succeeded, Is.True, third.Validation.Reason);
            AssertBudget(state, 0, 3, true);
        }

        [Test]
        public void EndActionTurn_DiscardsUnusedBudgetAndClearsTurnCharacterFlagsOnly()
        {
            var state = CreateActionState();
            var player = state.FindPlayer(1);
            player.RemainingMainActionsThisTurn = 2;
            player.CompletedMainActionsThisTurn = 1;
            player.ActedMainActionThisTurn = false;
            player.UsedCharacterThisRound = true;
            player.UsedCharacterThisTurn = true;
            player.CharacterCardLockedThisTurn = true;

            var result = new RoundAdvanceService().EndCompletedAction(state, 1);

            Assert.That(result.IsValid, Is.True, result.Reason);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            AssertBudget(state, 0, 1, true);
            Assert.That(player.UsedCharacterThisTurn, Is.False);
            Assert.That(player.CharacterCardLockedThisTurn, Is.False);
            Assert.That(player.UsedCharacterThisRound, Is.True);
        }

        [Test]
        public void AdvanceActionRound_ResetsEveryPlayersBudgetAndTurnCharacterFlags()
        {
            var state = CreateActionState();
            var first = state.FindPlayer(1);
            first.RemainingMainActionsThisTurn = 0;
            first.CompletedMainActionsThisTurn = 1;
            first.ActedMainActionThisTurn = true;
            first.UsedCharacterThisRound = true;
            first.UsedCharacterThisTurn = true;
            first.CharacterCardLockedThisTurn = true;

            var second = state.FindPlayer(2);
            second.RemainingMainActionsThisTurn = 2;
            second.CompletedMainActionsThisTurn = 1;
            second.ActedMainActionThisTurn = false;
            second.UsedCharacterThisTurn = true;
            second.CharacterCardLockedThisTurn = true;
            state.CurrentPlayerId = 2;

            var result = new RoundAdvanceService().EndCompletedAction(state, 2);

            Assert.That(result.IsValid, Is.True, result.Reason);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound2));
            Assert.That(state.ActionRound, Is.EqualTo(2));
            for (var i = 0; i < state.Players.Count; i++)
            {
                Assert.That(state.Players[i].RemainingMainActionsThisTurn, Is.EqualTo(1));
                Assert.That(state.Players[i].CompletedMainActionsThisTurn, Is.Zero);
                Assert.That(state.Players[i].ActedMainActionThisTurn, Is.False);
                Assert.That(state.Players[i].UsedCharacterThisTurn, Is.False);
                Assert.That(state.Players[i].CharacterCardLockedThisTurn, Is.False);
            }

            Assert.That(first.UsedCharacterThisRound, Is.True);
        }

        [Test]
        public void CharacterCard_WhenLockedBySpecialAction_IsRejectedWithoutMutation()
        {
            var state = CreateCharacterActionState(CharacterCardDatabase.Liskarm);
            var player = state.FindPlayer(1);
            player.CharacterCardLockedThisTurn = true;
            var command = CreateLiskarmStrategyCommand(player.CoveredCharacterCardId);

            var result = new UseCharacterCardCommandHandler().Handle(state, command);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(player.CoveredCharacterCardId, Is.EqualTo(command.TargetId));
            Assert.That(player.UsedCharacterThisRound, Is.False);
            Assert.That(player.UsedCharacterThisTurn, Is.False);
            Assert.That(state.Map.Influences, Is.Empty);
            AssertBudget(state, 1, 0, false);
        }

        [Test]
        public void CharacterCardProjection_PreservesActionBudgetAndMarksBothCharacterUsageFlags()
        {
            var state = CreateCharacterActionState(CharacterCardDatabase.Liskarm);
            var player = state.FindPlayer(1);
            player.RemainingMainActionsThisTurn = 2;
            player.CompletedMainActionsThisTurn = 1;
            player.ActedMainActionThisTurn = false;

            var result = new UseCharacterCardCommandHandler().Handle(
                state,
                CreateLiskarmStrategyCommand(player.CoveredCharacterCardId));

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            AssertBudget(state, 2, 1, false);
            Assert.That(player.UsedCharacterThisRound, Is.True);
            Assert.That(player.UsedCharacterThisTurn, Is.True);
            Assert.That(player.CharacterCardLockedThisTurn, Is.False);
            Assert.That(state.Map.Influences, Has.Count.EqualTo(2));
        }

        private static GameState CreateActionState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                Round = 1,
                ActionRound = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState { PlayerId = 1, Color = PlayerColor.Red },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Blue }
                }
            };
        }

        private static GameState CreateCharacterActionState(string templateId)
        {
            var player = new PlayerState { PlayerId = 1, Color = PlayerColor.Red };
            var cardId = "character.red.p1." + templateId;
            player.CoveredCharacterCardId = cardId;
            player.CoveredCharacterCardIds.Add(cardId);
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                Round = 1,
                ActionRound = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players = { player }
            };
        }

        private static InfluenceService CreateInfluenceService()
        {
            return new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder()));
        }

        private static GameCommand DeployTo(string targetId)
        {
            return new GameCommand
            {
                Kind = GameCommandKind.DeployInfluence,
                PlayerId = 1,
                TargetId = targetId
            };
        }

        private static GameCommand CreateLiskarmStrategyCommand(string cardId)
        {
            var command = new GameCommand
            {
                Kind = GameCommandKind.UseCharacterCard,
                PlayerId = 1,
                TargetId = cardId
            };
            command.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            command.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] = CharacterEffectModes.Strategy;
            command.Parameters[CharacterEffectParameterKeys.PlacementSlotId1] = InfluenceService.GetRouteSlotId("A1", 0);
            command.Parameters[CharacterEffectParameterKeys.PlacementSlotId2] = InfluenceService.GetRouteSlotId("B1", 0);
            return command;
        }

        private static void AddResourceToken(GameState state, string locationId)
        {
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = locationId,
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
        }

        private static void AssertBudget(GameState state, int remaining, int completed, bool acted)
        {
            var player = state.FindPlayer(1);
            Assert.That(player.RemainingMainActionsThisTurn, Is.EqualTo(remaining));
            Assert.That(player.CompletedMainActionsThisTurn, Is.EqualTo(completed));
            Assert.That(player.ActedMainActionThisTurn, Is.EqualTo(acted));
        }
    }
}
