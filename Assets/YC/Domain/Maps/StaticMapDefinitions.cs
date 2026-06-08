using System.Collections.Generic;
using YC.Domain.Rules;

namespace YC.Domain.Maps
{
    public static class StaticMapDefinitions
    {
        // TODO: Replace these placeholder layouts with structured map data from the rulebook.
        public static GameMapDefinition CreateThreePlayerPlaceholder()
        {
            return new GameMapDefinition
            {
                MapId = "placeholder-3p",
                MinPlayers = 3,
                MaxPlayers = 3,
                Locations = new List<MapLocationDefinition>
                {
                    new MapLocationDefinition
                    {
                        LocationId = "city-a",
                        RegionId = "north",
                        ResourceType = ResourceType.PureOriginium,
                        CanDockCity = true,
                        ResourceSlotCount = 1,
                        InfluenceSlotCount = 1
                    },
                    new MapLocationDefinition
                    {
                        LocationId = "mine-b",
                        RegionId = "north",
                        ResourceType = ResourceType.Iron,
                        CanDockCity = false,
                        ResourceSlotCount = 1,
                        InfluenceSlotCount = 1
                    },
                    new MapLocationDefinition
                    {
                        LocationId = "harbor-c",
                        RegionId = "south",
                        ResourceType = ResourceType.OriginiumShard,
                        CanDockCity = true,
                        ResourceSlotCount = 1,
                        InfluenceSlotCount = 1,
                        EventSlotCount = 1
                    }
                },
                Routes = new List<MapRouteDefinition>
                {
                    new MapRouteDefinition
                    {
                        RouteId = "route-a-b",
                        FromLocationId = "city-a",
                        ToLocationId = "mine-b",
                        InfluenceSlotCount = 1,
                        BaseCost = 1
                    },
                    new MapRouteDefinition
                    {
                        RouteId = "route-b-c",
                        FromLocationId = "mine-b",
                        ToLocationId = "harbor-c",
                        InfluenceSlotCount = 1,
                        BaseCost = 2
                    }
                },
                Regions = new List<MapRegionDefinition>
                {
                    new MapRegionDefinition
                    {
                        RegionId = "north",
                        DisplayName = "North",
                        ScoreValue = 2,
                        LocationIds = new List<string> { "city-a", "mine-b" }
                    },
                    new MapRegionDefinition
                    {
                        RegionId = "south",
                        DisplayName = "South",
                        ScoreValue = 1,
                        LocationIds = new List<string> { "harbor-c" }
                    }
                }
            };
        }

        // TODO: Replace these placeholder layouts with structured map data from the rulebook.
        public static GameMapDefinition CreateFourPlayerPlaceholder()
        {
            return new GameMapDefinition
            {
                MapId = "placeholder-4p",
                MinPlayers = 4,
                MaxPlayers = 4,
                Locations = new List<MapLocationDefinition>
                {
                    new MapLocationDefinition
                    {
                        LocationId = "city-a",
                        RegionId = "north",
                        ResourceType = ResourceType.PureOriginium,
                        CanDockCity = true,
                        ResourceSlotCount = 1,
                        InfluenceSlotCount = 1
                    },
                    new MapLocationDefinition
                    {
                        LocationId = "mine-b",
                        RegionId = "north",
                        ResourceType = ResourceType.Iron,
                        CanDockCity = false,
                        ResourceSlotCount = 1,
                        InfluenceSlotCount = 1
                    },
                    new MapLocationDefinition
                    {
                        LocationId = "harbor-c",
                        RegionId = "south",
                        ResourceType = ResourceType.OriginiumShard,
                        CanDockCity = true,
                        ResourceSlotCount = 1,
                        InfluenceSlotCount = 1,
                        EventSlotCount = 1
                    },
                    new MapLocationDefinition
                    {
                        LocationId = "market-d",
                        RegionId = "south",
                        ResourceType = ResourceType.GoldVoucher,
                        CanDockCity = false,
                        ResourceSlotCount = 1,
                        InfluenceSlotCount = 1
                    }
                },
                Routes = new List<MapRouteDefinition>
                {
                    new MapRouteDefinition
                    {
                        RouteId = "route-a-b",
                        FromLocationId = "city-a",
                        ToLocationId = "mine-b",
                        InfluenceSlotCount = 1,
                        BaseCost = 1
                    },
                    new MapRouteDefinition
                    {
                        RouteId = "route-b-c",
                        FromLocationId = "mine-b",
                        ToLocationId = "harbor-c",
                        InfluenceSlotCount = 1,
                        BaseCost = 2
                    },
                    new MapRouteDefinition
                    {
                        RouteId = "route-c-d",
                        FromLocationId = "harbor-c",
                        ToLocationId = "market-d",
                        InfluenceSlotCount = 1,
                        BaseCost = 1
                    },
                    new MapRouteDefinition
                    {
                        RouteId = "route-d-a",
                        FromLocationId = "market-d",
                        ToLocationId = "city-a",
                        InfluenceSlotCount = 1,
                        BaseCost = 2
                    }
                },
                Regions = new List<MapRegionDefinition>
                {
                    new MapRegionDefinition
                    {
                        RegionId = "north",
                        DisplayName = "North",
                        ScoreValue = 2,
                        LocationIds = new List<string> { "city-a", "mine-b" }
                    },
                    new MapRegionDefinition
                    {
                        RegionId = "south",
                        DisplayName = "South",
                        ScoreValue = 2,
                        LocationIds = new List<string> { "harbor-c", "market-d" }
                    }
                }
            };
        }
    }
}
