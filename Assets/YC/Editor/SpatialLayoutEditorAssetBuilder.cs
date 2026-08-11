using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using YC.Presentation;
using YC.Presentation.Maps;

namespace YC.Editor
{
    public static class SpatialLayoutEditorAssetBuilder
    {
        public const string SourceJsonPath =
            "Assets/YC/Editor/Data/spatial_layout_manifest.json";
        public const string SourceJsonGuid = "f746bcfc63b342d29fbdcb9e134a1f03";
        public const string ExpectedManifestSha256 =
            "03C979B63F8B036225E061A59BE9E0EA2E86BB9F5CDE81C854396894E124DB5B";
        public const string CardBoardLayoutAssetPath =
            "Assets/YC/Presentation/Content/CardBoardVisualLayout.asset";
        public const string CardBoardLayoutAssetGuid = "dd760d7d5af84c1990edbfc132128f68";
        public const string FourPlayerMapLayoutAssetPath =
            "Assets/Resources/MapLayouts/map-four-players.asset";
        public const string FourPlayerMapLayoutAssetGuid = "07db2f96f43c41d6be8c2c15966f760e";

        private sealed class ParsedSource
        {
            public string Sha256 = string.Empty;
            public readonly List<Vector2> SlotCenters = new List<Vector2>();
            public float SlotWidthRatio;
            public float SlotHeightRatio;
            public readonly List<MarkerAreaAnchor> MarkerAnchors = new List<MarkerAreaAnchor>();
            public int RegularMarkerColumnCount;
            public float RegularMarkerColumnSpacingX;
            public float RegularMarkerRowSpacingY;
            public int MilitaryPlayerLaneCount;
            public int MilitaryMarkerRowCount;
            public float MilitaryFirstLaneX;
            public float MilitaryLaneSpacingX;
            public float MilitaryFirstMarkerY;
            public float MilitaryMarkerSpacingY;
            public Rect LevelOneUsedArea;
            public Rect LevelTwoUsedFromTwoArea;
            public Rect LevelTwoUsedFromOneArea;
            public Vector2 FallbackAreaHalfExtents;
            public float BuildInfoMarkerSize;
            public float DeclarationPreviewMarkerSize;
            public string MapId = string.Empty;
            public readonly List<MapScoreTrackSegmentDefinition> ScoreTrackSegments =
                new List<MapScoreTrackSegmentDefinition>();
            public readonly List<MapScoreMarkerOffsetDefinition> ScoreMarkerOffsets =
                new List<MapScoreMarkerOffsetDefinition>();
        }

        [MenuItem("YC/Build/Spatial Layout/Rebuild Assets")]
        public static void RebuildAssetsMenu()
        {
            RebuildAssets();
        }

        [MenuItem("YC/Build/Spatial Layout/Rebuild Assets And Configure Prefabs")]
        public static void RebuildAssetsAndConfigurePrefabsMenu()
        {
            RebuildAssets();
            YC.EditorTools.BuildInfoPanelEditorAssetBuilder.Rebuild();
            YC.EditorTools.CityStyleDeclarationPreviewEditorAssetBuilder.Rebuild();
            SpatialLayoutBuildReadiness.ValidateReadyForBuild();
            Debug.Log("[SpatialLayoutEditorAssetBuilder] 已从锁定 manifest 重建空间布局资产并配置 Prefab。");
        }

        public static void RebuildAssets()
        {
            var source = ParseSource();
            RebuildCardBoardLayout(source);
            UpdateMapScoreTrackFields(source);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(
                CardBoardLayoutAssetPath,
                ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(
                FourPlayerMapLayoutAssetPath,
                ImportAssetOptions.ForceUpdate);

            LoadRequiredCardBoardLayout();
            LoadRequiredFourPlayerMapLayout();
        }

        public static CardBoardVisualLayout LoadRequiredCardBoardLayout()
        {
            var layout = AssetDatabase.LoadAssetAtPath<CardBoardVisualLayout>(
                CardBoardLayoutAssetPath);
            if (layout == null ||
                AssetDatabase.AssetPathToGUID(CardBoardLayoutAssetPath) != CardBoardLayoutAssetGuid)
            {
                throw new InvalidOperationException(
                    "缺少固定 GUID 的 CardBoardVisualLayout 资产。");
            }

            if (!layout.TryValidateConfiguration(out var reason) ||
                !string.Equals(
                    layout.SourceManifestSha256,
                    ExpectedManifestSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !MatchesCardBoardSource(layout, ParseSource()))
            {
                throw new InvalidOperationException(
                    "CardBoardVisualLayout 未精确匹配锁定 manifest：" + reason);
            }

            ValidateUniqueMainAsset(layout, CardBoardLayoutAssetPath, CardBoardLayoutAssetGuid);
            return layout;
        }

        public static MapDisplayLayout LoadRequiredFourPlayerMapLayout()
        {
            var layout = AssetDatabase.LoadAssetAtPath<MapDisplayLayout>(
                FourPlayerMapLayoutAssetPath);
            if (layout == null ||
                AssetDatabase.AssetPathToGUID(FourPlayerMapLayoutAssetPath) !=
                FourPlayerMapLayoutAssetGuid)
            {
                throw new InvalidOperationException(
                    "缺少固定 GUID 的四人 MapDisplayLayout 资产。");
            }

            if (!layout.TryValidateScoreTrack(out var reason) ||
                !string.Equals(
                    layout.SpatialLayoutManifestSha256,
                    ExpectedManifestSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !MatchesMapScoreSource(layout, ParseSource()))
            {
                throw new InvalidOperationException(
                    "四人 MapDisplayLayout 计分轨迹未精确匹配锁定 manifest：" + reason);
            }

            ValidateUniqueMainAsset(
                layout,
                FourPlayerMapLayoutAssetPath,
                FourPlayerMapLayoutAssetGuid);
            return layout;
        }

        internal static string ComputeCurrentSourceSha256()
        {
            return ComputeSha256(SourceJsonPath);
        }

        internal static bool MatchesCardBoardSource(CardBoardVisualLayout layout)
        {
            return layout != null && MatchesCardBoardSource(layout, ParseSource());
        }

        internal static bool MatchesMapScoreSource(MapDisplayLayout layout)
        {
            return layout != null && MatchesMapScoreSource(layout, ParseSource());
        }

        private static void RebuildCardBoardLayout(ParsedSource source)
        {
            var layout = AssetDatabase.LoadAssetAtPath<CardBoardVisualLayout>(
                CardBoardLayoutAssetPath);
            if (layout == null)
            {
                layout = ScriptableObject.CreateInstance<CardBoardVisualLayout>();
                AssetDatabase.CreateAsset(layout, CardBoardLayoutAssetPath);
            }

            if (AssetDatabase.AssetPathToGUID(CardBoardLayoutAssetPath) != CardBoardLayoutAssetGuid)
            {
                throw new InvalidOperationException("CardBoardVisualLayout 资产 GUID 已改变。");
            }

            layout.ConfigureForEditor(
                source.Sha256,
                source.SlotCenters,
                source.SlotWidthRatio,
                source.SlotHeightRatio,
                source.MarkerAnchors,
                source.RegularMarkerColumnCount,
                source.RegularMarkerColumnSpacingX,
                source.RegularMarkerRowSpacingY,
                source.MilitaryPlayerLaneCount,
                source.MilitaryMarkerRowCount,
                source.MilitaryFirstLaneX,
                source.MilitaryLaneSpacingX,
                source.MilitaryFirstMarkerY,
                source.MilitaryMarkerSpacingY,
                source.LevelOneUsedArea,
                source.LevelTwoUsedFromTwoArea,
                source.LevelTwoUsedFromOneArea,
                source.FallbackAreaHalfExtents,
                source.BuildInfoMarkerSize,
                source.DeclarationPreviewMarkerSize);
            if (!layout.TryValidateConfiguration(out var reason) ||
                !MatchesCardBoardSource(layout, source))
            {
                throw new InvalidOperationException(
                    "由 manifest 生成的 CardBoardVisualLayout 无效：" + reason);
            }

            EditorUtility.SetDirty(layout);
        }

        private static void UpdateMapScoreTrackFields(ParsedSource source)
        {
            var layout = AssetDatabase.LoadAssetAtPath<MapDisplayLayout>(
                FourPlayerMapLayoutAssetPath);
            if (layout == null ||
                AssetDatabase.AssetPathToGUID(FourPlayerMapLayoutAssetPath) !=
                FourPlayerMapLayoutAssetGuid)
            {
                throw new InvalidOperationException("四人 MapDisplayLayout 缺失或 GUID 已改变。");
            }

            if (!string.Equals(layout.MapId, source.MapId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "空间布局 manifest mapId 与现有 MapDisplayLayout 不一致。");
            }

            var protectedGeometry = CaptureProtectedMapGeometry(layout);
            var serialized = new SerializedObject(layout);
            RequireProperty(serialized, "spatialLayoutManifestSha256").stringValue = source.Sha256;

            var segments = RequireProperty(serialized, "scoreTrackSegments");
            segments.arraySize = source.ScoreTrackSegments.Count;
            for (var i = 0; i < source.ScoreTrackSegments.Count; i++)
            {
                var target = segments.GetArrayElementAtIndex(i);
                var value = source.ScoreTrackSegments[i];
                target.FindPropertyRelative("MinimumScore").intValue = value.MinimumScore;
                target.FindPropertyRelative("MaximumScore").intValue = value.MaximumScore;
                target.FindPropertyRelative("Start").vector2Value = value.Start;
                target.FindPropertyRelative("End").vector2Value = value.End;
            }

            var offsetGroups = RequireProperty(serialized, "scoreMarkerOffsets");
            offsetGroups.arraySize = source.ScoreMarkerOffsets.Count;
            for (var i = 0; i < source.ScoreMarkerOffsets.Count; i++)
            {
                var target = offsetGroups.GetArrayElementAtIndex(i);
                var value = source.ScoreMarkerOffsets[i];
                target.FindPropertyRelative("MarkerCount").intValue = value.MarkerCount;
                var offsets = target.FindPropertyRelative("Offsets");
                offsets.arraySize = value.Offsets.Count;
                for (var offsetIndex = 0; offsetIndex < value.Offsets.Count; offsetIndex++)
                {
                    offsets.GetArrayElementAtIndex(offsetIndex).vector2Value =
                        value.Offsets[offsetIndex];
                }
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            if (!string.Equals(
                    protectedGeometry,
                    CaptureProtectedMapGeometry(layout),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "定向写入计分字段时改变了 MapSprite/Locations/Routes/MapId，已停止保存。");
            }

            string reason = null;
            if (AssetDatabase.AssetPathToGUID(FourPlayerMapLayoutAssetPath) !=
                FourPlayerMapLayoutAssetGuid ||
                !layout.TryValidateScoreTrack(out reason) ||
                !MatchesMapScoreSource(layout, source))
            {
                throw new InvalidOperationException(
                    "由 manifest 写入的四人地图计分轨迹无效：" + reason);
            }

            EditorUtility.SetDirty(layout);
        }

        private static ParsedSource ParseSource()
        {
            if (!File.Exists(SourceJsonPath) ||
                AssetDatabase.AssetPathToGUID(SourceJsonPath) != SourceJsonGuid)
            {
                throw new InvalidOperationException("空间布局 manifest 缺失或 GUID 已改变。");
            }

            var hash = ComputeSha256(SourceJsonPath);
            if (!string.Equals(hash, ExpectedManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "空间布局 manifest SHA-256 已改变；必须先审核并更新固定哈希。");
            }

            var root = JObject.Parse(File.ReadAllText(SourceJsonPath));
            RequireExactInt(root, "schemaVersion", 1);
            var result = new ParsedSource { Sha256 = hash };
            ParseCardBoard(RequireObject(root, "cardBoard"), result);
            ParseMap(RequireObject(root, "mapFourPlayers"), result);
            ValidateParsedSource(result);
            return result;
        }

        private static void ParseCardBoard(JObject source, ParsedSource result)
        {
            result.SlotWidthRatio = RequireFloat(source, "slotWidthRatio");
            result.SlotHeightRatio = RequireFloat(source, "slotHeightRatio");
            var centers = RequireArray(source, "slotCenters");
            for (var i = 0; i < centers.Count; i++)
            {
                result.SlotCenters.Add(ReadPoint(RequireObject(centers[i], "slotCenters[" + i + "]")));
            }

            var anchors = RequireArray(source, "markerAnchors");
            for (var i = 0; i < anchors.Count; i++)
            {
                var anchor = RequireObject(anchors[i], "markerAnchors[" + i + "]");
                result.MarkerAnchors.Add(new MarkerAreaAnchor
                {
                    MarkerArea = RequireString(anchor, "markerArea"),
                    Anchor = ReadPoint(anchor)
                });
            }

            var regular = RequireObject(source, "regularMarkers");
            result.RegularMarkerColumnCount = RequireInt(regular, "columnCount");
            result.RegularMarkerColumnSpacingX = RequireFloat(regular, "columnSpacingX");
            result.RegularMarkerRowSpacingY = RequireFloat(regular, "rowSpacingY");

            var military = RequireObject(source, "militaryUnusedMarkers");
            result.MilitaryPlayerLaneCount = RequireInt(military, "playerLaneCount");
            result.MilitaryMarkerRowCount = RequireInt(military, "markerRowCount");
            result.MilitaryFirstLaneX = RequireFloat(military, "firstLaneX");
            result.MilitaryLaneSpacingX = RequireFloat(military, "laneSpacingX");
            result.MilitaryFirstMarkerY = RequireFloat(military, "firstMarkerY");
            result.MilitaryMarkerSpacingY = RequireFloat(military, "markerSpacingY");

            var areas = RequireArray(source, "specialActionAreas");
            if (areas.Count != 3)
            {
                throw new InvalidOperationException("specialActionAreas 必须恰好包含三项。");
            }
            result.LevelOneUsedArea = ReadArea(areas, 0, "level_one_used");
            result.LevelTwoUsedFromTwoArea = ReadArea(areas, 1, "level_two_used_from_two");
            result.LevelTwoUsedFromOneArea = ReadArea(areas, 2, "level_two_used_from_one");
            result.FallbackAreaHalfExtents = ReadPoint(
                RequireObject(source, "fallbackSpecialActionAreaHalfExtents"));
            var sizes = RequireObject(source, "markerSizesPixels");
            result.BuildInfoMarkerSize = RequireFloat(sizes, "buildInfo");
            result.DeclarationPreviewMarkerSize = RequireFloat(sizes, "declarationPreview");
        }

        private static void ParseMap(JObject source, ParsedSource result)
        {
            result.MapId = RequireString(source, "mapId");
            var segments = RequireArray(source, "scoreTrackSegments");
            for (var i = 0; i < segments.Count; i++)
            {
                var segment = RequireObject(segments[i], "scoreTrackSegments[" + i + "]");
                result.ScoreTrackSegments.Add(new MapScoreTrackSegmentDefinition
                {
                    MinimumScore = RequireInt(segment, "minimumScore"),
                    MaximumScore = RequireInt(segment, "maximumScore"),
                    Start = ReadPoint(RequireObject(segment, "start")),
                    End = ReadPoint(RequireObject(segment, "end"))
                });
            }

            var groups = RequireArray(source, "scoreMarkerOffsets");
            for (var i = 0; i < groups.Count; i++)
            {
                var group = RequireObject(groups[i], "scoreMarkerOffsets[" + i + "]");
                var definition = new MapScoreMarkerOffsetDefinition
                {
                    MarkerCount = RequireInt(group, "markerCount")
                };
                var offsets = RequireArray(group, "offsets");
                for (var offsetIndex = 0; offsetIndex < offsets.Count; offsetIndex++)
                {
                    definition.Offsets.Add(ReadPoint(
                        RequireObject(offsets[offsetIndex], "offsets[" + offsetIndex + "]")));
                }
                result.ScoreMarkerOffsets.Add(definition);
            }
        }

        private static void ValidateParsedSource(ParsedSource source)
        {
            var cardProbe = ScriptableObject.CreateInstance<CardBoardVisualLayout>();
            var mapProbe = ScriptableObject.CreateInstance<MapDisplayLayout>();
            try
            {
                cardProbe.ConfigureForEditor(
                    source.Sha256, source.SlotCenters, source.SlotWidthRatio,
                    source.SlotHeightRatio, source.MarkerAnchors,
                    source.RegularMarkerColumnCount, source.RegularMarkerColumnSpacingX,
                    source.RegularMarkerRowSpacingY, source.MilitaryPlayerLaneCount,
                    source.MilitaryMarkerRowCount, source.MilitaryFirstLaneX,
                    source.MilitaryLaneSpacingX, source.MilitaryFirstMarkerY,
                    source.MilitaryMarkerSpacingY, source.LevelOneUsedArea,
                    source.LevelTwoUsedFromTwoArea, source.LevelTwoUsedFromOneArea,
                    source.FallbackAreaHalfExtents, source.BuildInfoMarkerSize,
                    source.DeclarationPreviewMarkerSize);
                if (!cardProbe.TryValidateConfiguration(out var cardReason))
                {
                    throw new InvalidOperationException("空间布局 manifest 卡板部分无效：" + cardReason);
                }

                var serialized = new SerializedObject(mapProbe);
                RequireProperty(serialized, "spatialLayoutManifestSha256").stringValue = source.Sha256;
                WriteScoreCollections(serialized, source);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                if (!mapProbe.TryValidateScoreTrack(out var mapReason))
                {
                    throw new InvalidOperationException("空间布局 manifest 地图部分无效：" + mapReason);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cardProbe);
                UnityEngine.Object.DestroyImmediate(mapProbe);
            }
        }

        private static void WriteScoreCollections(SerializedObject serialized, ParsedSource source)
        {
            var segments = RequireProperty(serialized, "scoreTrackSegments");
            segments.arraySize = source.ScoreTrackSegments.Count;
            for (var i = 0; i < source.ScoreTrackSegments.Count; i++)
            {
                var target = segments.GetArrayElementAtIndex(i);
                var value = source.ScoreTrackSegments[i];
                target.FindPropertyRelative("MinimumScore").intValue = value.MinimumScore;
                target.FindPropertyRelative("MaximumScore").intValue = value.MaximumScore;
                target.FindPropertyRelative("Start").vector2Value = value.Start;
                target.FindPropertyRelative("End").vector2Value = value.End;
            }

            var groups = RequireProperty(serialized, "scoreMarkerOffsets");
            groups.arraySize = source.ScoreMarkerOffsets.Count;
            for (var i = 0; i < source.ScoreMarkerOffsets.Count; i++)
            {
                var target = groups.GetArrayElementAtIndex(i);
                var value = source.ScoreMarkerOffsets[i];
                target.FindPropertyRelative("MarkerCount").intValue = value.MarkerCount;
                var offsets = target.FindPropertyRelative("Offsets");
                offsets.arraySize = value.Offsets.Count;
                for (var offsetIndex = 0; offsetIndex < value.Offsets.Count; offsetIndex++)
                {
                    offsets.GetArrayElementAtIndex(offsetIndex).vector2Value = value.Offsets[offsetIndex];
                }
            }
        }

        private static bool MatchesCardBoardSource(CardBoardVisualLayout layout, ParsedSource source)
        {
            if (layout.CityBoardSlotCount != source.SlotCenters.Count ||
                !Approximately(layout.CityBoardSlotWidthRatio, source.SlotWidthRatio) ||
                !Approximately(layout.CityBoardSlotHeightRatio, source.SlotHeightRatio) ||
                layout.MarkerAreaAnchorCount != source.MarkerAnchors.Count ||
                layout.RegularMarkerColumnCount != source.RegularMarkerColumnCount ||
                !Approximately(layout.RegularMarkerColumnSpacingX, source.RegularMarkerColumnSpacingX) ||
                !Approximately(layout.RegularMarkerRowSpacingY, source.RegularMarkerRowSpacingY) ||
                layout.MilitaryUnusedPlayerLaneCount != source.MilitaryPlayerLaneCount ||
                layout.MilitaryUnusedMarkerRowCount != source.MilitaryMarkerRowCount ||
                !Approximately(layout.MilitaryUnusedFirstLaneX, source.MilitaryFirstLaneX) ||
                !Approximately(layout.MilitaryUnusedLaneSpacingX, source.MilitaryLaneSpacingX) ||
                !Approximately(layout.MilitaryUnusedFirstMarkerY, source.MilitaryFirstMarkerY) ||
                !Approximately(layout.MilitaryUnusedMarkerSpacingY, source.MilitaryMarkerSpacingY) ||
                !Approximately(layout.LevelOneUsedSpecialActionArea, source.LevelOneUsedArea) ||
                !Approximately(layout.LevelTwoUsedFromTwoSpecialActionArea, source.LevelTwoUsedFromTwoArea) ||
                !Approximately(layout.LevelTwoUsedFromOneSpecialActionArea, source.LevelTwoUsedFromOneArea) ||
                !Approximately(layout.FallbackSpecialActionAreaHalfExtents, source.FallbackAreaHalfExtents) ||
                !Approximately(layout.BuildInfoMarkerSize.x, source.BuildInfoMarkerSize) ||
                !Approximately(layout.DeclarationPreviewMarkerSize.x, source.DeclarationPreviewMarkerSize))
            {
                return false;
            }

            for (var i = 0; i < source.SlotCenters.Count; i++)
            {
                if (!Approximately(layout.GetCityBoardSlotCenter(i), source.SlotCenters[i]))
                {
                    return false;
                }
            }
            for (var i = 0; i < source.MarkerAnchors.Count; i++)
            {
                if (!Approximately(
                        layout.GetMarkerAreaAnchor(source.MarkerAnchors[i].MarkerArea),
                        source.MarkerAnchors[i].Anchor))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool MatchesMapScoreSource(MapDisplayLayout layout, ParsedSource source)
        {
            if (!string.Equals(layout.MapId, source.MapId, StringComparison.Ordinal) ||
                layout.ScoreTrackSegments.Count != source.ScoreTrackSegments.Count ||
                layout.ScoreMarkerOffsets.Count != source.ScoreMarkerOffsets.Count)
            {
                return false;
            }
            for (var i = 0; i < source.ScoreTrackSegments.Count; i++)
            {
                var left = layout.ScoreTrackSegments[i];
                var right = source.ScoreTrackSegments[i];
                if (left.MinimumScore != right.MinimumScore || left.MaximumScore != right.MaximumScore ||
                    !Approximately(left.Start, right.Start) || !Approximately(left.End, right.End))
                {
                    return false;
                }
            }
            for (var i = 0; i < source.ScoreMarkerOffsets.Count; i++)
            {
                var left = layout.ScoreMarkerOffsets[i];
                var right = source.ScoreMarkerOffsets[i];
                if (left.MarkerCount != right.MarkerCount || left.Offsets.Count != right.Offsets.Count)
                {
                    return false;
                }
                for (var j = 0; j < right.Offsets.Count; j++)
                {
                    if (!Approximately(left.Offsets[j], right.Offsets[j]))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static string CaptureProtectedMapGeometry(MapDisplayLayout layout)
        {
            var value = new StringBuilder();
            value.Append(layout.MapId).Append('|');
            if (layout.MapSprite == null ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    layout.MapSprite,
                    out var spriteGuid,
                    out long spriteLocalId))
            {
                value.Append("null");
            }
            else
            {
                value.Append(spriteGuid).Append(':').Append(spriteLocalId);
            }
            value.Append('|').Append(layout.Locations == null ? -1 : layout.Locations.Count);
            if (layout.Locations != null)
            {
                foreach (var location in layout.Locations)
                {
                    if (location == null) { value.Append("|null"); continue; }
                    Append(value, location.LocationId, location.NormalizedPosition, location.ResourceTokenOffset);
                    value.Append(':').Append(location.InfluenceSlots == null ? -1 : location.InfluenceSlots.Count);
                    if (location.InfluenceSlots != null)
                        foreach (var slot in location.InfluenceSlots) Append(value, slot);
                }
            }
            value.Append('|').Append(layout.Routes == null ? -1 : layout.Routes.Count);
            if (layout.Routes != null)
            {
                foreach (var route in layout.Routes)
                {
                    if (route == null) { value.Append("|null"); continue; }
                    Append(value, route.RouteId, route.NormalizedPosition, Vector2.zero);
                    value.Append(':').Append(route.InfluenceSlots == null ? -1 : route.InfluenceSlots.Count);
                    if (route.InfluenceSlots != null)
                        foreach (var slot in route.InfluenceSlots) Append(value, slot);
                }
            }
            return value.ToString();
        }

        private static void Append(
            StringBuilder target,
            string id,
            Vector2 first,
            Vector2 second)
        {
            target.Append('|').Append(id).Append(':');
            Append(target, first); target.Append(':'); Append(target, second);
        }

        private static void Append(StringBuilder target, MapInfluenceSlotLayoutDefinition slot)
        {
            if (slot == null) { target.Append("|null"); return; }
            target.Append('|'); Append(target, slot.Offset);
            target.Append(':').Append(slot.Size.ToString("R", CultureInfo.InvariantCulture));
            target.Append(':').Append(slot.ColliderRadius.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void Append(StringBuilder target, Vector2 value)
        {
            target.Append(value.x.ToString("R", CultureInfo.InvariantCulture));
            target.Append(',').Append(value.y.ToString("R", CultureInfo.InvariantCulture));
        }

        private static Rect ReadArea(JArray values, int index, string expectedId)
        {
            var source = RequireObject(values[index], "specialActionAreas[" + index + "]");
            if (RequireString(source, "areaId") != expectedId)
                throw new InvalidOperationException("specialActionAreas 顺序或稳定 areaId 不正确。");
            return Rect.MinMaxRect(
                RequireFloat(source, "minX"), RequireFloat(source, "minY"),
                RequireFloat(source, "maxX"), RequireFloat(source, "maxY"));
        }

        private static Vector2 ReadPoint(JObject source)
        {
            return new Vector2(RequireFloat(source, "x"), RequireFloat(source, "y"));
        }

        private static JObject RequireObject(JObject source, string property)
        {
            return RequireObject(source[property], property);
        }

        private static JObject RequireObject(JToken token, string label)
        {
            if (!(token is JObject result))
                throw new InvalidOperationException(label + " 必须是 JSON object。");
            return result;
        }

        private static JArray RequireArray(JObject source, string property)
        {
            if (!(source[property] is JArray result))
                throw new InvalidOperationException(property + " 必须是 JSON array。");
            return result;
        }

        private static string RequireString(JObject source, string property)
        {
            var value = (string)source[property];
            if (string.IsNullOrEmpty(value))
                throw new InvalidOperationException(property + " 必须是非空字符串。");
            return value;
        }

        private static int RequireInt(JObject source, string property)
        {
            var token = source[property];
            if (token == null || token.Type != JTokenType.Integer)
                throw new InvalidOperationException(property + " 必须是整数。");
            return (int)token;
        }

        private static void RequireExactInt(JObject source, string property, int expected)
        {
            var value = RequireInt(source, property);
            if (value != expected)
                throw new InvalidOperationException(property + " 必须为 " + expected + "。");
        }

        private static float RequireFloat(JObject source, string property)
        {
            var token = source[property];
            if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer))
                throw new InvalidOperationException(property + " 必须是有限数值。");
            var value = (float)token;
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidOperationException(property + " 必须是有限数值。");
            return value;
        }

        private static SerializedProperty RequireProperty(SerializedObject source, string property)
        {
            var result = source.FindProperty(property);
            if (result == null)
                throw new InvalidOperationException(source.targetObject.GetType().Name + " 缺少字段 " + property + "。");
            return result;
        }

        private static void ValidateUniqueMainAsset(
            UnityEngine.Object asset,
            string path,
            string expectedGuid)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(path);
            if (assets.Length != 1 || assets[0] != asset || !AssetDatabase.Contains(asset) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out var guid, out long localId) ||
                guid != expectedGuid || localId == 0)
            {
                throw new InvalidOperationException(path + " 必须是固定 GUID 的唯一持久化主资产。");
            }
        }

        private static string ComputeSha256(string path)
        {
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(File.ReadAllBytes(path)))
                    .Replace("-", string.Empty);
            }
        }

        private static bool Approximately(float left, float right)
        {
            return Mathf.Abs(left - right) < 0.000001f;
        }

        private static bool Approximately(Vector2 left, Vector2 right)
        {
            return Approximately(left.x, right.x) && Approximately(left.y, right.y);
        }

        private static bool Approximately(Rect left, Rect right)
        {
            return Approximately(left.xMin, right.xMin) && Approximately(left.yMin, right.yMin) &&
                   Approximately(left.xMax, right.xMax) && Approximately(left.yMax, right.yMax);
        }
    }
}
