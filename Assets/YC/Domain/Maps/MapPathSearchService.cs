using System;
using System.Collections.Generic;

namespace YC.Domain.Maps
{
    public sealed class MapPathSearchService
    {
        private readonly IMapQueryService mapQuery;

        public MapPathSearchService(IMapQueryService mapQuery)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
        }

        public MapPath FindShortestPath(string fromLocationId, string toLocationId)
        {
            mapQuery.GetLocation(fromLocationId);
            mapQuery.GetLocation(toLocationId);

            if (fromLocationId == toLocationId)
            {
                return new MapPath { LocationIds = new List<string> { fromLocationId } };
            }

            var visited = new HashSet<string> { fromLocationId };
            var queue = new Queue<MapPath>();
            queue.Enqueue(new MapPath { LocationIds = new List<string> { fromLocationId } });

            while (queue.Count > 0)
            {
                var currentPath = queue.Dequeue();
                var currentLocationId = currentPath.LocationIds[currentPath.LocationIds.Count - 1];
                var adjacentLocations = mapQuery.GetAdjacentLocations(currentLocationId);

                for (var i = 0; i < adjacentLocations.Count; i++)
                {
                    var nextLocationId = adjacentLocations[i].LocationId;
                    if (visited.Contains(nextLocationId))
                    {
                        continue;
                    }

                    var route = mapQuery.FindRoute(currentLocationId, nextLocationId);
                    var nextPath = ClonePath(currentPath);
                    nextPath.LocationIds.Add(nextLocationId);
                    nextPath.RouteIds.Add(route.RouteId);

                    if (nextLocationId == toLocationId)
                    {
                        return nextPath;
                    }

                    visited.Add(nextLocationId);
                    queue.Enqueue(nextPath);
                }
            }

            throw new ArgumentException("No path exists between the supplied locations.");
        }

        public IReadOnlyList<MapPath> EnumerateSimplePaths(string fromLocationId, string toLocationId, int maxSteps)
        {
            if (maxSteps < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSteps), "Max steps cannot be negative.");
            }

            mapQuery.GetLocation(fromLocationId);
            mapQuery.GetLocation(toLocationId);

            var results = new List<MapPath>();
            var initialPath = new MapPath { LocationIds = new List<string> { fromLocationId } };
            Explore(initialPath, toLocationId, maxSteps, results);
            return results.AsReadOnly();
        }

        public IReadOnlyDictionary<string, MapPath> FindReachablePaths(
            string fromLocationId,
            Func<MapRouteDefinition, bool> canTraverseRoute)
        {
            mapQuery.GetLocation(fromLocationId);
            if (canTraverseRoute == null)
            {
                throw new ArgumentNullException(nameof(canTraverseRoute));
            }

            var paths = new Dictionary<string, MapPath>(StringComparer.Ordinal);
            var queue = new Queue<MapPath>();
            var startPath = new MapPath { LocationIds = new List<string> { fromLocationId } };
            paths[fromLocationId] = startPath;
            queue.Enqueue(startPath);

            while (queue.Count > 0)
            {
                var currentPath = queue.Dequeue();
                var currentLocationId = currentPath.LocationIds[currentPath.LocationIds.Count - 1];
                for (var routeIndex = 0; routeIndex < mapQuery.Map.Routes.Count; routeIndex++)
                {
                    var route = mapQuery.Map.Routes[routeIndex];
                    if (!canTraverseRoute(route) || !RouteCoversLocation(route, currentLocationId))
                    {
                        continue;
                    }

                    var coveredLocationIds = GetCoveredLocationIds(route);
                    for (var locationIndex = 0; locationIndex < coveredLocationIds.Count; locationIndex++)
                    {
                        var nextLocationId = coveredLocationIds[locationIndex];
                        if (paths.ContainsKey(nextLocationId))
                        {
                            continue;
                        }

                        var nextPath = ClonePath(currentPath);
                        nextPath.LocationIds.Add(nextLocationId);
                        nextPath.RouteIds.Add(route.RouteId);
                        paths[nextLocationId] = nextPath;
                        queue.Enqueue(nextPath);
                    }
                }
            }

            return paths;
        }

        private void Explore(MapPath currentPath, string targetLocationId, int maxSteps, List<MapPath> results)
        {
            var currentLocationId = currentPath.LocationIds[currentPath.LocationIds.Count - 1];
            if (currentLocationId == targetLocationId)
            {
                results.Add(ClonePath(currentPath));
                return;
            }

            if (currentPath.StepCount >= maxSteps)
            {
                return;
            }

            var adjacentLocations = mapQuery.GetAdjacentLocations(currentLocationId);
            for (var i = 0; i < adjacentLocations.Count; i++)
            {
                var nextLocationId = adjacentLocations[i].LocationId;
                if (currentPath.LocationIds.Contains(nextLocationId))
                {
                    continue;
                }

                var route = mapQuery.FindRoute(currentLocationId, nextLocationId);
                currentPath.LocationIds.Add(nextLocationId);
                currentPath.RouteIds.Add(route.RouteId);

                Explore(currentPath, targetLocationId, maxSteps, results);

                currentPath.RouteIds.RemoveAt(currentPath.RouteIds.Count - 1);
                currentPath.LocationIds.RemoveAt(currentPath.LocationIds.Count - 1);
            }
        }

        private static MapPath ClonePath(MapPath source)
        {
            return new MapPath
            {
                LocationIds = new List<string>(source.LocationIds),
                RouteIds = new List<string>(source.RouteIds)
            };
        }

        private static bool RouteCoversLocation(MapRouteDefinition route, string locationId)
        {
            var coveredLocationIds = GetCoveredLocationIds(route);
            for (var i = 0; i < coveredLocationIds.Count; i++)
            {
                if (coveredLocationIds[i] == locationId)
                {
                    return true;
                }
            }

            return false;
        }

        private static IReadOnlyList<string> GetCoveredLocationIds(MapRouteDefinition route)
        {
            return route.CoveredLocationIds != null && route.CoveredLocationIds.Count > 0
                ? route.CoveredLocationIds
                : new List<string> { route.FromLocationId, route.ToLocationId };
        }
    }
}
