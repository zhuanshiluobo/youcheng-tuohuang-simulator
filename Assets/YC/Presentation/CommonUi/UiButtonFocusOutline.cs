using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class UiButtonFocusOutline : MonoBehaviour, ISelectHandler, IDeselectHandler
    {
        [SerializeField] private Outline outline;
        private void OnEnable() { if (outline != null) outline.enabled = false; }
        private void OnDisable() { if (outline != null) outline.enabled = false; }
        public void OnSelect(BaseEventData eventData) { if (outline != null) outline.enabled = true; }
        public void OnDeselect(BaseEventData eventData) { if (outline != null) outline.enabled = false; }
    }
}
