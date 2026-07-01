using UnityEngine;

namespace YC.Presentation
{
    public sealed class MapHotspot : MonoBehaviour
    {
        private SpriteRenderer spriteRenderer;
        private MobileCityInteractionController controller;

        public string LocationId { get; private set; }

        public void Initialize(MobileCityInteractionController owner, string locationId)
        {
            controller = owner;
            LocationId = locationId;
            spriteRenderer = GetComponent<SpriteRenderer>();
        }

        public void SetColor(Color color)
        {
            if (spriteRenderer != null)
            {
                spriteRenderer.color = color;
            }
        }

        private void OnMouseDown()
        {
            controller.OnHotspotClicked(LocationId);
        }
    }

    public sealed class MobileCityClickTarget : MonoBehaviour
    {
        private MobileCityInteractionController controller;

        public void Initialize(MobileCityInteractionController owner)
        {
            controller = owner;
        }

        private void OnMouseDown()
        {
            controller.OnMobileCityClicked();
        }
    }

    public sealed class InfluenceSlotClickTarget : MonoBehaviour
    {
        private MobileCityInteractionController controller;
        private string slotId = string.Empty;

        public void Initialize(MobileCityInteractionController owner, string influenceSlotId)
        {
            controller = owner;
            slotId = influenceSlotId ?? string.Empty;
        }

        private void OnMouseDown()
        {
            controller.OnInfluenceSlotClicked(slotId);
        }
    }
}
