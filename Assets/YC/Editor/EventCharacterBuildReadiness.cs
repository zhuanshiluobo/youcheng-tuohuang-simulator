using System;
using System.Collections.Generic;
using System.IO;
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
            SavedSceneBootstrapYaml.Validate(
                scenePath, yaml, prefabGuid, rootLocalId, bootstrapLocalId,
                catalogGuid, catalogLocalId, bootstrapScriptGuid,
                "EventCharacterCatalogBootstrap", "catalog");
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
