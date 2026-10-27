using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    // 入口Condition只确认既有费用。正文仍由原BodyHandlerRef在激活后构造，费用handler不获得正文权限。
    public static class AbilityActionEntryCondition
    {
        public const string Version = "entry_condition_gate.v1.0.0";
        public const int InvocationProtocolVersion = 2;
        public static string Unwrap(IList<EffectSpec> costs, out IList<EffectSpec> payment)
        {
            payment = costs;
            if (costs == null || !costs.Any(s => s?.EffectTypeId == EffectTypeIds.Condition)) return "";
            if (costs.Count != 1) return "entry_condition_requires_single_root";
            var gate = costs[0];
            if (!Flag(gate.NormalizedArguments, "ruleCondition") || gate.NestedEffectGroups?.Count != 2 || gate.Children?.Count != 0 ||
                gate.NestedEffectGroups[0]?.Count != 1 || gate.NestedEffectGroups[1]?.Count != 0 || gate.RequiredOutcome != null)
                return "entry_condition_requires_one_cost_and_separate_body";
            if (!string.IsNullOrEmpty(gate.CompletionHandlerId) || gate.ContinuationContext != null)
                return "entry_condition_continuation_denied";
            var left = gate.NestedEffectGroups[0][0];
            if (!AllowedCost(left?.EffectTypeId) || left.Children?.Count != 0 || left.NestedEffectGroups?.Count != 0 ||
                !string.IsNullOrEmpty(left.CompletionHandlerId) || left.ContinuationContext != null || left.RequiredOutcome != null)
                return "entry_condition_cost_program_unsupported";
            if (gate.PlayerId > 0 && left.PlayerId > 0 && gate.PlayerId != left.PlayerId || gate.DecisionPlayerId > 0 && gate.DecisionPlayerId != gate.PlayerId)
                return "entry_condition_player_invalid";
            payment = gate.NestedEffectGroups[0]; return "";
        }
        private static bool AllowedCost(string type) => type == ResourceEffectTypeIds.Pay || type == ResourcePaymentChoiceEffectExecutor.TypeId || type == ItemTransferEffectExecutor.TypeId;
        internal static bool IsGate(IList<EffectSpec> costs) => costs?.Count == 1 && costs[0]?.EffectTypeId == EffectTypeIds.Condition;
        internal static string ProgramDigest(EffectSpec gate)
        {
            var stored = gate.ToPersistentDefinition();
            return ProgramDigest(stored.NormalizedArguments, stored.PlayerId, stored.DeclinePromptKey, stored.NestedEffects, stored.NestedGroupSizes);
        }
        private static string ProgramDigest(NormalizedValue args, int player, string prompt, List<EffectSpecRuntimeState> nested, List<int> sizes)
        {
            // ruleCondition的有效AllowDecline/DecisionPlayer由内核从左式派生，不把派生前后的值误判成程序变化。
            string program = JsonConvert.SerializeObject(new { arguments = args.ToDeterministicString(), player, prompt, nested, sizes });
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(program)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }
        internal static List<string> PaymentRoots(GameState state, AbilityActionInvocation invocation)
        {
            if (invocation.PaymentEffectIds?.Count > 0) return invocation.PaymentEffectIds;
            return invocation.EntryConditionVersion == Version && invocation.Phase == "entry_payment"
                ? state.EffectRuntime.EffectNodes.Single(n => n.EffectId == invocation.OwnerEffectId).ChildEffectIds.ToList() : new List<string>();
        }
        internal static string ValidateReceipt(GameState state, EffectRegistry registry, AbilityActionInvocation invocation)
        {
            if (string.IsNullOrEmpty(invocation.EntryConditionVersion))
            {
                if (invocation.ProtocolVersion != 1) return "entry_condition_receipt_without_protocol";
                var paymentIds = invocation.PaymentEffectIds ?? new List<string>();
                if (invocation.Phase == "entry_payment") paymentIds = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == invocation.OwnerEffectId).ChildEffectIds;
                if (paymentIds.Any(id => state.EffectRuntime.EffectNodes.Any(n => n.EffectId == id && n.EffectTypeId == EffectTypeIds.Condition)))
                    return "entry_condition_protocol_missing";
                // 不以handler哈希猜付款子树：同一handler可合法兼任费用与正文，普通正文Condition仍走旧合同。
                return string.IsNullOrEmpty(invocation.EntryConditionHandlerHash) && string.IsNullOrEmpty(invocation.EntryConditionHandlerVersion) && string.IsNullOrEmpty(invocation.EntryConditionProgramDigest) ? "" : "entry_condition_receipt_without_protocol";
            }
            if (invocation.ProtocolVersion != InvocationProtocolVersion || invocation.EntryConditionVersion != Version || string.IsNullOrEmpty(invocation.EntryConditionHandlerHash) || string.IsNullOrEmpty(invocation.EntryConditionHandlerVersion) || string.IsNullOrEmpty(invocation.EntryConditionProgramDigest))
                return "entry_condition_protocol_invalid";
            var roots = PaymentRoots(state, invocation);
            var gate = roots.Count == 1 ? state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == roots[0]) : null;
            if (gate == null || gate.ParentEffectId != invocation.OwnerEffectId || gate.PlayerId != invocation.SourceRef.AuthorizedPlayerId || gate.EffectTypeId != EffectTypeIds.Condition ||
                !Flag(gate.NormalizedArguments, "ruleCondition") || gate.NestedGroupSizes?.Count != 2 || gate.NestedGroupSizes[0] != 1 || gate.NestedGroupSizes[1] != 0 || gate.NestedEffects?.Count != 1 ||
                !string.IsNullOrEmpty(gate.ContinuationHandlerId) || gate.ContinuationContext != null || gate.RequiredOutcome != null)
                return "entry_condition_program_invalid";
            var left = gate.NestedEffects[0];
            if (!AllowedCost(left.EffectTypeId) || left.NestedEffects?.Count != 0 || !string.IsNullOrEmpty(left.CompletionHandlerId) || left.ContinuationContext != null || left.RequiredOutcome != null)
                return "entry_condition_cost_program_invalid";
            if (!string.IsNullOrEmpty(gate.FlowStage) && (gate.AllowDecline != (left.EffectTypeId != ResourceEffectTypeIds.Pay) || gate.DecisionPlayerId != gate.PlayerId))
                return "entry_condition_decision_invalid";
            if (invocation.EntryConditionProgramDigest != ProgramDigest(gate.NormalizedArguments, gate.PlayerId, gate.DeclinePromptKey, gate.NestedEffects, gate.NestedGroupSizes))
                return "entry_condition_program_changed";
            if (invocation.ActivationCommitted && (gate.Status != EffectNodeStatus.Completed || Text(gate.NormalizedResult, "outcome") != "condition_met"))
                return "entry_condition_activation_without_success";
            if (registry != null)
            {
                var action = registry.ActionDeclaration(invocation.ActionId);
                if (string.IsNullOrEmpty(action.EntryCostRef) || invocation.EntryConditionHandlerHash != registry.HandlerHash(action.EntryCostRef) ||
                    invocation.EntryConditionHandlerVersion != registry.GetAbilityHandlerVersion(action.EntryCostRef)) return "entry_condition_handler_changed";
            }
            return "";
        }
    }
}
