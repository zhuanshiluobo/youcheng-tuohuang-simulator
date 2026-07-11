using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Rules;

namespace YC.Domain.Harvest
{
    public sealed class ResourceCollectionRouteOption
    {
        public string RouteId = string.Empty;
        public string PaymentKey = string.Empty;
        public int Cost;
        public bool CanAfford;
        public List<int> OpponentOwnerPlayerIds = new List<int>();
    }

    public sealed class ResourceCollectionSelectionQuery
    {
        private readonly Dictionary<string, MapPath> pathsByLocationId =
            new Dictionary<string, MapPath>(System.StringComparer.Ordinal);
        private readonly Dictionary<string, ResourceCollectionRouteOption> routeOptionsById =
            new Dictionary<string, ResourceCollectionRouteOption>(System.StringComparer.Ordinal);

        public ValidationResult Validation = ValidationResult.Success;
        public List<string> CandidateLocationIds = new List<string>();
        public int ConfirmedTollCost;
        public int AvailableGoldVoucher;

        public IReadOnlyDictionary<string, MapPath> PathsByLocationId
        {
            get { return pathsByLocationId; }
        }

        public IReadOnlyDictionary<string, ResourceCollectionRouteOption> RouteOptionsById
        {
            get { return routeOptionsById; }
        }

        public bool IsValid
        {
            get { return Validation != null && Validation.IsValid; }
        }

        public bool TryGetPath(string locationId, out MapPath path)
        {
            return pathsByLocationId.TryGetValue(locationId, out path);
        }

        public bool TryGetRouteOption(string routeId, out ResourceCollectionRouteOption option)
        {
            return routeOptionsById.TryGetValue(routeId, out option);
        }

        internal void AddPath(string locationId, MapPath path)
        {
            pathsByLocationId[locationId] = path;
        }

        internal void AddRouteOption(ResourceCollectionRouteOption option)
        {
            routeOptionsById[option.RouteId] = option;
        }

        internal static ResourceCollectionSelectionQuery Failure(ValidationResult validation)
        {
            return new ResourceCollectionSelectionQuery { Validation = validation };
        }
    }
}
