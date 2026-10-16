using System;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Enterprises
{
    /// <summary>按选择顺序升级不同企业，沿用单企业的到达奖励与提交合同。</summary>
    public static class EnterpriseDistinctUpgradeFlow
    {
        public const string TypeId = "effect.enterprise.upgrade_distinct";
        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, "v1"));
        }

        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            var invalid = EnterpriseEffectExecutor.CheckArguments(context); if (invalid != null) return invalid;
            var node = context.Node;
            if (node.FlowStage.Length == 0) Set(node, "selectedEnterpriseItemIds", Strings(Array.Empty<string>()));
            var selected = Value(node.ContinuationData, "selectedEnterpriseItemIds").Items.Select(v => v.StringValue).ToList();
            if (context.ChildNodes.Any(n => n.Status == EffectNodeStatus.Failed)) return EffectStepResult.Failed("enterprise_upgrade_failed");
            int count = Number(node.NormalizedArguments, "count");
            if (selected.Count == count) return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(true))).WithFlowStage("finished");
            var candidates = context.State.ItemZones.Items.Where(i => !selected.Contains(i.ItemId) &&
                context.State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.enterprise_display"))
                .Select(i => i.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (candidates.Count < count - selected.Count) return EffectStepResult.Failed("insufficient_distinct_enterprises");
            if (node.FlowStage == "choose_enterprise")
            {
                string id = Answer(context);
                if (candidates.Contains(id))
                {
                    selected.Add(id); Set(node, "selectedEnterpriseItemIds", Strings(selected));
                    return EffectStepResult.Continue("upgrading").AddChild(EnterpriseEffectExecutor.UpgradeSpec(node.PlayerId, id, Number(node.NormalizedArguments, "levels")));
                }
            }
            var request = new EffectInteractionSpec { InteractionTypeId = "enterprise.upgrade", AnsweringPlayerId = node.PlayerId,
                Visibility = "owner", PromptKey = "enterprise.choose_distinct", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1 };
            request.CandidateIds.AddRange(candidates);
            return EffectStepResult.Continue("choose_enterprise").AddInteraction(PrepareInteraction(context, request));
        }

        public static bool TryValidate(GameState state, out string reason)
        {
            reason = "enterprise_distinct_restore_invalid";
            foreach (var node in state.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == TypeId))
            {
                if (EnterpriseEffectExecutor.ValidateArguments(TypeId, node.NormalizedArguments).Length > 0 ||
                    EnterpriseEffectExecutor.MissingFields(TypeId, node.NormalizedArguments).Count > 0 ||
                    Text(node.NormalizedArguments, "player") != node.PlayerId.ToString(CultureInfo.InvariantCulture) ||
                    !new[] { "", "choose_enterprise", "upgrading", "finished" }.Contains(node.FlowStage)) return false;
                var selected = Value(node.ContinuationData, "selectedEnterpriseItemIds");
                if (node.FlowStage.Length == 0 && selected == null) continue;
                if (selected?.Kind != NormalizedValueKind.Array || selected.Items.Any(v => v.Kind != NormalizedValueKind.String) ||
                    selected.Items.Select(v => v.StringValue).Distinct().Count() != selected.Items.Count || selected.Items.Count > Number(node.NormalizedArguments, "count") ||
                    node.ChildEffectIds.Count != selected.Items.Count) return false;
                for (int index = 0; index < selected.Items.Count; index++)
                {
                    var child = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == node.ChildEffectIds[index]);
                    if (child?.EffectTypeId != LuaDomainEffectTypeIds.UpgradeEnterprise || child.PlayerId != node.PlayerId ||
                        Text(child.NormalizedArguments, "enterpriseItemId") != selected.Items[index].StringValue ||
                        Number(child.NormalizedArguments, "levels") != Number(node.NormalizedArguments, "levels")) return false;
                }
            }
            reason = ""; return true;
        }
    }
}
