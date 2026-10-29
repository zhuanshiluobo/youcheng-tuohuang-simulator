using System.Linq;
using UnityEditor;
using UnityEngine;

namespace YC.Editor
{
    /// <summary>仅手动调用；普通构建不会重新设置项目图标。</summary>
    public static class ApplicationIconSetup
    {
        private const string IconPath = "Assets/Branding/ApplicationIcon.png";

        [MenuItem("YC/Project/Apply Application Icon")]
        public static void Apply()
        {
            AssetDatabase.ImportAsset(IconPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(IconPath);
            importer.textureType = TextureImporterType.Default;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = false;
            // 保留原图透明像素的颜色，避免 Unity 图标缩放时出现边缘扩色条纹。
            importer.alphaIsTransparency = false;
            importer.SaveAndReimport();
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
            if (icon == null) throw new System.InvalidOperationException("应用图标导入失败。");
            foreach (var target in new[] { BuildTargetGroup.Unknown, BuildTargetGroup.Standalone })
            {
                var sizes = PlayerSettings.GetIconSizesForTargetGroup(target);
                if (sizes.Length == 0) throw new System.InvalidOperationException("未取得图标尺寸：" + target);
                PlayerSettings.SetIconsForTargetGroup(target, sizes.Select(_ => icon).ToArray());
                if (PlayerSettings.GetIconsForTargetGroup(target).Any(value => value != icon))
                    throw new System.InvalidOperationException("应用图标设置未生效：" + target);
                Debug.Log("应用图标已设置：" + target + "，尺寸：" + string.Join(",", sizes));
            }
        }

        public static void ApplyAndBuildRelease()
        {
            Apply();
            LocalhostBuildMenu.BuildRelease();
        }
    }
}
