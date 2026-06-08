using UnityEditor;

namespace YC.Editor
{
    public sealed class MapTexturePostprocessor : AssetPostprocessor
    {
        private const string MapTexturePathPrefix = "Assets/YC/Data/Maps/Textures/";

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(MapTexturePathPrefix))
            {
                return;
            }

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = false;
        }
    }
}
