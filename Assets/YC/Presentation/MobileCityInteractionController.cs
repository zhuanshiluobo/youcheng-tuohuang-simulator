using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.CardFlows;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Exploration;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Maps;
using UnityEngine;
using UnityEngine.EventSystems;
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
            ResolvingDispatchDecision,
            ResolvingEventInfluenceTarget,
            ResolvingResourceCollection,
            PendingChoice,
            WaitingForNextPlayer
        }

        [SerializeField] private SpriteRenderer mapRenderer;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool debugClicks;

        private GameSession session;
        private MapQueryService mapQuery;
        private InfluenceService influenceService;
        private ExplorationService explorationService;
        private CommandSubmissionController commandSubmission;
        private ExpandableInfoPanel infoPanel;
        private BuildInfoPanel buildInfoPanel;
        private readonly EventChoiceDialog eventChoiceDialog = new EventChoiceDialog();
        private readonly EventOptionSelectionController eventOptionSelection = new EventOptionSelectionController();
        private readonly EventInfluenceTargetSelectionController eventInfluenceTargetSelection = new EventInfluenceTargetSelectionController();
        private readonly ExplorePaymentRecipientSelectionController explorePaymentRecipientSelection = new ExplorePaymentRecipientSelectionController();
        private readonly BuildFacilitySelectionController buildFacilitySelection = new BuildFacilitySelectionController();
        private readonly CityStyleSelectionController cityStyleSelection = new CityStyleSelectionController();
        private MapViewPresenter mapView;
        private GameObject dispatchDecisionOverlay;
        private Canvas uiCanvas;
        private ActionPanelController actionPanel;
        private PromptPresenter promptPresenter;
        private string completedMainActionName = string.Empty;
        private string pendingExploreTargetId = string.Empty;
        private MapPath pendingExplorePath;
        private readonly List<ExplorePathChoice> pendingExplorePathChoices = new List<ExplorePathChoice>();
        private readonly HashSet<string> collectionCandidateLocationIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> collectionSelectedLocationIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> collectionDeselectedLocationIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> collectionRouteIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> collectionPaidRouteIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> collectionPaymentRecipients = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, MapPath> collectionPathsByLocationId = new Dictionary<string, MapPath>(StringComparer.Ordinal);
        private ActionPanelMode actionPanelMode = ActionPanelMode.Hidden;
        private bool awaitingInitialPlacement = true;
        private string pendingDispatchSourceSlotId = string.Empty;
        private string pendingDispatchFirstSourceSlotId = string.Empty;
        private string pendingDispatchFirstTargetSlotId = string.Empty;
        private string pendingConfirmationActionKey = string.Empty;
        private string pendingConfirmationTargetId = string.Empty;
        private string pendingConfirmationLocationId = string.Empty;
        private string pendingConfirmationSlotId = string.Empty;
        private Action pendingConfirmationCallback;
        private int localPlayerId = 1;
        private int lastDebugCoordinateLogFrame = -1;
        private int lastEventInfluenceTargetSelectionFrame = -1;

        public GameState CurrentState
        {
            get { return session == null ? null : session.State; }
        }

        private PendingCardChoiceView CurrentPendingChoice
        {
            get { return session == null ? null : CardFlowStateAdapter.GetPendingChoiceView(session.State); }
        }

        public bool CanEndCurrentAction()
        {
            SynchronizeLocalPlayerForHotseat();

            if (session == null ||
                session.State == null ||
                session.State.HasPendingChoice())
            {
                return false;
            }

            var player = session.State.FindPlayer(localPlayerId);
            if (session.State.Phase == GamePhase.ResourceCollection)
            {
                return player != null && !player.HasCollectedResourcesThisRound;
            }

            if (session.State.Phase == GamePhase.Cleanup)
            {
                return player != null &&
                       (localPlayerId == session.State.StartPlayerId || localPlayerId == session.State.CurrentPlayerId);
            }

            if (!IsLocalPlayersTurn())
            {
                return false;
            }

            if (session.State.Phase != GamePhase.ActionRound1 && session.State.Phase != GamePhase.ActionRound2)
            {
                return false;
            }

            return player != null && player.ActedMainActionThisTurn;
        }

        public void EndCurrentAction()
        {
            if (!CanEndCurrentAction())
            {
                SetPrompt(GetUnavailableEndActionPrompt());
                return;
            }

            if (session.State.Phase == GamePhase.ResourceCollection)
            {
                SubmitResourceCollection();
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
            SetPrompt(session.State.Phase == GamePhase.ResourceCollection
                ? BuildResourceCollectionStatus()
                : BuildEndActionPrompt(session.State));
        }

        private string GetUnavailableEndActionPrompt()
        {
            if (session == null || session.State == null)
            {
                return "当前没有可结束的回合。";
            }

            if (session.State.Phase == GamePhase.ResourceCollection)
            {
                return "当前玩家已经提交过采集，等待其他玩家。";
            }

            if (session.State.Phase == GamePhase.Cleanup)
            {
                return "当前只有起始玩家可以结束收尾阶段。";
            }

            return "完成主要行动后才能结束本回合。";
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

            BuildSession();
            mapView = new MapViewPresenter(this, transform, mapRenderer, mapQuery, influenceService);
            mapView.BuildViews();
            RefreshResourceTokenDisplay();
            RefreshInfluenceDisplay();
            BuildPromptPresenter();
            BuildActionPanel();
            EnsureInfoPanel();
            EnsureBuildInfoPanel();
            EnsureSettingsMenu();
            ShowInitialPlacementChoices();
            BuildCommandSubmission(GameLaunchContext.Instance);
        }

        private void Update()
        {
            UpdatePromptAnimation();
            UpdatePendingConfirmationCancellation();

            if (!IsShiftDebugClick())
            {
                return;
            }

            LogPointerMapCoordinate();
        }

        private void OnDestroy()
        {
            if (commandSubmission != null)
            {
                commandSubmission.Dispose();
                commandSubmission = null;
            }
        }

        private static string BuildEndActionPrompt(GameState state)
        {
            if (state != null && state.Phase == GamePhase.ResourceCollection)
            {
                return "采集已提交，等待其他玩家完成采集。";
            }

            if (state != null && state.Phase == GamePhase.Cleanup)
            {
                return "采集结算完成。请结束收尾阶段进入下一回合。";
            }

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

            if (mapView == null)
            {
                CancelPendingConfirmation(true);
                return;
            }

            if (actionPanelMode == ActionPanelMode.ResolvingEventInfluenceTarget ||
                eventInfluenceTargetSelection.IsSelecting)
            {
                if (lastEventInfluenceTargetSelectionFrame == Time.frameCount)
                {
                    return;
                }

                if (SelectFirstEventInfluenceLocationSlot(locationId))
                {
                    MarkEventInfluenceTargetSelectionFrame();
                }

                return;
            }

            if (!mapView.ContainsHighlightedLocation(locationId))
            {
                CancelPendingConfirmation(true);
                return;
            }

            if (actionPanelMode == ActionPanelMode.ResolvingResourceCollection)
            {
                ToggleCollectionLocationSelection(locationId);
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

            if (mapView == null)
            {
                CancelPendingConfirmation(true);
                return;
            }

            if (actionPanelMode == ActionPanelMode.ResolvingEventInfluenceTarget ||
                eventInfluenceTargetSelection.IsSelecting)
            {
                if (!mapView.ContainsHighlightedInfluenceSlot(slotId))
                {
                    SetPrompt("请选择高亮的影响力槽位。");
                    return;
                }

                if (!TryMarkEventInfluenceTargetSelectionFrame())
                {
                    return;
                }

                SelectEventInfluenceSlot(slotId);
                return;
            }

            if (!mapView.ContainsHighlightedInfluenceSlot(slotId))
            {
                CancelPendingConfirmation(true);
                return;
            }

            switch (actionPanelMode)
            {
                case ActionPanelMode.ResolvingResourceCollection:
                    SelectCollectionRoutePayment(slotId);
                    break;
                case ActionPanelMode.ResolvingDeployTarget:
                    TryDeployInfluenceToSlot(slotId);
                    break;
                case ActionPanelMode.ResolvingDispatchSource:
                    SelectDispatchSourceSlot(slotId);
                    break;
                case ActionPanelMode.ResolvingDispatchTarget:
                    TryDispatchInfluenceToSlot(slotId);
                    break;
            }
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
            if (!RequestSecondClickConfirmation(
                    "MoveCity",
                    locationId,
                    "城市移动：预计花费" + GetCityMoveOriginiumShardCost() + "源石碎片",
                    () => ExecuteMoveCity(locationId),
                    locationId,
                    string.Empty))
            {
                return;
            }
        }

        private void ExecuteMoveCity(string locationId)
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
            RefreshResourceDisplay();
            RefreshInfluenceDisplay();
            if (CurrentPendingChoice != null)
            {
                RefreshActionPanel();
                ShowPendingEventCardOptions();
                return;
            }
            CompleteActionCommandUi("城市移动");
        }

        private void TryExploreLocation(string locationId)
        {
            int estimatedCost;
            string errorPrompt;
            if (!TryEstimateExploreGoldVoucherCost(locationId, out estimatedCost, out errorPrompt))
            {
                SetPrompt(errorPrompt);
                return;
            }

            if (!RequestSecondClickConfirmation(
                    "Explore",
                    locationId,
                    "探索：预计花费" + estimatedCost + "金券",
                    () => BeginExploreChoiceFromLocation(locationId),
                    locationId,
                    string.Empty))
            {
                return;
            }
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
            var player = session.State.FindPlayer(localPlayerId);
            var remainingInfluence = player == null ? 0 : Math.Max(0, player.InfluenceSupply - 1);
            if (!RequestSecondClickConfirmation(
                    "DeployInfluence",
                    slotId,
                    "再次点击确认放置   影响力剩余：" + remainingInfluence ,
                    () => ExecuteDeployInfluenceToSlot(slotId),
                    GetLocationIdForSlot(slotId),
                    slotId))
            {
                return;
            }
        }

        private void ExecuteDeployInfluenceToSlot(string slotId)
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

            CompleteActionCommandUi("部署");
        }

        private void SelectDispatchSource(string locationId)
        {
            var slotId = FindFirstOwnLocationInfluenceSlot(locationId);
            if (string.IsNullOrEmpty(slotId))
            {
                SetPrompt("请选择一个自己的影响力作为调度来源。");
                return;
            }

            SelectDispatchSourceSlot(slotId);
        }

        private void SelectDispatchSourceSlot(string slotId)
        {
            ClearPendingConfirmation(false);

            var placement = influenceService.FindInfluence(session.State, slotId);
            if (placement == null ||
                placement.PlayerId != localPlayerId ||
                placement.SlotId == pendingDispatchFirstSourceSlotId)
            {
                SetPrompt("请选择一个自己的影响力作为调度来源。");
                return;
            }

            if (!CanMoveInfluenceFromSource(placement.SlotId))
            {
                SetPrompt("该影响力当前没有可调度的目标。");
                return;
            }

            pendingDispatchSourceSlotId = placement.SlotId;
            actionPanelMode = ActionPanelMode.ResolvingDispatchTarget;
            ClearHighlights();
            HighlightDispatchTargets(placement.SlotId);
            RefreshActionPanel();
            SetPrompt("请选择调度目标槽位。");
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

            TryDispatchInfluenceToSlot(targetSlotId);
        }

        private void TryDispatchInfluenceToSlot(string targetSlotId)
        {
            if (string.IsNullOrEmpty(targetSlotId))
            {
                SetPrompt("该资源点没有可调度进入的影响力空格。");
                return;
            }

            if (!RequestSecondClickConfirmation(
                    "DispatchInfluence",
                    targetSlotId,
                    "确认移动？",
                    () => ExecuteDispatchInfluenceToSlot(targetSlotId),
                    GetLocationIdForSlot(targetSlotId),
                    targetSlotId))
            {
                return;
            }
        }

        private void ExecuteDispatchInfluenceToSlot(string targetSlotId)
        {
            if (HasPendingDispatchFirstMove())
            {
                SubmitPendingDispatchCommand(pendingDispatchSourceSlotId, targetSlotId);
                return;
            }

            pendingDispatchFirstSourceSlotId = pendingDispatchSourceSlotId;
            pendingDispatchFirstTargetSlotId = targetSlotId;
            pendingDispatchSourceSlotId = string.Empty;
            actionPanelMode = ActionPanelMode.ResolvingDispatchDecision;
            ClearHighlights();
            RefreshInfluenceDisplay();
            ShowDispatchDecisionDialog();
            RefreshActionPanel();
            SetPrompt("已预览本次调度。请选择再次调度，或取消以结束调度。");
        }

        private void SubmitPendingDispatchCommand(string secondSourceSlotId, string secondTargetSlotId)
        {
            if (!HasPendingDispatchFirstMove())
            {
                return;
            }

            HideDispatchDecisionDialog();

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

            CompleteActionCommandUi("调度");
        }

        private void CompleteActionCommandUi(string actionName)
        {
            completedMainActionName = actionName ?? string.Empty;
            actionPanelMode = ActionPanelMode.ChooseAction;
            ClearPendingConfirmation(false);
            ClearPendingDispatch();
            ClearPendingEventInfluenceSelection();
            ClearHighlights();
            RefreshResourceDisplay();
            RefreshInfluenceDisplay();
            RefreshActionPanel();
            RefreshRoundTrackerFromState();
            UpdateEntranceOrActionPrompt(BuildCompletedMainActionMessage(completedMainActionName));
        }

        private static string BuildCompletedMainActionMessage(string actionName)
        {
            return string.IsNullOrEmpty(actionName)
                ? "主要行动已完成"
                : actionName + "已完成";
        }

        private void ClearPendingDispatch()
        {
            HideDispatchDecisionDialog();
            ClearPendingConfirmation(false);
            pendingDispatchSourceSlotId = string.Empty;
            pendingDispatchFirstSourceSlotId = string.Empty;
            pendingDispatchFirstTargetSlotId = string.Empty;
        }

        private bool HasPendingDispatchFirstMove()
        {
            return !string.IsNullOrEmpty(pendingDispatchFirstSourceSlotId) &&
                   !string.IsNullOrEmpty(pendingDispatchFirstTargetSlotId);
        }

        private void ShowDispatchDecisionDialog()
        {
            var canvasTransform = GetUiCanvasTransform();
            if (canvasTransform == null)
            {
                return;
            }

            HideDispatchDecisionDialog();

            dispatchDecisionOverlay = new GameObject("Dispatch Decision Overlay", typeof(RectTransform), typeof(Image));
            dispatchDecisionOverlay.transform.SetParent(canvasTransform, false);

            var overlayRect = dispatchDecisionOverlay.GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;
            dispatchDecisionOverlay.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);

            var panel = new GameObject("Dispatch Decision Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panel.transform.SetParent(overlayRect, false);

            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(520f, 190f);
            panelRect.anchoredPosition = new Vector2(0f, -20f);

            panel.GetComponent<Image>().color = UiTheme.PanelBackground;
            var outline = panel.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(3f, -3f);

            CreateDispatchDecisionText(panelRect, "调度已移动到目标槽位", 20, FontStyle.Bold, new Vector2(0f, -42f));
            CreateDispatchDecisionText(panelRect, "可以再调度一个影响力，或取消并结束本次调度。", 15, FontStyle.Normal, new Vector2(0f, -78f));
            CreateDispatchDecisionButton(panelRect, "再次调度", new Vector2(-120f, -130f), ContinuePendingDispatch);
            CreateDispatchDecisionButton(panelRect, "取消", new Vector2(120f, -130f), FinishPendingDispatch);
        }

        private void ContinuePendingDispatch()
        {
            if (!HasPendingDispatchFirstMove())
            {
                HideDispatchDecisionDialog();
                return;
            }

            HideDispatchDecisionDialog();
            actionPanelMode = ActionPanelMode.ResolvingDispatchSource;
            ClearPendingConfirmation(false);
            ClearHighlights();
            HighlightOwnInfluenceLocations();
            RefreshActionPanel();
            SetPrompt("调度：请选择第二个影响力。");
        }

        private void FinishPendingDispatch()
        {
            if (!HasPendingDispatchFirstMove())
            {
                HideDispatchDecisionDialog();
                return;
            }

            SubmitPendingDispatchCommand(string.Empty, string.Empty);
        }

        private void HideDispatchDecisionDialog()
        {
            if (dispatchDecisionOverlay != null)
            {
                Destroy(dispatchDecisionOverlay);
                dispatchDecisionOverlay = null;
            }
        }

        private static Text CreateDispatchDecisionText(
            RectTransform parent,
            string value,
            int fontSize,
            FontStyle fontStyle,
            Vector2 position)
        {
            var textObject = new GameObject("Dispatch Decision Text", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);

            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(460f, 30f);
            rect.anchoredPosition = position;

            var text = textObject.GetComponent<Text>();
            text.text = value ?? string.Empty;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = UiTheme.GoldText;
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.font = FontUtility.GetCjkFont(fontSize);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 12;
            text.resizeTextMaxSize = fontSize;
            return text;
        }

        private static Button CreateDispatchDecisionButton(
            RectTransform parent,
            string label,
            Vector2 position,
            Action action)
        {
            var buttonObject = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(170f, 44f);
            rect.anchoredPosition = position;

            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);

            var button = buttonObject.GetComponent<Button>();
            if (action != null)
            {
                button.onClick.AddListener(() => action());
            }

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(buttonObject.transform, false);

            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(10f, 0f);
            labelRect.offsetMax = new Vector2(-10f, 0f);

            var text = labelObject.GetComponent<Text>();
            text.text = label ?? string.Empty;
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

        private void EnsureBuildInfoPanel()
        {
            buildInfoPanel = FindObjectOfType<BuildInfoPanel>();
            if (buildInfoPanel == null)
            {
                var go = new GameObject("BuildInfoPanel");
                go.transform.SetParent(transform, false);
                buildInfoPanel = go.AddComponent<BuildInfoPanel>();
            }

            buildInfoPanel.Initialize(buildInfoPanel.transform);
            buildInfoPanel.FacilityClicked += OnBuildInfoFacilityClicked;
            buildInfoPanel.CityStyleClicked += OnBuildInfoCityStyleClicked;
            buildInfoPanel.CityBoardSlotClicked += OnBuildInfoCityBoardSlotClicked;
            RefreshBuildInfoPanel();
        }

        private void EnsureSettingsMenu()
        {
            if (FindObjectOfType<GameSettingsMenuController>() != null)
            {
                return;
            }

            var go = new GameObject("GameSettingsMenu");
            go.transform.SetParent(transform, false);
            go.AddComponent<GameSettingsMenuController>();
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
            RefreshBuildInfoPanel();
        }

        private void RefreshBuildInfoPanel()
        {
            if (buildInfoPanel == null)
            {
                buildInfoPanel = FindObjectOfType<BuildInfoPanel>();
            }

            if (buildInfoPanel == null || session == null || session.State == null)
            {
                return;
            }

            buildInfoPanel.Refresh(session.State, localPlayerId);
        }

        private void OnBuildInfoFacilityClicked(string facilityId)
        {
            var facility = FacilityCardDatabase.Get(facilityId);
            SetPrompt(facility == null
                ? "建设面板：已选择设施。"
                : "建设面板：已选择设施 " + facility.Name + "。");
        }

        private void OnBuildInfoCityStyleClicked(string cityStyleId)
        {
            var cityStyle = CityStyleDatabase.Get(cityStyleId);
            SetPrompt(cityStyle == null
                ? "建设面板：已选择城市样式。"
                : "建设面板：已选择城市样式 " + cityStyle.Name + "。");
        }

        private void OnBuildInfoCityBoardSlotClicked(int slotIndex)
        {
            SetPrompt("建设面板：已选择城市面板槽位 " + (slotIndex + 1) + "。");
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
            explorePaymentRecipientSelection.Clear();
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
            explorePaymentRecipientSelection.Clear();
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
            explorePaymentRecipientSelection.BuildForPathWithPaymentKeys(
                path,
                GetRoutePaymentKey,
                routeId => HasRoutePaymentKeyInfluenceOwnedBy(GetRoutePaymentKey(routeId), localPlayerId),
                routeId => GetOpponentInfluenceOwnersOnRoutePaymentKey(GetRoutePaymentKey(routeId), localPlayerId));
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
            var paidPaymentKeys = new List<string>();
            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                var routeId = path.RouteIds[i];
                var paymentKey = GetRoutePaymentKey(routeId);
                if (paidPaymentKeys.Contains(paymentKey) ||
                    HasRoutePaymentKeyInfluenceOwnedBy(paymentKey, localPlayerId))
                {
                    continue;
                }

                paidPaymentKeys.Add(paymentKey);
                var owners = GetOpponentInfluenceOwnersOnRoutePaymentKey(paymentKey, localPlayerId);
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
            var paidPaymentKeys = new List<string>();
            if (path != null)
            {
                for (var i = 0; i < path.RouteIds.Count; i++)
                {
                    var routeId = path.RouteIds[i];
                    var paymentKey = GetRoutePaymentKey(routeId);
                    if (paidPaymentKeys.Contains(paymentKey) ||
                        HasRoutePaymentKeyInfluenceOwnedBy(paymentKey, localPlayerId))
                    {
                        continue;
                    }

                    paidPaymentKeys.Add(paymentKey);
                    var owners = GetOpponentInfluenceOwnersOnRoutePaymentKey(paymentKey, localPlayerId);
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
            BuildExplorePaymentChoices(path);

            if (explorePaymentRecipientSelection.HasChoices)
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
            var pendingChoice = CurrentPendingChoice;
            if (pendingChoice == null) return;

            var card = EventCardDatabase.Get(pendingChoice.CardId);
            if (card == null) return;

            ShowEventCardOptions(card);
        }

        private void ShowEventCardOptions(EventCardDefinition card)
        {
            if (card == null || card.ChoiceRewards.Count == 0) return;
            eventOptionSelection.Begin(card);
            eventChoiceDialog.ShowEventCardOptions(
                GetUiCanvasTransform(),
                card,
                GetEventCardMetadataLabel(card),
                explorePaymentRecipientSelection.Choices,
                explorePaymentRecipientSelection.RecipientsByRouteId,
                GetPlayerDisplayName,
                ApplyEventChoice,
                SelectExplorePaymentRecipient);

            SetPrompt("请选择事件牌的一个选项。");
        }

        private void ShowExplorePathOptions()
        {
            eventChoiceDialog.ShowExplorePathOptions(
                GetUiCanvasTransform(),
                pendingExplorePathChoices,
                SelectExplorePathChoice);

            SetPrompt("多条最短路线的路费相同，请选择要支付给哪一方。");
        }

        private void ShowExplorePaymentOptions()
        {
            eventChoiceDialog.ShowExplorePaymentOptions(
                GetUiCanvasTransform(),
                explorePaymentRecipientSelection.Choices,
                explorePaymentRecipientSelection.RecipientsByRouteId,
                GetPlayerDisplayName,
                SelectExplorePaymentRecipient,
                SubmitExploreStart);

            SetPrompt("选择每条路线的过路费接收者，然后确认探索。");
        }

        private RectTransform GetUiCanvasTransform()
        {
            return uiCanvas == null ? null : uiCanvas.GetComponent<RectTransform>();
        }

        private void SelectExplorePaymentRecipient(string routeId, int recipientPlayerId)
        {
            if (!explorePaymentRecipientSelection.SelectRecipient(routeId, recipientPlayerId))
            {
                return;
            }

            ShowExplorePaymentOptions();
        }

        private void ApplyEventChoice(int choiceIndex)
        {
            EventOptionSelection selection;
            if (!eventOptionSelection.TrySelectChoice(choiceIndex, out selection))
            {
                return;
            }

            var pendingEventCard = eventOptionSelection.PendingEventCard;
            if (selection.RequiresInfluenceTargets)
            {
                BeginEventInfluenceTargetSelection(pendingEventCard, selection.ChoiceIndex);
                return;
            }

            var pendingChoice = CurrentPendingChoice;
            if (pendingChoice != null &&
                pendingChoice.ChoiceType == ExploreLocationCommandHandler.ExploreEventChoiceType)
            {
                SubmitResolveExploreChoice(selection.ChoiceIndex);
                return;
            }

            if (pendingChoice != null &&
                pendingChoice.ChoiceType == MoveCityCommandHandler.MoveCityEventChoiceType)
            {
                SubmitResolveMoveCityChoice(selection.ChoiceIndex);
                return;
            }

            if (!string.IsNullOrEmpty(pendingExploreTargetId))
            {
                SubmitExploreChoice(selection.ChoiceIndex);
                return;
            }

            bool appliedLocally;
            var result = SubmitGameCommand(new GameCommand
            {
                Kind = GameCommandKind.ResolveEntranceEvent,
                PlayerId = localPlayerId,
                OptionIds = new List<string> { selection.ChoiceIndex.ToString() }
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

        private void BeginEventInfluenceTargetSelection(EventCardDefinition card, int choiceIndex)
        {
            eventInfluenceTargetSelection.Begin(choiceIndex);
            lastEventInfluenceTargetSelectionFrame = -1;
            eventChoiceDialog.CollapseForMapInteraction();
            actionPanelMode = ActionPanelMode.ResolvingEventInfluenceTarget;
            ClearHighlights();

            var highlightedCount = HighlightEventInfluenceTargets(card, choiceIndex);
            RefreshActionPanel();
            if (highlightedCount <= 0)
            {
                ClearPendingEventInfluenceSelection();
                actionPanelMode = ActionPanelMode.PendingChoice;
                ShowEventCardOptions(card);
                SetPrompt("没有可用于该事件效果的影响力槽位。");
                return;
            }

            SetPrompt("请选择事件效果要放置影响力的槽位。");
        }

        private bool SelectFirstEventInfluenceLocationSlot(string locationId)
        {
            if (!eventInfluenceTargetSelection.IsSelecting)
            {
                return false;
            }

            var card = GetPendingEventCardForTargetSelection();
            if (card == null)
            {
                return false;
            }

            var location = mapQuery.GetLocation(locationId);
            for (var i = 0; i < location.InfluenceSlotCount; i++)
            {
                var slotId = InfluenceService.GetLocationSlotId(locationId, i);
                if (mapView.ContainsHighlightedInfluenceSlot(slotId))
                {
                    SelectEventInfluenceSlot(slotId);
                    return true;
                }
            }

            for (var routeIndex = 0; routeIndex < mapQuery.Map.Routes.Count; routeIndex++)
            {
                var route = mapQuery.Map.Routes[routeIndex];
                if (!RouteTouchesLocation(route, locationId))
                {
                    continue;
                }

                for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    var slotId = InfluenceService.GetRouteSlotId(route.RouteId, slotIndex);
                    if (mapView.ContainsHighlightedInfluenceSlot(slotId))
                    {
                        SelectEventInfluenceSlot(slotId);
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryMarkEventInfluenceTargetSelectionFrame()
        {
            if (lastEventInfluenceTargetSelectionFrame == Time.frameCount)
            {
                return false;
            }

            lastEventInfluenceTargetSelectionFrame = Time.frameCount;
            return true;
        }

        private void MarkEventInfluenceTargetSelectionFrame()
        {
            lastEventInfluenceTargetSelectionFrame = Time.frameCount;
        }

        private void SelectEventInfluenceSlot(string slotId)
        {
            var card = GetPendingEventCardForTargetSelection();
            if (card == null || !eventInfluenceTargetSelection.IsSelecting)
            {
                SetPrompt("当前没有待处理的事件影响力目标。");
                return;
            }

            if (!mapView.ContainsHighlightedInfluenceSlot(slotId))
            {
                SetPrompt("请选择高亮的影响力槽位。");
                return;
            }

            bool completed;
            if (!eventInfluenceTargetSelection.TrySelectSlot(card, slotId, out completed))
            {
                return;
            }

            if (!completed)
            {
                ClearHighlights();
                HighlightEventInfluenceTargets(card, eventInfluenceTargetSelection.ChoiceIndex);
                RefreshActionPanel();
                SetPrompt("继续选择事件效果的影响力槽位。");
                return;
            }

            var choiceIndex = eventInfluenceTargetSelection.ChoiceIndex;
            var pendingChoice = CurrentPendingChoice;
            if (pendingChoice != null &&
                pendingChoice.ChoiceType == ExploreLocationCommandHandler.ExploreEventChoiceType)
            {
                SubmitResolveExploreChoice(choiceIndex);
                return;
            }

            if (pendingChoice != null &&
                pendingChoice.ChoiceType == MoveCityCommandHandler.MoveCityEventChoiceType)
            {
                SubmitResolveMoveCityChoice(choiceIndex);
                return;
            }

            SubmitExploreChoice(choiceIndex);
        }

        private static bool RouteTouchesLocation(MapRouteDefinition route, string locationId)
        {
            if (route == null || string.IsNullOrEmpty(locationId))
            {
                return false;
            }

            if (route.FromLocationId == locationId || route.ToLocationId == locationId)
            {
                return true;
            }

            return route.CoveredLocationIds != null && route.CoveredLocationIds.Contains(locationId);
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

            if (CurrentPendingChoice != null)
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
            var command = new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = localPlayerId,
                OptionIds = new List<string> { choiceIndex.ToString() }
            };
            AddPendingEventInfluenceSlots(command, ExploreLocationCommandHandler.EventInfluenceSlotIdsParameter);
            var result = SubmitGameCommand(command, out appliedLocally);

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
            CompleteActionCommandUi("探索");
        }

        private void SubmitResolveMoveCityChoice(int choiceIndex)
        {
            bool appliedLocally;
            var pendingChoice = CurrentPendingChoice;
            var command = new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = localPlayerId,
                TargetId = pendingChoice == null ? string.Empty : pendingChoice.TargetId,
                OptionIds = new List<string> { choiceIndex.ToString() }
            };
            AddPendingEventInfluenceSlots(command, MoveCityCommandHandler.EventInfluenceSlotIdsParameter);
            var result = SubmitGameCommand(command, out appliedLocally);

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            if (!appliedLocally)
            {
                SetPrompt("城市移动事件选择已发送给主机，等待确认。");
                return;
            }

            HideEventCardOptions();
            ClearHighlights();
            RefreshResourceDisplay();
            RefreshInfluenceDisplay();
            CompleteActionCommandUi("\u57ce\u5e02\u79fb\u52a8");
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
            AddPendingEventInfluenceSlots(command, ExploreLocationCommandHandler.EventInfluenceSlotIdsParameter);

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
            CompleteActionCommandUi("探索");
        }

        private void AddPendingEventInfluenceSlots(GameCommand command, string parameterName)
        {
            eventInfluenceTargetSelection.AddCommandParameter(command, parameterName);
        }

        private EventCardDefinition GetPendingEventCardForTargetSelection()
        {
            var pendingChoice = CurrentPendingChoice;
            if (pendingChoice != null)
            {
                return EventCardDatabase.Get(pendingChoice.CardId);
            }

            var card = PeekExploreEventCard(pendingExploreTargetId);
            return card ?? eventOptionSelection.PendingEventCard;
        }

        private string GetPendingEventOriginLocationId()
        {
            var pendingChoice = CurrentPendingChoice;
            if (pendingChoice != null)
            {
                return pendingChoice.TargetId;
            }

            return pendingExploreTargetId;
        }

        private int HighlightEventInfluenceTargets(EventCardDefinition card, int choiceIndex)
        {
            if (card == null ||
                choiceIndex < 0 ||
                choiceIndex >= card.ChoicePendingEffects.Count)
            {
                return 0;
            }

            var count = 0;
            var originLocationId = GetPendingEventOriginLocationId();
            var effects = card.ChoicePendingEffects[choiceIndex];
            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var location = mapQuery.Map.Locations[i];
                for (var slotIndex = 0; slotIndex < location.InfluenceSlotCount; slotIndex++)
                {
                    var slotId = InfluenceService.GetLocationSlotId(location.LocationId, slotIndex);
                    if (CanUseEventInfluenceSlot(effects, originLocationId, slotId))
                    {
                        mapView.HighlightInfluenceSlot(slotId);
                        SetHighlighted(location.LocationId, new Color(0.15f, 0.8f, 1f, 0.85f));
                        count += 1;
                    }
                }
            }

            for (var i = 0; i < mapQuery.Map.Routes.Count; i++)
            {
                var route = mapQuery.Map.Routes[i];
                for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    var slotId = InfluenceService.GetRouteSlotId(route.RouteId, slotIndex);
                    if (CanUseEventInfluenceSlot(effects, originLocationId, slotId))
                    {
                        mapView.HighlightInfluenceSlot(slotId);
                        count += 1;
                    }
                }
            }

            RefreshInfluenceDisplay();
            return count;
        }

        private bool CanUseEventInfluenceSlot(
            IReadOnlyList<EventEffect> effects,
            string originLocationId,
            string slotId)
        {
            if (eventInfluenceTargetSelection.ContainsSlot(slotId))
            {
                return false;
            }

            InfluenceSlotReference slot;
            string reason;
            if (!InfluenceSlotReference.TryParse(mapQuery, slotId, out slot, out reason))
            {
                return false;
            }

            if (!IsEventInfluenceSlotInAnyScope(effects, originLocationId, slot))
            {
                return false;
            }

            if (IsReservedExplorePrimaryInfluenceSlot(originLocationId, slot))
            {
                return false;
            }

            if (influenceService.FindInfluence(session.State, slot.SlotId) != null)
            {
                return false;
            }

            if (slot.Kind == InfluenceSlotKind.Location &&
                slot.LocationId != originLocationId &&
                (!HasResourceToken(slot.LocationId) || HasOpponentCityAtLocation(slot.LocationId)))
            {
                return false;
            }

            var validation = influenceService.CanPlace(session.State, localPlayerId, slot.SlotId);
            return validation.IsValid ||
                   (slot.Kind == InfluenceSlotKind.Location &&
                    slot.LocationId == originLocationId &&
                    validation.ErrorCode == CommandErrorCode.ClosedLocation);
        }

        private bool IsReservedExplorePrimaryInfluenceSlot(string originLocationId, InfluenceSlotReference slot)
        {
            if (slot.Kind != InfluenceSlotKind.Location ||
                slot.LocationId != originLocationId ||
                string.IsNullOrEmpty(pendingExploreTargetId))
            {
                return false;
            }

            return slot.SlotId == FindFirstPlaceableEventOriginLocationSlot(originLocationId);
        }

        private string FindFirstPlaceableEventOriginLocationSlot(string originLocationId)
        {
            var location = mapQuery.GetLocation(originLocationId);
            for (var i = 0; i < location.InfluenceSlotCount; i++)
            {
                var slotId = InfluenceService.GetLocationSlotId(originLocationId, i);
                if (influenceService.FindInfluence(session.State, slotId) == null)
                {
                    return slotId;
                }
            }

            return string.Empty;
        }

        private bool IsEventInfluenceSlotInAnyScope(
            IReadOnlyList<EventEffect> effects,
            string originLocationId,
            InfluenceSlotReference slot)
        {
            if (effects == null)
            {
                return false;
            }

            for (var i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                if (effect == null ||
                    effect.Kind != EventEffectKind.PlaceInfluence ||
                    effect.Amount <= 0)
                {
                    continue;
                }

                if (IsEventInfluenceSlotInScope(effect.TargetScope, originLocationId, slot))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsEventInfluenceSlotInScope(
            EventEffectTargetScope scope,
            string originLocationId,
            InfluenceSlotReference slot)
        {
            switch (scope)
            {
                case EventEffectTargetScope.None:
                    return true;
                case EventEffectTargetScope.CurrentLocationOrAdjacentRoute:
                    return (slot.Kind == InfluenceSlotKind.Location && slot.LocationId == originLocationId) ||
                           (slot.Kind == InfluenceSlotKind.Route && RouteCoversLocation(slot.RouteId, originLocationId));
                case EventEffectTargetScope.AdjacentRoute:
                    return slot.Kind == InfluenceSlotKind.Route && RouteCoversLocation(slot.RouteId, originLocationId);
                default:
                    return false;
            }
        }

        private bool RouteCoversLocation(string routeId, string locationId)
        {
            return RouteCoversLocation(mapQuery.GetRoute(routeId), locationId);
        }

        private static bool RouteCoversLocation(MapRouteDefinition route, string locationId)
        {
            var coveredLocationIds = GetRouteCoveredLocationIds(route);
            for (var i = 0; i < coveredLocationIds.Count; i++)
            {
                if (coveredLocationIds[i] == locationId)
                {
                    return true;
                }
            }

            return false;
        }

        private bool RequestSecondClickConfirmation(
            string actionKey,
            string targetId,
            string prompt,
            Action confirmedAction,
            string locationId,
            string slotId)
        {
            if (pendingConfirmationActionKey == actionKey && pendingConfirmationTargetId == targetId)
            {
                var callback = pendingConfirmationCallback;
                ClearPendingConfirmation(false);
                ClearHighlights();
                if (callback != null)
                {
                    callback();
                }

                return true;
            }

            ClearPendingConfirmation(false);
            pendingConfirmationActionKey = actionKey ?? string.Empty;
            pendingConfirmationTargetId = targetId ?? string.Empty;
            pendingConfirmationLocationId = locationId ?? string.Empty;
            pendingConfirmationSlotId = slotId ?? string.Empty;
            pendingConfirmationCallback = confirmedAction;
            HighlightPendingConfirmationTarget();
            SetPrompt(prompt);
            return false;
        }

        private bool HasPendingConfirmation()
        {
            return !string.IsNullOrEmpty(pendingConfirmationActionKey);
        }

        private void ClearPendingConfirmation(bool restoreCurrentMode)
        {
            if (!HasPendingConfirmation())
            {
                return;
            }

            pendingConfirmationActionKey = string.Empty;
            pendingConfirmationTargetId = string.Empty;
            pendingConfirmationLocationId = string.Empty;
            pendingConfirmationSlotId = string.Empty;
            pendingConfirmationCallback = null;

            if (restoreCurrentMode)
            {
                RestoreHighlightsForCurrentMode();
                RefreshActionPanel();
                SetPrompt(GetCurrentModePrompt());
            }
        }

        private void HighlightPendingConfirmationTarget()
        {
            ClearHighlights();

            if (!string.IsNullOrEmpty(pendingConfirmationLocationId))
            {
                SetHighlighted(pendingConfirmationLocationId, new Color(1f, 0.82f, 0.2f, 0.95f));
            }

            if (!string.IsNullOrEmpty(pendingConfirmationSlotId) && mapView != null)
            {
                mapView.HighlightInfluenceSlot(pendingConfirmationSlotId);
                RefreshInfluenceDisplay();
            }
        }

        private void RestoreHighlightsForCurrentMode()
        {
            ClearHighlights();

            switch (actionPanelMode)
            {
                case ActionPanelMode.ResolvingMoveTarget:
                    var player = session.State.FindPlayer(localPlayerId);
                    if (player != null && !string.IsNullOrEmpty(player.CityLocationId))
                    {
                        HighlightReachableLocations(player.CityLocationId);
                    }
                    break;
                case ActionPanelMode.ResolvingExploreTarget:
                    HighlightExplorableLocations();
                    break;
                case ActionPanelMode.ResolvingDeployTarget:
                    HighlightDeployTargets(true);
                    break;
                case ActionPanelMode.ResolvingDispatchSource:
                    HighlightOwnInfluenceLocations();
                    break;
                case ActionPanelMode.ResolvingDispatchTarget:
                    if (!string.IsNullOrEmpty(pendingDispatchSourceSlotId))
                    {
                        HighlightDispatchTargets(pendingDispatchSourceSlotId);
                    }
                    break;
                case ActionPanelMode.ResolvingEventInfluenceTarget:
                    var card = GetPendingEventCardForTargetSelection();
                    if (card != null && eventInfluenceTargetSelection.IsSelecting)
                    {
                        HighlightEventInfluenceTargets(card, eventInfluenceTargetSelection.ChoiceIndex);
                    }
                    break;
            }
        }

        private string GetCurrentModePrompt()
        {
            switch (actionPanelMode)
            {
                case ActionPanelMode.ResolvingMoveTarget:
                    return "城市移动：选择一个高亮资源点。";
                case ActionPanelMode.ResolvingExploreTarget:
                    return "探索：选择一个高亮资源点。";
                case ActionPanelMode.ResolvingDeployTarget:
                    return "选择一个影响力空格放置影响力";
                case ActionPanelMode.ResolvingDispatchSource:
                    return "调度：先选择一个自己的影响力。";
                case ActionPanelMode.ResolvingDispatchTarget:
                    return "请选择调度目标槽位。";
                default:
                    return "请从右下角行动面板选择主要行动。";
            }
        }

        private void UpdatePendingConfirmationCancellation()
        {
            if (!HasPendingConfirmation() || !Input.GetMouseButtonDown(0))
            {
                return;
            }

            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            {
                return;
            }

            if (IsPointerOverMapInteractionTarget())
            {
                return;
            }

            CancelPendingConfirmation(true);
        }

        private void CancelPendingConfirmation(bool restoreCurrentMode)
        {
            ClearPendingConfirmation(restoreCurrentMode);
        }

        private bool IsPointerOverMapInteractionTarget()
        {
            if (targetCamera == null)
            {
                return false;
            }

            var screenPosition = Input.mousePosition;
            var worldPosition = targetCamera.ScreenToWorldPoint(new Vector3(
                screenPosition.x,
                screenPosition.y,
                -targetCamera.transform.position.z));
            var hits = Physics2D.OverlapPointAll(worldPosition);
            for (var i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                if (hit == null)
                {
                    continue;
                }

                if (hit.GetComponent<MapHotspot>() != null ||
                    hit.GetComponent<InfluenceSlotClickTarget>() != null ||
                    hit.GetComponent<MobileCityClickTarget>() != null)
                {
                    return true;
                }
            }

            return false;
        }

        private int GetCityMoveOriginiumShardCost()
        {
            return new TravelCostService(mapQuery).GetCityMoveBaseCost().OriginiumShard;
        }

        private bool TryEstimateExploreGoldVoucherCost(string locationId, out int estimatedCost, out string errorPrompt)
        {
            estimatedCost = 0;
            errorPrompt = string.Empty;

            IReadOnlyList<MapPath> paths;
            try
            {
                paths = explorationService.FindDefaultPathChoices(session.State, localPlayerId, locationId);
            }
            catch (ArgumentException)
            {
                errorPrompt = "没有可到达该探索目标的路线。";
                return false;
            }

            if (paths.Count <= 0)
            {
                errorPrompt = "没有可到达该探索目标的路线。";
                return false;
            }

            estimatedCost = CalculateExploreGoldVoucherCost(paths[0]);
            return true;
        }

        private int CalculateExploreGoldVoucherCost(MapPath path)
        {
            if (path == null)
            {
                return 0;
            }

            var total = 0;
            var paidPaymentKeys = new List<string>();
            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                var paymentKey = GetRoutePaymentKey(path.RouteIds[i]);
                if (paidPaymentKeys.Contains(paymentKey) ||
                    HasRoutePaymentKeyInfluenceOwnedBy(paymentKey, localPlayerId))
                {
                    continue;
                }

                paidPaymentKeys.Add(paymentKey);
                total += ExplorationService.RouteCostGoldVoucher;
            }

            return total;
        }

        private string GetLocationIdForSlot(string slotId)
        {
            InfluenceSlotReference slot;
            string reason;
            if (InfluenceSlotReference.TryParse(mapQuery, slotId, out slot, out reason) &&
                slot.Kind == InfluenceSlotKind.Location)
            {
                return slot.LocationId;
            }

            return string.Empty;
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
            return explorePaymentRecipientSelection.EncodeRecipients();
        }

        private void ClearPendingExploreChoice()
        {
            pendingExploreTargetId = string.Empty;
            pendingExplorePath = null;
            explorePaymentRecipientSelection.Clear();
            pendingExplorePathChoices.Clear();
            ClearPendingEventInfluenceSelection();
        }

        private void ClearPendingEventInfluenceSelection()
        {
            eventOptionSelection.Clear();
            eventInfluenceTargetSelection.Clear();
        }

        private void HideEventCardOptions()
        {
            eventChoiceDialog.Hide();
            ClearPendingExploreChoice();
        }

        private string GetEventCardMetadataLabel(EventCardDefinition card)
        {
            var targetId = pendingExploreTargetId;
            var pendingChoice = CurrentPendingChoice;
            if (string.IsNullOrEmpty(targetId) && pendingChoice != null)
            {
                targetId = pendingChoice.TargetId;
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
            var result = GameSessionBootstrapper.Build(GameLaunchContext.Instance);
            session = result.Session;
            mapQuery = result.MapQuery;
            influenceService = result.InfluenceService;
            explorationService = result.ExplorationService;
            localPlayerId = result.LocalPlayerId;
        }

        private void BuildCommandSubmission(GameLaunchContext launchContext)
        {
            commandSubmission = new CommandSubmissionController(
                session,
                launchContext,
                localPlayerId,
                this,
                RefreshAllFromState,
                SetPrompt);
            commandSubmission.Initialize();
        }

        private CommandResult SubmitGameCommand(GameCommand command, out bool appliedLocally)
        {
            if (commandSubmission == null)
            {
                appliedLocally = false;
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "命令提交器尚未初始化"));
            }

            return commandSubmission.Submit(command, out appliedLocally);
        }

        private void RefreshAllFromState()
        {
            SynchronizeLocalPlayerForHotseat();

            actionPanelMode = ActionPanelMode.ChooseAction;
            ClearPendingDispatch();
            HideEventCardOptions();

            var player = session.State.FindPlayer(localPlayerId);
            if (player == null || !player.ActedMainActionThisTurn)
            {
                completedMainActionName = string.Empty;
            }

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
            if (session.State.Phase != GamePhase.ResourceCollection)
            {
                ClearCollectionSelection();
            }

            var pendingChoice = CurrentPendingChoice;
            if (pendingChoice != null && pendingChoice.PlayerId == localPlayerId)
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

            if (session.State.Phase == GamePhase.ResourceCollection)
            {
                BeginResourceCollectionSelection();
                return;
            }

            ClearHighlights();
            UpdateEntranceOrActionPrompt();
        }

        private static int GetFirstTurnPlayerId(GameState state)
        {
            var order = new TurnOrderService().GetTurnOrder(state);
            return order.Count > 0 ? order[0] : state.StartPlayerId;
        }

        private void RefreshInfluenceDisplay()
        {
            if (mapView == null || session == null)
            {
                return;
            }

            mapView.RefreshInfluenceDisplay(
                session.State,
                HasPendingDispatchFirstMove(),
                pendingDispatchFirstSourceSlotId,
                pendingDispatchFirstTargetSlotId);
        }

        private void BuildPromptPresenter()
        {
            promptPresenter = PromptPresenter.Build(transform);
            uiCanvas = promptPresenter == null ? null : promptPresenter.Canvas;
        }

        private void BuildActionPanel()
        {
            actionPanel = ActionPanelController.Build(
                uiCanvas,
                OnUseCharacterActionClicked,
                OnDeclareCityStyleClicked,
                BeginDeployAction,
                BeginDispatchAction,
                BeginExploreAction,
                BeginMoveAction,
                OnBuildActionClicked,
                OnSpecialActionClicked,
                EndCurrentAction);
            RefreshActionPanel();
        }

        private void RefreshActionPanel()
        {
            SynchronizeLocalPlayerForHotseat();

            if (actionPanel == null || !actionPanel.IsReady || session == null)
            {
                return;
            }

            var state = session.State;
            var player = state.FindPlayer(localPlayerId);
            var isActionPhase = state.Phase == GamePhase.ActionRound1 || state.Phase == GamePhase.ActionRound2;
            var isLocalTurn = IsLocalPlayersTurn();
            var hasPendingChoice = state.HasPendingChoice();
            var mainActionDone = player != null && player.ActedMainActionThisTurn;
            var canChooseQuickAction = isActionPhase && isLocalTurn && !hasPendingChoice;
            var canChooseMainAction = canChooseQuickAction && !mainActionDone;
            var pendingChoice = CardFlowStateAdapter.GetPendingChoiceView(state);
            var pendingChoiceBelongsToLocalPlayer = hasPendingChoice &&
                                                    pendingChoice != null &&
                                                    pendingChoice.PlayerId == localPlayerId;

            if (hasPendingChoice)
            {
                if (!eventInfluenceTargetSelection.IsSelecting)
                {
                    actionPanelMode = ActionPanelMode.PendingChoice;
                    if (pendingChoiceBelongsToLocalPlayer && !eventChoiceDialog.IsShowing)
                    {
                        ShowPendingEventCardOptions();
                    }
                }
            }
            else if (state.Phase == GamePhase.ResourceCollection)
            {
                actionPanelMode = player != null && !player.HasCollectedResourcesThisRound
                    ? ActionPanelMode.ResolvingResourceCollection
                    : ActionPanelMode.WaitingForNextPlayer;
            }
            else if (state.Phase == GamePhase.Cleanup)
            {
                actionPanelMode = ActionPanelMode.WaitingForNextPlayer;
            }
            else if (isActionPhase && (!isLocalTurn || mainActionDone))
            {
                actionPanelMode = ActionPanelMode.WaitingForNextPlayer;
            }
            else if (isActionPhase &&
                     canChooseMainAction &&
                     (actionPanelMode == ActionPanelMode.Hidden ||
                      actionPanelMode == ActionPanelMode.PendingChoice ||
                      actionPanelMode == ActionPanelMode.WaitingForNextPlayer))
            {
                actionPanelMode = ActionPanelMode.ChooseAction;
            }

            actionPanel.SetHeader(
                "本机：" + GetPlayerDisplayName(localPlayerId) + " / 行动：" + GetPlayerDisplayName(state.CurrentPlayerId),
                "第 " + state.Round + " 回合 / 行动轮 " + state.ActionRound);
            actionPanel.SetLocalPlayerColor(player == null ? Color.white : UiTheme.GetPlayerColor(player.Color, 1f));
            actionPanel.SetButtonStates(
                canChooseQuickAction && player != null && !player.UsedCharacterThisRound,
                canChooseQuickAction,
                canChooseMainAction,
                canChooseMainAction,
                canChooseMainAction,
                canChooseMainAction && player != null,
                canChooseMainAction,
                canChooseMainAction && HasAvailableSpecialAction(player),
                CanEndCurrentAction() && !RoundTrackRule.IsFinalState(state));
            actionPanel.SetStatus(GetActionPanelStatus(state, player, isActionPhase, isLocalTurn, hasPendingChoice, mainActionDone));
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

            if (state.Phase == GamePhase.ResourceCollection)
            {
                if (player == null)
                {
                    return "采集阶段：当前玩家不存在";
                }

                if (player.HasCollectedResourcesThisRound)
                {
                    return "采集阶段：已提交采集，等待其他玩家";
                }

                return BuildResourceCollectionStatus();
            }

            if (state.Phase == GamePhase.Cleanup)
            {
                return CanEndCurrentAction()
                    ? "收尾阶段：点击结束本回合进入下一回合"
                    : "收尾阶段：等待起始玩家结束本回合";
            }

            if (!isActionPhase)
            {
                return "当前阶段不能执行行动";
            }

            if (hasPendingChoice)
            {
                var pendingChoice = CardFlowStateAdapter.GetPendingChoiceView(state);
                return pendingChoice != null && pendingChoice.PlayerId != localPlayerId
                    ? "等待玩家 " + pendingChoice.PlayerId + " 处理事件选择"
                    : "请先处理事件选择";
            }

            if (!isLocalTurn)
            {
                return "等待玩家 " + state.CurrentPlayerId + " 行动";
            }

            if (mainActionDone)
            {
                return BuildCompletedMainActionMessage(completedMainActionName);
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
                case ActionPanelMode.ResolvingDispatchDecision:
                    return "调度：选择再次调度或取消";
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
            if (mapView == null || mapView.HighlightedLocationCount == 0)
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
                ShowDispatchDecisionDialog();
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
            if (!CanStartQuickActionSelection())
            {
                return;
            }

            ClearPendingDispatch();
            ClearHighlights();
            RefreshActionPanel();

            var options = cityStyleSelection.BuildOptions(session.State, localPlayerId);
            eventChoiceDialog.ShowCityStyleOptions(
                uiCanvas == null ? null : uiCanvas.GetComponent<RectTransform>(),
                options,
                SubmitDeclareCityStyle,
                () => SetPrompt("已取消宣告城市样式。"));
            SetPrompt("宣告城市样式：查看可宣告样式和不可宣告原因。");
        }

        private void SubmitDeclareCityStyle(string cityStyleId)
        {
            bool appliedLocally;
            var result = SubmitGameCommand(
                cityStyleSelection.CreateCommand(localPlayerId, cityStyleId),
                out appliedLocally);

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            if (!appliedLocally)
            {
                SetPrompt("宣告城市样式命令已发送给主机，等待确认。");
                return;
            }

            RefreshInfoPanel();
            RefreshActionPanel();
            SetPrompt("城市样式宣告成功，最终计分会计入该样式分。");
        }

        private void OnBuildActionClicked()
        {
            if (!CanStartActionSelection())
            {
                return;
            }

            var player = session.State.FindPlayer(localPlayerId);
            var cityBoardSlotIndex = BuildFacilityService.FindFirstEmptyCityBoardSlot(session.State, localPlayerId);
            if (player == null)
            {
                SetPrompt("当前玩家不存在。");
                return;
            }

            if (cityBoardSlotIndex < 0)
            {
                SetPrompt("城市面板没有空槽位。");
                return;
            }

            if (session.State.Decks.FacilitySupply.Count <= 0)
            {
                SetPrompt("设施供应区为空。");
                return;
            }

            ClearPendingDispatch();
            ClearHighlights();
            RefreshActionPanel();
            eventChoiceDialog.ShowBuildFacilityOptions(
                uiCanvas == null ? null : uiCanvas.GetComponent<RectTransform>(),
                session.State.Decks.FacilitySupply,
                player,
                cityBoardSlotIndex,
                SubmitBuildFacility,
                () => SetPrompt("已取消建设。"));
            SetPrompt("建设：选择设施和支付方式。");
        }

        private void SubmitBuildFacility(string facilityId, string paymentMode)
        {
            bool appliedLocally;
            var result = SubmitGameCommand(
                buildFacilitySelection.CreateCommand(session.State, localPlayerId, facilityId, paymentMode),
                out appliedLocally);

            if (!result.Succeeded)
            {
                SetPrompt(result.Validation.Reason);
                return;
            }

            if (!appliedLocally)
            {
                SetPrompt("建设命令已发送给主机，等待确认。");
                return;
            }

            RefreshInfoPanel();
            CompleteActionCommandUi("建设");
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

        private bool CanStartQuickActionSelection()
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

            if (session.State.Phase != GamePhase.ActionRound1 && session.State.Phase != GamePhase.ActionRound2)
            {
                SetPrompt("当前阶段不能执行快速行动。");
                return false;
            }

            return true;
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
            if (mapView == null)
            {
                return;
            }

            mapView.RefreshResourceTokenDisplay(session == null ? null : session.State);
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
            if (awaitingInitialPlacement && IsLocalPlayersTurn() && (mapView == null || mapView.HighlightedLocationCount == 0))
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

            if (session.State.Phase == GamePhase.ResourceCollection)
            {
                var collectingPlayerId = FindFirstUncollectedResourceCollectionPlayerId(session.State);
                if (collectingPlayerId > 0)
                {
                    localPlayerId = collectingPlayerId;
                }

                return;
            }

            localPlayerId = session.State.CurrentPlayerId;
        }

        private static int FindFirstUncollectedResourceCollectionPlayerId(GameState state)
        {
            var order = new TurnOrderService().GetTurnOrder(state);
            for (var i = 0; i < order.Count; i++)
            {
                var player = state.FindPlayer(order[i]);
                if (player != null && !player.HasCollectedResourcesThisRound)
                {
                    return player.PlayerId;
                }
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                if (!state.Players[i].HasCollectedResourcesThisRound)
                {
                    return state.Players[i].PlayerId;
                }
            }

            return -1;
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
            if (session.State.Phase == GamePhase.ResourceCollection)
            {
                SetPrompt(BuildResourceCollectionStatus());
                return;
            }

            if (session.State.Phase == GamePhase.Cleanup)
            {
                SetPrompt("收尾阶段：点击结束本回合进入下一回合。");
                return;
            }

            if (!string.IsNullOrEmpty(actionMessage))
            {
                SetPrompt(actionMessage);
                return;
            }

            var currentPlayer = session.State.FindPlayer(localPlayerId);
            if (currentPlayer != null && currentPlayer.ActedMainActionThisTurn)
            {
                SetPrompt(BuildCompletedMainActionMessage(completedMainActionName));
                return;
            }

            SetPrompt("请从右下角行动面板选择主要行动。");
        }

        private void BeginResourceCollectionSelection()
        {
            var player = session.State.FindPlayer(localPlayerId);
            actionPanelMode = player != null && !player.HasCollectedResourcesThisRound
                ? ActionPanelMode.ResolvingResourceCollection
                : ActionPanelMode.WaitingForNextPlayer;

            ClearCollectionSelection();

            if (player == null || player.HasCollectedResourcesThisRound || string.IsNullOrEmpty(player.CityLocationId))
            {
                ClearHighlights();
                RefreshActionPanel();
                SetPrompt(BuildResourceCollectionStatus());
                return;
            }

            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var locationId = mapQuery.Map.Locations[i].LocationId;
                if (!CanCollectLocationFromUi(locationId))
                {
                    continue;
                }

                collectionCandidateLocationIds.Add(locationId);
            }

            RefreshCollectionHighlights();
            SetPrompt(BuildResourceCollectionStatus());
        }

        private void SubmitResourceCollection()
        {
            RefreshCollectionSelection();

            var locationIds = BuildSelectedCollectionLocationIds();
            var selectedRouteIds = BuildSelectedCollectionRouteIds(locationIds);
            var paymentRecipients = EncodeCollectionPaymentRecipients(selectedRouteIds);

            var command = new GameCommand
            {
                Kind = GameCommandKind.CollectResource,
                PlayerId = localPlayerId
            };
            command.Parameters[CollectResourceCommandHandler.LocationIdsParameter] = EncodeIds(locationIds);
            command.Parameters[CollectResourceCommandHandler.RouteIdsParameter] = EncodeIds(new List<string>(selectedRouteIds));
            if (!string.IsNullOrEmpty(paymentRecipients))
            {
                command.Parameters[CollectResourceCommandHandler.PaymentRecipientsParameter] = paymentRecipients;
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
                SetPrompt("采集命令已发送给主机，等待确认。");
                return;
            }

            RefreshAllFromState();
            SetPrompt(BuildCollectionResultPrompt(result));
        }

        private void SelectCollectionRoutePayment(string slotId)
        {
            InfluenceSlotReference slot;
            string reason;
            if (!InfluenceSlotReference.TryParse(mapQuery, slotId, out slot, out reason) ||
                slot.Kind != InfluenceSlotKind.Route)
            {
                SetPrompt("请选择高亮航道支付路费。");
                return;
            }

            var routeId = slot.RouteId;
            if (collectionRouteIds.Contains(routeId) && !HasRouteInfluenceOwnedBy(routeId, localPlayerId))
            {
                ShowCollectionRoutePaymentDialog(routeId, GetOpponentInfluenceOwnersOnRoute(routeId, localPlayerId));
                return;
            }

            if (!collectionRouteIds.Contains(routeId) || HasRouteInfluenceOwnedBy(routeId, localPlayerId))
            {
                SetPrompt("该航道本次采集无需支付路费。");
                return;
            }

            collectionPaidRouteIds.Add(routeId);
            var owners = GetOpponentInfluenceOwnersOnRoute(routeId, localPlayerId);
            if (owners.Count <= 0)
            {
                collectionPaymentRecipients.Remove(routeId);
                RefreshCollectionHighlights();
                SetPrompt("航道 " + routeId + " 的路费将支付给银行。");
                return;
            }

            var nextIndex = 0;
            int currentReceiver;
            if (collectionPaymentRecipients.TryGetValue(routeId, out currentReceiver))
            {
                var currentIndex = owners.IndexOf(currentReceiver);
                if (currentIndex >= 0)
                {
                    nextIndex = (currentIndex + 1) % owners.Count;
                }
            }

            var receiverPlayerId = owners[nextIndex];
            collectionPaymentRecipients[routeId] = receiverPlayerId;
            RefreshCollectionHighlights();
            SetPrompt("航道 " + routeId + " 的路费将支付给 " + GetPlayerDisplayName(receiverPlayerId) + "。");
        }

        private void ToggleCollectionLocationSelection(string locationId)
        {
            if (!collectionCandidateLocationIds.Contains(locationId))
            {
                SetPrompt("该资源点本次不能采集。");
                return;
            }

            if (!IsCollectionLocationAvailable(locationId))
            {
                SetPrompt("请先支付通往该资源点所需的航道路费。");
                return;
            }

            if (collectionSelectedLocationIds.Contains(locationId))
            {
                collectionDeselectedLocationIds.Add(locationId);
                RefreshCollectionHighlights();
                SetPrompt("已从本次采集中移除资源点 " + locationId + "。");
                return;
            }

            collectionDeselectedLocationIds.Remove(locationId);
            RefreshCollectionHighlights();
            SetPrompt("已加入本次采集资源点 " + locationId + "。");
        }

        private void RefreshCollectionHighlights()
        {
            RebuildCollectionNetwork();
            ClearHighlights();
            RefreshCollectionSelection();

            foreach (var routeId in collectionRouteIds)
            {
                if (!IsCollectionRoutePayable(routeId))
                {
                    continue;
                }

                var owners = GetOpponentInfluenceOwnersOnRoute(routeId, localPlayerId);
                if (!collectionPaidRouteIds.Contains(routeId) || owners.Count > 1)
                {
                    HighlightCollectionRoute(routeId);
                }
            }

            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var locationId = mapQuery.Map.Locations[i].LocationId;
                if (!collectionCandidateLocationIds.Contains(locationId) ||
                    !IsCollectionLocationAvailable(locationId))
                {
                    continue;
                }

                var color = collectionSelectedLocationIds.Contains(locationId)
                    ? new Color(0.25f, 0.95f, 0.45f, 0.82f)
                    : new Color(0.15f, 0.8f, 1f, 0.65f);
                SetHighlighted(locationId, color);
            }

            RefreshInfluenceDisplay();
            RefreshActionPanel();
        }

        private void RefreshCollectionSelection()
        {
            collectionSelectedLocationIds.Clear();
            foreach (var locationId in collectionCandidateLocationIds)
            {
                if (collectionDeselectedLocationIds.Contains(locationId) ||
                    !IsCollectionLocationAvailable(locationId))
                {
                    continue;
                }

                collectionSelectedLocationIds.Add(locationId);
            }
        }

        private void ShowCollectionRoutePaymentDialog(string routeId, IReadOnlyList<int> owners)
        {
            if (uiCanvas == null)
            {
                ConfirmCollectionRoutePayment(routeId, owners != null && owners.Count > 0 ? owners[0] : -1);
                return;
            }

            eventChoiceDialog.ShowResourceCollectionPaymentOptions(
                uiCanvas.GetComponent<RectTransform>(),
                routeId,
                ExplorationService.RouteCostGoldVoucher,
                owners,
                GetPlayerDisplayName,
                receiverPlayerId => ConfirmCollectionRoutePayment(routeId, receiverPlayerId),
                () => ConfirmCollectionRoutePayment(routeId, -1),
                () => SetPrompt(BuildResourceCollectionStatus()));
        }

        private void ConfirmCollectionRoutePayment(string routeId, int receiverPlayerId)
        {
            collectionPaidRouteIds.Add(routeId);
            if (receiverPlayerId > 0)
            {
                collectionPaymentRecipients[routeId] = receiverPlayerId;
            }
            else
            {
                collectionPaymentRecipients.Remove(routeId);
            }

            RefreshCollectionHighlights();
            SetPrompt(receiverPlayerId > 0
                ? "航道 " + routeId + " 的路费将支付给 " + GetPlayerDisplayName(receiverPlayerId) + "。"
                : "航道 " + routeId + " 的路费将支付给银行。");
        }

        private void RebuildCollectionNetwork()
        {
            collectionRouteIds.Clear();
            collectionPathsByLocationId.Clear();

            var player = session.State.FindPlayer(localPlayerId);
            if (player == null || string.IsNullOrEmpty(player.CityLocationId))
            {
                return;
            }

            var reachablePaths = BuildCollectionReachablePaths(player.CityLocationId);
            foreach (var pair in reachablePaths)
            {
                if (collectionCandidateLocationIds.Contains(pair.Key))
                {
                    collectionPathsByLocationId[pair.Key] = pair.Value;
                }
            }

            for (var i = 0; i < mapQuery.Map.Routes.Count; i++)
            {
                var route = mapQuery.Map.Routes[i];
                if (IsCollectionRouteSatisfied(route.RouteId) ||
                    !RouteTouchesReachableLocation(route, reachablePaths))
                {
                    continue;
                }

                collectionRouteIds.Add(route.RouteId);
            }
        }

        private Dictionary<string, MapPath> BuildCollectionReachablePaths(string sourceLocationId)
        {
            var paths = new Dictionary<string, MapPath>(StringComparer.Ordinal);
            var queue = new Queue<MapPath>();
            var startPath = new MapPath { LocationIds = new List<string> { sourceLocationId } };
            paths[sourceLocationId] = startPath;
            queue.Enqueue(startPath);

            while (queue.Count > 0)
            {
                var currentPath = queue.Dequeue();
                var currentLocationId = currentPath.LocationIds[currentPath.LocationIds.Count - 1];
                for (var routeIndex = 0; routeIndex < mapQuery.Map.Routes.Count; routeIndex++)
                {
                    var route = mapQuery.Map.Routes[routeIndex];
                    if (!IsCollectionRouteSatisfied(route.RouteId) ||
                        !RouteCoversLocation(route, currentLocationId))
                    {
                        continue;
                    }

                    var coveredLocationIds = GetRouteCoveredLocationIds(route);
                    for (var locationIndex = 0; locationIndex < coveredLocationIds.Count; locationIndex++)
                    {
                        var nextLocationId = coveredLocationIds[locationIndex];
                        if (paths.ContainsKey(nextLocationId))
                        {
                            continue;
                        }

                        var nextPath = new MapPath
                        {
                            LocationIds = new List<string>(currentPath.LocationIds),
                            RouteIds = new List<string>(currentPath.RouteIds)
                        };
                        nextPath.LocationIds.Add(nextLocationId);
                        nextPath.RouteIds.Add(route.RouteId);
                        paths[nextLocationId] = nextPath;
                        queue.Enqueue(nextPath);
                    }
                }
            }

            return paths;
        }

        private static bool RouteTouchesReachableLocation(
            MapRouteDefinition route,
            IDictionary<string, MapPath> reachablePaths)
        {
            if (route.CoveredLocationIds != null && route.CoveredLocationIds.Count > 0)
            {
                for (var i = 0; i < route.CoveredLocationIds.Count; i++)
                {
                    if (reachablePaths.ContainsKey(route.CoveredLocationIds[i]))
                    {
                        return true;
                    }
                }
            }

            return reachablePaths.ContainsKey(route.FromLocationId) ||
                   reachablePaths.ContainsKey(route.ToLocationId);
        }

        private static IReadOnlyList<string> GetRouteCoveredLocationIds(MapRouteDefinition route)
        {
            if (route.CoveredLocationIds != null && route.CoveredLocationIds.Count > 0)
            {
                return route.CoveredLocationIds;
            }

            return new List<string> { route.FromLocationId, route.ToLocationId };
        }

        private void HighlightCollectionRoute(string routeId)
        {
            var route = mapQuery.GetRoute(routeId);
            for (var i = 0; i < route.InfluenceSlotCount; i++)
            {
                mapView.HighlightInfluenceSlot(InfluenceService.GetRouteSlotId(routeId, i));
            }
        }

        private bool CanCollectLocationFromUi(string locationId)
        {
            if (!HasResourceToken(locationId))
            {
                return false;
            }

            var player = session.State.FindPlayer(localPlayerId);
            if (player != null && player.CityLocationId == locationId)
            {
                return true;
            }

            for (var i = 0; i < session.State.Map.Influences.Count; i++)
            {
                var influence = session.State.Map.Influences[i];
                if (influence.PlayerId == localPlayerId &&
                    IsInfluenceOnLocation(influence, locationId))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsCollectionLocationAvailable(string locationId)
        {
            MapPath path;
            if (!collectionPathsByLocationId.TryGetValue(locationId, out path))
            {
                return false;
            }

            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                if (!IsCollectionRouteSatisfied(path.RouteIds[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsCollectionRoutePayable(string routeId)
        {
            return collectionRouteIds.Contains(routeId) && !HasRouteInfluenceOwnedBy(routeId, localPlayerId);
        }

        private bool IsCollectionRouteSatisfied(string routeId)
        {
            return HasRouteInfluenceOwnedBy(routeId, localPlayerId) || collectionPaidRouteIds.Contains(routeId);
        }

        private List<string> BuildSelectedCollectionLocationIds()
        {
            var result = new List<string>();
            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var locationId = mapQuery.Map.Locations[i].LocationId;
                if (collectionSelectedLocationIds.Contains(locationId))
                {
                    result.Add(locationId);
                }
            }

            return result;
        }

        private HashSet<string> BuildSelectedCollectionRouteIds(IReadOnlyList<string> locationIds)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < locationIds.Count; i++)
            {
                MapPath path;
                if (!collectionPathsByLocationId.TryGetValue(locationIds[i], out path))
                {
                    continue;
                }

                for (var routeIndex = 0; routeIndex < path.RouteIds.Count; routeIndex++)
                {
                    result.Add(path.RouteIds[routeIndex]);
                }
            }

            return result;
        }

        private string EncodeCollectionPaymentRecipients(HashSet<string> selectedRouteIds)
        {
            var encoded = string.Empty;
            foreach (var pair in collectionPaymentRecipients)
            {
                if (!selectedRouteIds.Contains(pair.Key))
                {
                    continue;
                }

                encoded += string.IsNullOrEmpty(encoded)
                    ? pair.Key + "=" + pair.Value
                    : ";" + pair.Key + "=" + pair.Value;
            }

            return encoded;
        }

        private void ClearCollectionSelection()
        {
            collectionCandidateLocationIds.Clear();
            collectionSelectedLocationIds.Clear();
            collectionDeselectedLocationIds.Clear();
            collectionRouteIds.Clear();
            collectionPaidRouteIds.Clear();
            collectionPaymentRecipients.Clear();
            collectionPathsByLocationId.Clear();
        }

        private string BuildResourceCollectionStatus()
        {
            RefreshCollectionSelection();

            if (collectionCandidateLocationIds.Count <= 0)
            {
                return "采集阶段：没有可采集资源点，点击结束本回合跳过采集";
            }

            var unpaidRouteCount = 0;
            foreach (var routeId in collectionRouteIds)
            {
                if (IsCollectionRoutePayable(routeId) && !collectionPaidRouteIds.Contains(routeId))
                {
                    unpaidRouteCount += 1;
                }
            }

            if (collectionSelectedLocationIds.Count <= 0 && unpaidRouteCount > 0)
            {
                return "采集阶段：点击高亮航道支付路费，支付后会高亮可采集资源点";
            }

            return "采集阶段：已选择 " + collectionSelectedLocationIds.Count + " 个资源点。点击航道支付或切换接收方，结束本回合后结算";
        }

        private static string BuildCollectionResultPrompt(CommandResult result)
        {
            if (result == null || result.Events == null || result.Events.Count <= 0)
            {
                return "采集已结算。";
            }

            var data = result.Events[0].Data;
            var parts = new List<string>();
            AddRewardPart(parts, data, "rewardGoldVoucher", "金券");
            AddRewardPart(parts, data, "rewardOriginium", "源岩");
            AddRewardPart(parts, data, "rewardOriginiumShard", "源石碎片");
            AddRewardPart(parts, data, "rewardIron", "异铁");
            AddRewardPart(parts, data, "rewardPureOriginium", "至纯源石");

            var summary = parts.Count <= 0 ? "无资源" : string.Join("、", parts.ToArray());
            string locations;
            data.TryGetValue("locationIds", out locations);
            return string.IsNullOrEmpty(locations)
                ? "本次采集获得：" + summary + "。"
                : "本次采集 " + locations + "，获得：" + summary + "。";
        }

        private static void AddRewardPart(
            List<string> parts,
            IReadOnlyDictionary<string, string> data,
            string key,
            string label)
        {
            if (data == null)
            {
                return;
            }

            string value;
            int amount;
            if (data.TryGetValue(key, out value) &&
                int.TryParse(value, out amount) &&
                amount > 0)
            {
                parts.Add(label + " +" + amount);
            }
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
                    mapView.HighlightInfluenceSlot(slotId);
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
                    mapView.HighlightInfluenceSlot(slotId);
                }
            }
        }

        private void HighlightOwnInfluenceLocations()
        {
            for (var i = 0; i < GetStateInfluenceCount(); i++)
            {
                var influence = session.State.Map.Influences[i];
                if (influence.PlayerId != localPlayerId ||
                    influence.SlotId == pendingDispatchFirstSourceSlotId ||
                    !CanMoveInfluenceFromSource(influence.SlotId))
                {
                    continue;
                }

                mapView.HighlightInfluenceSlot(influence.SlotId);
                if (!string.IsNullOrEmpty(influence.LocationId))
                {
                    SetHighlighted(influence.LocationId, new Color(0.86f, 0.75f, 0.2f, 0.8f));
                }
            }

            RefreshInfluenceDisplay();
        }

        private void HighlightDispatchTargets(string sourceSlotId)
        {
            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var locationId = mapQuery.Map.Locations[i].LocationId;
                var location = mapQuery.Map.Locations[i];

                for (var slotIndex = 0; slotIndex < location.InfluenceSlotCount; slotIndex++)
                {
                    var slotId = InfluenceService.GetLocationSlotId(locationId, slotIndex);
                    if (CanMoveInfluenceFromSourceToSlot(sourceSlotId, slotId))
                    {
                        mapView.HighlightInfluenceSlot(slotId);
                        SetHighlighted(locationId, new Color(0.15f, 0.8f, 1f, 0.85f));
                    }
                }
            }

            for (var i = 0; i < mapQuery.Map.Routes.Count; i++)
            {
                var route = mapQuery.Map.Routes[i];
                for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    var slotId = InfluenceService.GetRouteSlotId(route.RouteId, slotIndex);
                    if (CanMoveInfluenceFromSourceToSlot(sourceSlotId, slotId))
                    {
                        mapView.HighlightInfluenceSlot(slotId);
                    }
                }
            }

            RefreshInfluenceDisplay();
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

        private bool CanMoveInfluenceFromSource(string sourceSlotId)
        {
            if (string.IsNullOrEmpty(sourceSlotId) || sourceSlotId == pendingDispatchFirstSourceSlotId)
            {
                return false;
            }

            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var location = mapQuery.Map.Locations[i];
                for (var slotIndex = 0; slotIndex < location.InfluenceSlotCount; slotIndex++)
                {
                    if (CanMoveInfluenceFromSourceToSlot(
                        sourceSlotId,
                        InfluenceService.GetLocationSlotId(location.LocationId, slotIndex)))
                    {
                        return true;
                    }
                }
            }

            for (var i = 0; i < mapQuery.Map.Routes.Count; i++)
            {
                var route = mapQuery.Map.Routes[i];
                for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    if (CanMoveInfluenceFromSourceToSlot(
                        sourceSlotId,
                        InfluenceService.GetRouteSlotId(route.RouteId, slotIndex)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool CanMoveInfluenceFromSourceToSlot(string sourceSlotId, string targetSlotId)
        {
            if (string.IsNullOrEmpty(sourceSlotId) || string.IsNullOrEmpty(targetSlotId))
            {
                return false;
            }

            if (HasPendingDispatchFirstMove())
            {
                return WithPendingDispatchFirstMove(
                    () => influenceService.CanMove(session.State, localPlayerId, sourceSlotId, targetSlotId).IsValid);
            }

            return influenceService.CanMove(session.State, localPlayerId, sourceSlotId, targetSlotId).IsValid;
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
                    influence.SlotId != pendingDispatchFirstSourceSlotId &&
                    CanMoveInfluenceFromSource(influence.SlotId))
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

        private bool HasRoutePaymentKeyInfluenceOwnedBy(string paymentKey, int playerId)
        {
            for (var i = 0; i < session.State.Map.Influences.Count; i++)
            {
                var influence = session.State.Map.Influences[i];
                if (influence.PlayerId == playerId && IsInfluenceOnRoutePaymentKey(influence, paymentKey))
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

        private List<int> GetOpponentInfluenceOwnersOnRoutePaymentKey(string paymentKey, int playerId)
        {
            var owners = new List<int>();
            for (var i = 0; i < session.State.Map.Influences.Count; i++)
            {
                var influence = session.State.Map.Influences[i];
                if (influence.PlayerId == playerId || !IsInfluenceOnRoutePaymentKey(influence, paymentKey))
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

        private bool IsInfluenceOnRoutePaymentKey(InfluencePlacement influence, string paymentKey)
        {
            if (!string.IsNullOrEmpty(influence.RouteId) &&
                GetRoutePaymentKey(influence.RouteId) == paymentKey)
            {
                return true;
            }

            InfluenceSlotReference slot;
            string reason;
            return InfluenceSlotReference.TryParse(mapQuery, influence.SlotId, out slot, out reason) &&
                   slot.Kind == InfluenceSlotKind.Route &&
                   GetRoutePaymentKey(slot.RouteId) == paymentKey;
        }

        private string GetRoutePaymentKey(string routeId)
        {
            var route = mapQuery.GetRoute(routeId);
            if (!string.IsNullOrEmpty(route.RegionId) && IsRoutePaymentRegion(route.RegionId))
            {
                return route.RegionId;
            }

            return route.RouteId;
        }

        private bool IsRoutePaymentRegion(string regionId)
        {
            for (var i = 0; i < mapQuery.Map.Regions.Count; i++)
            {
                var region = mapQuery.Map.Regions[i];
                if (region.RegionId == regionId)
                {
                    return region.LocationIds != null && region.LocationIds.Count > 0;
                }
            }

            return false;
        }

        private bool IsInfluenceOnLocation(InfluencePlacement influence, string locationId)
        {
            if (influence.LocationId == locationId)
            {
                return true;
            }

            InfluenceSlotReference slot;
            string reason;
            return InfluenceSlotReference.TryParse(mapQuery, influence.SlotId, out slot, out reason) &&
                   slot.Kind == InfluenceSlotKind.Location &&
                   slot.LocationId == locationId;
        }

        private bool IsRedZoneClosed(MapLocationDefinition location)
        {
            return RedZoneAccessRule.IsClosed(session.State, mapQuery.Map, location);
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
            mapView.SetHighlighted(locationId, color);
        }

        private void ClearHighlights()
        {
            mapView.ClearHighlights();
            RefreshInfluenceDisplay();
            ApplyDebugHotspotHighlights();
        }

        private void RefreshCityViewsFromState()
        {
            mapView.RefreshCityViewsFromState(session == null ? null : session.State, localPlayerId);
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
            mapView.MoveCityView(playerId, locationId, session == null ? null : session.State, localPlayerId);
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
            var normalizedPosition = mapView.ToNormalizedMapPosition(worldPosition);
            Debug.Log(string.Format(
                "Map click world=({0:F3}, {1:F3}) normalized=({2:F3}, {3:F3})",
                worldPosition.x,
                worldPosition.y,
                normalizedPosition.x,
                normalizedPosition.y));
        }

        private void ApplyDebugHotspotHighlights()
        {
            mapView.ApplyDebugHotspotHighlights(debugClicks);
        }

        private void SetPrompt(string message)
        {
            if (promptPresenter != null)
            {
                promptPresenter.SetPrompt(message);
            }
        }

        private void UpdatePromptAnimation()
        {
            if (promptPresenter != null)
            {
                promptPresenter.Update(HasPendingConfirmation());
            }
        }

    }

}
