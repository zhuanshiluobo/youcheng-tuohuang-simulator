using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class DeployInfluenceCommandHandler : IGameCommandHandler
    {
        private readonly InfluenceService influenceService;
        private readonly RoundAdvanceService roundAdvanceService;

        public DeployInfluenceCommandHandler(InfluenceService influenceService)
            : this(influenceService, new RoundAdvanceService())
        {
        }

        public DeployInfluenceCommandHandler(InfluenceService influenceService, RoundAdvanceService roundAdvanceService)
        {
            this.influenceService = influenceService;
            this.roundAdvanceService = roundAdvanceService;
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null && command.Kind == GameCommandKind.DeployInfluence;
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            var guard = MainActionCommandGuard.Validate(state, command);
            if (!guard.IsValid)
            {
                return CommandResult.Invalid(guard);
            }

            var placement = influenceService.Place(state, command.PlayerId, command.TargetId);
            if (!placement.Succeeded)
            {
                return CommandResult.Invalid(placement.Validation);
            }

            roundAdvanceService.MarkMainActionComplete(state, command.PlayerId);

            var message = "Player " + command.PlayerId + " deployed influence to " + command.TargetId + ".";
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.InfluencePlaced,
                    PlayerId = command.PlayerId,
                    SubjectId = command.TargetId,
                    Message = message
                }
            }, message);
        }
    }
}
