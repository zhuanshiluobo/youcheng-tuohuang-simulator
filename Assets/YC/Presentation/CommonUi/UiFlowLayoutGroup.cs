using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    // Positions existing semantic Text and Image children. It never creates UI objects.
    public sealed class UiFlowLayoutGroup : LayoutGroup
    {
        [SerializeField] private float horizontalSpacing = 2f;
        [SerializeField] private float verticalSpacing = 2f;
        [SerializeField] private float minimumRowHeight = 28f;
        private float measuredHeight;

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            SetLayoutInputForAxis(padding.horizontal + 40f, -1f, -1f, 0);
        }

        public override void CalculateLayoutInputVertical()
        {
            measuredHeight = Arrange(false);
            SetLayoutInputForAxis(measuredHeight, measuredHeight, -1f, 1);
        }

        public override void SetLayoutHorizontal() { Arrange(true); }
        public override void SetLayoutVertical() { Arrange(true); }

        private float Arrange(bool place)
        {
            var available = Mathf.Max(1f, rectTransform.rect.width - padding.horizontal);
            var verticalStart = place ? GetStartOffset(1, Mathf.Max(0f, measuredHeight - padding.vertical)) : padding.top;
            float Width(RectTransform child) => Mathf.Min(available,
                Mathf.Max(LayoutUtility.GetMinWidth(child), LayoutUtility.GetPreferredWidth(child)));
            var y = 0f;
            for (var start = 0; start < rectChildren.Count;)
            {
                var end = start;
                var usedWidth = 0f;
                var flexible = 0f;
                var rowHeight = minimumRowHeight;
                while (end < rectChildren.Count)
                {
                    var child = rectChildren[end];
                    var width = Width(child);
                    var next = usedWidth + (end > start ? horizontalSpacing : 0f) + width;
                    if (end > start && next > available) break;
                    usedWidth = next;
                    flexible += Mathf.Max(0f, LayoutUtility.GetFlexibleWidth(child));
                    rowHeight = Mathf.Max(rowHeight, LayoutUtility.GetPreferredHeight(child));
                    end++;
                }
                var x = 0f;
                for (var i = start; place && i < end; i++)
                {
                    var child = rectChildren[i];
                    var width = Width(child);
                    if (flexible > 0f)
                        width += Mathf.Max(0f, available - usedWidth) * Mathf.Max(0f, LayoutUtility.GetFlexibleWidth(child)) / flexible;
                    var height = Mathf.Max(minimumRowHeight, LayoutUtility.GetPreferredHeight(child));
                    SetChildAlongAxis(child, 0, padding.left + x, width);
                    var fragment = child.GetComponent<UiSemanticText>();
                    var offset = fragment == null ? 0f : fragment.BaselineOffset;
                    SetChildAlongAxis(child, 1, verticalStart + y + offset, height);
                    x += width + horizontalSpacing;
                }
                y += rowHeight + verticalSpacing;
                start = end;
            }
            return padding.vertical + Mathf.Max(0f, y - verticalSpacing);
        }
    }
}
