using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class GameBoxTokenItemView : MonoBehaviour
    {
        [SerializeField] private Image artwork;
        [SerializeField] private Text count;
        [SerializeField] private Text missingArtwork;
        [SerializeField] private string countFormat = "×{0}";
        public Sprite Artwork => artwork.sprite;
        public void Render(Sprite sprite, int quantity)
        {
            artwork.sprite = sprite; artwork.enabled = sprite != null; artwork.preserveAspect = true;
            missingArtwork.gameObject.SetActive(sprite == null);
            count.text = string.Format(countFormat,quantity);
        }
    }
}
