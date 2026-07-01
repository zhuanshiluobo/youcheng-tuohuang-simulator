using System.Collections.Generic;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Scoring
{
    public sealed class RegionControlService
    {
        private readonly IMapQueryService mapQuery;
        private readonly InfluenceQueryService influenceQuery;

        public RegionControlService(IMapQueryService mapQuery)
        {
            this.mapQuery = mapQuery ?? throw new System.ArgumentNullException(nameof(mapQuery));
            influenceQuery = new InfluenceQueryService(mapQuery);
        }

        public IReadOnlyList<RegionControlResult> Evaluate(GameState state)
        {
            var results = new List<RegionControlResult>();

            foreach (var region in mapQuery.Map.Regions)
            {
                var influenceCounts = influenceQuery.CountAllPlayersInRegion(state, region.RegionId, true);

                int? controllerId = null;
                var maxCount = 0;
                var tie = false;

                foreach (var pair in influenceCounts)
                {
                    if (pair.Value > maxCount)
                    {
                        maxCount = pair.Value;
                        controllerId = pair.Key;
                        tie = false;
                    }
                    else if (pair.Value == maxCount)
                    {
                        tie = true;
                    }
                }

                results.Add(new RegionControlResult
                {
                    RegionId = region.RegionId,
                    ScoreValue = region.ScoreValue,
                    ControllerPlayerId = tie ? null : controllerId,
                    InfluenceCounts = influenceCounts
                });
            }

            return results;
        }
    }

    public sealed class RegionControlResult
    {
        public string RegionId = string.Empty;
        public int ScoreValue;
        public int? ControllerPlayerId;
        public Dictionary<int, int> InfluenceCounts = new Dictionary<int, int>();
    }
}
