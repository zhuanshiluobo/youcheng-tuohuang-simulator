using System;
using System.Collections.Generic;
using YC.Domain.Maps;

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

                if (definition.InfluenceSlots == null)
                {
                    errors.Add("Route " + definition.RouteId + " must define influence slots.");
                }
                else if (definition.InfluenceSlots.Count != route.InfluenceSlotCount)
                {
                    errors.Add(
                        "Route " + definition.RouteId + " has " + definition.InfluenceSlots.Count +
                        " display slots but rules require " + route.InfluenceSlotCount + ".");
                }

                ValidateInfluenceSlots(definition.RouteId, definition.InfluenceSlots, errors);
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

        private static void ValidateInfluenceSlots(
            string routeId,
            IReadOnlyList<MapInfluenceSlotDisplayDefinition> slots,
            List<string> errors)
        {
            if (slots == null)
            {
                return;
            }

            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot == null)
                {
                    errors.Add("Route " + routeId + " influence slot " + i + " is null.");
                    continue;
                }

                var position = slot.NormalizedPosition;
                if (position.x < 0f || position.x > 1f || position.y < 0f || position.y > 1f)
                {
                    errors.Add("Route " + routeId + " influence slot " + i + " is outside normalized map bounds.");
                }

                if (slot.Size <= 0f)
                {
                    errors.Add("Route " + routeId + " influence slot " + i + " must have a positive size.");
                }

                if (slot.ColliderRadius <= 0f)
                {
                    errors.Add("Route " + routeId + " influence slot " + i + " must have a positive collider radius.");
                }
            }
        }
    }
}
