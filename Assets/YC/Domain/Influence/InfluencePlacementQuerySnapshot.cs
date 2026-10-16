using System;
using System.Collections.Generic;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Influence
{
    /// <summary>仅供一次同步候选查询使用的地图事实；不跨调用保存，也不以revision判断有效性。</summary>
    internal sealed class InfluencePlacementQuerySnapshot
    {
        private readonly HashSet<string> occupiedSlots = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> resourceLocations = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> opponentCityLocations = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> openLocations = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> coveredRoutes = new HashSet<string>(StringComparer.Ordinal);

        public bool PlayerExists { get; }
        public int InfluenceSupply { get; }

        internal InfluencePlacementQuerySnapshot(GameState state, int playerId,
            IMapQueryService mapQuery, IInfluenceRoadCoverageQuery roadQuery)
        {
            var player = state.FindPlayer(playerId);
            PlayerExists = player != null;
            if (!PlayerExists) return;
            InfluenceSupply = player.InfluenceSupply;
            // LegacyPositionList每次访问都会重新物化；每个集合在这里枚举一次。
            foreach (var placement in state.Map.Influences) occupiedSlots.Add(placement.SlotId);
            foreach (var token in state.Map.ResourceTokens) resourceLocations.Add(token.LocationId);
            foreach (var other in state.Players)
                if (other.PlayerId != playerId) opponentCityLocations.Add(other.CityLocationId);
            foreach (var location in state.Map.OpenLocationIds) openLocations.Add(location);

            if (roadQuery is RuntimeStateRoadCoverageQuery)
            {
                // 该sealed实现的唯一来源就是RoadRouteIds；一次读取避免每条路线重新投影。
                foreach (var route in state.Map.RoadRouteIds) coveredRoutes.Add(route);
            }
            else if (mapQuery.Map.Routes != null)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                foreach (var route in mapQuery.Map.Routes)
                    if (route != null && !string.IsNullOrEmpty(route.RouteId) && visited.Add(route.RouteId) &&
                        roadQuery.IsRouteCoveredByRoad(state, route.RouteId)) coveredRoutes.Add(route.RouteId);
            }
        }

        internal bool IsOccupied(string slotId) => occupiedSlots.Contains(slotId);
        internal bool HasResourceToken(string locationId) => resourceLocations.Contains(locationId);
        internal bool HasOpponentCity(string locationId) => opponentCityLocations.Contains(locationId);
        internal bool IsLocationOpen(string locationId) => openLocations.Contains(locationId);
        internal bool IsRouteCovered(string routeId) => coveredRoutes.Contains(routeId);
    }
}
