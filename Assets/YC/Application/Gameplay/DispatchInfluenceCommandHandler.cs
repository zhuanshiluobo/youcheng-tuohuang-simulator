using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class DispatchInfluenceCommandHandler : IGameCommandHandler
    {
        private readonly InfluenceService influenceService;
        private readonly RoundAdvanceService roundAdvanceService;
        private readonly EffectRegistry effectRegistry;

        public DispatchInfluenceCommandHandler(InfluenceService influenceService)
            : this(influenceService, new RoundAdvanceService(), new EffectRegistry())
        {
        }

        public DispatchInfluenceCommandHandler(InfluenceService influenceService, RoundAdvanceService roundAdvanceService)
            : this(influenceService, roundAdvanceService, new EffectRegistry())
        {
        }

        public DispatchInfluenceCommandHandler(
            InfluenceService influenceService,
            RoundAdvanceService roundAdvanceService,
            EffectRegistry effectRegistry)
        {
            this.influenceService = influenceService;
            this.roundAdvanceService = roundAdvanceService;
            this.effectRegistry = effectRegistry ?? throw new System.ArgumentNullException(nameof(effectRegistry));
            InfluenceEffectExecutor.Register(this.effectRegistry, influenceService);
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

            var moveValidation = influenceService.CanMoveAtomically(state, command.PlayerId, moves);
            if (!moveValidation.IsValid)
            {
                return CommandResult.Invalid(moveValidation);
            }

            var moveIds = new List<KeyValuePair<string, string>>();
            for (var i = 0; i < moves.Count; i++)
            {
                var source = influenceService.FindInfluence(state, moves[i].SourceSlotId);
                if (source == null)
                {
                    return CommandResult.Invalid(ValidationResult.Failure(
                        CommandErrorCode.InvalidSource,
                        "要移动的影响力不存在。"));
                }

                var stableInfluenceId = InfluenceIdentity.GetStableId(state, source);
                moveIds.Add(new KeyValuePair<string, string>(stableInfluenceId, moves[i].TargetSlotId));
            }

            var effect = InfluenceEffectSpecFactory.MoveInfluences(command.PlayerId, moveIds, InfluenceCauseKinds.MoveCity);
            var executor = new EffectTreeExecutor(state, effectRegistry);
            string effectId;
            if (!executor.TryCreatePlayerActionEffect(
                    command.PlayerId,
                    effect,
                    string.Empty,
                    "command.dispatch_influence",
                    out effectId))
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.WrongPhase, executor.LastDiagnostic));
            }
            executor.RunUntilQuiescent();
            EffectNodeRuntimeState node = executor.GetNode(effectId);
            if (node == null || node.Status != EffectNodeStatus.Completed)
            {
                return CommandResult.Invalid(InfluenceEffectFailureMapper.ToValidation(node));
            }

            roundAdvanceService.MarkMainActionComplete(state, command.PlayerId);

            var movedCount = moves.Count;
            var message = "玩家 " + command.PlayerId + " 已调度影响力 " + movedCount + " 次。";
            return CommandResult.SuccessResult(new List<GameEvent>(), message);
        }
    }
}
