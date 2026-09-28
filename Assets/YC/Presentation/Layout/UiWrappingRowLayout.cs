using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>按可用宽度分行，窄区域保留最少列数；支持单行横向滚动。</summary>
    [AddComponentMenu("Layout/Wrapping Row Layout")]
    public sealed class UiWrappingRowLayout : LayoutGroup
    {
        [SerializeField, Min(0), Tooltip("0 表示只由可用宽度决定列数。")]
        private int maximumColumns = 4;
        [SerializeField, Min(1)] private int minimumColumns = 1;
        [SerializeField, Min(1f), Tooltip("增加列数时的参考槽宽；窄屏仍优先保持最少列数。")]
        private float minimumSlotWidth = 176f;
        [SerializeField, Min(1f)] private float preferredSlotWidth = 212f;
        [SerializeField] private Vector2 spacing = new Vector2(16f, 16f);
        [SerializeField] private RectTransform heightViewport;
        [SerializeField] private bool expandSingleRowToHeight;

        public int Columns { get; private set; } = 1;
        public bool HorizontalMode { get; private set; }
        public float HorizontalContentWidth => padding.horizontal + rectChildren.Count * FitRowHeight(preferredSlotWidth) +
            Mathf.Max(0, rectChildren.Count - 1) * spacing.x;
        private Vector2 lastViewportSize;
        private int ColumnLimit => Mathf.Max(1, maximumColumns > 0
            ? Mathf.Min(maximumColumns, rectChildren.Count) : rectChildren.Count);

        public bool WantsSingleRow(float availableWidth)
        {
            if (!expandSingleRowToHeight || rectChildren.Count == 0) return false;
            var columns = Mathf.Clamp(Mathf.FloorToInt((availableWidth - padding.horizontal + spacing.x) /
                (minimumSlotWidth + spacing.x)), Mathf.Min(minimumColumns, ColumnLimit), ColumnLimit);
            return rectChildren.Count <= columns;
        }

        public void SetHorizontalMode(bool value)
        {
            if (HorizontalMode == value) return;
            HorizontalMode = value;
            SetDirty();
        }

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            SetLayoutInputForAxis(padding.horizontal,
                padding.horizontal + ColumnLimit * preferredSlotWidth + (ColumnLimit - 1) * spacing.x, -1f, 0);
        }

        public override void SetLayoutHorizontal()
        {
            var available = Mathf.Max(0f, rectTransform.rect.width - padding.horizontal);
            var count = rectChildren.Count;
            var minColumns = Mathf.Min(Mathf.Max(1, minimumColumns), ColumnLimit);
            Columns = HorizontalMode ? Mathf.Max(1, count) : Mathf.Clamp(
                Mathf.FloorToInt((available + spacing.x) / (minimumSlotWidth + spacing.x)), minColumns, ColumnLimit);
            var width = HorizontalMode ? FitRowHeight(preferredSlotWidth) : Mathf.Min(preferredSlotWidth,
                Mathf.Max(0f, (available - spacing.x * (Columns - 1)) / Columns));
            if (!HorizontalMode && count <= Columns) width = FitRowHeight(width);
            var start = GetStartOffset(0, Columns * width + (Columns - 1) * spacing.x);
            for (var i = 0; i < count; i++)
                SetChildAlongAxis(rectChildren[i], 0, start + i % Columns * (width + spacing.x), width);
        }

        private void LateUpdate()
        {
            if (heightViewport == null || lastViewportSize == heightViewport.rect.size) return;
            lastViewportSize = heightViewport.rect.size;
            SetDirty();
        }

        private float FitRowHeight(float width)
        {
            if (heightViewport == null || rectChildren.Count == 0) return width;
            var parentLayout = transform.parent == null ? null : transform.parent.GetComponent<LayoutGroup>();
            var available = Mathf.Max(0f, heightViewport.rect.height - padding.vertical -
                (parentLayout == null ? 0 : parentLayout.padding.vertical));
            var fittedWidth = expandSingleRowToHeight ? float.PositiveInfinity : width;
            foreach (var slot in rectChildren)
            {
                if (!expandSingleRowToHeight && MeasureSupplyHeight(slot, fittedWidth) <= available) continue;
                var low = 0f;
                var high = width;
                if (expandSingleRowToHeight)
                {
                    // 完整卡槽随宽度等比增高；先找到足以覆盖视口高度的上界。
                    for (var i = 0; i < 20 && MeasureSupplyHeight(slot, high) < available; i++) high *= 2f;
                }
                for (var i = 0; i < 20; i++)
                {
                    var mid = (low + high) * .5f;
                    if (MeasureSupplyHeight(slot, mid) <= available) low = mid;
                    else high = mid;
                }
                fittedWidth = Mathf.Min(fittedWidth, low);
            }
            return fittedWidth;
        }

        private static float MeasureSupplyHeight(RectTransform slot, float width)
        {
            var layout = slot.GetComponent<VerticalLayoutGroup>();
            var total = layout == null ? 0f : layout.padding.vertical;
            var count = 0;
            foreach (Transform child in slot)
            {
                if (!child.gameObject.activeInHierarchy) continue;
                var card = child.GetComponent<FacilityEffectCardView>();
                if (card == null) continue;
                total += card.MeasureSlotSize(width).y;
                if (count++ > 0 && layout != null) total += layout.spacing;
            }
            return count == 0 ? LayoutUtility.GetPreferredHeight(slot) : total;
        }

        public override void CalculateLayoutInputVertical()
        {
            var height = (float)padding.vertical;
            for (var first = 0; first < rectChildren.Count; first += Columns)
                height += RowHeight(first) + (first == 0 ? 0f : spacing.y);
            SetLayoutInputForAxis(height, height, -1f, 1);
        }

        public override void SetLayoutVertical()
        {
            var y = GetStartOffset(1, GetTotalPreferredSize(1) - padding.vertical);
            for (var first = 0; first < rectChildren.Count; first += Columns)
            {
                for (var i = first; i < Mathf.Min(first + Columns, rectChildren.Count); i++)
                    SetChildAlongAxis(rectChildren[i], 1, y, LayoutUtility.GetPreferredHeight(rectChildren[i]));
                y += RowHeight(first) + spacing.y;
            }
        }

        private float RowHeight(int first)
        {
            var height = 0f;
            for (var i = first; i < Mathf.Min(first + Columns, rectChildren.Count); i++)
                height = Mathf.Max(height, LayoutUtility.GetPreferredHeight(rectChildren[i]));
            return height;
        }
    }
}
