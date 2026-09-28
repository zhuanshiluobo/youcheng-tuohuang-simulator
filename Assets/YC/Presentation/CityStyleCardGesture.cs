using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace YC.Presentation
{
    /// <summary>总览长按、右键或双击查看；放大卡面点按返回，拖动不触发点击。</summary>
    public sealed class CityStyleCardGesture : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IPointerClickHandler,
        IPointerExitHandler, IBeginDragHandler
    {
        [SerializeField] private float longPressSeconds = 0.5f;
        private Action open, back;
        private bool held, suppressClick;
        private float pressedAt;
        private Vector2 pressedPosition;
        private PointerEventData heldPointer;
        public bool CanOpen => open != null;

        public void Configure(Action openRequested, Action backRequested = null)
        {
            open = openRequested;
            back = backRequested;
            held = suppressClick = false;
        }

        public void OnPointerDown(PointerEventData data)
        {
            suppressClick = false;
            if (data != null) pressedPosition = data.position;
            held = data != null && data.button == PointerEventData.InputButton.Left && open != null;
            if (!held) return;
            pressedAt = Time.unscaledTime;
            heldPointer = data;
        }

        private void Update()
        {
            if (!held) return;
            var threshold = EventSystem.current != null ? EventSystem.current.pixelDragThreshold : 10;
            if ((heldPointer.position - pressedPosition).sqrMagnitude > threshold * threshold)
            { held = false; return; }
            if (Time.unscaledTime - pressedAt < longPressSeconds) return;
            held = false;
            suppressClick = true;
            open?.Invoke();
        }

        public void OnPointerUp(PointerEventData data)
        {
            held = false;
            var threshold = EventSystem.current != null ? EventSystem.current.pixelDragThreshold : 10;
            if (data != null && (data.position - pressedPosition).sqrMagnitude > threshold * threshold)
                suppressClick = true;
        }
        public void OnPointerExit(PointerEventData data) => held = false;
        public void OnBeginDrag(PointerEventData data) { held = false; suppressClick = true; }
        public void OnPointerClick(PointerEventData data)
        {
            if (data == null || suppressClick) return;
            if (data.button == PointerEventData.InputButton.Right) open?.Invoke();
            else if (data.button == PointerEventData.InputButton.Left)
            {
                if (back != null) back();
                else if (data.clickCount >= 2) open?.Invoke();
            }
        }

        private void OnDisable() { held = false; }
    }
}
