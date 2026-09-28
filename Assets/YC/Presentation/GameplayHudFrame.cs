using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>共享 HUD 中的安全区域、常驻栏和内容页面边界。</summary>
    public sealed class GameplayHudFrame : MonoBehaviour
    {
        private enum LayoutMode { Wide, Standard, Compact }

        [Header("可选的代码布局（默认关闭，保留场景和预制体编辑）")]
        [Tooltip("仅主动启用时，才按旧设计尺寸重排常驻栏和内容区。普通运行不覆盖手动布局。")]
        [SerializeField] private bool useResponsiveLayout;
        [Tooltip("不启用自动布局时，选择短摘要；默认只显示长摘要，避免两套内容重叠。")]
        [SerializeField] private bool showShortSummary;
        [SerializeField] private string roundTotalFormat = "/ {0} 回合";
        [SerializeField] private string redZoneOpenRoundFormat = "第 {0} 回合起";
        [SerializeField] private Color completedRoundColor = new Color(.75f, .54f, .13f, 1f);
        [SerializeField] private Color upcomingRoundColor = new Color(.67f, .64f, .56f, .7f);

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
        [SerializeField] private RectTransform[] mapBackdrops;
        [SerializeField] private RectTransform compactNavigation;
        [SerializeField] private Button[] compactNavigationButtons;
        [SerializeField] private Sprite compactSelectedSprite;
        [SerializeField] private Sprite compactIdleSprite;
        [SerializeField] private Color compactSelectedTextColor;
        [SerializeField] private Color compactIdleTextColor;
        [SerializeField] private Text roundNumberText;
        [SerializeField] private Text roundTotalText;
        [SerializeField] private Text phaseText;
        [SerializeField] private Text redZoneText;
        [SerializeField] private Text redZoneOpenRoundText;
        [SerializeField] private Text turnTagText;
        [SerializeField] private Image[] roundTicks;
        [SerializeField] private Text summaryText;
        [SerializeField] private Text shortSummaryText;
        [SerializeField] private Text selfNameText;
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
        [SerializeField] private string foldVisibleText = "收起";
        [SerializeField] private string foldSuspendedText = "展开";
        [SerializeField] private string foldUnavailableText = "无待处理结算";
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
        [Header("逻辑尺寸与布局阈值")]
        [SerializeField] private float wideMinWidth = 1540f;
        [SerializeField] private float compactMaxWidth = 1060f;
        [SerializeField] private float compactMaxHeight = 670f;
        [SerializeField] private float minimumControlSpacing = 12f;
        [SerializeField] private float modeHysteresis = 24f;
        [SerializeField] private float wideTopHeight = 72f;
        [SerializeField] private float standardTopHeight = 80f;
        [SerializeField] private float compactTopHeight = 104f;
        [SerializeField] private float wideBottomHeight = 96f;
        [SerializeField] private float standardBottomHeight = 104f;
        [SerializeField] private float compactBottomHeight = 120f;
        [SerializeField] private float contentGap = 8f;
        [SerializeField] private float bottomScreenMargin = 0f;
        [SerializeField] private float designWidth = 1920f;
        [SerializeField] private float designHeight = 1080f;
        [SerializeField] private float designContentLeft = 16f;
        [SerializeField] private float designContentRight = 1904f;
        [SerializeField] private float designContentTop = 72f;
        [SerializeField] private float designContentBottom = 978f;
        [SerializeField] private float layoutSideInset = 16f;
        [SerializeField] private float compactNavigationHeight = 36f;
        [SerializeField] private float compactPanMinimumGain = 1.08f;
        [SerializeField] private float compactTopRowOffset = 24f;
        [SerializeField] private float compactStatusRowOffset = -29f;
        [SerializeField] private float normalStatusHeight = 55f;
        [SerializeField] private float compactStatusHeight = 42f;

        private static GameplayHudFrame active;
        private LayoutMode mode = LayoutMode.Standard;
        private int lastScreenWidth = -1;
        private int lastScreenHeight = -1;
        private Rect lastSafeArea;
        private float lastScaleFactor = -1f;
        private float lastContentScaleFactor = -1f;
        private Vector2 lastSurfaceSize;
        private Canvas contentCanvas;
        private GameObject visiblePage;
        private GameObject suspendedEffectPage;
        private GameObject suspendedEffectFocus;
        private string currentRequestId;
        private int currentRevision;
        private string suspendedRequestId;
        private int suspendedRevision;
        private bool effectSuspended;
        private readonly List<RectTransform> externalPages = new List<RectTransform>();
        private float currentTopHeight;
        private float currentBottomHeight;
        private int compactSection = 1;

        public static GameplayHudFrame Active => active;
        public static bool EffectInputSuspended => active != null && active.effectSuspended;
        public RectTransform ContentRect => contentRect;
        public Canvas BarCanvas => barCanvas;
        public RectTransform TopBar => topBar;
        public RectTransform BottomBar => bottomBar;
        public Button SettingsButton => settingsButton;
        public Button FoldButton => foldButton;
        public Button EndActionButton => endActionButton;

        public bool TryValidateConfiguration(out string reason)
        {
            if (barCanvas == null || safeArea == null || topBar == null || bottomBar == null ||
                contentRect == null || mapRegion == null || mainSurface == null || mainRegions == null ||
                topBarContent == null || bottomBarContent == null ||
                mapBackdrops == null || mapBackdrops.Length != 4 ||
                compactNavigation == null || compactNavigationButtons == null ||
                compactNavigationButtons.Length != 3 || compactSelectedSprite == null ||
                compactIdleSprite == null ||
                roundNumberText == null || roundTotalText == null ||
                phaseText == null || redZoneText == null || redZoneOpenRoundText == null ||
                turnTagText == null || roundTicks == null || roundTicks.Length == 0 ||
                summaryText == null || shortSummaryText == null || selfNameText == null ||
                resourceValueTexts == null || resourceValueTexts.Length != 5 ||
                settingsButton == null || foldButton == null || foldButtonText == null ||
                undoButton == null || endActionButton == null)
            {
                reason = "常驻栏或内容区的预制体引用不完整。";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        private void Awake()
        {
            active = this;
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
            if (compactNavigationButtons != null)
            {
                for (var i = 0; i < compactNavigationButtons.Length; i++)
                {
                    var button = compactNavigationButtons[i];
                    if (button == null) continue;
                    var section = i;
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(() => SelectCompactSection(section));
                }
            }
            ApplyLayout(true);
            UpdateFoldButton();
        }

        private void OnDestroy()
        {
            InteractionRequestRouter.RequestRouted -= HandleRequestRouted;
            if (active == this) active = null;
        }

        private void HandleRequestRouted(YC.Domain.Interactions.InteractionRequestProjection projection)
        {
            if (projection == null) ClearRequest();
            else SetRequest(projection.InteractionId, projection.StateRevision);
        }

        private System.Func<bool> canCancelMainAction;
        private System.Action cancelMainAction;

        public void BindMainActionCancellation(System.Func<bool> canCancel, System.Action cancel)
        {
            canCancelMainAction = canCancel;
            cancelMainAction = cancel;
            if (undoButton == null) return;
            undoButton.onClick.RemoveListener(CancelMainAction);
            undoButton.onClick.AddListener(CancelMainAction);
            undoButton.interactable = canCancelMainAction != null && canCancelMainAction();
        }

        private void CancelMainAction()
        {
            if (canCancelMainAction != null && canCancelMainAction()) cancelMainAction?.Invoke();
        }

        private void Update()
        {
            if (undoButton != null) undoButton.interactable = canCancelMainAction != null && canCancelMainAction();
            if (barCanvas != null && (lastScreenWidth != Screen.width || lastScreenHeight != Screen.height ||
                lastSafeArea != Screen.safeArea || lastScaleFactor != barCanvas.scaleFactor ||
                (mainSurface != null && lastSurfaceSize != mainSurface.rect.size) ||
                (contentCanvas != null && lastContentScaleFactor != contentCanvas.scaleFactor)))
                ApplyLayout(false);
        }

        public void Refresh(GameState state, ActionPanelViewModel action, string endUnavailableReason)
        {
            if (state == null || action == null) return;
            if (roundNumberText != null) roundNumberText.text = state.Round.ToString();
            if (roundTotalText != null) roundTotalText.text = string.Format(roundTotalFormat, state.MaxRounds);
            if (phaseText != null) phaseText.text = GetPhaseText(state.Phase);
            if (turnTagText != null) turnTagText.text =
                action.IsWaitingForOtherPlayers ? waitingTurnText : localTurnText;
            if (roundTicks != null)
            {
                for (var i = 0; i < roundTicks.Length; i++)
                {
                    var tick = roundTicks[i];
                    if (tick == null) continue;
                    tick.gameObject.SetActive(i < state.MaxRounds);
                    tick.color = i < state.Round ? completedRoundColor : upcomingRoundColor;
                }
            }
            var summary = action.CanEndAction ? action.StatusText : endUnavailableReason;
            if (summaryText != null) summaryText.text = summary ?? string.Empty;
            if (shortSummaryText != null) shortSummaryText.text = summary ?? string.Empty;
            if (redZoneText != null)
            {
                var map = state.MapId == StaticMapDefinitions.FourPlayerMapId ||
                          state.MapId == StaticMapDefinitions.ThreePlayerMapId
                    ? StaticMapDefinitions.Resolve(state.MapId) : null;
                var openRound = RedZoneAccessRule.GetOpenRound(map, state.Players.Count);
                redZoneText.text = state.Round >= openRound ? redZoneOpenText : redZoneClosedText;
                if (redZoneOpenRoundText != null)
                    redZoneOpenRoundText.text = string.Format(redZoneOpenRoundFormat, openRound);
            }
            if (endActionButton != null) endActionButton.interactable = action.CanEndAction;
            var localPlayer = state.Players.Find(player => player.Color == action.LocalPlayerColor);
            if (selfNameText != null)
                selfNameText.text = localPlayer == null ? string.Empty :
                    localPlayer.Name ?? string.Empty;
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
            var replaced = currentRequestId != interactionId || revision < currentRevision;
            if (replaced)
            {
                effectSuspended = false;
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
            currentRequestId = null;
            effectSuspended = false;
            suspendedRequestId = null;
            suspendedEffectPage = null;
            suspendedEffectFocus = null;
            UpdateFoldButton();
        }

        public void ShowPage(GameObject page, bool effectPage)
        {
            if (page == null) return;
            if (!effectPage && !string.IsNullOrEmpty(currentRequestId) && !effectSuspended)
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

        public void ConstrainExternalPage(RectTransform page)
        {
            if (page == null) return;
            if (!externalPages.Contains(page)) externalPages.Add(page);
            ApplyExternalPageBounds(page);
        }

        public void SuspendEffectForInformation()
        {
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

        private void ToggleEffectPage()
        {
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
            if (suspendedEffectPage != null) suspendedEffectPage.SetActive(true);
            if (EventSystem.current != null && suspendedEffectPage != null)
            {
                var focus = suspendedEffectFocus != null && suspendedEffectFocus.activeInHierarchy
                    ? suspendedEffectFocus : null;
                if (focus == null)
                {
                    foreach (var selectable in suspendedEffectPage.GetComponentsInChildren<Selectable>(true))
                    {
                        if (!selectable.gameObject.activeInHierarchy || !selectable.IsInteractable()) continue;
                        focus = selectable.gameObject;
                        break;
                    }
                }
                EventSystem.current.SetSelectedGameObject(focus);
            }
            UpdateFoldButton();
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

        private void UpdateFoldButton()
        {
            var canRestore = !string.IsNullOrEmpty(currentRequestId) && suspendedEffectPage != null &&
                             currentRequestId == suspendedRequestId;
            if (foldButton != null) foldButton.interactable = canRestore;
            if (foldButtonText != null)
                foldButtonText.text = canRestore
                    ? (effectSuspended ? foldSuspendedText : foldVisibleText)
                    : foldUnavailableText;
        }

        private void OpenSettings()
        {
            var settings = FindObjectOfType<GameSettingsMenuController>();
            if (settings != null) settings.Open();
        }

        private void ApplyLayout(bool force)
        {
            if (!useResponsiveLayout)
            {
                if (longSummary != null) longSummary.SetActive(!showShortSummary);
                if (shortSummary != null) shortSummary.SetActive(showShortSummary);
                // 地图相机跟随实际编辑后的 UI 窗口，但不反向改写 UI。
                lastScreenWidth = Screen.width;
                lastScreenHeight = Screen.height;
                lastSafeArea = Screen.safeArea;
                lastScaleFactor = barCanvas == null ? -1f : barCanvas.scaleFactor;
                lastContentScaleFactor = contentCanvas == null ? -1f : contentCanvas.scaleFactor;
                lastSurfaceSize = mainSurface == null ? Vector2.zero : mainSurface.rect.size;
                ApplyMapViewport();
                return;
            }
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
            // 保留 Canvas 的基础缩放，同时用实际可用窗口判断是否需要重排。
            var width = Mathf.Min(safe.width / scale, safe.width);
            var height = Mathf.Min(safe.height / scale, safe.height);
            var h = modeHysteresis;
            var topControlMinimum = roundNumberText.rectTransform.sizeDelta.x +
                phaseText.rectTransform.sizeDelta.x + redZoneText.rectTransform.sizeDelta.x +
                settingsButton.GetComponent<RectTransform>().sizeDelta.x +
                foldButton.GetComponent<RectTransform>().sizeDelta.x + minimumControlSpacing * 6f;
            var compactThreshold = Mathf.Max(compactMaxWidth, topControlMinimum);
            if (width < compactThreshold - h || height < compactMaxHeight - h ||
                (mode == LayoutMode.Compact &&
                 (width <= compactThreshold + h || height <= compactMaxHeight + h)))
                mode = LayoutMode.Compact;
            else if (width > wideMinWidth + h ||
                     (mode == LayoutMode.Wide && width >= wideMinWidth - h))
                mode = LayoutMode.Wide;
            else
                mode = LayoutMode.Standard;
            var topHeight = mode == LayoutMode.Wide ? wideTopHeight : mode == LayoutMode.Standard ? standardTopHeight : compactTopHeight;
            var bottomHeight = mode == LayoutMode.Wide ? wideBottomHeight : mode == LayoutMode.Standard ? standardBottomHeight : compactBottomHeight;
            currentTopHeight = topHeight;
            currentBottomHeight = bottomHeight;
            topBar.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, topHeight);
            bottomBar.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, bottomHeight);
            ApplyBarContentLayout(topBar, topBarContent, designWidth, wideTopHeight, topHeight);
            ApplyBarContentLayout(bottomBar, bottomBarContent, designContentRight - designContentLeft,
                wideBottomHeight, bottomHeight);
            ApplyMainRegionLayout(topHeight, bottomHeight);
            ApplyBackdrops();
            var contentScale = contentCanvas == null ? scale : Mathf.Max(0.01f, contentCanvas.scaleFactor);
            contentRect.offsetMin = new Vector2(safe.xMin / contentScale,
                (safe.yMin + (bottomHeight + bottomScreenMargin + contentGap) * scale) / contentScale);
            contentRect.offsetMax = new Vector2((safe.xMax - Screen.width) / contentScale,
                (safe.yMax - Screen.height - (topHeight + contentGap) * scale) / contentScale);
            if (longSummary != null) longSummary.SetActive(mode == LayoutMode.Wide);
            if (shortSummary != null) shortSummary.SetActive(mode != LayoutMode.Wide);
            ApplyMapViewport();
            for (var i = externalPages.Count - 1; i >= 0; i--)
            {
                if (externalPages[i] == null) externalPages.RemoveAt(i);
                else ApplyExternalPageBounds(externalPages[i]);
            }
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

        private static void ApplyBarContentLayout(RectTransform bar, RectTransform contents,
            float designWidth, float designHeight, float actualHeight)
        {
            if (bar == null || contents == null || designWidth <= 0f || designHeight <= 0f) return;
            var scale = Mathf.Min(1f, bar.rect.width / designWidth);
            scale = Mathf.Max(.01f, scale);
            contents.anchorMin = contents.anchorMax = Vector2.up;
            contents.pivot = Vector2.up;
            contents.sizeDelta = new Vector2(designWidth, designHeight);
            contents.localScale = new Vector3(scale, scale, 1f);
            contents.anchoredPosition = new Vector2(
                (bar.rect.width - designWidth * scale) * .5f,
                -(actualHeight - designHeight * scale) * .5f);
        }

        private void ApplyMainRegionLayout(float topHeight, float bottomHeight)
        {
            if (mainSurface == null || mainRegions == null) return;
            var available = mainSurface.rect.size;
            if (available.x < 1f || available.y < 1f) return;
            var designSpanWidth = designContentRight - designContentLeft;
            var designSpanHeight = designContentBottom - designContentTop;
            if (designSpanWidth <= 0f || designSpanHeight <= 0f) return;
            var freeHeight = available.y - topHeight - bottomHeight - bottomScreenMargin -
                             contentGap * 2f;
            var widthFit = (available.x - layoutSideInset * 2f) / designSpanWidth;
            var normalFit = Mathf.Min(widthFit, freeHeight / designSpanHeight);
            var panFit = (freeHeight - compactNavigationHeight - contentGap) / designSpanHeight;
            var usePanning = mode == LayoutMode.Compact && panFit > normalFit * compactPanMinimumGain &&
                             designSpanWidth * panFit > available.x - layoutSideInset * 2f;
            var fit = usePanning ? panFit : normalFit;
            fit = Mathf.Max(.01f, fit);
            if (compactNavigation != null)
            {
                compactNavigation.gameObject.SetActive(usePanning);
                if (usePanning)
                {
                    compactNavigation.anchorMin = compactNavigation.anchorMax = Vector2.up;
                    compactNavigation.pivot = Vector2.up;
                    compactNavigation.sizeDelta = new Vector2(360f, compactNavigationHeight);
                    compactNavigation.anchoredPosition = new Vector2(
                        (available.x - 360f) * .5f,
                        -(available.y - bottomHeight - bottomScreenMargin -
                          compactNavigationHeight - contentGap));
                }
            }
            mainRegions.anchorMin = mainRegions.anchorMax = Vector2.up;
            mainRegions.pivot = Vector2.up;
            mainRegions.sizeDelta = new Vector2(designWidth, designHeight);
            mainRegions.localScale = new Vector3(fit, fit, 1f);
            var horizontal = (available.x - designWidth * fit) * .5f;
            if (usePanning)
            {
                if (compactSection == 0)
                    horizontal = layoutSideInset - designContentLeft * fit;
                else if (compactSection == 2)
                    horizontal = available.x - layoutSideInset - designContentRight * fit;
            }
            mainRegions.anchoredPosition = new Vector2(horizontal,
                -(topHeight + contentGap - designContentTop * fit));
            RefreshCompactNavigation();
        }

        private void SelectCompactSection(int section)
        {
            compactSection = Mathf.Clamp(section, 0, 2);
            ApplyLayout(true);
        }

        private void RefreshCompactNavigation()
        {
            if (compactNavigationButtons == null) return;
            for (var i = 0; i < compactNavigationButtons.Length; i++)
            {
                var button = compactNavigationButtons[i];
                if (button == null) continue;
                var image = button.GetComponent<Image>();
                if (image != null) image.sprite = i == compactSection
                    ? compactSelectedSprite : compactIdleSprite;
                var label = button.GetComponentInChildren<Text>();
                if (label != null) label.color = i == compactSection
                    ? compactSelectedTextColor : compactIdleTextColor;
            }
        }

        private void ApplyMapViewport()
        {
            if (mapRegion == null || Screen.width <= 0 || Screen.height <= 0) return;
            var display = FindObjectOfType<MapDisplayController>();
            if (display == null) return;
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
            if (!useResponsiveLayout) return;
            var pageCanvas = page.GetComponentInParent<Canvas>();
            var pageScale = pageCanvas == null ? 1f : Mathf.Max(0.01f, pageCanvas.scaleFactor);
            var barScale = barCanvas == null ? 1f : Mathf.Max(0.01f, barCanvas.scaleFactor);
            var safe = Screen.safeArea;
            page.anchorMin = Vector2.zero;
            page.anchorMax = Vector2.one;
            page.offsetMin = new Vector2(safe.xMin / pageScale,
                (safe.yMin + (currentBottomHeight + bottomScreenMargin + contentGap) * barScale) / pageScale);
            page.offsetMax = new Vector2((safe.xMax - Screen.width) / pageScale,
                (safe.yMax - Screen.height - (currentTopHeight + contentGap) * barScale) / pageScale);
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
