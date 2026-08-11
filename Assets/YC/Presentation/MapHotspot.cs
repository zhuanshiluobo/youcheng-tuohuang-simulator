using UnityEngine;

namespace YC.Presentation
{
    public sealed class MapHotspot : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer spriteRenderer;
        [SerializeField] private MapHighlightPulse highlightPulse;
        [SerializeField] private MapPlacementFeedback placementFeedback;
        private MobileCityInteractionController controller;

        public string LocationId { get; private set; }
        public SpriteRenderer Renderer => spriteRenderer;

        public bool Bind(MobileCityInteractionController owner, string locationId, out string reason)
        {
            if (owner == null || string.IsNullOrEmpty(locationId) || spriteRenderer == null ||
                highlightPulse == null || placementFeedback == null)
            {
                reason = "Map hotspot has incomplete fixed bindings.";
                return false;
            }
            controller = owner;
            LocationId = locationId;
            if (!highlightPulse.Bind(spriteRenderer, out reason) || !placementFeedback.Bind(out reason))
            {
                return false;
            }
            reason = string.Empty;
            return true;
        }

        public void SetColor(Color color)
        {
            if (spriteRenderer == null) return;
            highlightPulse?.SetHighlighted(false);
            spriteRenderer.color = color;
            spriteRenderer.enabled = color.a > 0f;
        }

        public void SetHighlighted(bool highlighted) => highlightPulse?.SetHighlighted(highlighted);
        public void PlayPlacementFeedback() => placementFeedback?.Play();

        private void OnMouseDown()
        {
            if (controller != null) controller.OnHotspotClicked(LocationId);
        }
    }
}
