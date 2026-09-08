using System;
using System.Collections.Generic;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Maps
{
    [Serializable]
    public sealed class GameMapDefinition
    {
        public string MapId = string.Empty;
        public int MinPlayers;
        public int MaxPlayers;
        public List<MapLocationDefinition> Locations = new List<MapLocationDefinition>();
        public List<MapRouteDefinition> Routes = new List<MapRouteDefinition>();
        public List<MapRegionDefinition> Regions = new List<MapRegionDefinition>();
    }

    [Serializable]
    public sealed class MapLocationDefinition
    {
        public string LocationId = string.Empty;
        public string RegionId = string.Empty;
        public ResourceType ResourceType;
        public bool CanDockCity;
        public bool IsRedZone;
        public EventColor EventColor = EventColor.Yellow;
        public ResourceSet InitialEntranceReward = new ResourceSet();
        public int ResourceSlotCount;
        public int InfluenceSlotCount;
        public int EventSlotCount;
    }

    [Serializable]
    public sealed class MapRouteDefinition
    {
        public string RouteId = string.Empty;
        public string FromLocationId = string.Empty;
        public string ToLocationId = string.Empty;
        public string RegionId = string.Empty;
        public List<string> CoveredLocationIds = new List<string>();
        public int InfluenceSlotCount;
        public int BaseCost;
    }

    [Serializable]
    public sealed class MapRegionDefinition
    {
        public string RegionId = string.Empty;
        public string DisplayName = string.Empty;
        public int ScoreValue;
        public List<string> LocationIds = new List<string>();
        public List<string> RouteIds = new List<string>();
    }
}
