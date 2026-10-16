using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace YC.Presentation
{
    /// <summary>他人城市的整区返回表面；同次按下抬起且未拖动才返回。</summary>
    public sealed class CityBoardInspectionInput : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler
    {
        [SerializeField] private float dragThreshold = 6f;
        private Action returnToOwnCity;
        private Vector2 down;
        private int pointerId;
        private bool armed, dragged;
        private static int returnedFrame = -1;
        public static bool ReturnedThisFrame => returnedFrame == Time.frameCount;
        public void Configure(Action action) => returnToOwnCity = action;
        private void OnDisable() { armed = dragged = false; }
        public void OnPointerDown(PointerEventData data)
        {
            if (data.button != PointerEventData.InputButton.Left) return;
            down = data.position; pointerId = data.pointerId; armed = true; dragged = false;
            data.Use();
        }
        public void OnDrag(PointerEventData data)
        {
            if (armed && (data.position - down).sqrMagnitude > dragThreshold * dragThreshold) dragged = true;
            data.eligibleForClick = false;
        }
        public void OnPointerUp(PointerEventData data)
        {
            var hit = data.pointerCurrentRaycast.gameObject;
            var complete = armed && data.pointerId == pointerId && data.button == PointerEventData.InputButton.Left &&
                !dragged && (data.position - down).sqrMagnitude <= dragThreshold * dragThreshold &&
                hit != null && ExecuteEvents.GetEventHandler<IPointerDownHandler>(hit) == gameObject;
            armed = false;
            data.eligibleForClick = false;
            data.Use();
            if (!complete) return;
            returnedFrame = Time.frameCount;
            returnToOwnCity?.Invoke();
        }
    }
}
