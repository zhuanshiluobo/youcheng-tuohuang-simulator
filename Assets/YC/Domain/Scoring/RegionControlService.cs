using System.Collections.Generic;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Scoring
{
    public sealed class RegionControlService
    {
        private readonly IMapQueryService mapQuery;

        public RegionControlService(IMapQueryService mapQuery)
        {
            this.mapQuery = mapQuery ?? throw new System.ArgumentNullException(nameof(mapQuery));
        }

        public IReadOnlyList<RegionControlResult> Evaluate(GameState state)
        {
            var results = new List<RegionControlResult>();

            foreach (var region in mapQuery.Map.Regions)
            {
                var influenceCounts = new Dictionary<int, int>();

                foreach (var locationId in region.LocationIds)
                {
                    foreach (var influence in state.Map.Influences)
                    {
                        if (influence.LocationId != locationId) continue;
                        if (!influenceCounts.ContainsKey(influence.PlayerId))
                            influenceCounts[influence.PlayerId] = 0;
                        influenceCounts[influence.PlayerId]++;
                    }

                    foreach (var player in state.Players)
                    {
                        if (player.CityLocationId == locationId)
                        {
                            if (!influenceCounts.ContainsKey(player.PlayerId))
                                influenceCounts[player.PlayerId] = 0;
                            influenceCounts[player.PlayerId] += 2;
                        }
                    }
                }

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
