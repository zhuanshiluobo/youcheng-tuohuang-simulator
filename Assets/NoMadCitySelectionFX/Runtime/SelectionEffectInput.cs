using UnityEngine;
using UnityEngine.EventSystems;

namespace NoMadCity.SelectionFX
{
    /// Put this on the existing raycastable candidate/Button, not the decorative child.
    /// This forwards presentation only; it never changes selection or confirms a choice.
    public sealed class SelectionEffectInput : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, ISelectHandler, IDeselectHandler
    {
        [SerializeField] private SelectionEffectGraphic effect;
        public void OnPointerEnter(PointerEventData e) { if (effect) effect.SetHovered(true); }
        public void OnPointerExit(PointerEventData e) { if (effect) effect.SetHovered(false); }
        public void OnSelect(BaseEventData e) { if (effect) effect.SetFocused(true); }
        public void OnDeselect(BaseEventData e) { if (effect) effect.SetFocused(false); }
        private void OnDisable() { if (effect) { effect.SetHovered(false); effect.SetFocused(false); } }
    }
}
