using System;
using System.Collections.Generic;
using System.IO;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Application.Setup;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Exploration;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using YC.Presentation.Maps;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class MobileCityInteractionController : MonoBehaviour
    {
        private enum ActionPanelMode
        {
            Hidden,
            ChooseAction,
            ResolvingMoveTarget,
            ResolvingExploreTarget,
            ResolvingDeployTarget,
            ResolvingDispatchSource,
            ResolvingDispatchTarget,
            PendingChoice,
            WaitingForNextPlayer
        }

        private readonly Dictionary<string, LocationView> locationsById = new Dictionary<string, LocationView>();
        private readonly Dictionary<string, MapHotspot> hotspotsById = new Dictionary<string, MapHotspot>();
        private readonly Dictionary<string, List<SpriteRenderer>> influenceSlotRenderers = new Dictionary<string, List<SpriteRenderer>>();
        private readonly Dictionary<string, List<SpriteRenderer>> routeInfluenceSlotRenderers = new Dictionary<string, List<SpriteRenderer>>();
        private readonly Dictionary<string, SpriteRenderer> resourceTokenRenderers = new Dictionary<string, SpriteRenderer>();
        private readonly Dictionary<ResourceType, Sprite> resourceTokenSprites = new Dictionary<ResourceType, Sprite>();
        private readonly Dictionary<int, GameObject> cityObjectsByPlayerId = new Dictionary<int, GameObject>();
        private readonly HashSet<string> highlightedLocationIds = new HashSet<string>();
        private readonly HashSet<string> highlightedInfluenceSlotIds = new HashSet<string>();
        private Sprite emptyInfluenceSlotSprite;
        private Sprite occupiedInfluenceSlotSprite;

        [SerializeField] private SpriteRenderer mapRenderer;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool debugClicks;

        private GameSession session;
        private MapQueryService mapQuery;
        private InfluenceService influenceService;
        private ExplorationService explorationService;
        private EventDeckService eventDeckService;
        private UnityNetcodeCommandTransport commandTransport;
        private ExpandableInfoPanel infoPanel;
        private Canvas uiCanvas;
        private GameObject actionPanelObject;
        private Text actionCurrentPlayerText;
        private Text actionPhaseText;
        private Text actionStatusText;
        private Button useCharacterButton;
        private Button declareCityStyleButton;
        private Button deployButton;
        private Button dispatchButton;
        private Button exploreButton;
        private Button moveCityButton;
        private Button buildButton;
        private Button specialActionButton;
        private Text promptText;
        private GameObject eventChoiceOverlay;
        private EventCardDefinition pendingEventCard;
        private string pendingExploreTargetId = string.Empty;
        private MapPath pendingExplorePath;
        private readonly Dictionary<string, int> pendingExplorePaymentRecipients = new Dictionary<string, int>();
        private readonly List<ExplorePaymentChoice> pendingExplorePaymentChoices = new List<ExplorePaymentChoice>();
        private readonly List<ExplorePathChoice> pendingExplorePathChoices = new List<ExplorePathChoice>();
        private ActionPanelMode actionPanelMode = ActionPanelMode.Hidden;
        private bool awaitingInitialPlacement = true;
        private string pendingDispatchSourceSlotId = string.Empty;
        private string pendingDispatchFirstSourceSlotId = string.Empty;
        private string pendingDispatchFirstTargetSlotId = string.Empty;
        private int localPlayerId = 1;
        private int lastDebugCoordinateLogFrame = -1;

        public GameState CurrentState
        {
            get { return session == null ? null : session.State; }
        }

        public bool CanEndCurrentAction()
        {
            SynchronizeLocalPlayerForHotseat();

            if (session == null ||
                session.State == null ||
                session.State.HasPendingChoice() ||
                !IsLocalPlayersTurn())
            {
                return false;
            }

            if (session.State.Phase != GamePhase.ActionRound1 && session.State.Phase != GamePhase.ActionRound2)
            {
                return false;
            }

            var player = session.State.FindPlayer(localPlayerId);
            return player != null && player.ActedMainActionThisTurn;
        }

        public void EndCurrentAction()
        {
            if (!CanEndCurrentAction())
            {
                SetPrompt("完成主要行动后才能结束本回合。");
                return;
            }

            bool appliedLocally;
            var result = SubmitGameCommand(new GameCommand
            {
                Kind = GameCommandKind.EndAction,
                PlayerId = localPlayerId
            }, out appliedLocally);

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            if (!appliedLocally)
            {
                SetPrompt("结束回合命令已发送给主机，等待确认。");
                return;
            }

            RefreshAllFromState();
            SetPrompt(BuildEndActionPrompt(session.State));
        }

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
            BuildActionPanel();
            BuildHotspots();
            BuildResourceTokenViews();
            BuildInfluenceSlotViews();
            EnsureInfoPanel();
            ShowInitialPlacementChoices();
            BuildCommandTransport(GameLaunchContext.Instance);
        }

        private void Update()
        {
            if (!IsShiftDebugClick())
            {
                return;
            }

            LogPointerMapCoordinate();
        }

        private void OnDestroy()
        {
            if (commandTransport != null)
            {
                commandTransport.InitialStateApplied -= OnInitialNetworkStateApplied;
                commandTransport.ConfirmedCommandApplied -= OnConfirmedNetworkCommandApplied;
                commandTransport.CommandRejected -= OnNetworkCommandRejected;
            }
        }

        private static string BuildEndActionPrompt(GameState state)
        {
            if (state != null && state.Phase == GamePhase.ActionRound1 && state.ActionRound == 1)
            {
                return "已进入下一回合，请继续行动。";
            }

            return "本回合已结束，等待下一位玩家行动。";
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
            state.CurrentPlayerId = GetFirstTurnPlayerId(state);

            actionPanelMode = ActionPanelMode.ChooseAction;
            ClearPendingDispatch();
            ClearHighlights();
            RefreshAllFromState();
            SetPrompt("请从右下角行动面板选择主要行动。");
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

            if (!highlightedLocationIds.Contains(locationId))
            {
                return;
            }

            switch (actionPanelMode)
            {
                case ActionPanelMode.ResolvingMoveTarget:
                    TryMoveCity(locationId);
                    break;
                case ActionPanelMode.ResolvingExploreTarget:
                    TryExploreLocation(locationId);
                    break;
                case ActionPanelMode.ResolvingDeployTarget:
                    TryDeployInfluence(locationId);
                    break;
                case ActionPanelMode.ResolvingDispatchSource:
                    SelectDispatchSource(locationId);
                    break;
                case ActionPanelMode.ResolvingDispatchTarget:
                    TryDispatchInfluence(locationId);
                    break;
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

            SetPrompt("请使用右下角行动面板选择主要行动。");
        }

        public void OnInfluenceSlotClicked(string slotId)
        {
            if (IsShiftDebugClick())
            {
                LogPointerMapCoordinate();
                return;
            }

            if (actionPanelMode != ActionPanelMode.ResolvingDeployTarget)
            {
                return;
            }

            if (!highlightedInfluenceSlotIds.Contains(slotId))
            {
                return;
            }

            TryDeployInfluenceToSlot(slotId);
        }

        private void TryPlaceInitialCity(string locationId)
        {
            bool appliedLocally;
            var result = SubmitGameCommand(new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = localPlayerId,
                TargetId = locationId
            }, out appliedLocally);

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            if (!appliedLocally)
            {
                SetPrompt("命令已发送给主机，等待确认。");
                return;
            }

            RefreshAllFromState();
        }

        private void TryMoveCity(string locationId)
        {
            bool appliedLocally;
            var result = SubmitGameCommand(new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = localPlayerId,
                TargetId = locationId
            }, out appliedLocally);

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            if (!appliedLocally)
            {
                SetPrompt("移动命令已发送给主机，等待确认。");
                return;
            }

            MoveCityView(locationId);
            CompleteActionCommandUi("移动城市已完成，请点击结束本回合。");
        }

        private void TryExploreLocation(string locationId)
        {
            BeginExploreChoiceFromLocation(locationId);
        }

        private void TryDeployInfluence(string locationId)
        {
            var slotId = FindFirstPlaceableLocationSlot(locationId, true);
            if (string.IsNullOrEmpty(slotId))
            {
                SetPrompt("该位置没有可部署的影响力空格。");
                return;
            }

            TryDeployInfluenceToSlot(slotId);
        }

        private void TryDeployInfluenceToSlot(string slotId)
        {
            bool appliedLocally;
            var result = SubmitGameCommand(new GameCommand
            {
                Kind = GameCommandKind.DeployInfluence,
                PlayerId = localPlayerId,
                TargetId = slotId
            }, out appliedLocally);

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            if (!appliedLocally)
            {
                SetPrompt("部署命令已发送给主机，等待确认。");
                return;
            }

            CompleteActionCommandUi("部署已完成，请点击结束本回合。");
        }

        private void SelectDispatchSource(string locationId)
        {
            var slotId = FindFirstOwnLocationInfluenceSlot(locationId);
            if (string.IsNullOrEmpty(slotId))
            {
                SetPrompt("请选择一个自己的影响力作为调度来源。");
                return;
            }

            pendingDispatchSourceSlotId = slotId;
            actionPanelMode = ActionPanelMode.ResolvingDispatchTarget;
            ClearHighlights();
            HighlightDispatchTargets(locationId);
            RefreshActionPanel();
            SetPrompt("请选择调度目标资源点。");
        }

        private void TryDispatchInfluence(string locationId)
        {
            if (string.IsNullOrEmpty(pendingDispatchSourceSlotId))
            {
                actionPanelMode = ActionPanelMode.ResolvingDispatchSource;
                SetPrompt("请先选择调度来源影响力。");
                return;
            }

            var targetSlotId = FindFirstPlaceableLocationSlot(locationId, false);
            if (string.IsNullOrEmpty(targetSlotId))
            {
                SetPrompt("该资源点没有可调度进入的影响力空格。");
                return;
            }

            if (HasPendingDispatchFirstMove())
            {
                SubmitPendingDispatchCommand(pendingDispatchSourceSlotId, targetSlotId);
                return;
            }

            pendingDispatchFirstSourceSlotId = pendingDispatchSourceSlotId;
            pendingDispatchFirstTargetSlotId = targetSlotId;
            pendingDispatchSourceSlotId = string.Empty;
            actionPanelMode = ActionPanelMode.ResolvingDispatchSource;
            ClearHighlights();
            HighlightOwnInfluenceLocations();
            RefreshActionPanel();
            SetPrompt("调度：选择第二个影响力，或再次点击调度完成。");
        }

        private void SubmitPendingDispatchCommand(string secondSourceSlotId, string secondTargetSlotId)
        {
            if (!HasPendingDispatchFirstMove())
            {
                return;
            }

            bool appliedLocally;
            var command = new GameCommand
            {
                Kind = GameCommandKind.DispatchInfluence,
                PlayerId = localPlayerId,
                SourceId = pendingDispatchFirstSourceSlotId,
                TargetId = pendingDispatchFirstTargetSlotId
            };

            if (!string.IsNullOrEmpty(secondSourceSlotId) && !string.IsNullOrEmpty(secondTargetSlotId))
            {
                command.Parameters["source2"] = secondSourceSlotId;
                command.Parameters["target2"] = secondTargetSlotId;
            }

            var result = SubmitGameCommand(command, out appliedLocally);

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            if (!appliedLocally)
            {
                SetPrompt("调度命令已发送给主机，等待确认。");
                return;
            }

            CompleteActionCommandUi("调度已完成，请点击结束本回合。");
        }

        private void CompleteActionCommandUi(string message)
        {
            actionPanelMode = ActionPanelMode.ChooseAction;
            ClearPendingDispatch();
            ClearHighlights();
            RefreshResourceDisplay();
            RefreshInfluenceDisplay();
            RefreshActionPanel();
            RefreshRoundTrackerFromState();
            UpdateEntranceOrActionPrompt(message);
        }

        private void ClearPendingDispatch()
        {
            pendingDispatchSourceSlotId = string.Empty;
            pendingDispatchFirstSourceSlotId = string.Empty;
            pendingDispatchFirstTargetSlotId = string.Empty;
        }

        private bool HasPendingDispatchFirstMove()
        {
            return !string.IsNullOrEmpty(pendingDispatchFirstSourceSlotId) &&
                   !string.IsNullOrEmpty(pendingDispatchFirstTargetSlotId);
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
            infoPanel.SetRowValue("玩家概览", "剩余影响力", player.InfluenceSupply.ToString());
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

        private void BeginExploreChoiceFromLocation(string locationId)
        {
            var player = session.State.FindPlayer(localPlayerId);
            if (player == null || string.IsNullOrEmpty(player.CityLocationId))
            {
                SetPrompt("玩家城市不在场上。");
                return;
            }

            IReadOnlyList<MapPath> paths;
            try
            {
                paths = explorationService.FindDefaultPathChoices(session.State, localPlayerId, locationId);
            }
            catch (ArgumentException)
            {
                SetPrompt("没有可到达该探索目标的路线。");
                return;
            }

            if (paths.Count <= 0)
            {
                SetPrompt("没有可到达该探索目标的路线。");
                return;
            }

            pendingExploreTargetId = locationId ?? string.Empty;
            pendingExplorePath = null;
            pendingExplorePaymentRecipients.Clear();
            pendingExplorePaymentChoices.Clear();
            pendingExplorePathChoices.Clear();
            for (var i = 0; i < paths.Count; i++)
            {
                pendingExplorePathChoices.Add(new ExplorePathChoice(paths[i], GetExplorePathChoiceLabel(paths[i], i)));
            }

            ClearHighlights();
            RefreshActionPanel();

            if (ShouldPromptForExplorePathChoice(paths))
            {
                ShowExplorePathOptions();
                return;
            }

            SelectExplorePath(paths[0]);
        }

        private void BeginExploreChoice(string locationId, MapPath path, EventCardDefinition card)
        {
            pendingExploreTargetId = locationId ?? string.Empty;
            pendingExplorePath = path;
            pendingExplorePaymentRecipients.Clear();
            pendingExplorePaymentChoices.Clear();
            pendingExplorePathChoices.Clear();
            BuildExplorePaymentChoices(path);

            ClearHighlights();
            RefreshActionPanel();
            ShowEventCardOptions(card);
        }

        private EventCardDefinition PeekExploreEventCard(string locationId)
        {
            var color = StaticMapDefinitions.GetEventColor(locationId);
            List<string> deck;
            switch (color)
            {
                case EventColor.Green:
                    deck = session.State.Decks.EventDeckGreen;
                    break;
                case EventColor.Yellow:
                    deck = session.State.Decks.EventDeckYellow;
                    break;
                case EventColor.Red:
                    deck = session.State.Decks.EventDeckRed;
                    break;
                default:
                    return null;
            }

            if (deck == null || deck.Count == 0)
            {
                return null;
            }

            return EventCardDatabase.Get(deck[deck.Count - 1]);
        }

        private void BuildExplorePaymentChoices(MapPath path)
        {
            if (path == null)
            {
                return;
            }

            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                var routeId = path.RouteIds[i];
                if (HasRouteInfluenceOwnedBy(routeId, localPlayerId))
                {
                    continue;
                }

                var opponentOwners = GetOpponentInfluenceOwnersOnRoute(routeId, localPlayerId);
                if (opponentOwners.Count <= 0)
                {
                    continue;
                }

                pendingExplorePaymentChoices.Add(new ExplorePaymentChoice(routeId, opponentOwners));
                pendingExplorePaymentRecipients[routeId] = opponentOwners[0];
            }
        }

        private bool ShouldPromptForExplorePathChoice(IReadOnlyList<MapPath> paths)
        {
            if (paths == null || paths.Count <= 1)
            {
                return false;
            }

            var signatures = new HashSet<string>();
            var hasOpponentToll = false;
            for (var i = 0; i < paths.Count; i++)
            {
                var signature = GetExplorePathOpponentRecipientSignature(paths[i]);
                if (!string.IsNullOrEmpty(signature))
                {
                    hasOpponentToll = true;
                }

                signatures.Add(signature);
            }

            return hasOpponentToll && signatures.Count > 1;
        }

        private string GetExplorePathOpponentRecipientSignature(MapPath path)
        {
            if (path == null)
            {
                return string.Empty;
            }

            var signature = string.Empty;
            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                var routeId = path.RouteIds[i];
                if (HasRouteInfluenceOwnedBy(routeId, localPlayerId))
                {
                    continue;
                }

                var owners = GetOpponentInfluenceOwnersOnRoute(routeId, localPlayerId);
                if (owners.Count <= 0)
                {
                    continue;
                }

                owners.Sort();
                if (!string.IsNullOrEmpty(signature))
                {
                    signature += "|";
                }

                for (var ownerIndex = 0; ownerIndex < owners.Count; ownerIndex++)
                {
                    if (ownerIndex > 0)
                    {
                        signature += ",";
                    }

                    signature += owners[ownerIndex].ToString();
                }
            }

            return signature;
        }

        private string GetExplorePathChoiceLabel(MapPath path, int index)
        {
            return "路线 " + (index + 1) + "：" + EncodeIds(path.LocationIds) + "；路费：" + GetExplorePathPaymentLabel(path);
        }

        private string GetExplorePathPaymentLabel(MapPath path)
        {
            var systemTollCount = 0;
            var recipientNames = new List<string>();
            if (path != null)
            {
                for (var i = 0; i < path.RouteIds.Count; i++)
                {
                    var routeId = path.RouteIds[i];
                    if (HasRouteInfluenceOwnedBy(routeId, localPlayerId))
                    {
                        continue;
                    }

                    var owners = GetOpponentInfluenceOwnersOnRoute(routeId, localPlayerId);
                    if (owners.Count <= 0)
                    {
                        systemTollCount += 1;
                        continue;
                    }

                    owners.Sort();
                    for (var ownerIndex = 0; ownerIndex < owners.Count; ownerIndex++)
                    {
                        var name = GetPlayerDisplayName(owners[ownerIndex]);
                        if (!recipientNames.Contains(name))
                        {
                            recipientNames.Add(name);
                        }
                    }
                }
            }

            var label = string.Empty;
            for (var i = 0; i < recipientNames.Count; i++)
            {
                if (!string.IsNullOrEmpty(label))
                {
                    label += "、";
                }

                label += recipientNames[i];
            }

            if (systemTollCount > 0)
            {
                if (!string.IsNullOrEmpty(label))
                {
                    label += "、";
                }

                label += "系统";
            }

            return string.IsNullOrEmpty(label) ? "无" : label;
        }

        private void SelectExplorePath(MapPath path)
        {
            pendingExplorePath = path;
            pendingExplorePaymentRecipients.Clear();
            pendingExplorePaymentChoices.Clear();
            BuildExplorePaymentChoices(path);

            if (pendingExplorePaymentChoices.Count > 0)
            {
                ShowExplorePaymentOptions();
                return;
            }

            SubmitExploreStart();
        }

        private void SelectExplorePathChoice(int choiceIndex)
        {
            if (choiceIndex < 0 || choiceIndex >= pendingExplorePathChoices.Count)
            {
                return;
            }

            SelectExplorePath(pendingExplorePathChoices[choiceIndex].Path);
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
            if (eventChoiceOverlay != null)
            {
                Destroy(eventChoiceOverlay);
                eventChoiceOverlay = null;
            }

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
            panelRect.sizeDelta = new Vector2(760f, 274f + card.ChoiceRewards.Count * 72f + pendingExplorePaymentChoices.Count * 54f);
            panelRect.anchoredPosition = new Vector2(0f, -40f);

            panel.GetComponent<Image>().color = UiTheme.PanelBackground;
            panel.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
            panel.GetComponent<Outline>().effectDistance = new Vector2(3f, -3f);

            var titleObj = new GameObject("Title", typeof(RectTransform), typeof(Text));
            titleObj.transform.SetParent(panelRect, false);
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.sizeDelta = new Vector2(0f, 46f);
            titleRect.anchoredPosition = new Vector2(0f, -30f);

            var titleText = titleObj.GetComponent<Text>();
            titleText.text = GetEventCardDisplayName(card);
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.color = UiTheme.GoldText;
            titleText.fontSize = 24;
            titleText.fontStyle = FontStyle.Bold;
            titleText.font = FontUtility.GetCjkFont(24);
            titleText.resizeTextForBestFit = true;
            titleText.resizeTextMinSize = 16;
            titleText.resizeTextMaxSize = 24;

            var resourcePointObj = new GameObject("Resource Point", typeof(RectTransform), typeof(Text));
            resourcePointObj.transform.SetParent(panelRect, false);
            var resourcePointRect = resourcePointObj.GetComponent<RectTransform>();
            resourcePointRect.anchorMin = new Vector2(0.06f, 1f);
            resourcePointRect.anchorMax = new Vector2(0.94f, 1f);
            resourcePointRect.sizeDelta = new Vector2(0f, 24f);
            resourcePointRect.anchoredPosition = new Vector2(0f, -66f);

            var resourcePointText = resourcePointObj.GetComponent<Text>();
            resourcePointText.text = GetEventCardMetadataLabel(card);
            resourcePointText.alignment = TextAnchor.MiddleCenter;
            resourcePointText.color = UiTheme.LabelText;
            resourcePointText.fontSize = 14;
            resourcePointText.fontStyle = FontStyle.Bold;
            resourcePointText.font = FontUtility.GetCjkFont(14);
            resourcePointText.horizontalOverflow = HorizontalWrapMode.Wrap;
            resourcePointText.verticalOverflow = VerticalWrapMode.Truncate;
            resourcePointText.resizeTextForBestFit = true;
            resourcePointText.resizeTextMinSize = 12;
            resourcePointText.resizeTextMaxSize = 14;

            var descriptionObj = new GameObject("Description", typeof(RectTransform), typeof(Text));
            descriptionObj.transform.SetParent(panelRect, false);
            var descriptionRect = descriptionObj.GetComponent<RectTransform>();
            descriptionRect.anchorMin = new Vector2(0.06f, 1f);
            descriptionRect.anchorMax = new Vector2(0.94f, 1f);
            descriptionRect.sizeDelta = new Vector2(0f, 100f);
            descriptionRect.anchoredPosition = new Vector2(0f, -130f);

            var descriptionText = descriptionObj.GetComponent<Text>();
            descriptionText.text = string.IsNullOrEmpty(card.Description) ? "暂无描述" : card.Description;
            descriptionText.alignment = TextAnchor.UpperLeft;
            descriptionText.color = UiTheme.ValueText;
            descriptionText.fontSize = 15;
            descriptionText.font = FontUtility.GetCjkFont(15);
            descriptionText.horizontalOverflow = HorizontalWrapMode.Wrap;
            descriptionText.verticalOverflow = VerticalWrapMode.Truncate;
            descriptionText.resizeTextForBestFit = true;
            descriptionText.resizeTextMinSize = 12;
            descriptionText.resizeTextMaxSize = 15;

            CreateExplorePaymentRecipientControls(panelRect);

            for (var i = 0; i < card.ChoiceRewards.Count; i++)
            {
                var capturedIndex = i;
                var desc = card.ChoiceDescriptions[i];

                var btnObj = new GameObject("Choice " + (i + 1), typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
                btnObj.transform.SetParent(panelRect, false);

                var btnRect = btnObj.GetComponent<RectTransform>();
                btnRect.anchorMin = new Vector2(0.05f, 1f);
                btnRect.anchorMax = new Vector2(0.95f, 1f);
                btnRect.sizeDelta = new Vector2(0f, 62f);
                btnRect.anchoredPosition = new Vector2(0f, -234f - pendingExplorePaymentChoices.Count * 54f - i * 72f);

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
                labelText.text = desc;
                labelText.alignment = TextAnchor.MiddleLeft;
                labelText.color = UiTheme.GoldText;
                labelText.fontSize = 15;
                labelText.font = FontUtility.GetCjkFont(15);
                labelText.horizontalOverflow = HorizontalWrapMode.Wrap;
                labelText.verticalOverflow = VerticalWrapMode.Truncate;
                labelText.resizeTextForBestFit = true;
                labelText.resizeTextMinSize = 12;
                labelText.resizeTextMaxSize = 15;

                btnObj.GetComponent<Button>().onClick.AddListener(() => ApplyEventChoice(capturedIndex));
            }

            SetPrompt("请选择事件牌的一个选项。");
        }

        private void ShowExplorePathOptions()
        {
            if (eventChoiceOverlay != null)
            {
                Destroy(eventChoiceOverlay);
                eventChoiceOverlay = null;
            }

            var canvasTransform = promptText.canvas.GetComponent<RectTransform>();
            eventChoiceOverlay = new GameObject("Explore Path Overlay", typeof(RectTransform), typeof(Image));
            eventChoiceOverlay.transform.SetParent(canvasTransform, false);

            var overlayRect = eventChoiceOverlay.GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;

            eventChoiceOverlay.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);

            var panel = new GameObject("Path Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panel.transform.SetParent(overlayRect, false);

            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(820f, 126f + pendingExplorePathChoices.Count * 62f);
            panelRect.anchoredPosition = new Vector2(0f, -30f);

            panel.GetComponent<Image>().color = UiTheme.PanelBackground;
            panel.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
            panel.GetComponent<Outline>().effectDistance = new Vector2(3f, -3f);

            var titleObj = new GameObject("Title", typeof(RectTransform), typeof(Text));
            titleObj.transform.SetParent(panelRect, false);
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.sizeDelta = new Vector2(0f, 46f);
            titleRect.anchoredPosition = new Vector2(0f, -30f);

            var titleText = titleObj.GetComponent<Text>();
            titleText.text = "选择探索路线";
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.color = UiTheme.GoldText;
            titleText.fontSize = 22;
            titleText.fontStyle = FontStyle.Bold;
            titleText.font = FontUtility.GetCjkFont(22);

            for (var i = 0; i < pendingExplorePathChoices.Count; i++)
            {
                var capturedIndex = i;
                var btnObj = new GameObject("Path Choice " + (i + 1), typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
                btnObj.transform.SetParent(panelRect, false);

                var btnRect = btnObj.GetComponent<RectTransform>();
                btnRect.anchorMin = new Vector2(0.05f, 1f);
                btnRect.anchorMax = new Vector2(0.95f, 1f);
                btnRect.sizeDelta = new Vector2(0f, 50f);
                btnRect.anchoredPosition = new Vector2(0f, -88f - i * 62f);

                btnObj.GetComponent<Image>().color = UiTheme.ButtonBackground;
                btnObj.GetComponent<Outline>().effectColor = UiTheme.GoldOutlineThin;
                btnObj.GetComponent<Outline>().effectDistance = new Vector2(1f, -1f);
                btnObj.GetComponent<Button>().onClick.AddListener(() => SelectExplorePathChoice(capturedIndex));

                var labelObj = new GameObject("Label", typeof(RectTransform), typeof(Text));
                labelObj.transform.SetParent(btnRect, false);
                var labelRect = labelObj.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = new Vector2(12f, 0f);
                labelRect.offsetMax = new Vector2(-12f, 0f);

                var labelText = labelObj.GetComponent<Text>();
                labelText.text = pendingExplorePathChoices[i].Label;
                labelText.alignment = TextAnchor.MiddleLeft;
                labelText.color = UiTheme.GoldText;
                labelText.fontSize = 14;
                labelText.font = FontUtility.GetCjkFont(14);
                labelText.horizontalOverflow = HorizontalWrapMode.Wrap;
                labelText.verticalOverflow = VerticalWrapMode.Truncate;
                labelText.resizeTextForBestFit = true;
                labelText.resizeTextMinSize = 11;
                labelText.resizeTextMaxSize = 14;
            }

            SetPrompt("多条最短路线的路费相同，请选择要支付给哪一方。");
        }

        private void ShowExplorePaymentOptions()
        {
            if (eventChoiceOverlay != null)
            {
                Destroy(eventChoiceOverlay);
                eventChoiceOverlay = null;
            }

            var canvasTransform = promptText.canvas.GetComponent<RectTransform>();
            eventChoiceOverlay = new GameObject("Explore Payment Overlay", typeof(RectTransform), typeof(Image));
            eventChoiceOverlay.transform.SetParent(canvasTransform, false);

            var overlayRect = eventChoiceOverlay.GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;

            eventChoiceOverlay.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);

            var panel = new GameObject("Payment Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panel.transform.SetParent(overlayRect, false);

            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(760f, 160f + pendingExplorePaymentChoices.Count * 54f);
            panelRect.anchoredPosition = new Vector2(0f, -30f);

            panel.GetComponent<Image>().color = UiTheme.PanelBackground;
            panel.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
            panel.GetComponent<Outline>().effectDistance = new Vector2(3f, -3f);

            var titleObj = new GameObject("Title", typeof(RectTransform), typeof(Text));
            titleObj.transform.SetParent(panelRect, false);
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.sizeDelta = new Vector2(0f, 46f);
            titleRect.anchoredPosition = new Vector2(0f, -30f);

            var titleText = titleObj.GetComponent<Text>();
            titleText.text = "选择过路费接收者";
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.color = UiTheme.GoldText;
            titleText.fontSize = 22;
            titleText.fontStyle = FontStyle.Bold;
            titleText.font = FontUtility.GetCjkFont(22);

            CreateExplorePaymentRecipientControls(panelRect);

            var confirmObj = new GameObject("Confirm Explore", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            confirmObj.transform.SetParent(panelRect, false);
            var confirmRect = confirmObj.GetComponent<RectTransform>();
            confirmRect.anchorMin = new Vector2(0.5f, 1f);
            confirmRect.anchorMax = new Vector2(0.5f, 1f);
            confirmRect.sizeDelta = new Vector2(220f, 42f);
            confirmRect.anchoredPosition = new Vector2(0f, -114f - pendingExplorePaymentChoices.Count * 54f);

            confirmObj.GetComponent<Image>().color = UiTheme.ButtonBackground;
            confirmObj.GetComponent<Outline>().effectColor = UiTheme.GoldOutlineThin;
            confirmObj.GetComponent<Outline>().effectDistance = new Vector2(1f, -1f);
            confirmObj.GetComponent<Button>().onClick.AddListener(SubmitExploreStart);

            var labelObj = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObj.transform.SetParent(confirmRect, false);
            var labelRect = labelObj.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(8f, 0f);
            labelRect.offsetMax = new Vector2(-8f, 0f);

            var labelText = labelObj.GetComponent<Text>();
            labelText.text = "支付过路费并探索";
            labelText.alignment = TextAnchor.MiddleCenter;
            labelText.color = UiTheme.GoldText;
            labelText.fontSize = 15;
            labelText.fontStyle = FontStyle.Bold;
            labelText.font = FontUtility.GetCjkFont(15);

            SetPrompt("选择每条路线的过路费接收者，然后确认探索。");
        }

        private void CreateExplorePaymentRecipientControls(RectTransform panelRect)
        {
            if (pendingExplorePaymentChoices.Count <= 0)
            {
                return;
            }

            for (var i = 0; i < pendingExplorePaymentChoices.Count; i++)
            {
                var choice = pendingExplorePaymentChoices[i];

                var labelObj = new GameObject("Payment " + choice.RouteId, typeof(RectTransform), typeof(Text));
                labelObj.transform.SetParent(panelRect, false);
                var labelRect = labelObj.GetComponent<RectTransform>();
                labelRect.anchorMin = new Vector2(0.06f, 1f);
                labelRect.anchorMax = new Vector2(0.34f, 1f);
                labelRect.sizeDelta = new Vector2(0f, 40f);
                labelRect.anchoredPosition = new Vector2(0f, -204f - i * 54f);

                var labelText = labelObj.GetComponent<Text>();
                labelText.text = "过路费 " + choice.RouteId;
                labelText.alignment = TextAnchor.MiddleLeft;
                labelText.color = UiTheme.ValueText;
                labelText.fontSize = 14;
                labelText.font = FontUtility.GetCjkFont(14);
                labelText.resizeTextForBestFit = true;
                labelText.resizeTextMinSize = 11;
                labelText.resizeTextMaxSize = 14;

                for (var ownerIndex = 0; ownerIndex < choice.RecipientPlayerIds.Count; ownerIndex++)
                {
                    var recipientPlayerId = choice.RecipientPlayerIds[ownerIndex];
                    var routeId = choice.RouteId;

                    var btnObj = new GameObject("Payment Recipient " + routeId + " " + recipientPlayerId, typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
                    btnObj.transform.SetParent(panelRect, false);

                    var btnRect = btnObj.GetComponent<RectTransform>();
                    btnRect.anchorMin = new Vector2(0.36f, 1f);
                    btnRect.anchorMax = new Vector2(0.94f, 1f);
                    btnRect.sizeDelta = new Vector2(0f, 34f);
                    btnRect.anchoredPosition = new Vector2(ownerIndex * 110f, -204f - i * 54f);

                    btnObj.GetComponent<Image>().color = UiTheme.ButtonBackground;
                    btnObj.GetComponent<Outline>().effectColor = pendingExplorePaymentRecipients[routeId] == recipientPlayerId
                        ? UiTheme.GoldOutline
                        : UiTheme.GoldOutlineThin;
                    btnObj.GetComponent<Outline>().effectDistance = new Vector2(1f, -1f);

                    var textObj = new GameObject("Label", typeof(RectTransform), typeof(Text));
                    textObj.transform.SetParent(btnRect, false);
                    var textRect = textObj.GetComponent<RectTransform>();
                    textRect.anchorMin = Vector2.zero;
                    textRect.anchorMax = Vector2.one;
                    textRect.offsetMin = new Vector2(8f, 0f);
                    textRect.offsetMax = new Vector2(-8f, 0f);

                    var text = textObj.GetComponent<Text>();
                    text.text = GetPlayerDisplayName(recipientPlayerId);
                    text.alignment = TextAnchor.MiddleCenter;
                    text.color = UiTheme.GoldText;
                    text.fontSize = 13;
                    text.font = FontUtility.GetCjkFont(13);
                    text.resizeTextForBestFit = true;
                    text.resizeTextMinSize = 10;
                    text.resizeTextMaxSize = 13;

                    btnObj.GetComponent<Button>().onClick.AddListener(() =>
                    {
                        pendingExplorePaymentRecipients[routeId] = recipientPlayerId;
                        ShowExplorePaymentOptions();
                    });
                }
            }
        }

        private void ApplyEventChoice(int choiceIndex)
        {
            if (pendingEventCard == null) return;
            if (choiceIndex < 0 || choiceIndex >= pendingEventCard.ChoiceRewards.Count) return;

            if (session.State.PendingChoice != null &&
                session.State.PendingChoice.ChoiceType == ExploreLocationCommandHandler.ExploreEventChoiceType)
            {
                SubmitResolveExploreChoice(choiceIndex);
                return;
            }

            if (!string.IsNullOrEmpty(pendingExploreTargetId))
            {
                SubmitExploreChoice(choiceIndex);
                return;
            }

            bool appliedLocally;
            var result = SubmitGameCommand(new GameCommand
            {
                Kind = GameCommandKind.ResolveEntranceEvent,
                PlayerId = localPlayerId,
                OptionIds = new List<string> { choiceIndex.ToString() }
            }, out appliedLocally);

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            if (!appliedLocally)
            {
                SetPrompt("事件选择已发送给主机，等待确认。");
                return;
            }

            RefreshAllFromState();
        }

        private void SubmitExploreStart()
        {
            if (pendingExplorePath == null)
            {
                SetPrompt("缺少探索路线。");
                return;
            }

            var command = new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = localPlayerId,
                TargetId = pendingExploreTargetId
            };
            command.Parameters[ExploreLocationCommandHandler.PathLocationIdsParameter] = EncodeIds(pendingExplorePath.LocationIds);
            command.Parameters[ExploreLocationCommandHandler.RouteIdsParameter] = EncodeIds(pendingExplorePath.RouteIds);

            var encodedPayments = EncodePaymentRecipients();
            if (!string.IsNullOrEmpty(encodedPayments))
            {
                command.Parameters[ExploreLocationCommandHandler.PaymentRecipientsParameter] = encodedPayments;
            }

            bool appliedLocally;
            var result = SubmitGameCommand(command, out appliedLocally);

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            if (!appliedLocally)
            {
                SetPrompt("探索命令已发送给主机，等待确认。");
                return;
            }

            HideEventCardOptions();
            ClearPendingExploreChoice();
            RefreshResourceDisplay();
            RefreshInfluenceDisplay();

            if (session.State.PendingChoice != null)
            {
                ShowPendingEventCardOptions();
                return;
            }

            RefreshActionPanel();
            SetPrompt("探索已开始。");
        }

        private void SubmitResolveExploreChoice(int choiceIndex)
        {
            bool appliedLocally;
            var result = SubmitGameCommand(new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = localPlayerId,
                OptionIds = new List<string> { choiceIndex.ToString() }
            }, out appliedLocally);

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            if (!appliedLocally)
            {
                SetPrompt("探索事件选择已发送给主机，等待确认。");
                return;
            }

            HideEventCardOptions();
            ClearHighlights();
            RefreshResourceDisplay();
            RefreshInfluenceDisplay();
            CompleteActionCommandUi("探索已完成，请点击结束本回合。");
        }

        private void SubmitExploreChoice(int choiceIndex)
        {
            if (pendingExplorePath == null)
            {
                SetPrompt("缺少探索路线。");
                return;
            }

            var command = new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = localPlayerId,
                TargetId = pendingExploreTargetId
            };
            command.Parameters[ExploreLocationCommandHandler.EventOptionIdParameter] = choiceIndex.ToString();
            command.Parameters[ExploreLocationCommandHandler.PathLocationIdsParameter] = EncodeIds(pendingExplorePath.LocationIds);
            command.Parameters[ExploreLocationCommandHandler.RouteIdsParameter] = EncodeIds(pendingExplorePath.RouteIds);

            var encodedPayments = EncodePaymentRecipients();
            if (!string.IsNullOrEmpty(encodedPayments))
            {
                command.Parameters[ExploreLocationCommandHandler.PaymentRecipientsParameter] = encodedPayments;
            }

            bool appliedLocally;
            var result = SubmitGameCommand(command, out appliedLocally);

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            if (!appliedLocally)
            {
                SetPrompt("探索命令已发送给主机，等待确认。");
                return;
            }

            HideEventCardOptions();
            CompleteActionCommandUi("探索已完成，请点击结束本回合。");
        }

        private static string EncodeIds(IReadOnlyList<string> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return string.Empty;
            }

            var value = ids[0] ?? string.Empty;
            for (var i = 1; i < ids.Count; i++)
            {
                value += "," + (ids[i] ?? string.Empty);
            }

            return value;
        }

        private string EncodePaymentRecipients()
        {
            var encoded = string.Empty;
            for (var i = 0; i < pendingExplorePaymentChoices.Count; i++)
            {
                var routeId = pendingExplorePaymentChoices[i].RouteId;
                int recipientPlayerId;
                if (!pendingExplorePaymentRecipients.TryGetValue(routeId, out recipientPlayerId))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(encoded))
                {
                    encoded += ";";
                }

                encoded += routeId + "=" + recipientPlayerId;
            }

            return encoded;
        }

        private void ClearPendingExploreChoice()
        {
            pendingExploreTargetId = string.Empty;
            pendingExplorePath = null;
            pendingExplorePaymentRecipients.Clear();
            pendingExplorePaymentChoices.Clear();
            pendingExplorePathChoices.Clear();
        }

        private void HideEventCardOptions()
        {
            if (eventChoiceOverlay != null)
            {
                Destroy(eventChoiceOverlay);
                eventChoiceOverlay = null;
            }

            pendingEventCard = null;
            ClearPendingExploreChoice();
        }

        private static string GetEventCardDisplayName(EventCardDefinition card)
        {
            if (card == null || string.IsNullOrEmpty(card.Name))
            {
                return "事件牌";
            }

            return card.Name
                .Replace("（4）", string.Empty)
                .Replace("(4)", string.Empty)
                .Trim();
        }

        private string GetEventCardMetadataLabel(EventCardDefinition card)
        {
            var targetId = pendingExploreTargetId;
            if (string.IsNullOrEmpty(targetId) && session != null && session.State.PendingChoice != null)
            {
                targetId = session.State.PendingChoice.TargetId;
            }

            var cardInfo = card == null
                ? string.Empty
                : GetEventColorDisplayName(card.Color) + "    " +
                  GetResourceTypeDisplayName(card.ResourceType) + " * " + card.ResourceAmount;

            return string.IsNullOrEmpty(targetId)
                ? cardInfo
                : "\u6240\u5c5e\u8d44\u6e90\u70b9\uff1a" + targetId + "    " + cardInfo;
        }

        private static string GetEventColorDisplayName(EventColor color)
        {
            switch (color)
            {
                case EventColor.Green:
                    return "\u7eff\u8272\u533a\u57df";
                case EventColor.Yellow:
                    return "\u9ec4\u8272\u533a\u57df";
                case EventColor.Red:
                    return "\u7ea2\u8272\u533a\u57df";
                default:
                    return color.ToString();
            }
        }

        private static string GetResourceTypeDisplayName(ResourceType resourceType)
        {
            switch (resourceType)
            {
                case ResourceType.Originium:
                    return "\u6e90\u5ca9";
                case ResourceType.OriginiumShard:
                    return "\u6e90\u77f3\u788e\u7247";
                case ResourceType.Iron:
                    return "\u5f02\u94c1";
                case ResourceType.PureOriginium:
                    return "\u81f3\u7eaf\u6e90\u77f3";
                case ResourceType.GoldVoucher:
                    return "\u91d1\u5238";
                default:
                    return resourceType.ToString();
            }
        }

        private void BuildSession()
        {
            mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());

            var launchContext = GameLaunchContext.Instance;
            var launchMode = launchContext == null ? LaunchMode.Local : launchContext.Mode;
            var eventDeckSeed = GetEventDeckSeed(launchContext);
            eventDeckService = new EventDeckService(eventDeckSeed);
            if (IsNetworkLaunch(launchContext) && !LaunchContextContainsLocalPlayer(launchContext))
            {
                Debug.LogError("Network launch is missing a valid local player id. The local client will remain spectator-only.");
                localPlayerId = -1;
            }
            else if (launchContext != null && launchContext.LocalPlayerId > 0)
            {
                localPlayerId = launchContext.LocalPlayerId;
            }

            var state = GameLaunchStateFactory.CreateInitialState(
                launchMode,
                localPlayerId,
                launchContext == null ? null : launchContext.Players,
                mapQuery.Map.MapId,
                eventDeckSeed);

            eventDeckService.InitializeDecks(
                state.Decks,
                EventCardDatabase.GreenCardIds,
                EventCardDatabase.YellowCardIds,
                EventCardDatabase.RedCardIds);

            session = new GameSession(state);
            session.RegisterHandler(new SetupCommandHandler(
                mapQuery,
                eventDeckService,
                new ResourceTokenService(),
                new TurnOrderService()));
            session.RegisterHandler(new EndActionCommandHandler());
            influenceService = new InfluenceService(mapQuery);
            session.RegisterHandler(new DeployInfluenceCommandHandler(influenceService));
            session.RegisterHandler(new DispatchInfluenceCommandHandler(influenceService));
            var travelCostService = new TravelCostService(mapQuery);
            var movementService = new CityMovementService(mapQuery, influenceService, travelCostService);
            session.RegisterHandler(new MoveCityCommandHandler(movementService));
            explorationService = new ExplorationService(
                mapQuery,
                influenceService,
                eventDeckService,
                new ResourceTokenService());
            session.RegisterHandler(new ExploreLocationCommandHandler(explorationService));
        }

        private void BuildCommandTransport(GameLaunchContext launchContext)
        {
            if (launchContext == null || launchContext.Mode == LaunchMode.Local)
            {
                return;
            }

            try
            {
                commandTransport = UnityNetcodeCommandTransport.Ensure();
                commandTransport.InitialStateApplied += OnInitialNetworkStateApplied;
                commandTransport.ConfirmedCommandApplied += OnConfirmedNetworkCommandApplied;
                commandTransport.CommandRejected += OnNetworkCommandRejected;
                commandTransport.Initialize(session, launchContext.Mode, localPlayerId, launchContext.Players);
            }
            catch (Exception ex)
            {
                if (commandTransport != null)
                {
                    commandTransport.InitialStateApplied -= OnInitialNetworkStateApplied;
                    commandTransport.ConfirmedCommandApplied -= OnConfirmedNetworkCommandApplied;
                    commandTransport.CommandRejected -= OnNetworkCommandRejected;
                    commandTransport = null;
                }

                Debug.LogException(ex, this);
                SetPrompt("联网同步初始化失败，请返回房间重试。");
            }
        }

        private CommandResult SubmitGameCommand(GameCommand command, out bool appliedLocally)
        {
            if (commandTransport != null)
            {
                return commandTransport.SubmitOrSend(command, out appliedLocally);
            }

            if (IsNetworkLaunch(GameLaunchContext.Instance))
            {
                appliedLocally = false;
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "\u8054\u7f51\u540c\u6b65\u672a\u521d\u59cb\u5316"));
            }

            var result = session.Submit(command);
            appliedLocally = result.Succeeded;
            return result;
        }

        private void OnConfirmedNetworkCommandApplied(ConfirmedGameCommandDto confirmed)
        {
            RefreshAllFromState();
        }

        private void OnInitialNetworkStateApplied(InitialGameStateDto snapshot)
        {
            RefreshAllFromState();
        }

        private void OnNetworkCommandRejected(RejectedGameCommandDto rejected)
        {
            SetPrompt(NetworkCommandPromptFormatter.BuildRejectedCommandPrompt(rejected));
        }

        private void RefreshAllFromState()
        {
            SynchronizeLocalPlayerForHotseat();

            actionPanelMode = ActionPanelMode.ChooseAction;
            ClearPendingDispatch();
            HideEventCardOptions();

            var player = session.State.FindPlayer(localPlayerId);
            awaitingInitialPlacement = session.State.Phase == GamePhase.Entrance
                && (player == null || string.IsNullOrEmpty(player.CityLocationId));

            RefreshCityViewsFromState();
            RefreshResourceDisplay();
            RefreshInfluenceDisplay();
            RefreshActionPanel();
            RefreshRoundTrackerFromState();
            RefreshPendingChoiceOrHighlights();
        }

        private void RefreshPendingChoiceOrHighlights()
        {
            if (session.State.PendingChoice != null && session.State.PendingChoice.PlayerId == localPlayerId)
            {
                ClearHighlights();
                ShowPendingEventCardOptions();
                return;
            }

            if (awaitingInitialPlacement)
            {
                ShowInitialPlacementChoices();
                return;
            }

            ClearHighlights();
            UpdateEntranceOrActionPrompt();
        }

        private static int GetEventDeckSeed(GameLaunchContext launchContext)
        {
            if (launchContext == null || string.IsNullOrEmpty(launchContext.RoomId))
            {
                return EventDeckService.DefaultSeed;
            }

            return EventDeckService.CreateSeed(launchContext.RoomId);
        }

        private static int GetStartPlayerId(GameLaunchContext launchContext)
        {
            if (IsNetworkLaunch(launchContext))
            {
                return 1;
            }

            if (launchContext != null && launchContext.Players.Count > 0)
            {
                return launchContext.Players[0].PlayerId;
            }

            return 1;
        }

        private static bool IsNetworkLaunch(GameLaunchContext launchContext)
        {
            return launchContext != null && GameLaunchStateFactory.IsNetworkLaunch(launchContext.Mode);
        }

        private static bool LaunchContextContainsLocalPlayer(GameLaunchContext launchContext)
        {
            return launchContext != null &&
                   GameLaunchStateFactory.ContainsPlayer(launchContext.Players, launchContext.LocalPlayerId);
        }

        private static int GetFirstTurnPlayerId(GameState state)
        {
            var order = new TurnOrderService().GetTurnOrder(state);
            return order.Count > 0 ? order[0] : state.StartPlayerId;
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
        }

        private void BuildResourceTokenViews()
        {
            foreach (var pair in locationsById)
            {
                var tokenObject = new GameObject("ResourceToken " + pair.Key, typeof(SpriteRenderer));
                tokenObject.transform.SetParent(transform, false);

                var normalizedPosition = pair.Value.NormalizedPosition;
                tokenObject.transform.position = ToWorldPosition(
                    new Vector2(normalizedPosition.x - 0.034f, normalizedPosition.y - 0.045f), -0.18f);
                tokenObject.transform.localScale = new Vector3(0.38f, 0.38f, 1f);
                tokenObject.SetActive(false);

                var renderer = tokenObject.GetComponent<SpriteRenderer>();
                renderer.sortingOrder = 18;
                resourceTokenRenderers[pair.Key] = renderer;
            }

            RefreshResourceTokenDisplay();
        }

        private void BuildInfluenceSlotViews()
        {
            EnsureInfluenceSlotSprites();

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
                    renderer.sprite = emptyInfluenceSlotSprite;
                    renderer.color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
                    renderer.sortingOrder = 15;

                    var collider = slotObject.AddComponent<CircleCollider2D>();
                    collider.radius = 0.22f;
                    slotObject.AddComponent<InfluenceSlotClickTarget>().Initialize(
                        this,
                        InfluenceService.GetLocationSlotId(locationId, i));

                    slotRenderers.Add(renderer);
                }

                influenceSlotRenderers[locationId] = slotRenderers;
            }

            BuildRouteInfluenceSlotViews();
            RefreshInfluenceDisplay();
        }

        private void BuildRouteInfluenceSlotViews()
        {
            EnsureInfluenceSlotSprites();

            var definitions = FourPlayerRouteDisplayDefinitions.Create();
            var errors = MapRouteDisplayDefinitionValidator.Validate(mapQuery.Map, definitions);
            if (errors.Count > 0)
            {
                Debug.LogError("Route influence slot display configuration is invalid:\n" + string.Join("\n", errors), this);
                return;
            }

            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                var slotRenderers = new List<SpriteRenderer>();

                for (var slotIndex = 0; slotIndex < definition.InfluenceSlotPositions.Count; slotIndex++)
                {
                    var slotObject = new GameObject(
                        "RouteInfluenceSlot " + definition.RouteId + ":" + slotIndex,
                        typeof(SpriteRenderer));
                    slotObject.transform.SetParent(transform, false);
                    slotObject.transform.position = ToWorldPosition(
                        definition.InfluenceSlotPositions[slotIndex], -0.24f);

                    var renderer = slotObject.GetComponent<SpriteRenderer>();
                    renderer.sprite = emptyInfluenceSlotSprite;
                    renderer.color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
                    renderer.sortingOrder = 15;

                    var collider = slotObject.AddComponent<CircleCollider2D>();
                    collider.radius = 0.22f;
                    slotObject.AddComponent<InfluenceSlotClickTarget>().Initialize(
                        this,
                        InfluenceService.GetRouteSlotId(definition.RouteId, slotIndex));

                    slotRenderers.Add(renderer);
                }

                routeInfluenceSlotRenderers[definition.RouteId] = slotRenderers;
            }
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
                        renderers[i].sprite = occupiedInfluenceSlotSprite;
                        renderers[i].color = GetPlayerColor(placement.PlayerId, 0.85f);
                    }
                    else if (highlightedInfluenceSlotIds.Contains(slotId))
                    {
                        renderers[i].sprite = emptyInfluenceSlotSprite;
                        renderers[i].color = new Color(1f, 0.82f, 0.2f, 0.95f);
                    }
                    else
                    {
                        renderers[i].sprite = emptyInfluenceSlotSprite;
                        renderers[i].color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
                    }
                }
            }

            RefreshRouteInfluenceDisplay();
        }

        private void RefreshRouteInfluenceDisplay()
        {
            foreach (var pair in routeInfluenceSlotRenderers)
            {
                var routeId = pair.Key;
                var renderers = pair.Value;

                for (var i = 0; i < renderers.Count; i++)
                {
                    var slotId = InfluenceService.GetRouteSlotId(routeId, i);
                    var placement = influenceService.FindInfluence(session.State, slotId);

                    if (placement != null)
                    {
                        renderers[i].sprite = occupiedInfluenceSlotSprite;
                        renderers[i].color = GetPlayerColor(placement.PlayerId, 0.85f);
                    }
                    else if (highlightedInfluenceSlotIds.Contains(slotId))
                    {
                        renderers[i].sprite = emptyInfluenceSlotSprite;
                        renderers[i].color = new Color(1f, 0.82f, 0.2f, 0.95f);
                    }
                    else
                    {
                        renderers[i].sprite = emptyInfluenceSlotSprite;
                        renderers[i].color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
                    }
                }
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
        }

        private void BuildPromptUi()
        {
            UguiUtility.EnsureEventSystem();

            var canvasObject = new GameObject("Mobile City UI Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);

            uiCanvas = canvasObject.GetComponent<Canvas>();
            uiCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            uiCanvas.sortingOrder = 15;

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

        private void BuildActionPanel()
        {
            if (uiCanvas == null)
            {
                return;
            }

            var canvasTransform = uiCanvas.GetComponent<RectTransform>();
            actionPanelObject = new GameObject("Action Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            actionPanelObject.transform.SetParent(canvasTransform, false);

            var panelRect = actionPanelObject.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(1f, 0f);
            panelRect.anchorMax = new Vector2(1f, 0f);
            panelRect.pivot = new Vector2(1f, 0f);
            panelRect.sizeDelta = new Vector2(360f, 430f);
            panelRect.anchoredPosition = new Vector2(-24f, 118f);

            actionPanelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
            var outline = actionPanelObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(3f, -3f);

            actionCurrentPlayerText = CreateActionPanelText(panelRect, "当前玩家", 20, new Vector2(0f, -26f), FontStyle.Bold);
            actionPhaseText = CreateActionPanelText(panelRect, "阶段", 16, new Vector2(0f, -58f), FontStyle.Normal);

            CreateActionPanelText(panelRect, "快速行动", 16, new Vector2(0f, -98f), FontStyle.Bold);
            useCharacterButton = CreateActionButton(panelRect, "使用角色牌", new Vector2(-86f, -134f), OnUseCharacterActionClicked);
            declareCityStyleButton = CreateActionButton(panelRect, "宣告样式", new Vector2(86f, -134f), OnDeclareCityStyleClicked);

            CreateActionPanelText(panelRect, "主要行动", 16, new Vector2(0f, -180f), FontStyle.Bold);
            deployButton = CreateActionButton(panelRect, "部署", new Vector2(-86f, -216f), BeginDeployAction);
            dispatchButton = CreateActionButton(panelRect, "调度", new Vector2(86f, -216f), BeginDispatchAction);
            exploreButton = CreateActionButton(panelRect, "探索", new Vector2(-86f, -270f), BeginExploreAction);
            moveCityButton = CreateActionButton(panelRect, "城市移动", new Vector2(86f, -270f), BeginMoveAction);
            buildButton = CreateActionButton(panelRect, "建设", new Vector2(-86f, -324f), OnBuildActionClicked);
            specialActionButton = CreateActionButton(panelRect, "特殊行动", new Vector2(86f, -324f), OnSpecialActionClicked);

            actionStatusText = CreateActionPanelText(panelRect, "状态", 15, new Vector2(0f, -382f), FontStyle.Normal);
            actionStatusText.resizeTextForBestFit = true;
            actionStatusText.resizeTextMinSize = 11;
            actionStatusText.resizeTextMaxSize = 15;

            RefreshActionPanel();
        }

        private static Text CreateActionPanelText(RectTransform parent, string value, int size, Vector2 position, FontStyle style)
        {
            var textObject = new GameObject(value + " Text", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);

            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(316f, 30f);
            rect.anchoredPosition = position;

            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = UiTheme.GoldText;
            text.fontSize = size;
            text.fontStyle = style;
            text.font = FontUtility.GetCjkFont(size);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static Button CreateActionButton(
            RectTransform parent,
            string label,
            Vector2 position,
            UnityEngine.Events.UnityAction action)
        {
            var buttonObject = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(150f, 42f);
            rect.anchoredPosition = position;

            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);

            var button = buttonObject.GetComponent<Button>();
            button.onClick.AddListener(action);

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(buttonObject.transform, false);

            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(8f, 0f);
            labelRect.offsetMax = new Vector2(-8f, 0f);

            var text = labelObject.GetComponent<Text>();
            text.text = label;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = UiTheme.GoldText;
            text.fontSize = 16;
            text.fontStyle = FontStyle.Bold;
            text.font = FontUtility.GetCjkFont(16);
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 12;
            text.resizeTextMaxSize = 16;
            return button;
        }

        private void RefreshActionPanel()
        {
            SynchronizeLocalPlayerForHotseat();

            if (actionPanelObject == null || session == null)
            {
                return;
            }

            var state = session.State;
            var player = state.FindPlayer(localPlayerId);
            var isActionPhase = state.Phase == GamePhase.ActionRound1 || state.Phase == GamePhase.ActionRound2;
            var isLocalTurn = IsLocalPlayersTurn();
            var hasPendingChoice = state.HasPendingChoice();
            var mainActionDone = player != null && player.ActedMainActionThisTurn;
            var canChooseAction = isActionPhase && isLocalTurn && !hasPendingChoice && !mainActionDone;
            var pendingChoiceBelongsToLocalPlayer = hasPendingChoice &&
                                                    state.PendingChoice.PlayerId == localPlayerId;

            if (hasPendingChoice)
            {
                actionPanelMode = ActionPanelMode.PendingChoice;
                if (pendingChoiceBelongsToLocalPlayer && eventChoiceOverlay == null)
                {
                    ShowPendingEventCardOptions();
                }
            }
            else if (isActionPhase && (!isLocalTurn || mainActionDone))
            {
                actionPanelMode = ActionPanelMode.WaitingForNextPlayer;
            }
            else if (isActionPhase &&
                     canChooseAction &&
                     (actionPanelMode == ActionPanelMode.Hidden ||
                      actionPanelMode == ActionPanelMode.PendingChoice ||
                      actionPanelMode == ActionPanelMode.WaitingForNextPlayer))
            {
                actionPanelMode = ActionPanelMode.ChooseAction;
            }

            actionCurrentPlayerText.text = "本机：" + GetPlayerDisplayName(localPlayerId) + " / 行动：" + GetPlayerDisplayName(state.CurrentPlayerId);
            actionPhaseText.text = "第 " + state.Round + " 回合 / 行动轮 " + state.ActionRound;

            SetButtonInteractable(useCharacterButton, canChooseAction && player != null && !player.UsedCharacterThisRound);
            SetButtonInteractable(declareCityStyleButton, canChooseAction);
            SetButtonInteractable(deployButton, canChooseAction);
            SetButtonInteractable(dispatchButton, canChooseAction);
            SetButtonInteractable(exploreButton, canChooseAction);
            SetButtonInteractable(moveCityButton, canChooseAction && player != null && !player.HasMovedCityThisRound);
            SetButtonInteractable(buildButton, canChooseAction);
            SetButtonInteractable(specialActionButton, canChooseAction && HasAvailableSpecialAction(player));

            actionStatusText.text = GetActionPanelStatus(state, player, isActionPhase, isLocalTurn, hasPendingChoice, mainActionDone);
        }

        private string GetActionPanelStatus(
            GameState state,
            PlayerState player,
            bool isActionPhase,
            bool isLocalTurn,
            bool hasPendingChoice,
            bool mainActionDone)
        {
            if (state.Phase == GamePhase.Entrance)
            {
                return "入场阶段：选择初始移动城市位置";
            }

            if (!isActionPhase)
            {
                return "当前阶段不能执行行动";
            }

            if (hasPendingChoice)
            {
                return state.PendingChoice != null && state.PendingChoice.PlayerId != localPlayerId
                    ? "等待玩家 " + state.PendingChoice.PlayerId + " 处理事件选择"
                    : "请先处理事件选择";
            }

            if (!isLocalTurn)
            {
                return "等待玩家 " + state.CurrentPlayerId + " 行动";
            }

            if (mainActionDone)
            {
                return "主要行动已完成，请点击结束本回合";
            }

            switch (actionPanelMode)
            {
                case ActionPanelMode.ResolvingMoveTarget:
                    return "城市移动：选择高亮资源点";
                case ActionPanelMode.ResolvingExploreTarget:
                    return "探索：选择高亮资源点";
                case ActionPanelMode.ResolvingDeployTarget:
                    return "部署：选择影响力空格";
                case ActionPanelMode.ResolvingDispatchSource:
                    return "调度：选择来源影响力";
                case ActionPanelMode.ResolvingDispatchTarget:
                    return "调度：选择目标资源点";
                default:
                    return player == null ? "未知玩家" : "尚未执行主要行动";
            }
        }

        private void BeginMoveAction()
        {
            if (!CanStartActionSelection())
            {
                return;
            }

            var player = session.State.FindPlayer(localPlayerId);
            if (player == null || string.IsNullOrEmpty(player.CityLocationId))
            {
                SetPrompt("玩家城市不在场上。");
                return;
            }

            actionPanelMode = ActionPanelMode.ResolvingMoveTarget;
            ClearPendingDispatch();
            ClearHighlights();
            HighlightReachableLocations(player.CityLocationId);
            RefreshActionPanel();
            SetPrompt("城市移动：选择一个高亮资源点。");
        }

        private void BeginExploreAction()
        {
            if (!CanStartActionSelection())
            {
                return;
            }

            actionPanelMode = ActionPanelMode.ResolvingExploreTarget;
            ClearPendingDispatch();
            ClearHighlights();
            HighlightExplorableLocations();
            RefreshActionPanel();
            if (highlightedLocationIds.Count == 0)
            {
                SetPrompt("当前没有可探索资源点。");
                return;
            }

            SetPrompt("探索：选择一个高亮资源点。");
        }

        private void BeginDeployAction()
        {
            if (!CanStartActionSelection())
            {
                return;
            }

            actionPanelMode = ActionPanelMode.ResolvingDeployTarget;
            ClearPendingDispatch();
            ClearHighlights();
            HighlightDeployTargets(true);
            RefreshActionPanel();
            SetPrompt("选择一个影响力空格放置影响力");
        }

        private void BeginDispatchAction()
        {
            if (HasPendingDispatchFirstMove())
            {
                SubmitPendingDispatchCommand(string.Empty, string.Empty);
                return;
            }

            if (!CanStartActionSelection())
            {
                return;
            }

            actionPanelMode = ActionPanelMode.ResolvingDispatchSource;
            ClearPendingDispatch();
            ClearHighlights();
            HighlightOwnInfluenceLocations();
            RefreshActionPanel();
            SetPrompt("调度：先选择一个自己的影响力。");
        }

        private void OnUseCharacterActionClicked()
        {
            SetPrompt("使用角色牌暂未实现。");
        }

        private void OnDeclareCityStyleClicked()
        {
            SetPrompt("宣告城市样式暂未实现。");
        }

        private void OnBuildActionClicked()
        {
            SetPrompt("建设行动暂未实现。");
        }

        private void OnSpecialActionClicked()
        {
            SetPrompt("特殊行动暂未实现。");
        }

        private bool CanStartActionSelection()
        {
            SynchronizeLocalPlayerForHotseat();

            var player = session.State.FindPlayer(localPlayerId);
            if (player == null)
            {
                SetPrompt("当前玩家不存在。");
                return false;
            }

            if (!IsLocalPlayersTurn())
            {
                SetPrompt("等待玩家 " + session.State.CurrentPlayerId + " 行动。");
                return false;
            }

            if (session.State.HasPendingChoice())
            {
                SetPrompt("请先处理待选择项。");
                return false;
            }

            if (player.ActedMainActionThisTurn)
            {
                SetPrompt("当前玩家已经执行过主要行动。");
                return false;
            }

            if (session.State.Phase != GamePhase.ActionRound1 && session.State.Phase != GamePhase.ActionRound2)
            {
                SetPrompt("当前阶段不能执行行动。");
                return false;
            }

            return true;
        }

        private static void SetButtonInteractable(Button button, bool interactable)
        {
            if (button == null)
            {
                return;
            }

            button.interactable = interactable;
        }

        private static bool HasAvailableSpecialAction(PlayerState player)
        {
            return player != null && player.UsedSpecialActionIdsThisRound.Count == 0;
        }

        private string GetPlayerDisplayName(int playerId)
        {
            var player = session.State.FindPlayer(playerId);
            if (player == null)
            {
                return playerId.ToString();
            }

            return string.IsNullOrEmpty(player.Name) ? "Player " + playerId : player.Name;
        }

        private void RefreshResourceDisplay()
        {
            RefreshInfoPanel();
            RefreshResourceTokenDisplay();
        }

        private void RefreshResourceTokenDisplay()
        {
            foreach (var pair in resourceTokenRenderers)
            {
                pair.Value.gameObject.SetActive(false);
            }

            if (session == null || session.State == null || session.State.Map == null)
            {
                return;
            }

            for (var i = 0; i < session.State.Map.ResourceTokens.Count; i++)
            {
                var token = session.State.Map.ResourceTokens[i];
                if (!resourceTokenRenderers.TryGetValue(token.LocationId, out var renderer))
                {
                    continue;
                }

                renderer.sprite = GetResourceTokenSprite(token.ResourceType);
                renderer.gameObject.SetActive(renderer.sprite != null);
            }
        }

        private Sprite GetResourceTokenSprite(ResourceType resourceType)
        {
            if (resourceTokenSprites.TryGetValue(resourceType, out var sprite))
            {
                return sprite;
            }

            sprite = LoadResourceTokenSprite(resourceType);
            resourceTokenSprites[resourceType] = sprite;
            return sprite;
        }

        private static Sprite LoadResourceTokenSprite(ResourceType resourceType)
        {
            var fileName = GetResourceTokenFileName(resourceType);
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            var projectRoot = Directory.GetParent(UnityEngine.Application.dataPath);
            if (projectRoot == null)
            {
                return null;
            }

            var path = Path.Combine(projectRoot.FullName, "游城拓荒", "素材", fileName);
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

        private static string GetResourceTokenFileName(ResourceType resourceType)
        {
            switch (resourceType)
            {
                case ResourceType.Originium:
                    return "土.png";
                case ResourceType.OriginiumShard:
                    return "源石碎片.png";
                case ResourceType.Iron:
                    return "异铁.png";
                case ResourceType.PureOriginium:
                    return "至纯源石.png";
                case ResourceType.GoldVoucher:
                    return "金券.png";
                default:
                    return string.Empty;
            }
        }

        private void ShowInitialPlacementChoices()
        {
            SynchronizeLocalPlayerForHotseat();
            ClearHighlights();
            awaitingInitialPlacement = IsAwaitingLocalInitialPlacement();

            if (awaitingInitialPlacement && IsLocalPlayersTurn())
            {
                foreach (var locationId in StaticMapDefinitions.FourPlayerInitialLocationIds)
                {
                    if (CanUseInitialPlacementLocation(locationId))
                    {
                        SetHighlighted(locationId, new Color(0.25f, 0.95f, 0.45f, 0.82f));
                    }
                }
            }

            UpdateEntranceOrActionPrompt();
            if (awaitingInitialPlacement && IsLocalPlayersTurn() && highlightedLocationIds.Count == 0)
            {
                SetPrompt("当前没有可放置移动城市的入场点。");
            }

            ApplyDebugHotspotHighlights();
        }

        private bool IsLocalPlayersTurn()
        {
            SynchronizeLocalPlayerForHotseat();
            return session == null || session.State.CurrentPlayerId == localPlayerId;
        }

        private void SynchronizeLocalPlayerForHotseat()
        {
            if (!ShouldControlCurrentPlayerLocally() ||
                session == null ||
                session.State == null ||
                session.State.CurrentPlayerId <= 0)
            {
                return;
            }

            localPlayerId = session.State.CurrentPlayerId;
        }

        private static bool ShouldControlCurrentPlayerLocally()
        {
            var launchContext = GameLaunchContext.Instance;
            return launchContext == null || launchContext.Mode == LaunchMode.Local;
        }

        private bool IsAwaitingLocalInitialPlacement()
        {
            if (session == null || session.State.Phase != GamePhase.Entrance)
            {
                return false;
            }

            var player = session.State.FindPlayer(localPlayerId);
            return player == null || string.IsNullOrEmpty(player.CityLocationId);
        }

        private bool CanUseInitialPlacementLocation(string locationId)
        {
            MapLocationDefinition location;
            try
            {
                location = mapQuery.GetLocation(locationId);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (!location.CanDockCity)
            {
                return false;
            }

            if (mapQuery.Map.MapId == StaticMapDefinitions.FourPlayerMapId &&
                !StaticMapDefinitions.FourPlayerInitialLocationIds.Contains(locationId))
            {
                return false;
            }

            for (var i = 0; i < session.State.Players.Count; i++)
            {
                var player = session.State.Players[i];
                if (player.PlayerId != localPlayerId && player.CityLocationId == locationId)
                {
                    return false;
                }
            }

            return true;
        }

        private void UpdateEntranceOrActionPrompt()
        {
            UpdateEntranceOrActionPrompt(string.Empty);
        }

        private void UpdateEntranceOrActionPrompt(string actionMessage)
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

            RefreshActionPanel();
            if (!string.IsNullOrEmpty(actionMessage))
            {
                SetPrompt(actionMessage);
                return;
            }

            SetPrompt("请从右下角行动面板选择主要行动。");
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

        private void HighlightExplorableLocations()
        {
            var player = session.State.FindPlayer(localPlayerId);
            if (player == null || string.IsNullOrEmpty(player.CityLocationId))
            {
                return;
            }

            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var location = mapQuery.Map.Locations[i];
                if (location.LocationId == player.CityLocationId ||
                    HasResourceToken(location.LocationId) ||
                    IsRedZoneClosed(location) ||
                    !CanBuildPath(player.CityLocationId, location.LocationId))
                {
                    continue;
                }

                SetHighlighted(location.LocationId, new Color(0.25f, 0.95f, 0.45f, 0.82f));
            }
        }

        private void HighlightDeployTargets(bool requireAvailableSupply)
        {
            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var locationId = mapQuery.Map.Locations[i].LocationId;
                HighlightPlaceableLocationSlots(locationId, requireAvailableSupply);
            }

            for (var i = 0; i < mapQuery.Map.Routes.Count; i++)
            {
                HighlightPlaceableRouteSlots(mapQuery.Map.Routes[i].RouteId, requireAvailableSupply);
            }

            RefreshInfluenceDisplay();
        }

        private void HighlightPlaceableLocationSlots(string locationId, bool requireAvailableSupply)
        {
            var location = mapQuery.GetLocation(locationId);
            for (var i = 0; i < location.InfluenceSlotCount; i++)
            {
                var slotId = InfluenceService.GetLocationSlotId(locationId, i);
                if (CanUseInfluenceSlot(slotId, requireAvailableSupply))
                {
                    highlightedInfluenceSlotIds.Add(slotId);
                }
            }
        }

        private void HighlightPlaceableRouteSlots(string routeId, bool requireAvailableSupply)
        {
            var route = mapQuery.GetRoute(routeId);
            for (var i = 0; i < route.InfluenceSlotCount; i++)
            {
                var slotId = InfluenceService.GetRouteSlotId(routeId, i);
                if (CanUseInfluenceSlot(slotId, requireAvailableSupply))
                {
                    highlightedInfluenceSlotIds.Add(slotId);
                }
            }
        }

        private void HighlightOwnInfluenceLocations()
        {
            for (var i = 0; i < GetStateInfluenceCount(); i++)
            {
                var influence = session.State.Map.Influences[i];
                if (influence.PlayerId != localPlayerId ||
                    string.IsNullOrEmpty(influence.LocationId) ||
                    influence.SlotId == pendingDispatchFirstSourceSlotId)
                {
                    continue;
                }

                SetHighlighted(influence.LocationId, new Color(0.86f, 0.75f, 0.2f, 0.8f));
            }
        }

        private void HighlightDispatchTargets(string sourceLocationId)
        {
            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var locationId = mapQuery.Map.Locations[i].LocationId;
                if (locationId == sourceLocationId)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(FindFirstPlaceableLocationSlot(locationId, false)))
                {
                    SetHighlighted(locationId, new Color(0.15f, 0.8f, 1f, 0.85f));
                }
            }
        }

        private int GetStateInfluenceCount()
        {
            return session == null || session.State == null || session.State.Map == null
                ? 0
                : session.State.Map.Influences.Count;
        }

        private string FindFirstPlaceableLocationSlot(string locationId, bool requireAvailableSupply)
        {
            if (!HasResourceToken(locationId))
            {
                return string.Empty;
            }

            if (HasOpponentCityAtLocation(locationId))
            {
                return string.Empty;
            }

            var location = mapQuery.GetLocation(locationId);
            for (var i = 0; i < location.InfluenceSlotCount; i++)
            {
                var slotId = InfluenceService.GetLocationSlotId(locationId, i);
                if (CanUseInfluenceSlot(slotId, requireAvailableSupply))
                {
                    return slotId;
                }
            }

            return string.Empty;
        }

        private bool CanUseInfluenceSlot(string slotId, bool requireAvailableSupply)
        {
            if (!requireAvailableSupply && HasPendingDispatchFirstMove())
            {
                return WithPendingDispatchFirstMove(() => CanUseInfluenceSlotWithoutPendingFirstMove(slotId, false));
            }

            return CanUseInfluenceSlotWithoutPendingFirstMove(slotId, requireAvailableSupply);
        }

        private bool CanUseInfluenceSlotWithoutPendingFirstMove(string slotId, bool requireAvailableSupply)
        {
            var validation = requireAvailableSupply
                ? influenceService.CanPlace(session.State, localPlayerId, slotId)
                : influenceService.CanMove(session.State, localPlayerId, pendingDispatchSourceSlotId, slotId);
            return validation.IsValid;
        }

        private bool WithPendingDispatchFirstMove(Func<bool> action)
        {
            var placement = influenceService.FindInfluence(session.State, pendingDispatchFirstSourceSlotId);
            if (placement == null)
            {
                return false;
            }

            var originalSlotId = placement.SlotId;
            var originalLocationId = placement.LocationId;
            var originalRouteId = placement.RouteId;
            var move = influenceService.Move(
                session.State,
                localPlayerId,
                pendingDispatchFirstSourceSlotId,
                pendingDispatchFirstTargetSlotId);
            if (!move.Succeeded)
            {
                return false;
            }

            try
            {
                return action();
            }
            finally
            {
                placement.SlotId = originalSlotId;
                placement.LocationId = originalLocationId;
                placement.RouteId = originalRouteId;
            }
        }

        private string FindFirstOwnLocationInfluenceSlot(string locationId)
        {
            for (var i = 0; i < session.State.Map.Influences.Count; i++)
            {
                var influence = session.State.Map.Influences[i];
                if (influence.PlayerId == localPlayerId &&
                    influence.LocationId == locationId &&
                    influence.SlotId != pendingDispatchFirstSourceSlotId)
                {
                    return influence.SlotId;
                }
            }

            return string.Empty;
        }

        private bool HasResourceToken(string locationId)
        {
            for (var i = 0; i < session.State.Map.ResourceTokens.Count; i++)
            {
                if (session.State.Map.ResourceTokens[i].LocationId == locationId)
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasOpponentCityAtLocation(string locationId)
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

        private bool HasRouteInfluenceOwnedBy(string routeId, int playerId)
        {
            for (var i = 0; i < session.State.Map.Influences.Count; i++)
            {
                var influence = session.State.Map.Influences[i];
                if (influence.PlayerId == playerId && IsInfluenceOnRoute(influence, routeId))
                {
                    return true;
                }
            }

            return false;
        }

        private List<int> GetOpponentInfluenceOwnersOnRoute(string routeId, int playerId)
        {
            var owners = new List<int>();
            for (var i = 0; i < session.State.Map.Influences.Count; i++)
            {
                var influence = session.State.Map.Influences[i];
                if (influence.PlayerId == playerId || !IsInfluenceOnRoute(influence, routeId))
                {
                    continue;
                }

                if (!owners.Contains(influence.PlayerId))
                {
                    owners.Add(influence.PlayerId);
                }
            }

            return owners;
        }

        private bool IsInfluenceOnRoute(InfluencePlacement influence, string routeId)
        {
            if (influence.RouteId == routeId)
            {
                return true;
            }

            InfluenceSlotReference slot;
            string reason;
            return InfluenceSlotReference.TryParse(mapQuery, influence.SlotId, out slot, out reason) &&
                   slot.Kind == InfluenceSlotKind.Route &&
                   slot.RouteId == routeId;
        }

        private bool IsRedZoneClosed(MapLocationDefinition location)
        {
            if (!location.IsRedZone)
            {
                return false;
            }

            var openRound = session.State.Players.Count <= 2 ? 6 : session.State.Players.Count == 3 ? 5 : 4;
            return session.State.Round < openRound;
        }

        private bool CanBuildPath(string sourceLocationId, string targetLocationId)
        {
            try
            {
                new MapPathSearchService(mapQuery).FindShortestPath(sourceLocationId, targetLocationId);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
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
            highlightedInfluenceSlotIds.Clear();
            foreach (var pair in hotspotsById)
            {
                pair.Value.SetColor(new Color(0.25f, 0.95f, 0.45f, 0f));
            }

            RefreshInfluenceDisplay();
            ApplyDebugHotspotHighlights();
        }

        private void RefreshCityViewsFromState()
        {
            foreach (var pair in cityObjectsByPlayerId)
            {
                pair.Value.SetActive(false);
            }

            if (session == null || session.State == null)
            {
                return;
            }

            for (var i = 0; i < session.State.Players.Count; i++)
            {
                var player = session.State.Players[i];
                if (string.IsNullOrEmpty(player.CityLocationId))
                {
                    continue;
                }

                MoveCityView(player.PlayerId, player.CityLocationId);
            }
        }

        private void RefreshRoundTrackerFromState()
        {
            var roundTracker = FindObjectOfType<RoundTrackerController>();
            if (roundTracker != null)
            {
                roundTracker.RefreshFromState(session.State);
            }
        }

        private void MoveCityView(string locationId)
        {
            MoveCityView(localPlayerId, locationId);
        }

        private void MoveCityView(int playerId, string locationId)
        {
            if (!locationsById.TryGetValue(locationId, out var view))
            {
                return;
            }

            var cityObject = EnsureCityObject(playerId);
            cityObject.transform.position = ToWorldPosition(view.NormalizedPosition, -0.4f) + new Vector3(0f, 0.38f, 0f);
            cityObject.SetActive(true);
        }

        private GameObject EnsureCityObject(int playerId)
        {
            GameObject cityObject;
            if (cityObjectsByPlayerId.TryGetValue(playerId, out cityObject))
            {
                return cityObject;
            }

            cityObject = new GameObject("Mobile City P" + playerId, typeof(SpriteRenderer), typeof(BoxCollider2D), typeof(MobileCityClickTarget));
            cityObject.transform.SetParent(transform, false);
            cityObject.SetActive(false);

            var cityRenderer = cityObject.GetComponent<SpriteRenderer>();
            cityRenderer.sprite = CreateCitySprite();
            cityRenderer.color = GetPlayerCityColor(playerId);
            cityRenderer.sortingOrder = 20 + playerId;

            var cityCollider = cityObject.GetComponent<BoxCollider2D>();
            cityCollider.size = new Vector2(0.95f, 1.35f);
            cityCollider.enabled = playerId == localPlayerId;

            cityObject.GetComponent<MobileCityClickTarget>().Initialize(this);
            cityObjectsByPlayerId[playerId] = cityObject;
            return cityObject;
        }

        private Color GetPlayerCityColor(int playerId)
        {
            return GetPlayerColor(playerId, 1f);
        }

        private Color GetPlayerColor(int playerId, float alpha)
        {
            var player = session == null || session.State == null ? null : session.State.FindPlayer(playerId);
            if (player == null)
            {
                return Color.white;
            }

            return GetPlayerColor(player.Color, alpha);
        }

        private static Color GetPlayerColor(PlayerColor playerColor, float alpha)
        {
            switch (playerColor)
            {
                case PlayerColor.Red:
                    return new Color(0.7019608f, 0f, 0.1137255f, alpha);
                case PlayerColor.Blue:
                    return new Color(0.003921569f, 0.2705882f, 0.6980392f, alpha);
                case PlayerColor.Green:
                    return new Color(0.3764706f, 0.8235294f, 0.003921569f, alpha);
                case PlayerColor.Yellow:
                    return new Color(1f, 0.7450981f, 0f, alpha);
                default:
                    return Color.white;
            }
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

        private sealed class ExplorePathChoice
        {
            public readonly MapPath Path;
            public readonly string Label;

            public ExplorePathChoice(MapPath path, string label)
            {
                Path = path;
                Label = label ?? string.Empty;
            }
        }

        private sealed class ExplorePaymentChoice
        {
            public readonly string RouteId;
            public readonly List<int> RecipientPlayerIds;

            public ExplorePaymentChoice(string routeId, List<int> recipientPlayerIds)
            {
                RouteId = routeId ?? string.Empty;
                RecipientPlayerIds = recipientPlayerIds ?? new List<int>();
            }
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

    public sealed class InfluenceSlotClickTarget : MonoBehaviour
    {
        private MobileCityInteractionController controller;
        private string slotId = string.Empty;

        public void Initialize(MobileCityInteractionController owner, string influenceSlotId)
        {
            controller = owner;
            slotId = influenceSlotId ?? string.Empty;
        }

        private void OnMouseDown()
        {
            controller.OnInfluenceSlotClicked(slotId);
        }
    }
}
