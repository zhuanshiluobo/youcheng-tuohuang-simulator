using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>单击选牌；长按、双击或右键查看。拖动交给所在的滚动列表。</summary>
    public sealed class CardPickerCardButton : Button,
        IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        [SerializeField] private float longPressSeconds = .5f;
        [SerializeField] private float doubleClickSeconds = .3f;
        private Action preview;
        private Func<bool> canPreview;
        private PointerEventData heldPointer;
        private Vector2 pressedPosition;
        private float pressedAt, selectAt;
        private bool held, suppressClick, pendingSelect, secondPress;
        private ScrollRect scroll;

        public void ConfigurePreview(Action open, Func<bool> isCurrent)
        {
            preview = open;
            canPreview = isCurrent;
            ResetGesture();
        }

        public override void OnPointerDown(PointerEventData data)
        {
            base.OnPointerDown(data);
            if (data == null) return;
            secondPress = pendingSelect && Time.unscaledTime <= selectAt;
            pendingSelect = false;
            suppressClick = false;
            pressedPosition = data.position;
            pressedAt = Time.unscaledTime;
            heldPointer = data;
            held = data.button == PointerEventData.InputButton.Left;
        }

        public override void OnPointerUp(PointerEventData data)
        {
            base.OnPointerUp(data);
            held = false;
            if (Moved(data)) suppressClick = true;
        }

        public override void OnPointerExit(PointerEventData data)
        {
            base.OnPointerExit(data);
            held = false;
        }

        public override void OnPointerClick(PointerEventData data)
        {
            if (data == null || !IsActive() || suppressClick) return;
            if (data.button == PointerEventData.InputButton.Right ||
                (data.button == PointerEventData.InputButton.Left && (secondPress || data.clickCount >= 2)))
            {
                OpenPreview();
                return;
            }
            if (data.button != PointerEventData.InputButton.Left || !IsInteractable()) return;
            // 等待双击窗口，避免第一次点击重建选择页后丢失第二次点击。
            pendingSelect = true;
            selectAt = Time.unscaledTime + doubleClickSeconds;
        }

        private void Update()
        {
            if (held && Moved(heldPointer)) { held = false; suppressClick = true; }
            if (held && Time.unscaledTime - pressedAt >= longPressSeconds) OpenPreview();
            if (!pendingSelect || held || Time.unscaledTime < selectAt) return;
            pendingSelect = false;
            if (IsActive() && IsInteractable()) onClick.Invoke();
        }

        private void OpenPreview()
        {
            held = pendingSelect = false;
            suppressClick = true;
            if (IsActive() && preview != null && (canPreview == null || canPreview())) preview();
        }

        private bool Moved(PointerEventData data)
        {
            var threshold = EventSystem.current == null ? 10 : EventSystem.current.pixelDragThreshold;
            return data != null && (data.position - pressedPosition).sqrMagnitude > threshold * threshold;
        }

        public void OnInitializePotentialDrag(PointerEventData data)
        {
            scroll = GetComponentInParent<ScrollRect>();
            if (scroll != null) scroll.OnInitializePotentialDrag(data);
        }

        public void OnBeginDrag(PointerEventData data)
        {
            held = pendingSelect = false;
            suppressClick = true;
            if (scroll != null) scroll.OnBeginDrag(data);
        }

        public void OnDrag(PointerEventData data)
        {
            if (scroll != null) scroll.OnDrag(data);
        }

        public void OnEndDrag(PointerEventData data)
        {
            if (scroll != null) scroll.OnEndDrag(data);
        }

        protected override void OnDisable()
        {
            ResetGesture();
            base.OnDisable();
        }

        private void ResetGesture()
        {
            held = pendingSelect = secondPress = suppressClick = false;
            heldPointer = null;
        }
    }
}
