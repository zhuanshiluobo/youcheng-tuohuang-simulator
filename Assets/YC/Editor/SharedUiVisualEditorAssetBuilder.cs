using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YC.Presentation;

namespace YC.EditorTools
{
    public static class SharedUiVisualEditorAssetBuilder
    {
        public const string AssetPath =
            "Assets/YC/Presentation/Sprites/SharedUiVisuals.asset";

        [MenuItem("Tools/YC/Rebuild Shared UI Visual Assets")]
        public static void Rebuild()
        {
            BuildOrUpdate();
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);
            Debug.Log("[SharedUiVisualEditorAssetBuilder] Rebuilt persistent shared UI sprites.");
        }

        public static UiVisualAssetLibrary BuildOrUpdate()
        {
            EnsureFolder("Assets/YC/Presentation/Sprites");
            var library = AssetDatabase.LoadAssetAtPath<UiVisualAssetLibrary>(AssetPath);
            if (library == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(AssetPath) != null)
                {
                    throw new InvalidOperationException(
                        "Shared UI visual path is occupied by an incompatible asset.");
                }

                library = ScriptableObject.CreateInstance<UiVisualAssetLibrary>();
                library.name = "SharedUiVisuals";
                AssetDatabase.CreateAsset(library, AssetPath);
            }

            var assets = AssetDatabase.LoadAllAssetsAtPath(AssetPath);
            var upTexture = FindOrCreateTexture(assets, "UI Triangle Up Texture", true);
            var downTexture = FindOrCreateTexture(assets, "UI Triangle Down Texture", false);
            assets = AssetDatabase.LoadAllAssetsAtPath(AssetPath);
            var upSprite = FindOrCreateSprite(assets, upTexture, "UI Triangle Up");
            var downSprite = FindOrCreateSprite(assets, downTexture, "UI Triangle Down");

            var data = new SerializedObject(library);
            data.FindProperty("triangleUp").objectReferenceValue = upSprite;
            data.FindProperty("triangleDown").objectReferenceValue = downSprite;
            data.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            return library;
        }

        private static Texture2D FindOrCreateTexture(UnityEngine.Object[] assets, string name, bool pointsUp)
        {
            var texture = assets.OfType<Texture2D>().FirstOrDefault(item => item.name == name);
            if (texture == null)
            {
                texture = new Texture2D(32, 32, TextureFormat.RGBA32, false) { name = name };
                AssetDatabase.AddObjectToAsset(texture, AssetPath);
            }

            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            PaintTriangle(texture, pointsUp);
            EditorUtility.SetDirty(texture);
            return texture;
        }

        private static Sprite FindOrCreateSprite(
            UnityEngine.Object[] assets,
            Texture2D texture,
            string name)
        {
            var sprite = assets.OfType<Sprite>().FirstOrDefault(item => item.name == name);
            if (sprite != null)
            {
                return sprite;
            }

            sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                texture.width);
            sprite.name = name;
            AssetDatabase.AddObjectToAsset(sprite, AssetPath);
            return sprite;
        }

        private static void PaintTriangle(Texture2D texture, bool pointsUp)
        {
            const float padding = 5f;
            var size = texture.width;
            var center = (size - 1) * 0.5f;
            var bottom = padding;
            var top = size - 1 - padding;
            var height = top - bottom;
            var maximumHalfWidth = center - padding;
            for (var y = 0; y < size; y++)
            {
                var verticalProgress = (y - bottom) / height;
                var withinHeight = verticalProgress >= 0f && verticalProgress <= 1f;
                var halfWidth = pointsUp
                    ? maximumHalfWidth * (1f - verticalProgress)
                    : maximumHalfWidth * verticalProgress;
                for (var x = 0; x < size; x++)
                {
                    var inside = withinHeight && Mathf.Abs(x - center) <= halfWidth + 0.5f;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, inside ? 1f : 0f));
                }
            }

            texture.Apply(false, false);
        }

        private static void EnsureFolder(string path)
        {
            var parts = path.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }
    }
}
