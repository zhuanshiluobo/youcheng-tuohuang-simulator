using UnityEditor;

namespace YC.Editor
{
    public sealed class MapTexturePostprocessor : AssetPostprocessor
    {
        private const string MapTexturePathPrefix = "Assets/YC/Data/Maps/Textures/";
        private const string ResourceIconPathPrefix = "Assets/YC/Data/ResourceIcons/";
        private const string RulebookPagePathPrefix = "Assets/Resources/RulebookPages/";

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(MapTexturePathPrefix) &&
                !assetPath.StartsWith(ResourceIconPathPrefix) &&
                !assetPath.StartsWith(RulebookPagePathPrefix))
            {
                return;
            }

            var importer = (TextureImporter)assetImporter;
            importer.mipmapEnabled = false;
            if (assetPath.StartsWith(RulebookPagePathPrefix))
            {
                importer.textureType = TextureImporterType.Default;
                importer.maxTextureSize = 4096;
                importer.wrapMode = UnityEngine.TextureWrapMode.Clamp;
                return;
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = assetPath.StartsWith(ResourceIconPathPrefix);
        }
    }
}
