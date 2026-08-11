using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace YC.Presentation
{
    public sealed class CityStyleDeclarationSlotPointerHandler : MonoBehaviour,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerEnterHandler,
        IPointerClickHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        private int slotIndex;
        private Func<bool> isLeftPointerHeld;
        private Action<int, Vector2> leftPointerDown;
        private Action<int, Vector2> leftPointerEnter;
        private Action<Vector2> leftPointerDrag;
        private Action leftPointerUp;
        private Action<int> leftClick;
        private bool draggedDuringCurrentPress;

        public void Configure(
            int configuredSlotIndex,
            Func<bool> configuredIsLeftPointerHeld,
            Action<int, Vector2> configuredLeftPointerDown,
            Action<int, Vector2> configuredLeftPointerEnter,
            Action<Vector2> configuredLeftPointerDrag,
            Action configuredLeftPointerUp,
            Action<int> configuredLeftClick)
        {
            slotIndex = configuredSlotIndex;
            isLeftPointerHeld = configuredIsLeftPointerHeld;
            leftPointerDown = configuredLeftPointerDown;
            leftPointerEnter = configuredLeftPointerEnter;
            leftPointerDrag = configuredLeftPointerDrag;
            leftPointerUp = configuredLeftPointerUp;
            leftClick = configuredLeftClick;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                draggedDuringCurrentPress = false;
                leftPointerDown?.Invoke(slotIndex, eventData.position);
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                leftPointerUp?.Invoke();
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (eventData != null && isLeftPointerHeld != null && isLeftPointerHeld())
            {
                leftPointerEnter?.Invoke(slotIndex, eventData.position);
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData == null)
            {
                return;
            }

            if (eventData.button == PointerEventData.InputButton.Left &&
                !draggedDuringCurrentPress && !eventData.dragging)
            {
                leftClick?.Invoke(slotIndex);
            }
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                draggedDuringCurrentPress = true;
                leftPointerDrag?.Invoke(eventData.position);
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                draggedDuringCurrentPress = true;
                leftPointerDrag?.Invoke(eventData.position);
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                leftPointerUp?.Invoke();
            }
        }
    }
}
