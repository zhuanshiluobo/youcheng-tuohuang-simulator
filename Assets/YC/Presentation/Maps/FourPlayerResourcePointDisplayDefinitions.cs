using System.Collections.Generic;
using UnityEngine;
using YC.Domain.Maps;

namespace YC.Presentation.Maps
{
    public static class FourPlayerResourcePointDisplayDefinitions
    {
        public static IReadOnlyList<MapResourcePointDisplayDefinition> Create()
        {
            return new List<MapResourcePointDisplayDefinition>
            {
                Point("A-01", 0.793f, 0.215f, Slots(Slot(0.800f, 0.251f), Slot(0.800f, 0.272f))),
                Point("A-02", 0.786f, 0.415f, Slots(Slot(0.793f, 0.451f), Slot(0.793f, 0.472f))),
                Point("A-03", 0.645f, 0.443f, Slots(Slot(0.652f, 0.479f), Slot(0.652f, 0.500f))),
                Point("B-01", 0.860f, 0.614f, Slots(Slot(0.867f, 0.650f), Slot(0.867f, 0.671f))),
                Point("B-02", 0.756f, 0.713f, Slots(Slot(0.763f, 0.749f), Slot(0.763f, 0.770f))),
                Point("B-03", 0.609f, 0.646f, Slots(Slot(0.616f, 0.682f), Slot(0.616f, 0.703f))),
                Point("C-01", 0.755f, 0.866f, Slots(Slot(0.762f, 0.902f), Slot(0.762f, 0.923f))),
                Point("C-02", 0.396f, 0.794f, Slots(Slot(0.403f, 0.830f), Slot(0.403f, 0.851f))),
                Point("C-03", 0.089f, 0.753f, Slots(Slot(0.096f, 0.789f), Slot(0.096f, 0.810f))),
                Point("D-01", 0.645f, 0.314f, Slots(Slot(0.652f, 0.350f), Slot(0.652f, 0.371f))),
                Point("D-02", 0.503f, 0.265f, Slots(Slot(0.510f, 0.301f), Slot(0.510f, 0.322f))),
                Point("D-03", 0.521f, 0.458f, Slots(Slot(0.528f, 0.494f), Slot(0.528f, 0.515f))),
                Point("E-01", 0.508f, 0.713f, Slots(Slot(0.515f, 0.749f), Slot(0.515f, 0.770f))),
                Point("E-02", 0.292f, 0.652f, Slots(Slot(0.299f, 0.688f), Slot(0.299f, 0.709f))),
                Point("E-03", 0.145f, 0.562f, Slots(Slot(0.152f, 0.598f), Slot(0.152f, 0.619f))),
                Point("F-01", 0.283f, 0.311f, Slots(Slot(0.290f, 0.347f), Slot(0.290f, 0.368f))),
                Point("F-02", 0.343f, 0.496f, Slots(Slot(0.350f, 0.532f), Slot(0.350f, 0.553f))),
                Point("F-03", 0.132f, 0.369f, Slots(Slot(0.139f, 0.405f), Slot(0.139f, 0.426f))),
                Point("G-01", 0.685f, 0.112f, Slots(Slot(0.692f, 0.148f), Slot(0.692f, 0.169f))),
                Point("G-02", 0.436f, 0.134f, Slots(Slot(0.443f, 0.170f), Slot(0.443f, 0.191f))),
                Point("G-03", 0.096f, 0.087f, Slots(Slot(0.103f, 0.123f), Slot(0.103f, 0.144f))),
                Point("G-04", 0.268f, 0.198f, Slots(Slot(0.275f, 0.234f), Slot(0.275f, 0.255f)))
            };
        }

        private static MapResourcePointDisplayDefinition Point(
            string locationId,
            float x,
            float y,
            List<MapInfluenceSlotDisplayDefinition> influenceSlots)
        {
            return new MapResourcePointDisplayDefinition
            {
                MapId = StaticMapDefinitions.FourPlayerMapId,
                LocationId = locationId,
                ResourceTokenPosition = new Vector2(x, y),
                InfluenceSlots = influenceSlots
            };
        }

        private static List<MapInfluenceSlotDisplayDefinition> Slots(params MapInfluenceSlotDisplayDefinition[] slots)
        {
            return new List<MapInfluenceSlotDisplayDefinition>(slots);
        }

        private static MapInfluenceSlotDisplayDefinition Slot(float x, float y, float size = 1f, float colliderRadius = 0.22f)
        {
            return new MapInfluenceSlotDisplayDefinition
            {
                NormalizedPosition = new Vector2(x, y),
                Size = size,
                ColliderRadius = colliderRadius
            };
        }
    }
}
