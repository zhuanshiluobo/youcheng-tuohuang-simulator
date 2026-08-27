using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Presentation.Maps;

namespace YC.Presentation
{
    [DisallowMultipleComponent]
    public sealed class MapView : MonoBehaviour
    {
        [SerializeField] private MapCoordinateSpace coordinateSpace;
        [SerializeField] private MapVisualSpriteLibrary spriteLibrary;
        [SerializeField] private List<MapLocationViewBinding> locations = new List<MapLocationViewBinding>();
        [SerializeField] private List<MapResourceTokenViewBinding> resourceTokens =
            new List<MapResourceTokenViewBinding>();
        [SerializeField] private List<MapInfluenceSlotViewBinding> influenceSlots =
            new List<MapInfluenceSlotViewBinding>();
        [SerializeField] private List<MapCityViewBinding> cityPool = new List<MapCityViewBinding>();
        [SerializeField] private List<MapScoreMarkerViewBinding> scoreMarkerPool =
            new List<MapScoreMarkerViewBinding>();

        public MapCoordinateSpace CoordinateSpace => coordinateSpace;
        public MapVisualSpriteLibrary SpriteLibrary => spriteLibrary;
        public SpriteRenderer MapRenderer => coordinateSpace == null ? null : coordinateSpace.MapRenderer;
        public IReadOnlyList<MapLocationViewBinding> Locations => locations;
        public IReadOnlyList<MapResourceTokenViewBinding> ResourceTokens => resourceTokens;
        public IReadOnlyList<MapInfluenceSlotViewBinding> InfluenceSlots => influenceSlots;
        public IReadOnlyList<MapCityViewBinding> CityPool => cityPool;
        public IReadOnlyList<MapScoreMarkerViewBinding> ScoreMarkerPool => scoreMarkerPool;

        public bool Bind(
            MobileCityInteractionController controller,
            GameMapDefinition map,
            out string reason)
        {
            if (!TryValidateConfiguration(map, out reason))
            {
                return false;
            }

            for (var i = 0; i < locations.Count; i++)
            {
                if (!locations[i].Hotspot.Bind(controller, locations[i].LocationId, out reason))
                {
                    return false;
                }
            }

            for (var i = 0; i < influenceSlots.Count; i++)
            {
                var binding = influenceSlots[i];
                if (!binding.ClickTarget.Bind(controller, binding.SlotId, out reason) ||
                    !binding.BorderPulse.BindWithDuration(
                        binding.BorderRenderer,
                        MapHotspot.HighlightPulseDuration,
                        out reason) ||
                    !binding.PlacementFeedback.Bind(out reason))
                {
                    return false;
                }
            }

            for (var i = 0; i < cityPool.Count; i++)
            {
                if (!cityPool[i].ClickTarget.Bind(controller, out reason))
                {
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        public bool TryValidateConfiguration(GameMapDefinition map, out string reason)
        {
            if (map == null)
            {
                reason = "Map view requires a map definition.";
                return false;
            }

            if (coordinateSpace == null)
            {
                reason = "Map view requires a coordinate space.";
                return false;
            }

            if (!coordinateSpace.ValidateBinding(out reason))
            {
                return false;
            }

            if (spriteLibrary == null)
            {
                reason = "Map view requires a sprite library.";
                return false;
            }

            if (!spriteLibrary.TryValidateConfiguration(out reason))
            {
                return false;
            }

            var layoutErrors = MapDisplayLayoutValidator.Validate(map, coordinateSpace.Layout);
            if (layoutErrors.Count > 0)
            {
                reason = "Map display layout is invalid:\n" + string.Join("\n", layoutErrors);
                return false;
            }

            if (!ValidateLocationBindings(map, out reason) ||
                !ValidateResourceTokenBindings(coordinateSpace.Layout, out reason) ||
                !ValidateInfluenceSlotBindings(map, out reason) ||
                !ValidatePlayerPools(map, out reason))
            {
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private bool ValidateLocationBindings(GameMapDefinition map, out string reason)
        {
            var expected = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < map.Locations.Count; i++)
            {
                expected.Add(map.Locations[i].LocationId);
            }

            var actual = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < locations.Count; i++)
            {
                var binding = locations[i];
                if (binding == null || binding.Hotspot == null || string.IsNullOrEmpty(binding.LocationId))
                {
                    reason = "Map view has an invalid location binding at index " + i + ".";
                    return false;
                }

                if (!actual.Add(binding.LocationId))
                {
                    reason = "Map view has duplicate location binding " + binding.LocationId + ".";
                    return false;
                }
            }

            return ValidateExactIds("location", expected, actual, out reason);
        }

        private bool ValidateResourceTokenBindings(MapDisplayLayout layout, out string reason)
        {
            var expected = new HashSet<string>(StringComparer.Ordinal);
            var definitions = layout.CreateResourcePointDefinitions();
            for (var i = 0; i < definitions.Count; i++)
            {
                expected.Add(definitions[i].LocationId);
            }

            var actual = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < resourceTokens.Count; i++)
            {
                var binding = resourceTokens[i];
                if (binding == null || binding.Renderer == null || string.IsNullOrEmpty(binding.LocationId))
                {
                    reason = "Map view has an invalid resource token binding at index " + i + ".";
                    return false;
                }

                if (!actual.Add(binding.LocationId))
                {
                    reason = "Map view has duplicate resource token binding " + binding.LocationId + ".";
                    return false;
                }
            }

            return ValidateExactIds("resource token", expected, actual, out reason);
        }

        private bool ValidateInfluenceSlotBindings(GameMapDefinition map, out string reason)
        {
            var expected = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < map.Locations.Count; i++)
            {
                var location = map.Locations[i];
                for (var slotIndex = 0; slotIndex < location.InfluenceSlotCount; slotIndex++)
                {
                    expected.Add(InfluenceService.GetLocationSlotId(location.LocationId, slotIndex));
                }
            }
            for (var i = 0; i < map.Routes.Count; i++)
            {
                var route = map.Routes[i];
                for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    expected.Add(InfluenceService.GetRouteSlotId(route.RouteId, slotIndex));
                }
            }

            var actual = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < influenceSlots.Count; i++)
            {
                var binding = influenceSlots[i];
                if (binding == null || string.IsNullOrEmpty(binding.SlotId) ||
                    binding.Renderer == null || binding.Collider == null || binding.ClickTarget == null ||
                    binding.BorderRenderer == null || binding.BorderPulse == null ||
                    binding.PlacementFeedback == null || binding.PieceVisual == null)
                {
                    reason = "Map view has an invalid influence slot binding at index " + i + ".";
                    return false;
                }

                if (!binding.PieceVisual.TryValidateConfiguration(out reason))
                {
                    reason = "Map view influence slot " + binding.SlotId + " has an invalid piece visual: " + reason;
                    return false;
                }

                if (!actual.Add(binding.SlotId))
                {
                    reason = "Map view has duplicate influence slot binding " + binding.SlotId + ".";
                    return false;
                }
            }

            return ValidateExactIds("influence slot", expected, actual, out reason);
        }

        private bool ValidatePlayerPools(GameMapDefinition map, out string reason)
        {
            if (cityPool.Count != map.MaxPlayers || scoreMarkerPool.Count != map.MaxPlayers)
            {
                reason = "Map view player pools must each contain exactly " + map.MaxPlayers + " entries.";
                return false;
            }

            var cities = new HashSet<int>();
            var scores = new HashSet<int>();
            for (var i = 0; i < map.MaxPlayers; i++)
            {
                var city = cityPool[i];
                var score = scoreMarkerPool[i];
                if (city == null || city.Renderer == null || city.Collider == null || city.ClickTarget == null ||
                    city.PieceVisual == null ||
                    score == null || score.Renderer == null || score.BorderRenderer == null ||
                    score.PieceVisual == null)
                {
                    reason = "Map view has an invalid player pool binding at index " + i + ".";
                    return false;
                }

                if (!city.PieceVisual.TryValidateConfiguration(out reason))
                {
                    reason = "Map view city P" + city.PlayerId + " has an invalid piece visual: " + reason;
                    return false;
                }

                if (!score.PieceVisual.TryValidateConfiguration(out reason))
                {
                    reason = "Map view score marker P" + score.PlayerId + " has an invalid piece visual: " + reason;
                    return false;
                }

                if (!cities.Add(city.PlayerId) || !scores.Add(score.PlayerId))
                {
                    reason = "Map view player pools contain duplicate player ids.";
                    return false;
                }
            }

            for (var playerId = 1; playerId <= map.MaxPlayers; playerId++)
            {
                if (!cities.Contains(playerId) || !scores.Contains(playerId))
                {
                    reason = "Map view player pools must use each player id from 1 through " + map.MaxPlayers + ".";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        private static bool ValidateExactIds(
            string label,
            HashSet<string> expected,
            HashSet<string> actual,
            out string reason)
        {
            if (expected.SetEquals(actual))
            {
                reason = string.Empty;
                return true;
            }

            expected.ExceptWith(actual);
            reason = "Map view " + label + " bindings are incomplete or contain unknown ids. Missing: " +
                     string.Join(", ", expected) + ".";
            return false;
        }
    }

    [Serializable]
    public sealed class MapLocationViewBinding
    {
        [SerializeField] private string locationId = string.Empty;
        [SerializeField] private MapHotspot hotspot;
        public string LocationId => locationId;
        public MapHotspot Hotspot => hotspot;
    }

    [Serializable]
    public sealed class MapResourceTokenViewBinding
    {
        [SerializeField] private string locationId = string.Empty;
        [SerializeField] private SpriteRenderer renderer;
        public string LocationId => locationId;
        public SpriteRenderer Renderer => renderer;
    }

    [Serializable]
    public sealed class MapInfluenceSlotViewBinding
    {
        [SerializeField] private string slotId = string.Empty;
        [SerializeField] private SpriteRenderer renderer;
        [SerializeField] private CircleCollider2D collider;
        [SerializeField] private InfluenceSlotClickTarget clickTarget;
        [SerializeField] private SpriteRenderer borderRenderer;
        [SerializeField] private MapHighlightPulse borderPulse;
        [SerializeField] private MapPlacementFeedback placementFeedback;
        [SerializeField] private MapPieceVisual pieceVisual;

        public string SlotId => slotId;
        public SpriteRenderer Renderer => renderer;
        public CircleCollider2D Collider => collider;
        public InfluenceSlotClickTarget ClickTarget => clickTarget;
        public SpriteRenderer BorderRenderer => borderRenderer;
        public MapPieceVisual PieceVisual => pieceVisual;
        internal MapHighlightPulse BorderPulse => borderPulse;
        internal MapPlacementFeedback PlacementFeedback => placementFeedback;
    }

    [Serializable]
    public sealed class MapCityViewBinding
    {
        [SerializeField] private int playerId;
        [SerializeField] private SpriteRenderer renderer;
        [SerializeField] private BoxCollider2D collider;
        [SerializeField] private MobileCityClickTarget clickTarget;
        [SerializeField] private MapPieceVisual pieceVisual;
        public int PlayerId => playerId;
        public SpriteRenderer Renderer => renderer;
        public BoxCollider2D Collider => collider;
        public MobileCityClickTarget ClickTarget => clickTarget;
        public MapPieceVisual PieceVisual => pieceVisual;
    }

    [Serializable]
    public sealed class MapScoreMarkerViewBinding
    {
        [SerializeField] private int playerId;
        [SerializeField] private SpriteRenderer renderer;
        [SerializeField] private SpriteRenderer borderRenderer;
        [SerializeField] private MapPieceVisual pieceVisual;
        public int PlayerId => playerId;
        public SpriteRenderer Renderer => renderer;
        public SpriteRenderer BorderRenderer => borderRenderer;
        public MapPieceVisual PieceVisual => pieceVisual;
    }
}
