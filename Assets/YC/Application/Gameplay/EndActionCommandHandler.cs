using System.Collections.Generic;
using System.Globalization;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
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
        private readonly EffectRegistry registry;
        private readonly RoundExecutionService roundExecutionService;

        public EndActionCommandHandler()
            : this(new RoundAdvanceService())
        {
        }

        public EndActionCommandHandler(RoundAdvanceService roundAdvanceService)
            : this(roundAdvanceService, null)
        {
        }

        public EndActionCommandHandler(RoundAdvanceService roundAdvanceService, FinalScoringService finalScoringService,
            EffectRegistry registry = null, RoundExecutionService roundExecutionService = null)
        {
            this.roundAdvanceService = roundAdvanceService;
            this.finalScoringService = finalScoringService;
            this.registry = registry; this.roundExecutionService = roundExecutionService;
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

            if (!string.IsNullOrEmpty(state?.EffectRuntime?.ActiveSettlementWindowId) || !string.IsNullOrEmpty(Parameter(command, "windowId")))
            {
                if (registry == null) return Invalid(CommandErrorCode.UnknownCommand, "settlement_registry_missing");
                if (!int.TryParse(Parameter(command, "expectedRevision"), NumberStyles.None, CultureInfo.InvariantCulture, out int revision))
                    return Invalid(CommandErrorCode.InvalidTarget, "settlement_revision_missing");
                var executor = roundExecutionService == null ? new EffectTreeExecutor(state, registry) : roundExecutionService.CreateCommandExecutor(state);
                if (!executor.TryEndSettlementAction(command.PlayerId, Parameter(command, "windowId"), Parameter(command, "currentNodeEffectId"),
                    Parameter(command, "interactionId"), revision, command.CommandId, out var reason)) return Invalid(SettlementHostProtocol.ErrorCode(reason), reason);
                var report = executor.RunUntilQuiescent();
                if (report.Faulted) return Invalid(CommandErrorCode.UnknownCommand, report.FaultCode);
                new RoundExecutionProjector().Project(state);
                return CommandResult.SuccessResult(new List<GameEvent>(), "结算结束请求已处理。");
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

            var message = "玩家" + command.PlayerId + "已结束行动。";
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
        private static string Parameter(GameCommand command, string key) => command.Parameters.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
        private static CommandResult Invalid(CommandErrorCode code, string reason) => CommandResult.Invalid(ValidationResult.Failure(code, reason));
    }
}
