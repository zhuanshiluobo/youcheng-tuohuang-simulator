using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using YC.Presentation;

namespace YC.Editor
{
    public static class EventCharacterBuildReadiness
    {
        public const string StartScenePath = "Assets/Scenes/StartScene.unity";
        public const string GameScenePath = "Assets/Scenes/SampleScene.unity";

        private static readonly string[] ProductionScenePaths =
        {
            StartScenePath,
            GameScenePath
        };

        public static void ValidateReadyForBuild()
        {
            try
            {
                var catalog = EventCharacterCardCatalogEditorAssetBuilder.LoadRequiredCatalog();
                if (!EventCharacterCardCatalogEditorAssetBuilder.MatchesSource(catalog))
                {
                    throw new InvalidOperationException(
                        "EventCharacterCardCatalog 的 SHA 或字段与 manifest 不一致。");
                }

                ValidateGameSettingsPrefab(catalog);
                ValidateSavedProductionSceneConnections();
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new BuildFailedException(
                    "Event/Character build readiness 失败：" + exception.Message);
            }
        }

        public static void ValidateGameSettingsPrefab(EventCharacterCardCatalog expectedCatalog)
        {
            if (expectedCatalog == null)
            {
                throw new InvalidOperationException("Event/Character 目录引用为空。");
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                EventCharacterCardCatalogEditorAssetBuilder.GameSettingsPrefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException("缺少 GameSettings Prefab。");
            }

            if (!prefab.activeSelf)
            {
                throw new InvalidOperationException("GameSettings Prefab 根对象不能处于禁用状态。");
            }

            var bootstraps =
                prefab.GetComponentsInChildren<EventCharacterCatalogBootstrap>(true);
            if (bootstraps.Length != 1 || bootstraps[0].gameObject != prefab ||
                !bootstraps[0].enabled || bootstraps[0].Catalog != expectedCatalog)
            {
                throw new InvalidOperationException(
                    "GameSettings Prefab 的 EventCharacterCatalogBootstrap 数量、位置、" +
                    "启用状态或引用无效。");
            }

            if (!bootstraps[0].TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException(
                    "GameSettings Prefab 的 EventCharacterCatalogBootstrap 接线无效：" + reason);
            }

            var eventOrder = GetExecutionOrder(typeof(EventCharacterCatalogBootstrap));
            var contentOrder = GetExecutionOrder(typeof(CityStyleSpecialActionCatalogBootstrap));
            var facilityOrder = GetExecutionOrder(typeof(FacilityCatalogBootstrap));
            if (eventOrder >= contentOrder || eventOrder >= facilityOrder)
            {
                throw new InvalidOperationException(
                    "EventCharacterCatalogBootstrap 必须早于其他内容目录 Bootstrap 执行。");
            }
        }

        internal static void ValidateSavedProductionSceneConnections()
        {
            var prefabPath = EventCharacterCardCatalogEditorAssetBuilder.GameSettingsPrefabPath;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException("缺少 GameSettings Prefab。");
            }

            var prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
            if (string.IsNullOrEmpty(prefabGuid) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    prefab,
                    out var rootGuid,
                    out long rootLocalId) ||
                rootGuid != prefabGuid || rootLocalId == 0)
            {
                throw new InvalidOperationException("无法解析 GameSettings Prefab 根对象标识。");
            }

            var bootstraps =
                prefab.GetComponentsInChildren<EventCharacterCatalogBootstrap>(true);
            if (bootstraps.Length != 1 || bootstraps[0].gameObject != prefab ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    bootstraps[0],
                    out var componentGuid,
                    out long componentLocalId) ||
                componentGuid != prefabGuid || componentLocalId == 0)
            {
                throw new InvalidOperationException(
                    "无法解析根对象 EventCharacterCatalogBootstrap 的持久化标识。");
            }

            var catalog = EventCharacterCardCatalogEditorAssetBuilder.LoadRequiredCatalog();
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    catalog,
                    out var catalogGuid,
                    out long catalogLocalId) ||
                string.IsNullOrEmpty(catalogGuid) || catalogLocalId == 0)
            {
                throw new InvalidOperationException("无法解析 Event/Character 目录资产标识。");
            }

            var script = MonoScript.FromMonoBehaviour(bootstraps[0]);
            if (script == null ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    script,
                    out var scriptGuid,
                    out long scriptLocalId) ||
                string.IsNullOrEmpty(scriptGuid) || scriptLocalId == 0)
            {
                throw new InvalidOperationException(
                    "无法解析 EventCharacterCatalogBootstrap 脚本标识。");
            }

            var enabledScenePaths = new HashSet<string>(StringComparer.Ordinal);
            var buildScenes = EditorBuildSettings.scenes;
            for (var i = 0; i < buildScenes.Length; i++)
            {
                if (buildScenes[i].enabled)
                {
                    enabledScenePaths.Add(NormalizePath(buildScenes[i].path));
                }
            }

            for (var i = 0; i < ProductionScenePaths.Length; i++)
            {
                var scenePath = NormalizePath(ProductionScenePaths[i]);
                if (!enabledScenePaths.Contains(scenePath))
                {
                    throw new InvalidOperationException(
                        scenePath + " 未位于启用的 EditorBuildSettings 场景中。");
                }

                if (!File.Exists(scenePath))
                {
                    throw new InvalidOperationException("缺少正式场景：" + scenePath);
                }

                ValidateSavedSceneYaml(
                    scenePath,
                    File.ReadAllText(scenePath),
                    prefabGuid,
                    rootLocalId,
                    componentLocalId,
                    catalogGuid,
                    catalogLocalId,
                    scriptGuid);
            }
        }

        internal static void ValidateSavedSceneYaml(
            string scenePath,
            string yaml,
            string prefabGuid,
            long rootLocalId,
            long bootstrapLocalId,
            string catalogGuid,
            long catalogLocalId,
            string bootstrapScriptGuid)
        {
            if (string.IsNullOrEmpty(yaml))
            {
                throw new InvalidOperationException(scenePath + " 的场景 YAML 为空。");
            }

            var blocks = Regex.Matches(
                yaml,
                @"^--- !u!1001 &.*?(?=^--- !u!|\z)",
                RegexOptions.Multiline | RegexOptions.Singleline);
            var sourceToken =
                "m_SourcePrefab: {fileID: 100100000, guid: " + prefabGuid + ", type: 3}";
            var matchingBlocks = new List<string>();
            for (var i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].Value.Contains(sourceToken))
                {
                    matchingBlocks.Add(blocks[i].Value);
                }
            }

            if (matchingBlocks.Count != 1)
            {
                throw new InvalidOperationException(
                    scenePath + " 必须恰好连接一个 GameSettings Prefab 实例，实际 " +
                    matchingBlocks.Count + " 个。");
            }

            var directScriptPattern =
                @"m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*" +
                Regex.Escape(bootstrapScriptGuid) + @",\s*type:\s*3\}";
            if (Regex.IsMatch(yaml, directScriptPattern, RegexOptions.IgnoreCase))
            {
                throw new InvalidOperationException(
                    scenePath + " 包含额外的本地 EventCharacterCatalogBootstrap 组件。");
            }

            var block = matchingBlocks[0];
            var removedStart = block.IndexOf("m_RemovedComponents:", StringComparison.Ordinal);
            var removedEnd = block.IndexOf("m_RemovedGameObjects:", StringComparison.Ordinal);
            if (removedStart < 0 || removedEnd <= removedStart)
            {
                throw new InvalidOperationException(
                    scenePath + " 的 GameSettings Prefab override 结构无效。");
            }

            var removedSection = block.Substring(removedStart, removedEnd - removedStart);
            if (removedSection.Contains("fileID: " + bootstrapLocalId) &&
                removedSection.Contains("guid: " + prefabGuid))
            {
                throw new InvalidOperationException(
                    scenePath + " 删除了 EventCharacterCatalogBootstrap 组件。");
            }

            RejectDisabledOverride(
                scenePath,
                block,
                prefabGuid,
                rootLocalId,
                "m_IsActive",
                "GameSettings Prefab 根对象");
            RejectDisabledOverride(
                scenePath,
                block,
                prefabGuid,
                bootstrapLocalId,
                "m_Enabled",
                "EventCharacterCatalogBootstrap");

            var catalogOverrides = FindPropertyOverrides(
                block,
                prefabGuid,
                bootstrapLocalId,
                "catalog");
            for (var i = 0; i < catalogOverrides.Count; i++)
            {
                var referenceId = long.Parse(catalogOverrides[i].Groups["referenceId"].Value);
                var referenceGuid = catalogOverrides[i].Groups["referenceGuid"].Value;
                if (referenceId != catalogLocalId ||
                    !string.Equals(referenceGuid, catalogGuid, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        scenePath + " 覆盖了 EventCharacterCatalogBootstrap 的 catalog 引用。");
                }
            }
        }

        private static void RejectDisabledOverride(
            string scenePath,
            string block,
            string prefabGuid,
            long targetLocalId,
            string propertyPath,
            string label)
        {
            var overrides = FindPropertyOverrides(
                block,
                prefabGuid,
                targetLocalId,
                propertyPath);
            for (var i = 0; i < overrides.Count; i++)
            {
                if (overrides[i].Groups["value"].Value.Trim() == "0")
                {
                    throw new InvalidOperationException(
                        scenePath + " 通过 override 禁用了" + label + "。");
                }
            }
        }

        private static MatchCollection FindPropertyOverrides(
            string block,
            string prefabGuid,
            long targetLocalId,
            string propertyPath)
        {
            var pattern =
                @"(?m)^\s*-\s+target:\s+\{fileID:\s*" + targetLocalId +
                @",\s*guid:\s*" + Regex.Escape(prefabGuid) +
                @",\s*type:\s*\d+\}\r?\n" +
                @"^\s*propertyPath:\s*" + Regex.Escape(propertyPath) + @"\s*\r?\n" +
                @"^\s*value:[ \t]*(?<value>[^\r\n]*)\r?\n" +
                @"^\s*objectReference:\s*\{fileID:\s*(?<referenceId>-?\d+)" +
                @"(?:,\s*guid:\s*(?<referenceGuid>[0-9a-f]+),\s*type:\s*\d+)?\}";
            return Regex.Matches(block, pattern, RegexOptions.IgnoreCase);
        }

        private static int GetExecutionOrder(Type componentType)
        {
            var attribute = (DefaultExecutionOrder)Attribute.GetCustomAttribute(
                componentType,
                typeof(DefaultExecutionOrder));
            if (attribute == null)
            {
                throw new InvalidOperationException(
                    componentType.Name + " 缺少 DefaultExecutionOrder。");
            }

            return attribute.order;
        }

        private static string NormalizePath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }
    }
}
