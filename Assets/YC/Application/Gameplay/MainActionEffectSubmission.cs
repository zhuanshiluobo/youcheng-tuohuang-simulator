using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Events;
using YC.Domain.State;
using YC.Domain.Rules;

namespace YC.Application.Gameplay
{
    internal static class MainActionEffectSubmission
    {
        public static CommandResult Begin(GameState state, GameCommand command, EffectRegistry registry, EffectSpec root)
        {
            var validation = MainActionCommandGuard.Validate(state, command);
            if (!validation.IsValid) return CommandResult.Invalid(validation);
            MainActionSelectionEffectExecutor.Register(registry);
            var executor = new EffectTreeExecutor(state, registry);
            if (!executor.TryCreatePlayerActionEffect(command.PlayerId, root, string.Empty, command.CommandId ?? string.Empty, out var id))
                return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, executor.LastDiagnostic));
            var report = executor.RunUntilQuiescent();
            var node = executor.GetNode(id);
            if (report.Faulted || node == null || node.Status == EffectNodeStatus.Failed || node.Status == EffectNodeStatus.Faulted)
                return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, node == null ? executor.LastDiagnostic : node.FailureReason));
            return CommandResult.SuccessResult(new List<GameEvent>(), report.WaitingForInput ? "行动已提交，请处理待选择项；最终确认前可撤销整个行动。" : "行动已完成。");
        }
    }
}
