using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class InformationValueCell : MonoBehaviour
    {
        [SerializeField] private InfluenceMarker2DView marker;
        [SerializeField] private Text label;
        public Text Label => label;
        public void Render(InfluenceMarker2DColor color, string value)
        { marker.SetAppearance(color, false, 0); label.text = value; }
        public bool IsConfigured => marker != null && label != null;
    }
}
