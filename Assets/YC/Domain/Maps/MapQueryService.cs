using System;
using System.Collections.Generic;

namespace YC.Domain.Maps
{
    public sealed class MapQueryService : IMapQueryService
    {
        private readonly Dictionary<string, MapLocationDefinition> locationsById;
        private readonly Dictionary<string, MapRouteDefinition> routesById;
        private readonly Dictionary<string, MapRegionDefinition> regionsById;
        private readonly Dictionary<string, List<MapLocationDefinition>> adjacentLocationsById;

        public MapQueryService(GameMapDefinition map)
        {
            Map = map ?? throw new ArgumentNullException(nameof(map));
            locationsById = new Dictionary<string, MapLocationDefinition>();
            routesById = new Dictionary<string, MapRouteDefinition>();
            regionsById = new Dictionary<string, MapRegionDefinition>();
            adjacentLocationsById = new Dictionary<string, List<MapLocationDefinition>>();

            foreach (var location in Map.Locations)
            {
                if (string.IsNullOrEmpty(location.LocationId))
                {
                    continue;
                }

                locationsById[location.LocationId] = location;
                adjacentLocationsById[location.LocationId] = new List<MapLocationDefinition>();
            }

            foreach (var route in Map.Routes)
            {
                if (!string.IsNullOrEmpty(route.RouteId))
                {
                    routesById[route.RouteId] = route;
                }

                var coveredLocationIds = GetCoveredLocationIds(route);
                if (coveredLocationIds.Count < 2)
                {
                    continue;
                }

                for (var fromIndex = 0; fromIndex < coveredLocationIds.Count; fromIndex++)
                {
                    if (!locationsById.TryGetValue(coveredLocationIds[fromIndex], out var fromLocation))
                    {
                        continue;
                    }

                    for (var toIndex = 0; toIndex < coveredLocationIds.Count; toIndex++)
                    {
                        if (fromIndex == toIndex ||
                            !locationsById.TryGetValue(coveredLocationIds[toIndex], out var toLocation))
                        {
                            continue;
                        }

                        if (!adjacentLocationsById[fromLocation.LocationId].Contains(toLocation))
                        {
                            adjacentLocationsById[fromLocation.LocationId].Add(toLocation);
                        }
                    }
                }
            }

            foreach (var region in Map.Regions)
            {
                if (string.IsNullOrEmpty(region.RegionId))
                {
                    continue;
                }

                regionsById[region.RegionId] = region;
            }
        }

        public GameMapDefinition Map { get; }

        public MapLocationDefinition GetLocation(string locationId)
        {
            if (string.IsNullOrEmpty(locationId) || !locationsById.TryGetValue(locationId, out var location))
            {
                throw new ArgumentException("Unknown map location id.", nameof(locationId));
            }

            return location;
        }

        public MapRouteDefinition GetRoute(string routeId)
        {
            if (string.IsNullOrEmpty(routeId) || !routesById.TryGetValue(routeId, out var route))
            {
                throw new ArgumentException("Unknown map route id.", nameof(routeId));
            }

            return route;
        }

        public MapRouteDefinition FindRoute(string fromLocationId, string toLocationId)
        {
            GetLocation(fromLocationId);
            GetLocation(toLocationId);

            foreach (var route in Map.Routes)
            {
                if (IsRouteBetween(route, fromLocationId, toLocationId))
                {
                    return route;
                }
            }

            throw new ArgumentException("No route exists between the supplied locations.");
        }

        public IReadOnlyList<MapLocationDefinition> GetAdjacentLocations(string locationId)
        {
            GetLocation(locationId);
            return adjacentLocationsById[locationId].AsReadOnly();
        }

        public MapRegionDefinition GetRegionForLocation(string locationId)
        {
            var location = GetLocation(locationId);
            if (!string.IsNullOrEmpty(location.RegionId) && regionsById.TryGetValue(location.RegionId, out var region))
            {
                return region;
            }

            foreach (var candidate in Map.Regions)
            {
                if (candidate.LocationIds.Contains(locationId))
                {
                    return candidate;
                }
            }

            throw new ArgumentException("No region contains the supplied location id.", nameof(locationId));
        }

        public bool CanDockCity(string locationId)
        {
            return GetLocation(locationId).CanDockCity;
        }

        private static bool IsRouteBetween(MapRouteDefinition route, string fromLocationId, string toLocationId)
        {
            var coveredLocationIds = GetCoveredLocationIds(route);
            return ContainsLocationId(coveredLocationIds, fromLocationId) &&
                   ContainsLocationId(coveredLocationIds, toLocationId);
        }

        private static IReadOnlyList<string> GetCoveredLocationIds(MapRouteDefinition route)
        {
            if (route.CoveredLocationIds != null && route.CoveredLocationIds.Count > 0)
            {
                return route.CoveredLocationIds;
            }

            return new List<string> { route.FromLocationId, route.ToLocationId };
        }

        private static bool ContainsLocationId(IReadOnlyList<string> locationIds, string locationId)
        {
            for (var i = 0; i < locationIds.Count; i++)
            {
                if (locationIds[i] == locationId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
