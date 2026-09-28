using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>保留原生网格，纵向剩余空间均分给各行之间和上下两端。</summary>
    [AddComponentMenu("布局/上下均分间距网格布局")]
    public sealed class UiDistributedGridLayoutGroup : GridLayoutGroup
    {
        public override void SetLayoutVertical()
        {
            if (constraint != Constraint.FixedColumnCount || rectChildren.Count == 0)
            {
                base.SetLayoutVertical();
                return;
            }

            var rows = Mathf.CeilToInt((float)rectChildren.Count / Mathf.Max(1, constraintCount));
            var requiredHeight = padding.vertical + rows * cellSize.y + (rows - 1) * spacing.y;
            var surplus = Mathf.Max(0f, rectTransform.rect.height - requiredHeight);
            var minimumSpacing = m_Spacing;
            var alignment = m_ChildAlignment;
            try
            {
                m_Spacing.y += surplus / (rows + 1);
                // 居中把剩下的两份空间分给上下两端，保留原来的水平对齐。
                m_ChildAlignment = (TextAnchor)(3 + (int)alignment % 3);
                base.SetLayoutVertical();
            }
            finally
            {
                m_Spacing = minimumSpacing;
                m_ChildAlignment = alignment;
            }
        }
    }
}
