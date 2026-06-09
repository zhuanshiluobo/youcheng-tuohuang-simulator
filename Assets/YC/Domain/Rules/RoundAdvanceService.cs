using System;
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
            if (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2)
            {
                return;
            }

            if (AllPlayersActed(state))
            {
                AdvanceActionRound(state);
                return;
            }

            state.CurrentPlayerId = FindNextUnactedPlayerId(state);
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

        private void AdvanceActionRound(GameState state)
        {
            ResetActionFlags(state);

            if (state.Phase == GamePhase.ActionRound1)
            {
                state.Phase = GamePhase.ActionRound2;
                state.ActionRound = 2;
                state.CurrentPlayerId = state.StartPlayerId;
                return;
            }

            if (state.Phase == GamePhase.ActionRound2)
            {
                state.Phase = GamePhase.ResourceCollection;
                state.ActionRound = 0;
                state.CurrentPlayerId = state.StartPlayerId;
            }
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
