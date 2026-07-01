using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Exploration;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Tests.EditMode
{
    public sealed class TravelCostServiceTests
    {
        [Test]
        public void CalculateRouteCost_SumsRouteBaseCostAsGoldVoucher()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
            var pathSearch = new MapPathSearchService(mapQuery);
            var costService = new TravelCostService(mapQuery);
            var path = pathSearch.FindShortestPath("city-a", "harbor-c");

            var cost = costService.CalculateRouteCost(path);

            Assert.That(cost.GoldVoucher, Is.EqualTo(3));
            Assert.That(cost.PureOriginium, Is.EqualTo(0));
            Assert.That(cost.OriginiumShard, Is.EqualTo(0));
            Assert.That(cost.Iron, Is.EqualTo(0));
        }

        [Test]
        public void GetCityMoveBaseCost_ReturnsThreeOriginiumShard()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
            var costService = new TravelCostService(mapQuery);

            var cost = costService.GetCityMoveBaseCost();

            Assert.That(cost.OriginiumShard, Is.EqualTo(3));
            Assert.That(cost.PureOriginium, Is.EqualTo(0));
            Assert.That(cost.GoldVoucher, Is.EqualTo(0));
        }

        [Test]
        public void RouteTollService_ByRouteId_PaysEachRouteSeparately()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var tollService = new RouteTollService(mapQuery);
            var state = CreateRouteTollState();

            List<ExplorationTravelPayment> payments;
            var result = tollService.TryBuildPaymentPlan(
                state,
                1,
                new List<string> { "A1", "A2" },
                null,
                RouteTollPaymentKeyMode.RouteId,
                out payments);

            Assert.That(result.IsValid, Is.True, result.Reason);
            Assert.That(payments, Has.Count.EqualTo(2));
            Assert.That(tollService.SumPayments(payments), Is.EqualTo(4));
        }

        [Test]
        public void RouteTollService_SharedRegion_PaysRegionPaymentKeyOnce()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var tollService = new RouteTollService(mapQuery);
            var state = CreateRouteTollState();

            List<ExplorationTravelPayment> payments;
            var result = tollService.TryBuildPaymentPlan(
                state,
                1,
                new List<string> { "A1", "A2" },
                null,
                RouteTollPaymentKeyMode.SharedRegion,
                out payments);

            Assert.That(result.IsValid, Is.True, result.Reason);
            Assert.That(payments, Has.Count.EqualTo(1));
            Assert.That(payments[0].RouteId, Is.EqualTo("A1"));
            Assert.That(tollService.SumPayments(payments), Is.EqualTo(2));
        }

        [Test]
        public void RouteTollService_WithOwnRouteInfluence_SkipsSharedPaymentKey()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var tollService = new RouteTollService(mapQuery);
            var state = CreateRouteTollState();
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = InfluenceService.GetRouteSlotId("A2", 0),
                RouteId = "A2"
            });

            List<ExplorationTravelPayment> payments;
            var result = tollService.TryBuildPaymentPlan(
                state,
                1,
                new List<string> { "A1" },
                null,
                RouteTollPaymentKeyMode.SharedRegion,
                out payments);

            Assert.That(result.IsValid, Is.True, result.Reason);
            Assert.That(payments, Is.Empty);
        }

        private static GameState CreateRouteTollState()
        {
            return new GameState
            {
                Players =
                {
                    new PlayerState { PlayerId = 1, Color = PlayerColor.Red },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Blue }
                }
            };
        }
    }
}
