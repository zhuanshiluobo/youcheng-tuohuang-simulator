using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Domain.Maps;

namespace YC.Presentation.Maps
{
    [Serializable]
    public sealed class MapResourcePointDisplayDefinition
    {
        public string MapId = string.Empty;
        public string LocationId = string.Empty;
        public Vector2 ResourceTokenPosition;
        public List<MapInfluenceSlotDisplayDefinition> InfluenceSlots = new List<MapInfluenceSlotDisplayDefinition>();
    }

    [Serializable]
    public sealed class MapInfluenceSlotDisplayDefinition
    {
        public Vector2 NormalizedPosition;
        public float Size = 1f;
        public float ColliderRadius = 0.22f;
    }

    public static class MapResourcePointDisplayDefinitionValidator
    {
        public static IReadOnlyList<string> Validate(
            GameMapDefinition map,
            IReadOnlyList<MapResourcePointDisplayDefinition> definitions)
        {
            var errors = new List<string>();
            if (map == null)
            {
                errors.Add("Map definition is required.");
                return errors;
            }

            if (definitions == null)
            {
                errors.Add("Resource point display definitions are required.");
                return errors;
            }

            var locationsById = new Dictionary<string, MapLocationDefinition>(StringComparer.Ordinal);
            for (var i = 0; i < map.Locations.Count; i++)
            {
                var location = map.Locations[i];
                if (!string.IsNullOrEmpty(location.LocationId))
                {
                    locationsById[location.LocationId] = location;
                }
            }

            var definitionsByLocationId = new Dictionary<string, MapResourcePointDisplayDefinition>(StringComparer.Ordinal);
            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                if (definition == null)
                {
                    errors.Add("Resource point display definition at index " + i + " is null.");
                    continue;
                }

                if (!string.Equals(definition.MapId, map.MapId, StringComparison.Ordinal))
                {
                    errors.Add("Resource point " + definition.LocationId + " uses mapId " + definition.MapId + " but expected " + map.MapId + ".");
                }

                if (string.IsNullOrEmpty(definition.LocationId))
                {
                    errors.Add("Resource point display definition at index " + i + " has no locationId.");
                    continue;
                }

                if (definitionsByLocationId.ContainsKey(definition.LocationId))
                {
                    errors.Add("Resource point " + definition.LocationId + " has duplicate display definitions.");
                }
                else
                {
                    definitionsByLocationId.Add(definition.LocationId, definition);
                }

                if (!locationsById.TryGetValue(definition.LocationId, out var location))
                {
                    errors.Add("Resource point " + definition.LocationId + " does not exist in map " + map.MapId + ".");
                    continue;
                }

                ValidatePoint(definition.LocationId, "resource token", definition.ResourceTokenPosition, errors);
                ValidateInfluenceSlots(definition.LocationId, location.InfluenceSlotCount, definition.InfluenceSlots, errors);
            }

            for (var i = 0; i < map.Locations.Count; i++)
            {
                var locationId = map.Locations[i].LocationId;
                if (!definitionsByLocationId.ContainsKey(locationId))
                {
                    errors.Add("Resource point " + locationId + " is missing a display definition.");
                }
            }

            return errors;
        }

        private static void ValidateInfluenceSlots(
            string locationId,
            int expectedCount,
            IReadOnlyList<MapInfluenceSlotDisplayDefinition> slots,
            List<string> errors)
        {
            if (slots == null)
            {
                errors.Add("Resource point " + locationId + " must define influence slots.");
                return;
            }

            if (slots.Count != expectedCount)
            {
                errors.Add(
                    "Resource point " + locationId + " has " + slots.Count +
                    " display slots but rules require " + expectedCount + ".");
            }

            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot == null)
                {
                    errors.Add("Resource point " + locationId + " influence slot " + i + " is null.");
                    continue;
                }

                ValidatePoint(locationId, "influence slot " + i, slot.NormalizedPosition, errors);

                if (slot.Size <= 0f)
                {
                    errors.Add("Resource point " + locationId + " influence slot " + i + " must have a positive size.");
                }

                if (slot.ColliderRadius <= 0f)
                {
                    errors.Add("Resource point " + locationId + " influence slot " + i + " must have a positive collider radius.");
                }
            }
        }

        private static void ValidatePoint(
            string locationId,
            string label,
            Vector2 point,
            List<string> errors)
        {
            if (point.x < 0f || point.x > 1f || point.y < 0f || point.y > 1f)
            {
                errors.Add("Resource point " + locationId + " " + label + " is outside normalized map bounds.");
            }
        }
    }
}
