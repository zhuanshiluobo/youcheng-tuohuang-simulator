using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using YC.Domain.Maps;
using YC.Presentation;
using YC.Presentation.Maps;

namespace YC.Editor
{
    public static class SpatialLayoutBuildReadiness
    {
        private const string BuildInfoPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/BuildInfoPanel.prefab";
        private const string DeclarationPreviewPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CityStyleDeclarationPreviewDialog.prefab";
        private const string MapViewPrefabPath =
            "Assets/YC/Presentation/Prefabs/Map/MapView.prefab";
        private const string GameplayHudPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";

        public static void ValidateReadyForBuild()
        {
            ValidateManifest();
            var cardBoardLayout =
                SpatialLayoutEditorAssetBuilder.LoadRequiredCardBoardLayout();
            var mapLayout =
                SpatialLayoutEditorAssetBuilder.LoadRequiredFourPlayerMapLayout();
            ValidateCardBoardPrefabs(cardBoardLayout);
            ValidateMapPrefabAndScene(mapLayout);
        }

        public static void ValidateManifest()
        {
            if (!File.Exists(SpatialLayoutEditorAssetBuilder.SourceJsonPath) ||
                AssetDatabase.AssetPathToGUID(SpatialLayoutEditorAssetBuilder.SourceJsonPath) !=
                SpatialLayoutEditorAssetBuilder.SourceJsonGuid)
            {
                throw new InvalidOperationException("空间布局 manifest 缺失或 GUID 已改变。");
            }

            var hash = SpatialLayoutEditorAssetBuilder.ComputeCurrentSourceSha256();
            if (!string.Equals(
                    hash,
                    SpatialLayoutEditorAssetBuilder.ExpectedManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("空间布局 manifest SHA-256 与审核锁不一致。");
            }
        }

        public static void ValidateCardBoardPrefabs(CardBoardVisualLayout expectedLayout)
        {
            ValidatePrefabConsumer<BuildInfoPanel>(
                BuildInfoPrefabPath,
                expectedLayout,
                "cardBoardVisualLayout");
            ValidatePrefabConsumer<CityStyleDeclarationPreviewView>(
                DeclarationPreviewPrefabPath,
                expectedLayout,
                "cardBoardVisualLayout");

            var buildInfoPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BuildInfoPrefabPath);
            var buildInfo = buildInfoPrefab.GetComponent<BuildInfoPanel>();
            string buildInfoReason = null;
            if (buildInfo.View == null ||
                !buildInfo.View.TryValidateConfiguration(out buildInfoReason))
            {
                throw new InvalidOperationException(
                    "BuildInfoPanel Prefab 固定 View 无效：" + buildInfoReason);
            }

            var previewPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DeclarationPreviewPrefabPath);
            var preview = previewPrefab.GetComponent<CityStyleDeclarationPreviewView>();
            if (!preview.TryValidateConfiguration(out var previewReason))
            {
                throw new InvalidOperationException(
                    "CityStyleDeclarationPreview Prefab 固定 View 无效：" + previewReason);
            }

            var gameplayHud = AssetDatabase.LoadAssetAtPath<GameObject>(GameplayHudPrefabPath);
            var nestedBuildInfo = gameplayHud == null
                ? Array.Empty<BuildInfoPanel>()
                : gameplayHud.GetComponentsInChildren<BuildInfoPanel>(true);
            var registries = gameplayHud == null
                ? Array.Empty<GameplayDialogRegistry>()
                : gameplayHud.GetComponentsInChildren<GameplayDialogRegistry>(true);
            if (nestedBuildInfo.Length != 1 ||
                nestedBuildInfo[0].CardBoardVisualLayout != expectedLayout ||
                registries.Length != 1 ||
                registries[0].CityStyleDeclarationPreviewPrefab != preview)
            {
                throw new InvalidOperationException(
                    "GameplayInteractionHud 必须通过持久 Prefab 链接消费两个空间布局消费者。");
            }

            foreach (var scene in EditorBuildSettings.scenes.Where(item => item.enabled))
            {
                var sceneYaml = File.ReadAllText(scene.path);
                if (Regex.IsMatch(
                        sceneYaml,
                        @"propertyPath:\s*cardBoardVisualLayout\s*$",
                        RegexOptions.Multiline | RegexOptions.CultureInvariant))
                {
                    throw new InvalidOperationException(
                        scene.path + " 不得覆盖 Prefab 提供的 CardBoardVisualLayout 引用。");
                }
            }
        }

        public static void ValidateMapPrefabAndScene(MapDisplayLayout expectedLayout)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MapViewPrefabPath);
            if (prefab == null || !prefab.activeSelf)
            {
                throw new InvalidOperationException("缺少启用的 MapView Prefab。");
            }

            var coordinateSpaces = prefab.GetComponentsInChildren<MapCoordinateSpace>(true);
            var views = prefab.GetComponentsInChildren<MapView>(true);
            if (coordinateSpaces.Length != 1 || views.Length != 1 ||
                coordinateSpaces[0].gameObject != prefab || views[0].gameObject != prefab ||
                !coordinateSpaces[0].enabled || coordinateSpaces[0].Layout != expectedLayout)
            {
                throw new InvalidOperationException(
                    "MapView Prefab 必须在启用根对象唯一持有预期 MapDisplayLayout 引用。");
            }

            var map = StaticMapDefinitions.CreateFourPlayerMap();
            if (!views[0].TryValidateConfiguration(map, out var reason))
            {
                throw new InvalidOperationException("MapView Prefab 配置无效：" + reason);
            }

            var enabledBuildScenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();
            if (!enabledBuildScenes.Contains(SampleScenePath))
            {
                throw new InvalidOperationException("SampleScene 必须处于启用的 Build Settings 场景中。");
            }

            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    coordinateSpaces[0],
                    out _,
                    out long coordinateSpaceLocalId) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    prefab,
                    out _,
                    out long rootLocalId) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    expectedLayout,
                    out var mapLayoutGuid,
                    out long mapLayoutLocalId))
            {
                throw new InvalidOperationException("无法解析地图 Prefab/布局的持久 fileID。");
            }

            var coordinateScript = MonoScript.FromMonoBehaviour(coordinateSpaces[0]);
            var coordinateScriptGuid = coordinateScript == null
                ? string.Empty
                : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(coordinateScript));
            if (string.IsNullOrEmpty(coordinateScriptGuid))
            {
                throw new InvalidOperationException("无法解析 MapCoordinateSpace 脚本 GUID。");
            }

            var sceneYaml = File.ReadAllText(SampleScenePath);
            ValidateSavedSceneYaml(
                SampleScenePath,
                sceneYaml,
                AssetDatabase.AssetPathToGUID(MapViewPrefabPath),
                rootLocalId,
                coordinateSpaceLocalId,
                mapLayoutGuid,
                mapLayoutLocalId,
                coordinateScriptGuid);
        }

        internal static void ValidateSavedSceneYaml(
            string scenePath,
            string yaml,
            string mapPrefabGuid,
            long rootLocalId,
            long coordinateSpaceLocalId,
            string mapLayoutGuid,
            long mapLayoutLocalId,
            string coordinateScriptGuid)
        {
            if (string.IsNullOrEmpty(yaml))
            {
                throw new InvalidOperationException(scenePath + " 场景 YAML 为空。");
            }

            var sourceReference = new Regex(
                @"m_SourcePrefab:\s*\{fileID:\s*\d+,\s*guid:\s*" +
                Regex.Escape(mapPrefabGuid) + @",\s*type:\s*3\}",
                RegexOptions.CultureInvariant);
            var prefabBlocks = Regex.Matches(
                    yaml,
                    @"---\s*!u!1001\s*&[^\r\n]+[\s\S]*?(?=\r?\n---\s*!u!|\z)",
                    RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Where(match => sourceReference.IsMatch(match.Value))
                .Select(match => match.Value)
                .ToArray();
            if (prefabBlocks.Length != 1)
            {
                throw new InvalidOperationException(
                    scenePath + " 必须恰好包含一个 MapView Prefab 实例。");
            }

            var prefabBlock = prefabBlocks[0];

            var directCoordinateSpace = new Regex(
                @"m_Script:\s*\{fileID:\s*11500000,\s*guid:\s*" +
                Regex.Escape(coordinateScriptGuid) + @",\s*type:\s*3\}",
                RegexOptions.CultureInvariant);
            if (directCoordinateSpace.IsMatch(yaml))
            {
                throw new InvalidOperationException(
                    scenePath + " 直接新增了第二个 MapCoordinateSpace 组件。");
            }

            RejectRemovedReference(
                scenePath,
                prefabBlock,
                "m_RemovedComponents",
                coordinateSpaceLocalId,
                mapPrefabGuid);
            RejectRemovedReference(
                scenePath,
                prefabBlock,
                "m_RemovedGameObjects",
                rootLocalId,
                mapPrefabGuid);
            RejectOverride(scenePath, prefabBlock, rootLocalId, mapPrefabGuid, "m_IsActive");
            RejectOverride(
                scenePath,
                prefabBlock,
                coordinateSpaceLocalId,
                mapPrefabGuid,
                "m_Enabled");

            var layoutOverride = FindOverride(
                prefabBlock,
                coordinateSpaceLocalId,
                mapPrefabGuid,
                "layout");
            if (layoutOverride != null)
            {
                var expectedReference = "{fileID: " + mapLayoutLocalId +
                                        ", guid: " + mapLayoutGuid + ", type: 2}";
                if (!layoutOverride.Contains("objectReference: " + expectedReference))
                {
                    throw new InvalidOperationException(
                        scenePath + " 把 MapCoordinateSpace.layout 覆盖为空或错误资产。");
                }

                throw new InvalidOperationException(
                    scenePath + " 不应重复覆盖来自 MapView Prefab 的 layout 引用。");
            }
        }

        private static void ValidatePrefabConsumer<T>(
            string path,
            CardBoardVisualLayout expectedLayout,
            string propertyName)
            where T : MonoBehaviour
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || !prefab.activeSelf)
            {
                throw new InvalidOperationException(path + " 缺失或根对象未启用。");
            }

            var consumers = prefab.GetComponentsInChildren<T>(true);
            if (consumers.Length != 1 || consumers[0].gameObject != prefab ||
                !consumers[0].enabled)
            {
                throw new InvalidOperationException(
                    path + " 必须在启用根对象恰好包含一个 " + typeof(T).Name + "。");
            }

            ValidateCardBoardConsumerReference(
                consumers[0],
                expectedLayout,
                path,
                propertyName);

            var yamlFieldCount = Regex.Matches(
                File.ReadAllText(path),
                @"^\s*" + Regex.Escape(propertyName) + @"\s*:",
                RegexOptions.Multiline | RegexOptions.CultureInvariant).Count;
            if (yamlFieldCount != 1)
            {
                throw new InvalidOperationException(
                    path + " 必须恰好序列化一次 " + propertyName + "，实际 " +
                    yamlFieldCount + " 次。");
            }
        }

        internal static void ValidateCardBoardConsumerReference(
            MonoBehaviour consumer,
            CardBoardVisualLayout expectedLayout,
            string label,
            string propertyName = "cardBoardVisualLayout")
        {
            var property = consumer == null
                ? null
                : new SerializedObject(consumer).FindProperty(propertyName);
            if (property == null || property.objectReferenceValue == null ||
                property.objectReferenceValue != expectedLayout)
            {
                throw new InvalidOperationException(
                    label + " 未显式引用预期 CardBoardVisualLayout 资产。");
            }
        }

        private static void RejectRemovedReference(
            string scenePath,
            string yaml,
            string section,
            long localId,
            string prefabGuid)
        {
            var sectionBody = ExtractYamlSection(yaml, section);
            var pattern = new Regex(
                @"\{fileID:\s*" + localId +
                @",\s*guid:\s*" + Regex.Escape(prefabGuid) + @",\s*type:\s*3\}",
                RegexOptions.CultureInvariant);
            if (pattern.IsMatch(sectionBody))
            {
                throw new InvalidOperationException(
                    scenePath + " 从 MapView Prefab 移除了必需对象或组件。");
            }
        }

        private static string ExtractYamlSection(string yaml, string section)
        {
            var lines = Regex.Split(yaml ?? string.Empty, "\\r?\\n");
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith(section + ":", StringComparison.Ordinal))
                {
                    continue;
                }

                var indentation = lines[i].Length - trimmed.Length;
                var body = lines[i].Substring(lines[i].IndexOf(':') + 1) + "\n";
                for (var lineIndex = i + 1; lineIndex < lines.Length; lineIndex++)
                {
                    var nextTrimmed = lines[lineIndex].TrimStart();
                    if (nextTrimmed.Length == 0)
                    {
                        body += "\n";
                        continue;
                    }

                    var nextIndentation = lines[lineIndex].Length - nextTrimmed.Length;
                    if (nextIndentation <= indentation && nextTrimmed.StartsWith("m_", StringComparison.Ordinal))
                    {
                        break;
                    }

                    body += lines[lineIndex] + "\n";
                }

                return body;
            }

            throw new InvalidOperationException("PrefabInstance YAML 缺少 " + section + " section。");
        }

        private static void RejectOverride(
            string scenePath,
            string yaml,
            long localId,
            string prefabGuid,
            string propertyPath)
        {
            if (FindOverride(yaml, localId, prefabGuid, propertyPath) != null)
            {
                throw new InvalidOperationException(
                    scenePath + " 覆盖了 MapView 必需状态 " + propertyPath + "。");
            }
        }

        private static string FindOverride(
            string yaml,
            long localId,
            string prefabGuid,
            string propertyPath)
        {
            var pattern = new Regex(
                @"-\s*target:\s*\{fileID:\s*" + localId + @",\s*guid:\s*" +
                Regex.Escape(prefabGuid) +
                @",\s*type:\s*3\}[\s\S]*?propertyPath:\s*" +
                Regex.Escape(propertyPath) + @"\s*\r?\n[\s\S]*?(?=\r?\n\s*-\s*target:|\z)",
                RegexOptions.CultureInvariant);
            var match = pattern.Match(yaml);
            return match.Success ? match.Value : null;
        }
    }
}
