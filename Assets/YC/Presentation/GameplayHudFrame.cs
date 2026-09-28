using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Interactions;
using YC.Presentation.Workflows;

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
        [SerializeField] private Text turnTagText;
        [SerializeField] private Image[] roundTicks;
        [SerializeField] private Text summaryText;
        [SerializeField] private Text shortSummaryText;
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
        private string interactionMessage;
        private float interactionMessageExpiresAt;
        private bool mapViewportDirty;
        private int currentRevision;
        private string suspendedRequestId;
        private int suspendedRevision;
        private bool effectSuspended;
        private bool mapInteractionActive;
        private readonly List<RectTransform> externalPages = new List<RectTransform>();

        public static GameplayHudFrame Active => active;
        public static bool EffectInputSuspended => active != null && active.effectSuspended &&
            (!active.mapInteractionActive || active.visiblePage != null);
        public RectTransform ContentRect => contentRect;
        public Canvas BarCanvas => barCanvas;
        public RectTransform TopBar => topBar;
        public RectTransform BottomBar => bottomBar;
        public Button SettingsButton => settingsButton;
        public Button FoldButton => foldButton;
        public Button EndActionButton => endActionButton;
        public string InteractionMessage => interactionMessage ?? string.Empty;

        public bool TryValidateConfiguration(out string reason)
        {
            if (barCanvas == null || safeArea == null || topBar == null || bottomBar == null ||
                contentRect == null || mapRegion == null || mainSurface == null || mainRegions == null ||
                columnsLayout == null || topContentLayout == null || mapSurroundLayout == null || citySlotsLayout == null ||
                topBarContent == null || bottomBarContent == null ||
                mapBackdrops == null || mapBackdrops.Length != 4 ||
                roundNumberText == null || roundTotalText == null ||
                phaseText == null || redZoneText == null || redZoneOpenRoundText == null ||
                turnTagText == null || roundTicks == null || roundTicks.Length == 0 ||
                summaryText == null || shortSummaryText == null || scoreValueText == null ||
                resourceValueTexts == null || resourceValueTexts.Length != 5 ||
                settingsButton == null || foldButton == null || foldButtonText == null ||
                undoButton == null || endActionButton == null)
            {
                reason = "常驻栏或内容区的预制体引用不完整。";
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

        private void Awake()
        {
            active = this;
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
            if (stagePage != null && !stagePageSuspended && (visiblePage == null || !visiblePage.activeSelf))
                ShowPage(stagePage, false);
            if (barCanvas != null && (lastScreenWidth != Screen.width || lastScreenHeight != Screen.height ||
                lastSafeArea != Screen.safeArea || lastScaleFactor != barCanvas.scaleFactor ||
                (mainSurface != null && lastSurfaceSize != mainSurface.rect.size) ||
                (contentCanvas != null && lastContentScaleFactor != contentCanvas.scaleFactor)))
                ApplyLayout(false);
            if (mapViewportDirty)
            {
                mapViewportDirty = false;
                // 首次布局可能晚于 Awake；地图开口和相机必须消费同一最终矩形。
                if (mapSurroundLayout != null) mapSurroundLayout.Refresh();
                ApplyBackdrops();
                ApplyMapViewport();
            }
        }

        public void Refresh(GameState state, ActionPanelViewModel action, string endUnavailableReason)
        {
            if (state == null || action == null) return;
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
            var replaced = currentRequestId != interactionId || revision < currentRevision;
            var sameFlow = !string.IsNullOrEmpty(flowId) && flowId == currentFlowId;
            if (replaced)
            {
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
            if (!string.IsNullOrEmpty(pendingPrompt) &&
                (string.IsNullOrEmpty(message) || !message.Contains(pendingPrompt)))
                message = string.IsNullOrEmpty(message) ? pendingPrompt : message + "\n" + pendingPrompt;
            interactionMessage = string.IsNullOrWhiteSpace(message) ? null : message;
            interactionMessageExpiresAt = Time.unscaledTime + 3f;
            UpdateSummary();
        }

        public void UpdateInteractionMessage(bool keepVisible)
        {
            if (interactionMessage == null) return;
            if (keepVisible) interactionMessageExpiresAt = Time.unscaledTime + 3f;
            else if (Time.unscaledTime >= interactionMessageExpiresAt)
            {
                interactionMessage = null;
                UpdateSummary();
            }
        }

        private void UpdateSummary()
        {
            var summary = lastActionSummary;
            if (currentProjection != null && currentProjection.Status == "open")
            {
                var skippable = currentProjection.AllowDecline;
                summary = currentProjection.MaxSelections > 0
                    ? string.Format(skippable ? skippableSelectionSummaryFormat :
                        selectionSummaryFormat, currentProjection.MinSelections,
                        currentProjection.MaxSelections)
                    : skippable ? skippableTargetSummary : targetSummary;
            }
            if (!string.IsNullOrEmpty(interactionMessage)) summary = interactionMessage;
            if (summaryText != null) summaryText.text = summary ?? string.Empty;
            if (shortSummaryText != null) shortSummaryText.text = summary ?? string.Empty;
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
            if (stagePageFoldable && visiblePage == stagePage && page != stagePage)
                SuspendStagePage();
            if (!effectPage && !string.IsNullOrEmpty(currentRequestId))
                SuspendEffectForInformation();
            var keepEffectHidden = effectPage && effectSuspended &&
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
            if (foldButton != null) foldButton.interactable = canRestore;
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
            if (mainSurface == null || mapRegion == null || mapBackdrops == null ||
                mapBackdrops.Length != 4) return;
            var corners = new Vector3[4];
            mapRegion.GetWorldCorners(corners);
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
