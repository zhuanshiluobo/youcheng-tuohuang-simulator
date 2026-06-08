using System;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Maps;

namespace YC.Tests.EditMode
{
    public sealed class MapPathSearchServiceTests
    {
        [Test]
        public void FindShortestPath_ReturnsDirectRouteWhenAvailable()
        {
            var service = CreatePathSearchService();

            var path = service.FindShortestPath("city-a", "mine-b");

            Assert.That(path.LocationIds, Is.EqualTo(new[] { "city-a", "mine-b" }));
            Assert.That(path.RouteIds, Is.EqualTo(new[] { "route-a-b" }));
        }

        [Test]
        public void FindShortestPath_ReturnsMultiStepRoute()
        {
            var service = CreatePathSearchService();

            var path = service.FindShortestPath("city-a", "harbor-c");

            Assert.That(path.LocationIds, Is.EqualTo(new[] { "city-a", "mine-b", "harbor-c" }));
            Assert.That(path.RouteIds, Is.EqualTo(new[] { "route-a-b", "route-b-c" }));
        }

        [Test]
        public void EnumerateSimplePaths_ReturnsPathsWithinStepLimit()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerPlaceholder());
            var service = new MapPathSearchService(mapQuery);

            var paths = service.EnumerateSimplePaths("city-a", "harbor-c", 2)
                .Select(path => string.Join(">", path.LocationIds))
                .ToArray();

            Assert.That(paths, Is.EquivalentTo(new[] { "city-a>mine-b>harbor-c", "city-a>market-d>harbor-c" }));
        }

        [Test]
        public void FindShortestPath_WhenLocationIsUnknown_Throws()
        {
            var service = CreatePathSearchService();

            Assert.That(() => service.FindShortestPath("missing", "city-a"), Throws.TypeOf<ArgumentException>());
        }

        private static MapPathSearchService CreatePathSearchService()
        {
            return new MapPathSearchService(new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder()));
        }
    }
}
