using System;
using System.Collections.Generic;
using YC.Domain.Harvest;
using YC.Domain.Maps;

namespace YC.Presentation
{
    public sealed class ResourceCollectionSelectionController
    {
        private readonly HashSet<string> candidateLocationIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> selectedLocationIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> deselectedLocationIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> routeIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> paidRouteIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> paymentRecipients = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, MapPath> pathsByLocationId = new Dictionary<string, MapPath>(StringComparer.Ordinal);

        public IReadOnlyCollection<string> CandidateLocationIds
        {
            get { return candidateLocationIds; }
        }

        public IReadOnlyCollection<string> SelectedLocationIds
        {
            get { return selectedLocationIds; }
        }

        public IReadOnlyCollection<string> RouteIds
        {
            get { return routeIds; }
        }

        public IReadOnlyCollection<string> PaidRouteIds
        {
            get { return paidRouteIds; }
        }

        public IReadOnlyDictionary<string, int> PaymentRecipients
        {
            get { return paymentRecipients; }
        }

        public void Clear()
        {
            candidateLocationIds.Clear();
            selectedLocationIds.Clear();
            deselectedLocationIds.Clear();
            routeIds.Clear();
            paidRouteIds.Clear();
            paymentRecipients.Clear();
            pathsByLocationId.Clear();
        }

        public void AddCandidateLocation(string locationId)
        {
            if (!string.IsNullOrEmpty(locationId))
            {
                candidateLocationIds.Add(locationId);
            }
        }

        public bool HasCandidateLocation(string locationId)
        {
            return candidateLocationIds.Contains(locationId);
        }

        public bool HasDeselectedLocation(string locationId)
        {
            return deselectedLocationIds.Contains(locationId);
        }

        public void SetPathForLocation(string locationId, MapPath path)
        {
            if (!string.IsNullOrEmpty(locationId) && path != null)
            {
                pathsByLocationId[locationId] = path;
            }
        }

        public void ClearRoutesAndPaths()
        {
            routeIds.Clear();
            pathsByLocationId.Clear();
        }

        public void ApplyQuery(ResourceCollectionSelectionQuery query)
        {
            candidateLocationIds.Clear();
            routeIds.Clear();
            pathsByLocationId.Clear();
            if (query == null || !query.IsValid)
            {
                return;
            }

            for (var i = 0; i < query.CandidateLocationIds.Count; i++)
            {
                candidateLocationIds.Add(query.CandidateLocationIds[i]);
            }

            foreach (var pair in query.PathsByLocationId)
            {
                pathsByLocationId[pair.Key] = pair.Value;
            }

            foreach (var pair in query.RouteOptionsById)
            {
                routeIds.Add(pair.Key);
            }
        }

        public void AddRoute(string routeId)
        {
            if (!string.IsNullOrEmpty(routeId))
            {
                routeIds.Add(routeId);
            }
        }

        public bool HasRoute(string routeId)
        {
            return routeIds.Contains(routeId);
        }

        public void ConfirmRoutePayment(string routeId, int receiverPlayerId)
        {
            if (string.IsNullOrEmpty(routeId))
            {
                return;
            }

            paidRouteIds.Add(routeId);
            if (receiverPlayerId > 0)
            {
                paymentRecipients[routeId] = receiverPlayerId;
            }
            else
            {
                paymentRecipients.Remove(routeId);
            }
        }

        public int GetNextPaymentRecipient(string routeId, IReadOnlyList<int> owners)
        {
            if (owners == null || owners.Count <= 0)
            {
                return -1;
            }

            var nextIndex = 0;
            int currentReceiver;
            if (paymentRecipients.TryGetValue(routeId, out currentReceiver))
            {
                var currentIndex = IndexOf(owners, currentReceiver);
                if (currentIndex >= 0)
                {
                    nextIndex = (currentIndex + 1) % owners.Count;
                }
            }

            return owners[nextIndex];
        }

        public bool IsRoutePaidToBank(string routeId)
        {
            return paidRouteIds.Contains(routeId) && !paymentRecipients.ContainsKey(routeId);
        }

        public CollectionToggleResult ToggleLocation(string locationId, Func<string, bool> isLocationAvailable)
        {
            if (!candidateLocationIds.Contains(locationId))
            {
                return CollectionToggleResult.NotCandidate;
            }

            if (isLocationAvailable == null || !isLocationAvailable(locationId))
            {
                return CollectionToggleResult.Unavailable;
            }

            if (selectedLocationIds.Contains(locationId))
            {
                deselectedLocationIds.Add(locationId);
                RefreshSelection(isLocationAvailable);
                return CollectionToggleResult.Removed;
            }

            deselectedLocationIds.Remove(locationId);
            RefreshSelection(isLocationAvailable);
            return CollectionToggleResult.Added;
        }

        public void RefreshSelection(Func<string, bool> isLocationAvailable)
        {
            selectedLocationIds.Clear();
            foreach (var locationId in candidateLocationIds)
            {
                if (deselectedLocationIds.Contains(locationId) ||
                    isLocationAvailable == null ||
                    !isLocationAvailable(locationId))
                {
                    continue;
                }

                selectedLocationIds.Add(locationId);
            }
        }

        public bool TryGetPath(string locationId, out MapPath path)
        {
            return pathsByLocationId.TryGetValue(locationId, out path);
        }

        public List<string> BuildSelectedLocationIds(IReadOnlyList<MapLocationDefinition> orderedLocations)
        {
            var result = new List<string>();
            if (orderedLocations == null)
            {
                return result;
            }

            for (var i = 0; i < orderedLocations.Count; i++)
            {
                var locationId = orderedLocations[i].LocationId;
                if (selectedLocationIds.Contains(locationId))
                {
                    result.Add(locationId);
                }
            }

            return result;
        }

        public HashSet<string> BuildSelectedRouteIds(IReadOnlyList<string> locationIds)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (locationIds == null)
            {
                return result;
            }

            for (var i = 0; i < locationIds.Count; i++)
            {
                MapPath path;
                if (!pathsByLocationId.TryGetValue(locationIds[i], out path))
                {
                    continue;
                }

                for (var routeIndex = 0; routeIndex < path.RouteIds.Count; routeIndex++)
                {
                    result.Add(path.RouteIds[routeIndex]);
                }
            }

            return result;
        }

        public string EncodePaymentRecipients(HashSet<string> selectedRouteIds)
        {
            var encoded = string.Empty;
            if (selectedRouteIds == null)
            {
                return encoded;
            }

            foreach (var pair in paymentRecipients)
            {
                if (!selectedRouteIds.Contains(pair.Key))
                {
                    continue;
                }

                encoded += string.IsNullOrEmpty(encoded)
                    ? pair.Key + "=" + pair.Value
                    : ";" + pair.Key + "=" + pair.Value;
            }

            return encoded;
        }

        public string BuildStatus(Func<string, bool> isRoutePayable)
        {
            RefreshSelection(locationId => IsLocationAvailableByPath(locationId, isRoutePayable));

            if (candidateLocationIds.Count <= 0)
            {
                return "采集阶段：没有可采集资源点，点击结束本回合跳过采集";
            }

            var unpaidRouteCount = 0;
            foreach (var routeId in routeIds)
            {
                if (isRoutePayable != null && isRoutePayable(routeId) && !paidRouteIds.Contains(routeId))
                {
                    unpaidRouteCount += 1;
                }
            }

            if (selectedLocationIds.Count <= 0 && unpaidRouteCount > 0)
            {
                return "采集阶段：点击高亮航道支付路费，支付后会高亮可采集资源点";
            }

            return "采集阶段：已选择 " + selectedLocationIds.Count + " 个资源点。点击航道支付或切换接收方，结束本回合后结算";
        }

        public bool IsRouteSatisfied(string routeId, Func<string, bool> hasLocalRouteInfluence)
        {
            return (hasLocalRouteInfluence != null && hasLocalRouteInfluence(routeId)) || paidRouteIds.Contains(routeId);
        }

        private bool IsLocationAvailableByPath(string locationId, Func<string, bool> isRoutePayable)
        {
            MapPath path;
            if (!pathsByLocationId.TryGetValue(locationId, out path))
            {
                return false;
            }

            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                var routeId = path.RouteIds[i];
                if (isRoutePayable != null && isRoutePayable(routeId) && !paidRouteIds.Contains(routeId))
                {
                    return false;
                }
            }

            return true;
        }

        private static int IndexOf(IReadOnlyList<int> values, int target)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] == target)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    public enum CollectionToggleResult
    {
        NotCandidate,
        Unavailable,
        Removed,
        Added
    }
}
