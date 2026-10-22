using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Interactions
{
    public sealed class InvokeSettlementOptionCommandHandler : IGameCommandHandler, ISettlementProjectionProvider
    {
        public IReadOnlyList<YC.Domain.Cards.CharacterSettlementOption> GetSettlementOptions(GameState state, GameStateViewer viewer)
            => viewer.Role == GameStateViewerRole.Player && !string.IsNullOrEmpty(state?.EffectRuntime?.ActiveSettlementWindowId)
                ? registry.GetSettlementOptions(state, viewer.PlayerId) : null;
        private readonly EffectRegistry registry;
        private readonly RoundExecutionService roundExecutionService;
        public InvokeSettlementOptionCommandHandler(EffectRegistry registry, RoundExecutionService roundExecutionService = null)
        { this.registry = registry ?? throw new ArgumentNullException(nameof(registry)); this.roundExecutionService = roundExecutionService; }
        public bool CanHandle(GameCommand command) => command?.Kind == GameCommandKind.InvokeSettlementOption;
        public CommandResult Handle(GameState state, GameCommand command)
        {
            if (state == null || command == null || !CanHandle(command)) return Invalid("结算请求无效。");
            var phase = CommandPhasePolicy.ValidatePhase(state, command.Kind); if (!phase.IsValid) return CommandResult.Invalid(phase);
            if (!int.TryParse(Parameter(command, "expectedRevision"), NumberStyles.None, CultureInfo.InvariantCulture, out int revision)) return Invalid("结算请求缺少有效版本。");
            var executor = roundExecutionService == null ? new EffectTreeExecutor(state, registry) : roundExecutionService.CreateCommandExecutor(state);
            if (!executor.TryInvokeCharacterSettlementOption(command.PlayerId, Parameter(command, "optionId"), Parameter(command, "useId"), revision, out bool alreadyInvoked, out string reason)) return Invalid(reason);
            if (!alreadyInvoked)
            {
                var report = executor.RunUntilQuiescent(); if (report.Faulted) return Invalid("结算效果无法继续：" + report.FaultCode);
                if (roundExecutionService != null)
                {
                    var advance = roundExecutionService.Advance(state);
                    if (!advance.IsValid) return CommandResult.Invalid(advance);
                }
                else new RoundExecutionProjector().Project(state);
            }
            return CommandResult.SuccessResult(new List<GameEvent>(), alreadyInvoked ? "该结算项已受理。" : "结算项已受理。");
        }
        public static string Parameter(GameCommand command, string key) => command.Parameters.TryGetValue(key, out string value) ? value ?? "" : "";
        private static CommandResult Invalid(string reason) => CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, reason));
    }
}
