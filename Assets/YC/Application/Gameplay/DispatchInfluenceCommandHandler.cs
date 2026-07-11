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

            if (command.Parameters == null)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.InvalidTarget,
                    "调度参数不能为空。"));
            }

            var hasSecondSource = command.Parameters.TryGetValue("source2", out var source2);
            var hasSecondTarget = command.Parameters.TryGetValue("target2", out var target2);
            if (hasSecondSource != hasSecondTarget ||
                (hasSecondSource && (string.IsNullOrEmpty(source2) || string.IsNullOrEmpty(target2))))
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.InvalidTarget,
                    "第二次调度必须同时提供 source2 和 target2。"));
            }

            var moves = new List<InfluenceMoveRequest>
            {
                new InfluenceMoveRequest(command.SourceId, command.TargetId)
            };
            if (hasSecondSource)
            {
                moves.Add(new InfluenceMoveRequest(source2, target2));
            }

            var moveResult = influenceService.MoveAtomically(state, command.PlayerId, moves);
            if (!moveResult.Succeeded)
            {
                return CommandResult.Invalid(moveResult.Validation);
            }

            roundAdvanceService.MarkMainActionComplete(state, command.PlayerId);

            var movedCount = moves.Count;
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
