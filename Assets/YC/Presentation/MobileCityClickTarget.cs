using UnityEngine;

namespace YC.Presentation
{
    public sealed class MobileCityClickTarget : MonoBehaviour,
        UnityEngine.EventSystems.IPointerClickHandler
    {
        private MobileCityInteractionController controller;

        public bool Bind(MobileCityInteractionController owner, out string reason)
        {
            if (owner == null)
            {
                reason = "Mobile city click target requires a controller binding.";
                return false;
            }
            controller = owner;
            reason = string.Empty;
            return true;
        }

        private void OnMouseDown()
        {
            if (Camera.main != null &&
                Camera.main.GetComponent<UnityEngine.EventSystems.Physics2DRaycaster>() != null) return;
            if (controller != null && TabletopPointerClassifier.CanRouteMapPointer(Input.mousePosition))
                controller.OnMobileCityClicked();
        }

        public void OnPointerClick(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (eventData != null &&
                eventData.button == UnityEngine.EventSystems.PointerEventData.InputButton.Left &&
                controller != null && TabletopPointerClassifier.CanRouteMapPointer(eventData.position))
                controller.OnMobileCityClicked();
        }
    }
}
