using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public static class CleanupSourceInvocationExecutor
    {
        public const string RegistrationTypeId = "effect.cleanup.source.register", TypeId = "effect.cleanup.source.invoke";
        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(RegistrationTypeId, out _)) registry.Register(new EffectRegistration(RegistrationTypeId,
                c => throw new KernelException(EffectFaultCodes.InvalidRuntimeState, "cleanup_source_anchor_must_not_execute"),
                EffectExecutorKind.Atomic, CleanupSourceService.Version));
            if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, CleanupSourceService.Version));
        }

        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            var node = context.Node; var state = context.State;
            var invocation = CleanupSourceService.Invocations(state).SingleOrDefault(i => i.TargetEffectId == node.EffectId);
            if (invocation == null || invocation.InvocationId != Text(node.NormalizedArguments, "invocationId"))
                throw new KernelException(EffectFaultCodes.InvalidRuntimeState, "cleanup_invocation_missing");
            // 正文节点在受理工作副本中已真实挂入；这里只开始原树，绝不再调用Lua。
            if (node.FlowStage.Length == 0) return EffectStepResult.Continue("body");
            if (node.FlowStage != "body") throw new KernelException(EffectFaultCodes.InvalidRuntimeState, "cleanup_invocation_stage_invalid");
            var nodes = state.EffectRuntime.EffectNodes.Where(n => n.EffectId != node.EffectId &&
                InfluenceDepartureProtocol.IsWithin(state, n.EffectId, node.EffectId)).ToList();
            if (nodes.Any(n => n.Status == EffectNodeStatus.Faulted))
                throw new KernelException(EffectFaultCodes.InvalidRuntimeState, "cleanup_invocation_child_faulted");
            // 正常未终止子节点由原child blocker拦住；被调度到这里而仍未结清是损坏，不以NoProgress伪装等待。
            if (nodes.Any(n => !CleanupSourceService.Terminal(n)))
                throw new KernelException(EffectFaultCodes.InvalidRuntimeState, "cleanup_invocation_child_blocker_missing");
            if (context.TryAttachDeferredResponseDependenciesOnWorking(CleanupSourceDependencies.Instance.GetPendingResponses(state, node.EffectId)))
                return EffectStepResult.Continue("body");
            if (CleanupSourceDependencies.Instance.HasPendingForInvocation(state, node.EffectId, out string dependency))
                throw new KernelException(EffectFaultCodes.InvalidRuntimeState, dependency);
            invocation.PaymentEffectIds = nodes.Where(n => IsPaid(state, n)).Select(n => n.EffectId).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var expected = new HashSet<string>(nodes.Where(n => n.EffectTypeId == EffectTypeIds.Condition &&
                Text(n.NormalizedResult, "outcome") == "condition_not_met").Select(n => Text(n.NormalizedResult, "failedChildId")), StringComparer.Ordinal);
            bool declined = nodes.Any(n => n.FailureReason == "player_declined");
            bool unmet = nodes.Any(n => Text(n.NormalizedResult, "outcome") == "condition_not_met");
            // 左式未满足是规则终态；已付右式的未解释失败必须保留同次故障，不能回入口补发。
            if (invocation.PaymentEffectIds.Count > 0 && nodes.Any(n => n.Status == EffectNodeStatus.Failed &&
                !expected.Contains(n.EffectId) && n.FailureReason != "player_declined" && n.EffectTypeId != EffectTypeIds.Condition))
                throw new KernelException(EffectFaultCodes.InvalidRuntimeState, "cleanup_paid_body_failed");
            string outcome = declined ? "declined" : unmet || nodes.Any(n => n.Status == EffectNodeStatus.Failed) ? "condition_failed" : "completed";
            return EffectStepResult.Completed(Object("invocationId", NormalizedValue.CreateString(invocation.InvocationId),
                "outcome", NormalizedValue.CreateString(outcome)));
        }

        internal static bool IsPaid(GameState state, EffectNodeRuntimeState node) => AbilityActionExecutor.IsCommittedCost(state, node);
    }
}
