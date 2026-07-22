using System;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    internal static class MainActionCommandGuard
    {
        private static readonly MainActionBudgetService MainActionBudgetService = new MainActionBudgetService();

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
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "命令玩家不存在。");
            }

            if (state.CurrentPlayerId != command.PlayerId)
            {
                return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "命令玩家不是当前玩家。");
            }

            if (state.HasPendingChoice())
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "请先处理待选择项，再提交其他行动。");
            }

            return MainActionBudgetService.ValidateCanSpend(state, command.PlayerId);
        }
    }
}
