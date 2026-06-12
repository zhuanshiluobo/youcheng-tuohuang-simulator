using NUnit.Framework;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class InfluenceMoveRuleTests
    {
        [Test]
        public void Validate_MoveToValidEmptySlot_Succeeds()
        {
            var state = CreateState();
            AddResourceToken(state, "A-01");
            AddResourceToken(state, "A-02");
            var sourceSlot = LocationSlot("A-01", 0);
            var targetSlot = LocationSlot("A-02", 0);
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = sourceSlot,
                LocationId = "A-01"
            });
            var rule = CreateRule();

            var result = rule.Validate(state, 1, sourceSlot, targetSlot);

            Assert.That(result.Succeeded, Is.True);
        }

        [Test]
        public void Validate_MoveToOccupiedSlot_FailsWithOccupiedSlot()
        {
            var state = CreateState();
            AddResourceToken(state, "A-01");
            AddResourceToken(state, "A-02");
            var sourceSlot = LocationSlot("A-01", 0);
            var targetSlot = LocationSlot("A-02", 0);
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = sourceSlot,
                LocationId = "A-01"
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = targetSlot,
                LocationId = "A-02"
            });
            var rule = CreateRule();

            var result = rule.Validate(state, 1, sourceSlot, targetSlot);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(InfluenceFailureCode.OccupiedSlot));
        }

        [Test]
        public void Validate_SourceInfluenceNotFound_FailsWithInfluenceNotFound()
        {
            var state = CreateState();
            AddResourceToken(state, "A-01");
            var rule = CreateRule();

            var result = rule.Validate(state, 1, LocationSlot("A-01", 0), LocationSlot("A-02", 0));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(InfluenceFailureCode.InfluenceNotFound));
        }

        [Test]
        public void Validate_MoveOpponentInfluence_FailsWithInfluenceOwnerMismatch()
        {
            var state = CreateState();
            AddResourceToken(state, "A-01");
            AddResourceToken(state, "A-02");
            var sourceSlot = LocationSlot("A-01", 0);
            var targetSlot = LocationSlot("A-02", 0);
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = sourceSlot,
                LocationId = "A-01"
            });
            var rule = CreateRule();

            var result = rule.Validate(state, 1, sourceSlot, targetSlot);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(InfluenceFailureCode.InfluenceOwnerMismatch));
        }

        [Test]
        public void Validate_MoveToUnexploredLocation_FailsWithResourceTokenRequired()
        {
            var state = CreateState();
            AddResourceToken(state, "A-01");
            var sourceSlot = LocationSlot("A-01", 0);
            var targetSlot = LocationSlot("A-02", 0);
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = sourceSlot,
                LocationId = "A-01"
            });
            var rule = CreateRule();

            var result = rule.Validate(state, 1, sourceSlot, targetSlot);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(InfluenceFailureCode.ResourceTokenRequired));
        }

        [Test]
        public void Validate_MoveToRoadCoveredRoute_FailsWithRouteCoveredByRoad()
        {
            var state = CreateState();
            AddResourceToken(state, "A-01");
            var sourceSlot = LocationSlot("A-01", 0);
            var targetSlot = RouteSlot("A1", 0);
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = sourceSlot,
                LocationId = "A-01"
            });
            state.Map.RoadRouteIds.Add("A1");
            var rule = CreateRule();

            var result = rule.Validate(state, 1, sourceSlot, targetSlot);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(InfluenceFailureCode.RouteCoveredByRoad));
        }

        [Test]
        public void Validate_DoesNotModifyState()
        {
            var state = CreateState();
            AddResourceToken(state, "A-01");
            AddResourceToken(state, "A-02");
            var sourceSlot = LocationSlot("A-01", 0);
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = sourceSlot,
                LocationId = "A-01"
            });
            var rule = CreateRule();
            var supplyBefore = state.FindPlayer(1).InfluenceSupply;
            var influencesBefore = state.Map.Influences.Count;

            rule.Validate(state, 1, sourceSlot, LocationSlot("A-02", 0));

            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(supplyBefore));
            Assert.That(state.Map.Influences.Count, Is.EqualTo(influencesBefore));
            Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(sourceSlot));
        }

        private static InfluenceMoveRule CreateRule()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var placementRule = new InfluencePlacementRule(mapQuery, new RuntimeStateRoadCoverageQuery());
            return new InfluenceMoveRule(placementRule);
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
