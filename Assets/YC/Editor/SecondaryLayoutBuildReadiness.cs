using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using YC.Presentation;

namespace YC.Editor
{
    public static class SecondaryLayoutBuildReadiness
    {
        private const string GameplayHudPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        private const string BuildInfoPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/BuildInfoPanel.prefab";
        private const string CharacterHandPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/CharacterHandPanel.prefab";
        private const string CityStylePrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CityStyleDeclarationPreviewDialog.prefab";
        private const string ZoomableViewerPrefabPath =
            "Assets/YC/Presentation/Prefabs/Viewers/ZoomableImageViewer.prefab";

        private static readonly IReadOnlyDictionary<string, int> ExpectedConstructorCounts =
            new Dictionary<string, int>
            {
                { "Assets/YC/Presentation/ActionPanelController.cs", 0 },
                { "Assets/YC/Presentation/PromptPresenter.cs", 2 },
                { "Assets/YC/Presentation/BuildInfoPanel.cs", 2 },
                { "Assets/YC/Presentation/CityStyleDeclarationPreviewDialog.cs", 2 },
                { "Assets/YC/Presentation/CardPointerInteraction.cs", 0 },
                { "Assets/YC/Presentation/CharacterHandPanel.cs", 0 },
                { "Assets/YC/Presentation/ZoomableImageViewerController.cs", 6 }
            };

        public static void ValidateReadyForBuild()
        {
            ValidateManifest();
            var action = SecondaryLayoutEditorAssetBuilder.LoadRequiredActionProfile();
            var card = SecondaryLayoutEditorAssetBuilder.LoadRequiredCardProfile();
            var viewer = SecondaryLayoutEditorAssetBuilder.LoadRequiredZoomableViewerProfile();
            var characterHand = SecondaryLayoutEditorAssetBuilder.LoadRequiredCharacterHandProfile();
            ValidatePrefabReferences(action, card, viewer, characterHand);
            ValidateSceneOverrides();
            ValidateSourceSemantics();
        }

        public static void ValidateManifest()
        {
            if (!File.Exists(SecondaryLayoutEditorAssetBuilder.SourceJsonPath) ||
                AssetDatabase.AssetPathToGUID(SecondaryLayoutEditorAssetBuilder.SourceJsonPath) !=
                SecondaryLayoutEditorAssetBuilder.SourceJsonGuid)
                throw new InvalidOperationException("次级布局 manifest 缺失或 GUID 已改变。");

            if (!string.Equals(
                    SecondaryLayoutEditorAssetBuilder.ComputeCurrentSourceSha256(),
                    SecondaryLayoutEditorAssetBuilder.ExpectedManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("次级布局 manifest SHA-256 与审核锁不一致。");
        }

        public static void ValidatePrefabReferences(
            ActionPanelLayoutProfile action,
            CardInteractionLayoutProfile card,
            ZoomableViewerLayoutProfile viewer,
            CharacterHandLayoutProfile characterHand)
        {
            var hud = LoadPrefab(GameplayHudPrefabPath);
            var actionViews = hud.GetComponentsInChildren<ActionPanelView>(true);
            var registries = hud.GetComponentsInChildren<GameplayDialogRegistry>(true);
            var nestedBuildInfoViews = hud.GetComponentsInChildren<BuildInfoPanelView>(true);
            var nestedCharacterHandPanels = hud.GetComponentsInChildren<CharacterHandPanel>(true);
            var actionReason = string.Empty;
            if (actionViews.Length != 1 || actionViews[0].LayoutProfile != action ||
                !actionViews[0].TryValidateConfiguration(out actionReason))
                throw new InvalidOperationException("Gameplay HUD 的 ActionPanel Profile 引用无效：" + actionReason);
            var registryReason = string.Empty;
            if (registries.Length != 1 || registries[0].CardInteractionLayoutProfile != card ||
                !registries[0].TryValidateConfiguration(out registryReason))
                throw new InvalidOperationException("Gameplay HUD 的卡牌交互 Registry 引用无效：" + registryReason);
            if (nestedBuildInfoViews.Length != 1 ||
                nestedBuildInfoViews[0].CardInteractionLayoutProfile != card)
                throw new InvalidOperationException("Gameplay HUD 的嵌套建设面板未共享 CardInteractionLayoutProfile。");
            var handReason = string.Empty;
            if (nestedCharacterHandPanels.Length != 1 ||
                nestedCharacterHandPanels[0].LayoutProfile != characterHand ||
                !nestedCharacterHandPanels[0].TryValidateConfiguration(out handReason))
                throw new InvalidOperationException("Gameplay HUD 的嵌套手牌面板引用无效：" + handReason);

            var standaloneHand = LoadPrefab(CharacterHandPrefabPath).GetComponent<CharacterHandPanel>();
            handReason = string.Empty;
            if (standaloneHand == null || standaloneHand.LayoutProfile != characterHand ||
                !standaloneHand.TryValidateConfiguration(out handReason))
                throw new InvalidOperationException("CharacterHandPanel Profile 引用无效：" + handReason);

            var buildInfoView = LoadPrefab(BuildInfoPrefabPath).GetComponent<BuildInfoPanelView>();
            var buildInfoReason = string.Empty;
            if (buildInfoView == null || buildInfoView.CardInteractionLayoutProfile != card ||
                !buildInfoView.TryValidateConfiguration(out buildInfoReason))
                throw new InvalidOperationException("BuildInfoPanel Profile 引用无效：" + buildInfoReason);

            var cityStyleView = LoadPrefab(CityStylePrefabPath)
                .GetComponent<CityStyleDeclarationPreviewView>();
            var cityStyleReason = string.Empty;
            if (cityStyleView == null || cityStyleView.CardInteractionLayoutProfile != card ||
                !cityStyleView.TryValidateConfiguration(out cityStyleReason))
                throw new InvalidOperationException("CityStyleDeclarationPreview Profile 引用无效：" + cityStyleReason);

            var zoomableView = LoadPrefab(ZoomableViewerPrefabPath)
                .GetComponentInChildren<ZoomableImageViewerView>(true);
            var viewerReason = string.Empty;
            if (zoomableView == null || zoomableView.LayoutProfile != viewer ||
                !zoomableView.TryValidateConfiguration(out viewerReason))
                throw new InvalidOperationException("ZoomableImageViewer Profile 引用无效：" + viewerReason);
        }

        public static void ValidateSourceSemantics()
        {
            foreach (var pair in ExpectedConstructorCounts)
            {
                var count = CountVector2Constructors(pair.Key);
                if (count != pair.Value)
                    throw new InvalidOperationException(
                        pair.Key + " 的 new Vector2 构造数应为 " + pair.Value + "，实际为 " + count + "。");
            }

            var presentationRoot = Path.GetFullPath("Assets/YC/Presentation");
            var total = 0;
            foreach (var path in Directory.GetFiles(
                         presentationRoot,
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(path);
                total += Regex.Matches(source, @"\bnew\s+Vector2\s*\(").Count;
                if (source.Contains("GameplayInteractionLayoutProfile"))
                    throw new InvalidOperationException(path + " 不得恢复已撤销的 GameplayInteractionLayoutProfile。");
            }

            if (total != 76)
                throw new InvalidOperationException("Presentation 全局 new Vector2 构造数应为 76，实际为 " + total + "。");

            AssertNoRuntimeFallback("Assets/YC/Presentation/ActionPanelController.cs");
            AssertNoRuntimeFallback("Assets/YC/Presentation/BuildInfoPanel.cs");
            AssertNoRuntimeFallback("Assets/YC/Presentation/CityStyleDeclarationPreviewDialog.cs");
            AssertNoRuntimeFallback("Assets/YC/Presentation/CardPointerInteraction.cs");
            AssertNoRuntimeFallback("Assets/YC/Presentation/CharacterHandPanel.cs");
            AssertNoRuntimeFallback("Assets/YC/Presentation/ZoomableImageViewerController.cs");
            ValidateSharedDragGhostConsumers();
        }

        private static void ValidateSceneOverrides()
        {
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (!scene.enabled || !File.Exists(scene.path)) continue;
                var yaml = File.ReadAllText(scene.path);
                if (Regex.IsMatch(
                        yaml,
                        @"propertyPath:\s*(cardInteractionLayoutProfile|layoutProfile)\s*$",
                        RegexOptions.Multiline | RegexOptions.CultureInvariant))
                    throw new InvalidOperationException(scene.path + " 不得覆盖次级布局 Profile 的 Prefab 引用。");
            }
        }

        private static void AssertNoRuntimeFallback(string path)
        {
            var source = File.ReadAllText(path);
            if (Regex.IsMatch(
                    source,
                    @"Resources\s*\.\s*Load[^;]*(LayoutProfile|secondary_layout)|" +
                    @"FindObjects?OfType[^;]*LayoutProfile",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidOperationException(path + " 不得通过运行时发现或全局查询取得次级布局 Profile。");
        }

        private static void ValidateSharedDragGhostConsumers()
        {
            var utility = File.ReadAllText("Assets/YC/Presentation/CardPointerInteraction.cs");
            var buildInfo = File.ReadAllText("Assets/YC/Presentation/BuildInfoPanel.cs");
            var cityStyle = File.ReadAllText("Assets/YC/Presentation/CityStyleDeclarationPreviewDialog.cs");
            var facilityEffect = File.ReadAllText("Assets/YC/Presentation/FacilityEffectChoiceDialog.cs");
            if (!utility.Contains("CardDragGhostLayout layout") ||
                !buildInfo.Contains("CardInteractionLayoutProfile.DragGhostLayout.RootLayout.ApplyTo") ||
                !cityStyle.Contains("CardInteractionLayoutProfile.DragGhostLayout") ||
                !facilityEffect.Contains("cardInteractionLayoutProfile.DragGhostLayout"))
                throw new InvalidOperationException(
                    "BuildInfo、CityStyle 与 FacilityEffectChoiceDialog 必须共享 CardDragGhostLayout。");
        }

        private static int CountVector2Constructors(string path)
        {
            return Regex.Matches(File.ReadAllText(path), @"\bnew\s+Vector2\s*\(").Count;
        }

        private static GameObject LoadPrefab(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new InvalidOperationException("缺少 Prefab：" + path);
            return prefab;
        }
    }

    internal sealed class SecondaryLayoutBuildPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -780;

        public void OnPreprocessBuild(BuildReport report)
        {
            SecondaryLayoutBuildReadiness.ValidateReadyForBuild();
        }
    }
}
