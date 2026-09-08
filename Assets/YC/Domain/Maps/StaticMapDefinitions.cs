using System;
using System.Collections.Generic;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Maps
{
    public static class StaticMapDefinitions
    {
        public const string FourPlayerMapId = "map-four-players";
        public const string ThreePlayerMapId = "map-three-players";
        public const string ThreePlayerTestFixtureMapId = "test-fixture-3p";

        public static readonly HashSet<string> FourPlayerInitialLocationIds = new HashSet<string>
        {
            "G-01", "A-01", "A-02", "B-01", "B-02", "C-01"
        };

        public static readonly HashSet<string> FourPlayerRedZoneLocationIds = new HashSet<string>
        {
            "G-04", "F-01", "F-02", "F-03", "E-02", "E-03"
        };

        public static EventColor GetEventColor(string locationId)
        {
            if (FourPlayerInitialLocationIds.Contains(locationId)) return EventColor.Green;
            if (FourPlayerRedZoneLocationIds.Contains(locationId)) return EventColor.Red;
            return EventColor.Yellow;
        }

        public static EventColor GetEventColor(string mapId, string locationId)
        {
            return GetEventColor(Resolve(mapId), locationId);
        }

        // 使用当前地图的数据；测试小图通过显式注入保留默认黄色，不按重名地点套用四人颜色。
        public static EventColor GetEventColor(GameMapDefinition map, string locationId)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            foreach (var location in map.Locations)
            {
                if (string.Equals(location.LocationId, locationId, StringComparison.Ordinal))
                    return location.EventColor;
            }
            throw new ArgumentException("当前地图不存在地点：" + locationId, nameof(locationId));
        }

        // 仅供显式注入的测试夹具使用；正式地图解析永不返回此图。
        public static GameMapDefinition CreateThreePlayerPlaceholder()
        {
            return new GameMapDefinition
            {
                MapId = ThreePlayerTestFixtureMapId,
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

        public static GameMapDefinition Resolve(string mapId)
        {
            switch (mapId)
            {
                case ThreePlayerMapId: return CreateThreePlayerMap();
                case FourPlayerMapId: return CreateFourPlayerMap();
                default: throw new ArgumentException("未知的正式地图 ID：" + mapId, nameof(mapId));
            }
        }

        public static GameMapDefinition ForPlayerCount(int playerCount)
        {
            switch (playerCount)
            {
                case 3: return CreateThreePlayerMap();
                case 4: return CreateFourPlayerMap();
                default: throw new ArgumentOutOfRangeException(nameof(playerCount), playerCount, "正式地图只支持 3 或 4 人。");
            }
        }

        public static GameMapDefinition CreateThreePlayerMap()
        {
            // 按三人.jpg 核对：18 地点、20 航道、7 区块，完整映射见三人地图路线数据.txt。
            // R4 是左侧三叉双槽航道；R5 是 E-01 与 C-02 之间的单槽航道。
            return new GameMapDefinition
            {
                MapId = ThreePlayerMapId,
                MinPlayers = 3,
                MaxPlayers = 3,
                Locations = new List<MapLocationDefinition>
                {
                    Location("A-01", "A", ResourceType.Iron, 2, eventColor: EventColor.Green),
                    Location("A-02", "A", ResourceType.Iron, 2, eventColor: EventColor.Green),
                    Location("A-03", "A", ResourceType.Iron, 2, eventColor: EventColor.Yellow),
                    Location("B-01", "B", ResourceType.OriginiumShard, 2, eventColor: EventColor.Green, initialEntranceReward: new ResourceSet { Originium = 2, Iron = 2, OriginiumShard = 2 }),
                    Location("B-02", "B", ResourceType.OriginiumShard, 2, eventColor: EventColor.Green),
                    Location("B-03", "B", ResourceType.OriginiumShard, 2, eventColor: EventColor.Yellow),
                    Location("C-01", "C", ResourceType.PureOriginium, 2, eventColor: EventColor.Green),
                    Location("C-02", "C", ResourceType.PureOriginium, 2, eventColor: EventColor.Yellow),
                    Location("C-03", "C", ResourceType.PureOriginium, 2, eventColor: EventColor.Yellow),
                    Location("D-01", "D", ResourceType.Iron, 2, eventColor: EventColor.Yellow),
                    Location("D-02", "D", ResourceType.Iron, 2, eventColor: EventColor.Yellow),
                    Location("D-03", "D", ResourceType.Iron, 2, eventColor: EventColor.Yellow),
                    Location("E-01", "E", ResourceType.OriginiumShard, 2, eventColor: EventColor.Yellow),
                    Location("E-02", "E", ResourceType.OriginiumShard, 2, isRedZone: true, eventColor: EventColor.Red),
                    Location("E-03", "E", ResourceType.OriginiumShard, 2, isRedZone: true, eventColor: EventColor.Red),
                    Location("F-01", "F", ResourceType.OriginiumShard, 2, isRedZone: true, eventColor: EventColor.Red),
                    Location("F-02", "F", ResourceType.OriginiumShard, 2, isRedZone: true, eventColor: EventColor.Red),
                    Location("F-03", "F", ResourceType.OriginiumShard, 2, isRedZone: true, eventColor: EventColor.Red)
                },
                Routes = new List<MapRouteDefinition>
                {
                    Route("A1", "A", 1, "A-01", "A-02"),
                    Route("A2", "A", 2, "A-02", "A-03", "B-03"),
                    Route("B1", "B", 1, "A-02", "B-02"),
                    Route("B2", "B", 2, "B-01", "B-02", "C-01"),
                    Route("C1", "C", 1, "C-02", "C-03"),
                    Route("C2", "C", 2, "B-02", "B-03", "C-01", "C-02"),
                    Route("D1", "D", 2, "A-03", "D-01", "D-02", "D-03"),
                    Route("D2", "D", 2, "D-02", "D-03", "F-02"),
                    Route("E1", "E", 2, "E-01", "E-02", "F-02"),
                    Route("E2", "E", 2, "C-03", "E-02", "E-03"),
                    Route("F1", "F", 1, "D-02", "F-01"),
                    Route("F2", "F", 1, "F-01", "F-02"),
                    Route("F3", "F", 1, "F-01", "F-03"),
                    Route("R1", "R", 1, "A-01", "D-01"),
                    Route("R2", "R", 1, "A-01", "B-01"),
                    Route("R3", "R", 1, "C-03", "E-01"),
                    Route("R4", "R", 2, "E-03", "F-02", "F-03"),
                    Route("R5", "R", 1, "C-02", "E-01"),
                    Route("R6", "R", 1, "A-02", "B-01"),
                    Route("R7", "R", 2, "B-03", "D-03", "E-01")
                },
                Regions = new List<MapRegionDefinition>
                {
                    Region("A", "A区", 3, new[] { "A1", "A2" }, "A-01", "A-02", "A-03"),
                    Region("B", "B区", 3, new[] { "B1", "B2" }, "B-01", "B-02", "B-03"),
                    Region("C", "C区", 3, new[] { "C1", "C2" }, "C-01", "C-02", "C-03"),
                    Region("D", "D区", 3, new[] { "D1", "D2" }, "D-01", "D-02", "D-03"),
                    Region("E", "E区", 4, new[] { "E1", "E2" }, "E-01", "E-02", "E-03"),
                    Region("F", "F区", 4, new[] { "F1", "F2", "F3" }, "F-01", "F-02", "F-03"),
                    Region("R", "R区", 3, new[] { "R1", "R2", "R3", "R4", "R5", "R6", "R7" })
                }
            };
        }

        public static GameMapDefinition CreateFourPlayerMap()
        {
            // All 22 locations, 22 routes, and 8 regions verified against 游城拓荒/路线图终版.txt.
            return new GameMapDefinition
            {
                MapId = FourPlayerMapId,
                MinPlayers = 4,
                MaxPlayers = 4,
                Locations = new List<MapLocationDefinition>
                {
                    Location("A-01", "A", ResourceType.Iron, 2),
                    Location("A-02", "A", ResourceType.Iron, 2),
                    Location("A-03", "A", ResourceType.Iron, 2),
                    Location("B-01", "B", ResourceType.OriginiumShard, 2),
                    Location("B-02", "B", ResourceType.OriginiumShard, 2),
                    Location("B-03", "B", ResourceType.OriginiumShard, 2),
                    Location("C-01", "C", ResourceType.PureOriginium, 2),
                    Location("C-02", "C", ResourceType.PureOriginium, 2),
                    Location("C-03", "C", ResourceType.PureOriginium, 2),
                    Location("D-01", "D", ResourceType.Iron, 2),
                    Location("D-02", "D", ResourceType.Iron, 2),
                    Location("D-03", "D", ResourceType.Iron, 2),
                    Location("E-01", "E", ResourceType.OriginiumShard, 2),
                    Location("E-02", "E", ResourceType.OriginiumShard, 2, isRedZone: true),
                    Location("E-03", "E", ResourceType.OriginiumShard, 2, isRedZone: true),
                    Location("F-01", "F", ResourceType.OriginiumShard, 2, isRedZone: true),
                    Location("F-02", "F", ResourceType.OriginiumShard, 2, isRedZone: true),
                    Location("F-03", "F", ResourceType.OriginiumShard, 2, isRedZone: true),
                    Location("G-01", "G", ResourceType.PureOriginium, 2),
                    Location("G-02", "G", ResourceType.PureOriginium, 2),
                    Location("G-03", "G", ResourceType.PureOriginium, 2),
                    Location("G-04", "G", ResourceType.PureOriginium, 2, isRedZone: true)
                },
                Routes = new List<MapRouteDefinition>
                {
                    Route("A1", "A", 1, "A-01", "A-02"),
                    Route("A2", "A", 2, "A-02", "A-03", "B-03"),
                    Route("B1", "B", 1, "A-02", "B-02"),
                    Route("B2", "B", 2, "B-01", "B-02", "C-01"),
                    Route("C1", "C", 1, "C-02", "C-03"),
                    Route("C2", "C", 2, "B-02", "B-03", "C-01", "C-02"),
                    Route("D1", "D", 2, "A-03", "D-01", "D-02", "D-03"),
                    Route("D2", "D", 2, "D-02", "D-03", "F-02"),
                    Route("E1", "E", 2, "E-01", "E-02", "F-02"),
                    Route("E2", "E", 2, "C-03", "E-02", "E-03"),
                    Route("F1", "F", 1, "D-02", "F-01"),
                    Route("F2", "F", 1, "F-01", "F-02"),
                    Route("F3", "F", 2, "F-01", "F-03", "G-04"),
                    Route("G1", "G", 1, "G-02", "G-03"),
                    Route("G2", "G", 1, "G-03", "G-04"),
                    Route("R1", "R", 2, "A-01", "D-01", "G-01", "G-02"),
                    Route("R2", "R", 2, "A-01", "B-01", "G-01"),
                    Route("R3", "R", 1, "C-03", "G-03"),
                    Route("R4", "R", 1, "E-03", "F-03"),
                    Route("R5", "R", 1, "E-03", "F-02"),
                    Route("R6", "R", 1, "A-02", "B-01"),
                    Route("R7", "R", 2, "B-03", "D-03", "E-01")
                },
                Regions = new List<MapRegionDefinition>
                {
                    Region("A", "A Block", 3, new[] { "A1", "A2" }, "A-01", "A-02", "A-03"),
                    Region("B", "B Block", 3, new[] { "B1", "B2" }, "B-01", "B-02", "B-03"),
                    Region("C", "C Block", 3, new[] { "C1", "C2" }, "C-01", "C-02", "C-03"),
                    Region("D", "D Block", 3, new[] { "D1", "D2" }, "D-01", "D-02", "D-03"),
                    Region("E", "E Block", 4, new[] { "E1", "E2" }, "E-01", "E-02", "E-03"),
                    Region("F", "F Block", 4, new[] { "F1", "F2", "F3" }, "F-01", "F-02", "F-03"),
                    Region("G", "G Block", 3, new[] { "G1", "G2" }, "G-01", "G-02", "G-03", "G-04"),
                    Region("R", "R Block", 3, new[] { "R1", "R2", "R3", "R4", "R5", "R6", "R7" })
                }
            };
        }

        private static MapLocationDefinition Location(
            string locationId,
            string regionId,
            ResourceType resourceType,
            int influenceSlotCount,
            bool isRedZone = false,
            EventColor? eventColor = null,
            ResourceSet initialEntranceReward = null)
        {
            return new MapLocationDefinition
            {
                LocationId = locationId,
                RegionId = regionId,
                ResourceType = resourceType,
                CanDockCity = true,
                IsRedZone = isRedZone,
                EventColor = eventColor ?? GetEventColor(locationId),
                InitialEntranceReward = initialEntranceReward ?? new ResourceSet(),
                ResourceSlotCount = 1,
                InfluenceSlotCount = influenceSlotCount,
                EventSlotCount = 1
            };
        }

        private static MapRouteDefinition Route(
            string routeId,
            string regionId,
            int influenceSlotCount,
            params string[] coveredLocationIds)
        {
            return new MapRouteDefinition
            {
                RouteId = routeId,
                FromLocationId = coveredLocationIds.Length > 0 ? coveredLocationIds[0] : string.Empty,
                ToLocationId = coveredLocationIds.Length > 1 ? coveredLocationIds[1] : string.Empty,
                RegionId = regionId,
                CoveredLocationIds = new List<string>(coveredLocationIds),
                InfluenceSlotCount = influenceSlotCount,
                BaseCost = 2
            };
        }

        private static MapRegionDefinition Region(
            string regionId,
            string displayName,
            int scoreValue,
            string[] routeIds,
            params string[] locationIds)
        {
            return new MapRegionDefinition
            {
                RegionId = regionId,
                DisplayName = displayName,
                ScoreValue = scoreValue,
                LocationIds = new List<string>(locationIds),
                RouteIds = new List<string>(routeIds)
            };
        }
    }
}
