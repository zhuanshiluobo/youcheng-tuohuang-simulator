using System;
using System.Collections.Generic;
using YC.Domain.Maps;
using UnityEngine;

namespace YC.Presentation.Maps
{
    public static class MapRouteDisplayDefinitionValidator
    {
        public static IReadOnlyList<string> Validate(
            GameMapDefinition map,
            IReadOnlyList<MapRouteDisplayDefinition> definitions)
        {
            var errors = new List<string>();
            if (map == null)
            {
                errors.Add("Map definition is required.");
                return errors;
            }

            if (definitions == null)
            {
                errors.Add("Route display definitions are required.");
                return errors;
            }

            var routesById = new Dictionary<string, MapRouteDefinition>(StringComparer.Ordinal);
            for (var i = 0; i < map.Routes.Count; i++)
            {
                var route = map.Routes[i];
                if (!string.IsNullOrEmpty(route.RouteId))
                {
                    routesById[route.RouteId] = route;
                }
            }

            var definitionsByRouteId = new Dictionary<string, MapRouteDisplayDefinition>(StringComparer.Ordinal);
            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                if (definition == null)
                {
                    errors.Add("Route display definition at index " + i + " is null.");
                    continue;
                }

                if (!string.Equals(definition.MapId, map.MapId, StringComparison.Ordinal))
                {
                    errors.Add("Route " + definition.RouteId + " uses mapId " + definition.MapId + " but expected " + map.MapId + ".");
                }

                if (string.IsNullOrEmpty(definition.RouteId))
                {
                    errors.Add("Route display definition at index " + i + " has no routeId.");
                    continue;
                }

                if (definitionsByRouteId.ContainsKey(definition.RouteId))
                {
                    errors.Add("Route " + definition.RouteId + " has duplicate display definitions.");
                }
                else
                {
                    definitionsByRouteId.Add(definition.RouteId, definition);
                }

                if (!routesById.TryGetValue(definition.RouteId, out var route))
                {
                    errors.Add("Route " + definition.RouteId + " does not exist in map " + map.MapId + ".");
                    continue;
                }

                if (definition.NormalizedPoints == null || definition.NormalizedPoints.Count < 2)
                {
                    errors.Add("Route " + definition.RouteId + " must define at least two normalized points.");
                }

                if (definition.InfluenceSlotPositions == null)
                {
                    errors.Add("Route " + definition.RouteId + " must define influence slot positions.");
                }
                else if (definition.InfluenceSlotPositions.Count != route.InfluenceSlotCount)
                {
                    errors.Add(
                        "Route " + definition.RouteId + " has " + definition.InfluenceSlotPositions.Count +
                        " display slots but rules require " + route.InfluenceSlotCount + ".");
                }

                ValidatePoints(definition.RouteId, "route point", definition.NormalizedPoints, errors);
                ValidatePoints(definition.RouteId, "influence slot", definition.InfluenceSlotPositions, errors);
            }

            for (var i = 0; i < map.Routes.Count; i++)
            {
                var routeId = map.Routes[i].RouteId;
                if (!definitionsByRouteId.ContainsKey(routeId))
                {
                    errors.Add("Route " + routeId + " is missing a display definition.");
                }
            }

            return errors;
        }

        private static void ValidatePoints(
            string routeId,
            string label,
            IReadOnlyList<Vector2> points,
            List<string> errors)
        {
            if (points == null)
            {
                return;
            }

            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                if (point.x < 0f || point.x > 1f || point.y < 0f || point.y > 1f)
                {
                    errors.Add("Route " + routeId + " " + label + " " + i + " is outside normalized map bounds.");
                }
            }
        }
    }
}
