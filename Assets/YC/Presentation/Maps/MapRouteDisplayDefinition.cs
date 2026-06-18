using System;
using System.Collections.Generic;
using UnityEngine;

namespace YC.Presentation.Maps
{
    [Serializable]
    public sealed class MapRouteDisplayDefinition
    {
        public string MapId = string.Empty;
        public string RouteId = string.Empty;
        public List<Vector2> NormalizedPoints = new List<Vector2>();
        public List<Vector2> InfluenceSlotPositions = new List<Vector2>();
        public Color NormalColor = new Color(0.9f, 0.78f, 0.36f, 0.75f);
        public Color CoveredColor = new Color(0.45f, 0.45f, 0.45f, 0.75f);
        public float Width = 0.035f;
    }
}
