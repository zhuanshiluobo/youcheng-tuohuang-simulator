using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>由内容布局报告按钮高度，背景图的原始尺寸不参与高度计算。</summary>
    public sealed class UiContentHeightLayout : LayoutGroup
    {
        [SerializeField] private RectTransform content;
        public override int layoutPriority => 2;

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            SetLayoutInputForAxis(0f, -1f, 1f, 0);
        }

        public override void CalculateLayoutInputVertical()
        {
            var height = content == null ? 0f : LayoutUtility.GetPreferredHeight(content);
            SetLayoutInputForAxis(height, height, 0f, 1);
        }

        public override void SetLayoutHorizontal()
        {
            if (content != null) SetChildAlongAxis(content, 0, 0f, rectTransform.rect.width);
        }

        public override void SetLayoutVertical()
        {
            if (content != null) SetChildAlongAxis(content, 1, 0f, rectTransform.rect.height);
        }
    }
}
