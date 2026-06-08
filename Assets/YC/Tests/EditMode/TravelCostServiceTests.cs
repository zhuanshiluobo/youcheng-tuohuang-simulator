using NUnit.Framework;
using YC.Domain.Maps;
using YC.Domain.Rules;

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
        public void GetCityMoveBaseCost_ReturnsThreePureOriginium()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
            var costService = new TravelCostService(mapQuery);

            var cost = costService.GetCityMoveBaseCost();

            Assert.That(cost.PureOriginium, Is.EqualTo(3));
            Assert.That(cost.GoldVoucher, Is.EqualTo(0));
        }
    }
}
