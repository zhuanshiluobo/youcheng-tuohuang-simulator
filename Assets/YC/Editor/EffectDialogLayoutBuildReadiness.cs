using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YC.Presentation;

namespace YC.Editor
{
    public static class EffectDialogLayoutBuildReadiness
    {
        public const string EffectDialogShellPrefabGuid =
            YC.EditorTools.GameplayDialogEditorAssetBuilder.EffectDialogShellPrefabGuid;
        public const string GameplayHudPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        public const string SampleScenePath = "Assets/Scenes/SampleScene.unity";

        public static void ValidateReadyForBuild()
        {
            var source = EffectDialogLayoutEditorAssetBuilder.ParseSource();
            var profile = EffectDialogLayoutEditorAssetBuilder.LoadRequiredEffectProfile();
            if (!profile.MatchesValuesForEditor(source.EffectDialog))
                throw new InvalidOperationException("效果对话框布局 Profile 与锁定 manifest 不一致。");

            AssertControlledAssets();
            ValidateEffectPrefab(profile);
            ValidateProductionDependencies(profile);
            ValidateSourceSemantics();
        }

        internal static void ValidateSourceSemantics()
        {
            var shellSource = ReadSource("Assets/YC/Presentation/EffectDialogShellView.cs");
            var primitivesSource = ReadSource("Assets/YC/Presentation/EffectDialogPrimitives.cs");
            var facilitySource = ReadSource("Assets/YC/Presentation/FacilityEffectChoiceDialog.cs");
            var characterSource = ReadSource("Assets/YC/Presentation/CharacterCardEffectChoiceDialog.cs");
            var specialSource = ReadSource("Assets/YC/Presentation/SpecialActionChoiceDialog.cs");
            if (!shellSource.Contains("[SerializeField] private EffectDialogLayoutProfile layoutProfile;") ||
                !primitivesSource.Contains("EffectDialogCollapseSpec(EffectDialogLayoutProfile layoutProfile)") ||
                !facilitySource.Contains("private readonly EffectDialogLayoutProfile layoutProfile;") ||
                !characterSource.Contains("private readonly EffectDialogLayoutProfile layoutProfile;") ||
                !specialSource.Contains("private readonly EffectDialogLayoutProfile layoutProfile;") ||
                HasRuntimeLookupFallback(shellSource) || HasRuntimeLookupFallback(primitivesSource) ||
                HasRuntimeLookupFallback(facilitySource) || HasRuntimeLookupFallback(characterSource) ||
                HasRuntimeLookupFallback(specialSource))
            {
                throw new InvalidOperationException("Effect 消费端不满足显式 Profile 注入与无运行时查找合同。");
            }
        }

        private static void ValidateEffectPrefab(EffectDialogLayoutProfile profile)
        {
            var path = YC.EditorTools.GameplayDialogEditorAssetBuilder.EffectDialogShellPrefabPath;
            EffectDialogLayoutControlledMetaGuid.ValidateAsset(path, EffectDialogShellPrefabGuid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new InvalidOperationException("EffectDialogShell Prefab 缺失。");
            ValidateEffectPrefabContents(prefab, profile);
            AssertNoMissingScripts(path);
            var dependencies = AssetDatabase.GetDependencies(path, true);
            if (dependencies.Count(item => item == EffectDialogLayoutEditorAssetBuilder.EffectProfileAssetPath) != 1)
                throw new InvalidOperationException("EffectDialogShell 必须依赖唯一锁定布局 Profile。");
        }

        internal static void ValidateEffectPrefabContents(GameObject prefab, EffectDialogLayoutProfile profile)
        {
            var reason = string.Empty;
            var view = prefab == null ? null : prefab.GetComponent<EffectDialogShellView>();
            if (view == null || view.LayoutProfile != profile || !view.TryValidateConfiguration(out reason))
                throw new InvalidOperationException("EffectDialogShell View/Profile 配置无效：" + reason);
            if (view.OptionRowTemplate.gameObject.activeSelf || view.ResourceRowTemplate.gameObject.activeSelf ||
                view.FacilityCardTemplate.gameObject.activeSelf)
                throw new InvalidOperationException("EffectDialogShell 动态模板必须默认禁用。");
            if (view.Panel == null || view.Panel.sizeDelta != profile.PanelLayout.SizeDelta)
                throw new InvalidOperationException("EffectDialogShell 面板尺寸未匹配布局 Profile。");
            var toggleRect = view.CollapseButton.GetComponent<RectTransform>();
            if (toggleRect.sizeDelta != profile.ExpandedToggleLayout.SizeDelta ||
                toggleRect.anchoredPosition != profile.ExpandedToggleLayout.AnchoredPosition)
                throw new InvalidOperationException("EffectDialogShell 收起按钮未匹配布局 Profile。");
            var serialized = new SerializedObject(view);
            var actions = serialized.FindProperty("actionButtons");
            for (var i = 0; i < actions.arraySize; i++)
            for (var other = 0; other < i; other++)
            {
                if (actions.GetArrayElementAtIndex(i).objectReferenceValue ==
                    actions.GetArrayElementAtIndex(other).objectReferenceValue)
                    throw new InvalidOperationException("EffectDialogShell 动作按钮引用必须互不相同。");
            }
        }

        private static void ValidateProductionDependencies(EffectDialogLayoutProfile profile)
        {
            var hud = AssetDatabase.LoadAssetAtPath<GameObject>(GameplayHudPrefabPath);
            var registry = hud == null ? null : hud.GetComponentInChildren<GameplayDialogRegistry>(true);
            if (registry == null || registry.EffectDialogShellPrefab == null ||
                registry.EffectDialogShellPrefab.LayoutProfile != profile)
                throw new InvalidOperationException("生产 Gameplay HUD Registry 未引用锁定 Effect Prefab/Profile。");

            var sceneDependencies = AssetDatabase.GetDependencies(SampleScenePath, true);
            if (!sceneDependencies.Contains(GameplayHudPrefabPath) ||
                !sceneDependencies.Contains(EffectDialogLayoutEditorAssetBuilder.EffectProfileAssetPath))
                throw new InvalidOperationException("SampleScene 生产依赖链未包含 HUD 与效果对话框布局 Profile。");
        }

        private static void AssertControlledAssets()
        {
            EffectDialogLayoutControlledMetaGuid.ValidateAsset(
                EffectDialogLayoutEditorAssetBuilder.SourceJsonPath,
                EffectDialogLayoutEditorAssetBuilder.SourceJsonGuid);
            EffectDialogLayoutControlledMetaGuid.ValidateAsset(
                EffectDialogLayoutEditorAssetBuilder.EffectProfileAssetPath,
                EffectDialogLayoutEditorAssetBuilder.EffectProfileAssetGuid);
            EffectDialogLayoutControlledMetaGuid.ValidateAsset(
                YC.EditorTools.GameplayDialogEditorAssetBuilder.EffectDialogShellPrefabPath,
                EffectDialogShellPrefabGuid);
            var matches = AssetDatabase.FindAssets("t:EffectDialogLayoutProfile");
            if (matches.Length != 1 ||
                !string.Equals(matches[0], EffectDialogLayoutEditorAssetBuilder.EffectProfileAssetGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("EffectDialogLayoutProfile 必须且只能存在一个固定 GUID 主资产。");
        }

        private static void AssertNoMissingScripts(string path)
        {
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var transforms = contents.GetComponentsInChildren<Transform>(true);
                for (var i = 0; i < transforms.Length; i++)
                {
                    if (transforms[i].GetComponents<Component>().Any(component => component == null))
                        throw new InvalidOperationException(path + " 在 " + transforms[i].name + " 含 Missing Script。");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private static string ReadSource(string path)
        {
            return File.ReadAllText(Path.GetFullPath(path));
        }

        private static bool HasRuntimeLookupFallback(string source)
        {
            return source.Contains("Resources.Load") || source.Contains("FindObjectOfType") ||
                   source.Contains("FindFirstObjectByType") || source.Contains("FindAnyObjectByType") ||
                   source.Contains("GameObject.Find(");
        }
    }
}
