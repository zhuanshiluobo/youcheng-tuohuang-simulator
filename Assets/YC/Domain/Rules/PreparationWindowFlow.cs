using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Rules
{
    /// <summary>由可信宿主登记的步骤；模式只能引用匹配来源、版本与脚本身份的登记项。</summary>
    public sealed class PreparationStepRegistration
    {
        private readonly PreparationStepDeclaration identity;
        private readonly Func<EffectExecutionContext, NormalizedValue, IList<EffectSpec>> build;

        public PreparationStepRegistration(PreparationStepDeclaration declaration,
            Func<EffectExecutionContext, NormalizedValue, IList<EffectSpec>> buildEffects)
        {
            if (declaration == null || buildEffects == null) throw new ArgumentNullException();
            declaration.Validate();
            identity = GameStateCloneService.DeepClone(declaration);
            build = buildEffects;
        }

        public string SourcePackId => identity.SourcePackId;
        public string HandlerId => identity.HandlerId;
        public string HandlerVersion => identity.HandlerVersion;

        internal bool Matches(PreparationStepDeclaration step) => step != null &&
            identity.SourcePackId == step.SourcePackId && identity.SourcePackVersion == step.SourcePackVersion &&
            identity.Kind == step.Kind && identity.HandlerId == step.HandlerId && identity.HandlerVersion == step.HandlerVersion &&
            identity.ScriptPath == step.ScriptPath && identity.ScriptHash == step.ScriptHash &&
            SameCompletion(identity.Completion, step.Completion);

        private static bool SameCompletion(PreparationCompletionDeclaration a, PreparationCompletionDeclaration b) =>
            a == null ? b == null : b != null && a.HandlerId == b.HandlerId && a.HandlerVersion == b.HandlerVersion &&
                a.ScriptPath == b.ScriptPath && a.ScriptHash == b.ScriptHash;

        internal IList<EffectSpec> Build(EffectExecutionContext context, PreparationStepDeclaration step)
        {
            if (!Matches(step)) throw new InvalidOperationException("preparation_step_source_mismatch:" + step.StepId);
            var effects = build(context, step.Arguments.Clone());
            if (effects == null || effects.Any(effect => effect == null))
                throw new InvalidOperationException("preparation_step_effects_invalid:" + step.StepId);
            return effects;
        }
    }

    /// <summary>三段流程共同使用的效果树窗口；只编排已登记步骤，不直接改变配方或分发结果。</summary>
    public static class PreparationWindowFlow
    {
        public const string WindowTypeId = "setup.preparation.window";
        public const string StepTypeId = "setup.preparation.step";
        public const string Before = "beforeDistribution";
        public const string Middle = "betweenDistributionAndConfirmation";
        public const string After = "afterConfirmation";
        public const string Version = "1.0.0";

        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (!registry.TryGet(WindowTypeId, out _))
                registry.Register(new EffectRegistration(WindowTypeId, ExecuteWindow, EffectExecutorKind.IntrinsicFlow, Version));
            if (!registry.TryGet(StepTypeId, out _))
                registry.Register(new EffectRegistration(StepTypeId, ExecuteStep, EffectExecutorKind.IntrinsicFlow, Version));
        }

        public static EffectSpec WindowSpec(string window, int player)
        {
            if (!new[] { Before, Middle, After }.Contains(window)) throw new ArgumentException("未知准备窗口。", nameof(window));
            return EffectSpec.Create(WindowTypeId, Object("window", NormalizedValue.CreateString(window)), player);
        }

        private static IList<PreparationStepDeclaration> Steps(PreparationFlowPlan plan, string window) => window switch
        {
            Before => plan.BeforeDistribution,
            Middle => plan.BetweenDistributionAndConfirmation,
            After => plan.AfterConfirmation,
            _ => null
        };

        private static string GroupKey(int index) => "preparation-step:" + index.ToString(CultureInfo.InvariantCulture);
        private static string StableKey(string window, string step, int player) =>
            "preparation:" + window + ":" + step + ":" + player.ToString(CultureInfo.InvariantCulture);

        private static List<int> Actors(PreparationStepDeclaration step, IReadOnlyList<int> order) =>
            step.Execution == "once" ? new List<int> { order[0] } : order.ToList();

        private static EffectSpec StepSpec(string window, PreparationStepDeclaration step, int index, int player)
        {
            var spec = EffectSpec.Create(StepTypeId, Object("window", NormalizedValue.CreateString(window),
                "stepId", NormalizedValue.CreateString(step.StepId), "stepIndex", NormalizedValue.CreateInteger(index)), player);
            spec.StableKey = StableKey(window, step.StepId, player);
            if (step.Execution != "once") spec.Visibility = GameStateVisibilityPolicy.Owner;
            return spec;
        }

        private static EffectStepResult ExecuteWindow(EffectExecutionContext context)
        {
            var node = context.Node;
            var plan = context.State.SetupRecipe?.Preparation;
            var order = context.State.EffectRuntime.PlayerOrderSnapshot;
            string window = Text(node.NormalizedArguments, "window");
            if (!ValidateInput(context.State, plan, window, node.PlayerId, out var steps))
                return EffectStepResult.Failed("preparation_window_input_invalid");
            if (node.FlowStage == string.Empty)
            {
                if (node.ChildEffectIds.Count != 0 || node.ChildSchedulingGroups.Count != 0)
                    return EffectStepResult.Failed("preparation_window_children_invalid");
                Set(node, "nextStep", NormalizedValue.CreateInteger(0));
            }
            else
            {
                if (!ValidateWindow(context.State, node, plan, context.Registry, out _))
                    return EffectStepResult.Failed("preparation_window_state_invalid");
                if (context.ChildNodes.Any(child => child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted))
                    return EffectStepResult.Failed("preparation_step_failed");
                if (context.ChildNodes.Any(child => child.Status != EffectNodeStatus.Completed))
                    return EffectStepResult.NoProgress("等待准备步骤及其子效果完成。");
                Set(node, "nextStep", NormalizedValue.CreateInteger(Number(node.ContinuationData, "nextStep") + 1));
            }
            int index = Number(node.ContinuationData, "nextStep");
            if (index == steps.Count) return EffectStepResult.Completed().WithFlowStage("finished");
            if (index < 0 || index > steps.Count) return EffectStepResult.Failed("preparation_step_index_invalid");
            var step = steps[index];
            if (!context.Registry.TryGetPreparationStep(step, out _)) return EffectStepResult.Failed("preparation_step_unregistered:" + step.StepId);
            var tasks = Actors(step, order).Select(player => StepSpec(window, step, index, player)).ToList();
            var result = EffectStepResult.Continue("waiting_step");
            return step.Execution == "eachIndependent"
                ? result.AddIndependentChildren(GroupKey(index), tasks)
                : result.AddChildren(tasks);
        }

        private static EffectStepResult ExecuteStep(EffectExecutionContext context)
        {
            var node = context.Node;
            var plan = context.State.SetupRecipe?.Preparation;
            var parent = context.State.EffectRuntime.EffectNodes.Find(item => item.EffectId == node.ParentEffectId);
            if (parent?.EffectTypeId != WindowTypeId || !parent.ChildEffectIds.Contains(node.EffectId) ||
                !ValidateWindow(context.State, parent, plan, context.Registry, out _) || parent.FlowStage != "waiting_step")
                return EffectStepResult.Failed("preparation_step_parent_invalid");
            int index = Number(node.NormalizedArguments, "stepIndex", -1);
            var steps = Steps(plan, Text(node.NormalizedArguments, "window"));
            if (steps == null || index < 0 || index >= steps.Count || index != Number(parent.ContinuationData, "nextStep") ||
                !context.Registry.TryGetPreparationStep(steps[index], out var registration))
                return EffectStepResult.Failed("preparation_step_identity_invalid");
            if (node.FlowStage == string.Empty)
            {
                if (node.ChildEffectIds.Count != 0) return EffectStepResult.Failed("preparation_step_children_invalid");
                return EffectStepResult.Continue("running_effects").AddChildren(registration.Build(context, steps[index]));
            }
            if (node.FlowStage != "running_effects") return EffectStepResult.Failed("preparation_step_stage_invalid");
            if (context.ChildNodes.Any(child => child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted))
                return EffectStepResult.Failed("preparation_step_child_failed");
            if (context.ChildNodes.Any(child => child.Status != EffectNodeStatus.Completed))
                return EffectStepResult.NoProgress("等待准备步骤的子效果和续接。");
            return EffectStepResult.Completed().WithFlowStage("finished");
        }

        private static bool ValidateInput(GameState state, PreparationFlowPlan plan, string window, int player,
            out IList<PreparationStepDeclaration> steps, IReadOnlyList<int> preparationOrder = null)
        {
            steps = null;
            if (plan == null || state?.EffectRuntime?.PlayerOrderSnapshot == null) return false;
            try { plan.Validate(); } catch (InvalidOperationException) { return false; }
            var order = preparationOrder ?? state.EffectRuntime.PlayerOrderSnapshot;
            steps = Steps(plan, window);
            return steps != null && order.Count >= 2 && order.Count <= 4 && order.Distinct().Count() == order.Count &&
                order.All(id => id > 0 && state.FindPlayer(id) != null) && player == order[0];
        }

        public static bool ValidateWindow(GameState state, EffectNodeRuntimeState node, PreparationFlowPlan plan,
            EffectRegistry registry, out string diagnostic, IReadOnlyList<int> preparationOrder = null)
        {
            diagnostic = "preparation_window_invalid";
            if (node?.EffectTypeId != WindowTypeId ||
                !ValidateInput(state, plan, Text(node.NormalizedArguments, "window"), node.PlayerId, out var steps, preparationOrder)) return false;
            if (node.FlowStage == string.Empty)
            {
                bool empty = node.ChildEffectIds.Count == 0 && node.ChildSchedulingGroups.Count == 0;
                if (empty) diagnostic = string.Empty;
                return empty;
            }
            int index = Number(node.ContinuationData, "nextStep", -1);
            bool finished = node.FlowStage == "finished";
            if (index < 0 || index > steps.Count || finished && index != steps.Count ||
                !finished && (node.FlowStage != "waiting_step" || index >= steps.Count)) return false;
            int activeCount = finished ? steps.Count : index + 1;
            int cursor = 0, groupCount = 0;
            string window = Text(node.NormalizedArguments, "window");
            for (int i = 0; i < activeCount; i++)
            {
                var step = steps[i];
                if (registry != null && !registry.TryGetPreparationStep(step, out _)) return false;
                var actors = Actors(step, preparationOrder ?? state.EffectRuntime.PlayerOrderSnapshot);
                var groups = node.ChildSchedulingGroups.Where(group => group.StableGroupKey == GroupKey(i)).ToList();
                if (step.Execution == "eachIndependent")
                {
                    if (groups.Count != 1 || groups[0].StartIndex != cursor || groups[0].ChildEffectIds.Count != actors.Count ||
                        groups[0].OwnerEffectId != node.EffectId || groups[0].Mode != EffectChildSchedulingMode.Independent || groups[0].ProtocolVersion != 1) return false;
                    groupCount++;
                }
                else if (groups.Count != 0) return false;
                for (int p = 0; p < actors.Count; p++, cursor++)
                {
                    if (cursor >= node.ChildEffectIds.Count) return false;
                    string id = node.ChildEffectIds[cursor];
                    var child = state.EffectRuntime.EffectNodes.Find(item => item.EffectId == id);
                    if (child == null || child.ParentEffectId != node.EffectId || child.EffectTypeId != StepTypeId ||
                        child.PlayerId != actors[p] || Text(child.NormalizedArguments, "window") != window ||
                        Text(child.NormalizedArguments, "stepId") != step.StepId || Number(child.NormalizedArguments, "stepIndex", -1) != i ||
                        child.EffectId != StableIdFactory.Create("effect", node.EffectId, "child", "child_effect",
                            cursor.ToString(CultureInfo.InvariantCulture), StepTypeId, StableKey(window, step.StepId, actors[p])) ||
                        step.Execution != "once" && child.Visibility != GameStateVisibilityPolicy.Owner ||
                        step.Execution == "eachIndependent" && groups[0].ChildEffectIds[p] != id ||
                        i < index && child.Status != EffectNodeStatus.Completed ||
                        finished && child.Status != EffectNodeStatus.Completed) return false;
                }
            }
            if (cursor != node.ChildEffectIds.Count || node.ChildSchedulingGroups.Count != groupCount) return false;
            diagnostic = string.Empty;
            return true;
        }
    }
}

namespace YC.Domain.Effects
{
    public sealed partial class EffectRegistry
    {
        private readonly Dictionary<string, YC.Domain.Rules.PreparationStepRegistration> preparationSteps =
            new Dictionary<string, YC.Domain.Rules.PreparationStepRegistration>(StringComparer.Ordinal);
        private static string PreparationKey(string pack, string handler, string version) => pack + "\n" + handler + "\n" + version;

        public void RegisterPreparationStep(YC.Domain.Rules.PreparationStepRegistration registration)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            string key = PreparationKey(registration.SourcePackId, registration.HandlerId, registration.HandlerVersion);
            if (preparationSteps.ContainsKey(key)) throw new InvalidOperationException("preparation_step_registration_duplicate:" + registration.HandlerId);
            preparationSteps.Add(key, registration);
        }

        public bool TryGetPreparationStep(YC.Domain.Rules.PreparationStepDeclaration declaration,
            out YC.Domain.Rules.PreparationStepRegistration registration)
        {
            registration = null;
            return declaration != null && preparationSteps.TryGetValue(PreparationKey(declaration.SourcePackId, declaration.HandlerId, declaration.HandlerVersion), out registration) &&
                registration.Matches(declaration);
        }
    }
}
