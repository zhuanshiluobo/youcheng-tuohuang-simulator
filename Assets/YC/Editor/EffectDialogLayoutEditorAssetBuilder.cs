using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using YC.Presentation;

namespace YC.Editor
{
    public static class EffectDialogLayoutEditorAssetBuilder
    {
        public const string SourceJsonPath =
            "Assets/YC/Editor/Data/effect_dialog_layout_manifest.json";
        public const string SourceJsonGuid = "6a219b35f3dd49a6a04a5b4ceea9bb70";
        public const string ExpectedManifestSha256 =
            "84988DCACCEE2575064C8BCAE1A4FD1D5EB533F993E31122397FF1EA9A9AF7BB";
        public const string EffectProfileAssetPath =
            "Assets/YC/Presentation/Content/EffectDialogLayoutProfile.asset";
        public const string EffectProfileAssetGuid = "c4df7a0a512c4f9ca6979764614620dd";
        public const string ExpandableProfileAssetPath =
            "Assets/YC/Presentation/Content/ExpandableInfoPanelLayoutProfile.asset";
        public const string ExpandableProfileAssetGuid = "8f1b3fc2cb09419b939e7d36abe52d4a";

        private static readonly string[] UnrelatedAssetPaths =
        {
            YC.EditorTools.GameplayDialogEditorAssetBuilder.DispatchDecisionPrefabPath,
            YC.EditorTools.GameplayDialogEditorAssetBuilder.EventChoiceDialogPrefabPath,
            "Assets/YC/Presentation/Content/EventChoiceDialogLayoutProfile.asset",
            "Assets/YC/Editor/Data/event_choice_dialog_layout_manifest.json",
            YC.EditorTools.GameplayInteractionHudEditorAssetBuilder.PrefabPath,
            YC.EditorTools.GameplayDialogEditorAssetBuilder.SharedUiVisualsAssetPath,
            "Assets/YC/Presentation/Content/UiThemeCatalog.asset",
            "Assets/YC/Presentation/Content/EventCharacterCardCatalog.asset",
            "Assets/YC/Presentation/Content/CityStyleSpecialActionCatalog.asset",
            "Assets/Scenes/SampleScene.unity"
        };

        [Serializable]
        internal sealed class SourceManifest
        {
            public int Version;
            public EffectDialogLayoutValues EffectDialog;
            public ExpandableInfoPanelLayoutValues ExpandableInfoPanel;
        }

        private sealed class AssetFingerprint
        {
            public string Path;
            public string Guid;
            public string Sha256;
        }

        [MenuItem("YC/Build/Effect Dialog Layout/Rebuild Profiles")]
        public static void RebuildProfilesMenu()
        {
            RebuildProfilesOnlyForBuilder();
            Debug.Log("[EffectDialogLayoutEditorAssetBuilder] 已重建两个布局 Profile。");
        }

        [MenuItem("YC/Build/Effect Dialog Layout/Rebuild Profiles And Prefabs")]
        public static void RebuildProfilesAndPrefabsMenu()
        {
            var unrelated = CaptureUnrelatedFingerprints();
            RebuildProfilesOnlyForBuilder();
            YC.EditorTools.GameplayDialogEditorAssetBuilder.RebuildEffectDialogOnly();
            YC.EditorTools.ExpandableInfoPanelEditorAssetBuilder.RebuildWithProfile(
                LoadRequiredExpandableProfile());
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            AssertUnrelatedFingerprintsUnchanged(unrelated);
            EffectDialogLayoutBuildReadiness.ValidateReadyForBuild();
            Debug.Log(
                "[EffectDialogLayoutEditorAssetBuilder] 已定向重建 Profile、EffectDialogShell 与 ExpandableInfoPanel Prefab。");
        }

        internal static void RebuildProfilesOnlyForBuilder()
        {
            var source = ParseSource();
            EnsureControlledMetaGuid(EffectProfileAssetPath, EffectProfileAssetGuid);
            EnsureControlledMetaGuid(ExpandableProfileAssetPath, ExpandableProfileAssetGuid);

            var effectProfile = AssetDatabase.LoadAssetAtPath<EffectDialogLayoutProfile>(
                EffectProfileAssetPath);
            if (effectProfile == null)
            {
                effectProfile = ScriptableObject.CreateInstance<EffectDialogLayoutProfile>();
                AssetDatabase.CreateAsset(effectProfile, EffectProfileAssetPath);
            }

            var expandableProfile = AssetDatabase.LoadAssetAtPath<ExpandableInfoPanelLayoutProfile>(
                ExpandableProfileAssetPath);
            if (expandableProfile == null)
            {
                expandableProfile = ScriptableObject.CreateInstance<ExpandableInfoPanelLayoutProfile>();
                AssetDatabase.CreateAsset(expandableProfile, ExpandableProfileAssetPath);
            }

            effectProfile.ConfigureForEditor(ExpectedManifestSha256, source.EffectDialog);
            expandableProfile.ConfigureForEditor(
                ExpectedManifestSha256,
                source.ExpandableInfoPanel);
            EditorUtility.SetDirty(effectProfile);
            EditorUtility.SetDirty(expandableProfile);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(EffectProfileAssetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(ExpandableProfileAssetPath, ImportAssetOptions.ForceUpdate);
            ValidateProfilesAgainstSource(source);
        }

        public static EffectDialogLayoutProfile LoadRequiredEffectProfile()
        {
            var source = ParseSource();
            var profile = AssetDatabase.LoadAssetAtPath<EffectDialogLayoutProfile>(
                EffectProfileAssetPath);
            ValidateEffectProfile(profile, source.EffectDialog);
            return profile;
        }

        public static ExpandableInfoPanelLayoutProfile LoadRequiredExpandableProfile()
        {
            var source = ParseSource();
            var profile = AssetDatabase.LoadAssetAtPath<ExpandableInfoPanelLayoutProfile>(
                ExpandableProfileAssetPath);
            ValidateExpandableProfile(profile, source.ExpandableInfoPanel);
            return profile;
        }

        internal static SourceManifest ParseSource()
        {
            EnsureControlledMetaGuid(SourceJsonPath, SourceJsonGuid);
            var absolutePath = Path.GetFullPath(SourceJsonPath);
            if (!File.Exists(absolutePath) ||
                !string.Equals(
                    AssetDatabase.AssetPathToGUID(SourceJsonPath),
                    SourceJsonGuid,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "效果对话框布局 manifest 缺失或 GUID 发生变化。");
            }

            var sha256 = ComputeFileSha256(absolutePath);
            if (!string.Equals(sha256, ExpectedManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "效果对话框布局 manifest SHA-256 与锁定值不一致：" + sha256);
            }

            var source = JsonUtility.FromJson<SourceManifest>(
                File.ReadAllText(absolutePath));
            if (source == null || source.Version != 1 || source.EffectDialog == null ||
                source.ExpandableInfoPanel == null)
            {
                throw new InvalidOperationException("效果对话框布局 manifest 版本或载荷无效。");
            }

            var effectProbe = ScriptableObject.CreateInstance<EffectDialogLayoutProfile>();
            var expandableProbe = ScriptableObject.CreateInstance<ExpandableInfoPanelLayoutProfile>();
            try
            {
                effectProbe.ConfigureForEditor(ExpectedManifestSha256, source.EffectDialog);
                expandableProbe.ConfigureForEditor(
                    ExpectedManifestSha256,
                    source.ExpandableInfoPanel);
                if (!effectProbe.TryValidateConfiguration(out var effectReason))
                {
                    throw new InvalidOperationException(
                        "EffectDialog manifest 载荷无效：" + effectReason);
                }

                if (!expandableProbe.TryValidateConfiguration(out var expandableReason))
                {
                    throw new InvalidOperationException(
                        "ExpandableInfoPanel manifest 载荷无效：" + expandableReason);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(effectProbe);
                UnityEngine.Object.DestroyImmediate(expandableProbe);
            }

            return source;
        }

        private static void ValidateProfilesAgainstSource(SourceManifest source)
        {
            ValidateEffectProfile(
                AssetDatabase.LoadAssetAtPath<EffectDialogLayoutProfile>(EffectProfileAssetPath),
                source.EffectDialog);
            ValidateExpandableProfile(
                AssetDatabase.LoadAssetAtPath<ExpandableInfoPanelLayoutProfile>(
                    ExpandableProfileAssetPath),
                source.ExpandableInfoPanel);
        }

        private static void ValidateEffectProfile(
            EffectDialogLayoutProfile profile,
            EffectDialogLayoutValues expected)
        {
            EnsureControlledMetaGuid(EffectProfileAssetPath, EffectProfileAssetGuid);
            var reason = string.Empty;
            if (profile == null || AssetDatabase.LoadMainAssetAtPath(EffectProfileAssetPath) != profile ||
                !EditorUtility.IsPersistent(profile) ||
                !string.Equals(
                    AssetDatabase.AssetPathToGUID(EffectProfileAssetPath),
                    EffectProfileAssetGuid,
                    StringComparison.OrdinalIgnoreCase) ||
                !profile.TryValidateConfiguration(out reason) ||
                !string.Equals(
                    profile.SourceManifestSha256,
                    ExpectedManifestSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !profile.MatchesValuesForEditor(expected))
            {
                throw new InvalidOperationException(
                    "EffectDialogLayoutProfile 未精确匹配锁定 manifest：" + reason);
            }
        }

        private static void ValidateExpandableProfile(
            ExpandableInfoPanelLayoutProfile profile,
            ExpandableInfoPanelLayoutValues expected)
        {
            EnsureControlledMetaGuid(ExpandableProfileAssetPath, ExpandableProfileAssetGuid);
            var reason = string.Empty;
            if (profile == null ||
                AssetDatabase.LoadMainAssetAtPath(ExpandableProfileAssetPath) != profile ||
                !EditorUtility.IsPersistent(profile) ||
                !string.Equals(
                    AssetDatabase.AssetPathToGUID(ExpandableProfileAssetPath),
                    ExpandableProfileAssetGuid,
                    StringComparison.OrdinalIgnoreCase) ||
                !profile.TryValidateConfiguration(out reason) ||
                !string.Equals(
                    profile.SourceManifestSha256,
                    ExpectedManifestSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !profile.MatchesValuesForEditor(expected))
            {
                throw new InvalidOperationException(
                    "ExpandableInfoPanelLayoutProfile 未精确匹配锁定 manifest：" + reason);
            }
        }

        private static List<AssetFingerprint> CaptureUnrelatedFingerprints()
        {
            var result = new List<AssetFingerprint>(UnrelatedAssetPaths.Length);
            for (var i = 0; i < UnrelatedAssetPaths.Length; i++)
            {
                var path = UnrelatedAssetPaths[i];
                if (!File.Exists(Path.GetFullPath(path)))
                {
                    throw new InvalidOperationException("定向重建前缺少无关资产：" + path);
                }

                result.Add(new AssetFingerprint
                {
                    Path = path,
                    Guid = AssetDatabase.AssetPathToGUID(path),
                    Sha256 = ComputeFileSha256(Path.GetFullPath(path))
                });
            }

            return result;
        }

        private static void AssertUnrelatedFingerprintsUnchanged(
            IReadOnlyList<AssetFingerprint> before)
        {
            for (var i = 0; i < before.Count; i++)
            {
                var fingerprint = before[i];
                var actualGuid = AssetDatabase.AssetPathToGUID(fingerprint.Path);
                var actualSha256 = ComputeFileSha256(Path.GetFullPath(fingerprint.Path));
                if (!string.Equals(actualGuid, fingerprint.Guid, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(actualSha256, fingerprint.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Effect 布局定向重建修改了无关资产：" + fingerprint.Path);
                }
            }
        }

        internal static string ComputeFileSha256(string path)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path)))
                    .Replace("-", string.Empty);
            }
        }

        private static void EnsureControlledMetaGuid(string assetPath, string expectedGuid)
        {
            EffectDialogLayoutControlledMetaGuid.ValidateAsset(assetPath, expectedGuid);
        }
    }

    internal static class EffectDialogLayoutControlledMetaGuid
    {
        private static readonly Regex TopLevelGuidLine = new Regex(
            @"(?m)^guid:[ \t]*(?<guid>[0-9a-fA-F]{32})[ \t]*\r?$",
            RegexOptions.CultureInvariant);

        internal static void ValidateAsset(string assetPath, string expectedGuid)
        {
            var metaPath = Path.GetFullPath(assetPath + ".meta");
            if (!File.Exists(metaPath))
            {
                throw new InvalidOperationException(
                    "受控布局资产必须在创建或保存前保留固定 GUID meta：" + metaPath);
            }

            ValidateMetaGuidText(
                File.ReadAllText(metaPath),
                expectedGuid,
                AssetDatabase.AssetPathToGUID(assetPath));
        }

        internal static void ValidateMetaGuidText(
            string metaText,
            string expectedGuid,
            string actualAssetGuid)
        {
            if (string.IsNullOrEmpty(expectedGuid) ||
                !Regex.IsMatch(expectedGuid, "^[0-9a-fA-F]{32}$"))
            {
                throw new InvalidOperationException("受控布局资产预期 GUID 格式无效。");
            }

            var matches = TopLevelGuidLine.Matches(metaText ?? string.Empty);
            if (matches.Count != 1 ||
                !string.Equals(
                    matches[0].Groups["guid"].Value,
                    expectedGuid,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "受控布局资产 meta 必须且只能包含一条精确固定 GUID。");
            }

            if (string.IsNullOrEmpty(actualAssetGuid) ||
                !string.Equals(actualAssetGuid, expectedGuid, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "AssetDatabase 中的受控布局资产 GUID 与固定 GUID 不一致。");
            }
        }
    }
}
