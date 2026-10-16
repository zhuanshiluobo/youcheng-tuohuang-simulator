using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>
    /// Card-Picker-UI-v1 的尺寸输入。HLG/CSF 与各卡项 VLG 独占矩形排布；本组件不写子 RectTransform。
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent]
    [RequireComponent(typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter))]
    public sealed class CardPickerRowLayout : UIBehaviour, ILayoutElement
    {
        [SerializeField] private RectTransform viewport;
        [SerializeField] private HorizontalLayoutGroup row;
        [SerializeField] private ScrollRect scrollRect;
        [SerializeField] private GameObject scrollbarRoot;
        [SerializeField, Min(1f)] private float maximumFaceWidth = 288f;
        [SerializeField, Min(1)] private int visibleCardLimit = 5;
        [SerializeField, Min(1f)] private float minimumFaceWidth = 120f;
        [SerializeField, Min(1f)] private float widthStep = 12f;
        [SerializeField, Min(.01f)] private float faceHeightPerWidth = 17f / 12f;
        [SerializeField, Min(1f)] private float referenceViewportHeight = 482f;
        [SerializeField, Min(0)] private int horizontalInset = 16;
        [SerializeField, Min(0)] private int verticalInset = 28;
        [SerializeField, Min(0)] private int contentSidePadding = 1;
        [SerializeField, Min(0f)] private float spacing = 16f;
        [SerializeField, Min(0f)] private float scrollbarReservedHeight = 44f;

        private readonly List<FacilityEffectCardView> cards = new List<FacilityEffectCardView>();
        private readonly Vector3[] corners = new Vector3[4];
        private GameObject lastSelection;
        private Vector2 lastViewportSize;
        private bool revealSelection;
        private float measuredContentWidth;

        public int CardCount => cards.Count;
        public float CardFaceWidth { get; private set; }
        public float CardFaceHeight => CardFaceWidth * faceHeightPerWidth;
        public bool FitsAvailableHeight { get; private set; } = true;
        public bool IsOverflowing => viewport != null && measuredContentWidth > viewport.rect.width + .01f;
        public float minWidth => viewport == null ? 0f : Mathf.Max(0f, viewport.rect.width);
        public float preferredWidth => -1f;
        public float flexibleWidth => -1f;
        public float minHeight => -1f;
        public float preferredHeight => -1f;
        public float flexibleHeight => -1f;
        public int layoutPriority => 1;

        protected override void OnEnable()
        {
            base.OnEnable();
            ConfigureNativeComponents();
            lastSelection = null;
            Refresh();
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            if (viewport != null && viewport.rect.size != lastViewportSize) Refresh();
        }

        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            Refresh();
        }

        private void OnTransformChildrenChanged() => Refresh();

        /// <summary>候选增删或可见状态改变后调用；不改变选择、候选顺序或滚动状态。</summary>
        public void Refresh()
        {
            revealSelection = true;
            if (IsActive() && !CanvasUpdateRegistry.IsRebuildingLayout())
                LayoutRebuilder.MarkLayoutForRebuild(transform as RectTransform);
        }

        public void CalculateLayoutInputHorizontal()
        {
            if (row == null || row.transform != transform || viewport == null) return;
            var oldCount = cards.Count;
            cards.Clear();
            for (var i = 0; i < row.transform.childCount; i++)
            {
                var child = row.transform.GetChild(i);
                var card = child.GetComponent<FacilityEffectCardView>();
                var element = child.GetComponent<LayoutElement>();
                if (card != null && card.isActiveAndEnabled && element != null && !element.ignoreLayout)
                    cards.Add(card);
            }
            // 阈值以内由content的viewport最小宽度与HLG居中自然留白；
            // 仅长行使用原包两侧各1px，避免分数视口宽度下额外2px造成真实微溢出。

            var size = viewport.rect.size;
            var minimum = Mathf.Ceil(minimumFaceWidth / widthStep) * widthStep;
            var width = 0f;
            if (cards.Count > 0)
            {
                var fittingCount = Mathf.Min(cards.Count, Mathf.Max(1, visibleCardLimit));
                width = Mathf.Min(maximumFaceWidth,
                    FloorWidth((size.x - (fittingCount - 1) * spacing) / fittingCount - 2f * horizontalInset));
                width = Mathf.Max(minimum, FloorWidth(width));
                var height = Mathf.Min(referenceViewportHeight, Mathf.Max(0f, size.y));
                // 长行沿用阈值张数的卡宽；整页适配不改变卡牌逻辑尺寸。
                width = Mathf.Min(width, Mathf.Max(minimum, FloorWidth((height - 2f * verticalInset) / faceHeightPerWidth)));
            }

            if (oldCount != cards.Count || !Mathf.Approximately(width, CardFaceWidth) || size != lastViewportSize)
                revealSelection = true;
            lastViewportSize = size;
            CardFaceWidth = width;
            measuredContentWidth = RowWidth(cards.Count, width);
            ApplyRowPadding();
            var requiredHeight = CardFaceHeight + 2f * verticalInset + (IsOverflowing ? Mathf.CeilToInt(scrollbarReservedHeight) : 0f);
            FitsAvailableHeight = cards.Count == 0 || requiredHeight <= size.y + .01f;

            foreach (var card in cards) ApplyCardInputs(card);
            // 本组件与HLG同挂一处，组件顺序可能使HLG先计算。更新输入后重测原生HLG，不自行排位。
            row.CalculateLayoutInputHorizontal();
        }

        public void CalculateLayoutInputVertical() { }

        private float FloorWidth(float value) => Mathf.Floor(Mathf.Max(0f, value) / widthStep) * widthStep;
        private int RowSidePadding(int count) => count > visibleCardLimit ? contentSidePadding : 0;

        private void ApplyRowPadding()
        {
            var horizontal = RowSidePadding(cards.Count);
            var bottom = IsOverflowing ? Mathf.CeilToInt(scrollbarReservedHeight) : 0;
            var padding = row.padding;
            if (padding.left != horizontal || padding.right != horizontal || padding.top != 0 || padding.bottom != bottom)
                row.padding = new RectOffset(horizontal, horizontal, 0, bottom);
        }
        private float RowWidth(int count, float faceWidth) => 2f * RowSidePadding(count) +
            count * (faceWidth + 2f * horizontalInset) + Mathf.Max(0, count - 1) * spacing;

        private void ApplyCardInputs(FacilityEffectCardView card)
        {
            var item = card.GetComponent<LayoutElement>();
            var column = card.GetComponent<VerticalLayoutGroup>();
            var face = card.transform.Find("Card Face");
            var faceElement = face == null ? null : face.GetComponent<LayoutElement>();
            if (item == null || column == null || faceElement == null) return;
            SetSizeInput(item, CardFaceWidth + 2f * horizontalInset, CardFaceHeight + 2f * verticalInset);
            SetSizeInput(faceElement, CardFaceWidth, CardFaceHeight);
            SetPadding(column, horizontalInset, verticalInset);
            column.spacing = 0f;
            column.childAlignment = TextAnchor.MiddleCenter;
            column.childControlWidth = column.childControlHeight = true;
            column.childForceExpandWidth = column.childForceExpandHeight = false;
            column.childScaleWidth = column.childScaleHeight = false;
            if (card.DetailsButton != null)
            {
                var details = card.DetailsButton.GetComponent<LayoutElement>();
                if (details != null) details.ignoreLayout = true;
            }
        }

        private static void SetSizeInput(LayoutElement element, float width, float height)
        {
            element.minWidth = element.preferredWidth = width;
            element.minHeight = element.preferredHeight = height;
            element.flexibleWidth = element.flexibleHeight = 0f;
        }

        private static void SetPadding(HorizontalOrVerticalLayoutGroup layout, int horizontal, int vertical)
        {
            var padding = layout.padding;
            if (padding.left != horizontal || padding.right != horizontal || padding.top != vertical || padding.bottom != vertical)
                layout.padding = new RectOffset(horizontal, horizontal, vertical, vertical);
        }

        private void ConfigureNativeComponents()
        {
            if (row == null) row = GetComponent<HorizontalLayoutGroup>();
            if (row == null || row.transform != transform) return;
            ApplyRowPadding();
            row.spacing = spacing;
            row.childAlignment = TextAnchor.MiddleCenter;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = row.childForceExpandHeight = false;
            row.childScaleWidth = row.childScaleHeight = false;
            var fitter = GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
            // Content的固定preferredWidth会以高优先级盖掉HLG测得的长行宽度。
            var contentInput = GetComponent<LayoutElement>();
            if (contentInput != null) contentInput.preferredWidth = -1f;
            if (scrollRect == null) return;
            scrollRect.viewport = viewport;
            scrollRect.content = transform as RectTransform;
            scrollRect.horizontal = true;
            scrollRect.vertical = false;
            scrollRect.horizontalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            // 原生ScrollRect在仅横向滚动时已经把垂直鼠标滚轮转到X轴。
        }

        private void LateUpdate()
        {
            if (viewport == null || row == null || scrollRect == null) return;
            if (viewport.rect.size != lastViewportSize) Refresh();
            if (scrollbarRoot != null && scrollbarRoot.activeSelf != IsOverflowing)
                scrollbarRoot.SetActive(IsOverflowing);
            if (!IsOverflowing && Mathf.Abs(scrollRect.content.anchoredPosition.x) > .01f)
            {
                scrollRect.StopMovement();
                scrollRect.horizontalNormalizedPosition = 0f;
            }
            var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            if (selected == lastSelection && !revealSelection) return;
            lastSelection = selected;
            revealSelection = false;
            if (selected != null) EnsureVisible(selected.transform as RectTransform);
        }

        /// <summary>键盘/手柄焦点或调用方选中项改变时，只调整ScrollRect的滚动值。</summary>
        public void EnsureVisible(RectTransform target)
        {
            if (target == null || viewport == null || scrollRect == null || row == null ||
                !target.gameObject.activeInHierarchy || !target.IsChildOf(row.transform)) return;
            var item = target;
            while (item != null && item.parent != null && item.parent != row.transform) item = item.parent as RectTransform;
            if (item == null || item.GetComponent<FacilityEffectCardView>() == null) return;
            if (!CanvasUpdateRegistry.IsRebuildingLayout()) Canvas.ForceUpdateCanvases();
            var hiddenWidth = (row.transform as RectTransform).rect.width - viewport.rect.width;
            if (hiddenWidth <= .01f) return;
            item.GetWorldCorners(corners);
            var left = float.PositiveInfinity;
            var right = float.NegativeInfinity;
            foreach (var corner in corners)
            {
                var local = viewport.InverseTransformPoint(corner);
                left = Mathf.Min(left, local.x);
                right = Mathf.Max(right, local.x);
            }
            var delta = left < viewport.rect.xMin ? left - viewport.rect.xMin :
                right > viewport.rect.xMax ? right - viewport.rect.xMax : 0f;
            if (Mathf.Abs(delta) <= .01f) return;
            scrollRect.StopMovement();
            scrollRect.horizontalNormalizedPosition = Mathf.Clamp01(scrollRect.horizontalNormalizedPosition + delta / hiddenWidth);
        }

        public bool TryValidateConfiguration(out string reason)
        {
            reason = string.Empty;
            if (viewport == null || row == null || scrollRect == null || scrollbarRoot == null || row.transform != transform)
                reason = "卡牌行必须配置同对象HLG、实际Viewport、ScrollRect和滚动条根。";
            else if (widthStep <= 0f || faceHeightPerWidth <= 0f || maximumFaceWidth < minimumFaceWidth || visibleCardLimit < 1)
                reason = "卡面尺寸、比例或最小可读宽度配置无效。";
            else
                for (var i = 0; i < row.transform.childCount; i++)
                {
                    var child = row.transform.GetChild(i);
                    if (!child.gameObject.activeSelf) continue;
                    var item = child.GetComponent<LayoutElement>();
                    if (item != null && item.ignoreLayout) continue;
                    var card = child.GetComponent<FacilityEffectCardView>();
                    var face = child.Find("Card Face");
                    if (card == null || item == null || child.GetComponent<VerticalLayoutGroup>() == null ||
                        face == null || face.GetComponent<LayoutElement>() == null ||
                        (card.DetailsButton != null && card.DetailsButton.GetComponent<LayoutElement>() == null))
                    {
                        reason = "卡牌行只允许正式卡项：每项需FacilityEffectCardView、LE、VLG、Card Face的LE；详情按钮需ignoreLayout。";
                        break;
                    }
                }
            return string.IsNullOrEmpty(reason);
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(RectTransform visibleViewport, HorizontalLayoutGroup horizontalRow,
            ScrollRect scroll, GameObject scrollbar)
        {
            viewport = visibleViewport;
            row = horizontalRow;
            scrollRect = scroll;
            scrollbarRoot = scrollbar;
            ConfigureNativeComponents();
            Refresh();
        }
        protected override void OnValidate()
        {
            base.OnValidate();
            widthStep = Mathf.Max(1f, widthStep);
            faceHeightPerWidth = Mathf.Max(.01f, faceHeightPerWidth);
            ConfigureNativeComponents();
            Refresh();
        }
#endif
    }
}
