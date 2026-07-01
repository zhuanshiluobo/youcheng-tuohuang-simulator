using System;
using YC.Domain.Commands;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    public sealed class RoundAdvanceService
    {
        private readonly TurnOrderService turnOrderService;

        public RoundAdvanceService()
            : this(new TurnOrderService())
        {
        }

        public RoundAdvanceService(TurnOrderService turnOrderService)
        {
            this.turnOrderService = turnOrderService ?? throw new ArgumentNullException(nameof(turnOrderService));
        }

        public void CompleteMainAction(GameState state, int playerId)
        {
            MarkMainActionComplete(state, playerId);
            EndCompletedAction(state, playerId);
        }

        public void MarkMainActionComplete(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return;
            }

            player.ActedMainActionThisTurn = true;
        }

        public ValidationResult EndCompletedAction(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (state.Phase == GamePhase.Cleanup)
            {
                return EndCleanup(state, playerId);
            }

            if (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2)
            {
                return ValidationResult.Failure(CommandErrorCode.WrongPhase, "End action is only available during action rounds.");
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "Player must exist before ending an action.");
            }

            if (state.CurrentPlayerId != playerId)
            {
                return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "Only the current player can end their action.");
            }

            if (state.HasPendingChoice())
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "Resolve the pending choice before ending the action.");
            }

            if (!player.ActedMainActionThisTurn)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Complete a main action before ending the action.");
            }

            if (AllPlayersActed(state))
            {
                AdvanceActionRound(state);
                return ValidationResult.Success;
            }

            state.CurrentPlayerId = FindNextUnactedPlayerId(state);
            return ValidationResult.Success;
        }

        public void ResetActionFlags(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                state.Players[i].ActedMainActionThisTurn = false;
            }
        }

        public bool AllPlayersCollectedResources(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (state.Players.Count <= 0)
            {
                return false;
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                if (!state.Players[i].HasCollectedResourcesThisRound)
                {
                    return false;
                }
            }

            return true;
        }

        public void AdvanceResourceCollectionToCleanup(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            state.Phase = GamePhase.Cleanup;
            state.ActionRound = 0;
            state.CurrentPlayerId = state.StartPlayerId;
        }

        private void AdvanceActionRound(GameState state)
        {
            ResetActionFlags(state);

            if (state.Phase == GamePhase.ActionRound1)
            {
                state.Phase = GamePhase.ActionRound2;
                state.ActionRound = 2;
                state.CurrentPlayerId = GetFirstTurnPlayerId(state);
                return;
            }

            if (state.Phase == GamePhase.ActionRound2)
            {
                AdvanceToResourceCollection(state);
            }
        }

        private void AdvanceToResourceCollection(GameState state)
        {
            state.Phase = GamePhase.ResourceCollection;
            state.ActionRound = 0;
            state.CurrentPlayerId = GetFirstTurnPlayerId(state);

            for (var i = 0; i < state.Players.Count; i++)
            {
                state.Players[i].HasCollectedResourcesThisRound = false;
                state.Players[i].ResourceCollectionStartGoldVoucher = state.Players[i].Resources.GoldVoucher;
            }
        }

        private ValidationResult EndCleanup(GameState state, int playerId)
        {
            if (state.HasPendingChoice())
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "Resolve the pending choice before cleanup.");
            }

            if (playerId != state.StartPlayerId && playerId != state.CurrentPlayerId)
            {
                return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "Only the start player can end cleanup.");
            }

            if (state.Round >= state.MaxRounds)
            {
                state.Phase = GamePhase.FinalScoring;
                state.ActionRound = 0;
                return ValidationResult.Success;
            }

            state.StartPlayerId = GetNextStartPlayerId(state);
            state.CurrentPlayerId = GetFirstTurnPlayerId(state);
            state.Round += 1;
            state.Phase = GamePhase.ActionRound1;
            state.ActionRound = 1;

            for (var i = 0; i < state.Players.Count; i++)
            {
                state.Players[i].ActedMainActionThisTurn = false;
                state.Players[i].HasMovedCityThisRound = false;
                state.Players[i].HasCollectedResourcesThisRound = false;
                state.Players[i].ResourceCollectionStartGoldVoucher = -1;
            }

            return ValidationResult.Success;
        }

        private static bool AllPlayersActed(GameState state)
        {
            for (var i = 0; i < state.Players.Count; i++)
            {
                if (!state.Players[i].ActedMainActionThisTurn)
                {
                    return false;
                }
            }

            return state.Players.Count > 0;
        }

        private int GetFirstTurnPlayerId(GameState state)
        {
            var order = turnOrderService.GetTurnOrder(state);
            return order.Count > 0 ? order[0] : state.StartPlayerId;
        }

        private int GetNextStartPlayerId(GameState state)
        {
            var order = turnOrderService.GetTurnOrder(state);
            if (order.Count <= 0)
            {
                return state.StartPlayerId;
            }

            var currentIndex = 0;
            for (var i = 0; i < order.Count; i++)
            {
                if (order[i] == state.StartPlayerId)
                {
                    currentIndex = i;
                    break;
                }
            }

            return order[(currentIndex + 1) % order.Count];
        }

        private int FindNextUnactedPlayerId(GameState state)
        {
            var order = turnOrderService.GetTurnOrder(state);
            var currentIndex = 0;
            for (var i = 0; i < order.Count; i++)
            {
                if (order[i] == state.CurrentPlayerId)
                {
                    currentIndex = i;
                    break;
                }
            }

            for (var offset = 1; offset <= order.Count; offset++)
            {
                var playerId = order[(currentIndex + offset) % order.Count];
                var player = state.FindPlayer(playerId);
                if (player != null && !player.ActedMainActionThisTurn)
                {
                    return playerId;
                }
            }

            return state.CurrentPlayerId;
        }
    }
}
