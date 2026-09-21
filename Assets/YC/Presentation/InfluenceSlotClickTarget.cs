using UnityEngine;

namespace YC.Presentation
{
    public sealed class InfluenceSlotClickTarget : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler
    {
        private MobileCityInteractionController controller;
        private string slotId = string.Empty;

        public bool Bind(MobileCityInteractionController owner, string influenceSlotId, out string reason)
        {
            if (owner == null || string.IsNullOrEmpty(influenceSlotId))
            {
                reason = "Influence slot click target requires a controller and slot id binding.";
                return false;
            }
            controller = owner;
            slotId = influenceSlotId;
            YC.PlayerJourney.PlayerAutomationId.Attach(gameObject, "map.influence_slot." + slotId);
            reason = string.Empty;
            return true;
        }

        private void OnMouseDown()
        {
            if (Camera.main != null && Camera.main.GetComponent<UnityEngine.EventSystems.Physics2DRaycaster>() != null) return;
            if (controller != null) controller.OnInfluenceSlotClicked(slotId);
        }
        public void OnPointerClick(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (eventData.button == UnityEngine.EventSystems.PointerEventData.InputButton.Left && controller != null)
                controller.OnInfluenceSlotClicked(slotId);
        }
    }
}
