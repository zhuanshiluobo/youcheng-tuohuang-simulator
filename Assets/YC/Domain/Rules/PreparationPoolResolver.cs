using System;
using System.Collections.Generic;
using System.Linq;

namespace YC.Domain.Rules
{
    /// <summary>按冻结名单选出实际实体；候选缺失或重复不会降级为别的随机集合。</summary>
    public static class PreparationPoolResolver
    {
        public static List<T> Resolve<T>(PreparationPoolPolicy policy, IEnumerable<T> available,
            Func<T, string> definitionId, Action<IList<T>> shuffle, string subject)
        {
            if (policy == null || available == null || definitionId == null) throw new ArgumentNullException();
            policy.Validate(subject);
            var items = available.ToList();
            var byId = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                string id = definitionId(item);
                if (string.IsNullOrEmpty(id) || byId.ContainsKey(id))
                    throw new InvalidOperationException("preparation_pool_definition_duplicate:" + subject + ":" + id);
                byId.Add(id, item);
            }
            foreach (string id in policy.CandidateDefinitionIds.Concat(policy.RequiredDefinitionIds).Distinct(StringComparer.Ordinal))
                if (!byId.ContainsKey(id)) throw new InvalidOperationException("preparation_pool_definition_missing:" + subject + ":" + id);
            if (policy.Kind == "all") return items.OrderBy(definitionId, StringComparer.Ordinal).ToList();
            if (policy.Kind == "fixed") return policy.CandidateDefinitionIds.Select(id => byId[id]).ToList();
            var candidates = policy.CandidateDefinitionIds.Count == 0 ? byId.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList() :
                policy.CandidateDefinitionIds.OrderBy(id => id, StringComparer.Ordinal).ToList();
            var required = new HashSet<string>(policy.RequiredDefinitionIds, StringComparer.Ordinal);
            if (candidates.Count < policy.Count || required.Any(id => !candidates.Contains(id)))
                throw new InvalidOperationException("preparation_pool_insufficient:" + subject);
            var result = policy.RequiredDefinitionIds.Select(id => byId[id]).ToList();
            int extra = policy.Count - result.Count;
            if (extra > 0)
            {
                if (shuffle == null) throw new InvalidOperationException("preparation_pool_random_missing:" + subject);
                var remainder = candidates.Where(id => !required.Contains(id)).Select(id => byId[id]).ToList();
                shuffle(remainder);
                result.AddRange(remainder.Take(extra));
            }
            return result;
        }
    }
}
