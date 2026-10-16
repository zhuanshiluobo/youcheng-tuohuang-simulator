using System;
using System.Collections.Generic;
using System.Globalization;

namespace YC.Domain.Interactions
{
    // 只校验交互协议，不读写对局状态；结算执行器仍复验并创建资源子节点。
    public static class ResourceAllocationAnswer
    {
        public const string BoundedAnswerSchema = "bounded_resource_allocation";
        public static bool TryParseBounded(string encoded, IReadOnlyList<string> allowedIds, int minimumTotal, int maximumTotal,
            IReadOnlyDictionary<string, int> rowMaximums, out Dictionary<string, int> amounts, out string diagnostic)
        {
            amounts = new Dictionary<string, int>(StringComparer.Ordinal); diagnostic = "资源分配超出本笔总量或逐行上限。";
            if (encoded == null || minimumTotal < 0 || maximumTotal < minimumTotal || maximumTotal > 99 || allowedIds == null || rowMaximums == null) return false;
            var allowed = new HashSet<string>(allowedIds, StringComparer.Ordinal);
            if (allowed.Count != allowedIds.Count || allowed.Contains(null) || allowed.Contains(string.Empty)) return false;
            foreach (string id in allowed) if (!rowMaximums.TryGetValue(id, out int limit) || limit < 0 || limit > 99) return false;
            int total = 0;
            if (encoded.Length > 0)
                foreach (string part in encoded.Split(','))
                {
                    string[] pair = part.Split('=');
                    if (pair.Length != 2 || !allowed.Contains(pair[0]) || amounts.ContainsKey(pair[0]) ||
                        !int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count > rowMaximums[pair[0]]) return false;
                    total += count; if (total > maximumTotal) return false; amounts.Add(pair[0], count);
                }
            if (total < minimumTotal) return false;
            diagnostic = string.Empty; return true;
        }
        public static bool TryParse(string encoded, IList<string> allowedIds, int requiredTotal,
            out Dictionary<string, int> amounts, out string diagnostic)
        {
            amounts = new Dictionary<string, int>(StringComparer.Ordinal); diagnostic = "资源分配必须使用允许的资源、不重复且总数一致。";
            if (string.IsNullOrEmpty(encoded) || requiredTotal < 1 || requiredTotal > 99 || allowedIds == null) return false;
            int total = 0;
            foreach (string part in encoded.Split(','))
            {
                string[] pair = part.Split('=');
                if (pair.Length != 2 || !allowedIds.Contains(pair[0]) || amounts.ContainsKey(pair[0]) ||
                    !int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count > requiredTotal) return false;
                amounts.Add(pair[0], count); total += count;
                if (total > requiredTotal) return false;
            }
            if (total != requiredTotal) return false;
            diagnostic = string.Empty; return true;
        }
    }
}
