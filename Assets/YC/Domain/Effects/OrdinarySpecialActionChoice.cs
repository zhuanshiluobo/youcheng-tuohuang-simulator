using System;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    // 4.16.4：普通主要行动的Host选择；实际InvokeAction独自承担原费用、次数和额度。
    public static class OrdinarySpecialActionChoice
    {
        public const string TypeId = "flow.special_action.choose", Version = "1.0.0";
        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, Version) {
                Validator = s => ValidateArguments(s.NormalizedArguments) });
        }
        public static string ValidateArguments(NormalizedValue a) => a?.Kind == NormalizedValueKind.Object && a.IsValid() && a.Properties.Count == 3 &&
            Text(a, "commandId").Length > 0 && Text(a, "contentHash").Length > 0 && Value(a, "expectedRevision")?.Kind == NormalizedValueKind.Integer &&
            Value(a, "expectedRevision").IntegerValue >= 0 && Value(a, "expectedRevision").IntegerValue <= int.MaxValue &&
            a.Properties.All(p => new[] { "commandId", "contentHash", "expectedRevision" }.Contains(p.Name)) ? "" : "special_choice_arguments_invalid";

        private static EffectStepResult Execute(EffectExecutionContext c)
        {
            var n = c.Node;
            if (ValidateArguments(n.NormalizedArguments).Length > 0 || Text(n.NormalizedArguments, "contentHash") != c.Registry.ContentPackHash ||
                n.PlayerId < 1 || c.State.FindPlayer(n.PlayerId) == null) return EffectStepResult.Failed("special_choice_context_invalid");
            if (n.FlowStage == "")
            {
                var sources = c.Registry.QuerySpecialActionSources(c.State, n.PlayerId, false).Where(o => o.Enabled).ToList();
                if (sources.Count == 0) return EffectStepResult.Failed("special_choice_no_legal_source");
                var choice = ImmediateSpecialActionFlow.Build(n.PlayerId, sources, option => AbilityActionExecutor.Create(option,
                    StableIdFactory.Create("special-choice-action", n.EffectId, option.OptionId)), true);
                choice.StableKey = "special-sources";
                return EffectStepResult.Continue("choosing").AddChild(choice);
            }
            if (n.FlowStage != "choosing") return EffectStepResult.Failed("special_choice_stage_invalid");
            var children = Own(c.State, n);
            if (children.Count != 1 || children[0].EffectTypeId != LuaDomainEffectTypeIds.Choice) return EffectStepResult.Failed("special_choice_child_invalid");
            var child = children[0];
            if (child.Status != EffectNodeStatus.Completed && child.Status != EffectNodeStatus.Failed) return EffectStepResult.NoProgress("等待所选特殊行动及响应完成。");
            bool invoked = AbilityActionExecutor.Descendants(c.State, n.EffectId).Any(a => a.EffectTypeId == AbilityActionExecutor.TypeId);
            var result = Object("outcome", NormalizedValue.CreateString(child.Status == EffectNodeStatus.Failed ? "failed" : invoked ? "completed" : "cancelled"),
                "choiceEffectId", NormalizedValue.CreateString(child.EffectId));
            return (child.Status == EffectNodeStatus.Failed ? EffectStepResult.Failed(child.FailureReason, result) : EffectStepResult.Completed(result)).WithFlowStage("finished");
        }
        private static System.Collections.Generic.List<EffectNodeRuntimeState> Own(GameState s, EffectNodeRuntimeState n) => n.ChildEffectIds
            .Where(id => !s.EffectRuntime.DispatchReceipts.Any(r => r.Succeeded && r.AttachedEffectIds.Contains(id)))
            .Select(id => s.EffectRuntime.EffectNodes.Single(c => c.EffectId == id)).ToList();

        private static bool FrozenTree(EffectSpecRuntimeState choice, EffectNodeRuntimeState owner)
        {
            var options = Value(choice.NormalizedArguments, "options");
            if (choice.EffectTypeId != LuaDomainEffectTypeIds.Choice || choice.PlayerId != owner.PlayerId || options?.Kind != NormalizedValueKind.Array ||
                options.Items.Count < 1 || options.Items.Count > 16 || choice.NestedGroupSizes.Count != options.Items.Count ||
                choice.NestedGroupSizes.Any(k => k != 1) || choice.NestedEffects.Count != options.Items.Count ||
                Number(choice.NormalizedArguments, "minSelections", -1) != 0 || Number(choice.NormalizedArguments, "maxSelections", -1) != 1 ||
                Text(choice.NormalizedArguments, "interactionType") != "effect.choice.execute") return false;
            for (int i = 0; i < choice.NestedEffects.Count; i++)
            {
                var leaf = choice.NestedEffects[i];
                if (leaf.EffectTypeId == LuaDomainEffectTypeIds.Choice) { if (!FrozenTree(leaf, owner)) return false; continue; }
                if (leaf.EffectTypeId != AbilityActionExecutor.TypeId || leaf.DefinitionVersion != AbilityActionExecutor.Version || leaf.PlayerId != owner.PlayerId ||
                    leaf.NormalizedArguments?.Kind != NormalizedValueKind.Object || leaf.NormalizedArguments.Properties.Count != 6 ||
                    Value(leaf.NormalizedArguments, "arguments")?.Kind != NormalizedValueKind.Object || Value(leaf.NormalizedArguments, "arguments").Properties.Count != 0 ||
                    Text(leaf.NormalizedArguments, "requestId").Length == 0 || Text(leaf.NormalizedArguments, "factsDigest").Length == 0 ||
                    Text(leaf.NormalizedArguments, "optionId") != options.Items[i].StringValue ||
                    Text(leaf.NormalizedArguments, "contentHash") != Text(owner.NormalizedArguments, "contentHash") ||
                    Text(leaf.NormalizedArguments, "commandId") != StableIdFactory.Create("special-choice-action", owner.EffectId, options.Items[i].StringValue)) return false;
            }
            return true;
        }
        public static bool ValidateSaved(GameState state, EffectNodeRuntimeState n)
        {
            if (n.EffectTypeId != TypeId) return true;
            if (n.DefinitionVersion != Version || ValidateArguments(n.NormalizedArguments).Length > 0 || n.PositionChanges.Count != 0 || state.FindPlayer(n.PlayerId) == null || Text(n.NormalizedArguments, "contentHash") != state.ContentPackHash ||
                Number(n.NormalizedArguments, "expectedRevision") > state.EffectRuntime.StateRevision ||
                state.EffectRuntime.InteractionRequests.Any(r => r.OwnerEffectId == n.EffectId)) return false;
            var children = Own(state, n);
            if (n.FlowStage == "") return children.Count == 0;
            if ((n.FlowStage != "choosing" && n.FlowStage != "finished") || children.Count != 1) return false;
            var c = children[0];
            if (!FrozenTree(new EffectSpecRuntimeState { EffectTypeId = c.EffectTypeId, PlayerId = c.PlayerId, NormalizedArguments = c.NormalizedArguments,
                NestedGroupSizes = c.NestedGroupSizes, NestedEffects = c.NestedEffects }, n)) return false;
            if (n.FlowStage != "finished") return true;
            bool invoked = AbilityActionExecutor.Descendants(state, n.EffectId).Any(a => a.EffectTypeId == AbilityActionExecutor.TypeId);
            return (c.Status == EffectNodeStatus.Completed || c.Status == EffectNodeStatus.Failed) &&
                Text(n.NormalizedResult, "choiceEffectId") == c.EffectId && Text(n.NormalizedResult, "outcome") ==
                (c.Status == EffectNodeStatus.Failed ? "failed" : invoked ? "completed" : "cancelled");
        }
    }
}
