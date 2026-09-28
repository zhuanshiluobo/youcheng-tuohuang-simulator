using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    [RequireComponent(typeof(Button))]
    public sealed class UiSharedButtonState : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler,
        ISelectHandler, IDeselectHandler
    {
        [SerializeField] private UiSharedComponentTheme theme;
        [SerializeField] private Image face;
        [SerializeField] private Image stateLayer;
        [SerializeField] private bool useSecondaryFace;
        [SerializeField] private bool selected;
        [SerializeField] private bool pending;

        private Button button;
        private bool hovered;
        private bool pressed;
        private bool focused;
        private bool interactableBeforePending;

        private void Awake()
        {
            button = GetComponent<Button>();
            if (stateLayer != null) stateLayer.raycastTarget = false;
            Refresh();
        }

        private void OnEnable() { Refresh(); }
        private void OnDisable() { hovered = pressed = focused = false; }
        private void LateUpdate() { Refresh(); }

        public void SetSelected(bool value) { selected = value; Refresh(); }
        public void SetPending(bool value)
        {
            if (button == null) button = GetComponent<Button>();
            if (pending == value) return;
            if (value)
            {
                interactableBeforePending = button.interactable;
                button.interactable = false;
            }
            else
            {
                button.interactable = interactableBeforePending;
            }
            pending = value;
            Refresh();
        }
        public void OnPointerEnter(PointerEventData eventData) { hovered = true; Refresh(); }
        public void OnPointerExit(PointerEventData eventData) { hovered = pressed = false; Refresh(); }
        public void OnPointerDown(PointerEventData eventData) { pressed = true; Refresh(); }
        public void OnPointerUp(PointerEventData eventData) { pressed = false; Refresh(); }
        public void OnSelect(BaseEventData eventData) { focused = true; Refresh(); }
        public void OnDeselect(BaseEventData eventData) { focused = false; Refresh(); }

        public void Refresh()
        {
            if (theme == null || face == null || stateLayer == null) return;
            if (button == null) button = GetComponent<Button>();
            var disabled = button == null || !button.interactable;
            face.sprite = disabled ? theme.disabledButton :
                useSecondaryFace ? theme.secondaryButton : theme.primaryButton;
            face.type = Image.Type.Sliced;
            // The logical Button rectangle is the hit target. Both art layers stay decorative.
            face.raycastTarget = false;
            stateLayer.raycastTarget = false;
            stateLayer.color = pending ? theme.pendingTint :
                disabled ? Color.clear : pressed ? theme.pressedTint :
                selected ? theme.selectedTint : focused ? theme.focusedTint :
                hovered ? theme.hoverTint : Color.clear;
        }
    }
}
