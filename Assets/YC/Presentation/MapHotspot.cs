using UnityEngine;

namespace YC.Presentation
{
    public sealed class MapHotspot : MonoBehaviour
    {
        public const float HighlightScaleMultiplier = 2f;
        public const float HighlightPulseDuration = MapHighlightPulse.PulseDuration * 2f;

        [SerializeField] private SpriteRenderer spriteRenderer;
        [SerializeField] private MapHighlightPulse highlightPulse;
        [SerializeField] private MapPlacementFeedback placementFeedback;
        private MobileCityInteractionController controller;
        private Vector3 restingScale = Vector3.one;

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
            restingScale = spriteRenderer.transform.localScale;
            if (!highlightPulse.BindWithDuration(spriteRenderer, HighlightPulseDuration, out reason) ||
                !placementFeedback.Bind(out reason))
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
            spriteRenderer.transform.localScale = restingScale;
            spriteRenderer.color = color;
            spriteRenderer.enabled = color.a > 0f;
        }

        public void SetHighlighted(bool highlighted)
        {
            if (spriteRenderer != null)
            {
                spriteRenderer.transform.localScale = highlighted
                    ? restingScale * HighlightScaleMultiplier
                    : restingScale;
            }
            highlightPulse?.SetHighlighted(highlighted);
        }
        public void PlayPlacementFeedback() => placementFeedback?.Play();

        private void OnMouseDown()
        {
            if (controller != null) controller.OnHotspotClicked(LocationId);
        }
    }
}
