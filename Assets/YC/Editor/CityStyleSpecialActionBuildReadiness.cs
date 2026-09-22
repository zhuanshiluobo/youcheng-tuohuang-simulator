using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using YC.Presentation;

namespace YC.Editor
{
    public static class CityStyleSpecialActionBuildReadiness
    {
        public const string CatalogAssetPath =
            "Assets/YC/Presentation/Content/CityStyleSpecialActionCatalog.asset";
        public const string GameSettingsPrefabPath =
            "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab";
        public const string StartScenePath = "Assets/Scenes/StartScene.unity";
        public const string GameScenePath = "Assets/Scenes/SampleScene.unity";

        private static readonly string[] ProductionScenePaths =
        {
            StartScenePath,
            GameScenePath
        };

        public static CityStyleSpecialActionCatalog LoadRequiredCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<CityStyleSpecialActionCatalog>(
                CatalogAssetPath);
            if (catalog == null)
            {
                throw new InvalidOperationException("缺少 CityStyleSpecialActionCatalog 资产。");
            }

            if (!catalog.TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException(
                    "缺少有效 CityStyleSpecialActionCatalog：" + reason);
            }

            var assets = AssetDatabase.LoadAllAssetsAtPath(CatalogAssetPath);
            if (assets.Length != 1 || assets[0] != catalog ||
                !AssetDatabase.Contains(catalog) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    catalog,
                    out var catalogGuid,
                    out long localId) ||
                string.IsNullOrEmpty(catalogGuid) || localId == 0)
            {
                throw new InvalidOperationException(
                    "CityStyleSpecialActionCatalog 必须是唯一持久化主资产。");
            }

            return catalog;
        }

        public static void ValidateReadyForBuild()
        {
            try
            {
                var catalog = LoadRequiredCatalog();
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
                    "CityStyleSpecialAction build readiness 失败：" + exception.Message);
            }
        }

        public static void ValidateGameSettingsPrefab(
            CityStyleSpecialActionCatalog expectedCatalog)
        {
            if (expectedCatalog == null)
            {
                throw new InvalidOperationException("联合目录引用为空。");
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GameSettingsPrefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException("缺少 GameSettings Prefab。");
            }

            if (!prefab.activeSelf)
            {
                throw new InvalidOperationException("GameSettings Prefab 根对象不能处于禁用状态。");
            }

            var contentBootstraps =
                prefab.GetComponentsInChildren<CityStyleSpecialActionCatalogBootstrap>(true);
            if (contentBootstraps.Length != 1 || contentBootstraps[0].gameObject != prefab ||
                !contentBootstraps[0].enabled || contentBootstraps[0].Catalog != expectedCatalog)
            {
                throw new InvalidOperationException(
                    "GameSettings Prefab 的联合目录 Bootstrap 数量、位置或引用无效。");
            }

            if (!contentBootstraps[0].TryValidateConfiguration(out var contentReason))
            {
                throw new InvalidOperationException(
                    "GameSettings Prefab 的联合目录 Bootstrap 接线无效：" + contentReason);
            }

            var facilityCatalog = FacilityCardCatalogEditorAssetBuilder.LoadRequiredCatalog();
            var facilityBootstraps =
                prefab.GetComponentsInChildren<FacilityCatalogBootstrap>(true);
            if (facilityBootstraps.Length != 1 || facilityBootstraps[0].gameObject != prefab ||
                !facilityBootstraps[0].enabled || facilityBootstraps[0].Catalog != facilityCatalog)
            {
                throw new InvalidOperationException(
                    "GameSettings Prefab 的 FacilityCatalogBootstrap 数量、位置或引用无效。");
            }

            var contentOrder = GetExecutionOrder(typeof(CityStyleSpecialActionCatalogBootstrap));
            var facilityOrder = GetExecutionOrder(typeof(FacilityCatalogBootstrap));
            if (contentOrder >= facilityOrder)
            {
                throw new InvalidOperationException(
                    "联合目录 Bootstrap 必须早于 FacilityCatalogBootstrap 执行。");
            }
        }

        internal static void ValidateSavedProductionSceneConnections()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GameSettingsPrefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException("缺少 GameSettings Prefab。");
            }

            var prefabGuid = AssetDatabase.AssetPathToGUID(GameSettingsPrefabPath);
            if (string.IsNullOrEmpty(prefabGuid))
            {
                throw new InvalidOperationException("GameSettings Prefab 缺少 GUID。");
            }

            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    prefab,
                    out var rootGuid,
                    out long rootLocalId) ||
                rootGuid != prefabGuid || rootLocalId == 0)
            {
                throw new InvalidOperationException("无法解析 GameSettings Prefab 根对象标识。");
            }

            var bootstraps =
                prefab.GetComponentsInChildren<CityStyleSpecialActionCatalogBootstrap>(true);
            if (bootstraps.Length != 1 || bootstraps[0].gameObject != prefab ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    bootstraps[0],
                    out var componentGuid,
                    out long componentLocalId) ||
                componentGuid != prefabGuid || componentLocalId == 0)
            {
                throw new InvalidOperationException(
                    "无法解析 GameSettings Prefab 根对象联合目录 Bootstrap 的持久化标识。");
            }

            var catalog = LoadRequiredCatalog();
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    catalog,
                    out var catalogGuid,
                    out long catalogLocalId) ||
                string.IsNullOrEmpty(catalogGuid) || catalogLocalId == 0)
            {
                throw new InvalidOperationException("无法解析联合目录资产标识。");
            }

            var bootstrapScript = MonoScript.FromMonoBehaviour(bootstraps[0]);
            if (bootstrapScript == null ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    bootstrapScript,
                    out var bootstrapScriptGuid,
                    out long bootstrapScriptLocalId) ||
                string.IsNullOrEmpty(bootstrapScriptGuid) || bootstrapScriptLocalId == 0)
            {
                throw new InvalidOperationException("无法解析联合目录 Bootstrap 脚本标识。");
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
                    bootstrapScriptGuid);
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
                "联合目录 Bootstrap", "catalog");
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
