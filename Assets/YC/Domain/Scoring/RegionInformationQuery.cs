using System.Collections.Generic;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Scoring
{
    public sealed class RegionInformationRow
    {
        public string RegionId;
        public string Label;
        public int Points;
        public Dictionary<int, int> Influence;
        public int? EmptySlots;
        public int TotalSlots;
    }

    /// <summary>影响力复用计分口径（城市计 2）；空位按实际槽位逐个判断，不能用影响力相减。</summary>
    public static class RegionInformationQuery
    {
        public static List<RegionInformationRow> Build(GameState state, IMapQueryService map)
        {
            var rows = new List<RegionInformationRow>();
            if (state == null || map == null) return rows;
            var influence = new InfluenceQueryService(map);
            var occupied = new HashSet<string>();
            var complete = state.Map != null && state.Map.Influences != null;
            if (complete)
                foreach (var placement in state.Map.Influences)
                {
                    if (placement == null || !InfluenceSlotReference.TryParse(map, placement.SlotId,
                        out var slot, out _)) { complete = false; continue; }
                    occupied.Add(slot.SlotId);
                }
            foreach (var region in map.Map.Regions)
            {
                var empty = 0; var total = 0;
                foreach (var location in map.Map.Locations)
                    if (region.LocationIds.Contains(location.LocationId))
                        for (var i = 0; i < location.InfluenceSlotCount; i++)
                        { total++; if (!occupied.Contains(InfluenceSlotReference.ForLocation(location.LocationId, i).SlotId)) empty++; }
                foreach (var route in map.Map.Routes)
                    if (region.RouteIds.Contains(route.RouteId))
                        for (var i = 0; i < route.InfluenceSlotCount; i++)
                        { total++; if (!occupied.Contains(InfluenceSlotReference.ForRoute(route.RouteId, i).SlotId)) empty++; }
                rows.Add(new RegionInformationRow
                {
                    RegionId = region.RegionId, Label = region.DisplayName, Points = region.ScoreValue,
                    Influence = state.Map == null ? new Dictionary<int, int>() : influence.CountAllPlayersInRegion(state, region.RegionId, true),
                    EmptySlots = complete ? (int?)empty : null, TotalSlots = total
                });
            }
            return rows;
        }
    }
}
