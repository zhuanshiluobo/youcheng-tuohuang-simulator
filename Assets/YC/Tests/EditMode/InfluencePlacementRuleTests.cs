using NUnit.Framework;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class InfluencePlacementRuleTests
    {
        [Test]
        public void Validate_OnExploredLocation_Succeeds()
        {
            var state = CreateState();
            AddResourceToken(state, "A-01");
            var rule = CreateRule();

            var result = rule.Validate(state, 1, LocationSlot("A-01", 0), true);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.FailureCode, Is.EqualTo(InfluenceFailureCode.None));
        }

        [Test]
        public void Validate_OnUnexploredLocation_FailsWithResourceTokenRequired()
        {
            var state = CreateState();
            var rule = CreateRule();

            var result = rule.Validate(state, 1, LocationSlot("A-01", 0), true);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(InfluenceFailureCode.ResourceTokenRequired));
        }

        [Test]
        public void Validate_OnLocationWithOpponentCity_FailsWithOpponentCityPresent()
        {
            var state = CreateState();
            AddResourceToken(state, "A-01");
            state.FindPlayer(2).CityLocationId = "A-01";
            var rule = CreateRule();

            var result = rule.Validate(state, 1, LocationSlot("A-01", 0), true);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(InfluenceFailureCode.OpponentCityPresent));
        }

        [Test]
        public void Validate_OnRoadCoveredRoute_FailsWithRouteCoveredByRoad()
        {
            var state = CreateState();
            state.Map.RoadRouteIds.Add("A1");
            var rule = CreateRule();

            var result = rule.Validate(state, 1, RouteSlot("A1", 0), true);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(InfluenceFailureCode.RouteCoveredByRoad));
        }

        [Test]
        public void Validate_WithNoSupply_FailsWithInsufficientSupply()
        {
            var state = CreateState();
            AddResourceToken(state, "A-01");
            state.FindPlayer(1).InfluenceSupply = 0;
            var rule = CreateRule();

            var result = rule.Validate(state, 1, LocationSlot("A-01", 0), true);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(InfluenceFailureCode.InsufficientSupply));
        }

        [Test]
        public void Validate_OnOccupiedSlot_FailsWithOccupiedSlot()
        {
            var state = CreateState();
            AddResourceToken(state, "A-01");
            var slotId = LocationSlot("A-01", 0);
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = slotId,
                LocationId = "A-01"
            });
            var rule = CreateRule();

            var result = rule.Validate(state, 1, slotId, true);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(InfluenceFailureCode.OccupiedSlot));
        }

        [Test]
        public void Validate_WithInvalidSlotId_FailsWithInvalidSlot()
        {
            var state = CreateState();
            var rule = CreateRule();

            var result = rule.Validate(state, 1, "location:no-such-location:0", true);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(InfluenceFailureCode.InvalidSlot));
        }

        [Test]
        public void Validate_WithInvalidPlayer_FailsWithInvalidPlayer()
        {
            var state = CreateState();
            var rule = CreateRule();

            var result = rule.Validate(state, 99, LocationSlot("A-01", 0), true);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(InfluenceFailureCode.InvalidPlayer));
        }

        [Test]
        public void Validate_OnExploredRoute_Succeeds()
        {
            var state = CreateState();
            var rule = CreateRule();

            var result = rule.Validate(state, 1, RouteSlot("A1", 0), true);

            Assert.That(result.Succeeded, Is.True);
        }

        [Test]
        public void Validate_OnOwnCityLocation_Succeeds()
        {
            var state = CreateState();
            AddResourceToken(state, "A-01");
            state.FindPlayer(1).CityLocationId = "A-01";
            var rule = CreateRule();

            var result = rule.Validate(state, 1, LocationSlot("A-01", 0), true);

            Assert.That(result.Succeeded, Is.True);
        }

        [Test]
        public void Validate_MoveWithoutSupplyCheck_SucceedsEvenWhenSupplyEmpty()
        {
            var state = CreateState();
            AddResourceToken(state, "A-01");
            state.FindPlayer(1).InfluenceSupply = 0;
            var rule = CreateRule();

            var result = rule.Validate(state, 1, LocationSlot("A-01", 0), false);

            Assert.That(result.Succeeded, Is.True);
        }

        [Test]
        public void Validate_DoesNotModifyStateOnFailure()
        {
            var state = CreateState();
            var rule = CreateRule();
            var supplyBefore = state.FindPlayer(1).InfluenceSupply;
            var influencesBefore = state.Map.Influences.Count;

            rule.Validate(state, 1, LocationSlot("A-01", 0), true);

            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(supplyBefore));
            Assert.That(state.Map.Influences.Count, Is.EqualTo(influencesBefore));
        }

        private static InfluencePlacementRule CreateRule()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            return new InfluencePlacementRule(mapQuery, new RuntimeStateRoadCoverageQuery());
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

        private static void AddResourceToken(GameState state, string locationId)
        {
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = locationId,
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
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
