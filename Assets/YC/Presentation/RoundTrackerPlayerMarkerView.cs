using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class RoundTrackerPlayerMarkerView : MonoBehaviour
    {
        [SerializeField] private RectTransform markerTransform;
        [SerializeField] private Image markerImage;

        public RectTransform MarkerTransform => markerTransform;
        public Image MarkerImage => markerImage;

        public void Bind(string objectName, Color color, float x, float y)
        {
            gameObject.name = objectName;
            markerImage.color = color;
            markerTransform.anchoredPosition = new Vector2(x, y);
        }
    }
}
