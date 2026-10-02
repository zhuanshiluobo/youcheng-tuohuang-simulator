using System;
using System.Collections.Generic;
using YC.Domain.Harvest;

namespace YC.Presentation.Workflows
{
    /// <summary>只读派生提示：当前计划已覆盖，且剩余预算不存在能解锁漏选目标的合法扩展。</summary>
    public static class ResourceCollectionCoverage
    {
        public static bool IsComplete(ResourceCollectionSelectionQuery current,
            IEnumerable<string> selected, IEnumerable<string> paid,
            Func<IReadOnlyCollection<string>, ResourceCollectionSelectionQuery> query)
        {
            if (current == null || !current.IsValid || current.PathsByLocationId.Count == 0 ||
                current.ConfirmedTollCost > current.AvailableGoldVoucher) return false;
            var chosen = new HashSet<string>(selected, StringComparer.Ordinal);
            foreach (var location in current.PathsByLocationId.Keys)
                if (!chosen.Contains(location)) return false;
            if (chosen.IsSupersetOf(current.CandidateLocationIds)) return true;
            var plans = new Queue<KeyValuePair<HashSet<string>, ResourceCollectionSelectionQuery>>();
            plans.Enqueue(new KeyValuePair<HashSet<string>, ResourceCollectionSelectionQuery>(
                new HashSet<string>(paid, StringComparer.Ordinal), current));
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (plans.Count > 0)
            {
                var plan = plans.Dequeue();
                foreach (var option in plan.Value.RouteOptionsById.Values)
                {
                    if (!option.CanAfford || plan.Key.Contains(option.RouteId)) continue;
                    var next = new HashSet<string>(plan.Key, StringComparer.Ordinal) { option.RouteId };
                    var ordered = new List<string>(next); ordered.Sort(StringComparer.Ordinal);
                    if (!visited.Add(string.Join("|", ordered))) continue;
                    var result = query(next);
                    if (result == null || !result.IsValid || result.ConfirmedTollCost > result.AvailableGoldVoucher) continue;
                    foreach (var location in result.PathsByLocationId.Keys)
                        if (!chosen.Contains(location)) return false;
                    plans.Enqueue(new KeyValuePair<HashSet<string>, ResourceCollectionSelectionQuery>(next, result));
                }
            }
            return true;
        }
    }
}
