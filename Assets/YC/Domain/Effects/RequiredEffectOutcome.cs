using System;
using System.Linq;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    [Serializable] public sealed class RequiredEffectOutcome
    {
        public int ProtocolVersion = 1;
        public string ResultField = "";
        public long ExpectedValue;
        public static bool IsValid(RequiredEffectOutcome requirement) => requirement == null ||
            requirement.ProtocolVersion == 1 && !string.IsNullOrWhiteSpace(requirement.ResultField) &&
            requirement.ExpectedValue >= 0 && requirement.ResultField.IndexOf('.') < 0;
        public static RequiredEffectOutcome FromNormalized(NormalizedValue value)
        {
            if (value == null || value.Kind == NormalizedValueKind.Null) return null;
            if (value.Kind != NormalizedValueKind.Object || value.Properties.Count != 3 ||
                value.Properties.Any(p => !new[] { "protocolVersion", "resultField", "expectedValue" }.Contains(p.Name)) ||
                Value(value, "protocolVersion")?.Kind != NormalizedValueKind.Integer || Number(value, "protocolVersion") != 1 ||
                Value(value, "resultField")?.Kind != NormalizedValueKind.String || Value(value, "expectedValue")?.Kind != NormalizedValueKind.Integer)
                throw new InvalidOperationException("required_outcome_contract_invalid");
            var result = new RequiredEffectOutcome { ResultField = Text(value, "resultField"), ExpectedValue = Value(value, "expectedValue").IntegerValue };
            if (!IsValid(result)) throw new InvalidOperationException("required_outcome_contract_invalid");
            return result;
        }
        // Kernel必须在必要blocker结清、完成事件发布和兄弟释放之前调用。
        // 普通Failed仍是普通终态；只有显式承担的义务使用本合同。
        public static void ValidateBeforeTerminal(EffectNodeRuntimeState node, RequiredEffectOutcome requirement)
        {
            if (requirement == null) return;
            if (!IsValid(requirement)) throw new KernelException("required_outcome_contract_invalid", "必做效果的结果合同无效。");
            var value = Value(node.NormalizedResult, requirement.ResultField);
            if (node.AllowDecline || node.PendingOutcome != EffectPendingOutcome.Completed ||
                value?.Kind != NormalizedValueKind.Integer || value.IntegerValue != requirement.ExpectedValue)
                throw new KernelException("required_outcome_unfulfilled", "已承担的必做效果未履约；保留已提交事实并暂停后续效果。");
        }
        public static void ValidateInteraction(EffectInteractionSpec interaction, RequiredEffectOutcome requirement)
        {
            if (requirement != null && interaction.AllowDecline)
                throw new KernelException("required_outcome_decline_forbidden", "必做效果的正式请求不能允许放弃。");
        }
    }
}

