using System.Collections.Generic;
using System.IO;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;
using YC.Presentation.Maps;
using UnityEngine;

namespace YC.Presentation
{
    public sealed class MapViewPresenter
    {
        private const int InfluenceSlotHighlightTextureSize = 32;
        private const float InfluenceSlotHighlightSideLength = 12f;
        private const float InfluenceSlotHighlightOutlineThickness = 2f;

        private static readonly Vector3 ResourceTokenIconScale = new Vector3(2f, 2f, 1f);
        private static readonly Vector3 MobileCityScale = new Vector3(1.8f, 1.8f, 1f);
        private static readonly Color InfluenceSlotHighlightColor = new Color(1f, 0.82f, 0.2f, 0.95f);
        private static readonly Color OccupiedInfluenceHighlightColor = new Color(1f, 0.88f, 0.24f, 1f);

        private readonly MobileCityInteractionController controller;
        private readonly Transform parent;
        private readonly SpriteRenderer mapRenderer;
        private readonly MapQueryService mapQuery;
        private readonly InfluenceService influenceService;
        private readonly Dictionary<string, LocationView> locationsById = new Dictionary<string, LocationView>();
        private readonly Dictionary<string, MapHotspot> hotspotsById = new Dictionary<string, MapHotspot>();
        private readonly Dictionary<string, List<SpriteRenderer>> influenceSlotRenderers = new Dictionary<string, List<SpriteRenderer>>();
        private readonly Dictionary<string, List<SpriteRenderer>> routeInfluenceSlotRenderers = new Dictionary<string, List<SpriteRenderer>>();
        private readonly Dictionary<string, SpriteRenderer> influenceSlotBorderRenderers = new Dictionary<string, SpriteRenderer>();
        private readonly Dictionary<string, SpriteRenderer> resourceTokenRenderers = new Dictionary<string, SpriteRenderer>();
        private readonly Dictionary<string, Sprite> resourceTokenSprites = new Dictionary<string, Sprite>();
        private readonly Dictionary<int, GameObject> cityObjectsByPlayerId = new Dictionary<int, GameObject>();
        private readonly Dictionary<int, SpriteRenderer> scoreMarkerRenderers = new Dictionary<int, SpriteRenderer>();
        private readonly HashSet<string> highlightedLocationIds = new HashSet<string>();
        private readonly HashSet<string> highlightedInfluenceSlotIds = new HashSet<string>();

        private Sprite emptyInfluenceSlotSprite;
        private Sprite highlightedInfluenceSlotSprite;
        private Sprite occupiedInfluenceSlotSprite;
        private Sprite movableInfluenceBorderSprite;
        private Sprite scoreMarkerSprite;
        private Sprite scoreMarkerBorderSprite;

        public MapViewPresenter(
            MobileCityInteractionController controller,
            Transform parent,
            SpriteRenderer mapRenderer,
            MapQueryService mapQuery,
            InfluenceService influenceService)
        {
            this.controller = controller;
            this.parent = parent;
            this.mapRenderer = mapRenderer;
            this.mapQuery = mapQuery;
            this.influenceService = influenceService;
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

        public void BuildViews()
        {
            BuildLocationViews();
            BuildHotspots();
            BuildResourceTokenViews();
            BuildInfluenceSlotViews();
        }

        public void SetHighlighted(string locationId, Color color)
        {
            highlightedLocationIds.Add(locationId);
            MapHotspot hotspot;
            if (hotspotsById.TryGetValue(locationId, out hotspot))
            {
                hotspot.SetColor(color);
            }
        }

        public void HighlightInfluenceSlot(string slotId)
        {
            highlightedInfluenceSlotIds.Add(slotId);
        }

        public void ClearHighlights()
        {
            highlightedLocationIds.Clear();
            highlightedInfluenceSlotIds.Clear();
            foreach (var pair in hotspotsById)
            {
                pair.Value.SetColor(new Color(0.25f, 0.95f, 0.45f, 0f));
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
            foreach (var pair in cityObjectsByPlayerId)
            {
                pair.Value.SetActive(false);
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

            var cityObject = EnsureCityObject(playerId, state, localPlayerId);
            cityObject.transform.position = ToWorldPosition(view.NormalizedPosition, -0.4f);
            cityObject.SetActive(true);
        }

        public void RefreshResourceTokenDisplay(GameState state)
        {
            foreach (var pair in resourceTokenRenderers)
            {
                pair.Value.sprite = emptyInfluenceSlotSprite;
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

                var sprite = GetResourceTokenSprite(token.ResourceType, token.Amount);
                if (sprite == null)
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
                var locationId = pair.Key;
                var renderers = pair.Value;

                for (var i = 0; i < renderers.Count; i++)
                {
                    var slotId = InfluenceService.GetLocationSlotId(locationId, i);
                    var placement = FindDisplayedInfluence(
                        state,
                        slotId,
                        hasPendingDispatchFirstMove,
                        pendingDispatchFirstSourceSlotId,
                        pendingDispatchFirstTargetSlotId);
                    SetInfluenceSlotBorderVisible(slotId, placement != null && highlightedInfluenceSlotIds.Contains(slotId));
                    RefreshInfluenceSlotRenderer(state, renderers[i], slotId, placement);
                }
            }

            RefreshRouteInfluenceDisplay(
                state,
                hasPendingDispatchFirstMove,
                pendingDispatchFirstSourceSlotId,
                pendingDispatchFirstTargetSlotId);
        }

        public void RefreshScoreTrackDisplay(GameState state)
        {
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

                var normalizedPosition = FourPlayerScoreTrackDisplayDefinition.GetNormalizedPosition(trackScore) +
                                         GetScoreMarkerOffset(markerIndex, markerCountsByScore[trackScore]);
                var renderer = EnsureScoreMarker(player.PlayerId);
                renderer.transform.position = ToWorldPosition(normalizedPosition, -0.62f);
                renderer.color = GetPlayerColor(state, player.PlayerId, 1f);
                renderer.gameObject.SetActive(true);
            }
        }

        private static Vector2 GetScoreMarkerOffset(int markerIndex, int markerCount)
        {
            if (markerCount <= 1)
            {
                return Vector2.zero;
            }

            if (markerCount == 2)
            {
                return new Vector2(markerIndex == 0 ? -0.0055f : 0.0055f, 0f);
            }

            if (markerCount == 3)
            {
                switch (markerIndex)
                {
                    case 0:
                        return new Vector2(-0.0055f, 0.0045f);
                    case 1:
                        return new Vector2(0.0055f, 0.0045f);
                    default:
                        return new Vector2(0f, -0.005f);
                }
            }

            return new Vector2(
                markerIndex % 2 == 0 ? -0.0055f : 0.0055f,
                markerIndex < 2 ? 0.005f : -0.005f);
        }

        public Vector2 ToNormalizedMapPosition(Vector3 worldPosition)
        {
            var bounds = mapRenderer.bounds;
            return new Vector2(
                (worldPosition.x - bounds.min.x) / bounds.size.x,
                (bounds.max.y - worldPosition.y) / bounds.size.y);
        }

        private void BuildHotspots()
        {
            foreach (var pair in locationsById)
            {
                var view = pair.Value;
                var hotspotObject = new GameObject("Hotspot " + pair.Key, typeof(SpriteRenderer), typeof(CircleCollider2D), typeof(MapHotspot));
                hotspotObject.transform.SetParent(parent, false);
                hotspotObject.transform.position = ToWorldPosition(view.NormalizedPosition, -0.2f);

                var renderer = hotspotObject.GetComponent<SpriteRenderer>();
                renderer.sprite = UguiUtility.CreateCircleSprite(96, 40f, 8f);
                renderer.color = new Color(0.25f, 0.95f, 0.45f, 0f);
                renderer.sortingOrder = 10;

                var collider = hotspotObject.GetComponent<CircleCollider2D>();
                collider.radius = 0.45f;

                var hotspot = hotspotObject.GetComponent<MapHotspot>();
                hotspot.Initialize(controller, pair.Key);
                hotspotsById[pair.Key] = hotspot;
            }
        }

        private void BuildResourceTokenViews()
        {
            EnsureInfluenceSlotSprites();

            var definitions = FourPlayerResourcePointDisplayDefinitions.Create();
            var errors = MapResourcePointDisplayDefinitionValidator.Validate(mapQuery.Map, definitions);
            if (errors.Count > 0)
            {
                Debug.LogError("Resource point display configuration is invalid:\n" + string.Join("\n", errors), controller);
                return;
            }

            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                var tokenObject = new GameObject("ResourceToken " + definition.LocationId, typeof(SpriteRenderer));
                tokenObject.transform.SetParent(parent, false);
                tokenObject.transform.position = ToWorldPosition(definition.ResourceTokenPosition, -0.18f);
                tokenObject.transform.localScale = Vector3.one;

                var renderer = tokenObject.GetComponent<SpriteRenderer>();
                renderer.sprite = emptyInfluenceSlotSprite;
                renderer.color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
                renderer.sortingOrder = 18;
                resourceTokenRenderers[definition.LocationId] = renderer;
            }
        }

        private void BuildInfluenceSlotViews()
        {
            EnsureInfluenceSlotSprites();

            var definitions = FourPlayerResourcePointDisplayDefinitions.Create();
            var errors = MapResourcePointDisplayDefinitionValidator.Validate(mapQuery.Map, definitions);
            if (errors.Count > 0)
            {
                Debug.LogError("Resource point influence slot display configuration is invalid:\n" + string.Join("\n", errors), controller);
                return;
            }

            for (var definitionIndex = 0; definitionIndex < definitions.Count; definitionIndex++)
            {
                var definition = definitions[definitionIndex];
                var slotRenderers = new List<SpriteRenderer>(definition.InfluenceSlots.Count);

                for (var i = 0; i < definition.InfluenceSlots.Count; i++)
                {
                    var slotDefinition = definition.InfluenceSlots[i];
                    var slotId = InfluenceService.GetLocationSlotId(definition.LocationId, i);
                    var slotObject = new GameObject("InfluenceSlot " + definition.LocationId + ":" + i, typeof(SpriteRenderer));
                    slotObject.transform.SetParent(parent, false);
                    slotObject.transform.position = ToWorldPosition(slotDefinition.NormalizedPosition, -0.25f);
                    slotObject.transform.localScale = Vector3.one * slotDefinition.Size;

                    var renderer = slotObject.GetComponent<SpriteRenderer>();
                    renderer.sprite = emptyInfluenceSlotSprite;
                    renderer.color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
                    renderer.sortingOrder = 15;

                    var collider = slotObject.AddComponent<CircleCollider2D>();
                    collider.radius = slotDefinition.ColliderRadius;
                    slotObject.AddComponent<InfluenceSlotClickTarget>().Initialize(controller, slotId);
                    CreateInfluenceSlotBorderRenderer(slotObject.transform, slotId, renderer.sortingOrder + 1);

                    slotRenderers.Add(renderer);
                }

                influenceSlotRenderers[definition.LocationId] = slotRenderers;
            }

            BuildRouteInfluenceSlotViews();
        }

        private void BuildRouteInfluenceSlotViews()
        {
            EnsureInfluenceSlotSprites();

            var definitions = FourPlayerRouteDisplayDefinitions.Create();
            var errors = MapRouteDisplayDefinitionValidator.Validate(mapQuery.Map, definitions);
            if (errors.Count > 0)
            {
                Debug.LogError("Route influence slot display configuration is invalid:\n" + string.Join("\n", errors), controller);
                return;
            }

            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                var slotRenderers = new List<SpriteRenderer>();

                for (var slotIndex = 0; slotIndex < definition.InfluenceSlots.Count; slotIndex++)
                {
                    var slotDefinition = definition.InfluenceSlots[slotIndex];
                    var slotId = InfluenceService.GetRouteSlotId(definition.RouteId, slotIndex);
                    var slotObject = new GameObject(
                        "RouteInfluenceSlot " + definition.RouteId + ":" + slotIndex,
                        typeof(SpriteRenderer));
                    slotObject.transform.SetParent(parent, false);
                    slotObject.transform.position = ToWorldPosition(slotDefinition.NormalizedPosition, -0.24f);
                    slotObject.transform.localScale = Vector3.one * slotDefinition.Size;

                    var renderer = slotObject.GetComponent<SpriteRenderer>();
                    renderer.sprite = emptyInfluenceSlotSprite;
                    renderer.color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
                    renderer.sortingOrder = 15;

                    var collider = slotObject.AddComponent<CircleCollider2D>();
                    collider.radius = slotDefinition.ColliderRadius;
                    slotObject.AddComponent<InfluenceSlotClickTarget>().Initialize(controller, slotId);
                    CreateInfluenceSlotBorderRenderer(slotObject.transform, slotId, renderer.sortingOrder + 1);

                    slotRenderers.Add(renderer);
                }

                routeInfluenceSlotRenderers[definition.RouteId] = slotRenderers;
            }
        }

        private void RefreshRouteInfluenceDisplay(
            GameState state,
            bool hasPendingDispatchFirstMove,
            string pendingDispatchFirstSourceSlotId,
            string pendingDispatchFirstTargetSlotId)
        {
            foreach (var pair in routeInfluenceSlotRenderers)
            {
                var routeId = pair.Key;
                var renderers = pair.Value;

                for (var i = 0; i < renderers.Count; i++)
                {
                    var slotId = InfluenceService.GetRouteSlotId(routeId, i);
                    var placement = FindDisplayedInfluence(
                        state,
                        slotId,
                        hasPendingDispatchFirstMove,
                        pendingDispatchFirstSourceSlotId,
                        pendingDispatchFirstTargetSlotId);
                    SetInfluenceSlotBorderVisible(slotId, placement != null && highlightedInfluenceSlotIds.Contains(slotId));
                    RefreshInfluenceSlotRenderer(state, renderers[i], slotId, placement);
                }
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
                renderer.sprite = occupiedInfluenceSlotSprite;
                renderer.color = GetPlayerColor(state, placement.PlayerId, 1f);
            }
            else if (highlightedInfluenceSlotIds.Contains(slotId))
            {
                renderer.sprite = highlightedInfluenceSlotSprite;
                renderer.color = InfluenceSlotHighlightColor;
            }
            else
            {
                renderer.sprite = emptyInfluenceSlotSprite;
                renderer.color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
            }
        }

        private void CreateInfluenceSlotBorderRenderer(Transform parent, string slotId, int sortingOrder)
        {
            var borderObject = new GameObject("MovableInfluenceBorder", typeof(SpriteRenderer));
            borderObject.transform.SetParent(parent, false);
            borderObject.transform.localPosition = Vector3.zero;
            borderObject.transform.localScale = Vector3.one;

            var renderer = borderObject.GetComponent<SpriteRenderer>();
            renderer.sprite = movableInfluenceBorderSprite;
            renderer.color = OccupiedInfluenceHighlightColor;
            renderer.sortingOrder = sortingOrder;
            renderer.enabled = false;
            influenceSlotBorderRenderers[slotId] = renderer;
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
            SpriteRenderer renderer;
            if (influenceSlotBorderRenderers.TryGetValue(slotId, out renderer) && renderer != null)
            {
                renderer.enabled = visible;
            }
        }

        private void EnsureInfluenceSlotSprites()
        {
            if (emptyInfluenceSlotSprite == null)
            {
                emptyInfluenceSlotSprite = UguiUtility.CreateCircleSprite(24, 8f, 2f);
            }

            if (occupiedInfluenceSlotSprite == null)
            {
                occupiedInfluenceSlotSprite = UguiUtility.CreateFilledSquareSprite(24, 16f);
            }

            if (highlightedInfluenceSlotSprite == null)
            {
                highlightedInfluenceSlotSprite = UguiUtility.CreateSquareOutlineSprite(
                    InfluenceSlotHighlightTextureSize,
                    InfluenceSlotHighlightSideLength,
                    InfluenceSlotHighlightOutlineThickness);
            }

            if (movableInfluenceBorderSprite == null)
            {
                movableInfluenceBorderSprite = highlightedInfluenceSlotSprite;
            }
        }

        private SpriteRenderer EnsureScoreMarker(int playerId)
        {
            SpriteRenderer renderer;
            if (scoreMarkerRenderers.TryGetValue(playerId, out renderer) && renderer != null)
            {
                return renderer;
            }

            if (scoreMarkerSprite == null)
            {
                scoreMarkerSprite = UguiUtility.CreateFilledSquareSprite(32, 24f);
            }

            if (scoreMarkerBorderSprite == null)
            {
                scoreMarkerBorderSprite = UguiUtility.CreateSquareOutlineSprite(32, 29f, 4f);
            }

            var markerObject = new GameObject("ScoreMarker P" + playerId, typeof(SpriteRenderer));
            markerObject.transform.SetParent(parent, false);
            markerObject.transform.localScale = Vector3.one * 0.42f;

            renderer = markerObject.GetComponent<SpriteRenderer>();
            renderer.sprite = scoreMarkerSprite;
            renderer.sortingOrder = 31;

            var borderObject = new GameObject("ScoreMarker Border", typeof(SpriteRenderer));
            borderObject.transform.SetParent(markerObject.transform, false);
            var borderRenderer = borderObject.GetComponent<SpriteRenderer>();
            borderRenderer.sprite = scoreMarkerBorderSprite;
            borderRenderer.color = new Color(0.04f, 0.025f, 0.015f, 0.95f);
            borderRenderer.sortingOrder = 30;

            scoreMarkerRenderers[playerId] = renderer;
            return renderer;
        }

        private GameObject EnsureCityObject(int playerId, GameState state, int localPlayerId)
        {
            GameObject cityObject;
            if (cityObjectsByPlayerId.TryGetValue(playerId, out cityObject))
            {
                RefreshCityObjectAppearance(cityObject, playerId, state, localPlayerId);
                return cityObject;
            }

            cityObject = new GameObject("Mobile City P" + playerId, typeof(SpriteRenderer), typeof(BoxCollider2D), typeof(MobileCityClickTarget));
            cityObject.transform.SetParent(parent, false);
            cityObject.transform.localScale = MobileCityScale;
            cityObject.SetActive(false);

            var cityRenderer = cityObject.GetComponent<SpriteRenderer>();
            cityRenderer.sprite = CreateCitySprite();
            cityRenderer.color = GetPlayerColor(state, playerId, 1f);
            cityRenderer.sortingOrder = 20 + playerId;

            var cityCollider = cityObject.GetComponent<BoxCollider2D>();
            cityCollider.size = new Vector2(0.95f, 1.35f);

            cityObject.GetComponent<MobileCityClickTarget>().Initialize(controller);
            cityObjectsByPlayerId[playerId] = cityObject;
            RefreshCityObjectAppearance(cityObject, playerId, state, localPlayerId);
            return cityObject;
        }

        private void RefreshCityObjectAppearance(GameObject cityObject, int playerId, GameState state, int localPlayerId)
        {
            var cityRenderer = cityObject.GetComponent<SpriteRenderer>();
            if (cityRenderer != null)
            {
                cityRenderer.color = GetPlayerColor(state, playerId, 1f);
            }

            var cityCollider = cityObject.GetComponent<BoxCollider2D>();
            if (cityCollider != null)
            {
                cityCollider.enabled = playerId == localPlayerId;
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
            var bounds = mapRenderer.bounds;
            return new Vector3(
                bounds.min.x + bounds.size.x * normalizedPosition.x,
                bounds.max.y - bounds.size.y * normalizedPosition.y,
                z);
        }

        private Sprite GetResourceTokenSprite(ResourceType resourceType, int amount)
        {
            var key = GetResourceTokenSpriteKey(resourceType, amount);
            Sprite sprite;
            if (resourceTokenSprites.TryGetValue(key, out sprite))
            {
                return sprite;
            }

            sprite = LoadResourceTokenSprite(resourceType, amount);
            if (sprite != null)
            {
                resourceTokenSprites[key] = sprite;
            }

            return sprite;
        }

        private static Sprite LoadResourceTokenSprite(ResourceType resourceType, int amount)
        {
            var fileName = ResourceTokenIconDefinitions.GetFileName(resourceType, amount);
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            var path = ResolveResourceTokenPath(fileName);
            if (!File.Exists(path))
            {
                Debug.LogWarning("Resource token sprite not found: " + path);
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            if (!texture.LoadImage(File.ReadAllBytes(path)))
            {
                return null;
            }

            return Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                256f);
        }

        private static string GetResourceTokenSpriteKey(ResourceType resourceType, int amount)
        {
            return resourceType.ToString() + ":" + amount;
        }

        private static string ResolveResourceTokenPath(string fileName)
        {
            var streamingPath = Path.Combine(UnityEngine.Application.streamingAssetsPath, ResourceTokenIconDefinitions.DirectoryName, fileName);
            if (File.Exists(streamingPath))
            {
                return streamingPath;
            }

            var projectRoot = Directory.GetParent(UnityEngine.Application.dataPath);
            if (projectRoot == null)
            {
                return streamingPath;
            }

            var importedAssetPath = Path.Combine(
                projectRoot.FullName,
                "Assets",
                "YC",
                "Data",
                ResourceTokenIconDefinitions.DirectoryName,
                fileName);
            if (File.Exists(importedAssetPath))
            {
                return importedAssetPath;
            }

            return Path.Combine(projectRoot.FullName, "游城拓荒", "素材", fileName);
        }

        private void BuildLocationViews()
        {
            AddLocation("A-01", 0.838f, 0.248f);
            AddLocation("A-02", 0.831f, 0.449f);
            AddLocation("A-03", 0.689f, 0.477f);
            AddLocation("B-01", 0.904f, 0.643f);
            AddLocation("B-02", 0.799f, 0.746f);
            AddLocation("B-03", 0.655f, 0.682f);
            AddLocation("C-01", 0.801f, 0.893f);
            AddLocation("C-02", 0.444f, 0.829f);
            AddLocation("C-03", 0.134f, 0.780f);
            AddLocation("D-01", 0.692f, 0.347f);
            AddLocation("D-02", 0.549f, 0.296f);
            AddLocation("D-03", 0.564f, 0.489f);
            AddLocation("E-01", 0.553f, 0.751f);
            AddLocation("E-02", 0.335f, 0.684f);
            AddLocation("E-03", 0.190f, 0.587f);
            AddLocation("F-01", 0.329f, 0.349f);
            AddLocation("F-02", 0.387f, 0.523f);
            AddLocation("F-03", 0.176f, 0.400f);
            AddLocation("G-01", 0.734f, 0.145f);
            AddLocation("G-02", 0.482f, 0.162f);
            AddLocation("G-03", 0.139f, 0.115f);
            AddLocation("G-04", 0.310f, 0.232f);
        }

        private void AddLocation(string locationId, float x, float y)
        {
            locationsById[locationId] = new LocationView(new Vector2(x, y));
        }

        private static Sprite CreateCitySprite()
        {
            const int width = 80;
            const int height = 132;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Point;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var border = x < 5 || x >= width - 5 || y < 5 || y >= height - 5;
                    var stripe = x > 14 && x < 18 || x > 37 && x < 41 || x > 60 && x < 64;
                    // Keep the source texture neutral so SpriteRenderer.color preserves the
                    // exact player hue used by city-style influence markers. A blue source
                    // texture made yellow and green city tints converge into the same dark green.
                    var color = border
                        ? Color.white
                        : stripe
                            ? new Color(0.82f, 0.82f, 0.82f, 1f)
                            : new Color(0.58f, 0.58f, 0.58f, 1f);
                    texture.SetPixel(x, y, color);
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), 100f);
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
