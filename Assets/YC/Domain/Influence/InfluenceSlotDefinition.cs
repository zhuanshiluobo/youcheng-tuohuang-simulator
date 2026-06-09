using System;

namespace YC.Domain.Influence
{
    [Serializable]
    public sealed class InfluenceSlotDefinition
    {
        public string SlotId = string.Empty;
        public string LocationId = string.Empty;
        public string RouteId = string.Empty;
        public int Index;

        public bool IsLocationSlot
        {
            get { return !string.IsNullOrEmpty(LocationId); }
        }

        public bool IsRouteSlot
        {
            get { return !string.IsNullOrEmpty(RouteId); }
        }
    }
}
