using System;
using YC.Domain.State;

namespace YC.Domain.Economy
{
    public enum ResourceSaleFailureKind
    {
        None = 0,
        InvalidAmount = 1,
        InsufficientResource = 2
    }

    public sealed class ResourceSaleRequest
    {
        public ResourceSaleRequest(int originium, int originiumShard, int iron, int pureOriginium)
        {
            Originium = originium;
            OriginiumShard = originiumShard;
            Iron = iron;
            PureOriginium = pureOriginium;
        }

        public int Originium { get; private set; }
        public int OriginiumShard { get; private set; }
        public int Iron { get; private set; }
        public int PureOriginium { get; private set; }
    }

    public sealed class ResourceSaleResult
    {
        private ResourceSaleResult(bool succeeded, ResourceSaleFailureKind failureKind, int revenue)
        {
            Succeeded = succeeded;
            FailureKind = failureKind;
            Revenue = revenue;
        }

        public bool Succeeded { get; private set; }
        public ResourceSaleFailureKind FailureKind { get; private set; }
        public int Revenue { get; private set; }

        public static ResourceSaleResult Success(int revenue)
        {
            return new ResourceSaleResult(true, ResourceSaleFailureKind.None, revenue);
        }

        public static ResourceSaleResult Failure(ResourceSaleFailureKind failureKind)
        {
            return new ResourceSaleResult(false, failureKind, 0);
        }
    }

    public sealed class ResourceSaleService
    {
        public const int OriginiumUnitPrice = 3;
        public const int OriginiumShardUnitPrice = 3;
        public const int IronUnitPrice = 4;
        public const int PureOriginiumUnitPrice = 15;

        public ResourceSaleResult Sell(ResourceSet resources, ResourceSaleRequest request)
        {
            if (resources == null)
            {
                throw new ArgumentNullException(nameof(resources));
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.Originium < 0 ||
                request.OriginiumShard < 0 ||
                request.Iron < 0 ||
                request.PureOriginium < 0)
            {
                return ResourceSaleResult.Failure(ResourceSaleFailureKind.InvalidAmount);
            }

            if (request.Originium > resources.Originium ||
                request.OriginiumShard > resources.OriginiumShard ||
                request.Iron > resources.Iron ||
                request.PureOriginium > resources.PureOriginium)
            {
                return ResourceSaleResult.Failure(ResourceSaleFailureKind.InsufficientResource);
            }

            var revenue = request.Originium * OriginiumUnitPrice +
                          request.OriginiumShard * OriginiumShardUnitPrice +
                          request.Iron * IronUnitPrice +
                          request.PureOriginium * PureOriginiumUnitPrice;

            resources.Originium -= request.Originium;
            resources.OriginiumShard -= request.OriginiumShard;
            resources.Iron -= request.Iron;
            resources.PureOriginium -= request.PureOriginium;
            resources.GoldVoucher += revenue;
            return ResourceSaleResult.Success(revenue);
        }
    }
}
