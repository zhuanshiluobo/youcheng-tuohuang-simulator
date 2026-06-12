using System;
using System.Collections.Generic;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Influence
{
    public sealed class InfluenceQueryService
    {
        private readonly IMapQueryService mapQuery;

        public InfluenceQueryService(IMapQueryService mapQuery)
        {
            _ = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.mapQuery = mapQuery;
        }

        public InfluenceQueryService()
            : this(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()))
        {
        }

        public bool HasInfluenceAt(GameState state, string slotId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return FindInfluenceCore(state, slotId) != null;
        }

        public InfluencePlacement FindInfluence(GameState state, string slotId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return FindInfluenceCore(state, slotId);
        }

        public int CountInRegion(GameState state, int playerId, string regionId, bool includeCity)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var region = FindRegion(regionId);
            if (region == null)
            {
                throw new ArgumentException("Unknown map region id.", nameof(regionId));
            }

            return CountInRegionCore(state, playerId, region, includeCity);
        }

        public List<InfluencePlacement> ListByPlayer(GameState state, int playerId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var results = new List<InfluencePlacement>();
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                if (state.Map.Influences[i].PlayerId == playerId)
                {
                    results.Add(state.Map.Influences[i]);
                }
            }

            return results;
        }

        public Dictionary<int, int> CountAllPlayersInRegion(GameState state, string regionId, bool includeCity)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var region = FindRegion(regionId);
            if (region == null)
            {
                throw new ArgumentException("Unknown map region id.", nameof(regionId));
            }

            var counts = new Dictionary<int, int>();
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var placement = state.Map.Influences[i];
                if (!IsPlacementInRegion(placement, region))
                {
                    continue;
                }

                if (!counts.ContainsKey(placement.PlayerId))
                {
                    counts[placement.PlayerId] = 0;
                }

                counts[placement.PlayerId] += 1;
            }

            if (includeCity)
            {
                for (var i = 0; i < state.Players.Count; i++)
                {
                    var player = state.Players[i];
                    if (string.IsNullOrEmpty(player.CityLocationId))
                    {
                        continue;
                    }

                    if (IsLocationInRegion(player.CityLocationId, region))
                    {
                        if (!counts.ContainsKey(player.PlayerId))
                        {
                            counts[player.PlayerId] = 0;
                        }

                        counts[player.PlayerId] += 2;
                    }
                }
            }

            return counts;
        }

        private InfluencePlacement FindInfluenceCore(GameState state, string slotId)
        {
            if (string.IsNullOrEmpty(slotId))
            {
                return null;
            }

            InfluenceSlotReference requestedSlot;
            string ignoredReason;
            var requestedSlotValid = InfluenceSlotReference.TryParse(
                mapQuery, slotId, out requestedSlot, out ignoredReason);

            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var placement = state.Map.Influences[i];
                if (placement.SlotId == slotId)
                {
                    return placement;
                }

                if (!requestedSlotValid)
                {
                    continue;
                }

                InfluenceSlotReference placedSlot;
                if (InfluenceSlotReference.TryParse(mapQuery, placement.SlotId, out placedSlot, out ignoredReason) &&
                    placedSlot.SlotId == requestedSlot.SlotId)
                {
                    return placement;
                }
            }

            return null;
        }

        private int CountInRegionCore(GameState state, int playerId, MapRegionDefinition region, bool includeCity)
        {
            var count = 0;
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var placement = state.Map.Influences[i];
                if (placement.PlayerId != playerId)
                {
                    continue;
                }

                if (IsPlacementInRegion(placement, region))
                {
                    count += 1;
                }
            }

            if (includeCity)
            {
                var player = state.FindPlayer(playerId);
                if (player != null && IsLocationInRegion(player.CityLocationId, region))
                {
                    count += 2;
                }
            }

            return count;
        }

        private MapRegionDefinition FindRegion(string regionId)
        {
            for (var i = 0; i < mapQuery.Map.Regions.Count; i++)
            {
                if (mapQuery.Map.Regions[i].RegionId == regionId)
                {
                    return mapQuery.Map.Regions[i];
                }
            }

            return null;
        }

        private bool IsPlacementInRegion(InfluencePlacement placement, MapRegionDefinition region)
        {
            if (!string.IsNullOrEmpty(placement.LocationId))
            {
                return IsLocationInRegion(placement.LocationId, region);
            }

            if (!string.IsNullOrEmpty(placement.RouteId))
            {
                return IsRouteInRegion(placement.RouteId, region);
            }

            InfluenceSlotReference slot;
            string reason;
            if (!InfluenceSlotReference.TryParse(mapQuery, placement.SlotId, out slot, out reason))
            {
                return false;
            }

            return slot.Kind == InfluenceSlotKind.Location
                ? IsLocationInRegion(slot.LocationId, region)
                : IsRouteInRegion(slot.RouteId, region);
        }

        private bool IsLocationInRegion(string locationId, MapRegionDefinition region)
        {
            if (string.IsNullOrEmpty(locationId))
            {
                return false;
            }

            try
            {
                var location = mapQuery.GetLocation(locationId);
                if (location.RegionId == region.RegionId)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }

            return region.LocationIds.Contains(locationId);
        }

        private bool IsRouteInRegion(string routeId, MapRegionDefinition region)
        {
            MapRouteDefinition route;
            try
            {
                route = mapQuery.GetRoute(routeId);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(route.RegionId))
            {
                return route.RegionId == region.RegionId;
            }

            if (region.RouteIds.Contains(routeId))
            {
                return true;
            }

            return IsLocationInRegion(route.FromLocationId, region)
                && IsLocationInRegion(route.ToLocationId, region);
        }
    }
}
