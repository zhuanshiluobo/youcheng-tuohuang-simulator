using UnityEngine;
using UnityEngine.EventSystems;

namespace YC.Presentation
{
    /// <summary>效果对话框共用的标题栏拖动手柄。</summary>
    public sealed class EffectDialogDragHandle : MonoBehaviour,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        private RectTransform panel;
        private RectTransform parent;
        private Vector2 pointerOffset;
        private bool dragging;

        public void Configure(RectTransform configuredPanel)
        {
            panel = configuredPanel;
            parent = panel == null ? null : panel.parent as RectTransform;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            dragging = eventData != null &&
                       eventData.button == PointerEventData.InputButton.Left &&
                       panel != null &&
                       parent != null;
            if (!dragging)
            {
                return;
            }

            Vector2 localPoint;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    parent,
                    eventData.position,
                    eventData.pressEventCamera,
                    out localPoint))
            {
                dragging = false;
                return;
            }

            pointerOffset = panel.anchoredPosition - localPoint;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!dragging || eventData == null || panel == null || parent == null)
            {
                return;
            }

            Vector2 localPoint;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    parent,
                    eventData.position,
                    eventData.pressEventCamera,
                    out localPoint))
            {
                return;
            }

            panel.anchoredPosition = ClampToParent(localPoint + pointerOffset);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            dragging = false;
        }

        private void OnDisable()
        {
            dragging = false;
        }

        private Vector2 ClampToParent(Vector2 position)
        {
            var horizontalLimit = Mathf.Max(0f, (parent.rect.width - panel.rect.width) * 0.5f);
            var verticalLimit = Mathf.Max(0f, (parent.rect.height - panel.rect.height) * 0.5f);
            return new Vector2(
                Mathf.Clamp(position.x, -horizontalLimit, horizontalLimit),
                Mathf.Clamp(position.y, -verticalLimit, verticalLimit));
        }
    }
}
