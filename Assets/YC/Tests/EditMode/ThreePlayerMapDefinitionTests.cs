using System;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Maps;
using YC.Domain.Rules;
namespace YC.Tests.EditMode
{
    public sealed class ThreePlayerMapDefinitionTests
    {
        [Test]
        public void Resolution_IsStrictAndFixtureRequiresInjection()
        {
            Assert.That(StaticMapDefinitions.ForPlayerCount(3).MapId, Is.EqualTo("map-three-players"));
            Assert.That(StaticMapDefinitions.ForPlayerCount(4).MapId, Is.EqualTo(StaticMapDefinitions.FourPlayerMapId));
            foreach (var id in new[] { null, "", "unknown", "placeholder-3p", "test-fixture-3p" })
                Assert.Throws<ArgumentException>(() => StaticMapDefinitions.Resolve(id));
            foreach (var count in new[] { 0, 2, 5 })
                Assert.Throws<ArgumentOutOfRangeException>(() => StaticMapDefinitions.ForPlayerCount(count));
            var fixture = StaticMapDefinitions.CreateThreePlayerPlaceholder();
            Assert.That(fixture.MapId, Is.EqualTo("test-fixture-3p"));
            Assert.That(new MapQueryService(fixture).GetLocation("city-a"), Is.Not.Null);
        }
        [Test]
        public void Topology_MatchesSourceRoutesAndSlots()
        {
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            Assert.That(new[] { map.Locations.Count, map.Routes.Count, map.Regions.Count, map.Routes.Sum(r => r.InfluenceSlotCount) }, Is.EqualTo(new[] { 18, 20, 7, 29 }));
            var expected = new[] {
                "A1|1|A-01,A-02", "A2|2|A-02,A-03,B-03", "B1|1|A-02,B-02", "B2|2|B-01,B-02,C-01",
                "C1|1|C-02,C-03", "C2|2|B-02,B-03,C-01,C-02", "D1|2|A-03,D-01,D-02,D-03", "D2|2|D-02,D-03,F-02",
                "E1|2|E-01,E-02,F-02", "E2|2|C-03,E-02,E-03", "F1|1|D-02,F-01", "F2|1|F-01,F-02", "F3|1|F-01,F-03",
                "R1|1|A-01,D-01", "R2|1|A-01,B-01", "R3|1|C-03,E-01", "R4|2|E-03,F-02,F-03", "R5|1|C-02,E-01", "R6|1|A-02,B-01", "R7|2|B-03,D-03,E-01" };
            Assert.That(map.Routes.Select(r => r.RouteId + "|" + r.InfluenceSlotCount + "|" + string.Join(",", r.CoveredLocationIds)), Is.EquivalentTo(expected));
            foreach (var route in map.Routes)
            {
                Assert.That(route.RegionId, Is.EqualTo(route.RouteId.Substring(0, 1)));
                Assert.That(route.CoveredLocationIds.All(id => map.Locations.Any(l => l.LocationId == id)), Is.True);
                Assert.That(map.Regions.Single(r => r.RegionId == route.RegionId).RouteIds, Does.Contain(route.RouteId));
            }
        }
        [Test]
        public void ColorsAndEntranceReward_MatchSource()
        {
            var map = StaticMapDefinitions.Resolve(StaticMapDefinitions.ThreePlayerMapId);
            Assert.That(map.Locations.Where(l => l.EventColor == EventColor.Green).Select(l => l.LocationId), Is.EquivalentTo(new[] { "A-01", "A-02", "B-01", "B-02", "C-01" }));
            Assert.That(map.Locations.Where(l => l.EventColor == EventColor.Red).Select(l => l.LocationId), Is.EquivalentTo(new[] { "E-02", "E-03", "F-01", "F-02", "F-03" }));
            var reward = map.Locations.Single(l => l.LocationId == "B-01").InitialEntranceReward;
            Assert.That(new[] { reward.Originium, reward.Iron, reward.OriginiumShard, reward.GoldVoucher, reward.PureOriginium }, Is.EqualTo(new[] { 2, 2, 2, 0, 0 }));
            Assert.That(StaticMapDefinitions.GetEventColor(map.MapId, "B-01"), Is.EqualTo(EventColor.Green));
        }
        [Test]
        public void ColorQuery_UsesInjectedFieldsAndPreservesFourPlayerEntryPoint()
        {
            var map = new GameMapDefinition();
            map.Locations.Add(new MapLocationDefinition { LocationId = "A-01" });
            Assert.That(StaticMapDefinitions.GetEventColor(map, "A-01"), Is.EqualTo(EventColor.Yellow));
            map.Locations[0].EventColor = EventColor.Red;
            Assert.That(StaticMapDefinitions.GetEventColor(map, "A-01"), Is.EqualTo(EventColor.Red));
            Assert.Throws<ArgumentException>(() => StaticMapDefinitions.GetEventColor(map, "missing"));
            var four = StaticMapDefinitions.CreateFourPlayerMap();
            Assert.That(new[] { four.Locations.Count, four.Routes.Count, four.Regions.Count }, Is.EqualTo(new[] { 22, 22, 8 }));
            foreach (var location in four.Locations)
                Assert.That(location.EventColor, Is.EqualTo(StaticMapDefinitions.GetEventColor(location.LocationId)));
        }
    }
}
