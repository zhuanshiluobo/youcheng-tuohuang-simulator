using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Rules
{
    /// <summary>只读取前置步骤已完成的正式效果结果，推导本局一次性的分发策略。</summary>
    public static class PreparationOutputResolver
    {
        public static bool TryResolve(GameState state, PreparationFlowPlan plan, string ownerId,
            out PreparationEnterprisePlan effective, out string diagnostic, IReadOnlyList<int> preparationOrder = null)
        {
            effective = null; diagnostic = "preparation_output_invalid";
            if (state?.EffectRuntime?.EffectNodes == null || plan?.Enterprise == null || string.IsNullOrEmpty(ownerId)) return false;
            try
            {
                plan.Validate();
                var order = preparationOrder ?? state.EffectRuntime.PlayerOrderSnapshot;
                if (order == null || order.Count < 2 || order.Count > 4 || state.Players == null ||
                    order.Any(id => state.FindPlayer(id) == null) || order.Distinct().Count() != order.Count) return false;
                var nodes = state.EffectRuntime.EffectNodes;
                var owner = nodes.SingleOrDefault(node => node.EffectId == ownerId && node.EffectTypeId == PreparationFrameworkFlow.TypeId);
                var before = nodes.SingleOrDefault(node => node.ParentEffectId == ownerId && node.EffectTypeId == PreparationWindowFlow.WindowTypeId &&
                    Text(node.NormalizedArguments, "window") == PreparationWindowFlow.Before);
                if (owner == null || before == null || !owner.ChildEffectIds.Contains(before.EffectId) || before.Status != EffectNodeStatus.Completed ||
                    !PreparationWindowFlow.ValidateWindow(state, before, plan, null, out _, order)) return false;
                var values = new Dictionary<string, List<NormalizedValue>>(StringComparer.Ordinal);
                foreach (var binding in plan.DistributionInputs)
                {
                    int stepIndex = plan.BeforeDistribution.FindIndex(step => step.StepId == binding.StepId);
                    var step = plan.BeforeDistribution[stepIndex];
                    var tasks = before.ChildEffectIds.Select(id => nodes.Single(node => node.EffectId == id))
                        .Where(node => Number(node.NormalizedArguments, "stepIndex", -1) == stepIndex).ToList();
                    var actors = step.Execution == "once" ? order.Take(1) : order;
                    if (!tasks.Select(task => task.PlayerId).SequenceEqual(actors) || tasks.Any(task => task.Status != EffectNodeStatus.Completed)) return false;
                    var results = new List<NormalizedValue>();
                    foreach (var task in tasks)
                    {
                        var matches = new List<EffectNodeRuntimeState>();
                        for (int index = 0; index < task.ChildEffectIds.Count; index++)
                        {
                            string expected = StableIdFactory.Create("effect", task.EffectId, "child", "child_effect",
                                index.ToString(CultureInfo.InvariantCulture), binding.EffectTypeId, binding.EffectStableKey);
                            var child = nodes.SingleOrDefault(node => node.EffectId == task.ChildEffectIds[index]);
                            if (child?.EffectId == expected && child.ParentEffectId == task.EffectId && child.EffectTypeId == binding.EffectTypeId &&
                                child.PlayerId == task.PlayerId && child.Status == EffectNodeStatus.Completed) matches.Add(child);
                        }
                        if (matches.Count != 1) return false;
                        var result = matches[0].NormalizedResult;
                        var output = result?.Kind == NormalizedValueKind.Object ? Value(result, binding.ResultField) :
                            binding.ResultField == "value" ? result : null;
                        if (output == null || output.Kind == NormalizedValueKind.Null || !output.IsValid()) return false;
                        results.Add(output);
                    }
                    values.Add(binding.Target, results);
                }
                var resultPlan = GameStateCloneService.DeepClone(plan.Enterprise);
                // 前置结果属于本局：只用实际人数的规则校验动态池，原始模式配置仍保留全部人数。
                var currentRule = resultPlan.PlayerCounts.SingleOrDefault(rule => rule.PlayerCount == order.Count);
                if (currentRule == null) { diagnostic = "preparation_distribution_player_count_missing"; return false; }
                resultPlan.PlayerCounts = new List<PreparationEnterpriseCountRule> { currentRule };
                List<string> ReadDefinitions(string target)
                {
                    var ids = new List<string>();
                    foreach (var output in values[target])
                    {
                        var items = output.Kind == NormalizedValueKind.Array ? output.Items : new List<NormalizedValue> { output };
                        foreach (var item in items)
                        {
                            if (item?.Kind != NormalizedValueKind.String || !PreparationContract.IsIdentity(item.StringValue))
                                throw new InvalidOperationException("preparation_output_definition_invalid:" + target);
                            ids.Add(item.StringValue);
                        }
                    }
                    if (ids.Count > 256 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
                        throw new InvalidOperationException("preparation_output_definition_duplicate:" + target);
                    return ids;
                }
                int ReadCount(string target)
                {
                    var result = values[target].Single();
                    if (result.Kind != NormalizedValueKind.Integer || result.IntegerValue < 0 || result.IntegerValue > 256)
                        throw new InvalidOperationException("preparation_output_count_invalid:" + target);
                    return (int)result.IntegerValue;
                }
                if (values.ContainsKey("candidateDefinitionIds"))
                {
                    var candidates = ReadDefinitions("candidateDefinitionIds");
                    if (resultPlan.Pool.Kind == "all") resultPlan.Pool.Kind = "fixed";
                    resultPlan.Pool.CandidateDefinitionIds = candidates;
                    if (resultPlan.Pool.Kind == "fixed") resultPlan.Pool.Count = candidates.Count;
                }
                if (values.ContainsKey("requiredDefinitionIds"))
                {
                    if (resultPlan.Pool.Kind != "requiredRandom") return false;
                    resultPlan.Pool.RequiredDefinitionIds = ReadDefinitions("requiredDefinitionIds");
                }
                if (values.ContainsKey("candidateCount"))
                {
                    if (resultPlan.Pool.Kind != "requiredRandom") return false;
                    resultPlan.Pool.Count = ReadCount("candidateCount");
                }
                if (values.ContainsKey("extraRandomCount"))
                {
                    currentRule.ExtraRandom = ReadCount("extraRandomCount");
                    currentRule.FinalCount = currentRule.PlayerCount * currentRule.Keep + currentRule.ExtraRandom + resultPlan.Fixed.Count(item => !item.RequireConfirmation);
                }
                resultPlan.Validate();
                effective = resultPlan; diagnostic = string.Empty; return true;
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is NullReferenceException || error is IndexOutOfRangeException)
            { diagnostic = "preparation_output_invalid:" + error.Message; return false; }
        }
    }
}
