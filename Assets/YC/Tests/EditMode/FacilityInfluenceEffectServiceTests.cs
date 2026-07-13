using NUnit.Framework;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class FacilityInfluenceEffectServiceTests
    {
        [Test]
        public void ReplaceOnce_WithLegalRouteSlot_ReplacesOpponentInfluence()
        {
            var fixture = CreateFixture();
            var slotId = InfluenceService.GetRouteSlotId("A1", 0);
            AddInfluence(fixture.State, 2, slotId, string.Empty, "A1");

            var result = fixture.Service.ReplaceOnce(fixture.State, 1, slotId);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.TargetRemoved, Is.True);
            Assert.That(result.ReplacementPlaced, Is.True);
            Assert.That(fixture.Influence.FindInfluence(fixture.State, slotId).PlayerId, Is.EqualTo(1));
            Assert.That(fixture.State.FindPlayer(1).InfluenceSupply, Is.EqualTo(29));
            Assert.That(fixture.State.FindPlayer(2).InfluenceSupply, Is.EqualTo(31));
        }

        [Test]
        public void ReplaceOnce_WhenOpponentCityBlocksPlacement_RemovesTargetOnly()
        {
            var fixture = CreateFixture();
            var slotId = InfluenceService.GetLocationSlotId("A-02", 0);
            fixture.State.FindPlayer(2).CityLocationId = "A-02";
            fixture.State.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "A-02",
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
            AddInfluence(fixture.State, 2, slotId, "A-02", string.Empty);

            var result = fixture.Service.ReplaceOnce(fixture.State, 1, slotId);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.TargetRemoved, Is.True);
            Assert.That(result.ReplacementPlaced, Is.False);
            Assert.That(result.PlacementFailureCode, Is.EqualTo(InfluenceFailureCode.OpponentCityPresent));
            Assert.That(fixture.Influence.FindInfluence(fixture.State, slotId), Is.Null);
            Assert.That(fixture.State.FindPlayer(2).InfluenceSupply, Is.EqualTo(31));
        }

        [Test]
        public void ReplaceOnce_WhenOwnSupplyEmpty_RemovesTargetOnly()
        {
            var fixture = CreateFixture();
            var slotId = InfluenceService.GetRouteSlotId("A1", 0);
            fixture.State.FindPlayer(1).InfluenceSupply = 0;
            AddInfluence(fixture.State, 2, slotId, string.Empty, "A1");

            var result = fixture.Service.ReplaceOnce(fixture.State, 1, slotId);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.ReplacementPlaced, Is.False);
            Assert.That(result.PlacementFailureCode, Is.EqualTo(InfluenceFailureCode.InsufficientSupply));
            Assert.That(fixture.Influence.FindInfluence(fixture.State, slotId), Is.Null);
        }

        [Test]
        public void ReplaceOnce_TargetingOwnInfluence_FailsWithoutChangingState()
        {
            var fixture = CreateFixture();
            var slotId = InfluenceService.GetRouteSlotId("A1", 0);
            AddInfluence(fixture.State, 1, slotId, string.Empty, "A1");

            var result = fixture.Service.ReplaceOnce(fixture.State, 1, slotId);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.TargetRemoved, Is.False);
            Assert.That(fixture.Influence.FindInfluence(fixture.State, slotId).PlayerId, Is.EqualTo(1));
            Assert.That(fixture.State.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
        }

        private static Fixture CreateFixture()
        {
            var state = new GameState();
            state.Players.Add(new PlayerState { PlayerId = 1, Name = "P1" });
            state.Players.Add(new PlayerState { PlayerId = 2, Name = "P2" });
            var influence = new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
            return new Fixture
            {
                State = state,
                Influence = influence,
                Service = new FacilityInfluenceEffectService(influence)
            };
        }

        private static void AddInfluence(GameState state, int playerId, string slotId, string locationId, string routeId)
        {
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = playerId,
                SlotId = slotId,
                LocationId = locationId,
                RouteId = routeId
            });
        }

        private sealed class Fixture
        {
            public GameState State;
            public InfluenceService Influence;
            public FacilityInfluenceEffectService Service;
        }
    }
}
