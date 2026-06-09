using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Application.Setup;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class MobileCityInteractionController : MonoBehaviour
    {
        private const int PlayerId = 1;

        private static readonly HashSet<string> InitialLocationIds = new HashSet<string>
        {
            "G-01",
            "A-01",
            "A-02",
            "B-01",
            "B-02",
            "C-01"
        };

        private readonly Dictionary<string, LocationView> locationsById = new Dictionary<string, LocationView>();
        private readonly List<MapHotspot> hotspots = new List<MapHotspot>();
        private readonly HashSet<string> highlightedLocationIds = new HashSet<string>();

        [SerializeField] private SpriteRenderer mapRenderer;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool debugClicks;

        private GameSession session;
        private MapQueryService mapQuery;
        private GameObject cityObject;
        private Text promptText;
        private bool awaitingInitialPlacement = true;
        private bool choosingMoveTarget;
        private int lastDebugCoordinateLogFrame = -1;

        private void Awake()
        {
            if (mapRenderer == null)
            {
                mapRenderer = GetComponent<SpriteRenderer>();
            }

            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }

            BuildLocationViews();
            BuildSession();
            BuildPromptUi();
            BuildHotspots();
            ShowInitialPlacementChoices();
        }

        private void Update()
        {
            if (!IsShiftDebugClick())
            {
                return;
            }

            LogPointerMapCoordinate();
        }

        public void BeginNextRound()
        {
            var state = session.State;
            var player = state.FindPlayer(PlayerId);
            if (player != null)
            {
                player.HasMovedCityThisRound = false;
                player.ActedMainActionThisTurn = false;
            }

            state.Round += 1;
            state.Phase = GamePhase.ActionRound1;
            state.ActionRound = 1;
            state.CurrentPlayerId = state.StartPlayerId;

            choosingMoveTarget = false;
            ClearHighlights();
            SetPrompt("点击移动城市，查看本回合可到达的资源点。");
        }

        public void OnHotspotClicked(string locationId)
        {
            if (IsShiftDebugClick())
            {
                LogPointerMapCoordinate();
                return;
            }

            if (awaitingInitialPlacement)
            {
                TryPlaceInitialCity(locationId);
                return;
            }

            if (choosingMoveTarget && highlightedLocationIds.Contains(locationId))
            {
                TryMoveCity(locationId);
            }
        }

        public void OnMobileCityClicked()
        {
            if (IsShiftDebugClick())
            {
                LogPointerMapCoordinate();
                return;
            }

            if (awaitingInitialPlacement)
            {
                return;
            }

            var player = session.State.FindPlayer(PlayerId);
            if (player == null)
            {
                return;
            }

            if (player.HasMovedCityThisRound)
            {
                SetPrompt("本回合已经移动过城市。结束回合后可以再次移动。");
                return;
            }

            choosingMoveTarget = true;
            ClearHighlights();
            HighlightReachableLocations(player.CityLocationId);
            SetPrompt("选择一个高亮资源点移动城市。");
        }

        private void TryPlaceInitialCity(string locationId)
        {
            var result = session.Submit(new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = PlayerId,
                TargetId = locationId
            });

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            awaitingInitialPlacement = false;
            choosingMoveTarget = false;
            MoveCityView(locationId);
            ClearHighlights();
            SetPrompt("点击移动城市，查看本回合可到达的资源点。");
        }

        private void TryMoveCity(string locationId)
        {
            var result = session.Submit(new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = PlayerId,
                TargetId = locationId
            });

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            choosingMoveTarget = false;
            MoveCityView(locationId);
            ClearHighlights();
            SetPrompt("移动城市已移动。本回合不能再次移动。");
        }

        private void BuildSession()
        {
            mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var state = new GameState
            {
                Phase = GamePhase.Entrance,
                StartPlayerId = PlayerId,
                CurrentPlayerId = PlayerId,
                MapId = mapQuery.Map.MapId,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = PlayerId,
                        Name = "Player 1",
                        Color = PlayerColor.Blue
                    }
                }
            };

            session = new GameSession(state);
            session.RegisterHandler(new SetupCommandHandler(mapQuery));
            var influenceService = new InfluenceService(mapQuery);
            var travelCostService = new TravelCostService(mapQuery);
            var movementService = new CityMovementService(mapQuery, influenceService, travelCostService);
            session.RegisterHandler(new MoveCityCommandHandler(movementService));
        }

        private void BuildHotspots()
        {
            foreach (var pair in locationsById)
            {
                var view = pair.Value;
                var hotspotObject = new GameObject("Hotspot " + pair.Key, typeof(SpriteRenderer), typeof(CircleCollider2D), typeof(MapHotspot));
                hotspotObject.transform.SetParent(transform, false);
                hotspotObject.transform.position = ToWorldPosition(view.NormalizedPosition, -0.2f);

                var renderer = hotspotObject.GetComponent<SpriteRenderer>();
                renderer.sprite = CreateCircleSprite(96, 40f, 8f);
                renderer.color = new Color(0.25f, 0.95f, 0.45f, 0f);
                renderer.sortingOrder = 10;

                var collider = hotspotObject.GetComponent<CircleCollider2D>();
                collider.radius = 0.45f;

                var hotspot = hotspotObject.GetComponent<MapHotspot>();
                hotspot.Initialize(this, pair.Key, renderer);
                hotspots.Add(hotspot);
            }

            ApplyDebugHotspotHighlights();

            cityObject = new GameObject("Mobile City", typeof(SpriteRenderer), typeof(BoxCollider2D), typeof(MobileCityClickTarget));
            cityObject.transform.SetParent(transform, false);
            cityObject.SetActive(false);

            var cityRenderer = cityObject.GetComponent<SpriteRenderer>();
            cityRenderer.sprite = CreateCitySprite();
            cityRenderer.sortingOrder = 20;

            var cityCollider = cityObject.GetComponent<BoxCollider2D>();
            cityCollider.size = new Vector2(0.95f, 1.35f);

            cityObject.GetComponent<MobileCityClickTarget>().Initialize(this);
        }

        private void BuildPromptUi()
        {
            EnsureEventSystem();

            var canvasObject = new GameObject("Mobile City UI Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 15;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var panelObject = new GameObject("Prompt Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panelObject.transform.SetParent(canvasObject.transform, false);

            var panelTransform = panelObject.GetComponent<RectTransform>();
            panelTransform.anchorMin = new Vector2(0.5f, 1f);
            panelTransform.anchorMax = new Vector2(0.5f, 1f);
            panelTransform.pivot = new Vector2(0.5f, 1f);
            panelTransform.sizeDelta = new Vector2(820f, 68f);
            panelTransform.anchoredPosition = new Vector2(0f, -32f);

            panelObject.GetComponent<Image>().color = new Color(0.08f, 0.07f, 0.055f, 0.88f);
            var outline = panelObject.GetComponent<Outline>();
            outline.effectColor = new Color(0.78f, 0.63f, 0.38f, 0.9f);
            outline.effectDistance = new Vector2(3f, -3f);

            var textObject = new GameObject("Prompt Text", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(panelTransform, false);

            var textTransform = textObject.GetComponent<RectTransform>();
            textTransform.anchorMin = Vector2.zero;
            textTransform.anchorMax = Vector2.one;
            textTransform.offsetMin = new Vector2(22f, 0f);
            textTransform.offsetMax = new Vector2(-22f, 0f);

            promptText = textObject.GetComponent<Text>();
            promptText.alignment = TextAnchor.MiddleCenter;
            promptText.color = new Color(0.86f, 0.75f, 0.55f, 1f);
            promptText.fontSize = 30;
            promptText.fontStyle = FontStyle.Bold;
            promptText.font = Font.CreateDynamicFontFromOSFont(new[] { "SimHei", "Microsoft YaHei", "Arial" }, promptText.fontSize);
        }

        private void ShowInitialPlacementChoices()
        {
            ClearHighlights();
            foreach (var locationId in InitialLocationIds)
            {
                SetHighlighted(locationId, new Color(0.25f, 0.95f, 0.45f, 0.82f));
            }

            SetPrompt("选择绿色资源点放置移动城市");
            ApplyDebugHotspotHighlights();
        }

        private void HighlightReachableLocations(string sourceLocationId)
        {
            var adjacentLocations = mapQuery.GetAdjacentLocations(sourceLocationId);
            for (var i = 0; i < adjacentLocations.Count; i++)
            {
                var locationId = adjacentLocations[i].LocationId;
                if (IsOccupiedByAnotherCity(locationId))
                {
                    continue;
                }

                SetHighlighted(locationId, new Color(0.15f, 0.8f, 1f, 0.85f));
            }
        }

        private bool IsOccupiedByAnotherCity(string locationId)
        {
            for (var i = 0; i < session.State.Players.Count; i++)
            {
                var player = session.State.Players[i];
                if (player.PlayerId != PlayerId && player.CityLocationId == locationId)
                {
                    return true;
                }
            }

            return false;
        }

        private void SetHighlighted(string locationId, Color color)
        {
            highlightedLocationIds.Add(locationId);
            for (var i = 0; i < hotspots.Count; i++)
            {
                if (hotspots[i].LocationId == locationId)
                {
                    hotspots[i].SetColor(color);
                    return;
                }
            }
        }

        private void ClearHighlights()
        {
            highlightedLocationIds.Clear();
            for (var i = 0; i < hotspots.Count; i++)
            {
                hotspots[i].SetColor(new Color(0.25f, 0.95f, 0.45f, 0f));
            }

            ApplyDebugHotspotHighlights();
        }

        private void MoveCityView(string locationId)
        {
            if (!locationsById.TryGetValue(locationId, out var view))
            {
                return;
            }

            cityObject.transform.position = ToWorldPosition(view.NormalizedPosition, -0.4f) + new Vector3(0f, 0.38f, 0f);
            cityObject.SetActive(true);
        }

        private Vector3 ToWorldPosition(Vector2 normalizedPosition, float z)
        {
            var bounds = mapRenderer.bounds;
            return new Vector3(
                bounds.min.x + bounds.size.x * normalizedPosition.x,
                bounds.max.y - bounds.size.y * normalizedPosition.y,
                z);
        }

        private bool IsShiftDebugClick()
        {
            return debugClicks
                && Input.GetMouseButtonDown(0)
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
        }

        private void LogPointerMapCoordinate()
        {
            if (lastDebugCoordinateLogFrame == Time.frameCount || targetCamera == null || mapRenderer == null)
            {
                return;
            }

            lastDebugCoordinateLogFrame = Time.frameCount;
            var screenPosition = Input.mousePosition;
            var worldPosition = targetCamera.ScreenToWorldPoint(new Vector3(screenPosition.x, screenPosition.y, -targetCamera.transform.position.z));
            var normalizedPosition = ToNormalizedMapPosition(worldPosition);
            Debug.Log(string.Format(
                "Map click world=({0:F3}, {1:F3}) normalized=({2:F3}, {3:F3})",
                worldPosition.x,
                worldPosition.y,
                normalizedPosition.x,
                normalizedPosition.y));
        }

        private Vector2 ToNormalizedMapPosition(Vector3 worldPosition)
        {
            var bounds = mapRenderer.bounds;
            return new Vector2(
                (worldPosition.x - bounds.min.x) / bounds.size.x,
                (bounds.max.y - worldPosition.y) / bounds.size.y);
        }

        private void ApplyDebugHotspotHighlights()
        {
            if (!debugClicks)
            {
                return;
            }

            for (var i = 0; i < hotspots.Count; i++)
            {
                if (highlightedLocationIds.Contains(hotspots[i].LocationId))
                {
                    continue;
                }

                hotspots[i].SetColor(new Color(1f, 0.78f, 0.18f, 0.42f));
            }
        }

        private void SetPrompt(string message)
        {
            if (promptText != null)
            {
                promptText.text = message;
            }
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

        private static Sprite CreateCircleSprite(int size, float radius, float thickness)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            var center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var distance = Vector2.Distance(new Vector2(x, y), center);
                    var alpha = distance <= radius && distance >= radius - thickness ? 1f : 0f;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
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
                    var color = border
                        ? new Color(0.72f, 0.9f, 1f, 1f)
                        : stripe
                            ? new Color(0.32f, 0.68f, 0.95f, 1f)
                            : new Color(0.12f, 0.42f, 0.72f, 1f);
                    texture.SetPixel(x, y, color);
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        private static void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null)
            {
                return;
            }

            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
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

    public sealed class MapHotspot : MonoBehaviour
    {
        private SpriteRenderer spriteRenderer;
        private MobileCityInteractionController controller;

        public string LocationId { get; private set; }

        public void Initialize(MobileCityInteractionController owner, string locationId, SpriteRenderer renderer)
        {
            controller = owner;
            LocationId = locationId;
            spriteRenderer = renderer;
        }

        public void SetColor(Color color)
        {
            if (spriteRenderer != null)
            {
                spriteRenderer.color = color;
            }
        }

        private void OnMouseDown()
        {
            controller.OnHotspotClicked(LocationId);
        }
    }

    public sealed class MobileCityClickTarget : MonoBehaviour
    {
        private MobileCityInteractionController controller;

        public void Initialize(MobileCityInteractionController owner)
        {
            controller = owner;
        }

        private void OnMouseDown()
        {
            controller.OnMobileCityClicked();
        }
    }
}
