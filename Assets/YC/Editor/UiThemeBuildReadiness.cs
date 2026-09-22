using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using YC.Presentation;

namespace YC.Editor
{
    public static class UiThemeBuildReadiness
    {
        public const string CatalogAssetPath = "Assets/YC/Presentation/Content/UiThemeCatalog.asset";
        public const string GameSettingsPrefabPath =
            "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab";
        internal static readonly string[] ProductionScenePaths =
        {
            "Assets/Scenes/StartScene.unity",
            "Assets/Scenes/SampleScene.unity"
        };

        public static UiThemeCatalog LoadRequiredCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<UiThemeCatalog>(CatalogAssetPath);
            var reason = string.Empty;
            if (catalog == null || !catalog.TryValidateConfiguration(out reason))
            {
                throw new InvalidOperationException("缺少有效的 UiThemeCatalog：" + reason);
            }

            var assets = AssetDatabase.LoadAllAssetsAtPath(CatalogAssetPath);
            if (assets.Length != 1 || assets[0] != catalog || !AssetDatabase.Contains(catalog))
            {
                throw new InvalidOperationException("UiThemeCatalog 必须是唯一持久主资产。");
            }

            return catalog;
        }

        public static UiThemeCatalog RebuildCatalogAsset()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<UiThemeCatalog>(CatalogAssetPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<UiThemeCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogAssetPath);
            }

            catalog.ConfigureCanonicalValuesForEditor();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            return LoadRequiredCatalog();
        }

        public static UiThemeCatalog InitializeRequiredTheme()
        {
            var catalog = LoadRequiredCatalog();
            UiTheme.Initialize(catalog);
            return catalog;
        }

        public static void ValidateReadyForBuild()
        {
            try
            {
                var catalog = LoadRequiredCatalog();
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GameSettingsPrefabPath);
                if (prefab == null || !prefab.activeSelf)
                {
                    throw new InvalidOperationException("GameSettings Prefab 缺失或根对象被禁用。");
                }

                var bootstraps = prefab.GetComponentsInChildren<UiThemeBootstrap>(true);
                var reason = string.Empty;
                if (bootstraps.Length != 1 || bootstraps[0].gameObject != prefab ||
                    !bootstraps[0].enabled || bootstraps[0].Catalog != catalog ||
                    !bootstraps[0].TryValidateConfiguration(out reason))
                {
                    throw new InvalidOperationException("UiThemeBootstrap 数量、位置、启用状态或目录引用无效：" + reason);
                }

                var themeOrder = GetExecutionOrder(typeof(UiThemeBootstrap));
                if (themeOrder >= GetExecutionOrder(typeof(CityStyleSpecialActionCatalogBootstrap)) ||
                    themeOrder >= GetExecutionOrder(typeof(FacilityCatalogBootstrap)))
                {
                    throw new InvalidOperationException("UiThemeBootstrap 必须早于其他内容 bootstrap 执行。");
                }

                ValidateSavedProductionSceneConnections(prefab, catalog, bootstraps[0]);
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new BuildFailedException("UiTheme build readiness 失败：" + exception.Message);
            }
        }

        internal static void ValidateSavedProductionSceneConnections(
            GameObject prefab, UiThemeCatalog catalog, UiThemeBootstrap bootstrap)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(prefab, out var prefabGuid, out long rootId) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(bootstrap, out var componentGuid, out long bootstrapId) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(catalog, out var catalogGuid, out long catalogId) ||
                prefabGuid != componentGuid || rootId == 0 || bootstrapId == 0 || catalogId == 0)
            {
                throw new InvalidOperationException("无法解析 UiTheme prefab 或 catalog 的持久标识。");
            }

            var script = MonoScript.FromMonoBehaviour(bootstrap);
            if (script == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    script, out var scriptGuid, out long scriptId) || scriptId == 0)
            {
                throw new InvalidOperationException("无法解析 UiThemeBootstrap 脚本标识。");
            }

            var enabledScenes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled) enabledScenes.Add(scene.path.Replace('\\', '/'));
            }

            foreach (var scenePath in ProductionScenePaths)
            {
                if (!enabledScenes.Contains(scenePath) || !File.Exists(scenePath))
                {
                    throw new InvalidOperationException("正式场景未启用或缺失：" + scenePath);
                }

                ValidateSavedSceneYaml(scenePath, File.ReadAllText(scenePath), prefabGuid, rootId,
                    bootstrapId, catalogGuid, catalogId, scriptGuid);
            }
        }

        internal static void ValidateSavedSceneYaml(string scenePath, string yaml, string prefabGuid,
            long rootId, long bootstrapId, string catalogGuid, long catalogId, string scriptGuid)
        {
            SavedSceneBootstrapYaml.Validate(
                scenePath, yaml, prefabGuid, rootId, bootstrapId,
                catalogGuid, catalogId, scriptGuid,
                "UiThemeBootstrap", "themeCatalog", requireRemovalSection: false);
        }

        private static int GetExecutionOrder(Type type)
        {
            var attribute = (DefaultExecutionOrder)Attribute.GetCustomAttribute(
                type, typeof(DefaultExecutionOrder));
            if (attribute == null)
            {
                throw new InvalidOperationException(type.Name + " 缺少 DefaultExecutionOrder。");
            }

            return attribute.order;
        }
    }
}
