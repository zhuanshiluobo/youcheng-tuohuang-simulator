using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>按子物体布局尺寸排布，只将剩余宽度均分到间距。</summary>
    [AddComponentMenu("布局/自适应列间距横向布局")]
    public sealed class UiDistributedHorizontalLayoutGroup : HorizontalLayoutGroup
    {
        public override void CalculateLayoutInputHorizontal()
        {
            var configuredSpacing = m_Spacing;
            try { m_Spacing = 0f; base.CalculateLayoutInputHorizontal(); }
            finally { m_Spacing = configuredSpacing; }
        }

        public override void SetLayoutHorizontal()
        {
            var gapCount = rectChildren.Count - 1;
            if (gapCount <= 0) { base.SetLayoutHorizontal(); return; }
            var minimum = GetTotalMinSize(0);
            var preferred = GetTotalPreferredSize(0);
            var flexible = GetTotalFlexibleSize(0);
            var surplus = Mathf.Max(0f, rectTransform.rect.width - preferred);
            var configuredSpacing = m_Spacing;
            try
            {
                m_Spacing = surplus / gapCount;
                SetLayoutInputForAxis(minimum + surplus, preferred + surplus, flexible, 0);
                base.SetLayoutHorizontal();
            }
            finally
            {
                m_Spacing = configuredSpacing;
                SetLayoutInputForAxis(minimum, preferred, flexible, 0);
            }
        }
    }
}
