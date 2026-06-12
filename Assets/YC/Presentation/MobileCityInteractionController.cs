using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Application.Setup;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class MobileCityInteractionController : MonoBehaviour
    {
        private readonly Dictionary<string, LocationView> locationsById = new Dictionary<string, LocationView>();
        private readonly Dictionary<string, MapHotspot> hotspotsById = new Dictionary<string, MapHotspot>();
        private readonly Dictionary<string, List<SpriteRenderer>> influenceSlotRenderers = new Dictionary<string, List<SpriteRenderer>>();
        private readonly HashSet<string> highlightedLocationIds = new HashSet<string>();

        [SerializeField] private SpriteRenderer mapRenderer;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool debugClicks;

        private GameSession session;
        private MapQueryService mapQuery;
        private InfluenceService influenceService;
        private EventDeckService eventDeckService;
        private ExpandableInfoPanel infoPanel;
        private GameObject cityObject;
        private Text promptText;
        private GameObject eventChoiceOverlay;
        private EventCardDefinition pendingEventCard;
        private bool awaitingInitialPlacement = true;
        private bool choosingMoveTarget;
        private int localPlayerId = 1;
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
            BuildInfluenceSlotViews();
            EnsureInfoPanel();
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
            var player = state.FindPlayer(localPlayerId);
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
            RefreshInfoPanel();
            RefreshInfluenceDisplay();
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
                if (!IsLocalPlayersTurn())
                {
                    SetPrompt("等待玩家 " + session.State.CurrentPlayerId + " 完成入场。");
                    return;
                }

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

            if (!IsLocalPlayersTurn())
            {
                SetPrompt("等待玩家 " + session.State.CurrentPlayerId + " 行动。");
                return;
            }

            var player = session.State.FindPlayer(localPlayerId);
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
                PlayerId = localPlayerId,
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
            RefreshResourceDisplay();
            RefreshInfluenceDisplay();
            ClearHighlights();

            if (session.State.PendingChoice != null)
            {
                ShowPendingEventCardOptions();
            }
            else
            {
                UpdateEntranceOrActionPrompt();
            }
        }

        private void TryMoveCity(string locationId)
        {
            var result = session.Submit(new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = localPlayerId,
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
            RefreshResourceDisplay();
            RefreshInfluenceDisplay();

            SetPrompt("移动城市已移动。本回合不能再次移动。");
        }

        private void EnsureInfoPanel()
        {
            infoPanel = FindObjectOfType<ExpandableInfoPanel>();
            if (infoPanel == null)
            {
                var go = new GameObject("ExpandableInfoPanel");
                go.transform.SetParent(transform, false);
                infoPanel = go.AddComponent<ExpandableInfoPanel>();
            }

            infoPanel.Initialize(infoPanel.transform);
            RefreshInfoPanel();
        }

        private void RefreshInfoPanel()
        {
            if (infoPanel == null)
            {
                infoPanel = FindObjectOfType<ExpandableInfoPanel>();
            }

            if (infoPanel == null) return;

            var player = session.State.FindPlayer(localPlayerId);
            if (player == null) return;

            infoPanel.SetRowValue("玩家概览", "玩家", player.Name);
            infoPanel.SetRowValue("玩家概览", "分数", player.Score.ToString());

            var r = player.Resources;
            infoPanel.SetRowValue("资源状态", "源岩", r.Originium.ToString());
            infoPanel.SetRowValue("资源状态", "源石", r.OriginiumShard.ToString());
            infoPanel.SetRowValue("资源状态", "异铁", r.Iron.ToString());
            infoPanel.SetRowValue("资源状态", "至纯源石", r.PureOriginium.ToString());
            infoPanel.SetRowValue("资源状态", "金券", r.GoldVoucher.ToString());

            infoPanel.SetRowValue("城市与行动", "城市位置", player.CityLocationId);
            infoPanel.SetRowValue("城市与行动", "本回合", session.State.Round + " / 8");
            infoPanel.SetRowValue("城市与行动", "行动轮", session.State.ActionRound.ToString());
            infoPanel.SetRowValue("城市与行动", "已执行行动", player.ActedMainActionThisTurn ? "是" : "否");
            infoPanel.SetRowValue("城市与行动", "已移动城市", player.HasMovedCityThisRound ? "是" : "否");
        }

        private void ShowPendingEventCardOptions()
        {
            var pendingChoice = session.State.PendingChoice;
            if (pendingChoice == null) return;

            var card = EventCardDatabase.Get(pendingChoice.CardId);
            if (card == null) return;

            ShowEventCardOptions(card);
        }

        private void ShowEventCardOptions(EventCardDefinition card)
        {
            if (card == null || card.ChoiceRewards.Count == 0) return;
            pendingEventCard = card;

            var canvasTransform = promptText.canvas.GetComponent<RectTransform>();

            eventChoiceOverlay = new GameObject("Event Choice Overlay", typeof(RectTransform), typeof(Image));
            eventChoiceOverlay.transform.SetParent(canvasTransform, false);

            var overlayRect = eventChoiceOverlay.GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;

            eventChoiceOverlay.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);

            var panel = new GameObject("Choice Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panel.transform.SetParent(overlayRect, false);

            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(500f, 120f + card.ChoiceRewards.Count * 52f);
            panelRect.anchoredPosition = Vector2.zero;

            panel.GetComponent<Image>().color = UiTheme.PanelBackground;
            panel.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
            panel.GetComponent<Outline>().effectDistance = new Vector2(3f, -3f);

            var titleObj = new GameObject("Title", typeof(RectTransform), typeof(Text));
            titleObj.transform.SetParent(panelRect, false);
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.sizeDelta = new Vector2(0f, 40f);
            titleRect.anchoredPosition = new Vector2(0f, -20f);

            var titleText = titleObj.GetComponent<Text>();
            titleText.text = "事件牌 · " + card.CardId;
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.color = UiTheme.GoldText;
            titleText.fontSize = 22;
            titleText.fontStyle = FontStyle.Bold;
            titleText.font = FontUtility.GetCjkFont(22);

            for (var i = 0; i < card.ChoiceRewards.Count; i++)
            {
                var capturedIndex = i;
                var desc = card.ChoiceDescriptions[i];

                var btnObj = new GameObject("Choice " + (i + 1), typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
                btnObj.transform.SetParent(panelRect, false);

                var btnRect = btnObj.GetComponent<RectTransform>();
                btnRect.anchorMin = new Vector2(0.05f, 1f);
                btnRect.anchorMax = new Vector2(0.95f, 1f);
                btnRect.sizeDelta = new Vector2(0f, 42f);
                btnRect.anchoredPosition = new Vector2(0f, -55f - i * 50f);

                btnObj.GetComponent<Image>().color = UiTheme.ButtonBackground;
                btnObj.GetComponent<Outline>().effectColor = UiTheme.GoldOutlineThin;
                btnObj.GetComponent<Outline>().effectDistance = new Vector2(1f, -1f);

                var labelObj = new GameObject("Label", typeof(RectTransform), typeof(Text));
                labelObj.transform.SetParent(btnRect, false);
                var labelRect = labelObj.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = new Vector2(12f, 0f);
                labelRect.offsetMax = new Vector2(-12f, 0f);

                var labelText = labelObj.GetComponent<Text>();
                labelText.text = (i + 1) + ". " + desc;
                labelText.alignment = TextAnchor.MiddleLeft;
                labelText.color = UiTheme.GoldText;
                labelText.fontSize = 16;
                labelText.font = FontUtility.GetCjkFont(16);

                btnObj.GetComponent<Button>().onClick.AddListener(() => ApplyEventChoice(capturedIndex));
            }

            SetPrompt("请选择事件牌的一个选项。");
        }

        private void ApplyEventChoice(int choiceIndex)
        {
            if (pendingEventCard == null) return;
            if (choiceIndex < 0 || choiceIndex >= pendingEventCard.ChoiceRewards.Count) return;

            var result = session.Submit(new GameCommand
            {
                Kind = GameCommandKind.ResolveEntranceEvent,
                PlayerId = localPlayerId,
                OptionIds = new List<string> { choiceIndex.ToString() }
            });

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            HideEventCardOptions();
            ClearHighlights();
            RefreshResourceDisplay();
            UpdateEntranceOrActionPrompt();
        }

        private void HideEventCardOptions()
        {
            if (eventChoiceOverlay != null)
            {
                Destroy(eventChoiceOverlay);
                eventChoiceOverlay = null;
            }

            pendingEventCard = null;
        }

        private void BuildSession()
        {
            mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());

            eventDeckService = new EventDeckService();
            var launchContext = GameLaunchContext.Instance;
            if (launchContext != null && launchContext.LocalPlayerId > 0)
            {
                localPlayerId = launchContext.LocalPlayerId;
            }

            var startPlayerId = GetStartPlayerId(launchContext);

            var state = new GameState
            {
                Phase = GamePhase.Entrance,
                StartPlayerId = startPlayerId,
                CurrentPlayerId = startPlayerId,
                MapId = mapQuery.Map.MapId
            };

            AddPlayersFromLaunchContext(state, launchContext);

            eventDeckService.InitializeDecks(
                state.Decks,
                EventCardDatabase.GreenCardIds,
                EventCardDatabase.YellowCardIds,
                EventCardDatabase.RedCardIds);

            session = new GameSession(state);
            session.RegisterHandler(new SetupCommandHandler(mapQuery));
            influenceService = new InfluenceService(mapQuery);
            var travelCostService = new TravelCostService(mapQuery);
            var movementService = new CityMovementService(mapQuery, influenceService, travelCostService);
            session.RegisterHandler(new MoveCityCommandHandler(movementService));
        }

        private static int GetStartPlayerId(GameLaunchContext launchContext)
        {
            if (launchContext != null && launchContext.Players.Count > 0)
            {
                return launchContext.Players[0].PlayerId;
            }

            return 1;
        }

        private void AddPlayersFromLaunchContext(GameState state, GameLaunchContext launchContext)
        {
            if (launchContext != null && launchContext.Players.Count > 0)
            {
                for (var i = 0; i < launchContext.Players.Count; i++)
                {
                    var seat = launchContext.Players[i];
                    state.Players.Add(new PlayerState
                    {
                        PlayerId = seat.PlayerId,
                        Name = string.IsNullOrEmpty(seat.PlayerName) ? "Player " + seat.PlayerId : seat.PlayerName,
                        Color = seat.Color
                    });
                }

                return;
            }

            state.Players.Add(new PlayerState
            {
                PlayerId = localPlayerId,
                Name = "Player " + localPlayerId,
                Color = PlayerColor.Blue
            });
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
                renderer.sprite = UguiUtility.CreateCircleSprite(96, 40f, 8f);
                renderer.color = new Color(0.25f, 0.95f, 0.45f, 0f);
                renderer.sortingOrder = 10;

                var collider = hotspotObject.GetComponent<CircleCollider2D>();
                collider.radius = 0.45f;

                var hotspot = hotspotObject.GetComponent<MapHotspot>();
                hotspot.Initialize(this, pair.Key);
                hotspotsById[pair.Key] = hotspot;
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

        private void BuildInfluenceSlotViews()
        {
            foreach (var pair in locationsById)
            {
                var locationId = pair.Key;
                var view = pair.Value;
                var slotRenderers = new List<SpriteRenderer>();

                var location = mapQuery.GetLocation(locationId);
                var slotCount = location.InfluenceSlotCount;

                for (var i = 0; i < slotCount; i++)
                {
                    var slotObject = new GameObject("InfluenceSlot " + locationId + ":" + i, typeof(SpriteRenderer));
                    slotObject.transform.SetParent(transform, false);

                    // Test coordinates: positioned to the left of the movement point.
                    // Offset x by -0.022 per slot column; stagger y for multiple slots.
                    // TODO: replace with actual adapted coordinates later.
                    var slotPos = view.NormalizedPosition;
                    var yOffset = slotCount > 1 ? (i - (slotCount - 1) * 0.5f) * 0.016f : 0f;
                    slotObject.transform.position = ToWorldPosition(
                        new Vector2(slotPos.x - 0.028f, slotPos.y + yOffset), -0.25f);

                    var renderer = slotObject.GetComponent<SpriteRenderer>();
                    renderer.sprite = UguiUtility.CreateCircleSprite(24, 8f, 2f);
                    renderer.color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
                    renderer.sortingOrder = 15;

                    slotRenderers.Add(renderer);
                }

                influenceSlotRenderers[locationId] = slotRenderers;
            }

            RefreshInfluenceDisplay();
        }

        private void RefreshInfluenceDisplay()
        {
            if (session == null || influenceService == null) return;

            foreach (var pair in influenceSlotRenderers)
            {
                var locationId = pair.Key;
                var renderers = pair.Value;

                for (var i = 0; i < renderers.Count; i++)
                {
                    var slotId = InfluenceService.GetLocationSlotId(locationId, i);
                    var placement = influenceService.FindInfluence(session.State, slotId);

                    if (placement != null)
                    {
                        renderers[i].color = new Color(0.9f, 0.3f, 0.3f, 0.85f);
                    }
                    else
                    {
                        renderers[i].color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
                    }
                }
            }
        }

        private void BuildPromptUi()
        {
            UguiUtility.EnsureEventSystem();

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

            panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
            var outline = panelObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
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
            promptText.color = UiTheme.GoldText;
            promptText.fontSize = 30;
            promptText.fontStyle = FontStyle.Bold;
            promptText.font = FontUtility.GetCjkFont(promptText.fontSize);
        }

        private void RefreshResourceDisplay()
        {
            RefreshInfoPanel();
        }

        private void ShowInitialPlacementChoices()
        {
            ClearHighlights();
            if (IsLocalPlayersTurn())
            {
                foreach (var locationId in StaticMapDefinitions.FourPlayerInitialLocationIds)
                {
                    SetHighlighted(locationId, new Color(0.25f, 0.95f, 0.45f, 0.82f));
                }
            }

            UpdateEntranceOrActionPrompt();
            ApplyDebugHotspotHighlights();
        }

        private bool IsLocalPlayersTurn()
        {
            return session == null || session.State.CurrentPlayerId == localPlayerId;
        }

        private void UpdateEntranceOrActionPrompt()
        {
            if (session == null)
            {
                return;
            }

            if (session.State.Phase == GamePhase.Entrance)
            {
                var player = session.State.FindPlayer(localPlayerId);
                if (player != null && string.IsNullOrEmpty(player.CityLocationId) && IsLocalPlayersTurn())
                {
                    SetPrompt("选择绿色资源点放置移动城市");
                }
                else
                {
                    SetPrompt("等待玩家 " + session.State.CurrentPlayerId + " 完成入场。");
                }

                return;
            }

            SetPrompt("点击移动城市，查看本回合可到达的资源点。");
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
                if (player.PlayerId != localPlayerId && player.CityLocationId == locationId)
                {
                    return true;
                }
            }

            return false;
        }

        private void SetHighlighted(string locationId, Color color)
        {
            highlightedLocationIds.Add(locationId);
            if (hotspotsById.TryGetValue(locationId, out var hotspot))
            {
                hotspot.SetColor(color);
            }
        }

        private void ClearHighlights()
        {
            highlightedLocationIds.Clear();
            foreach (var pair in hotspotsById)
            {
                pair.Value.SetColor(new Color(0.25f, 0.95f, 0.45f, 0f));
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

            foreach (var pair in hotspotsById)
            {
                if (highlightedLocationIds.Contains(pair.Key))
                {
                    continue;
                }

                pair.Value.SetColor(new Color(1f, 0.78f, 0.18f, 0.42f));
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

        public void Initialize(MobileCityInteractionController owner, string locationId)
        {
            controller = owner;
            LocationId = locationId;
            spriteRenderer = GetComponent<SpriteRenderer>();
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
