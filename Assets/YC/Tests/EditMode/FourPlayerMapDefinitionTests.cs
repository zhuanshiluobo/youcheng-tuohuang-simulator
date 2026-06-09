using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Maps;

namespace YC.Tests.EditMode
{
    public sealed class FourPlayerMapDefinitionTests
    {
        [Test]
        public void CreateFourPlayerMap_HasExpectedResourcePointAndRegionCounts()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();

            Assert.That(map.MapId, Is.EqualTo("map-four-players"));
            Assert.That(map.MinPlayers, Is.EqualTo(4));
            Assert.That(map.MaxPlayers, Is.EqualTo(4));
            Assert.That(map.Locations, Has.Count.EqualTo(22));
            Assert.That(map.Regions, Has.Count.EqualTo(8));
            Assert.That(map.Routes, Has.Count.EqualTo(22));
        }

        [Test]
        public void CreateFourPlayerMap_UsesUniqueStableIds()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();

            Assert.That(map.Locations.Select(location => location.LocationId).Distinct(), Has.Count.EqualTo(22));
            Assert.That(map.Routes.Select(route => route.RouteId).Distinct(), Has.Count.EqualTo(22));
            Assert.That(map.Regions.Select(region => region.RegionId).Distinct(), Has.Count.EqualTo(8));
        }

        [Test]
        public void CreateFourPlayerMap_DefinesExpectedRouteCoverageAndInfluenceSlots()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();

            AssertRoute(map, "A1", 1, "A-01", "A-02");
            AssertRoute(map, "A2", 2, "A-02", "A-03", "B-03");
            AssertRoute(map, "B1", 1, "A-02", "B-02");
            AssertRoute(map, "B2", 2, "B-01", "B-02", "C-01");
            AssertRoute(map, "C1", 1, "C-02", "C-03");
            AssertRoute(map, "C2", 2, "B-02", "B-03", "C-01", "C-02");
            AssertRoute(map, "D1", 2, "A-03", "D-01", "D-02", "D-03");
            AssertRoute(map, "D2", 2, "D-02", "D-03", "F-02");
            AssertRoute(map, "E1", 2, "E-01", "E-02", "F-02");
            AssertRoute(map, "E2", 2, "C-03", "E-02", "E-03");
            AssertRoute(map, "F1", 1, "D-02", "F-01");
            AssertRoute(map, "F2", 1, "F-01", "F-02");
            AssertRoute(map, "F3", 2, "F-01", "F-03", "G-04");
            AssertRoute(map, "G1", 1, "G-02", "G-03");
            AssertRoute(map, "G2", 1, "G-03", "G-04");
            AssertRoute(map, "R1", 2, "A-01", "D-01", "G-01", "G-02");
            AssertRoute(map, "R2", 2, "A-01", "B-01", "G-01");
            AssertRoute(map, "R3", 1, "C-03", "G-03");
            AssertRoute(map, "R4", 1, "E-03", "F-03");
            AssertRoute(map, "R5", 1, "E-03", "F-02");
            AssertRoute(map, "R6", 1, "A-02", "B-01");
            AssertRoute(map, "R7", 2, "B-03", "D-03", "E-01");
        }

        [Test]
        public void CreateFourPlayerMap_DefinesEveryLocationWithTwoInfluenceSlots()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();

            Assert.That(map.Locations, Has.All.Matches<MapLocationDefinition>(
                location => location.InfluenceSlotCount == 2 &&
                            location.ResourceSlotCount == 1 &&
                            location.EventSlotCount == 1));
        }

        [Test]
        public void GetAdjacentLocations_FourPlayerMapMatchesRoutebookAdjacency()
        {
            var service = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());

            AssertAdjacent(service, "A-01", "G-01", "D-01", "G-02", "A-02", "B-01");
            AssertAdjacent(service, "A-02", "A-01", "A-03", "B-03", "B-02", "B-01");
            AssertAdjacent(service, "A-03", "A-02", "B-03", "D-01", "D-02", "D-03");
            AssertAdjacent(service, "B-01", "A-02", "A-01", "G-01", "B-02", "C-01");
            AssertAdjacent(service, "B-02", "A-02", "B-01", "C-01", "B-03", "C-02");
            AssertAdjacent(service, "B-03", "A-02", "A-03", "B-02", "C-01", "C-02", "D-03", "E-01");
            AssertAdjacent(service, "C-01", "B-01", "B-02", "B-03", "C-02");
            AssertAdjacent(service, "C-02", "C-03", "B-02", "B-03", "C-01");
            AssertAdjacent(service, "C-03", "C-02", "E-02", "E-03", "G-03");
            AssertAdjacent(service, "D-01", "A-01", "G-01", "G-02", "A-03", "D-02", "D-03");
            AssertAdjacent(service, "D-02", "A-03", "D-01", "D-03", "F-01", "F-02");
            AssertAdjacent(service, "D-03", "A-03", "D-01", "D-02", "F-02", "B-03", "E-01");
            AssertAdjacent(service, "E-01", "B-03", "D-03", "F-02", "E-02");
            AssertAdjacent(service, "E-02", "E-01", "F-02", "C-03", "E-03");
            AssertAdjacent(service, "E-03", "C-03", "E-02", "F-03", "F-02");
            AssertAdjacent(service, "F-01", "D-02", "F-02", "F-03", "G-04");
            AssertAdjacent(service, "F-02", "F-01", "E-03", "E-01", "E-02", "D-02", "D-03");
            AssertAdjacent(service, "F-03", "E-03", "F-01", "G-04");
            AssertAdjacent(service, "G-01", "A-01", "B-01", "D-01", "G-02");
            AssertAdjacent(service, "G-02", "G-03", "A-01", "D-01", "G-01");
            AssertAdjacent(service, "G-03", "G-02", "G-04", "C-03");
            AssertAdjacent(service, "G-04", "G-03", "F-01", "F-03");
        }

        [Test]
        public void CreateFourPlayerMap_AssignsLocationsToMapBlocks()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var service = new MapQueryService(map);

            Assert.That(service.GetRegionForLocation("G-04").RegionId, Is.EqualTo("G"));
            Assert.That(service.GetRegionForLocation("B-02").RegionId, Is.EqualTo("B"));
            Assert.That(map.Regions.Single(region => region.RegionId == "R").ScoreValue, Is.EqualTo(3));
            Assert.That(map.Regions.Single(region => region.RegionId == "G").LocationIds,
                Is.EquivalentTo(new[] { "G-01", "G-02", "G-03", "G-04" }));
        }

        [Test]
        public void CreateFourPlayerMap_AllResourcePointsCanDockCity()
        {
            var service = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());

            Assert.That(service.CanDockCity("A-01"), Is.True);
            Assert.That(service.CanDockCity("B-01"), Is.True);
            Assert.That(service.CanDockCity("G-04"), Is.True);
        }

        [Test]
        public void FindRoute_ReturnsKnownFourPlayerRouteInBothDirections()
        {
            var service = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());

            var forward = service.FindRoute("B-02", "C-02");
            var reverse = service.FindRoute("C-02", "B-02");

            Assert.That(forward.RouteId, Is.EqualTo("C2"));
            Assert.That(forward.InfluenceSlotCount, Is.EqualTo(2));
            Assert.That(forward.BaseCost, Is.EqualTo(2));
            Assert.That(reverse, Is.SameAs(forward));
        }

        [Test]
        public void FindShortestPath_FindsKeyPathAcrossGreenBlock()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var service = new MapPathSearchService(mapQuery);

            var path = service.FindShortestPath("G-03", "G-01");

            Assert.That(path.LocationIds, Is.EqualTo(new[] { "G-03", "G-02", "G-01" }));
            Assert.That(path.RouteIds, Is.EqualTo(new[] { "G1", "R1" }));
        }

        [Test]
        public void FourPlayerRoutes_KeepEveryResourcePointReachable()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var service = new MapQueryService(map);
            var visited = CollectReachableLocationIds(service, "A-01");

            Assert.That(visited, Is.EquivalentTo(map.Locations.Select(location => location.LocationId)));
        }

        [Test]
        public void FindRoute_WhenFourPlayerLocationsAreNotAdjacent_Throws()
        {
            var service = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());

            Assert.That(() => service.FindRoute("A-01", "C-01"), Throws.TypeOf<ArgumentException>());
        }

        private static HashSet<string> CollectReachableLocationIds(IMapQueryService service, string startLocationId)
        {
            var visited = new HashSet<string> { startLocationId };
            var queue = new Queue<string>();
            queue.Enqueue(startLocationId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var adjacentLocations = service.GetAdjacentLocations(current);
                for (var i = 0; i < adjacentLocations.Count; i++)
                {
                    var adjacentLocationId = adjacentLocations[i].LocationId;
                    if (!visited.Add(adjacentLocationId))
                    {
                        continue;
                    }

                    queue.Enqueue(adjacentLocationId);
                }
            }

            return visited;
        }

        private static void AssertRoute(
            GameMapDefinition map,
            string routeId,
            int influenceSlotCount,
            params string[] coveredLocationIds)
        {
            var route = map.Routes.Single(candidate => candidate.RouteId == routeId);

            Assert.That(route.InfluenceSlotCount, Is.EqualTo(influenceSlotCount));
            Assert.That(route.BaseCost, Is.EqualTo(2));
            Assert.That(route.CoveredLocationIds, Is.EqualTo(coveredLocationIds));
            Assert.That(route.FromLocationId, Is.EqualTo(coveredLocationIds[0]));
            Assert.That(route.ToLocationId, Is.EqualTo(coveredLocationIds[1]));
        }

        private static void AssertAdjacent(
            IMapQueryService service,
            string locationId,
            params string[] expectedAdjacentLocationIds)
        {
            var adjacentLocationIds = service.GetAdjacentLocations(locationId)
                .Select(location => location.LocationId)
                .ToArray();

            Assert.That(adjacentLocationIds, Is.EquivalentTo(expectedAdjacentLocationIds), locationId);
        }
    }
}
