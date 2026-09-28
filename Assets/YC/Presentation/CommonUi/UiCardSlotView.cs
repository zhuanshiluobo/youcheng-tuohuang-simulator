using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class UiCardSlotView : MonoBehaviour
    {
        [SerializeField] private Image artwork;
        [SerializeField] private Image selectedFrame;

        public void SetArtwork(Sprite sprite)
        {
            if (artwork == null) return;
            artwork.sprite = sprite;
            artwork.enabled = sprite != null;
            artwork.preserveAspect = true;
            artwork.raycastTarget = false;
        }

        public void SetSelected(bool selected)
        {
            if (selectedFrame != null) selectedFrame.enabled = selected;
        }
    }
}
