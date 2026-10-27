using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Scoring
{
    public sealed class RegionControlService
    {
        private readonly InfluenceQueryService influenceQuery;
        private readonly IMapQueryService mapQuery;
        private readonly ScoringModifierQueryService modifiers;
        public RegionControlService(IMapQueryService mapQuery)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            influenceQuery = new InfluenceQueryService(mapQuery);
        }
        public RegionControlService(IMapQueryService mapQuery, ScoringModifierQueryService modifiers) : this(mapQuery)
        { this.modifiers = modifiers ?? throw new ArgumentNullException(nameof(modifiers)); }

        public IReadOnlyList<RegionControlResult> Evaluate(GameState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (modifiers != null) return modifiers.BuildControlSnapshot(state, "preview").Regions.Select(r => r.ToResult()).ToArray();
            // 无服务时仅兼容没有计分来源的旧基础路径；正式内容不可静默漏掉合约。
            if (HasDeclaredModifiers(state)) throw new InvalidOperationException("scoring_query_service_required");
            var results = new List<RegionControlResult>();
            foreach (var region in mapQuery.Map.Regions)
            {
                var counts = influenceQuery.CountAllPlayersInRegion(state, region.RegionId, true);
                var physical = influenceQuery.CountAllPlayersInRegion(state, region.RegionId, false);
                foreach (var player in state.Players)
                { if (!counts.ContainsKey(player.PlayerId)) counts[player.PlayerId] = 0; if (!physical.ContainsKey(player.PlayerId)) physical[player.PlayerId] = 0; }
                var result = new RegionControlResult { RegionId = region.RegionId, BaseScoreValue = region.ScoreValue, ScoreValue = region.ScoreValue, InfluenceCounts = counts, PhysicalCounts = physical };
                foreach (var pair in counts) result.CityCounts[pair.Key] = pair.Value - physical[pair.Key];
                int highest = counts.Count == 0 ? 0 : counts.Values.Max();
                if (highest > 0) result.PositiveHighestPlayerIds.AddRange(counts.Where(p => p.Value == highest).Select(p => p.Key).OrderBy(id => id));
                result.IsTie = result.PositiveHighestPlayerIds.Count > 1;
                if (result.PositiveHighestPlayerIds.Count == 1) { result.ControllerPlayerId = result.PositiveHighestPlayerIds[0]; result.ControlReason = "unique_positive_highest"; }
                results.Add(result);
            }
            return results;
        }
        public static bool HasDeclaredModifiers(GameState state) => state?.ContentDefinitions?.AbilitySources.Any(d => d.QueryKinds.Contains(ScoringModifierProtocol.QueryKind)) == true;
    }

    [Serializable] public sealed class RegionControlResult
    {
        public string RegionId = string.Empty;
        public int BaseScoreValue, ScoreValue;
        public int? ControllerPlayerId;
        public string ControlReason = "uncontrolled";
        // 兼容旧查询名称，数值始终是完整有效合计。
        public Dictionary<int, int> InfluenceCounts = new Dictionary<int, int>();
        public Dictionary<int, int> PhysicalCounts = new Dictionary<int, int>();
        public Dictionary<int, int> CityCounts = new Dictionary<int, int>();
        public List<int> PositiveHighestPlayerIds = new List<int>();
        public bool IsTie;
        public List<RegionControlClaim> ControlClaims = new List<RegionControlClaim>();
        public List<ScoringModifierRecord> InfluenceModifiers = new List<ScoringModifierRecord>();
        public List<ScoringModifierRecord> TieModifiers = new List<ScoringModifierRecord>();
        public List<ScoringModifierRecord> ValueModifiers = new List<ScoringModifierRecord>();
    }
}
