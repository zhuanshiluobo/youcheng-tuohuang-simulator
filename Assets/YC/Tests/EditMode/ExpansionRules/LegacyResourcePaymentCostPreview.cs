using YC.Domain.Rules;
using YC.Domain.Effects;
using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    internal static class LegacyResourcePaymentCostPreview
    {
        public static bool CanPay(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner, ResourceSet gross)
        {
            if (state?.FindPlayer(owner.PlayerId) == null || !PaymentResources.Valid(gross)) return false;
            if (state.FindPlayer(owner.PlayerId).Resources.CanPay(gross)) return true;
            var rules = owner.Payment?.AllowsSubstitution == false ? Array.Empty<PaymentSourceRule>() : ResourcePaymentSourceService.Query(state, registry, owner, gross);
            var chosen = new List<PaymentSourceDraft>(); var used = new HashSet<string>(StringComparer.Ordinal);
            var allocated = new ResourceSet();
            bool Visit(int index)
            {
                if (index == rules.Count)
                {
                    var copy = GameStateCloneService.DeepClone(state);
                    var node = copy.EffectRuntime.EffectNodes.Find(n => n.EffectId == owner.EffectId);
                    if (node == null) { node = GameStateCloneService.DeepClone(owner); copy.EffectRuntime.EffectNodes.Add(node); }
                    var p = node.Payment ?? new ResourcePaymentRuntimeState { PaymentId = node.EffectId, OwnerEffectId = node.EffectId, PlayerId = node.PlayerId };
                    p.AllowsSubstitution = owner.Payment?.AllowsSubstitution ?? (ItemTransferEffectExecutor.Value(owner.NormalizedArguments, "allowSubstitution") == null || ItemTransferEffectExecutor.Flag(owner.NormalizedArguments, "allowSubstitution"));
                    p.PaymentProtocolVersion = 2; p.Demand = new ResourcePaymentDemandState { FixedCost = gross.Clone() };
                    p.SelectedAllocation = new ResourceSet(); p.GrossCost = gross.Clone(); p.PaymentCost = gross.Clone(); p.BeforePaymentReady = true;
                    p.SourceDrafts = GameStateCloneService.DeepClone(chosen); p.SourceEdits = new List<PaymentSourceEdit>(); node.Payment = p;
                    foreach (var d in p.SourceDrafts) { d.PaymentGeneration = p.PaymentGeneration; d.PaymentId = p.PaymentId; }
                    return ResourcePaymentPlanService.TryBuild(copy, registry, node, out _, out _);
                }
                if (Visit(index + 1)) return true;
                var rule = rules[index];
                int quota = rule.QuotaLimit - chosen.Where(d => d.Rule.QuotaGroupId == rule.QuotaGroupId && d.Rule.QuotaScope == rule.QuotaScope).Sum(d => PaymentResources.Total(d.Allocation));
                int max = Math.Min(rule.MaxUnits, quota);
                for (int units = 1; units <= max; units++)
                {
                    var demand = new ResourcePaymentDemandState { Mode = "total", AllowedResourceTypes = rule.AllowedResourceTypes.ToList(), TotalCount = units };
                    foreach (var a in ResourcePaymentAllocationService.EnumerateAllocations(demand))
                    {
                        if (PaymentResources.Types.Any(t => (long)a.Get(t) + allocated.Get(t) > gross.Get(t))) continue;
                        foreach (var ids in ItemSelections(rule, units))
                        {
                            if (ids.Any(used.Contains)) continue;
                            chosen.Add(new PaymentSourceDraft { Rule = rule, Allocation = a, SelectedItemIds = ids, BuildIdentity = ResourcePaymentSourceService.BuildIdentity(state, owner) });
                            foreach (var t in PaymentResources.Types) allocated.Set(t, allocated.Get(t) + a.Get(t)); foreach (string id in ids) used.Add(id);
                            bool success = Visit(index + 1);
                            foreach (var t in PaymentResources.Types) allocated.Set(t, allocated.Get(t) - a.Get(t)); foreach (string id in ids) used.Remove(id); chosen.RemoveAt(chosen.Count - 1);
                            if (success) return true;
                        }
                    }
                }
                return false;
            }
            IEnumerable<List<string>> ItemSelections(PaymentSourceRule rule, int units)
            {
                if (rule.ItemCost == null) { yield return new List<string>(); yield break; }
                long count = (long)units * rule.ItemCost.CountPerUnit; if (count > ItemTransferEffectExecutor.MaximumTokenCount) yield break;
                var groups = ResourcePaymentSourceService.TransferableItems(state, registry, owner, rule).Where(i => !used.Contains(i.ItemId))
                    .GroupBy(i => ResourcePaymentSourceService.ItemSelectionGroup(state, i)).Select(g => g.Select(i => i.ItemId).ToList()).ToList();
                foreach (var subset in GroupSelections(groups, 0, (int)count, new List<string>())) yield return subset;
            }
            return Visit(0);
        }
        private static IEnumerable<List<string>> GroupSelections(IReadOnlyList<List<string>> groups, int index, int left, List<string> current)
        {
            if (left == 0) { yield return current.ToList(); yield break; }
            if (index >= groups.Count || groups.Skip(index).Sum(g => g.Count) < left) yield break;
            // 报价只枚举不同事实组的数量，不把99枚等价ID展开成组合爆炸，也不创建选择请求。
            for (int take = Math.Min(left, groups[index].Count); take >= 0; take--)
            {
                current.AddRange(groups[index].Take(take));
                foreach (var subset in GroupSelections(groups, index + 1, left - take, current)) yield return subset;
                current.RemoveRange(current.Count - take, take);
            }
        }
        public static bool TryFirstAllocation(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner, ResourcePaymentDemandState demand, out ResourceSet allocation, out ResourceSet gross)
        {
            allocation = null; gross = null;
            foreach (var a in ResourcePaymentAllocationService.EnumerateAllocations(demand))
                if (ResourcePaymentAllocationService.TryValidate(demand, a, out var c, out _) && CanPay(state, registry, owner, c)) { allocation = a; gross = c; return true; }
            return false;
        }
    }
}