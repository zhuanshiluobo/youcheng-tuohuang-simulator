using UnityEngine;

namespace YC.Presentation
{
    public sealed class MapHotspot : MonoBehaviour
    {
        private SpriteRenderer spriteRenderer;
        private MobileCityInteractionController controller;
        private MapHighlightPulse highlightPulse;
        private MapPlacementFeedback placementFeedback;

        public string LocationId { get; private set; }

        public void Initialize(MobileCityInteractionController owner, string locationId)
        {
            controller = owner;
            LocationId = locationId;
            spriteRenderer = GetComponent<SpriteRenderer>();
            highlightPulse = GetComponent<MapHighlightPulse>();
            if (highlightPulse == null)
            {
                highlightPulse = gameObject.AddComponent<MapHighlightPulse>();
            }
            highlightPulse.Configure(spriteRenderer);
            placementFeedback = GetComponent<MapPlacementFeedback>();
            if (placementFeedback == null)
            {
                placementFeedback = gameObject.AddComponent<MapPlacementFeedback>();
            }
            placementFeedback.Configure(spriteRenderer == null ? 11 : spriteRenderer.sortingOrder + 1);
        }

        public void SetColor(Color color)
        {
            if (spriteRenderer != null)
            {
                highlightPulse?.SetHighlighted(false);
                spriteRenderer.color = color;
                spriteRenderer.enabled = color.a > 0f;
            }
        }

        public void SetHighlighted(bool highlighted)
        {
            highlightPulse?.SetHighlighted(highlighted);
        }

        public void PlayPlacementFeedback()
        {
            placementFeedback?.Play();
        }

        private void OnMouseDown()
        {
            if (controller != null)
            {
                controller.OnHotspotClicked(LocationId);
            }
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
