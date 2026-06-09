using System;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    internal static class MainActionCommandGuard
    {
        public static ValidationResult Validate(GameState state, GameCommand command)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            var phaseValidation = CommandPhasePolicy.ValidatePhase(state, command.Kind);
            if (!phaseValidation.IsValid)
            {
                return phaseValidation;
            }

            var player = state.FindPlayer(command.PlayerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "Command player does not exist.");
            }

            if (state.CurrentPlayerId != command.PlayerId)
            {
                return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "Command player is not the current player.");
            }

            if (state.HasPendingChoice())
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "Resolve the pending choice before submitting another action.");
            }

            if (player.ActedMainActionThisTurn)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Player already resolved a main action this turn.");
            }

            return ValidationResult.Success;
        }
    }
}
