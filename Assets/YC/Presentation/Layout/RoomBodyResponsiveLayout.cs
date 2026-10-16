using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>设置面板保持资产尺寸，成员面板承担宽度变化；高度交给纵向内容适配器。</summary>
    [AddComponentMenu("布局/房间主体自适应布局")]
    public sealed class RoomBodyResponsiveLayout : LayoutGroup
    {
        [SerializeField] private RectTransform memberPanel, settingsPanel;
        [SerializeField] private RoomSeatResponsiveLayout[] seats;
        [SerializeField, Min(0f)] private float columnSpacing = 12f;
        [SerializeField, Min(0f)] private float minimumMemberWidth = 384f;
        [SerializeField, Min(0f)] private float stackedSpacing = 12f;

        public override int layoutPriority => 1;

        private bool Stacked => settingsPanel != null &&
            rectTransform.rect.width < minimumMemberWidth + columnSpacing + settingsPanel.rect.width;

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            var settingsWidth = settingsPanel != null ? settingsPanel.rect.width : 0f;
            SetLayoutInputForAxis(settingsWidth, settingsWidth + columnSpacing + minimumMemberWidth, 1f, 0);
        }

        public override void SetLayoutHorizontal()
        {
            if (memberPanel == null || settingsPanel == null) return;
            var stacked = Stacked;
            var memberWidth = Mathf.Max(0f, rectTransform.rect.width -
                (stacked ? 0f : settingsPanel.rect.width + columnSpacing));
            SetChildAlongAxis(memberPanel, 0, 0f, memberWidth);
            // 此重载只驱动位置，设置面板的宽高与缩放仍由 Prefab 拥有。
            SetChildAlongAxis(settingsPanel, 0, stacked ? 0f : memberWidth + columnSpacing);
        }

        public override void CalculateLayoutInputVertical()
        {
            if (memberPanel == null || settingsPanel == null) return;
            var memberHeight = LayoutUtility.GetPreferredHeight(memberPanel);
            var height = Stacked ? memberHeight + stackedSpacing + settingsPanel.rect.height :
                Mathf.Max(memberHeight, settingsPanel.rect.height);
            SetLayoutInputForAxis(height, height, 0f, 1);
        }

        public override void SetLayoutVertical()
        {
            if (memberPanel == null || settingsPanel == null) return;
            var memberHeight = LayoutUtility.GetPreferredHeight(memberPanel);
            SetChildAlongAxis(memberPanel, 1, 0f, memberHeight);
            SetChildAlongAxis(settingsPanel, 1, Stacked ? memberHeight + stackedSpacing : 0f);
        }

        /// <summary>只在一次房间 Render 后比较已绑定的四座，不在 Update 中扫描层级。</summary>
        public void RefreshContentLayout()
        {
            var changed = false;
            if (seats != null)
                foreach (var seat in seats)
                    if (seat != null) changed |= seat.CaptureContentChanges();
            if (changed) SetDirty();
        }
    }
}
