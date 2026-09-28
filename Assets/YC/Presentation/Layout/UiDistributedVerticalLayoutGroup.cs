using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>保留原生纵向布局的行尺寸，将余量仅均分到行间；不预留固定间距。</summary>
    [AddComponentMenu("布局/自适应行间距纵向布局")]
    public sealed class UiDistributedVerticalLayoutGroup : VerticalLayoutGroup
    {
        public override void CalculateLayoutInputVertical()
        {
            var configuredSpacing = m_Spacing;
            try
            {
                // 内容的所需高度只包括行和 Padding，不能先用固定间距撑大 ScrollRect 内容。
                m_Spacing = 0f;
                base.CalculateLayoutInputVertical();
            }
            finally
            {
                m_Spacing = configuredSpacing;
            }
        }

        public override void SetLayoutVertical()
        {
            var gapCount = rectChildren.Count - 1;
            var preferred = GetTotalPreferredSize(1);
            var surplus = Mathf.Max(0f, rectTransform.rect.height - preferred);
            if (gapCount <= 0)
            {
                base.SetLayoutVertical();
                return;
            }

            var minimum = GetTotalMinSize(1);
            var flexible = GetTotalFlexibleSize(1);
            var configuredSpacing = m_Spacing;
            try
            {
                m_Spacing = surplus / gapCount;
                // 剩余高度已分给间距，原生布局无需再扩大行或追加整体对齐偏移。
                SetLayoutInputForAxis(minimum + surplus, preferred + surplus, flexible, 1);
                base.SetLayoutVertical();
            }
            finally
            {
                m_Spacing = configuredSpacing;
                SetLayoutInputForAxis(minimum, preferred, flexible, 1);
            }
        }
    }
}
