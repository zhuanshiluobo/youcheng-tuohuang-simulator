using System;
using System.Linq;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public static class RepeatEffectExecutor
    {
        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(LuaDomainEffectTypeIds.Repeat, out _)) registry.Register(new EffectRegistration(LuaDomainEffectTypeIds.Repeat, Execute, EffectExecutorKind.IntrinsicFlow));
        }
        public static string ValidateArguments(NormalizedValue args) => Value(args, "apiVersion")?.Kind == NormalizedValueKind.Integer && Number(args, "apiVersion") == 1 &&
            Value(args, "count")?.Kind == NormalizedValueKind.Integer && Value(args, "count").IntegerValue >= 0 && Value(args, "count").IntegerValue <= int.MaxValue &&
            (Value(args, "allowEarlyStop") == null || Value(args, "allowEarlyStop").Kind == NormalizedValueKind.Boolean) &&
            args.Properties.All(entry => new[] { "apiVersion", "count", "allowEarlyStop" }.Contains(entry.Name)) ? "" : "invalid_repeat_arguments";
        public static EffectStepResult Execute(EffectExecutionContext context)
        {
            var node = context.Node;
            if (Value(node.NormalizedArguments, "apiVersion")?.IntegerValue == 1 && Value(node.NormalizedArguments, "count") == null) return SkipIncomplete(context, new[] { "count" });
            string error = ValidateArguments(node.NormalizedArguments);
            if (error.Length > 0) return EffectStepResult.Failed(error);
            int requested = Number(node.NormalizedArguments, "count");
            int bodyCount = node.NestedGroupSizes.Count == 1 ? node.NestedGroupSizes[0] : -1;
            if (bodyCount < 0) return SkipIncomplete(context, new[] { "effects" });
            if (node.NestedEffects.Count != bodyCount) throw new InvalidOperationException("repeat_state_invalid");
            if (requested > context.Limits.MaxSteps || (long)requested * Math.Max(1, bodyCount) > context.Limits.MaxNodeCount - context.State.EffectRuntime.EffectNodes.Count + node.ChildEffectIds.Count) return EffectStepResult.Failed("repeat_budget_exceeded");
            if (node.FlowStage.Length == 0)
            {
                long minimumSteps = (long)requested * (Math.Max(1, bodyCount) + 1 + (Flag(node.NormalizedArguments, "allowEarlyStop") ? 1 : 0)) + 1;
                if (minimumSteps > context.Limits.MaxSteps - context.State.EffectRuntime.StepCount) return EffectStepResult.Failed("repeat_budget_exceeded");
                Set(node, "requestedCount", NormalizedValue.CreateInteger(requested)); Set(node, "nextIndex", NormalizedValue.CreateInteger(0)); Set(node, "completedCount", NormalizedValue.CreateInteger(0));
            }
            int index = Number(node.ContinuationData, "nextIndex");
            if (Number(node.ContinuationData, "requestedCount", -1) != requested || index < 0 || index > requested || Number(node.ContinuationData, "completedCount", -1) != index || node.ChildEffectIds.Count != (index + (node.FlowStage == "awaiting_iteration" ? 1 : 0)) * bodyCount) throw new InvalidOperationException("repeat_state_invalid");
            if (node.FlowStage == "awaiting_iteration")
            {
                var current = context.ChildNodes.Skip(index * bodyCount).Take(bodyCount).ToList();
                if (current.Count != bodyCount || current.Any(child => child.Status != EffectNodeStatus.Completed && child.Status != EffectNodeStatus.Failed)) return EffectStepResult.NoProgress("等待本轮子式完成。");
                index++; Set(node, "nextIndex", NormalizedValue.CreateInteger(index)); Set(node, "completedCount", NormalizedValue.CreateInteger(index));
            }
            else if (node.FlowStage == "pending_iteration" && Answer(context) == "stop") return Finish(node);
            else if (node.FlowStage.Length > 0 && node.FlowStage != "pending_iteration" && node.FlowStage != "finished") throw new InvalidOperationException("repeat_stage_invalid");
            if (index == requested || node.FlowStage == "finished") return Finish(node);
            if (Flag(node.NormalizedArguments, "allowEarlyStop") && node.FlowStage != "pending_iteration")
                return EffectStepResult.Continue("pending_iteration").AddInteraction(PrepareInteraction(context, new EffectInteractionSpec { InteractionTypeId = "lua.choice", AnsweringPlayerId = node.PlayerId, Visibility = "owner", PromptKey = "effect.repeat.continue", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "continue", "stop" } }));
            var step = EffectStepResult.Continue("awaiting_iteration");
            foreach (var runtime in node.NestedEffects) { var child = EffectRegistry.FromRuntimeSpec(runtime); child.StableKey = node.EffectId + ":iteration:" + index + ":" + step.ChildEffects.Count; step.AddChild(child); }
            return step;
        }
        private static EffectStepResult Finish(EffectNodeRuntimeState node) => EffectStepResult.Completed(Object("requestedCount", Value(node.ContinuationData, "requestedCount").Clone(), "completedCount", Value(node.ContinuationData, "completedCount").Clone())).WithFlowStage("finished");
    }
}
