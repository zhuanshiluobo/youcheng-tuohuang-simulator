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
    public sealed class PreparePaymentDraftCommandHandler : IGameCommandHandler
    {
        private readonly EffectRegistry registry;
        private readonly RoundExecutionService rounds;
        public PreparePaymentDraftCommandHandler(EffectRegistry registry, RoundExecutionService rounds = null)
        { this.registry = registry ?? throw new ArgumentNullException(nameof(registry)); this.rounds = rounds; }
        public bool CanHandle(GameCommand command) => command?.Kind == GameCommandKind.PreparePaymentDraft;
        public CommandResult Handle(GameState state, GameCommand command)
        {
            string Read(string key) => InvokeSettlementOptionCommandHandler.Parameter(command, key);
            string operation = Read("operation");
            // 效果准备由当前正式请求许可；不拿主要行动阶段拒绝其他阶段的真实授予。
            if (operation != "prepare_effect_build" && operation != "cancel_effect_build")
            {
                var phase = CommandPhasePolicy.ValidatePhase(state, command.Kind);
                if (!phase.IsValid) return CommandResult.Invalid(phase);
            }
            if (!int.TryParse(Read("expectedRevision"), NumberStyles.None, CultureInfo.InvariantCulture, out int revision))
                return Invalid("payment_revision_missing");
            var executor = rounds == null ? new EffectTreeExecutor(state, registry) : rounds.CreateCommandExecutor(state);
            var allowed = operation == "prepare_effect_build" ? new[] { "operation", "selectionOwnerEffectId", "selectionInteractionId", "selectionCandidateSetId", "selectionCandidateSetVersion", "expectedRevision", "paymentMode", "expectedSourceZoneId" } :
                operation == "prepare_action" ? new[] { "operation", "actionKind", "expectedRevision", "paymentMode", "expectedSourceZoneId", "cityBoardSlotIndex" } :
                operation == "cancel_action" || operation == "cancel_effect_build" ? new[] { "operation", "preparationOwnerEffectId", "expectedRevision" } :
                new[] { "operation", "ownerEffectId", "interactionId", "candidateId", "expectedRevision" };
            if (string.IsNullOrEmpty(command.CommandId) || command.Parameters != null && System.Linq.Enumerable.Any(command.Parameters.Keys, key => Array.IndexOf(allowed, key) < 0))
                return Invalid("payment_preparation_arguments_invalid");
            if (operation == "prepare_effect_build")
            {
                if (!int.TryParse(Read("selectionCandidateSetVersion"), NumberStyles.None, CultureInfo.InvariantCulture, out int version))
                    return Invalid("payment_preparation_arguments_invalid");
                var arguments = NormalizedValue.CreateObject(new[] {
                    new NormalizedValueEntry { Name = "paymentMode", Value = NormalizedValue.CreateString(Read("paymentMode")) },
                    new NormalizedValueEntry { Name = "expectedSourceZoneId", Value = NormalizedValue.CreateString(Read("expectedSourceZoneId")) }
                });
                if (!executor.TryPrepareEffectBuildPayment(command.PlayerId, Read("selectionOwnerEffectId"), Read("selectionInteractionId"),
                    Read("selectionCandidateSetId"), version, command.TargetId, arguments, revision, command.CommandId, out var effectReason)) return Invalid(effectReason);
                return CommandResult.SuccessResult(new List<GameEvent>(), "已准备本次效果建设费用。");
            }
            if (operation == "cancel_effect_build")
            {
                if (!executor.TryCancelPreparedEffectBuildPayment(command.PlayerId, Read("preparationOwnerEffectId"), revision,
                    command.CommandId, out var cancelEffectReason)) return Invalid(cancelEffectReason);
                return CommandResult.SuccessResult(new List<GameEvent>(), "已取消本笔未提交的效果建设准备。");
            }
            if (operation == "prepare_action")
            {
                string kind = Read("actionKind");
                var entries = new List<NormalizedValueEntry>();
                if (kind == "build")
                {
                    entries.Add(new NormalizedValueEntry { Name = "paymentMode", Value = NormalizedValue.CreateString(Read("paymentMode")) });
                    entries.Add(new NormalizedValueEntry { Name = "expectedSourceZoneId", Value = NormalizedValue.CreateString(Read("expectedSourceZoneId")) });
                    if (Read("cityBoardSlotIndex").Length > 0)
                    {
                        if (!int.TryParse(Read("cityBoardSlotIndex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot)) return Invalid("invalid_build_slot");
                        entries.Add(new NormalizedValueEntry { Name = "cityBoardSlotIndex", Value = NormalizedValue.CreateInteger(slot) });
                    }
                }
                if (!executor.TryPrepareActionPayment(command.PlayerId, kind, command.TargetId, NormalizedValue.CreateObject(entries), revision,
                    command.CommandId, out _, out _, out _, out var preparationReason)) return Invalid(preparationReason);
                return CommandResult.SuccessResult(new List<GameEvent>(), "已准备本笔费用，等待原行动确认。");
            }
            if (operation == "cancel_action")
            {
                if (!executor.TryCancelPreparedActionPayment(command.PlayerId, Read("preparationOwnerEffectId"), revision, command.CommandId, out var cancelReason)) return Invalid(cancelReason);
                return CommandResult.SuccessResult(new List<GameEvent>(), "已取消未提交的付款准备。");
            }
            if (operation.Length > 0 && operation != "select_cost") return Invalid("payment_operation_invalid");
            if (!executor.TryPreparePaymentDraft(command.PlayerId, Read("ownerEffectId"), Read("interactionId"), Read("candidateId"),
                revision, command.CommandId, out var reason)) return Invalid(reason);
            var report = executor.RunUntilQuiescent();
            if (report.Faulted) return Invalid(report.FaultCode);
            return CommandResult.SuccessResult(new List<GameEvent>(), "已选择本笔付款的费用草稿。");
        }
        private static CommandResult Invalid(string reason) => CommandResult.Invalid(ValidationResult.Failure(SettlementHostProtocol.ErrorCode(reason), reason));
    }
}
