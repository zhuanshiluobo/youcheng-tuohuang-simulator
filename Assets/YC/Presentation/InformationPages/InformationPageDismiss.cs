using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace YC.Presentation
{
    /// <summary>必须在本页完成同一指针的完整点击；拖动与子内容点击不关闭。</summary>
    public sealed class InformationPageDismiss : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler
    {
        [SerializeField] private float dragThreshold = 6;
        private int pointerId;
        private Vector2 down;
        private bool armed, dragged;
        public Action Dismiss;
        private void OnEnable() { armed = false; dragged = false; }
        public void OnPointerDown(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left) return;
            pointerId = e.pointerId; down = e.position; armed = true; dragged = false;
        }
        public void OnDrag(PointerEventData e)
        { if (armed && (e.position - down).sqrMagnitude > dragThreshold * dragThreshold) dragged = true; }
        public void OnPointerUp(PointerEventData e)
        {
            var hit = e.pointerCurrentRaycast.gameObject;
            var inside = hit != null && ExecuteEvents.GetEventHandler<IPointerDownHandler>(hit) == gameObject;
            var click = armed && e.pointerId == pointerId && e.button == PointerEventData.InputButton.Left &&
                !dragged && (e.position - down).sqrMagnitude <= dragThreshold * dragThreshold && inside;
            armed = false;
            if (click) Dismiss?.Invoke();
        }
    }
}
