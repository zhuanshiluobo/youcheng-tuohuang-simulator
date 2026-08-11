using System;
using System.Collections.Generic;

namespace YC.Presentation.Maps
{
    [Serializable]
    public sealed class MapRouteDisplayDefinition
    {
        public string MapId = string.Empty;
        public string RouteId = string.Empty;
        public List<MapInfluenceSlotDisplayDefinition> InfluenceSlots = new List<MapInfluenceSlotDisplayDefinition>();
    }
}
