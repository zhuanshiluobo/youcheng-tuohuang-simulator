using System.Collections.Generic;
using UnityEngine;
using YC.Domain.Maps;

namespace YC.Presentation.Maps
{
    public static class FourPlayerRouteDisplayDefinitions
    {
        public static IReadOnlyList<MapRouteDisplayDefinition> Create()
        {
            return new List<MapRouteDisplayDefinition>
            {
                Route("A1", Points("A-01", "A-02"), Slots(
                    Point(0.816f, 0.358f))),

                Route("A2", Points("A-02", "A-03", "B-03"), Slots(
                    Point(0.697f, 0.587f),
                    Point(0.717f, 0.587f))),

                Route("B1", Points("A-02", "B-02"), Slots(
                    Point(0.776f, 0.628f))),

                Route("B2", Points("B-01", "B-02", "C-01"), Slots(
                    Point(0.884f, 0.834f),
                    Point(0.904f, 0.834f))),

                Route("C1", Points("C-02", "C-03"), Slots(
                    Point(0.300f, 0.820f))),

                Route("C2", Points("B-02", "B-03", "C-01", "C-02"), Slots(
                    Point(0.662f, 0.823f),
                    Point(0.681f, 0.823f))),

                Route("D1", Points("A-03", "D-01", "D-02", "D-03"), Slots(
                    Point(0.578f, 0.387f),
                    Point(0.597f, 0.387f))),

                Route("D2", Points("D-02", "D-03", "F-02"), Slots(
                    Point(0.455f, 0.434f),
                    Point(0.474f, 0.434f))),

                Route("E1", Points("E-01", "E-02", "F-02"), Slots(
                    Point(0.454f, 0.666f),
                    Point(0.471f, 0.666f))),

                Route("E2", Points("C-03", "E-02", "E-03"), Slots(
                    Point(0.196f, 0.690f),
                    Point(0.213f, 0.690f))),

                Route("F1", Points("D-02", "F-01"), Slots(
                    Point(0.440f, 0.303f))),

                Route("F2", Points("F-01", "F-02"), Slots(
                    Point(0.324f, 0.439f))),

                Route("F3", Points("F-01", "F-03", "G-04"), Slots(
                    Point(0.184f, 0.312f),
                    Point(0.202f, 0.312f))),

                Route("G1", Points("G-02", "G-03"), Slots(
                    Point(0.320f, 0.100f))),

                Route("G2", Points("G-03", "G-04"), Slots(
                    Point(0.180f, 0.244f))),

                Route("R1", Points("A-01", "D-01", "G-01", "G-02"), Slots(
                    Point(0.685f, 0.241f),
                    Point(0.702f, 0.241f))),

                Route("R2", Points("A-01", "B-01", "G-01"), Slots(
                    Point(0.913f, 0.354f),
                    Point(0.932f, 0.354f))),

                Route("R3", Points("C-03", "G-03"), Slots(
                    Point(0.090f, 0.443f))),

                Route("R4", Points("E-03", "F-03"), Slots(
                    Point(0.181f, 0.497f))),

                Route("R5", Points("E-03", "F-02"), Slots(
                    Point(0.286f, 0.539f))),

                Route("R6", Points("A-02", "B-01"), Slots(
                    Point(0.872f, 0.546f))),

                Route("R7", Points("B-03", "D-03", "E-01"), Slots(
                    Point(0.567f, 0.597f),
                    Point(0.585f, 0.597f)))
            };
        }

        private static MapRouteDisplayDefinition Route(
            string routeId,
            List<Vector2> normalizedPoints,
            List<Vector2> influenceSlotPositions)
        {
            return new MapRouteDisplayDefinition
            {
                MapId = StaticMapDefinitions.FourPlayerMapId,
                RouteId = routeId,
                NormalizedPoints = normalizedPoints,
                InfluenceSlotPositions = influenceSlotPositions
            };
        }

        private static List<Vector2> Points(params string[] locationIds)
        {
            var points = new List<Vector2>(locationIds.Length);
            for (var i = 0; i < locationIds.Length; i++)
            {
                points.Add(LocationPoint(locationIds[i]));
            }

            return points;
        }

        private static List<Vector2> Slots(params Vector2[] points)
        {
            return new List<Vector2>(points);
        }

        private static Vector2 Point(float x, float y)
        {
            return new Vector2(x, y);
        }

        private static Vector2 LocationPoint(string locationId)
        {
            switch (locationId)
            {
                case "A-01": return Point(0.838f, 0.248f);
                case "A-02": return Point(0.831f, 0.449f);
                case "A-03": return Point(0.689f, 0.477f);
                case "B-01": return Point(0.904f, 0.643f);
                case "B-02": return Point(0.799f, 0.746f);
                case "B-03": return Point(0.655f, 0.682f);
                case "C-01": return Point(0.801f, 0.893f);
                case "C-02": return Point(0.444f, 0.829f);
                case "C-03": return Point(0.134f, 0.780f);
                case "D-01": return Point(0.692f, 0.347f);
                case "D-02": return Point(0.549f, 0.296f);
                case "D-03": return Point(0.564f, 0.489f);
                case "E-01": return Point(0.553f, 0.751f);
                case "E-02": return Point(0.335f, 0.684f);
                case "E-03": return Point(0.190f, 0.587f);
                case "F-01": return Point(0.329f, 0.349f);
                case "F-02": return Point(0.387f, 0.523f);
                case "F-03": return Point(0.176f, 0.400f);
                case "G-01": return Point(0.734f, 0.145f);
                case "G-02": return Point(0.482f, 0.162f);
                case "G-03": return Point(0.139f, 0.115f);
                case "G-04": return Point(0.310f, 0.232f);
                default: return Vector2.zero;
            }
        }
    }
}
