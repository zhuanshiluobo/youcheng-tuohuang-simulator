using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>效果对话框共享的移动与折叠状态。</summary>
    public sealed class EffectDialogCollapsiblePanel : MonoBehaviour,
        IBeginDragHandler,
        IDragHandler
    {
        private RectTransform panel;
        private Canvas canvas;
        private Image overlayImage;
        private GameObject expandedContent;
        private Text collapsedSummaryText;
        private RectTransform toggleRect;
        private Text toggleText;
        private Image toggleIcon;
        [SerializeField] private Sprite triangleUpSprite;
        [SerializeField] private Sprite triangleDownSprite;
        private Vector2 expandedSize;
        private float collapsedHeight;
        private string collapseLabel;
        private string expandLabel;
        private Color expandedOverlayColor;
        private Color collapsedOverlayColor;
        private bool expandedOverlayRaycastTarget;
        private bool collapsedOverlayRaycastTarget;
        private EffectDialogRectLayout collapsedToggleLayout;
        private EffectDialogRectLayout expandedToggleLayout;
        private bool configured;
        private bool collapsed;
        private bool clampToCanvasBounds;

        public bool IsCollapsed => configured && collapsed;

        public bool TryValidateVisualConfiguration(out string reason)
        {
            if (triangleUpSprite == null || triangleDownSprite == null)
            {
                reason = "Effect dialog collapsible panel requires persistent up/down triangle sprites.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        internal void Configure(EffectDialogCollapseSpec spec)
        {
            if (spec == null)
            {
                configured = false;
                return;
            }

            panel = spec.Panel == null ? transform as RectTransform : spec.Panel;
            canvas = spec.Canvas == null && panel != null
                ? panel.GetComponentInParent<Canvas>()
                : spec.Canvas;
            overlayImage = spec.OverlayImage;
            expandedContent = spec.ExpandedContent;
            collapsedSummaryText = spec.CollapsedSummaryText;
            toggleRect = spec.ToggleRect;
            toggleText = spec.ToggleText;
            toggleIcon = spec.ToggleIcon;
            expandedSize = spec.ExpandedSize;
            clampToCanvasBounds = spec.ClampToCanvasBounds;
            collapsedHeight = spec.CollapsedHeight;
            collapseLabel = spec.CollapseLabel ?? string.Empty;
            expandLabel = spec.ExpandLabel ?? string.Empty;
            collapsedOverlayColor = spec.CollapsedOverlayColor;
            collapsedOverlayRaycastTarget = spec.CollapsedOverlayRaycastTarget;
            collapsedToggleLayout = spec.CollapsedToggleLayout;
            expandedToggleLayout = spec.ExpandedToggleLayout;
            expandedOverlayColor = overlayImage == null ? Color.clear : overlayImage.color;
            expandedOverlayRaycastTarget = overlayImage != null && overlayImage.raycastTarget;
            collapsed = spec.StartCollapsed;
            configured = panel != null;
            ApplyCollapseState();
        }

        public void Toggle()
        {
            SetCollapsed(!collapsed);
        }

        public void SetCollapsed(bool value)
        {
            if (!configured || collapsed == value)
            {
                return;
            }

            collapsed = value;
            ApplyCollapseState();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!configured || panel == null || eventData == null)
            {
                return;
            }

            var scaleFactor = canvas == null || canvas.scaleFactor <= 0f ? 1f : canvas.scaleFactor;
            var position = panel.anchoredPosition + eventData.delta / scaleFactor;
            panel.anchoredPosition = clampToCanvasBounds ? ClampToParent(position) : position;
        }

        private Vector2 ClampToParent(Vector2 position)
        {
            var parent = panel == null ? null : panel.parent as RectTransform;
            if (parent == null)
            {
                return position;
            }

            var horizontalLimit = Mathf.Max(0f, (parent.rect.width - panel.rect.width) * 0.5f);
            var verticalLimit = Mathf.Max(0f, (parent.rect.height - panel.rect.height) * 0.5f);
            return new Vector2(
                Mathf.Clamp(position.x, -horizontalLimit, horizontalLimit),
                Mathf.Clamp(position.y, -verticalLimit, verticalLimit));
        }

        private void ApplyCollapseState()
        {
            if (!configured || panel == null)
            {
                return;
            }

            var previousHeight = panel.sizeDelta.y;
            var targetSize = collapsed
                ? new Vector2(expandedSize.x, collapsedHeight)
                : expandedSize;
            panel.sizeDelta = targetSize;
            panel.anchoredPosition += new Vector2(0f, (previousHeight - targetSize.y) * 0.5f);

            if (expandedContent != null)
            {
                expandedContent.SetActive(!collapsed);
            }

            if (collapsedSummaryText != null)
            {
                collapsedSummaryText.gameObject.SetActive(collapsed);
            }

            if (toggleText != null)
            {
                toggleText.text = collapsed ? expandLabel : collapseLabel;
            }

            if (toggleIcon != null)
            {
                toggleIcon.sprite = collapsed ? triangleDownSprite : triangleUpSprite;
            }
            if (collapsed)
            {
                collapsedToggleLayout.ApplyTo(toggleRect);
            }
            else
            {
                expandedToggleLayout.ApplyTo(toggleRect);
            }

            if (overlayImage != null)
            {
                overlayImage.color = collapsed ? collapsedOverlayColor : expandedOverlayColor;
                overlayImage.raycastTarget = collapsed
                    ? collapsedOverlayRaycastTarget
                    : expandedOverlayRaycastTarget;
            }
        }
    }
}
