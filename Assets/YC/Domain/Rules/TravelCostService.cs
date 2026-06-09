using System;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    public sealed class TravelCostService
    {
        private readonly IMapQueryService mapQuery;

        public TravelCostService(IMapQueryService mapQuery)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
        }

        public ResourceSet CalculateRouteCost(MapPath path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            var totalGoldVoucherCost = 0;
            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                totalGoldVoucherCost += Math.Max(0, mapQuery.GetRoute(path.RouteIds[i]).BaseCost);
            }

            return new ResourceSet { GoldVoucher = totalGoldVoucherCost };
        }

        public ResourceSet GetCityMoveBaseCost()
        {
            return new ResourceSet { OriginiumShard = 0 };
        }
    }
}
