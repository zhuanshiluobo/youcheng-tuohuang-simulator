using UnityEngine;

namespace YC.Presentation
{
    public sealed class CityStyleSpecialActionDropTarget : MonoBehaviour
    {
        public string MarkerArea { get; private set; } = string.Empty;

        public void Configure(string markerArea)
        {
            MarkerArea = markerArea ?? string.Empty;
        }
    }
}
