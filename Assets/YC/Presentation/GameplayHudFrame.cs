using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Interactions;
using YC.Presentation.Workflows;
using YC.Domain.Presentation;
using YC.Application.Presentation;
using System.Globalization;
using System.Linq;

namespace YC.Presentation
{
    /// <summary>共享 HUD 中的安全区域、常驻栏和内容页面边界。</summary>
    public sealed class GameplayHudFrame : MonoBehaviour
    {

        [SerializeField] private Canvas barCanvas;
        [SerializeField] private RectTransform safeArea;
        [SerializeField] private RectTransform topBar;
        [SerializeField] private RectTransform bottomBar;
        [SerializeField] private RectTransform topBarContent;
        [SerializeField] private RectTransform bottomBarContent;
        [SerializeField] private RectTransform contentRect;
        [SerializeField] private RectTransform mapRegion;
        [SerializeField] private RectTransform mapViewport;
        [SerializeField] private RectTransform mapVisibleBounds;
        [SerializeField] private RectTransform mainSurface;
        [SerializeField] private RectTransform mainRegions;
        [SerializeField] private HorizontalLayoutGroup columnsLayout;
        [SerializeField] private HorizontalLayoutGroup topContentLayout;
        [SerializeField] private UiMapSurroundLayout mapSurroundLayout;
        [SerializeField] private UiCitySlotsLayout citySlotsLayout;
        [SerializeField] private RectTransform[] mapBackdrops;
        [SerializeField] private Text roundNumberText;
        [SerializeField] private Text roundTotalText;
        [SerializeField] private Text phaseText;
        [SerializeField] private Text redZoneText;
        [SerializeField] private Text redZoneOpenRoundText;
        [SerializeField] private GameObject roundPlayerOrderGroup;
        [SerializeField] private InfluenceMarker2DView[] roundPlayerOrderMarkers;
        [SerializeField] private Text turnTagText;
        [SerializeField] private Image[] roundTicks;
        [SerializeField] private Text summaryText;
        [SerializeField] private Text shortSummaryText;
        [SerializeField] private GameplayTextView summaryDocumentView;
        [SerializeField] private GameplayTextView shortSummaryDocumentView;
        [SerializeField] private GameplayTextStyle gameplayTextStyle;
        [SerializeField] private GameplayIconCatalog gameplayIconCatalog;
        [SerializeField] private Text scoreValueText;
        [SerializeField] private Text[] resourceValueTexts;
        [SerializeField] private Button settingsButton;
        [SerializeField] private Button foldButton;
        [SerializeField] private Text foldButtonText;
        [SerializeField] private Button undoButton;
        [SerializeField] private Button endActionButton;
        [SerializeField] private GameObject longSummary;
        [SerializeField] private GameObject shortSummary;
        [SerializeField] private string redZoneClosedText = "红区未开放";
        [SerializeField] private string redZoneOpenText = "红区已开放";
        [SerializeField] private string localTurnText = "轮到你行动";
        [SerializeField] private string waitingTurnText = "等待其他玩家";
        [SerializeField] private string otherPlayerTurnFormat = "{0}正在行动";
        [SerializeField] private string unnamedPlayerFormat = "玩家{0}";
        [SerializeField] private string foldVisibleText = "收起";
        [SerializeField] private string foldSuspendedText = "展开";
        [SerializeField] private string foldUnavailableText = "收起";
        [SerializeField] private string selectionSummaryFormat = "待处理选择：{0}～{1} 项";
        [SerializeField] private string skippableSelectionSummaryFormat = "待处理选择：{0}～{1} 项（可跳过）";
        [SerializeField] private string targetSummary = "待处理目标：请选择目标";
        [SerializeField] private string skippableTargetSummary = "待处理目标：请选择目标（可跳过）";
        [SerializeField] private string voucherReservationSummaryFormat = "{0}  |  金券（可用/预留）：{1}";
        [SerializeField] private string voucherReservationPlayerFormat = "{0}号 {1}/{2}";
        [SerializeField] private string voucherReservationPlayerSeparator = "；";
        [Header("阶段显示文案")]
        [SerializeField] private string setupPhaseText = "准备";
        [SerializeField] private string entrancePhaseText = "入场";
        [SerializeField] private string roundStartPhaseText = "回合开始";
        [SerializeField] private string characterCoverPhaseText = "盖放角色牌";
        [SerializeField] private string firstActionPhaseText = "第一行动轮";
        [SerializeField] private string secondActionPhaseText = "第二行动轮";
        [SerializeField] private string collectionPhaseText = "采集";
        [SerializeField] private string cleanupPhaseText = "收尾";
        [SerializeField] private string finalScoringPhaseText = "最终计分";
        [SerializeField] private string gameOverPhaseText = "终局";

        private static GameplayHudFrame active;
        private Color? turnTagDefaultColor;
        private int lastScreenWidth = -1;
        private int lastScreenHeight = -1;
        private Rect lastSafeArea;
        private float lastScaleFactor = -1f;
        private float lastContentScaleFactor = -1f;
        private Vector2 lastSurfaceSize;
        private Canvas contentCanvas;
        private GameObject visiblePage;
        private GameObject stagePage;
        private bool stagePageFoldable;
        private bool stagePageSuspended;
        private GameObject suspendedStageFocus;
        private GameObject suspendedEffectPage;
        private GameObject suspendedEffectFocus;
        private string currentRequestId;
        private string currentFlowId;
        private InteractionRequestProjection currentProjection;
        private string lastActionSummary;
        private string collectionCompletionSummary;
        private string voucherReservationSummary;
        private string routeSelectionSummary;
        private string interactionMessage;
        private GameplayTextDocument scopedInteractionMessageDocument;
        private string scopedMessageGameId, scopedMessageContentHash;
        private int scopedMessageViewer = -1, scopedMessageRevision = -1;
        private Func<bool> scopedMessageIsCurrent;
        private GameplayTextDocument legacyInteractionMessageDocument;
        private string legacyMessageGameId, legacyMessagePendingId, legacyMessageContentHash;
        private int legacyMessageViewer = -1, legacyMessageRevision = -1;
        private Func<bool> legacyMessageIsCurrent;
        private Func<string, string> legacyDefinitionNameResolver;
        private GameplayTextDocument interactionDocument;
        private InteractionRequestProjection interactionDocumentRequest;
        private string interactionDocumentGameId;
        private int interactionDocumentViewer = -1, interactionDocumentRevision = -1;
        private bool interactionMessageOverridesPrompt;
        private IGameplayTextCatalogSnapshot gameplayCatalog;
        private IReadOnlyDictionary<string, string> gameplayDefinitionNames;
        private GameplayTextResolver gameplayResolver;
        private LegacyGameplayTextAdapter legacyText;
        private GameplayTextContext gameplayContext;
        private GameStateView gameplaySnapshot;
        private readonly HashSet<GameplayTextView> configuredTextViews = new HashSet<GameplayTextView>();
        private bool summaryDefaultsCaptured, defaultLongSummary, defaultShortSummary;
        private int presentationGeneration;
        private string gameplayScope;
        private float interactionMessageExpiresAt;
        private bool mapViewportDirty;
        private int currentRevision;
        private string suspendedRequestId;
        private int suspendedRevision;
        private bool effectSuspended;
        private bool mapInteractionActive;
        private sealed class PageReturnContext
        {
            public GameObject Page, Focus;
            public string Key;
            public Func<bool> IsCurrent;
            public Action Restore;
            public string FocusPath;
            public ScrollRect[] Scrolls;
            public Vector2[] ScrollPositions;
        }
        private PageReturnContext actionPage, informationReturn;
        private GameObject informationPage;
        private bool informationNavigation;
        private string navigationGameId;
        private int navigationViewer = -1, informationReturnedFrame = -1;
        private readonly List<RectTransform> externalPages = new List<RectTransform>();

        public static GameplayHudFrame Active => active;
        public static bool InformationInputBlockedThisFrame => active != null && active.informationReturnedFrame == Time.frameCount;
        public static bool EffectInputSuspended => active != null && (InformationInputBlockedThisFrame ||
            active.informationNavigation || active.effectSuspended && (!active.mapInteractionActive || active.visiblePage != null));
        public RectTransform ContentRect => contentRect;
        public Canvas BarCanvas => barCanvas;
        public RectTransform TopBar => topBar;
        public RectTransform BottomBar => bottomBar;
        public Button SettingsButton => settingsButton;
        public Button FoldButton => foldButton;
        public Button EndActionButton => endActionButton;
        public string InteractionMessage => interactionMessage ?? string.Empty;
        public bool GameplayPresentationReady => gameplayContext != null;
        public int PresentationGeneration => presentationGeneration;

        public bool TryValidateConfiguration(out string reason)
        {
            var missing = new List<string>();
            void Require(string field, UnityEngine.Object value) { if (value == null) missing.Add(field + "=null"); }
            void RequireArray<T>(string field, T[] value, int count, bool minimum = false)
            {
                if (value == null) missing.Add(field + "=null");
                else if (minimum ? value.Length < count : value.Length != count)
                    missing.Add(field + ".Length=" + value.Length + "，要求" + (minimum ? "至少" : string.Empty) + count);
            }
            Require(nameof(barCanvas), barCanvas); Require(nameof(safeArea), safeArea);
            Require(nameof(topBar), topBar); Require(nameof(bottomBar), bottomBar);
            Require(nameof(contentRect), contentRect); Require(nameof(mapRegion), mapRegion);
            Require(nameof(mapViewport), mapViewport); Require(nameof(mapVisibleBounds), mapVisibleBounds);
            Require(nameof(mainSurface), mainSurface); Require(nameof(mainRegions), mainRegions);
            Require(nameof(columnsLayout), columnsLayout); Require(nameof(topContentLayout), topContentLayout);
            Require(nameof(mapSurroundLayout), mapSurroundLayout); Require(nameof(citySlotsLayout), citySlotsLayout);
            Require(nameof(topBarContent), topBarContent); Require(nameof(bottomBarContent), bottomBarContent);
            RequireArray(nameof(mapBackdrops), mapBackdrops, 4);
            Require(nameof(roundNumberText), roundNumberText); Require(nameof(roundTotalText), roundTotalText);
            Require(nameof(phaseText), phaseText); Require(nameof(redZoneText), redZoneText);
            Require(nameof(redZoneOpenRoundText), redZoneOpenRoundText); Require(nameof(turnTagText), turnTagText);
            Require(nameof(roundPlayerOrderGroup), roundPlayerOrderGroup);
            RequireArray(nameof(roundPlayerOrderMarkers), roundPlayerOrderMarkers, 4);
            if (roundPlayerOrderMarkers != null)
                for (var i = 0; i < roundPlayerOrderMarkers.Length; i++)
                {
                    var marker = roundPlayerOrderMarkers[i];
                    Require(nameof(roundPlayerOrderMarkers) + "[" + i + "]", marker);
                    if (marker != null && (marker.ShowNumber || !marker.TryValidateConfiguration(out _)))
                        missing.Add(nameof(roundPlayerOrderMarkers) + "[" + i + "]需要完整的无数字方格引用");
                }
            RequireArray(nameof(roundTicks), roundTicks, 1, true);
            Require(nameof(summaryText), summaryText); Require(nameof(shortSummaryText), shortSummaryText);
            Require(nameof(summaryDocumentView), summaryDocumentView); Require(nameof(shortSummaryDocumentView), shortSummaryDocumentView);
            if (summaryDocumentView != null && summaryDocumentView.GetComponent<Text>() != summaryText)
                missing.Add(nameof(summaryDocumentView) + ".Text与" + nameof(summaryText) + "不是同一组件（" + ConfigurationObjectPath(summaryDocumentView.transform) + "）");
            if (shortSummaryDocumentView != null && shortSummaryDocumentView.GetComponent<Text>() != shortSummaryText)
                missing.Add(nameof(shortSummaryDocumentView) + ".Text与" + nameof(shortSummaryText) + "不是同一组件（" + ConfigurationObjectPath(shortSummaryDocumentView.transform) + "）");
            Require(nameof(gameplayTextStyle), gameplayTextStyle);
            if (gameplayTextStyle != null) Require(nameof(gameplayTextStyle) + ".FontRoles（" + gameplayTextStyle.name + "）", gameplayTextStyle.FontRoles);
            Require(nameof(gameplayIconCatalog), gameplayIconCatalog); Require(nameof(scoreValueText), scoreValueText);
            RequireArray(nameof(resourceValueTexts), resourceValueTexts, 5);
            Require(nameof(settingsButton), settingsButton); Require(nameof(foldButton), foldButton);
            Require(nameof(foldButtonText), foldButtonText); Require(nameof(undoButton), undoButton); Require(nameof(endActionButton), endActionButton);
            if (missing.Count > 0)
            {
                reason = "常驻栏或内容区的预制体引用不完整：" + ConfigurationObjectPath(transform) + " / GameplayHudFrame；" + string.Join("；", missing) + "。";
                return false;
            }
            if (barCanvas.sortingOrder != GameplayUiLayers.PersistentBars ||
                !endActionButton.transform.IsChildOf(bottomBar) ||
                topBar.parent != safeArea || bottomBar.parent.parent != safeArea)
            {
                reason = "常驻栏层级或结束行动入口不在预期位置。";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        private static string ConfigurationObjectPath(Transform value)
        {
            var names = new List<string>();
            for (var current = value; current != null; current = current.parent) names.Add(current.name);
            names.Reverse();
            return string.Join("/", names);
        }

        private void Awake()
        {
            active = this;
            CaptureSummaryDefaults();
            Canvas.willRenderCanvases += ReadCompletedLayout;
            if (contentRect != null) contentCanvas = contentRect.GetComponentInParent<Canvas>();
            InteractionRequestRouter.RequestRouted += HandleRequestRouted;
            if (barCanvas != null) barCanvas.sortingOrder = GameplayUiLayers.PersistentBars;
            if (settingsButton != null)
            {
                settingsButton.onClick.RemoveAllListeners();
                settingsButton.onClick.AddListener(OpenSettings);
            }
            if (foldButton != null)
            {
                foldButton.onClick.RemoveAllListeners();
                foldButton.onClick.AddListener(ToggleEffectPage);
            }
            if (undoButton != null) undoButton.interactable = false;
            ApplyLayout(true);
            UpdateFoldButton();
        }

        private void OnDestroy()
        {
            Canvas.willRenderCanvases -= ReadCompletedLayout;
            InteractionRequestRouter.RequestRouted -= HandleRequestRouted;
            if (active == this) active = null;
        }

        private readonly Vector3[] completedMapCorners = new Vector3[4];
        private Rect completedMapRect;

        private void ReadCompletedLayout()
        {
            if (mapRegion == null) return;
            mapRegion.GetWorldCorners(completedMapCorners);
            var rect = Rect.MinMaxRect(completedMapCorners[0].x, completedMapCorners[0].y,
                completedMapCorners[2].x, completedMapCorners[2].y);
            if (rect != completedMapRect)
            {
                completedMapRect = rect;
                mapViewportDirty = true;
            }
            // 展开详情会在 Update 之后改变原生布局；同帧同步真实相机视口，
            // 避免只缩小外框、地图仍绘制到刚腾出的详情区域。
            if (mapViewportDirty)
            {
                mapViewportDirty = false;
                ApplyMapViewport();
                RefreshMapOpening();
                if (mapSurroundLayout != null) mapSurroundLayout.Refresh();
                ApplyBackdrops();
            }
            else RefreshMapOpening();
            for (var i = externalPages.Count - 1; i >= 0; i--)
            {
                if (externalPages[i] == null) externalPages.RemoveAt(i);
                else ApplyExternalPageBounds(externalPages[i]);
            }
        }

        private void HandleRequestRouted(InteractionRequestProjection projection)
        {
            if (projection != null && !string.IsNullOrEmpty(currentRequestId) &&
                projection.StateRevision < currentRevision) return;
            currentProjection = projection;
            if (projection == null) ClearRequest();
            else SetRequestWithFlow(projection.InteractionId, projection.StateRevision,
                string.IsNullOrEmpty(projection.OwnerEffectId)
                    ? projection.SourceNodeId : projection.OwnerEffectId);
            UpdateSummary();
        }

        private void Update()
        {
            if (!informationNavigation && stagePage != null && !stagePageSuspended && (visiblePage == null || !visiblePage.activeSelf))
                ShowPage(stagePage, false);
            if (barCanvas != null && (lastScreenWidth != Screen.width || lastScreenHeight != Screen.height ||
                lastSafeArea != Screen.safeArea || lastScaleFactor != barCanvas.scaleFactor ||
                (mainSurface != null && lastSurfaceSize != mainSurface.rect.size) ||
                (contentCanvas != null && lastContentScaleFactor != contentCanvas.scaleFactor)))
                ApplyLayout(false);
        }

        public void Refresh(GameState state, ActionPanelViewModel action, string endUnavailableReason,
            string collectionCompletion = null, GameStateView publicState = null)
        {
            BindGameplaySnapshot(publicState);
            RefreshRoundPlayerOrder(publicState);
            var game = publicState?.GameId ?? state?.GameId;
            var viewer = publicState?.ViewerPlayerId ?? -1;
            if (navigationGameId != game || navigationViewer != viewer)
            {
                informationReturn = actionPage = null;
                informationPage = null;
                informationNavigation = false;
                navigationGameId = game;
                navigationViewer = viewer;
            }
            RefreshVoucherReservationSummary(publicState);
            if (state == null || action == null) { UpdateSummary(); return; }
            if (roundNumberText != null) roundNumberText.text = state.Round.ToString();
            if (roundTotalText != null) roundTotalText.text = "/ " + state.MaxRounds + " 回合";
            if (phaseText != null) phaseText.text = GetPhaseText(state.Phase);
            if (turnTagText != null)
            {
                if (!turnTagDefaultColor.HasValue) turnTagDefaultColor = turnTagText.color;
                var currentPlayer = state.FindPlayer(state.CurrentPlayerId);
                var isLocalTurn = currentPlayer != null && action.HasLocalPlayer &&
                                  currentPlayer.Color == action.LocalPlayerColor;
                if (currentPlayer != null && !isLocalTurn)
                {
                    var playerName = string.IsNullOrWhiteSpace(currentPlayer.Name)
                        ? string.Format(unnamedPlayerFormat, currentPlayer.PlayerId)
                        : currentPlayer.Name;
                    turnTagText.text = string.Format(otherPlayerTurnFormat, playerName);
                    turnTagText.color = UiTheme.GetPlayerColor(currentPlayer.Color, 1f);
                }
                else
                {
                    turnTagText.text = isLocalTurn ? localTurnText : waitingTurnText;
                    turnTagText.color = turnTagDefaultColor.Value;
                }
            }
            if (roundTicks != null)
            {
                for (var i = 0; i < roundTicks.Length; i++)
                {
                    var tick = roundTicks[i];
                    if (tick == null) continue;
                    tick.gameObject.SetActive(i < state.MaxRounds);
                    tick.color = i < state.Round
                        ? new Color(.75f, .54f, .13f, 1f)
                        : new Color(.67f, .64f, .56f, .7f);
                    tick.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                        i == state.Round - 1 ? 4f : 2f);
                }
            }
            lastActionSummary = action.CanEndAction ? action.StatusText : endUnavailableReason;
            collectionCompletionSummary = action.AllCollectionLocationsSelected ? collectionCompletion : null;
            UpdateSummary();
            if (redZoneText != null)
            {
                var map = state.MapId == StaticMapDefinitions.FourPlayerMapId ||
                          state.MapId == StaticMapDefinitions.ThreePlayerMapId
                    ? StaticMapDefinitions.Resolve(state.MapId) : null;
                var openRound = RedZoneAccessRule.GetOpenRound(map, state.Players.Count);
                redZoneText.text = state.Round >= openRound ? redZoneOpenText : redZoneClosedText;
                if (redZoneOpenRoundText != null)
                    redZoneOpenRoundText.text = "第 " + openRound + " 回合起";
            }
            if (endActionButton != null) endActionButton.interactable = action.CanEndAction;
            var localPlayer = state.Players.Find(player => player.Color == action.LocalPlayerColor);
            if (scoreValueText != null)
                scoreValueText.text = localPlayer == null ? string.Empty : localPlayer.Score.ToString();
            if (localPlayer != null && resourceValueTexts != null && resourceValueTexts.Length == 5)
            {
                var resources = localPlayer.Resources;
                var values = new[] { resources.Originium, resources.OriginiumShard,
                    resources.Iron, resources.PureOriginium, resources.GoldVoucher };
                for (var i = 0; i < resourceValueTexts.Length; i++)
                    if (resourceValueTexts[i] != null) resourceValueTexts[i].text = values[i].ToString();
            }
        }

        public void SetRequest(string interactionId, int revision)
        {
            SetRequestWithFlow(interactionId, revision, null);
        }

        private void SetRequestWithFlow(string interactionId, int revision, string flowId)
        {
            if (currentRequestId != interactionId || revision != currentRevision)
            {
                ClearInteractionDocument();
                interactionMessageOverridesPrompt = GetScopedInteractionMessageDocument() != null || GetLegacyInteractionMessageDocument() != null;
            }
            var replaced = currentRequestId != interactionId || revision < currentRevision;
            var sameFlow = !string.IsNullOrEmpty(flowId) && flowId == currentFlowId;
            if (replaced)
            {
                routeSelectionSummary = null;
                mapInteractionActive = false;
                if (!sameFlow) effectSuspended = false;
                if (visiblePage != null && visiblePage == suspendedEffectPage)
                {
                    visiblePage.SetActive(false);
                    visiblePage = null;
                }
                suspendedEffectPage = null;
                suspendedEffectFocus = null;
                suspendedRequestId = null;
            }
            currentRequestId = interactionId;
            currentFlowId = flowId;
            currentRevision = revision;
            if (suspendedEffectPage != null &&
                (interactionId != suspendedRequestId || revision < suspendedRevision))
            {
                suspendedEffectPage = null;
                suspendedEffectFocus = null;
                suspendedRequestId = null;
            }
            else if (suspendedEffectPage != null)
            {
                suspendedRevision = revision;
            }
            UpdateFoldButton();
        }

        public void ClearRequest()
        {
            ClearInteractionDocument();
            interactionMessageOverridesPrompt = GetScopedInteractionMessageDocument() != null || GetLegacyInteractionMessageDocument() != null;
            routeSelectionSummary = null;
            mapInteractionActive = false;
            currentRequestId = null;
            currentFlowId = null;
            currentProjection = null;
            effectSuspended = false;
            suspendedRequestId = null;
            suspendedEffectPage = null;
            suspendedEffectFocus = null;
            UpdateFoldButton();
        }

        public void SetInteractionMessage(string message, string pendingPrompt = null)
        {
            ClearScopedInteractionMessageDocument();
            ClearLegacyInteractionMessageDocument();
            ClearInteractionDocument();
            if (!string.IsNullOrEmpty(pendingPrompt) &&
                (string.IsNullOrEmpty(message) || !message.Contains(pendingPrompt)))
                message = string.IsNullOrEmpty(message) ? pendingPrompt : message + "\n" + pendingPrompt;
            interactionMessage = string.IsNullOrWhiteSpace(message) ? null : message;
            interactionMessageOverridesPrompt = interactionMessage != null;
            interactionMessageExpiresAt = Time.unscaledTime + 3f;
            UpdateSummary();
        }

        public void SetScopedInteractionMessageDocument(string gameId, int viewerPlayerId, int revision,
            GameplayTextDocument document, Func<bool> isCurrent)
        {
            if (!BindScopedInteractionMessageDocument(gameId, viewerPlayerId, revision, document, isCurrent)) return;
            if (document != null)
            {
                ClearLegacyInteractionMessageDocument();
                ClearInteractionDocument();
                interactionMessage = null;
                interactionMessageOverridesPrompt = true;
                interactionMessageExpiresAt = Time.unscaledTime + 3f;
            }
            UpdateSummary();
        }

        private bool BindScopedInteractionMessageDocument(string gameId, int viewerPlayerId, int revision,
            GameplayTextDocument document, Func<bool> isCurrent)
        {
            if (document == null)
            {
                if (scopedMessageGameId != gameId || scopedMessageViewer != viewerPlayerId || scopedMessageRevision != revision) return false;
                ClearScopedInteractionMessageDocument();
                return true;
            }
            var view = gameplaySnapshot;
            if (view == null || view.ViewerRole != GameStateViewerRole.Player || view.GameId != gameId ||
                view.ViewerPlayerId != viewerPlayerId || view.StateRevision != revision || isCurrent?.Invoke() != true) return false;
            scopedInteractionMessageDocument = document;
            scopedMessageGameId = gameId; scopedMessageViewer = viewerPlayerId; scopedMessageRevision = revision;
            scopedMessageContentHash = view.ContentPackHash; scopedMessageIsCurrent = isCurrent;
            return true;
        }

        private GameplayTextDocument GetScopedInteractionMessageDocument()
        {
            if (scopedInteractionMessageDocument == null) return null;
            var view = gameplaySnapshot;
            if (view != null && view.ViewerRole == GameStateViewerRole.Player && view.GameId == scopedMessageGameId &&
                view.ViewerPlayerId == scopedMessageViewer && view.StateRevision == scopedMessageRevision &&
                view.ContentPackHash == scopedMessageContentHash && scopedMessageIsCurrent?.Invoke() == true)
                return scopedInteractionMessageDocument;
            ClearScopedInteractionMessageDocument();
            return null;
        }

        private void ClearScopedInteractionMessageDocument()
        {
            var hadDocument = scopedInteractionMessageDocument != null;
            scopedInteractionMessageDocument = null; scopedMessageGameId = scopedMessageContentHash = null;
            scopedMessageViewer = scopedMessageRevision = -1; scopedMessageIsCurrent = null;
            if (!hadDocument) return;
            interactionMessageOverridesPrompt = false;
            if (summaryDocumentView is GameplayTextView longView) longView.ClearBinding();
            if (shortSummaryDocumentView is GameplayTextView shortView) shortView.ClearBinding();
        }

        public void SetLegacyInteractionMessageDocument(string gameId, int viewerPlayerId, int revision,
            string pendingId, GameplayTextDocument document, Func<bool> isCurrent)
        {
            if (!BindLegacyInteractionMessageDocument(gameId, viewerPlayerId, revision, pendingId, document, isCurrent)) return;
            if (document != null)
            {
                ClearScopedInteractionMessageDocument();
                ClearInteractionDocument();
                interactionMessage = null;
                interactionMessageOverridesPrompt = true;
                interactionMessageExpiresAt = Time.unscaledTime + 3f;
            }
            UpdateSummary();
        }

        private bool BindLegacyInteractionMessageDocument(string gameId, int viewerPlayerId, int revision,
            string pendingId, GameplayTextDocument document, Func<bool> isCurrent)
        {
            if (document == null)
            {
                if (legacyMessageGameId != gameId || legacyMessageViewer != viewerPlayerId ||
                    legacyMessageRevision != revision || legacyMessagePendingId != pendingId) return false;
                ClearLegacyInteractionMessageDocument();
                return true;
            }
            if (!MatchesLegacyMessage(gameId, viewerPlayerId, revision, pendingId, gameplaySnapshot) ||
                isCurrent?.Invoke() != true) return false;
            legacyInteractionMessageDocument = document;
            legacyMessageGameId = gameId;
            legacyMessageViewer = viewerPlayerId;
            legacyMessageRevision = revision;
            legacyMessagePendingId = pendingId;
            legacyMessageContentHash = gameplaySnapshot.ContentPackHash;
            legacyMessageIsCurrent = isCurrent;
            return true;
        }

        private static bool MatchesLegacyMessage(string gameId, int viewerPlayerId, int revision,
            string pendingId, GameStateView view)
        {
            if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(pendingId) || view == null ||
                view.ViewerRole != GameStateViewerRole.Player || view.ViewerPlayerId != viewerPlayerId ||
                view.GameId != gameId || view.StateRevision != revision) return false;
            var matches = view.Interactions.Where(value => value.InteractionId == pendingId).Take(2).ToArray();
            return matches.Length == 1 && matches[0].AnsweringPlayerId == viewerPlayerId &&
                matches[0].Visibility == GameStateVisibilityPolicy.Owner && matches[0].Status == "open" &&
                (matches[0].Kind == YC.Domain.Cards.CharacterPendingChoiceTypes.TinManDiscard ||
                 matches[0].Kind == YC.Domain.Cards.CharacterPendingChoiceTypes.TinManFirstPurchase ||
                 matches[0].Kind == YC.Domain.Cards.CharacterPendingChoiceTypes.TinManSecondPurchase);
        }

        private GameplayTextDocument GetLegacyInteractionMessageDocument()
        {
            if (legacyInteractionMessageDocument == null) return null;
            if (MatchesLegacyMessage(legacyMessageGameId, legacyMessageViewer, legacyMessageRevision,
                    legacyMessagePendingId, gameplaySnapshot) &&
                gameplaySnapshot.ContentPackHash == legacyMessageContentHash && legacyMessageIsCurrent?.Invoke() == true &&
                !MatchesSummaryRequest(currentProjection, gameplaySnapshot)) return legacyInteractionMessageDocument;
            ClearLegacyInteractionMessageDocument();
            return null;
        }

        private void ClearLegacyInteractionMessageDocument()
        {
            var hadDocument = legacyInteractionMessageDocument != null;
            legacyInteractionMessageDocument = null;
            legacyMessageGameId = legacyMessagePendingId = legacyMessageContentHash = null;
            legacyMessageViewer = legacyMessageRevision = -1;
            legacyMessageIsCurrent = null;
            if (!hadDocument) return;
            interactionMessageOverridesPrompt = false;
            if (summaryDocumentView is GameplayTextView longView) longView.ClearBinding();
            if (shortSummaryDocumentView is GameplayTextView shortView) shortView.ClearBinding();
        }

        internal static bool MatchesSummaryRequest(InteractionRequestProjection request, GameStateView view)
        {
            if (request == null || view == null || view.ViewerRole != GameStateViewerRole.Player ||
                request.AnsweringPlayerId != view.ViewerPlayerId || !request.VisibleToViewer || request.Status != "open" ||
                view.StateRevision < request.StateRevision) return false;
            var matches = view.Interactions?.Where(value => value != null && value.InteractionId == request.InteractionId).Take(2).ToArray();
            if (matches == null || matches.Length != 1) return false;
            var match = matches[0];
            return match.SourceNodeId == request.SourceNodeId && match.OwnerEffectId == request.OwnerEffectId &&
                match.Kind == request.InteractionTypeId && match.PromptKey == request.PromptKey &&
                match.AnsweringPlayerId == request.AnsweringPlayerId && match.Visibility == request.Visibility &&
                match.StateRevision == request.StateRevision && match.CandidateSetId == request.CandidateSetId &&
                match.CandidateSetVersion == request.CandidateSetVersion && match.MinSelections == request.MinSelections &&
                match.MaxSelections == request.MaxSelections && match.AllowDecline == request.AllowDecline &&
                match.AnswerSchema == request.AnswerSchema && match.Status == request.Status &&
                match.CandidateIds != null && request.CandidateIds != null && match.CandidateIds.SequenceEqual(request.CandidateIds);
        }

        public void SetInteractionDocument(InteractionRequestProjection request, GameplayTextDocument document)
        {
            if (document == null)
            {
                if (request == null || interactionDocumentRequest?.InteractionId == request.InteractionId &&
                    interactionDocumentRequest.StateRevision == request.StateRevision &&
                    InteractionCommandIntent.SameContract(interactionDocumentGameId, interactionDocumentRequest, interactionDocumentGameId, request))
                    ClearInteractionDocument();
                return;
            }
            if (!MatchesSummaryRequest(request, gameplaySnapshot) || currentProjection == null ||
                currentProjection.StateRevision != request.StateRevision ||
                !InteractionCommandIntent.SameContract(gameplaySnapshot.GameId, currentProjection, gameplaySnapshot.GameId, request)) return;
            if (ReferenceEquals(interactionDocument, document) && GetInteractionDocument() != null) return;
            ClearScopedInteractionMessageDocument();
            ClearLegacyInteractionMessageDocument();
            interactionDocument = document;
            interactionDocumentRequest = GameStateCloneService.DeepClone(request);
            interactionDocumentGameId = gameplaySnapshot.GameId;
            interactionDocumentViewer = gameplaySnapshot.ViewerPlayerId;
            interactionDocumentRevision = gameplaySnapshot.StateRevision;
            interactionMessage = null;
            interactionMessageOverridesPrompt = false;
            UpdateSummary();
        }

        private GameplayTextDocument GetInteractionDocument()
        {
            if (interactionDocument == null) return null;
            if (gameplaySnapshot?.GameId == interactionDocumentGameId && gameplaySnapshot.ViewerPlayerId == interactionDocumentViewer &&
                gameplaySnapshot.StateRevision == interactionDocumentRevision && MatchesSummaryRequest(interactionDocumentRequest, gameplaySnapshot) &&
                currentProjection?.StateRevision == interactionDocumentRequest.StateRevision &&
                InteractionCommandIntent.SameContract(interactionDocumentGameId, interactionDocumentRequest, interactionDocumentGameId, currentProjection))
                return interactionDocument;
            ClearInteractionDocument();
            return null;
        }

        private void ClearInteractionDocument()
        {
            interactionDocument = null;
            interactionDocumentRequest = null;
            interactionDocumentGameId = null;
            interactionDocumentViewer = interactionDocumentRevision = -1;
            if (summaryDocumentView is GameplayTextView longView) longView.ClearBinding();
            if (shortSummaryDocumentView is GameplayTextView shortView) shortView.ClearBinding();
        }

        private InteractionView GetSummaryRequest()
        {
            if (gameplaySnapshot == null) return null;
            if (!string.IsNullOrEmpty(currentRequestId))
                return currentProjection?.InteractionId == currentRequestId && MatchesSummaryRequest(currentProjection, gameplaySnapshot)
                    ? gameplaySnapshot.Interactions.Single(value => value.InteractionId == currentRequestId) : null;
            return gameplaySnapshot.Interactions.LastOrDefault(value => value.Status == "open" && value.AnsweringPlayerId == gameplaySnapshot.ViewerPlayerId);
        }

        public void SetRouteSelectionSummary(string summary)
        {
            routeSelectionSummary = summary;
            UpdateSummary();
        }

        public void UpdateInteractionMessage(bool keepVisible)
        {
            GetScopedInteractionMessageDocument();
            GetLegacyInteractionMessageDocument();
            if (interactionMessage == null && legacyInteractionMessageDocument == null && scopedInteractionMessageDocument == null) return;
            if (keepVisible) interactionMessageExpiresAt = Time.unscaledTime + 3f;
            else if (Time.unscaledTime >= interactionMessageExpiresAt)
            {
                ClearScopedInteractionMessageDocument();
                ClearLegacyInteractionMessageDocument();
                interactionMessage = null;
                interactionMessageOverridesPrompt = false;
                UpdateSummary();
            }
        }

        private void UpdateSummary()
        {
            if (gameplayContext == null) return;
            var document = GetScopedInteractionMessageDocument() ?? GetLegacyInteractionMessageDocument() ?? GetInteractionDocument();
            var request = GetSummaryRequest();
            if (interactionMessageOverridesPrompt && !string.IsNullOrEmpty(interactionMessage))
                document = legacyText.Resolve(interactionMessage, LegacyGameplayTextInputKind.Literal, gameplayContext);
            else if (document == null && request?.Presentation?.Prompt != null)
                document = ResolveGameplayText(request.Presentation.Prompt);
            var message = gameplaySnapshot?.ResolutionMessage;
            if (document == null && message?.Message != null && message.ViewerPlayerId == gameplayContext.ViewerPlayerId &&
                message.StateRevision == gameplayContext.StateRevision)
                document = ResolveGameplayText(message.Message);
            var summary = lastActionSummary;
            if (request != null)
            {
                var skippable = request.AllowDecline;
                summary = request.MaxSelections > 0
                    ? string.Format(skippable ? skippableSelectionSummaryFormat :
                        selectionSummaryFormat, request.MinSelections,
                        request.MaxSelections)
                    : skippable ? skippableTargetSummary : targetSummary;
            }
            if (!string.IsNullOrEmpty(routeSelectionSummary)) summary = routeSelectionSummary;
            if (request == null && !string.IsNullOrEmpty(interactionMessage)) summary = interactionMessage;
            if (!string.IsNullOrEmpty(collectionCompletionSummary)) summary += "\n" + collectionCompletionSummary;
            if (!string.IsNullOrEmpty(voucherReservationSummary))
            {
                summary = string.Format(voucherReservationSummaryFormat,
                    summary ?? string.Empty, voucherReservationSummary);
                summary = summary.TrimStart(' ', '|');
            }
            if (document == null) document = legacyText.Resolve(summary ?? string.Empty, LegacyGameplayTextInputKind.Literal, gameplayContext);
            RenderGameplayText(summaryDocumentView, document, false);
            RenderGameplayText(shortSummaryDocumentView, document, false);
        }

        public void ConfigureGameplayPresentation(IGameplayTextCatalogSnapshot catalog, IReadOnlyDictionary<string, string> definitionNames)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (gameplayTextStyle == null || gameplayIconCatalog == null) throw new InvalidOperationException("玩法文字资产尚未接线。");
            foreach (var view in configuredTextViews) if (view != null) view.ClearBinding();
            configuredTextViews.Clear(); gameplayCatalog = catalog;
            GameplayIconResourceResolver.UseCatalog(catalog);
            ClearScopedInteractionMessageDocument();
            ClearLegacyInteractionMessageDocument();
            ClearInteractionDocument();
            interactionMessageOverridesPrompt = false;
            presentationGeneration++;
            gameplayDefinitionNames = definitionNames ?? throw new ArgumentNullException(nameof(definitionNames));
            gameplayScope = null; gameplayContext = null; gameplaySnapshot = null;
        }

        public void BindGameplaySnapshot(GameStateView view)
        {
            if (ReferenceEquals(gameplaySnapshot, view) && gameplayContext != null) return;
            if (view == null || view.ViewerRole != GameStateViewerRole.Player || gameplayCatalog == null ||
                gameplayCatalog.ContentHash != view.ContentPackHash)
            {
                ClearScopedInteractionMessageDocument();
                ClearLegacyInteractionMessageDocument();
                ClearInteractionDocument();
                interactionMessageOverridesPrompt = false;
                gameplaySnapshot = null; gameplayContext = null;
                foreach (var text in configuredTextViews) if (text != null) text.ClearBinding();
                return;
            }
            string scope = view.GameId + ":" + view.ViewerPlayerId + ":" + view.ContentPackHash;
            if (scope != gameplayScope)
            {
                foreach (var text in configuredTextViews) if (text != null) text.ClearBinding();
                ClearScopedInteractionMessageDocument();
                ClearLegacyInteractionMessageDocument();
                ClearInteractionDocument();
                interactionMessageOverridesPrompt = false;
                configuredTextViews.Clear(); interactionMessage = routeSelectionSummary = null;
                gameplayScope = scope; gameplayResolver = gameplayTextStyle.CreateResolver(gameplayCatalog, scope);
                presentationGeneration++;
                // 完整目录语法与加载失败由共享正式装配统一预编译；此处仅创建观察者解析作用域。
                legacyText = new LegacyGameplayTextAdapter(gameplayResolver);
            }
            var playerNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var player in view.Players)
                if (!string.IsNullOrWhiteSpace(player.Name))
                {
                    string id = player.PlayerId.ToString(CultureInfo.InvariantCulture);
                    playerNames[id] = player.Name; playerNames["p" + id] = player.Name;
                }
            var definitions = BuildVisibleGameplayDefinitionNames(view, gameplayDefinitionNames, legacyDefinitionNameResolver);
            var items = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in view.ItemZones.Items) if (definitions.TryGetValue(item.DefinitionId, out var name)) items[item.ItemId] = name;
            gameplaySnapshot = view;
            GetScopedInteractionMessageDocument();
            GetLegacyInteractionMessageDocument();
            GetInteractionDocument();
            gameplayContext = new GameplayTextContext { VisibilityScopeId = scope, Catalog = gameplayCatalog,
                ContentHash = view.ContentPackHash, Locale = gameplayCatalog.Locale, ViewerPlayerId = view.ViewerPlayerId,
                StateRevision = view.StateRevision, PlayerNames = playerNames, ItemNames = items, DefinitionNames = definitions,
                SemanticKeywords = gameplayTextStyle.GetKeywordSnapshot() };
            // 资源名称和图标均由正式目录声明，缺项由统一解析器产生诊断。
            var resourceNames = new Dictionary<string, string>(StringComparer.Ordinal);
            var resourceIcons = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in new[] { new[] { "core:resource.orirock", "originium", "Originium" }, new[] { "core:resource.originium_shard", "originium_shard", "OriginiumShard" },
                new[] { "core:resource.iron", "iron", "Iron" }, new[] { "core:resource.pure_originium", "pure_originium", "PureOriginium" }, new[] { "core:resource.gold_voucher", "gold_voucher", "GoldVoucher" } })
                if (gameplayCatalog.TryGetIcon(pair[0], out var icon) && icon.AltText != null)
                    foreach (var id in pair.Skip(1)) { resourceNames[id] = gameplayResolver.Resolve(icon.AltText, gameplayContext).ToPlainText(); resourceIcons[id] = icon.Id; }
            gameplayContext.ResourceNames = resourceNames; gameplayContext.ResourceIconKeys = resourceIcons;
            UpdateSummary();
        }

        public void BindLegacyGameplayDefinitionNames(Func<string, string> resolveVisibleName)
        {
            legacyDefinitionNameResolver = resolveVisibleName;
            ClearScopedInteractionMessageDocument();
            ClearLegacyInteractionMessageDocument();
            ClearInteractionDocument();
            gameplayContext = null;
            presentationGeneration++;
        }

        private void RefreshRoundPlayerOrder(GameStateView view)
        {
            if (roundPlayerOrderGroup == null || roundPlayerOrderMarkers == null) return;
            var order = view?.CurrentRoundPlayerOrder;
            var hasOrder = order != null && order.Count >= 2 && order.Count <= roundPlayerOrderMarkers.Length &&
                view.Players != null && view.Players.All(player => player != null) &&
                TurnOrderService.IsValidPlayerOrder(order, view.Players.Select(player => player.PlayerId));
            for (var i = 0; i < roundPlayerOrderMarkers.Length; i++)
            {
                var marker = roundPlayerOrderMarkers[i];
                if (marker == null) continue;
                var visible = hasOrder && i < order.Count;
                marker.gameObject.SetActive(visible);
                if (visible) marker.RenderPlayer(view.Players.Find(player => player.PlayerId == order[i]).Color, 0);
            }
            roundPlayerOrderGroup.SetActive(hasOrder);
        }

        private static Dictionary<string, string> BuildVisibleGameplayDefinitionNames(GameStateView view,
            IReadOnlyDictionary<string, string> knownNames, Func<string, string> resolveVisibleLegacyName)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (view == null || view.ViewerRole != GameStateViewerRole.Player || knownNames == null) return result;
            foreach (var id in view.ItemZones.Items.Select(item => item.DefinitionId)
                .Concat(view.CharacterSettlementOptions.Select(option => option.SourceDefinitionId)).Distinct())
                if (!string.IsNullOrEmpty(id) && knownNames.TryGetValue(id, out var name) && !string.IsNullOrEmpty(name)) result[id] = name;
            foreach (var player in view.Players.Where(value => value.PlayerId == view.ViewerPlayerId && value.PrivateCardIdsVisible))
                foreach (var id in player.HandCardIds.Concat(player.DiscardCardIds)
                    .Concat(new[] { player.CoveredCharacterCardId }).Where(value => !string.IsNullOrEmpty(value)).Distinct())
                {
                    if (result.ContainsKey(id)) continue;
                    var name = knownNames.TryGetValue(id, out var known) ? known : resolveVisibleLegacyName?.Invoke(id);
                    if (!string.IsNullOrEmpty(name)) result[id] = name;
                }
            return result;
        }

        public GameplayTextDocument ResolveGameplayText(GameplayTextRef text)
        {
            using var measurement = UiRegressionMetrics.Measure("text.resolve");
            return text == null || gameplayContext == null ? null : gameplayResolver.Resolve(text, gameplayContext);
        }
        private GameplayTextView ConfigureTextView(Component component)
        {
            var view = component as GameplayTextView;
            if (view == null || gameplayCatalog == null) throw new InvalidOperationException("玩法文案宿主或活动目录缺失。");
            if (configuredTextViews.Add(view)) view.Configure(view.GetComponent<Text>(), gameplayTextStyle, gameplayIconCatalog, gameplayCatalog);
            return view;
        }
        public void RenderGameplayText(Component component, GameplayTextDocument document, bool singleLine)
        {
            using var measurement = UiRegressionMetrics.Measure("text.render");
            if (component == null) throw new InvalidOperationException("玩法文案宿主缺失。");
            if (document == null) { ((GameplayTextView)component).ClearBinding(); return; }
            var view = ConfigureTextView(component);
            // Chip 已按完整测量和 padding 限宽；View 每次读最终标签框，增宽后可恢复全文。
            if (singleLine) view.BindSingleLineEllipsized(document, float.MaxValue);
            else view.Bind(document);
        }
        public float MeasureGameplayText(Component component, GameplayTextDocument document) => ConfigureTextView(component).MeasureSingleLineWidth(document);
        public Sprite ResolveGameplayIcon(string key, string tone)
        {
            if (gameplayCatalog != null && gameplayCatalog.TryGetIcon(key, out var icon) &&
                GameplayIconResourceResolver.TryResolve(gameplayCatalog, gameplayIconCatalog, icon, tone == "light" ? GameplayIconTone.Light : tone == "muted" ? GameplayIconTone.Muted : GameplayIconTone.Dark, out var sprite)) return sprite;
            return null;
        }
        private void CaptureSummaryDefaults()
        {
            if (summaryDefaultsCaptured) return;
            defaultLongSummary = longSummary != null && longSummary.activeSelf;
            defaultShortSummary = shortSummary != null && shortSummary.activeSelf;
            summaryDefaultsCaptured = true;
        }
        public void SetSettlementButtonsVisible(bool visible)
        {
            CaptureSummaryDefaults();
            if (longSummary != null) longSummary.SetActive(!visible && defaultLongSummary);
            if (shortSummary != null) shortSummary.SetActive(!visible && defaultShortSummary);
            if (!visible) UpdateSummary();
        }

        private void RefreshVoucherReservationSummary(GameStateView publicState)
        {
            voucherReservationSummary = null;
            var players = publicState == null ? null : publicState.Players;
            if (players == null) return;
            var hasReservation = false;
            for (var i = 0; i < players.Count; i++)
                if (players[i] != null && players[i].ReservedGoldVoucher > 0)
                    hasReservation = true;
            if (!hasReservation) return;
            var balances = new List<string>(players.Count);
            foreach (var id in new TurnOrderService().GetLaunchPlayerOrder(publicState))
            {
                var player = players.Find(p => p.PlayerId == id);
                if (player == null) continue;
                balances.Add(string.Format(voucherReservationPlayerFormat, player.PlayerId,
                    player.AvailableGoldVoucher, player.ReservedGoldVoucher));
            }
            voucherReservationSummary = string.Join(voucherReservationPlayerSeparator, balances);
        }

        public void ShowStagePage(GameObject page)
        {
            if (stagePage != page)
            {
                stagePageFoldable = false;
                suspendedStageFocus = null;
            }
            stagePage = page;
            stagePageSuspended = false;
            ShowPage(page, false);
        }

        /// <summary>登记已有行动页面；临时查看资料不结束其草稿或规则请求。</summary>
        public void RegisterActionPage(GameObject page, string key, Func<bool> isCurrent, Action restore)
        {
            if (page == null) return;
            actionPage = new PageReturnContext { Page = page, Key = key, IsCurrent = isCurrent, Restore = restore };
        }

        public void ShowInformationPage(GameObject page)
        {
            if (page == null) return;
            if (!informationNavigation)
            {
                var source = visiblePage;
                var focus = EventSystem.current?.currentSelectedGameObject;
                informationReturn = null;
                if (source != null && actionPage?.Page == source && actionPage.IsCurrent() &&
                    !(source == suspendedEffectPage && CanFoldEffectPage))
                {
                    var registered = actionPage;
                    informationReturn = new PageReturnContext { Page = source, Focus = focus, Key = registered.Key,
                        IsCurrent = () => actionPage?.Page == source && actionPage.Key == registered.Key && registered.IsCurrent(),
                        Restore = registered.Restore };
                }
                else if ((source == null || source == suspendedEffectPage) && CanFoldEffectPage)
                {
                    source = suspendedEffectPage;
                    var request = currentRequestId;
                    var revision = currentRevision;
                    var candidateSet = currentProjection?.CandidateSetId;
                    var candidateVersion = currentProjection?.CandidateSetVersion;
                    var wasSuspended = effectSuspended;
                    var wasMapInteraction = mapInteractionActive;
                    informationReturn = new PageReturnContext { Page = source, Focus = focus,
                        IsCurrent = () => source == suspendedEffectPage && currentRequestId == request && currentRevision >= revision &&
                            currentProjection?.CandidateSetId == candidateSet && currentProjection?.CandidateSetVersion == candidateVersion,
                        Restore = () => { if (wasSuspended) mapInteractionActive = wasMapInteraction; else ResumeEffectPage(); } };
                }
                else if (source != null && source == stagePage)
                {
                    informationReturn = new PageReturnContext { Page = source, Focus = focus,
                        IsCurrent = () => stagePage == source, Restore = () => ShowStagePage(source) };
                }
                if (informationReturn != null)
                {
                    informationReturn.Scrolls = source.GetComponentsInChildren<ScrollRect>(true);
                    informationReturn.ScrollPositions = new Vector2[informationReturn.Scrolls.Length];
                    for (var i = 0; i < informationReturn.Scrolls.Length; i++)
                        informationReturn.ScrollPositions[i] = informationReturn.Scrolls[i].normalizedPosition;
                    if (focus != null && focus.transform.IsChildOf(source.transform))
                    {
                        var names = new List<string>();
                        for (var node = focus.transform; node != source.transform; node = node.parent) names.Add(node.name);
                        names.Reverse();
                        informationReturn.FocusPath = string.Join("/", names);
                    }
                }
                informationNavigation = true;
            }
            informationPage = page;
            ShowPage(page, false);
        }

        /// <summary>用户关闭资料导航时返回；失效清理用 AbandonInformationPage，内部转页用 ReleasePage。</summary>
        public void CloseInformationPage(GameObject page)
        {
            if (page == null) return;
            if (!informationNavigation || informationPage != page) { ReleasePage(page); return; }
            var saved = informationReturn;
            informationReturn = null;
            informationPage = null;
            informationNavigation = false;
            informationReturnedFrame = Time.frameCount;
            ReleasePage(page);
            if (saved?.Page == null || saved.IsCurrent?.Invoke() != true) return;
            saved.Restore?.Invoke();
            if (!saved.Page.activeInHierarchy) return;
            Canvas.ForceUpdateCanvases();
            for (var i = 0; saved.Scrolls != null && i < saved.Scrolls.Length; i++)
                if (saved.Scrolls[i] != null)
                {
                    saved.Scrolls[i].StopMovement();
                    saved.Scrolls[i].normalizedPosition = saved.ScrollPositions[i];
                }
            var focus = saved.Focus != null && saved.Focus.activeInHierarchy ? saved.Focus :
                string.IsNullOrEmpty(saved.FocusPath) ? null : saved.Page.transform.Find(saved.FocusPath)?.gameObject;
            RestorePageFocus(saved.Page, focus);
        }

        public bool IsActionPageCovered(GameObject page) => informationNavigation && page != null &&
            (actionPage?.Page == page || suspendedEffectPage == page);

        public void AbandonInformationPage(GameObject page)
        {
            var abandonedCurrentInformation = page != null && informationPage == page;
            if (abandonedCurrentInformation)
            {
                informationReturn = null;
                informationPage = null;
                informationNavigation = false;
            }
            ReleasePage(page);
            // 新请求可能已在资料页后登记；关闭资料时交回该页，不改变用户主动收起的状态。
            if (abandonedCurrentInformation && !informationNavigation && !effectSuspended && visiblePage == null &&
                suspendedEffectPage != null && !string.IsNullOrEmpty(currentRequestId) &&
                suspendedRequestId == currentRequestId && suspendedRevision == currentRevision)
                ShowPage(suspendedEffectPage, true);
        }

        public void ConfigureStagePageFolding(GameObject page, bool allowFolding)
        {
            if (stagePage != page || page == null) return;
            stagePageFoldable = allowFolding;
            UpdateFoldButton();
        }

        public void ReleaseStagePage(GameObject page)
        {
            if (stagePage != page) return;
            stagePage = null;
            stagePageFoldable = false;
            stagePageSuspended = false;
            suspendedStageFocus = null;
            UpdateFoldButton();
        }

        public void ShowPage(GameObject page, bool effectPage)
        {
            if (page == null) return;
            if (informationNavigation && (stagePage == page ||
                actionPage?.Page == page && informationReturn?.Page == page && informationReturn.IsCurrent?.Invoke() == true))
            {
                if (stagePage == page) stagePageSuspended = true;
                page.SetActive(false);
                return;
            }
            if (informationNavigation && actionPage?.Page == page && !effectPage)
            {
                // 新行动明确替换资料页；失效的旧返回上下文不能把新草稿隐藏。
                informationReturn = null;
                informationPage = null;
                informationNavigation = false;
            }
            if (stagePageFoldable && visiblePage == stagePage && page != stagePage)
                SuspendStagePage();
            if (!effectPage && !string.IsNullOrEmpty(currentRequestId))
                SuspendEffectForInformation();
            var keepEffectHidden = effectPage && (effectSuspended || informationNavigation) &&
                                   !string.IsNullOrEmpty(currentRequestId);
            if (!keepEffectHidden && visiblePage != null && visiblePage != page)
            {
                if (visiblePage == suspendedEffectPage)
                {
                    suspendedEffectPage = null;
                    suspendedEffectFocus = null;
                }
                CloseVisiblePage();
            }
            visiblePage = keepEffectHidden ? visiblePage : page;
            page.SetActive(!keepEffectHidden);
            if (effectPage && !string.IsNullOrEmpty(currentRequestId))
            {
                suspendedEffectPage = page;
                suspendedRequestId = currentRequestId;
                suspendedRevision = currentRevision;
            }
            UpdateFoldButton();
        }

        public void HidePage(GameObject page)
        {
            if (page == null) return;
            if (visiblePage == page) visiblePage = null;
            page.SetActive(false);
            UpdateFoldButton();
        }

        /// <summary>永久结束一个页面的生命周期；不改变其余页面或当前规则请求。</summary>
        public void ReleasePage(GameObject page)
        {
            if (page == null) return;
            if (actionPage?.Page == page) actionPage = null;
            if (informationReturn?.Page == page) informationReturn = null;
            ReleaseStagePage(page);
            if (suspendedEffectPage == page)
            {
                suspendedEffectPage = null;
                suspendedEffectFocus = null;
                suspendedRequestId = null;
                effectSuspended = false;
            }
            HidePage(page);
        }

        public void ConstrainExternalPage(RectTransform page)
        {
            if (page == null) return;
            if (!externalPages.Contains(page)) externalPages.Add(page);
            ApplyExternalPageBounds(page);
        }

        public void SuspendEffectForInformation()
        {
            mapInteractionActive = false;
            if (string.IsNullOrEmpty(currentRequestId) || suspendedEffectPage == null || effectSuspended) return;
            effectSuspended = true;
            if (suspendedEffectPage != null && visiblePage == suspendedEffectPage)
            {
                var selected = EventSystem.current == null ? null :
                    EventSystem.current.currentSelectedGameObject;
                suspendedEffectFocus = selected != null &&
                                       selected.transform.IsChildOf(suspendedEffectPage.transform)
                    ? selected : null;
                suspendedEffectPage.SetActive(false);
                visiblePage = null;
            }
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            UpdateFoldButton();
        }

        public void ResumeEffectPage()
        {
            if (effectSuspended) ToggleEffectPage();
        }

        public void SuspendEffectForMapInteraction()
        {
            SuspendEffectForInformation();
            mapInteractionActive = effectSuspended && suspendedEffectPage != null;
        }

        private void ToggleEffectPage()
        {
            if (informationNavigation) return;
            if (!CanFoldEffectPage && stagePageFoldable && stagePage != null)
            {
                if (!stagePageSuspended) SuspendStagePage();
                else
                {
                    CloseVisiblePage();
                    ShowStagePage(stagePage);
                    RestorePageFocus(stagePage, suspendedStageFocus);
                }
                return;
            }
            if (string.IsNullOrEmpty(currentRequestId) || suspendedEffectPage == null ||
                currentRequestId != suspendedRequestId) return;
            if (!effectSuspended)
            {
                SuspendEffectForInformation();
                return;
            }
            if (suspendedEffectPage != null && currentRequestId != suspendedRequestId) return;
            CloseVisiblePage();
            visiblePage = suspendedEffectPage;
            effectSuspended = false;
            mapInteractionActive = false;
            if (suspendedEffectPage != null) suspendedEffectPage.SetActive(true);
            RestorePageFocus(suspendedEffectPage, suspendedEffectFocus);
            UpdateFoldButton();
        }

        private void SuspendStagePage()
        {
            if (!stagePageFoldable || stagePage == null || stagePageSuspended) return;
            var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            suspendedStageFocus = selected != null && selected.transform.IsChildOf(stagePage.transform)
                ? selected : null;
            stagePageSuspended = true;
            HidePage(stagePage);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        }

        private static void RestorePageFocus(GameObject page, GameObject savedFocus)
        {
            if (EventSystem.current != null && page != null)
            {
                var focus = savedFocus != null && savedFocus.activeInHierarchy ? savedFocus : null;
                if (focus == null)
                {
                    foreach (var selectable in page.GetComponentsInChildren<Selectable>(true))
                    {
                        if (!selectable.gameObject.activeInHierarchy || !selectable.IsInteractable()) continue;
                        focus = selectable.gameObject;
                        break;
                    }
                }
                EventSystem.current.SetSelectedGameObject(focus);
            }
        }

        private void CloseVisiblePage()
        {
            var page = visiblePage;
            if (page == null)
            {
                visiblePage = null;
                return;
            }
            var settings = FindObjectOfType<GameSettingsMenuController>();
            var cardViewer = page.GetComponent<CardViewer>();
            if (cardViewer != null) cardViewer.Dismiss();
            if (settings != null && settings.OwnsPage(page)) settings.Close();
            var log = FindObjectOfType<ActionLogViewerController>();
            if (log != null && log.OwnsPage(page)) log.Close();
            var viewer = FindObjectOfType<ZoomableImageViewerController>();
            if (viewer != null && viewer.OwnsPage(page)) viewer.Close();
            var hand = FindObjectOfType<CharacterHandPanel>();
            if (hand != null && hand.OwnsDiscardPage(page)) hand.CloseDiscardPreview();
            if (page != null) page.SetActive(false);
            if (visiblePage == page) visiblePage = null;
        }

        private bool CanFoldEffectPage => !string.IsNullOrEmpty(currentRequestId) && suspendedEffectPage != null &&
                                          currentRequestId == suspendedRequestId;

        private void UpdateFoldButton()
        {
            var effectPageAvailable = CanFoldEffectPage;
            var canRestore = effectPageAvailable || (stagePageFoldable && stagePage != null);
            var suspended = effectPageAvailable ? effectSuspended : stagePageSuspended;
            if (foldButton != null) foldButton.interactable = canRestore && !informationNavigation;
            if (foldButtonText != null)
                foldButtonText.text = canRestore
                    ? (suspended ? foldSuspendedText : foldVisibleText)
                    : foldUnavailableText;
        }

        private void OpenSettings()
        {
            var settings = FindObjectOfType<GameSettingsMenuController>();
            if (settings != null) settings.Open();
        }

        private void ApplyLayout(bool force)
        {
            if (barCanvas == null || safeArea == null || topBar == null || bottomBar == null || contentRect == null)
                return;
            if (!force && lastScreenWidth == Screen.width && lastScreenHeight == Screen.height &&
                lastSafeArea == Screen.safeArea && lastScaleFactor == barCanvas.scaleFactor &&
                (mainSurface == null || lastSurfaceSize == mainSurface.rect.size) &&
                (contentCanvas == null || lastContentScaleFactor == contentCanvas.scaleFactor))
                return;
            lastScreenWidth = Screen.width;
            lastScreenHeight = Screen.height;
            lastSafeArea = Screen.safeArea;
            lastScaleFactor = barCanvas.scaleFactor;
            lastContentScaleFactor = contentCanvas == null ? -1f : contentCanvas.scaleFactor;
            lastSurfaceSize = mainSurface == null ? Vector2.zero : mainSurface.rect.size;
            var scale = Mathf.Max(0.01f, barCanvas.scaleFactor);
            var safe = Screen.safeArea;
            safeArea.offsetMin = new Vector2(safe.xMin / scale, safe.yMin / scale);
            safeArea.offsetMax = new Vector2((safe.xMax - Screen.width) / scale, (safe.yMax - Screen.height) / scale);
            // 根适配器只设置安全边界，栏高与余量由资产中的原生 VLG / LE 决定。
            LayoutRebuilder.MarkLayoutForRebuild(safeArea);
            mapViewportDirty = true;
        }

        private void ApplyBackdrops()
        {
            if (mainSurface == null || mapVisibleBounds == null || mapBackdrops == null ||
                mapBackdrops.Length != 4) return;
            var corners = new Vector3[4];
            mapVisibleBounds.GetWorldCorners(corners);
            var lowerLeft = mainSurface.InverseTransformPoint(corners[0]);
            var upperRight = mainSurface.InverseTransformPoint(corners[2]);
            var rect = mainSurface.rect;
            var left = Mathf.Clamp(lowerLeft.x - rect.xMin, 0f, rect.width);
            var right = Mathf.Clamp(upperRight.x - rect.xMin, 0f, rect.width);
            var bottom = Mathf.Clamp(lowerLeft.y - rect.yMin, 0f, rect.height);
            var top = Mathf.Clamp(upperRight.y - rect.yMin, 0f, rect.height);
            SetBackdrop(mapBackdrops[0], 0f, 0f, left, rect.height);
            SetBackdrop(mapBackdrops[1], right, 0f, rect.width - right, rect.height);
            SetBackdrop(mapBackdrops[2], left, top, right - left, rect.height - top);
            SetBackdrop(mapBackdrops[3], left, 0f, right - left, bottom);
        }

        private static void SetBackdrop(RectTransform rect, float x, float y, float width, float height)
        {
            if (rect == null) return;
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(Mathf.Max(0f, width), Mathf.Max(0f, height));
        }

        private void RefreshMapOpening()
        {
            var display = MapDisplayController.Active;
            if (mapVisibleBounds == null || display == null) return;
            var opening = display.VisibleViewportInRegion;
            var min = opening.min; var max = opening.max;
            if (mapVisibleBounds.anchorMin == min && mapVisibleBounds.anchorMax == max) return;
            mapVisibleBounds.anchorMin = min;
            mapVisibleBounds.anchorMax = max;
            mapVisibleBounds.offsetMin = mapVisibleBounds.offsetMax = Vector2.zero;
            if (mapSurroundLayout != null) mapSurroundLayout.Refresh();
            ApplyBackdrops();
        }

        private void ApplyMapViewport()
        {
            if (mapRegion == null || Screen.width <= 0 || Screen.height <= 0) return;
            var display = FindObjectOfType<MapDisplayController>();
            if (display == null) return;
            if (!mapRegion.gameObject.activeInHierarchy)
            {
                // 复用地图既有空视口契约：结束平移、释放缩放锚点并关闭相机输入。
                display.SetScreenViewport(new Rect(0f, 0f, 0f, 0f));
                return;
            }
            var corners = new Vector3[4];
            mapRegion.GetWorldCorners(corners);
            var lowerLeft = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
            var upperRight = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
            display.SetScreenViewport(new Rect(lowerLeft.x / Screen.width, lowerLeft.y / Screen.height,
                (upperRight.x - lowerLeft.x) / Screen.width,
                (upperRight.y - lowerLeft.y) / Screen.height));
        }


        private void ApplyExternalPageBounds(RectTransform page)
        {
            if (contentRect == null || !(page.parent is RectTransform parent)) return;
            // 外部独立 Canvas 读取唯一 PageHost 的最终边界，不再次扣除栏高。
            var corners = new Vector3[4];
            contentRect.GetWorldCorners(corners);
            var lower = parent.InverseTransformPoint(corners[0]);
            var upper = parent.InverseTransformPoint(corners[2]);
            page.anchorMin = Vector2.zero;
            page.anchorMax = Vector2.one;
            var min = new Vector2(lower.x - parent.rect.xMin, lower.y - parent.rect.yMin);
            var max = new Vector2(upper.x - parent.rect.xMax, upper.y - parent.rect.yMax);
            if ((page.offsetMin - min).sqrMagnitude > .0001f) page.offsetMin = min;
            if ((page.offsetMax - max).sqrMagnitude > .0001f) page.offsetMax = max;
        }

        private string GetPhaseText(GamePhase phase)
        {
            switch (phase)
            {
                case GamePhase.Setup: return setupPhaseText;
                case GamePhase.Entrance: return entrancePhaseText;
                case GamePhase.RoundStart: return roundStartPhaseText;
                case GamePhase.CharacterCover: return characterCoverPhaseText;
                case GamePhase.ActionRound1: return firstActionPhaseText;
                case GamePhase.ActionRound2: return secondActionPhaseText;
                case GamePhase.ResourceCollection: return collectionPhaseText;
                case GamePhase.Cleanup: return cleanupPhaseText;
                case GamePhase.FinalScoring: return finalScoringPhaseText;
                case GamePhase.GameOver: return gameOverPhaseText;
                default: return string.Empty;
            }
        }
    }
}
