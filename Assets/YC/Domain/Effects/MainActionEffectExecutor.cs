using YC.Domain.Rules;

namespace YC.Domain.Effects
{
    /// <summary>主要行动只组织可复用 Effect，并在全部结算成功后消费一次行动预算。</summary>
    public static class MainActionEffectExecutor
    {
        public const string TypeId = "action.main.execute";
        public const string Version = "1";

        public static void Register(EffectRegistry registry)
        {
            EffectRegistration existing;
            if (!registry.TryGet(TypeId, out existing))
                registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, Version));
        }

        public static bool IsPlanning(EffectExecutionContext context)
        {
            var parent = context.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == context.Node.ParentEffectId);
            return parent != null && parent.EffectTypeId == TypeId && context.ChildNodes.Count == 0;
        }

        public static bool IsCancellation(EffectExecutionContext context)
        {
            var answer = context.GetLatestInteractionAnswer();
            return IsPlanning(context) && answer != null && answer.Kind == YC.Domain.State.NormalizedValueKind.Boolean && !answer.BooleanValue;
        }

        public static EffectSpec Create(int playerId, EffectSpec operation)
        {
            var spec = new EffectSpec(TypeId) { DefinitionVersion = Version, PlayerId = playerId };
            spec.Children.Add(operation);
            return spec;
        }

        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            if (context.Node.PendingOutcome != YC.Domain.State.EffectPendingOutcome.None)
                return EffectStepResult.Completed(context.Node.NormalizedResult);
            if (context.Node.FlowStage == string.Empty)
            {
                var step = EffectStepResult.Continue("settling");
                foreach (var child in context.Node.NestedEffects)
                    step.AddChild(EffectRegistry.FromRuntimeSpec(child));
                return step;
            }

            foreach (var child in context.ChildNodes)
            {
                if (child.Status == YC.Domain.State.EffectNodeStatus.Failed || child.Status == YC.Domain.State.EffectNodeStatus.Faulted)
                    return EffectStepResult.Failed(child.FailureReason);
                if (child.Status != YC.Domain.State.EffectNodeStatus.Completed)
                    return EffectStepResult.NoProgress("等待主要行动子效果完成。");
            }

            new MainActionBudgetService().SpendCompletedMainAction(context.State, context.Node.PlayerId);
            return EffectStepResult.Completed();
        }
    }
}
