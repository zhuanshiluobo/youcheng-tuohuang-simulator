using System;
using YC.Domain.Commands;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    public sealed class MainActionBudgetService
    {
        public const int DefaultMainActionsPerTurn = 1;

        public ValidationResult ValidateCanSpend(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "执行主要行动的玩家不存在。");
            }

            if (player.ActedMainActionThisTurn || player.RemainingMainActionsThisTurn <= 0)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "本玩家行动轮的主要行动次数已用尽。");
            }

            return ValidationResult.Success;
        }

        public bool HasAvailableMainAction(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            return player != null &&
                   !player.ActedMainActionThisTurn &&
                   player.RemainingMainActionsThisTurn > 0;
        }

        public void SpendCompletedMainAction(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null ||
                player.ActedMainActionThisTurn ||
                player.RemainingMainActionsThisTurn <= 0)
            {
                return;
            }

            player.RemainingMainActionsThisTurn -= 1;
            player.CompletedMainActionsThisTurn += 1;
            player.ActedMainActionThisTurn = player.RemainingMainActionsThisTurn <= 0;
        }

        public void GrantAdditionalMainActions(GameState state, int playerId, int amount)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (amount <= 0)
            {
                return;
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return;
            }

            player.RemainingMainActionsThisTurn += amount;
            player.ActedMainActionThisTurn = false;
        }

        public void LockCharacterCard(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player != null)
            {
                player.CharacterCardLockedThisTurn = true;
            }
        }

        public void ResetForNewActionTurn(GameState state, int playerId)
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

            player.RemainingMainActionsThisTurn = DefaultMainActionsPerTurn;
            player.CompletedMainActionsThisTurn = 0;
            player.ActedMainActionThisTurn = false;
            player.UsedCharacterThisTurn = false;
            player.CharacterCardLockedThisTurn = false;
        }

        public void EndActionTurn(GameState state, int playerId)
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

            player.RemainingMainActionsThisTurn = 0;
            player.ActedMainActionThisTurn = true;
            player.UsedCharacterThisTurn = false;
            player.CharacterCardLockedThisTurn = false;
        }
    }
}
