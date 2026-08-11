using UnityEngine;

namespace YC.Presentation
{
    public sealed class InfluenceSlotClickTarget : MonoBehaviour
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
            reason = string.Empty;
            return true;
        }

        private void OnMouseDown()
        {
            if (controller != null) controller.OnInfluenceSlotClicked(slotId);
        }
    }
}
