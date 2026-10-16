using System.Collections.Generic;
using System.Linq;
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
        private readonly EffectRegistry effectRegistry;
        private readonly InfluenceService influenceService;

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
            this.influenceService = influenceService ?? throw new System.ArgumentNullException(nameof(influenceService));
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

            if (string.IsNullOrEmpty(command.SourceId) || string.IsNullOrEmpty(command.TargetId) ||
                command.OptionIds?.Count > 0 || command.Parameters?.Count > 0)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.InvalidTarget,
                    "dispatch_preselection_arguments_invalid"));
            }
            var source = state.Map.Influences.FirstOrDefault(value =>
                value != null && InfluenceIdentity.GetStableId(state, value) == command.SourceId);
            if (source == null || source.PlayerId != command.PlayerId)
                return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget,
                    "dispatch_preselection_source_invalid"));
            var moveValidation = influenceService.CanMove(state, command.PlayerId, command.SourceId, command.TargetId);
            if (!moveValidation.IsValid) return CommandResult.Invalid(moveValidation);
            var effect = InfluenceDispatchActionEffectExecutor.Create(command.PlayerId, command.SourceId, command.TargetId);
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
            var report = executor.RunUntilQuiescent();
            if (report.Faulted)
                return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget,
                    "调度效果无法继续：" + state.EffectRuntime.LastFaultMessage));
            EffectNodeRuntimeState node = executor.GetNode(effectId);
            if (node == null || node.Status == EffectNodeStatus.Failed || node.PendingOutcome == EffectPendingOutcome.Failed)
            {
                return CommandResult.Invalid(InfluenceEffectFailureMapper.ToValidation(node));
            }

            var message = node.Status == EffectNodeStatus.Completed ? "调度已完成。" : "调度已开始，请选择要移动的影响力。";
            return CommandResult.SuccessResult(new List<GameEvent>(), message);
        }
    }
}
