using System.Collections.Generic;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Scoring;
using YC.Domain.State;
using YC.Presentation.Maps;
using UnityEngine;

namespace YC.Presentation
{
    public sealed class MapViewPresenter
    {
        private static readonly Vector3 ResourceTokenIconScale = Vector3.one;
        private static readonly Vector3 MobileCityScale = new Vector3(1.8f, 1.8f, 1f);
        private readonly MobileCityInteractionController controller;
        private readonly MapView view;
        private readonly MapCoordinateSpace mapCoordinateSpace;
        private readonly MapDisplayLayout mapDisplayLayout;
        private readonly MapVisualSpriteLibrary sprites;
        private readonly MapQueryService mapQuery;
        private readonly InfluenceService influenceService;
        private readonly Dictionary<string, LocationView> locationsById = new Dictionary<string, LocationView>();
        private readonly Dictionary<string, MapHotspot> hotspotsById = new Dictionary<string, MapHotspot>();
        private readonly Dictionary<string, SpriteRenderer> influenceSlotRenderers =
            new Dictionary<string, SpriteRenderer>();
        private readonly Dictionary<string, SpriteRenderer> influenceSlotBorderRenderers = new Dictionary<string, SpriteRenderer>();
        private readonly Dictionary<string, MapHighlightPulse> influenceSlotPulses =
            new Dictionary<string, MapHighlightPulse>();
        private readonly Dictionary<string, MapPlacementFeedback> influenceSlotPlacementFeedbacks =
            new Dictionary<string, MapPlacementFeedback>();
        private readonly Dictionary<string, SpriteRenderer> resourceTokenRenderers = new Dictionary<string, SpriteRenderer>();
        private readonly Dictionary<int, MapCityViewBinding> cityBindingsByPlayerId =
            new Dictionary<int, MapCityViewBinding>();
        private readonly Dictionary<int, SpriteRenderer> scoreMarkerRenderers = new Dictionary<int, SpriteRenderer>();
        private readonly HashSet<string> highlightedLocationIds = new HashSet<string>();
        private readonly HashSet<string> highlightedInfluenceSlotIds = new HashSet<string>();

        public MapViewPresenter(
            MobileCityInteractionController controller,
            MapView view,
            MapQueryService mapQuery,
            InfluenceService influenceService)
        {
            this.controller = controller;
            this.view = view;
            this.mapQuery = mapQuery;
            this.influenceService = influenceService;
            mapCoordinateSpace = view == null ? null : view.CoordinateSpace;
            mapDisplayLayout = mapCoordinateSpace == null ? null : mapCoordinateSpace.Layout;
            sprites = view == null ? null : view.SpriteLibrary;
        }

        public int HighlightedLocationCount
        {
            get { return highlightedLocationIds.Count; }
        }

        public bool ContainsHighlightedLocation(string locationId)
        {
            return highlightedLocationIds.Contains(locationId);
        }

        public bool ContainsHighlightedInfluenceSlot(string slotId)
        {
            return highlightedInfluenceSlotIds.Contains(slotId);
        }

        public bool BuildViews(out string reason)
        {
            if (view == null || mapQuery == null || mapQuery.Map == null)
            {
                reason = "Map presenter requires a fixed MapView and map definition.";
                return false;
            }

            if (!view.Bind(controller, mapQuery.Map, out reason))
            {
                return false;
            }

            BuildLocationViews();
            BindFixedViews();
            reason = string.Empty;
            return true;
        }

        public void SetHighlighted(string locationId, Color color)
        {
            highlightedLocationIds.Add(locationId);
            MapHotspot hotspot;
            if (hotspotsById.TryGetValue(locationId, out hotspot))
            {
                hotspot.SetHighlighted(true);
            }
        }

        public void HighlightInfluenceSlot(string slotId)
        {
            highlightedInfluenceSlotIds.Add(slotId);
            SetInfluenceSlotBorderVisible(slotId, true);
        }

        public void PlayLocationConfirmation(string locationId)
        {
            MapHotspot hotspot;
            if (hotspotsById.TryGetValue(locationId, out hotspot))
            {
                hotspot.PlayPlacementFeedback();
            }
        }

        public void PlayInfluenceSlotConfirmation(string slotId)
        {
            MapPlacementFeedback feedback;
            if (influenceSlotPlacementFeedbacks.TryGetValue(slotId, out feedback))
            {
                feedback.Play();
            }
        }

        public void ClearHighlights()
        {
            highlightedLocationIds.Clear();
            highlightedInfluenceSlotIds.Clear();
            foreach (var pair in hotspotsById)
            {
                pair.Value.SetHighlighted(false);
            }
            foreach (var pair in influenceSlotBorderRenderers)
            {
                SetInfluenceSlotBorderVisible(pair.Key, false);
            }
        }

        public void ApplyDebugHotspotHighlights(bool debugClicks)
        {
            if (!debugClicks)
            {
                return;
            }

            foreach (var pair in hotspotsById)
            {
                if (highlightedLocationIds.Contains(pair.Key))
                {
                    continue;
                }

                pair.Value.SetColor(new Color(1f, 0.78f, 0.18f, 0.42f));
            }
        }

        public void RefreshCityViewsFromState(GameState state, int localPlayerId)
        {
            foreach (var pair in cityBindingsByPlayerId)
            {
                pair.Value.Renderer.gameObject.SetActive(false);
            }

            if (state == null)
            {
                return;
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (string.IsNullOrEmpty(player.CityLocationId))
                {
                    continue;
                }

                MoveCityView(player.PlayerId, player.CityLocationId, state, localPlayerId);
            }
        }

        public void MoveCityView(int playerId, string locationId, GameState state, int localPlayerId)
        {
            LocationView view;
            if (!locationsById.TryGetValue(locationId, out view))
            {
                return;
            }

            MapCityViewBinding cityBinding;
            if (!cityBindingsByPlayerId.TryGetValue(playerId, out cityBinding))
            {
                Debug.LogError("Map view has no city pool entry for player " + playerId + ".", controller);
                return;
            }

            cityBinding.Renderer.transform.position = ToWorldPosition(view.NormalizedPosition, -0.4f);
            cityBinding.Renderer.color = GetPlayerColor(state, playerId, 1f);
            cityBinding.Collider.enabled = playerId == localPlayerId;
            cityBinding.Renderer.gameObject.SetActive(true);
        }

        public void RefreshResourceTokenDisplay(GameState state)
        {
            foreach (var pair in resourceTokenRenderers)
            {
                pair.Value.sprite = sprites.EmptyInfluenceSlot;
                pair.Value.color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
                pair.Value.transform.localScale = Vector3.one;
                pair.Value.gameObject.SetActive(true);
            }

            if (state == null || state.Map == null)
            {
                return;
            }

            for (var i = 0; i < state.Map.ResourceTokens.Count; i++)
            {
                var token = state.Map.ResourceTokens[i];
                SpriteRenderer renderer;
                if (!resourceTokenRenderers.TryGetValue(token.LocationId, out renderer))
                {
                    continue;
                }

                Sprite sprite;
                if (!sprites.TryGetResourceTokenSprite(token.ResourceType, token.Amount, out sprite))
                {
                    renderer.gameObject.SetActive(false);
                    continue;
                }

                renderer.sprite = sprite;
                renderer.color = Color.white;
                renderer.transform.localScale = ResourceTokenIconScale;
            }
        }

        public void RefreshInfluenceDisplay(
            GameState state,
            bool hasPendingDispatchFirstMove,
            string pendingDispatchFirstSourceSlotId,
            string pendingDispatchFirstTargetSlotId)
        {
            if (state == null || influenceService == null)
            {
                return;
            }

            foreach (var pair in influenceSlotRenderers)
            {
                var slotId = pair.Key;
                var placement = FindDisplayedInfluence(
                    state,
                    slotId,
                    hasPendingDispatchFirstMove,
                    pendingDispatchFirstSourceSlotId,
                    pendingDispatchFirstTargetSlotId);
                SetInfluenceSlotBorderVisible(slotId, highlightedInfluenceSlotIds.Contains(slotId));
                RefreshInfluenceSlotRenderer(state, pair.Value, slotId, placement);
            }
        }

        public void RefreshScoreTrackDisplay(GameState state)
        {
            RequireScoreTrackLayout();
            foreach (var pair in scoreMarkerRenderers)
            {
                pair.Value.gameObject.SetActive(false);
            }

            if (state == null)
            {
                return;
            }

            var markerCountsByScore = new Dictionary<int, int>();
            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (player == null || !player.HasScoreTrackMarker)
                {
                    continue;
                }

                var trackScore = ScoreTrackService.ClampToTrack(player.Score);
                int markerCount;
                markerCountsByScore.TryGetValue(trackScore, out markerCount);
                markerCountsByScore[trackScore] = markerCount + 1;
            }

            var markerIndexesByScore = new Dictionary<int, int>();
            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (player == null || !player.HasScoreTrackMarker)
                {
                    continue;
                }

                var trackScore = ScoreTrackService.ClampToTrack(player.Score);
                int markerIndex;
                markerIndexesByScore.TryGetValue(trackScore, out markerIndex);
                markerIndexesByScore[trackScore] = markerIndex + 1;

                var normalizedPosition = mapDisplayLayout.GetScoreTrackNormalizedPosition(trackScore) +
                                         mapDisplayLayout.GetScoreMarkerOffset(
                                             markerIndex,
                                             markerCountsByScore[trackScore]);
                SpriteRenderer renderer;
                if (!scoreMarkerRenderers.TryGetValue(player.PlayerId, out renderer))
                {
                    Debug.LogError("Map view has no score marker pool entry for player " + player.PlayerId + ".", controller);
                    continue;
                }
                renderer.transform.position = ToWorldPosition(normalizedPosition, -0.62f);
                renderer.color = GetPlayerColor(state, player.PlayerId, 1f);
                renderer.gameObject.SetActive(true);
            }
        }

        private void RequireScoreTrackLayout()
        {
            string reason = null;
            if (mapDisplayLayout == null || mapQuery == null || mapQuery.Map == null ||
                !string.Equals(
                    mapDisplayLayout.MapId,
                    mapQuery.Map.MapId,
                    System.StringComparison.Ordinal) ||
                !mapDisplayLayout.TryValidateScoreTrack(out reason))
            {
                throw new System.InvalidOperationException(
                    "MapViewPresenter 缺少与当前地图匹配的有效计分轨迹布局：" +
                    (mapDisplayLayout == null
                        ? "MapDisplayLayout 引用为空。"
                        : reason ?? "MapId 不匹配。"));
            }
        }

        public Vector2 ToNormalizedMapPosition(Vector3 worldPosition)
        {
            return mapCoordinateSpace == null
                ? Vector2.zero
                : mapCoordinateSpace.ToNormalizedPosition(worldPosition);
        }

        private void BindFixedViews()
        {
            hotspotsById.Clear();
            resourceTokenRenderers.Clear();
            influenceSlotRenderers.Clear();
            influenceSlotBorderRenderers.Clear();
            influenceSlotPulses.Clear();
            influenceSlotPlacementFeedbacks.Clear();
            cityBindingsByPlayerId.Clear();
            scoreMarkerRenderers.Clear();

            for (var i = 0; i < view.Locations.Count; i++)
            {
                var binding = view.Locations[i];
                var location = locationsById[binding.LocationId];
                binding.Hotspot.transform.position = ToWorldPosition(location.NormalizedPosition, -0.2f);
                binding.Hotspot.Renderer.sprite = sprites.Hotspot;
                binding.Hotspot.SetColor(new Color(0.25f, 0.95f, 0.45f, 0f));
                hotspotsById.Add(binding.LocationId, binding.Hotspot);
            }

            var resourceDefinitions = mapCoordinateSpace.Layout.CreateResourcePointDefinitions();
            var resourcesById = new Dictionary<string, MapResourcePointDisplayDefinition>();
            for (var i = 0; i < resourceDefinitions.Count; i++)
            {
                resourcesById.Add(resourceDefinitions[i].LocationId, resourceDefinitions[i]);
            }
            for (var i = 0; i < view.ResourceTokens.Count; i++)
            {
                var binding = view.ResourceTokens[i];
                var renderer = binding.Renderer;
                renderer.transform.position = ToWorldPosition(
                    resourcesById[binding.LocationId].ResourceTokenPosition,
                    -0.18f);
                renderer.transform.localScale = Vector3.one;
                renderer.sprite = sprites.EmptyInfluenceSlot;
                renderer.color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
                renderer.gameObject.SetActive(true);
                resourceTokenRenderers.Add(binding.LocationId, renderer);
            }

            var influenceDefinitions = BuildInfluenceDefinitionsBySlotId();
            for (var i = 0; i < view.InfluenceSlots.Count; i++)
            {
                var binding = view.InfluenceSlots[i];
                var presentation = influenceDefinitions[binding.SlotId];
                var definition = presentation.Display;
                binding.Renderer.transform.position = ToWorldPosition(
                    definition.NormalizedPosition,
                    presentation.LocalZ);
                binding.Renderer.transform.localScale = Vector3.one * definition.Size;
                binding.Renderer.sprite = sprites.EmptyInfluenceSlot;
                binding.Renderer.color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
                binding.Collider.radius = definition.ColliderRadius;
                binding.BorderRenderer.sprite = sprites.MovableInfluenceBorder;
                binding.BorderRenderer.color = UiTheme.CyanAccent;
                binding.BorderPulse.SetHighlighted(false);
                influenceSlotRenderers.Add(binding.SlotId, binding.Renderer);
                influenceSlotBorderRenderers.Add(binding.SlotId, binding.BorderRenderer);
                influenceSlotPulses.Add(binding.SlotId, binding.BorderPulse);
                influenceSlotPlacementFeedbacks.Add(binding.SlotId, binding.PlacementFeedback);
            }

            for (var i = 0; i < view.CityPool.Count; i++)
            {
                var binding = view.CityPool[i];
                binding.Renderer.sprite = sprites.MobileCity;
                binding.Renderer.transform.localScale = MobileCityScale;
                binding.Renderer.gameObject.SetActive(false);
                cityBindingsByPlayerId.Add(binding.PlayerId, binding);
            }

            for (var i = 0; i < view.ScoreMarkerPool.Count; i++)
            {
                var binding = view.ScoreMarkerPool[i];
                binding.Renderer.sprite = sprites.ScoreMarker;
                binding.BorderRenderer.sprite = sprites.ScoreMarkerBorder;
                binding.Renderer.gameObject.SetActive(false);
                scoreMarkerRenderers.Add(binding.PlayerId, binding.Renderer);
            }
        }

        private Dictionary<string, InfluenceSlotPresentation> BuildInfluenceDefinitionsBySlotId()
        {
            var result = new Dictionary<string, InfluenceSlotPresentation>();
            var resourceDefinitions = mapCoordinateSpace.Layout.CreateResourcePointDefinitions();
            for (var i = 0; i < resourceDefinitions.Count; i++)
            {
                var definition = resourceDefinitions[i];
                for (var slotIndex = 0; slotIndex < definition.InfluenceSlots.Count; slotIndex++)
                {
                    result.Add(
                        InfluenceService.GetLocationSlotId(definition.LocationId, slotIndex),
                        new InfluenceSlotPresentation(definition.InfluenceSlots[slotIndex], -0.25f));
                }
            }

            var routeDefinitions = mapCoordinateSpace.Layout.CreateRouteDefinitions();
            for (var i = 0; i < routeDefinitions.Count; i++)
            {
                var definition = routeDefinitions[i];
                for (var slotIndex = 0; slotIndex < definition.InfluenceSlots.Count; slotIndex++)
                {
                    result.Add(
                        InfluenceService.GetRouteSlotId(definition.RouteId, slotIndex),
                        new InfluenceSlotPresentation(definition.InfluenceSlots[slotIndex], -0.24f));
                }
            }

            return result;
        }

        private sealed class InfluenceSlotPresentation
        {
            public readonly MapInfluenceSlotDisplayDefinition Display;
            public readonly float LocalZ;

            public InfluenceSlotPresentation(MapInfluenceSlotDisplayDefinition display, float localZ)
            {
                Display = display;
                LocalZ = localZ;
            }
        }

        private void RefreshInfluenceSlotRenderer(
            GameState state,
            SpriteRenderer renderer,
            string slotId,
            InfluencePlacement placement)
        {
            if (placement != null)
            {
                renderer.sprite = sprites.OccupiedInfluenceSlot;
                renderer.color = GetPlayerColor(state, placement.PlayerId, 1f);
            }
            else
            {
                renderer.sprite = sprites.EmptyInfluenceSlot;
                renderer.color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
            }
        }

        private InfluencePlacement FindDisplayedInfluence(
            GameState state,
            string slotId,
            bool hasPendingDispatchFirstMove,
            string pendingDispatchFirstSourceSlotId,
            string pendingDispatchFirstTargetSlotId)
        {
            if (hasPendingDispatchFirstMove)
            {
                if (slotId == pendingDispatchFirstSourceSlotId)
                {
                    return null;
                }

                if (slotId == pendingDispatchFirstTargetSlotId)
                {
                    return influenceService.FindInfluence(state, pendingDispatchFirstSourceSlotId);
                }
            }

            return influenceService.FindInfluence(state, slotId);
        }

        private void SetInfluenceSlotBorderVisible(string slotId, bool visible)
        {
            MapHighlightPulse pulse;
            if (influenceSlotPulses.TryGetValue(slotId, out pulse) && pulse != null)
            {
                pulse.SetHighlighted(visible);
            }
        }

        private Color GetPlayerColor(GameState state, int playerId, float alpha)
        {
            var player = state == null ? null : state.FindPlayer(playerId);
            if (player == null)
            {
                return Color.white;
            }

            return UiTheme.GetPlayerColor(player.Color, alpha);
        }

        private Vector3 ToWorldPosition(Vector2 normalizedPosition, float z)
        {
            return mapCoordinateSpace == null
                ? new Vector3(normalizedPosition.x, normalizedPosition.y, z)
                : mapCoordinateSpace.ToWorldPosition(normalizedPosition, z);
        }

        private void BuildLocationViews()
        {
            locationsById.Clear();
            var locations = mapCoordinateSpace.Layout.Locations;
            for (var i = 0; i < locations.Count; i++)
            {
                var location = locations[i];
                if (location != null && !string.IsNullOrEmpty(location.LocationId))
                {
                    AddLocation(location.LocationId, location.NormalizedPosition);
                }
            }
        }

        private void AddLocation(string locationId, Vector2 normalizedPosition)
        {
            locationsById[locationId] = new LocationView(normalizedPosition);
        }

        private sealed class LocationView
        {
            public readonly Vector2 NormalizedPosition;

            public LocationView(Vector2 normalizedPosition)
            {
                NormalizedPosition = normalizedPosition;
            }
        }
    }
}
