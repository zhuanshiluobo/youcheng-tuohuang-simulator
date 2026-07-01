using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class EndActionCommandHandler : IGameCommandHandler
    {
        private readonly RoundAdvanceService roundAdvanceService;
        private readonly FinalScoringService finalScoringService;

        public EndActionCommandHandler()
            : this(new RoundAdvanceService())
        {
        }

        public EndActionCommandHandler(RoundAdvanceService roundAdvanceService)
            : this(roundAdvanceService, null)
        {
        }

        public EndActionCommandHandler(RoundAdvanceService roundAdvanceService, FinalScoringService finalScoringService)
        {
            this.roundAdvanceService = roundAdvanceService;
            this.finalScoringService = finalScoringService;
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null && command.Kind == GameCommandKind.EndAction;
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            var phaseValidation = CommandPhasePolicy.ValidatePhase(state, command.Kind);
            if (!phaseValidation.IsValid)
            {
                return CommandResult.Invalid(phaseValidation);
            }

            var validation = roundAdvanceService.EndCompletedAction(state, command.PlayerId);
            if (!validation.IsValid)
            {
                return CommandResult.Invalid(validation);
            }

            if (state.Phase == GamePhase.FinalScoring && finalScoringService != null)
            {
                var scoring = finalScoringService.Resolve(state);
                if (!scoring.Succeeded)
                {
                    return CommandResult.Invalid(scoring.Validation);
                }
            }

            var message = "Player " + command.PlayerId + " ended their action.";
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.PlayerAdvanced,
                    PlayerId = command.PlayerId,
                    Message = message
                }
            }, message);
        }
    }
}
