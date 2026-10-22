using System.Collections.Generic;
using System.Linq;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    /// <summary>调度的两次移动各自提交；父节点只结算一次主要行动预算。</summary>
    public static class InfluenceDispatchActionEffectExecutor
    {
        public const string TypeId = "action.influence.dispatch";
        public const string DefinitionVersion = "v1";
        public const string SourceInteractionTypeId = "effect.influence.move.source";
        public const string TargetInteractionTypeId = "effect.influence.move.target";
        public const string SourcePromptKey = "effect.influence.move.choose_source";
        public const string TargetPromptKey = "effect.influence.move.choose_target";

        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(TypeId, out _))
                registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, DefinitionVersion));
        }

        public static EffectSpec Create(int playerId)
        {
            var spec = new EffectSpec(TypeId) { PlayerId = playerId, DefinitionVersion = DefinitionVersion };
            spec.Children.Add(InfluenceEffectSpecFactory.ChooseDispatchInfluence(playerId));
            spec.Children.Add(InfluenceEffectSpecFactory.ChooseDispatchInfluence(playerId));
            return spec;
        }

        public static EffectSpec Create(int playerId, string sourceInfluenceId, string targetSlotId)
        {
            var spec = new EffectSpec(TypeId) { PlayerId = playerId, DefinitionVersion = DefinitionVersion };
            var firstMove = InfluenceEffectSpecFactory.MoveInfluence(playerId, sourceInfluenceId, targetSlotId, InfluenceCauseKinds.MoveCity);
            firstMove.NormalizedArguments.Properties.Add(new NormalizedValueEntry {
                Name = "subjectFilter", Value = NormalizedValue.CreateString("self") });
            firstMove.NormalizedArguments = NormalizedValue.CreateObject(firstMove.NormalizedArguments.Properties);
            spec.Children.Add(firstMove);
            spec.Children.Add(InfluenceEffectSpecFactory.ChooseDispatchInfluence(playerId));
            return spec;
        }

        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            if (context.Node.PendingOutcome != EffectPendingOutcome.None)
                return EffectStepResult.Completed(context.Node.NormalizedResult.Clone());
            if (context.Node.FlowStage == string.Empty)
            {
                if (context.Node.NestedEffects.Count != 2)
                    return EffectStepResult.Failed("invalid_dispatch_children");
                var started = EffectStepResult.Continue("settling");
                foreach (var spec in context.Node.NestedEffects)
                    started.AddChild(EffectRegistry.FromRuntimeSpec(spec));
                return started;
            }

            var moves = context.ChildNodes;
            if (context.Node.FlowStage != "settling" || moves.Count != 2 ||
                moves.Any(move => move.EffectTypeId != InfluenceEffectTypeIds.MoveInfluence))
                return EffectStepResult.Failed("invalid_dispatch_children");
            if (moves.Any(move => move.Status != EffectNodeStatus.Completed && move.Status != EffectNodeStatus.Failed))
                return EffectStepResult.NoProgress("等待调度移动子节点结束。");

            var failed = moves.FirstOrDefault(move => move.Status == EffectNodeStatus.Failed);
            int movedCount = moves.Count(move => ItemTransferEffectExecutor.Value(move.NormalizedResult, "changed")?.BooleanValue == true);
            // 已生效的移动不能成为免费行动；主动跳过则正常收束此主要行动。
            bool spendBudget = failed == null || movedCount > 0;
            if (spendBudget) new MainActionBudgetService().SpendCompletedMainAction(context.State, context.Node.PlayerId);
            var result = ItemTransferEffectExecutor.Object(
                "operationKind", NormalizedValue.CreateString("dispatch"),
                "changed", NormalizedValue.CreateBoolean(movedCount > 0),
                "movedCount", NormalizedValue.CreateInteger(movedCount),
                "budgetSpent", NormalizedValue.CreateBoolean(spendBudget),
                "failedEffectId", failed == null ? NormalizedValue.CreateNull() : NormalizedValue.CreateStableReference("effect", failed.EffectId));
            return failed == null ? EffectStepResult.Completed(result) : EffectStepResult.Failed(failed.FailureReason, result);
        }
    }
}
