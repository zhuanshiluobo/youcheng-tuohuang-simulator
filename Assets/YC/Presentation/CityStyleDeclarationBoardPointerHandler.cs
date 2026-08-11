using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace YC.Presentation
{
    public sealed class CityStyleDeclarationBoardPointerHandler : MonoBehaviour,
        IPointerDownHandler,
        IPointerUpHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        private Action<Vector2> leftPointerDown;
        private Action<Vector2> leftPointerDrag;
        private Action leftPointerUp;

        public void Configure(
            Action<Vector2> configuredLeftPointerDown,
            Action<Vector2> configuredLeftPointerDrag,
            Action configuredLeftPointerUp)
        {
            leftPointerDown = configuredLeftPointerDown;
            leftPointerDrag = configuredLeftPointerDrag;
            leftPointerUp = configuredLeftPointerUp;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                leftPointerDown?.Invoke(eventData.position);
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                leftPointerUp?.Invoke();
            }
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                leftPointerDrag?.Invoke(eventData.position);
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
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
