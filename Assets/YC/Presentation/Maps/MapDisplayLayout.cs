using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Domain.Maps;
using YC.Domain.Scoring;

namespace YC.Presentation.Maps
{
    [CreateAssetMenu(fileName = "MapDisplayLayout", menuName = "YC/Maps/Map Display Layout")]
    public sealed class MapDisplayLayout : ScriptableObject
    {
        public string MapId = string.Empty;
        public Sprite MapSprite;
        public List<MapLocationLayoutDefinition> Locations = new List<MapLocationLayoutDefinition>();
        public List<MapRouteLayoutDefinition> Routes = new List<MapRouteLayoutDefinition>();
        [SerializeField] private string spatialLayoutManifestSha256 = string.Empty;
        [SerializeField] private List<MapScoreTrackSegmentDefinition> scoreTrackSegments =
            new List<MapScoreTrackSegmentDefinition>();
        [SerializeField] private List<MapScoreMarkerOffsetDefinition> scoreMarkerOffsets =
            new List<MapScoreMarkerOffsetDefinition>();

        public string SpatialLayoutManifestSha256 => spatialLayoutManifestSha256;
        public IReadOnlyList<MapScoreTrackSegmentDefinition> ScoreTrackSegments => scoreTrackSegments;
        public IReadOnlyList<MapScoreMarkerOffsetDefinition> ScoreMarkerOffsets => scoreMarkerOffsets;

        public Vector2 GetScoreTrackNormalizedPosition(int score)
        {
            EnsureValidScoreTrack();
            var clampedScore = ScoreTrackService.ClampToTrack(score);
            for (var i = 0; i < scoreTrackSegments.Count; i++)
            {
                var segment = scoreTrackSegments[i];
                if (clampedScore < segment.MinimumScore || clampedScore > segment.MaximumScore)
                {
                    continue;
                }

                var t = (float)(clampedScore - segment.MinimumScore) /
                        (segment.MaximumScore - segment.MinimumScore);
                return Vector2.Lerp(segment.Start, segment.End, t);
            }

            throw new InvalidOperationException(
                "MapDisplayLayout 计分轨迹没有覆盖分数 " + clampedScore + "。");
        }

        public Vector2 GetScoreMarkerOffset(int markerIndex, int markerCount)
        {
            EnsureValidScoreTrack();
            var requiredCount = Mathf.Clamp(markerCount, 1, scoreMarkerOffsets.Count);
            for (var i = 0; i < scoreMarkerOffsets.Count; i++)
            {
                var definition = scoreMarkerOffsets[i];
                if (definition.MarkerCount != requiredCount)
                {
                    continue;
                }

                return definition.Offsets[
                    Mathf.Clamp(markerIndex, 0, definition.Offsets.Count - 1)];
            }

            throw new InvalidOperationException(
                "MapDisplayLayout 缺少 " + requiredCount + " 人同分偏移布局。");
        }

        public bool TryValidateScoreTrack(out string reason)
        {
            if (!IsSha256(spatialLayoutManifestSha256))
            {
                reason = "MapDisplayLayout 缺少有效空间布局 manifest SHA-256。";
                return false;
            }

            if (scoreTrackSegments == null || scoreTrackSegments.Count != 2)
            {
                reason = "MapDisplayLayout 必须包含两段有序计分轨迹。";
                return false;
            }

            var first = scoreTrackSegments[0];
            var second = scoreTrackSegments[1];
            if (first == null || second == null ||
                first.MinimumScore != ScoreTrackService.MinimumTrackScore ||
                first.MaximumScore != 23 || second.MinimumScore != 24 ||
                second.MaximumScore != ScoreTrackService.MaximumTrackScore ||
                !IsNormalizedPoint(first.Start) || !IsNormalizedPoint(first.End) ||
                !IsNormalizedPoint(second.Start) || !IsNormalizedPoint(second.End))
            {
                reason = "MapDisplayLayout 两段计分轨迹范围或端点无效。";
                return false;
            }

            var supportedPlayerCount = MapId == StaticMapDefinitions.ThreePlayerMapId ? 3 : 4;
            if (scoreMarkerOffsets == null || scoreMarkerOffsets.Count != supportedPlayerCount)
            {
                reason = "MapDisplayLayout 必须包含 1 至 " + supportedPlayerCount + " 人同分偏移数组。";
                return false;
            }

            for (var i = 0; i < scoreMarkerOffsets.Count; i++)
            {
                var definition = scoreMarkerOffsets[i];
                var expectedCount = i + 1;
                if (definition == null || definition.MarkerCount != expectedCount ||
                    definition.Offsets == null || definition.Offsets.Count != expectedCount)
                {
                    reason = "MapDisplayLayout 同分偏移缺项、重复或长度错误：" + expectedCount;
                    return false;
                }

                for (var offsetIndex = 0; offsetIndex < definition.Offsets.Count; offsetIndex++)
                {
                    if (!IsFinite(definition.Offsets[offsetIndex].x) ||
                        !IsFinite(definition.Offsets[offsetIndex].y))
                    {
                        reason = "MapDisplayLayout 同分偏移包含非有限值。";
                        return false;
                    }
                }
            }

            reason = string.Empty;
            return true;
        }

        public IReadOnlyList<MapResourcePointDisplayDefinition> CreateResourcePointDefinitions()
        {
            if (Locations == null)
            {
                return new List<MapResourcePointDisplayDefinition>();
            }

            var definitions = new List<MapResourcePointDisplayDefinition>(Locations.Count);
            for (var i = 0; i < Locations.Count; i++)
            {
                var location = Locations[i];
                if (location == null)
                {
                    definitions.Add(null);
                    continue;
                }

                definitions.Add(new MapResourcePointDisplayDefinition
                {
                    MapId = MapId,
                    LocationId = location.LocationId,
                    ResourceTokenPosition = location.NormalizedPosition + location.ResourceTokenOffset,
                    InfluenceSlots = CreateSlots(location.NormalizedPosition, location.InfluenceSlots)
                });
            }

            return definitions;
        }

        public IReadOnlyList<MapRouteDisplayDefinition> CreateRouteDefinitions()
        {
            if (Routes == null)
            {
                return new List<MapRouteDisplayDefinition>();
            }

            var definitions = new List<MapRouteDisplayDefinition>(Routes.Count);
            for (var i = 0; i < Routes.Count; i++)
            {
                var routeLayout = Routes[i];
                if (routeLayout == null)
                {
                    definitions.Add(null);
                    continue;
                }

                definitions.Add(new MapRouteDisplayDefinition
                {
                    MapId = MapId,
                    RouteId = routeLayout.RouteId,
                    InfluenceSlots = CreateSlots(routeLayout.NormalizedPosition, routeLayout.InfluenceSlots)
                });
            }

            return definitions;
        }

        private static List<MapInfluenceSlotDisplayDefinition> CreateSlots(
            Vector2 anchor,
            IReadOnlyList<MapInfluenceSlotLayoutDefinition> layouts)
        {
            var slots = new List<MapInfluenceSlotDisplayDefinition>(layouts == null ? 0 : layouts.Count);
            if (layouts == null)
            {
                return slots;
            }

            for (var i = 0; i < layouts.Count; i++)
            {
                var layout = layouts[i];
                if (layout == null)
                {
                    slots.Add(null);
                    continue;
                }

                slots.Add(new MapInfluenceSlotDisplayDefinition
                {
                    NormalizedPosition = anchor + layout.Offset,
                    Size = layout.Size,
                    ColliderRadius = layout.ColliderRadius
                });
            }

            return slots;
        }

        private void EnsureValidScoreTrack()
        {
            if (!TryValidateScoreTrack(out var reason))
            {
                throw new InvalidOperationException(reason);
            }
        }

        private static bool IsNormalizedPoint(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) &&
                   value.x >= 0f && value.x <= 1f && value.y >= 0f && value.y <= 1f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var character = value[i];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'A' && character <= 'F') ||
                      (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }

    [Serializable]
    public sealed class MapLocationLayoutDefinition
    {
        public string LocationId = string.Empty;
        public Vector2 NormalizedPosition;
        public Vector2 ResourceTokenOffset;
        public List<MapInfluenceSlotLayoutDefinition> InfluenceSlots =
            new List<MapInfluenceSlotLayoutDefinition>();
    }

    [Serializable]
    public sealed class MapRouteLayoutDefinition
    {
        public string RouteId = string.Empty;
        public Vector2 NormalizedPosition;
        public List<MapInfluenceSlotLayoutDefinition> InfluenceSlots =
            new List<MapInfluenceSlotLayoutDefinition>();
    }

    [Serializable]
    public sealed class MapInfluenceSlotLayoutDefinition
    {
        public Vector2 Offset;
        public float Size = 1f;
        public float ColliderRadius = 0.22f;
    }

    [Serializable]
    public sealed class MapScoreTrackSegmentDefinition
    {
        public int MinimumScore;
        public int MaximumScore;
        public Vector2 Start;
        public Vector2 End;
    }

    [Serializable]
    public sealed class MapScoreMarkerOffsetDefinition
    {
        public int MarkerCount;
        public List<Vector2> Offsets = new List<Vector2>();
    }

    public static class MapDisplayLayoutCatalog
    {
        private const string ResourceDirectory = "MapLayouts/";

        public static MapDisplayLayout Load(string mapId)
        {
            return string.IsNullOrEmpty(mapId)
                ? null
                : Resources.Load<MapDisplayLayout>(ResourceDirectory + mapId);
        }
    }

    public static class MapDisplayLayoutValidator
    {
        public static IReadOnlyList<string> Validate(GameMapDefinition map, MapDisplayLayout layout)
        {
            var errors = new List<string>();
            if (map == null)
            {
                errors.Add("Map definition is required.");
                return errors;
            }

            if (layout == null)
            {
                errors.Add("Map display layout is required for map " + map.MapId + ".");
                return errors;
            }

            if (!string.Equals(layout.MapId, map.MapId, StringComparison.Ordinal))
            {
                errors.Add("Map display layout uses mapId " + layout.MapId + " but expected " + map.MapId + ".");
            }

            if (layout.MapSprite == null)
            {
                errors.Add("Map display layout " + layout.MapId + " is not bound to a map sprite.");
            }

            if (!layout.TryValidateScoreTrack(out var scoreTrackReason))
            {
                errors.Add(scoreTrackReason);
            }

            ValidateDomainIds(map, errors);
            ValidateAnchors(layout, errors);
            if (map.Locations == null || map.Routes == null ||
                layout.Locations == null || layout.Routes == null)
            {
                return errors;
            }

            AddErrors(errors, MapResourcePointDisplayDefinitionValidator.Validate(
                map,
                layout.CreateResourcePointDefinitions()));
            AddErrors(errors, MapRouteDisplayDefinitionValidator.Validate(
                map,
                layout.CreateRouteDefinitions()));
            return errors;
        }

        private static void ValidateAnchors(MapDisplayLayout layout, List<string> errors)
        {
            if (layout.Locations == null)
            {
                errors.Add("Map display layout must define locations.");
                return;
            }

            if (layout.Routes == null)
            {
                errors.Add("Map display layout must define routes.");
                return;
            }

            var locationIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < layout.Locations.Count; i++)
            {
                var location = layout.Locations[i];
                if (location == null)
                {
                    errors.Add("Map location layout at index " + i + " is null.");
                    continue;
                }

                if (!locationIds.Add(location.LocationId))
                {
                    errors.Add("Map location " + location.LocationId + " has duplicate layout anchors.");
                }

                ValidatePoint("Map location " + location.LocationId + " anchor", location.NormalizedPosition, errors);
            }

            var routeIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < layout.Routes.Count; i++)
            {
                var route = layout.Routes[i];
                if (route == null)
                {
                    errors.Add("Map route layout at index " + i + " is null.");
                    continue;
                }

                if (!routeIds.Add(route.RouteId))
                {
                    errors.Add("Map route " + route.RouteId + " has duplicate layout anchors.");
                }

                ValidatePoint("Map route " + route.RouteId + " anchor", route.NormalizedPosition, errors);
            }
        }

        private static void ValidateDomainIds(GameMapDefinition map, List<string> errors)
        {
            if (map.Locations == null)
            {
                errors.Add("Map definition must define locations.");
            }
            else
            {
                var locationIds = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < map.Locations.Count; i++)
                {
                    var location = map.Locations[i];
                    if (location == null || string.IsNullOrEmpty(location.LocationId))
                    {
                        errors.Add("Map location at index " + i + " has no unique id.");
                    }
                    else if (!locationIds.Add(location.LocationId))
                    {
                        errors.Add("Map definition has duplicate location id " + location.LocationId + ".");
                    }
                }
            }

            if (map.Routes == null)
            {
                errors.Add("Map definition must define routes.");
            }
            else
            {
                var routeIds = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < map.Routes.Count; i++)
                {
                    var route = map.Routes[i];
                    if (route == null || string.IsNullOrEmpty(route.RouteId))
                    {
                        errors.Add("Map route at index " + i + " has no unique id.");
                    }
                    else if (!routeIds.Add(route.RouteId))
                    {
                        errors.Add("Map definition has duplicate route id " + route.RouteId + ".");
                    }
                }
            }
        }

        private static void ValidatePoint(string label, Vector2 point, List<string> errors)
        {
            if (float.IsNaN(point.x) || float.IsInfinity(point.x) ||
                float.IsNaN(point.y) || float.IsInfinity(point.y) ||
                point.x < 0f || point.x > 1f || point.y < 0f || point.y > 1f)
            {
                errors.Add(label + " is outside normalized map bounds.");
            }
        }

        private static void AddErrors(List<string> target, IReadOnlyList<string> source)
        {
            for (var i = 0; i < source.Count; i++)
            {
                target.Add(source[i]);
            }
        }
    }
}
