using System;
using YC.Application.Sessions;
using YC.Domain.Presentation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    public static class SettlementEffectArrivalMotion
    {
        public static float StartOffset(float viewportWidth, float visibleLayoutLeft, float edgeInset) =>
            Math.Max(0, viewportWidth + edgeInset - visibleLayoutLeft);

        // CSS cubic-bezier 的横轴是时间；先反解横轴，不能直接把时间当曲线参数。
        public static float Ease(float progress, float x1, float y1, float x2, float y2)
        {
            if (progress <= 0) return 0;
            if (progress >= 1) return 1;
            double low = 0, high = 1, parameter = progress;
            x1 = Math.Max(0, Math.Min(1, x1)); x2 = Math.Max(0, Math.Min(1, x2));
            for (int i = 0; i < 24; i++)
            {
                parameter = (low + high) * .5;
                var x = Bezier(parameter, x1, x2);
                if (x < progress) low = parameter; else high = parameter;
            }
            return (float)Bezier(parameter, y1, y2);
        }
        private static double Bezier(double t, double first, double second) =>
            3 * (1 - t) * (1 - t) * t * first + 3 * (1 - t) * t * t * second + t * t * t;
    }

    /// <summary>单指针手势状态；拖动与长按只抑制本次按压，不延迟下一次普通点击。</summary>
    public sealed class SettlementEffectPointerGesture
    {
        private int pointer;
        private float originX, originY, started;
        private bool down, canceled, clickReady, inspected;
        public bool IsPressed => down && !canceled && !inspected;
        public void Begin(int id, float x, float y, float time)
        { pointer = id; originX = x; originY = y; started = time; down = true; canceled = clickReady = inspected = false; }
        public void Move(int id, float x, float y, float threshold)
        {
            if (!down || id != pointer) return;
            var dx = x - originX; var dy = y - originY;
            if (dx * dx + dy * dy > threshold * threshold) { canceled = true; clickReady = false; }
        }
        public bool TryLongPress(float time, float delay)
        {
            if (!IsPressed || time - started < delay) return false;
            inspected = true; clickReady = false; return true;
        }
        public void Release(int id)
        { if (down && id == pointer) { clickReady = !canceled && !inspected; down = false; } }
        public bool ConsumeClick(int id)
        { if (id != pointer || !clickReady) return false; clickReady = false; return true; }
        public void Cancel(int id) { if (id == pointer) { canceled = true; clickReady = false; } }
        public void Reset() { down = canceled = clickReady = inspected = false; }
    }

    public sealed class SettlementEffectChipView : MonoBehaviour, IPointerDownHandler, IPointerUpHandler,
        IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler,
        IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        [SerializeField] private Button hitTarget;
        [SerializeField] private Graphic hitArea;
        [SerializeField] private Image background, icon;
        [SerializeField] private Component labelView;
        [SerializeField] private LayoutElement layout;
        [SerializeField] private RectTransform visual;
        [SerializeField] private CanvasGroup visualGroup;
        [SerializeField] private HorizontalLayoutGroup contentLayout;
        [SerializeField] private RectOffset textContentPadding;
        [SerializeField] private RectOffset iconContentPadding;
        [SerializeField] private Color defaultInk, hoverInk, pressedInk, pendingInk, disabledInk;
        [SerializeField] private Sprite defaultBackground, hoverBackground, pressedBackground, pendingBackground, disabledBackground;
        [SerializeField, Min(0)] private float dragThreshold = 6f, longPressDelay = .5f;
        [SerializeField, Min(0)] private float minimumTextWidth = 88f, maximumTextWidth = 280f;
        [SerializeField, Min(0)] private float textPadding = 32f, iconTextPadding = 62f, iconOnlyWidth = 48f;
        [SerializeField, Min(0)] private float arrivalDuration = .36f, arrivalFadeDuration = .1f;
        [UnityEngine.Serialization.FormerlySerializedAs("arrivalOffset")]
        [SerializeField, Min(0)] private float arrivalEdgeInset = 24f;
        [SerializeField] private Vector4 arrivalEasing = new Vector4(.22f, .75f, .22f, 1f);
        private readonly SettlementEffectPointerGesture gesture = new SettlementEffectPointerGesture();
        private SettlementOptionView option;
        private long scope, identity;
        private bool pending, hovered, executionAllowed;
        private Action<long, long, int, string> use, inspect;
        private Action manualScroll;
        private Func<string, string, Sprite> resolveIcon;
        private Action<Component, GameplayTextDocument, bool> renderText;
        private ScrollRect scroll;
        private GameplayTextDocument labelDocument, descriptionDocument, ariaDocument;
        private string cachedOptionId, cachedMode, cachedIconKey;
        private int cachedRevision = -1;
        private Vector2 visualOrigin;
        private bool capturedOrigin, animating;
        private float arrivalStart, arrivalDistance;
        private Sprite darkIcon, lightIcon, mutedIcon;
        private float measuredLabelWidth;
        private PointerEventData.InputButton pressedButton;
        private void Awake()
        {
            // RectOffset 调用原生 API；反序列化完成后只补新组件的未配置默认值。
            if (textContentPadding == null) textContentPadding = new RectOffset(16, 16, 0, 0);
            if (iconContentPadding == null) iconContentPadding = new RectOffset();
        }
        public float LabelAvailableWidth => option == null || layout == null ? 0 :
            Mathf.Max(0, layout.preferredWidth - (option.Presentation.ContentMode == "icon_text" ? iconTextPadding : textPadding));

        public bool ValidateReferences(out string reason)
        {
            reason = string.Empty;
            if (hitTarget == null || hitArea == null || background == null || icon == null || labelView == null || layout == null ||
                visual == null || visualGroup == null || contentLayout == null || defaultBackground == null || hoverBackground == null ||
                pressedBackground == null || pendingBackground == null || disabledBackground == null)
            { reason = "结算按钮引用或五态背景缺失。"; return false; }
            if (hitTarget.transform != transform || hitArea.transform != transform || !hitArea.raycastTarget ||
                background.raycastTarget || icon.raycastTarget || layout.transform != transform || visual == transform ||
                !visual.IsChildOf(transform) || visualGroup.transform != visual || contentLayout.transform != visual || !background.transform.IsChildOf(visual) ||
                !icon.transform.IsChildOf(visual) || !labelView.transform.IsChildOf(visual) ||
                !(labelView.transform is RectTransform) || hitTarget.onClick.GetPersistentEventCount() != 0 ||
                hitTarget.navigation.mode != Navigation.Mode.None || hitTarget.transition != Selectable.Transition.None)
            { reason = "结算按钮应由专属指针入口处理，视觉层与布局占位必须分开。"; return false; }
            return true;
        }

        public void Bind(SettlementOptionView value, bool isPending, bool canExecute, long scopeToken, long entryIdentity, float availableWidth,
            Func<GameplayTextRef, GameplayTextDocument> resolver, Action<Component, GameplayTextDocument, bool> renderer,
            Func<Component, GameplayTextDocument, float> measurement, Func<string, string, Sprite> iconResolver,
            Action<long, long, int, string> onUse, Action<long, long, int, string> onInspect,
            ScrollRect scrollRect, Action onManualScroll)
        {
            if (!ValidateReferences(out var reason)) throw new InvalidOperationException(reason);
            if (value?.Presentation == null) throw new ArgumentException("结算按钮缺少统一展示声明。");
            if (option == null || option.OptionId != value.OptionId || scope != scopeToken || identity != entryIdentity ||
                option.StateRevision != value.StateRevision) { gesture.Reset(); hovered = false; }
            option = value; scope = scopeToken; identity = entryIdentity; pending = isPending; executionAllowed = canExecute;
            use = onUse; inspect = onInspect;
            scroll = scrollRect; manualScroll = onManualScroll; resolveIcon = iconResolver; renderText = renderer;
            var p = value.Presentation;
            if (cachedOptionId != value.OptionId || cachedRevision != value.StateRevision || cachedMode != p.ContentMode || cachedIconKey != p.IconKey)
            {
                labelDocument = p.Label == null ? null : resolver(p.Label);
                descriptionDocument = p.Description == null ? null : resolver(p.Description);
                ariaDocument = p.AriaLabel == null ? labelDocument : resolver(p.AriaLabel);
                if (descriptionDocument == null || ariaDocument == null ||
                    (p.ContentMode != "icon" && labelDocument == null) ||
                    (p.ContentMode != "text" && p.ContentMode != "icon_text" && p.ContentMode != "icon"))
                    throw new InvalidOperationException("结算按钮的名称、说明或内容模式不完整。");
                if (p.ContentMode != "text")
                {
                    darkIcon = resolveIcon(p.IconKey, "dark"); lightIcon = resolveIcon(p.IconKey, "light"); mutedIcon = resolveIcon(p.IconKey, "muted");
                    if (darkIcon == null || lightIcon == null || mutedIcon == null)
                        throw new InvalidOperationException("结算按钮图标缺少登记资源或状态版本：" + p.IconKey);
                }
                cachedOptionId = value.OptionId; cachedRevision = value.StateRevision; cachedMode = p.ContentMode; cachedIconKey = p.IconKey;
            }
            var hasText = p.ContentMode != "icon";
            labelView.gameObject.SetActive(hasText); icon.gameObject.SetActive(p.ContentMode != "text");
            contentLayout.padding = p.ContentMode == "icon" ? iconContentPadding : textContentPadding;
            measuredLabelWidth = hasText ? measurement(labelView, labelDocument) : 0;
            FitWidth(availableWidth);
            if (hasText) renderer(labelView, labelDocument, true);
            if (!capturedOrigin) { visualOrigin = visual.anchoredPosition; capturedOrigin = true; }
            // hitTarget 保持可接收指针；Host 禁用仍允许右键/长按查看。没有 onClick 监听或提交入口。
            hitTarget.interactable = true;
            RefreshState();
        }

        public void InvalidatePresentation() { cachedRevision = -1; cachedOptionId = null; }
        public void FitWidth(float availableWidth)
        {
            if (option == null) return;
            if (option.Presentation.ContentMode == "icon") { layout.preferredWidth = iconOnlyWidth; return; }
            if (maximumTextWidth < minimumTextWidth)
                throw new InvalidOperationException("结算按钮的文字宽度配置无效：最大宽度小于最小宽度。");
            // 首次绑定可能早于父级布局；窄视口只负责裁切和横向滚动，不能把条目最小宽度当作视口门槛。
            var cap = Mathf.Max(minimumTextWidth, Mathf.Floor(Mathf.Min(maximumTextWidth, Mathf.Max(0, availableWidth)) / 2) * 2);
            layout.preferredWidth = Mathf.Min(cap, Mathf.Ceil(Mathf.Max(minimumTextWidth,
                measuredLabelWidth + (option.Presentation.ContentMode == "icon_text" ? iconTextPadding : textPadding)) / 2) * 2);
        }
        public void SetPending(bool value) { pending = value; RefreshState(); }
        public void PrepareArrival(bool reduced)
        {
            animating = false;
            RestoreVisual();
            // 新项先隐藏，等本批布局结束后从实际裁切线外入场，避免首帧在终点闪现。
            if (!reduced && arrivalDuration > 0) visualGroup.alpha = 0;
        }

        public void PlayArrival(bool reduced, float delay, float viewportWidth, float visibleLayoutLeft)
        {
            arrivalDistance = SettlementEffectArrivalMotion.StartOffset(viewportWidth, visibleLayoutLeft, arrivalEdgeInset);
            arrivalStart = Time.unscaledTime + delay; animating = !reduced && arrivalDuration > 0;
            if (animating) { visual.anchoredPosition = visualOrigin + Vector2.right * arrivalDistance; visualGroup.alpha = 0; }
            else RestoreVisual();
        }

        public void ClearBinding()
        {
            if (renderText != null && labelView != null) renderText(labelView, null, true);
            option = null; use = inspect = null; manualScroll = null; scroll = null;
            resolveIcon = null; renderText = null; pending = hovered = executionAllowed = false; gesture.Reset(); InvalidatePresentation();
            labelDocument = descriptionDocument = ariaDocument = null; darkIcon = lightIcon = mutedIcon = null;
            animating = false; if (capturedOrigin) RestoreVisual();
        }

        private void RestoreVisual() { visual.anchoredPosition = visualOrigin; visualGroup.alpha = 1; }
        private void Update()
        {
            if (option == null) return;
            if (pressedButton == PointerEventData.InputButton.Left && gesture.TryLongPress(Time.unscaledTime, longPressDelay))
            { Inspect(); RefreshState(); }
            if (!animating) return;
            var elapsed = Time.unscaledTime - arrivalStart;
            var t = Mathf.Clamp01(elapsed / arrivalDuration);
            var eased = SettlementEffectArrivalMotion.Ease(t, arrivalEasing.x, arrivalEasing.y, arrivalEasing.z, arrivalEasing.w);
            visual.anchoredPosition = visualOrigin + Vector2.right * arrivalDistance * (1 - eased);
            visualGroup.alpha = arrivalFadeDuration > 0 ? Mathf.Clamp01(elapsed / arrivalFadeDuration) : elapsed < 0 ? 0 : 1;
            if (t >= 1) { animating = false; RestoreVisual(); }
        }

        private void RefreshState()
        {
            if (option == null) return;
            var disabled = !executionAllowed;
            background.sprite = pending ? pendingBackground : disabled ? disabledBackground :
                gesture.IsPressed ? pressedBackground : hovered ? hoverBackground : defaultBackground;
            if (option.Presentation.ContentMode != "text") icon.sprite = pending ? lightIcon : disabled ? mutedIcon : darkIcon;
            var textHost = labelView.GetComponent<Text>();
            if (textHost != null) textHost.color = pending ? pendingInk : disabled ? disabledInk :
                gesture.IsPressed ? pressedInk : hovered ? hoverInk : defaultInk;
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (option == null || (e.button != PointerEventData.InputButton.Left && e.button != PointerEventData.InputButton.Right)) return;
            pressedButton = e.button;
            var p = LocalPoint(e); gesture.Begin(e.pointerId, p.x, p.y, Time.unscaledTime); RefreshState();
        }
        public void OnPointerUp(PointerEventData e)
        { if (e.button != pressedButton) return; Move(e); gesture.Release(e.pointerId); RefreshState(); }
        public void OnPointerClick(PointerEventData e)
        {
            if (option == null) return;
            if (e.button == PointerEventData.InputButton.Right && gesture.ConsumeClick(e.pointerId)) { Inspect(); return; }
            if (e.button == PointerEventData.InputButton.Left && gesture.ConsumeClick(e.pointerId) && !pending && executionAllowed)
                use?.Invoke(scope, identity, option.StateRevision, option.OptionId);
        }
        public void OnPointerEnter(PointerEventData e)
        { hovered = true; RefreshState(); }
        public void OnPointerExit(PointerEventData e)
        { hovered = false; gesture.Cancel(e.pointerId); RefreshState(); }
        private void Inspect() { if (option != null) inspect?.Invoke(scope, identity, option.StateRevision, option.OptionId); }
        private Vector2 LocalPoint(PointerEventData e)
        {
            var canvas = GetComponentInParent<Canvas>();
            var root = canvas == null ? transform as RectTransform : canvas.rootCanvas.transform as RectTransform;
            return root != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(root, e.position, e.pressEventCamera, out var p)
                ? p : e.position;
        }
        private void Move(PointerEventData e) { var p = LocalPoint(e); gesture.Move(e.pointerId, p.x, p.y, dragThreshold); }
        public void OnInitializePotentialDrag(PointerEventData e)
        { e.useDragThreshold = false; scroll?.OnInitializePotentialDrag(e); }
        public void OnBeginDrag(PointerEventData e)
        { Move(e); if (e.button == PointerEventData.InputButton.Left) scroll?.OnBeginDrag(e); }
        public void OnDrag(PointerEventData e)
        { Move(e); if (e.button == PointerEventData.InputButton.Left) { manualScroll?.Invoke(); scroll?.OnDrag(e); } RefreshState(); }
        public void OnEndDrag(PointerEventData e)
        { Move(e); if (e.button == PointerEventData.InputButton.Left) scroll?.OnEndDrag(e); RefreshState(); }
        private void OnEnable() { RefreshState(); }
        private void OnDisable() { gesture.Reset(); hovered = false; animating = false; if (capturedOrigin) RestoreVisual(); }
    }
}
