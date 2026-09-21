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
    public sealed class DeployInfluenceCommandHandler : IGameCommandHandler
    {
        private readonly InfluenceService influenceService;
        private readonly RoundAdvanceService roundAdvanceService;
        private readonly EffectRegistry effectRegistry;

        public DeployInfluenceCommandHandler(InfluenceService influenceService)
            : this(influenceService, new RoundAdvanceService(), new EffectRegistry())
        {
        }

        public DeployInfluenceCommandHandler(InfluenceService influenceService, RoundAdvanceService roundAdvanceService)
            : this(influenceService, roundAdvanceService, new EffectRegistry())
        {
        }

        public DeployInfluenceCommandHandler(
            InfluenceService influenceService,
            RoundAdvanceService roundAdvanceService,
            EffectRegistry effectRegistry)
        {
            this.influenceService = influenceService;
            this.roundAdvanceService = roundAdvanceService;
            this.effectRegistry = effectRegistry ?? throw new System.ArgumentNullException(nameof(effectRegistry));
            InfluenceEffectExecutor.Register(this.effectRegistry, influenceService);
            MainActionEffectExecutor.Register(this.effectRegistry);
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

            var placementValidation = string.IsNullOrEmpty(command.TargetId)
                ? (influenceService.GetLegalPlacementSlotIds(state, command.PlayerId).Count > 0
                    ? ValidationResult.Success
                    : ValidationResult.Failure(CommandErrorCode.InvalidTarget, "当前没有合法的影响力放置位置。"))
                : influenceService.CanPlace(state, command.PlayerId, command.TargetId);
            if (!placementValidation.IsValid)
            {
                return CommandResult.Invalid(placementValidation);
            }

            var effect = MainActionEffectExecutor.Create(command.PlayerId,
                InfluenceEffectSpecFactory.PlaceInfluence(command.PlayerId, command.TargetId, "deploy_influence"));
            var executor = new EffectTreeExecutor(state, effectRegistry);
            string effectId;
            if (!executor.TryCreatePlayerActionEffect(
                    command.PlayerId,
                    effect,
                    string.Empty,
                    "command.deploy_influence",
                    out effectId))
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.WrongPhase, executor.LastDiagnostic));
            }
            var report = executor.RunUntilQuiescent();
            EffectNodeRuntimeState node = executor.GetNode(effectId);
            if (!report.Faulted && report.WaitingForInput && node != null && node.Status == EffectNodeStatus.Blocked)
                return CommandResult.SuccessResult(new List<GameEvent>(), "请选择放置位置；确认前可以取消。 ");
            if (node == null || node.Status != EffectNodeStatus.Completed)
            {
                return CommandResult.Invalid(InfluenceEffectFailureMapper.ToValidation(node));
            }

            var message = "玩家 " + command.PlayerId + " 已将影响力放置到 " + command.TargetId + "。";
            return CommandResult.SuccessResult(new List<GameEvent>(), message);
        }
    }
}
