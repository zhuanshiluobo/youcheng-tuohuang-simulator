using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation;

namespace YC.Editor
{
    public static class EffectDialogLayoutBuildReadiness
    {
        internal const string EffectDialogPrimitivesSha256 =
            "773DD745669CC0B20E14C89BC718437487B8556292C34D1C839EF4C942F4A938";
        internal const string FacilityEffectChoiceDialogSha256 =
            "12E63BBC0DE44826062B348B540C4947E3D518CF72E74A358F4529C09D9ACFE9";
        internal const string ExpandableInfoPanelSha256 =
            "DD49B5FCDD4ACA83C18CF552AEDF4F03EE676A874B78BD406FB3146A1C956C61";
        internal const string EffectDialogShellViewSha256 =
            "6517FCEAA3B0FBC0949EE8083257D7A04C410A969628A6D769FDC0CBB0B05905";
        internal const string EffectDialogLayoutProfileSha256 =
            "245E5B9A5A714D3858DAB2C18237FE61EBCADE238A13B0077514B9C4F18E3984";
        internal const string ExpandableInfoPanelLayoutProfileSha256 =
            "29C387F6098684646E371C5094353F26D0323F8FEFECBECC679739A4E6723597";
        internal const string GameplayDialogRegistrySha256 =
            "1CFC3E900520B00F38C51D761D40E7DE97F0E6F171328E2BE94057D5BC1DA5B4";
        internal const string CharacterCardEffectChoiceDialogSha256 =
            "39384175068DD57DFBEBFAC1DFFF8B125B665D131B99143C902A263DB27B122C";
        internal const string SpecialActionChoiceDialogSha256 =
            "2C07C101051DD969442320C7A495672EC613CD1F6A64B73CECE4BAC85CDCF419";
        public const string EffectDialogShellPrefabGuid =
            YC.EditorTools.GameplayDialogEditorAssetBuilder.EffectDialogShellPrefabGuid;
        public const string ExpandableInfoPanelPrefabGuid =
            "b57f7a980c01fa84c93597e4131f169c";
        public const string GameplayHudPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        public const string SampleScenePath = "Assets/Scenes/SampleScene.unity";

        private const string EffectDialogPrimitivesPath =
            "Assets/YC/Presentation/EffectDialogPrimitives.cs";
        private const string FacilityEffectChoiceDialogPath =
            "Assets/YC/Presentation/FacilityEffectChoiceDialog.cs";
        private const string ExpandableInfoPanelPath =
            "Assets/YC/Presentation/ExpandableInfoPanel.cs";
        private const string EffectDialogShellViewPath =
            "Assets/YC/Presentation/EffectDialogShellView.cs";
        private const string GameplayDialogRegistryPath =
            "Assets/YC/Presentation/GameplayDialogRegistry.cs";
        private const string EventChoiceDialogPath =
            "Assets/YC/Presentation/EventChoiceDialog.cs";
        private const string CharacterCardEffectChoiceDialogPath =
            "Assets/YC/Presentation/CharacterCardEffectChoiceDialog.cs";
        private const string SpecialActionChoiceDialogPath =
            "Assets/YC/Presentation/SpecialActionChoiceDialog.cs";

        public static void ValidateReadyForBuild()
        {
            var source = EffectDialogLayoutEditorAssetBuilder.ParseSource();
            var effectProfile =
                EffectDialogLayoutEditorAssetBuilder.LoadRequiredEffectProfile();
            var expandableProfile =
                EffectDialogLayoutEditorAssetBuilder.LoadRequiredExpandableProfile();
            if (!effectProfile.MatchesValuesForEditor(source.EffectDialog) ||
                !expandableProfile.MatchesValuesForEditor(source.ExpandableInfoPanel))
            {
                throw new InvalidOperationException("布局 Profile 与锁定 manifest 不一致。");
            }

            AssertControlledAssets();
            ValidateEffectPrefab(effectProfile);
            ValidateExpandablePrefab(expandableProfile);
            ValidateProductionDependencies(effectProfile, expandableProfile);
            ValidateSourceSemantics();
        }

        internal static void ValidateEffectPrefab(EffectDialogLayoutProfile profile)
        {
            var path = YC.EditorTools.GameplayDialogEditorAssetBuilder.EffectDialogShellPrefabPath;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null ||
                !string.Equals(
                    AssetDatabase.AssetPathToGUID(path),
                    EffectDialogShellPrefabGuid,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("EffectDialogShell Prefab 缺失或 GUID 漂移。");
            }

            ValidateEffectPrefabContents(prefab, profile);
            AssertSingleProfileDependency(path, EffectDialogLayoutEditorAssetBuilder.EffectProfileAssetPath);
            AssertNoMissingScripts(path);
        }

        internal static void ValidateEffectPrefabContents(
            GameObject prefab,
            EffectDialogLayoutProfile profile)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            var view = prefab.GetComponent<EffectDialogShellView>();
            var reason = string.Empty;
            if (view == null || !view.enabled || !view.gameObject.activeSelf ||
                !view.TryValidateConfiguration(out reason) ||
                view.LayoutProfile != profile || !EditorUtility.IsPersistent(view.LayoutProfile))
            {
                throw new InvalidOperationException(
                    "EffectDialogShell View/Profile 配置无效：" + reason);
            }

            AssertExactComponents(
                prefab,
                typeof(RectTransform),
                typeof(Canvas),
                typeof(GraphicRaycaster),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(EffectDialogShellView));
            AssertExactComponents(
                view.Panel.gameObject,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Outline),
                typeof(EffectDialogCollapsiblePanel));
            AssertDirectParent(view.Panel, prefab.transform, "Effect Panel");
            AssertDirectParent(view.ExpandedContent, view.Panel, "Expanded Content");
            AssertRect(view.Panel, profile.PanelLayout, "Effect Panel");
            AssertRect(view.TitleText.rectTransform, profile.TitleLayout, "Effect Title");
            AssertRect(
                view.DescriptionText.rectTransform,
                profile.DescriptionLayout,
                "Effect Description");
            AssertRect(
                view.CollapsedSummaryText.rectTransform,
                profile.CollapsedSummaryLayout,
                "Collapsed Summary");
            AssertRect(
                view.CollapseButton.GetComponent<RectTransform>(),
                profile.ExpandedToggleLayout,
                "Collapse Toggle");
            AssertRect(
                view.CollapseButtonIcon.rectTransform,
                profile.CollapseIconLayout,
                "Collapse Icon");
            AssertInset(
                view.OptionScroll.GetComponent<RectTransform>(),
                profile.OptionScrollLayout,
                "Option Scroll");
            AssertRect(
                view.ResourceSummaryText.rectTransform,
                profile.ResourceSummaryLayout,
                "Resource Summary");

            if (!view.CollapseButton.enabled || !view.CollapseButtonIcon.enabled ||
                !view.CollapseButtonIcon.gameObject.activeSelf ||
                !view.CollapsiblePanel.enabled ||
                view.OptionRowTemplate.gameObject.activeSelf ||
                view.ResourceRowTemplate.gameObject.activeSelf ||
                view.FacilityCardTemplate.gameObject.activeSelf ||
                view.OptionScroll.gameObject.activeSelf ||
                view.CollapsedSummaryText.gameObject.activeSelf ||
                view.CollapseButton.gameObject.activeSelf)
            {
                throw new InvalidOperationException(
                    "EffectDialogShell 固定模板与折叠控件必须在 Prefab 中默认禁用。");
            }

            if (!Approximately(
                    view.FacilityCardTemplate.CardRect.sizeDelta,
                    profile.ExtensionHubCardSize) ||
                !Approximately(
                    view.FacilityCardTemplate.Outline.effectDistance,
                    profile.FacilityCardOutlineDistance))
            {
                throw new InvalidOperationException("设施卡模板尺寸或 Outline 漂移。");
            }

            var serialized = new SerializedObject(view);
            var actions = serialized.FindProperty("actionButtons");
            if (actions == null || actions.arraySize != 3)
            {
                throw new InvalidOperationException("EffectDialogShell 必须保留三个固定动作按钮槽位。");
            }

            for (var i = 0; i < actions.arraySize; i++)
            {
                var action = actions.GetArrayElementAtIndex(i).objectReferenceValue as
                    EffectDialogActionButtonView;
                if (action == null || action.gameObject.activeSelf ||
                    !action.TryValidateConfiguration(out reason) ||
                    !Approximately(
                        action.GetComponent<RectTransform>().sizeDelta,
                        profile.ActionButtonSize))
                {
                    throw new InvalidOperationException("Effect 动作按钮槽位配置无效：" + reason);
                }

                for (var other = 0; other < i; other++)
                {
                    if (actions.GetArrayElementAtIndex(other).objectReferenceValue == action)
                    {
                        throw new InvalidOperationException("Effect 动作按钮槽位必须互不相同。");
                    }
                }
            }

        }

        internal static void ValidateExpandablePrefab(
            ExpandableInfoPanelLayoutProfile profile)
        {
            var path = YC.EditorTools.ExpandableInfoPanelEditorAssetBuilder.PrefabPath;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null ||
                !string.Equals(
                    AssetDatabase.AssetPathToGUID(path),
                    ExpandableInfoPanelPrefabGuid,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "ExpandableInfoPanel Prefab 缺失或 GUID 漂移。");
            }

            ValidateExpandablePrefabContents(prefab, profile);
            AssertSingleProfileDependency(
                path,
                EffectDialogLayoutEditorAssetBuilder.ExpandableProfileAssetPath);
            AssertNoMissingScripts(path);
        }

        internal static void ValidateExpandablePrefabContents(
            GameObject prefab,
            ExpandableInfoPanelLayoutProfile profile)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            var controller = prefab.GetComponent<ExpandableInfoPanel>();
            var view = prefab.GetComponent<ExpandableInfoPanelView>();
            var reason = string.Empty;
            if (controller == null || view == null || !controller.enabled || !view.enabled ||
                !prefab.activeSelf || controller.LayoutProfile != profile ||
                controller.View != view || view.Controller != controller ||
                !controller.TryValidateConfiguration(out reason))
            {
                throw new InvalidOperationException(
                    "ExpandableInfoPanel Controller/View/Profile 配置无效：" + reason);
            }

            AssertExactComponents(
                prefab,
                typeof(RectTransform),
                typeof(ExpandableInfoPanel),
                typeof(ExpandableInfoPanelView));
            AssertDirectParent(view.PanelTransform, prefab.transform, "Sidebar Panel");
            AssertDirectParent(
                view.ContentArea,
                view.PanelTransform,
                "Expandable Content Area");
            AssertDirectParent(
                view.ToggleButton.GetComponent<RectTransform>(),
                view.PanelTransform,
                "Expandable Toggle");
            AssertRect(view.PanelTransform, profile.PanelLayout, "Expandable Panel");
            AssertRect(
                view.ToggleButton.GetComponent<RectTransform>(),
                profile.ToggleLayout,
                "Expandable Toggle");
            AssertInset(view.ContentArea, profile.ContentAreaLayout, "Expandable Content Area");

            var templates = new[]
            {
                view.ModuleTemplate,
                view.RowTemplate,
                view.ImagePreviewTemplate,
                view.CardStripTemplate,
                view.CardThumbnailTemplate,
                view.DragGhostTemplate
            };
            for (var i = 0; i < templates.Length; i++)
            {
                if (templates[i] == null || templates[i].gameObject.activeSelf)
                {
                    throw new InvalidOperationException(
                        "ExpandableInfoPanel 动态模板必须完整且默认禁用。");
                }

                for (var other = 0; other < i; other++)
                {
                    if (templates[other] == templates[i])
                    {
                        throw new InvalidOperationException(
                            "ExpandableInfoPanel 动态模板引用必须互不相同。");
                    }
                }
            }

            AssertRect(
                view.ModuleTemplate.ContentRoot,
                profile.ModuleContentLayout,
                "Module Content Template");
        }

        internal static void ValidateSourceSemantics()
        {
            var effectSource = ReadSource(EffectDialogPrimitivesPath);
            var facilitySource = ReadSource(FacilityEffectChoiceDialogPath);
            var expandableSource = ReadSource(ExpandableInfoPanelPath);
            var shellSource = ReadSource(EffectDialogShellViewPath);
            var registrySource = ReadSource(GameplayDialogRegistryPath);
            var eventSource = ReadSource(EventChoiceDialogPath);
            var characterSource = ReadSource(CharacterCardEffectChoiceDialogPath);
            var specialActionSource = ReadSource(SpecialActionChoiceDialogPath);
            AssertSourceSha(effectSource, EffectDialogPrimitivesSha256, EffectDialogPrimitivesPath);
            AssertSourceSha(
                facilitySource,
                FacilityEffectChoiceDialogSha256,
                FacilityEffectChoiceDialogPath);
            AssertSourceSha(
                expandableSource,
                ExpandableInfoPanelSha256,
                ExpandableInfoPanelPath);
            AssertSourceSha(shellSource, EffectDialogShellViewSha256, EffectDialogShellViewPath);
            AssertSourceSha(
                ReadSource("Assets/YC/Presentation/EffectDialogLayoutProfile.cs"),
                EffectDialogLayoutProfileSha256,
                "EffectDialogLayoutProfile.cs");
            AssertSourceSha(
                ReadSource("Assets/YC/Presentation/ExpandableInfoPanelLayoutProfile.cs"),
                ExpandableInfoPanelLayoutProfileSha256,
                "ExpandableInfoPanelLayoutProfile.cs");
            AssertSourceSha(
                registrySource,
                GameplayDialogRegistrySha256,
                GameplayDialogRegistryPath);
            AssertSourceSha(
                characterSource,
                CharacterCardEffectChoiceDialogSha256,
                CharacterCardEffectChoiceDialogPath);
            AssertSourceSha(
                specialActionSource,
                SpecialActionChoiceDialogSha256,
                SpecialActionChoiceDialogPath);
            if (CountVector2Constructors(effectSource) != 9 ||
                CountVector2Constructors(facilitySource) != 5 ||
                CountVector2Constructors(expandableSource) != 7 ||
                CountVector2Constructors(shellSource) != 3 ||
                CountVector2Constructors(characterSource) != 0 ||
                CountVector2Constructors(specialActionSource) != 0)
            {
                throw new InvalidOperationException(
                    "Effect 固定布局迁移计数漂移；预期六文件为 9/5/7/3/0/0。");
            }

            var presentationRoot = Path.GetFullPath("Assets/YC/Presentation");
            var recursiveCount = Directory.GetFiles(
                    presentationRoot,
                    "*.cs",
                    SearchOption.AllDirectories)
                .Sum(path => CountVector2Constructors(File.ReadAllText(path)));
            if (recursiveCount != 87)
            {
                throw new InvalidOperationException(
                    "Presentation 递归 new Vector2 次数漂移；预期 87，实际 " +
                    recursiveCount + "。");
            }

            if (expandableSource.Contains("private const float ExpandedWidth") ||
                expandableSource.Contains("private const float ModuleContentWidth") ||
                !expandableSource.Contains("[SerializeField] private ExpandableInfoPanelLayoutProfile layoutProfile;") ||
                !shellSource.Contains("[SerializeField] private EffectDialogLayoutProfile layoutProfile;") ||
                !facilitySource.Contains("private readonly EffectDialogLayoutProfile layoutProfile;") ||
                !characterSource.Contains("private readonly EffectDialogLayoutProfile layoutProfile;") ||
                !specialActionSource.Contains("private readonly EffectDialogLayoutProfile layoutProfile;") ||
                !characterSource.Contains(
                    "layoutProfile = dialogRegistry.EffectDialogLayoutProfile;") ||
                !specialActionSource.Contains(
                    "layoutProfile = dialogRegistry.EffectDialogLayoutProfile;") ||
                !characterSource.Contains("!layoutProfile.TryValidateConfiguration(out layoutReason)") ||
                !specialActionSource.Contains("!layoutProfile.TryValidateConfiguration(out layoutReason)") ||
                !effectSource.Contains("EffectDialogCollapseSpec(EffectDialogLayoutProfile layoutProfile)") ||
                !eventSource.Contains("new EffectDialogCollapseSpec(") ||
                !eventSource.Contains("effectDialogLayoutProfile)") ||
                !registrySource.Contains(
                    "effectDialogShellPrefab == null ? null : effectDialogShellPrefab.LayoutProfile") ||
                HasRuntimeLookupFallback(effectSource) || HasRuntimeLookupFallback(facilitySource) ||
                HasRuntimeLookupFallback(expandableSource) || HasRuntimeLookupFallback(shellSource) ||
                HasRuntimeLookupFallback(characterSource) ||
                HasRuntimeLookupFallback(specialActionSource))
            {
                throw new InvalidOperationException(
                    "Effect/Expandable 消费端不再满足显式 Profile 注入与无 fallback 合同。");
            }

            if (!effectSource.Contains("spec.RowStartY - i * spec.RowSpacing") ||
                !facilitySource.Contains("rowStartX + i * (cardWidth + cardGap)") ||
                !expandableSource.Contains("previewWidth / aspect") ||
                !expandableSource.Contains("y + offset.y") ||
                !shellSource.Contains("descriptionHeight") ||
                !shellSource.Contains("new Vector2(scrollLayout.OffsetMin.x, bottom)") ||
                !characterSource.Contains(
                    "panelSize.y + optionCount * layoutProfile.CharacterOptionsRowHeight") ||
                !characterSource.Contains("Mathf.Clamp(") ||
                !specialActionSource.Contains("ExactTotal = 3") ||
                Count(specialActionSource, "Math.Max(0,") != 2)
            {
                throw new InvalidOperationException("动态行数、索引、比例或内容高度公式被误资产化。");
            }

            if (characterSource.Contains("Vector2Int") ||
                specialActionSource.Contains("Vector2Int") ||
                Regex.IsMatch(characterSource, @"\bVector2\s*\[") ||
                Regex.IsMatch(specialActionSource, @"\bVector2\s*\[") ||
                characterSource.Contains("new EffectDialogLayoutProfile") ||
                specialActionSource.Contains("new EffectDialogLayoutProfile"))
            {
                throw new InvalidOperationException(
                    "剩余 Effect 消费者不得以 Vector2Int、数组或运行时 Profile 伪装固定布局迁移。");
            }
        }

        internal static string ComputeNormalizedSourceSha256(string source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
            var normalized = string.Join("\n", lines).TrimEnd() + "\n";
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(
                        sha.ComputeHash(Encoding.UTF8.GetBytes(normalized)))
                    .Replace("-", string.Empty);
            }
        }

        private static void AssertSourceSha(string source, string expected, string label)
        {
            var actual = ComputeNormalizedSourceSha256(source);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    label + " 规范化源码 SHA-256 漂移：" + actual);
            }
        }

        private static void ValidateProductionDependencies(
            EffectDialogLayoutProfile effectProfile,
            ExpandableInfoPanelLayoutProfile expandableProfile)
        {
            var hud = AssetDatabase.LoadAssetAtPath<GameObject>(GameplayHudPrefabPath);
            var registry = hud == null
                ? null
                : hud.GetComponentInChildren<GameplayDialogRegistry>(true);
            if (hud == null || registry == null ||
                registry.EffectDialogShellPrefab == null ||
                registry.EffectDialogShellPrefab.LayoutProfile != effectProfile)
            {
                throw new InvalidOperationException(
                    "生产 Gameplay HUD Registry 未引用锁定 Effect Prefab/Profile。");
            }

            var expandable = hud.GetComponentInChildren<ExpandableInfoPanel>(true);
            if (expandable == null || expandable.LayoutProfile != expandableProfile ||
                !PrefabUtility.IsPartOfPrefabInstance(expandable.gameObject) ||
                AssetDatabase.GetAssetPath(
                    PrefabUtility.GetCorrespondingObjectFromSource(expandable.gameObject)) !=
                YC.EditorTools.ExpandableInfoPanelEditorAssetBuilder.PrefabPath)
            {
                throw new InvalidOperationException(
                    "生产 Gameplay HUD 未以锁定 Prefab/Profile 实例化 ExpandableInfoPanel。");
            }

            var sceneDependencies = AssetDatabase.GetDependencies(SampleScenePath, true);
            if (!sceneDependencies.Contains(GameplayHudPrefabPath) ||
                !sceneDependencies.Contains(
                    EffectDialogLayoutEditorAssetBuilder.EffectProfileAssetPath) ||
                !sceneDependencies.Contains(
                    EffectDialogLayoutEditorAssetBuilder.ExpandableProfileAssetPath))
            {
                throw new InvalidOperationException(
                    "SampleScene 生产依赖链未包含 HUD 与两个锁定布局 Profile。");
            }
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
                EffectDialogLayoutEditorAssetBuilder.ExpandableProfileAssetPath,
                EffectDialogLayoutEditorAssetBuilder.ExpandableProfileAssetGuid);
            EffectDialogLayoutControlledMetaGuid.ValidateAsset(
                YC.EditorTools.GameplayDialogEditorAssetBuilder.EffectDialogShellPrefabPath,
                EffectDialogShellPrefabGuid);
            EffectDialogLayoutControlledMetaGuid.ValidateAsset(
                YC.EditorTools.ExpandableInfoPanelEditorAssetBuilder.PrefabPath,
                ExpandableInfoPanelPrefabGuid);
            AssertUniqueTypedAsset(
                "EffectDialogLayoutProfile",
                EffectDialogLayoutEditorAssetBuilder.EffectProfileAssetGuid);
            AssertUniqueTypedAsset(
                "ExpandableInfoPanelLayoutProfile",
                EffectDialogLayoutEditorAssetBuilder.ExpandableProfileAssetGuid);
        }

        private static void AssertUniqueTypedAsset(string typeName, string expectedGuid)
        {
            var matches = AssetDatabase.FindAssets("t:" + typeName);
            if (matches.Length != 1 ||
                !string.Equals(matches[0], expectedGuid, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    typeName + " 必须且只能存在一个固定 GUID 的主资产。");
            }
        }

        private static void AssertSingleProfileDependency(string prefabPath, string profilePath)
        {
            var dependencies = AssetDatabase.GetDependencies(prefabPath, true);
            if (dependencies.Count(path => path == profilePath) != 1)
            {
                throw new InvalidOperationException(
                    prefabPath + " 必须且只能依赖唯一锁定布局 Profile。");
            }
        }

        private static void AssertNoMissingScripts(string path)
        {
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var transforms = contents.GetComponentsInChildren<Transform>(true);
                for (var i = 0; i < transforms.Length; i++)
                {
                    var components = transforms[i].GetComponents<Component>();
                    if (components.Any(component => component == null))
                    {
                        throw new InvalidOperationException(
                            path + " 在 " + transforms[i].name + " 含 Missing Script。");
                    }
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private static void AssertExactComponents(GameObject target, params Type[] expected)
        {
            var actual = target.GetComponents<Component>().Select(component => component.GetType()).ToArray();
            if (actual.Length != expected.Length || expected.Any(type => actual.Count(item => item == type) != 1))
            {
                throw new InvalidOperationException(
                    target.name + " 组件集合漂移：" +
                    string.Join(", ", actual.Select(type => type.Name).ToArray()));
            }
        }

        private static void AssertDirectParent(Transform child, Transform parent, string label)
        {
            if (child == null || child.parent != parent)
            {
                throw new InvalidOperationException(label + " 直接父级漂移。");
            }
        }

        private static void AssertRect(
            RectTransform actual,
            EffectDialogRectLayout expected,
            string label)
        {
            if (actual == null || !Approximately(actual.anchorMin, expected.AnchorMin) ||
                !Approximately(actual.anchorMax, expected.AnchorMax) ||
                !Approximately(actual.pivot, expected.Pivot) ||
                !Approximately(actual.sizeDelta, expected.SizeDelta) ||
                !Approximately(actual.anchoredPosition, expected.AnchoredPosition))
            {
                throw new InvalidOperationException(label + " RectTransform 漂移。");
            }
        }

        private static void AssertRect(
            RectTransform actual,
            ExpandableInfoRectLayout expected,
            string label)
        {
            if (actual == null || !Approximately(actual.anchorMin, expected.AnchorMin) ||
                !Approximately(actual.anchorMax, expected.AnchorMax) ||
                !Approximately(actual.pivot, expected.Pivot) ||
                !Approximately(actual.sizeDelta, expected.SizeDelta) ||
                !Approximately(actual.anchoredPosition, expected.AnchoredPosition))
            {
                throw new InvalidOperationException(label + " RectTransform 漂移。");
            }
        }

        private static void AssertInset(
            RectTransform actual,
            EffectDialogInsetLayout expected,
            string label)
        {
            if (actual == null || !Approximately(actual.anchorMin, expected.AnchorMin) ||
                !Approximately(actual.anchorMax, expected.AnchorMax) ||
                !Approximately(actual.pivot, expected.Pivot) ||
                !Approximately(actual.offsetMin, expected.OffsetMin) ||
                !Approximately(actual.offsetMax, expected.OffsetMax))
            {
                throw new InvalidOperationException(label + " Insets 漂移。");
            }
        }

        private static void AssertInset(
            RectTransform actual,
            ExpandableInfoInsetLayout expected,
            string label)
        {
            if (actual == null || !Approximately(actual.anchorMin, expected.AnchorMin) ||
                !Approximately(actual.anchorMax, expected.AnchorMax) ||
                !Approximately(actual.pivot, expected.Pivot) ||
                !Approximately(actual.offsetMin, expected.OffsetMin) ||
                !Approximately(actual.offsetMax, expected.OffsetMax))
            {
                throw new InvalidOperationException(label + " Insets 漂移。");
            }
        }

        private static bool Approximately(Vector2 left, Vector2 right)
        {
            return Mathf.Abs(left.x - right.x) <= 0.001f &&
                   Mathf.Abs(left.y - right.y) <= 0.001f;
        }

        private static string ReadSource(string path)
        {
            return File.ReadAllText(Path.GetFullPath(path));
        }

        private static int Count(string source, string value)
        {
            var count = 0;
            var index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        private static int CountVector2Constructors(string source)
        {
            return Regex.Matches(
                source ?? string.Empty,
                @"\bnew\s+(?:UnityEngine\s*\.\s*)?Vector2\s*\(",
                RegexOptions.CultureInvariant).Count;
        }

        private static bool HasRuntimeLookupFallback(string source)
        {
            return source.Contains("Resources.Load") ||
                   source.Contains("FindObjectOfType") ||
                   source.Contains("FindFirstObjectByType") ||
                   source.Contains("FindAnyObjectByType") ||
                   source.Contains("GameObject.Find(");
        }
    }
}
