using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using YC.Presentation;

namespace YC.Editor
{
    public static class EventChoiceDialogLayoutEditorAssetBuilder
    {
        public const string SourceJsonPath =
            "Assets/YC/Editor/Data/event_choice_dialog_layout_manifest.json";
        public const string SourceJsonGuid = "c2a9bc63da0f4d5ba412ef8b93d671e4";
        public const string ExpectedManifestSha256 =
            "71345F8231F0ADFAB117E66EF749CD48B52719AC005E04A7EBC29214425AE476";
        public const string ProfileAssetPath =
            "Assets/YC/Presentation/Content/EventChoiceDialogLayoutProfile.asset";
        public const string ProfileAssetGuid = "f6b125d7c0934eb2a8405c6e7d19f438";

        [Serializable]
        private sealed class SourceManifest
        {
            public int Version;
            public EventChoiceDialogLayoutValues Values;
        }

        [MenuItem("YC/Build/Event Choice Dialog Layout/Rebuild Profile")]
        public static void RebuildProfileMenu()
        {
            RebuildProfile();
        }

        [MenuItem("YC/Build/Event Choice Dialog Layout/Rebuild Profile And Dialog Prefab")]
        public static void RebuildProfileAndDialogPrefabMenu()
        {
            RebuildProfile();
            YC.EditorTools.GameplayDialogEditorAssetBuilder.RebuildEventChoiceDialogOnly();
            EventChoiceDialogLayoutBuildReadiness.ValidateReadyForBuild();
            Debug.Log("[EventChoiceDialogLayoutEditorAssetBuilder] 已重建布局 Profile 与 EventChoiceDialog Prefab。");
        }

        public static EventChoiceDialogLayoutProfile RebuildProfile()
        {
            var source = ParseSource();
            EnsureControlledMetaGuid(ProfileAssetPath, ProfileAssetGuid);
            var profile = AssetDatabase.LoadAssetAtPath<EventChoiceDialogLayoutProfile>(
                ProfileAssetPath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<EventChoiceDialogLayoutProfile>();
                AssetDatabase.CreateAsset(profile, ProfileAssetPath);
            }

            profile.ConfigureForEditor(ExpectedManifestSha256, source.Values);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(ProfileAssetPath, ImportAssetOptions.ForceUpdate);
            return LoadRequiredProfile();
        }

        public static EventChoiceDialogLayoutProfile LoadRequiredProfile()
        {
            var source = ParseSource();
            var profile = AssetDatabase.LoadAssetAtPath<EventChoiceDialogLayoutProfile>(
                ProfileAssetPath);
            if (profile == null ||
                !string.Equals(
                    AssetDatabase.AssetPathToGUID(ProfileAssetPath),
                    ProfileAssetGuid,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "缺少固定 GUID 的 EventChoiceDialogLayoutProfile 资产。");
            }

            if (!profile.TryValidateConfiguration(out var reason) ||
                !string.Equals(
                    profile.SourceManifestSha256,
                    ExpectedManifestSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !profile.MatchesValuesForEditor(source.Values))
            {
                throw new InvalidOperationException(
                    "EventChoiceDialogLayoutProfile 未精确匹配锁定 manifest：" + reason);
            }

            var matches = AssetDatabase.FindAssets("t:EventChoiceDialogLayoutProfile");
            if (matches.Length != 1 ||
                !string.Equals(matches[0], ProfileAssetGuid, StringComparison.OrdinalIgnoreCase) ||
                AssetDatabase.LoadMainAssetAtPath(ProfileAssetPath) != profile)
            {
                throw new InvalidOperationException(
                    "EventChoiceDialogLayoutProfile 必须是项目中唯一的主资产。");
            }

            return profile;
        }

        private static SourceManifest ParseSource()
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
                    "事件选择对话框布局 manifest 缺失或 GUID 发生变化。");
            }

            var sha256 = ComputeSha256(absolutePath);
            if (!string.Equals(
                    sha256,
                    ExpectedManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "事件选择对话框布局 manifest SHA-256 与锁定值不一致：" + sha256);
            }

            var source = JsonUtility.FromJson<SourceManifest>(
                File.ReadAllText(absolutePath));
            if (source == null || source.Version != 1 || source.Values == null)
            {
                throw new InvalidOperationException(
                    "事件选择对话框布局 manifest 版本或载荷无效。");
            }

            var probe = ScriptableObject.CreateInstance<EventChoiceDialogLayoutProfile>();
            try
            {
                probe.ConfigureForEditor(ExpectedManifestSha256, source.Values);
                if (!probe.TryValidateConfiguration(out var reason))
                {
                    throw new InvalidOperationException(
                        "事件选择对话框布局 manifest 载荷无效：" + reason);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }

            return source;
        }

        private static void EnsureControlledMetaGuid(string assetPath, string expectedGuid)
        {
            EventChoiceDialogControlledMetaGuid.ValidateAsset(assetPath, expectedGuid);
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path)))
                    .Replace("-", string.Empty);
            }
        }
    }
}
