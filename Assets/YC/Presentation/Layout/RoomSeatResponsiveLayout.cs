using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>在标准布局过程中先分配座位宽度，再按文字测高；不改变任何按钮行为。</summary>
    [AddComponentMenu("布局/房间座位自适应布局")]
    public sealed class RoomSeatResponsiveLayout : LayoutGroup
    {
        [SerializeField] private RectTransform frame, header, hostBadge, avatar;
        [SerializeField] private Text nameText, stateText, invitationText;
        [SerializeField] private RectTransform[] managementButtons, occupantButtons;
        [SerializeField] private Vector2 managementButtonSize = new Vector2(154f, 50f);
        [SerializeField] private Vector2 occupantButtonSize = new Vector2(178f, 50f);
        [SerializeField] private Vector2 buttonSpacing = new Vector2(12f, 14f);
        [SerializeField] private Vector2 nameOffset = new Vector2(80f, 10f);
        [SerializeField] private Vector2 badgeInset = new Vector2(56f, 16f);
        [SerializeField, Min(0f)] private float headerInset = 7f;
        [SerializeField, Min(0f)] private float minimumHeaderHeight = 50f;
        [SerializeField, Min(0f)] private float minimumNameHeight = 45f;
        [SerializeField, Min(0f)] private float nameBadgeSpacing = 16f;
        [SerializeField, Min(0f)] private float headerTextBottomPadding = 2f;
        [SerializeField, Min(0f)] private float headerBodySpacing = 3f;
        [SerializeField, Min(0f)] private float avatarTextSpacing = 22f;
        [SerializeField, Min(0f)] private float minimumStateWidth = 220f;
        [SerializeField, Min(0f)] private float actionSpacing = 16f;
        [SerializeField, Min(0f)] private float sectionSpacing = 14f;
        [SerializeField, Min(0f)] private float stateTopInset = 4f;
        [SerializeField, Min(0f)] private float invitationSpacing = 8f;
        [SerializeField, Min(1f)] private float maximumButtonScale = 1.25f;
        [SerializeField, Min(0f)] private float minimumSeatHeight = 202f;

        private bool compact, textBelowAvatar;
        private int columns;
        private float buttonScale, actionsWidth, actionsHeight, actionsX;
        private float headerHeight, nameHeight, bodyTop, informationHeight, stateHeight, invitationHeight;
        private string previousName, previousState, previousInvitation;
        private int previousVisibility;
        private bool hasContentSnapshot;

        // 布局尺寸优先于同物体装饰 Image 的原始图片尺寸。
        public override int layoutPriority => 1;

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            SetLayoutInputForAxis(0f, 0f, 1f, 0);
        }

        public override void SetLayoutHorizontal()
        {
            if (!HasReferences) return;
            var width = rectTransform.rect.width;
            var innerWidth = Mathf.Max(0f, width - padding.horizontal);
            var besideWidth = innerWidth - avatar.rect.width - avatarTextSpacing - minimumStateWidth - actionSpacing;
            var rowWidth = managementButtonSize.x * managementButtons.Length + buttonSpacing.x * (managementButtons.Length - 1);
            var pairWidth = managementButtonSize.x * 2f + buttonSpacing.x;
            compact = besideWidth < pairWidth;
            columns = besideWidth >= rowWidth ? managementButtons.Length : compact && innerWidth < pairWidth ? 1 : 2;
            buttonScale = columns == managementButtons.Length && !compact
                ? Mathf.Max(1f, Mathf.Min(maximumButtonScale, Mathf.Min(besideWidth / rowWidth,
                    avatar.rect.height / Mathf.Max(managementButtonSize.y, occupantButtonSize.y)))) : 1f;
            actionsWidth = Mathf.Max(columns * managementButtonSize.x + (columns - 1) * buttonSpacing.x,
                occupantButtonSize.x) * buttonScale;
            var rows = Mathf.CeilToInt((float)managementButtons.Length / columns);
            actionsHeight = Mathf.Max(rows * managementButtonSize.y + (rows - 1) * buttonSpacing.y,
                occupantButtonSize.y) * buttonScale;
            actionsX = compact ? padding.left : width - padding.right - actionsWidth;
            var informationWidth = compact ? innerWidth : actionsX - actionSpacing - padding.left;
            textBelowAvatar = compact && informationWidth - avatar.rect.width - avatarTextSpacing < minimumStateWidth;
            var textX = textBelowAvatar ? padding.left : padding.left + avatar.rect.width + avatarTextSpacing;
            var textWidth = Mathf.Max(1f, informationWidth - (textX - padding.left));

            SetChildAlongAxis(frame, 0, 0f, width);
            SetChildAlongAxis(header, 0, headerInset, Mathf.Max(0f, width - headerInset * 2f));
            var badgeX = width - badgeInset.x - hostBadge.rect.width;
            SetChildAlongAxis(hostBadge, 0, badgeX);
            SetChildAlongAxis(nameText.rectTransform, 0, nameOffset.x, Mathf.Max(1f, badgeX - nameBadgeSpacing - nameOffset.x));
            SetChildAlongAxis(avatar, 0, padding.left);
            SetChildAlongAxis(stateText.rectTransform, 0, textX, textWidth);
            SetChildAlongAxis(invitationText.rectTransform, 0, textX, textWidth);
            PlaceButtonsHorizontal(managementButtons, managementButtonSize, true);
            PlaceButtonsHorizontal(occupantButtons, occupantButtonSize, false);
        }

        public override void CalculateLayoutInputVertical()
        {
            if (!HasReferences) return;
            // Horizontal 已分配文字宽度，Text.preferredHeight 因此会包含当前宽度下的换行。
            nameHeight = Mathf.Max(minimumNameHeight, Measure(nameText));
            headerHeight = Mathf.Max(minimumHeaderHeight, nameOffset.y + nameHeight + headerTextBottomPadding - headerInset);
            bodyTop = Mathf.Max(padding.top, headerInset + headerHeight + headerBodySpacing);
            stateHeight = Measure(stateText);
            invitationHeight = Measure(invitationText);
            var textHeight = stateTopInset + stateHeight +
                (invitationHeight > 0f ? invitationSpacing + invitationHeight : 0f);
            informationHeight = textBelowAvatar ? avatar.rect.height + sectionSpacing + textHeight :
                Mathf.Max(avatar.rect.height, textHeight);
            var bodyHeight = compact ? informationHeight + sectionSpacing + actionsHeight :
                Mathf.Max(informationHeight, actionsHeight);
            var height = Mathf.Max(minimumSeatHeight, bodyTop + bodyHeight + padding.bottom);
            SetLayoutInputForAxis(height, height, 0f, 1);
        }

        public override void SetLayoutVertical()
        {
            if (!HasReferences) return;
            SetChildAlongAxis(frame, 1, 0f, rectTransform.rect.height);
            SetChildAlongAxis(header, 1, headerInset, headerHeight);
            SetChildAlongAxis(nameText.rectTransform, 1, nameOffset.y, nameHeight);
            SetChildAlongAxis(hostBadge, 1, badgeInset.y);
            SetChildAlongAxis(avatar, 1, bodyTop);
            var textY = bodyTop + stateTopInset + (textBelowAvatar ? avatar.rect.height + sectionSpacing : 0f);
            SetChildAlongAxis(stateText.rectTransform, 1, textY, stateHeight);
            SetChildAlongAxis(invitationText.rectTransform, 1, textY + stateHeight + invitationSpacing, invitationHeight);
            var actionsY = compact ? bodyTop + informationHeight + sectionSpacing :
                bodyTop + Mathf.Max(0f, (rectTransform.rect.height - bodyTop - padding.bottom - actionsHeight) * 0.5f);
            PlaceButtonsVertical(managementButtons, managementButtonSize, actionsY, true);
            PlaceButtonsVertical(occupantButtons, occupantButtonSize, actionsY, false);
        }

        private void PlaceButtonsHorizontal(RectTransform[] buttons, Vector2 size, bool grid)
        {
            for (var i = 0; i < buttons.Length; i++)
            {
                var button = buttons[i];
                if (button == null) continue;
                m_Tracker.Add(this, button, DrivenTransformProperties.Scale);
                button.localScale = new Vector3(buttonScale, buttonScale, 1f);
                var x = actionsX + (grid ? i % columns * (size.x + buttonSpacing.x) * buttonScale :
                    Mathf.Max(0f, actionsWidth - size.x * buttonScale));
                SetChildAlongAxisWithScale(button, 0, x, size.x, buttonScale);
            }
        }

        private void PlaceButtonsVertical(RectTransform[] buttons, Vector2 size, float y, bool grid)
        {
            for (var i = 0; i < buttons.Length; i++)
            {
                if (buttons[i] == null) continue;
                var offset = grid ? i / columns * (size.y + buttonSpacing.y) * buttonScale :
                    Mathf.Max(0f, (actionsHeight - size.y * buttonScale) * 0.5f);
                SetChildAlongAxisWithScale(buttons[i], 1, y + offset, size.y, buttonScale);
            }
        }

        private bool HasReferences => frame != null && header != null && hostBadge != null && avatar != null &&
            nameText != null && stateText != null && invitationText != null &&
            managementButtons != null && managementButtons.Length == 4 && occupantButtons != null && occupantButtons.Length == 3;

        private static float Measure(Text text) => text.isActiveAndEnabled && !string.IsNullOrEmpty(text.text)
            ? Mathf.Ceil(text.preferredHeight) : 0f;

        public bool CaptureContentChanges()
        {
            if (!HasReferences) return false;
            var visibility = (hostBadge.gameObject.activeSelf ? 1 : 0) | (invitationText.gameObject.activeSelf ? 2 : 0);
            for (var i = 0; i < managementButtons.Length; i++)
                if (managementButtons[i] != null && managementButtons[i].gameObject.activeSelf) visibility |= 1 << (i + 2);
            for (var i = 0; i < occupantButtons.Length; i++)
                if (occupantButtons[i] != null && occupantButtons[i].gameObject.activeSelf) visibility |= 1 << (i + 6);
            var changed = !hasContentSnapshot || previousVisibility != visibility || previousName != nameText.text ||
                previousState != stateText.text || previousInvitation != invitationText.text;
            hasContentSnapshot = true;
            previousVisibility = visibility;
            previousName = nameText.text;
            previousState = stateText.text;
            previousInvitation = invitationText.text;
            return changed;
        }
    }
}
