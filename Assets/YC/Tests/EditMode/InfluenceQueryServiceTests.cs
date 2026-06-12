using NUnit.Framework;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class InfluenceQueryServiceTests
    {
        [Test]
        public void HasInfluenceAt_EmptySlot_ReturnsFalse()
        {
            var state = CreateState();
            var service = CreateService();

            Assert.That(service.HasInfluenceAt(state, LocationSlot("A-01", 0)), Is.False);
        }

        [Test]
        public void HasInfluenceAt_OccupiedSlot_ReturnsTrue()
        {
            var state = CreateState();
            var slotId = LocationSlot("A-01", 0);
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = slotId, LocationId = "A-01" });
            var service = CreateService();

            Assert.That(service.HasInfluenceAt(state, slotId), Is.True);
        }

        [Test]
        public void CountInRegion_IncludesLocationInfluence()
        {
            var state = CreateState();
            var slotId = LocationSlot("A-01", 0);
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = slotId, LocationId = "A-01" });
            var service = CreateService();

            var count = service.CountInRegion(state, 1, "A", false);

            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void CountInRegion_WithCityBonus_AddsTwo()
        {
            var state = CreateState();
            var slotId = LocationSlot("A-01", 0);
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = slotId, LocationId = "A-01" });
            state.FindPlayer(1).CityLocationId = "A-01";
            var service = CreateService();

            var withoutCity = service.CountInRegion(state, 1, "A", false);
            var withCity = service.CountInRegion(state, 1, "A", true);

            Assert.That(withoutCity, Is.EqualTo(1));
            Assert.That(withCity, Is.EqualTo(3));
        }

        [Test]
        public void CountInRegion_WithoutCityBonus_ExcludesCity()
        {
            var state = CreateState();
            state.FindPlayer(1).CityLocationId = "A-01";
            var service = CreateService();

            var count = service.CountInRegion(state, 1, "A", false);

            Assert.That(count, Is.EqualTo(0));
        }

        [Test]
        public void CountAllPlayersInRegion_MultiplePlayers()
        {
            var state = CreateState();
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = LocationSlot("A-01", 0), LocationId = "A-01" });
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = LocationSlot("A-01", 1), LocationId = "A-01" });
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 2, SlotId = LocationSlot("A-02", 0), LocationId = "A-02" });
            var service = CreateService();

            var counts = service.CountAllPlayersInRegion(state, "A", false);

            Assert.That(counts[1], Is.EqualTo(2));
            Assert.That(counts[2], Is.EqualTo(1));
        }

        [Test]
        public void ListByPlayer_ReturnsCorrectInfluences()
        {
            var state = CreateState();
            var slot1 = LocationSlot("A-01", 0);
            var slot2 = LocationSlot("A-02", 0);
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = slot1, LocationId = "A-01" });
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = slot2, LocationId = "A-02" });
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 2, SlotId = LocationSlot("B-01", 0), LocationId = "B-01" });
            var service = CreateService();

            var list = service.ListByPlayer(state, 1);

            Assert.That(list.Count, Is.EqualTo(2));
            Assert.That(list[0].SlotId, Is.EqualTo(slot1));
            Assert.That(list[1].SlotId, Is.EqualTo(slot2));
        }

        [Test]
        public void FindInfluence_ExistingSlot_ReturnsPlacement()
        {
            var state = CreateState();
            var slotId = LocationSlot("A-01", 0);
            var placement = new InfluencePlacement { PlayerId = 1, SlotId = slotId, LocationId = "A-01" };
            state.Map.Influences.Add(placement);
            var service = CreateService();

            var found = service.FindInfluence(state, slotId);

            Assert.That(found, Is.Not.Null);
            Assert.That(found.PlayerId, Is.EqualTo(1));
            Assert.That(found.SlotId, Is.EqualTo(slotId));
        }

        [Test]
        public void FindInfluence_NonexistentSlot_ReturnsNull()
        {
            var state = CreateState();
            var service = CreateService();

            var found = service.FindInfluence(state, LocationSlot("A-01", 0));

            Assert.That(found, Is.Null);
        }

        [Test]
        public void CountInRegion_IncludesRouteInfluence()
        {
            var state = CreateState();
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = RouteSlot("A1", 0),
                RouteId = "A1"
            });
            var service = CreateService();

            var count = service.CountInRegion(state, 1, "A", false);

            Assert.That(count, Is.EqualTo(1));
        }

        private static InfluenceQueryService CreateService()
        {
            return new InfluenceQueryService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
        }

        private static GameState CreateState()
        {
            return new GameState
            {
                Players =
                {
                    new PlayerState { PlayerId = 1, Color = PlayerColor.Red, CityLocationId = "G-01" },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Blue, CityLocationId = "C-01" }
                }
            };
        }

        private static string LocationSlot(string locationId, int index)
        {
            return "location:" + locationId + ":" + index;
        }

        private static string RouteSlot(string routeId, int index)
        {
            return "route:" + routeId + ":" + index;
        }
    }
}
