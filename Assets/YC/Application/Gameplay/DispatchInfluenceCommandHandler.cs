using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class DispatchInfluenceCommandHandler : IGameCommandHandler
    {
        private readonly InfluenceService influenceService;
        private readonly RoundAdvanceService roundAdvanceService;

        public DispatchInfluenceCommandHandler(InfluenceService influenceService)
            : this(influenceService, new RoundAdvanceService())
        {
        }

        public DispatchInfluenceCommandHandler(InfluenceService influenceService, RoundAdvanceService roundAdvanceService)
        {
            this.influenceService = influenceService;
            this.roundAdvanceService = roundAdvanceService;
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null && command.Kind == GameCommandKind.DispatchInfluence;
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            var guard = MainActionCommandGuard.Validate(state, command);
            if (!guard.IsValid)
            {
                return CommandResult.Invalid(guard);
            }

            var firstMove = influenceService.Move(state, command.PlayerId, command.SourceId, command.TargetId);
            if (!firstMove.Succeeded)
            {
                return CommandResult.Invalid(firstMove.Validation);
            }

            var movedCount = 1;
            if (command.Parameters.TryGetValue("source2", out var source2) &&
                command.Parameters.TryGetValue("target2", out var target2) &&
                !string.IsNullOrEmpty(source2) &&
                !string.IsNullOrEmpty(target2))
            {
                var secondMove = influenceService.Move(state, command.PlayerId, source2, target2);
                if (!secondMove.Succeeded)
                {
                    influenceService.Move(state, command.PlayerId, command.TargetId, command.SourceId);
                    return CommandResult.Invalid(secondMove.Validation);
                }

                movedCount = 2;
            }

            roundAdvanceService.CompleteMainAction(state, command.PlayerId);

            var message = "Player " + command.PlayerId + " dispatched influence " + movedCount + " time(s).";
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.InfluenceMoved,
                    PlayerId = command.PlayerId,
                    SubjectId = command.TargetId,
                    Message = message
                }
            }, message);
        }
    }
}
