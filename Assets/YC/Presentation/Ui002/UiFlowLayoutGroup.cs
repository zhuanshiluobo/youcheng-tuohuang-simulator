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
            var x = 0f;
            var y = 0f;
            var rowHeight = minimumRowHeight;
            for (var i = 0; i < rectChildren.Count; i++)
            {
                var child = rectChildren[i];
                var preferredWidth = LayoutUtility.GetPreferredWidth(child);
                var width = Mathf.Min(available, Mathf.Max(LayoutUtility.GetMinWidth(child), preferredWidth));
                if (x > 0f && x + width > available)
                {
                    y += rowHeight + verticalSpacing;
                    x = 0f;
                    rowHeight = minimumRowHeight;
                }

                var height = Mathf.Max(minimumRowHeight, LayoutUtility.GetPreferredHeight(child));
                rowHeight = Mathf.Max(rowHeight, height);
                if (place)
                {
                    SetChildAlongAxis(child, 0, padding.left + x, width);
                    var fragment = child.GetComponent<UiSemanticText>();
                    var offset = fragment == null ? 0f : fragment.BaselineOffset;
                    SetChildAlongAxis(child, 1, padding.top + y + offset, height);
                }
                x += width + horizontalSpacing;
            }

            return padding.vertical + y + rowHeight;
        }
    }
}
