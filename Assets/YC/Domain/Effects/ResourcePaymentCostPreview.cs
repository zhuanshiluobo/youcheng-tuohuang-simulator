using YC.Domain.Rules;
using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static class ResourcePaymentCostPreview
    {
        public const string DemandQueryKind = "resource_payment_demand.v1";
        public static bool TryDeclaredDemand(GameState state, EffectRegistry registry, AbilitySourceRef source, string actionId, out ResourcePaymentDemandState demand, out string reason)
        {
            demand = null; reason = "payment_demand_query_missing";
            if (registry == null || source == null) return false;
            ResourcePaymentSourceService.Register(registry);
            var facts = ItemTransferEffectExecutor.Object("actionId", NormalizedValue.CreateString(actionId), "playerId", NormalizedValue.CreateInteger(source.AuthorizedPlayerId));
            var query = new AbilityQueryContext { QueryKind = DemandQueryKind, QueryId = StableIdFactory.Create("payment-demand-query", source.SourceKey, actionId, state.EffectRuntime.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                FactsDigest = StableIdFactory.Create("payment-demand-facts", source.SourceKey, facts.ToDeterministicString(), state.EffectRuntime.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                SubjectPlayerId = source.AuthorizedPlayerId, ContentVersion = registry.ContentPackHash, StateRevision = state.EffectRuntime.StateRevision, MapId = state.MapId, Facts = facts, Purpose = "action_body_payment" };
            var rows = registry.QueryRules(state, query).Where(r => r.SourceRef.ItemId == source.ItemId && r.SourceRef.DefinitionId == source.DefinitionId && r.SourceRef.AuthorizedPlayerId == source.AuthorizedPlayerId).SelectMany(r => r.Items.Items).ToList();
            if (rows.Count != 1) return false;
            reason = ResourcePaymentDemandParser.TryParse(rows[0], out demand, out var player);
            if (reason.Length == 0 && player != source.AuthorizedPlayerId) reason = "payment_permission_denied";
            return reason.Length == 0;
        }
        public static string PreviewActionCosts(GameState state, EffectRegistry registry, AbilitySourceRef source, AbilityActionDeclaration action, IList<EffectSpec> costs)
        {
            var style = YC.Domain.SpecialActions.SpecialActionDatabase.Get(state, action.ActionId);
            if (style?.BodyOwnsPayment == true)
            {
                if (costs?.Count > 0) return "payment_body_and_entry_cost_conflict";
                if (!TryDeclaredDemand(state, registry, source, action.ActionId, out var declared, out var reason)) return reason;
                var owner = new EffectNodeRuntimeState { EffectId = StableIdFactory.Create("payment-body-preview", source.SourceKey, action.ActionId), PlayerId = source.AuthorizedPlayerId, EffectTypeId = ResourceEffectTypeIds.Pay };
                return TryFirstAllocation(state, registry, owner, declared, out _, out _) ? "" : "insufficient_resource";
            }
            if (costs == null || costs.Count == 0) return "";
            if (costs.Count > 1) return "action_cost_joint_preview_unavailable";
            var spec = costs[0];
            if (spec.EffectTypeId != ResourceEffectTypeIds.Pay) return AbilityActionExecutor.PreviewCost(state, registry, source, costs);
            var payment = new EffectNodeRuntimeState { EffectId = StableIdFactory.Create("payment-entry-preview", source.SourceKey, action.ActionId), PlayerId = source.AuthorizedPlayerId, EffectTypeId = ResourceEffectTypeIds.Pay, NormalizedArguments = spec.NormalizedArguments.Clone() };
            if (ResourcePaymentDemandParser.IsTotal(spec.NormalizedArguments))
            {
                string error = ResourcePaymentDemandParser.TryParse(spec.NormalizedArguments, out var d, out var player);
                if (error.Length > 0 || player != source.AuthorizedPlayerId) return error.Length > 0 ? error : "payment_permission_denied";
                return TryFirstAllocation(state, registry, payment, d, out _, out _) ? "" : "insufficient_resource";
            }
            ResourceSet fixedCost;
            var fixedArgs = ItemTransferEffectExecutor.Value(spec.NormalizedArguments, "cost");
            if (fixedArgs != null)
            {
                if (!PaymentResources.TryRead(fixedArgs, out fixedCost)) return "entry_cost_invalid";
            }
            else
            {
                if (!Enum.TryParse(ItemTransferEffectExecutor.Text(spec.NormalizedArguments, "resourceType"), false, out ResourceType type) ||
                    !Enum.IsDefined(typeof(ResourceType), type) || ItemTransferEffectExecutor.Number(spec.NormalizedArguments, "amount", -1) < 0)
                    return "entry_cost_invalid";
                fixedCost = new ResourceSet(); fixedCost.Set(type, ItemTransferEffectExecutor.Number(spec.NormalizedArguments, "amount"));
            }
            return CanPay(state, registry, payment, fixedCost) ? "" : "insufficient_resource";
        }
        // 只对私有副本规划；不会在真实状态开窗口、发事件或消费来源。
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
