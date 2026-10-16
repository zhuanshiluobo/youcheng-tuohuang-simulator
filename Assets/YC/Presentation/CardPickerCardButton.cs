using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>单击立即选牌；长按或右键查看。拖动交给所在的滚动列表。</summary>
    public sealed class CardPickerCardButton : Button,
        IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        [SerializeField] private float longPressSeconds = .5f;
        private Action preview;
        private Func<bool> canPreview;
        private PointerEventData heldPointer;
        private Vector2 pressedPosition;
        private float pressedAt;
        private bool held, suppressClick;
        private bool previewOnSingleClick;
        private ScrollRect scroll;

        public void ConfigurePreview(Action open, Func<bool> isCurrent)
        {
            preview = open;
            canPreview = isCurrent;
            previewOnSingleClick = false;
            ResetGesture();
        }

        public void ConfigureSingleClickPreview(Action open, Func<bool> isCurrent)
        {
            ConfigurePreview(open, isCurrent);
            previewOnSingleClick = true;
        }

        public override void OnPointerDown(PointerEventData data)
        {
            base.OnPointerDown(data);
            if (data == null) return;
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
            if (data.button == PointerEventData.InputButton.Right)
            {
                OpenPreview();
                return;
            }
            if (data.button != PointerEventData.InputButton.Left) return;
            if (previewOnSingleClick)
            {
                OpenPreview();
                return;
            }
            if (!IsInteractable()) return;
            onClick.Invoke();
        }

        private void Update()
        {
            if (held && Moved(heldPointer)) { held = false; suppressClick = true; }
            if (held && Time.unscaledTime - pressedAt >= longPressSeconds) OpenPreview();
        }

        private void OpenPreview()
        {
            held = false;
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
            held = false;
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
            held = suppressClick = false;
            heldPointer = null;
        }
    }
}
