using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>按字排版混合字体，数字连续分段，显式换行保留在描述区内。</summary>
    public sealed class UiInlineTextLayout : LayoutGroup
    {
        [SerializeField] private float minimumLineHeight = 28f;
        [SerializeField] private float lineSpacing = 2f;
        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            SetLayoutInputForAxis(0f, 0f, 1f, 0);
        }
        public override void SetLayoutHorizontal() => Arrange(true);
        public override void CalculateLayoutInputVertical()
        {
            var height = Arrange(false);
            SetLayoutInputForAxis(height, height, 0f, 1);
        }
        public override void SetLayoutVertical() => Arrange(true);

        private float Arrange(bool place)
        {
            var available = Mathf.Max(1f, rectTransform.rect.width - padding.horizontal);
            var x = 0f;
            var y = (float)padding.top;
            var lineHeight = minimumLineHeight;
            foreach (var child in rectChildren)
            {
                var text = child.GetComponent<Text>();
                var newline = text != null && (text.text == "\n" || text.text == "\r" || text.text == "\r\n");
                var width = newline ? 0f : Mathf.Min(available, LayoutUtility.GetPreferredWidth(child));
                if (newline || (x > 0f && x + width > available + .01f))
                {
                    y += lineHeight + lineSpacing; x = 0f; lineHeight = minimumLineHeight;
                }
                if (newline)
                {
                    if (place) { SetChildAlongAxis(child, 0, padding.left, 0f); SetChildAlongAxis(child, 1, y, 0f); }
                    continue;
                }
                var height = Mathf.Max(minimumLineHeight, LayoutUtility.GetPreferredHeight(child));
                if (place)
                {
                    SetChildAlongAxis(child, 0, padding.left + x, width);
                    SetChildAlongAxis(child, 1, y, height);
                }
                lineHeight = Mathf.Max(lineHeight, height);
                x += width;
            }
            return y + (rectChildren.Count == 0 ? 0f : lineHeight) + padding.bottom;
        }
    }
}
