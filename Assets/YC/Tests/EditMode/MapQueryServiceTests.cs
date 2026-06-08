using System;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Maps;

namespace YC.Tests.EditMode
{
    public sealed class MapQueryServiceTests
    {
        [Test]
        public void GetAdjacentLocations_ReturnsConnectedLocations()
        {
            var service = CreateService();

            var adjacentLocationIds = service.GetAdjacentLocations("mine-b")
                .Select(location => location.LocationId)
                .ToArray();

            Assert.That(adjacentLocationIds, Is.EquivalentTo(new[] { "city-a", "harbor-c" }));
        }

        [Test]
        public void FindRoute_MatchesRouteInBothDirections()
        {
            var service = CreateService();

            var forward = service.FindRoute("city-a", "mine-b");
            var reverse = service.FindRoute("mine-b", "city-a");

            Assert.That(forward.RouteId, Is.EqualTo("route-a-b"));
            Assert.That(reverse, Is.SameAs(forward));
        }

        [Test]
        public void GetRegionForLocation_ReturnsOwningRegion()
        {
            var service = CreateService();

            var region = service.GetRegionForLocation("harbor-c");

            Assert.That(region.RegionId, Is.EqualTo("south"));
            Assert.That(region.LocationIds, Does.Contain("harbor-c"));
        }

        [Test]
        public void CanDockCity_ReturnsLocationDockFlag()
        {
            var service = CreateService();

            Assert.That(service.CanDockCity("city-a"), Is.True);
            Assert.That(service.CanDockCity("mine-b"), Is.False);
        }

        [Test]
        public void QueryMethods_WithUnknownIds_ThrowArgumentException()
        {
            var service = CreateService();

            Assert.That(() => service.GetLocation("missing"), Throws.TypeOf<ArgumentException>());
            Assert.That(() => service.GetRoute("missing"), Throws.TypeOf<ArgumentException>());
            Assert.That(() => service.FindRoute("missing", "city-a"), Throws.TypeOf<ArgumentException>());
            Assert.That(() => service.GetAdjacentLocations("missing"), Throws.TypeOf<ArgumentException>());
            Assert.That(() => service.GetRegionForLocation("missing"), Throws.TypeOf<ArgumentException>());
            Assert.That(() => service.CanDockCity("missing"), Throws.TypeOf<ArgumentException>());
        }

        private static MapQueryService CreateService()
        {
            return new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
        }
    }
}
