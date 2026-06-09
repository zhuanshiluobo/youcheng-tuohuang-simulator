using System;
using YC.Domain.Maps;

namespace YC.Domain.Influence
{
    public sealed class InfluenceSlotReference
    {
        public const string LocationPrefix = "location:";
        public const string RoutePrefix = "route:";

        private InfluenceSlotReference(InfluenceSlotKind kind, string locationId, string routeId, int slotIndex)
        {
            Kind = kind;
            LocationId = locationId ?? string.Empty;
            RouteId = routeId ?? string.Empty;
            SlotIndex = slotIndex;
            SlotId = kind == InfluenceSlotKind.Location
                ? LocationPrefix + LocationId + ":" + SlotIndex
                : RoutePrefix + RouteId + ":" + SlotIndex;
        }

        public InfluenceSlotKind Kind { get; private set; }
        public string LocationId { get; private set; }
        public string RouteId { get; private set; }
        public int SlotIndex { get; private set; }
        public string SlotId { get; private set; }

        public static InfluenceSlotReference ForLocation(string locationId, int slotIndex)
        {
            if (string.IsNullOrEmpty(locationId))
            {
                throw new ArgumentException("Location id is required.", nameof(locationId));
            }

            if (slotIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(slotIndex), "Slot index cannot be negative.");
            }

            return new InfluenceSlotReference(InfluenceSlotKind.Location, locationId, string.Empty, slotIndex);
        }

        public static InfluenceSlotReference ForRoute(string routeId, int slotIndex)
        {
            if (string.IsNullOrEmpty(routeId))
            {
                throw new ArgumentException("Route id is required.", nameof(routeId));
            }

            if (slotIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(slotIndex), "Slot index cannot be negative.");
            }

            return new InfluenceSlotReference(InfluenceSlotKind.Route, string.Empty, routeId, slotIndex);
        }

        public static bool TryParse(
            IMapQueryService mapQuery,
            string slotId,
            out InfluenceSlotReference slot,
            out string reason)
        {
            if (mapQuery == null)
            {
                throw new ArgumentNullException(nameof(mapQuery));
            }

            slot = null;
            reason = string.Empty;

            if (string.IsNullOrEmpty(slotId))
            {
                reason = "Influence slot id is required.";
                return false;
            }

            if (TryParsePrefixed(mapQuery, slotId, out slot, out reason))
            {
                return true;
            }

            if (TryParseUnprefixed(mapQuery, slotId, out slot, out reason))
            {
                return true;
            }

            if (TryResolveSingleSlot(mapQuery, slotId, out slot))
            {
                return true;
            }

            reason = "Influence slot id does not match a known location or route slot.";
            return false;
        }

        private static bool TryParsePrefixed(
            IMapQueryService mapQuery,
            string slotId,
            out InfluenceSlotReference slot,
            out string reason)
        {
            slot = null;
            reason = string.Empty;

            if (slotId.StartsWith(LocationPrefix, StringComparison.Ordinal))
            {
                return TryBuildLocationSlot(mapQuery, slotId.Substring(LocationPrefix.Length), out slot, out reason);
            }

            if (slotId.StartsWith(RoutePrefix, StringComparison.Ordinal))
            {
                return TryBuildRouteSlot(mapQuery, slotId.Substring(RoutePrefix.Length), out slot, out reason);
            }

            return false;
        }

        private static bool TryParseUnprefixed(
            IMapQueryService mapQuery,
            string slotId,
            out InfluenceSlotReference slot,
            out string reason)
        {
            slot = null;
            reason = string.Empty;

            var separatorIndex = slotId.LastIndexOf(':');
            if (separatorIndex < 0)
            {
                separatorIndex = slotId.LastIndexOf('#');
            }

            if (separatorIndex < 0)
            {
                return false;
            }

            var ownerId = slotId.Substring(0, separatorIndex);
            var indexText = slotId.Substring(separatorIndex + 1);
            int slotIndex;
            if (!int.TryParse(indexText, out slotIndex))
            {
                return false;
            }

            if (TryBuildKnownLocationSlot(mapQuery, ownerId, slotIndex, out slot, out reason))
            {
                return true;
            }

            if (TryBuildKnownRouteSlot(mapQuery, ownerId, slotIndex, out slot, out reason))
            {
                return true;
            }

            return false;
        }

        private static bool TryBuildLocationSlot(
            IMapQueryService mapQuery,
            string unprefixedSlotId,
            out InfluenceSlotReference slot,
            out string reason)
        {
            slot = null;
            reason = string.Empty;

            var separatorIndex = unprefixedSlotId.LastIndexOf(':');
            if (separatorIndex < 0)
            {
                reason = "Location influence slot ids must use location:<locationId>:<index>.";
                return false;
            }

            var locationId = unprefixedSlotId.Substring(0, separatorIndex);
            var indexText = unprefixedSlotId.Substring(separatorIndex + 1);
            int slotIndex;
            if (!int.TryParse(indexText, out slotIndex))
            {
                reason = "Location influence slot index must be an integer.";
                return false;
            }

            return TryBuildKnownLocationSlot(mapQuery, locationId, slotIndex, out slot, out reason);
        }

        private static bool TryBuildRouteSlot(
            IMapQueryService mapQuery,
            string unprefixedSlotId,
            out InfluenceSlotReference slot,
            out string reason)
        {
            slot = null;
            reason = string.Empty;

            var separatorIndex = unprefixedSlotId.LastIndexOf(':');
            if (separatorIndex < 0)
            {
                reason = "Route influence slot ids must use route:<routeId>:<index>.";
                return false;
            }

            var routeId = unprefixedSlotId.Substring(0, separatorIndex);
            var indexText = unprefixedSlotId.Substring(separatorIndex + 1);
            int slotIndex;
            if (!int.TryParse(indexText, out slotIndex))
            {
                reason = "Route influence slot index must be an integer.";
                return false;
            }

            return TryBuildKnownRouteSlot(mapQuery, routeId, slotIndex, out slot, out reason);
        }

        private static bool TryBuildKnownLocationSlot(
            IMapQueryService mapQuery,
            string locationId,
            int slotIndex,
            out InfluenceSlotReference slot,
            out string reason)
        {
            slot = null;
            reason = string.Empty;

            MapLocationDefinition location;
            try
            {
                location = mapQuery.GetLocation(locationId);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (slotIndex < 0 || slotIndex >= location.InfluenceSlotCount)
            {
                reason = "Location influence slot index is outside the defined slot count.";
                return false;
            }

            slot = ForLocation(locationId, slotIndex);
            return true;
        }

        private static bool TryBuildKnownRouteSlot(
            IMapQueryService mapQuery,
            string routeId,
            int slotIndex,
            out InfluenceSlotReference slot,
            out string reason)
        {
            slot = null;
            reason = string.Empty;

            MapRouteDefinition route;
            try
            {
                route = mapQuery.GetRoute(routeId);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (slotIndex < 0 || slotIndex >= route.InfluenceSlotCount)
            {
                reason = "Route influence slot index is outside the defined slot count.";
                return false;
            }

            slot = ForRoute(routeId, slotIndex);
            return true;
        }

        private static bool TryResolveSingleSlot(
            IMapQueryService mapQuery,
            string ownerId,
            out InfluenceSlotReference slot)
        {
            slot = null;

            try
            {
                var location = mapQuery.GetLocation(ownerId);
                if (location.InfluenceSlotCount == 1)
                {
                    slot = ForLocation(ownerId, 0);
                    return true;
                }
            }
            catch (ArgumentException)
            {
            }

            try
            {
                var route = mapQuery.GetRoute(ownerId);
                if (route.InfluenceSlotCount == 1)
                {
                    slot = ForRoute(ownerId, 0);
                    return true;
                }
            }
            catch (ArgumentException)
            {
            }

            return false;
        }
    }
}
