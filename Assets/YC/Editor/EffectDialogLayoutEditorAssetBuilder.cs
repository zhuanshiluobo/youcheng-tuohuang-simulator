using System;
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
            "47409DCBA169FDD577B10D0247FC34229D288FDA5C0FCA3B3AB7D3355A292C9A";
        public const string EffectProfileAssetPath =
            "Assets/YC/Presentation/Content/EffectDialogLayoutProfile.asset";
        public const string EffectProfileAssetGuid = "c4df7a0a512c4f9ca6979764614620dd";

        [Serializable]
        internal sealed class SourceManifest
        {
            public int Version;
            public EffectDialogLayoutValues EffectDialog;
        }

        [MenuItem("YC/Build/Effect Dialog Layout/Rebuild Profile")]
        public static void RebuildProfilesMenu()
        {
            RebuildProfilesOnlyForBuilder();
            Debug.Log("[EffectDialogLayoutEditorAssetBuilder] 已重建效果对话框布局 Profile。");
        }

        [MenuItem("YC/Build/Effect Dialog Layout/Rebuild Profile And Prefab")]
        public static void RebuildProfilesAndPrefabsMenu()
        {
            RebuildProfilesOnlyForBuilder();
            YC.EditorTools.GameplayDialogEditorAssetBuilder.RebuildEffectDialogOnly();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EffectDialogLayoutBuildReadiness.ValidateReadyForBuild();
            Debug.Log("[EffectDialogLayoutEditorAssetBuilder] 已定向重建 EffectDialogShell 与布局 Profile。");
        }

        internal static void RebuildProfilesOnlyForBuilder()
        {
            var source = ParseSource();
            EffectDialogLayoutControlledMetaGuid.ValidateAsset(EffectProfileAssetPath, EffectProfileAssetGuid);
            var profile = AssetDatabase.LoadAssetAtPath<EffectDialogLayoutProfile>(EffectProfileAssetPath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<EffectDialogLayoutProfile>();
                AssetDatabase.CreateAsset(profile, EffectProfileAssetPath);
            }

            profile.ConfigureForEditor(ExpectedManifestSha256, source.EffectDialog);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(EffectProfileAssetPath, ImportAssetOptions.ForceUpdate);
            ValidateEffectProfile(profile, source.EffectDialog);
        }

        public static EffectDialogLayoutProfile LoadRequiredEffectProfile()
        {
            var source = ParseSource();
            var profile = AssetDatabase.LoadAssetAtPath<EffectDialogLayoutProfile>(EffectProfileAssetPath);
            ValidateEffectProfile(profile, source.EffectDialog);
            return profile;
        }

        internal static SourceManifest ParseSource()
        {
            EffectDialogLayoutControlledMetaGuid.ValidateAsset(SourceJsonPath, SourceJsonGuid);
            var absolutePath = Path.GetFullPath(SourceJsonPath);
            if (!File.Exists(absolutePath) ||
                !string.Equals(AssetDatabase.AssetPathToGUID(SourceJsonPath), SourceJsonGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("效果对话框布局 manifest 缺失或 GUID 发生变化。");

            var sha256 = ComputeFileSha256(absolutePath);
            if (!string.Equals(sha256, ExpectedManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("效果对话框布局 manifest SHA-256 与锁定值不一致：" + sha256);

            var source = JsonUtility.FromJson<SourceManifest>(File.ReadAllText(absolutePath));
            if (source == null || source.Version != 1 || source.EffectDialog == null)
                throw new InvalidOperationException("效果对话框布局 manifest 版本或载荷无效。");

            var probe = ScriptableObject.CreateInstance<EffectDialogLayoutProfile>();
            try
            {
                probe.ConfigureForEditor(ExpectedManifestSha256, source.EffectDialog);
                if (!probe.TryValidateConfiguration(out var reason))
                    throw new InvalidOperationException("EffectDialog manifest 载荷无效：" + reason);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }

            return source;
        }

        internal static string ComputeFileSha256(string path)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", string.Empty);
            }
        }

        private static void ValidateEffectProfile(EffectDialogLayoutProfile profile, EffectDialogLayoutValues expected)
        {
            EffectDialogLayoutControlledMetaGuid.ValidateAsset(EffectProfileAssetPath, EffectProfileAssetGuid);
            var reason = string.Empty;
            if (profile == null || AssetDatabase.LoadMainAssetAtPath(EffectProfileAssetPath) != profile ||
                !EditorUtility.IsPersistent(profile) ||
                !string.Equals(AssetDatabase.AssetPathToGUID(EffectProfileAssetPath), EffectProfileAssetGuid, StringComparison.OrdinalIgnoreCase) ||
                !profile.TryValidateConfiguration(out reason) ||
                !string.Equals(profile.SourceManifestSha256, ExpectedManifestSha256, StringComparison.OrdinalIgnoreCase) ||
                !profile.MatchesValuesForEditor(expected))
                throw new InvalidOperationException("EffectDialogLayoutProfile 未精确匹配锁定 manifest：" + reason);
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
                throw new InvalidOperationException("受控布局资产必须在创建或保存前保留固定 GUID meta：" + metaPath);
            ValidateMetaGuidText(File.ReadAllText(metaPath), expectedGuid, AssetDatabase.AssetPathToGUID(assetPath));
        }

        internal static void ValidateMetaGuidText(string metaText, string expectedGuid, string actualAssetGuid)
        {
            if (string.IsNullOrEmpty(expectedGuid) || !Regex.IsMatch(expectedGuid, "^[0-9a-fA-F]{32}$"))
                throw new InvalidOperationException("受控布局资产预期 GUID 格式无效。");
            var matches = TopLevelGuidLine.Matches(metaText ?? string.Empty);
            if (matches.Count != 1 ||
                !string.Equals(matches[0].Groups["guid"].Value, expectedGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("受控布局资产 meta 必须且只能包含一条精确固定 GUID。");
            if (string.IsNullOrEmpty(actualAssetGuid) ||
                !string.Equals(actualAssetGuid, expectedGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AssetDatabase 中的受控布局资产 GUID 与固定 GUID 不一致。");
        }
    }
}
