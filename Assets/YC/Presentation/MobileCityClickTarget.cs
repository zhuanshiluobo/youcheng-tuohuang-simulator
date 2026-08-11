using UnityEngine;

namespace YC.Presentation
{
    public sealed class MobileCityClickTarget : MonoBehaviour
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
            if (controller != null) controller.OnMobileCityClicked();
        }
    }
}
