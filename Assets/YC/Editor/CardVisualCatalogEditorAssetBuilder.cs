using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YC.Domain.Cards;
using YC.Domain.Rules;
using YC.Presentation;

namespace YC.Editor
{
    /// <summary>仅维护仍使用独立资源的牌背与城市面板引用；卡牌正面由外部内容切片定义驱动。</summary>
    public static class CardVisualCatalogEditorAssetBuilder
    {
        public const string CatalogPath = "Assets/YC/Presentation/Content/CardVisualCatalog.asset";
        private const string CardImageRoot = "Assets/YC/Presentation/Resources/CardImages/";

        [MenuItem("Tools/YC/Rebuild Shared Card Asset References")]
        public static void Rebuild()
        {
            var backs = BuildCharacterBackEntries();
            var cityBoard = LoadTexture(CardImageRoot + "Boards/city_board.png");
            EnsureFolder("Assets/YC/Presentation/Content");
            var catalog = AssetDatabase.LoadAssetAtPath<CardVisualCatalog>(CatalogPath);
            var isNew = catalog == null;
            if (isNew)
            {
                catalog = ScriptableObject.CreateInstance<CardVisualCatalog>();
                catalog.name = "CardVisualCatalog";
            }

            catalog.ConfigureForEditor(backs, cityBoard);
            if (isNew) AssetDatabase.CreateAsset(catalog, CatalogPath);
            if (!catalog.TryValidateConfiguration(out var reason))
                throw new InvalidOperationException(reason);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[CardVisualCatalogEditorAssetBuilder] 已更新 4 张角色牌背和城市面板引用；牌面来自外部内容包切片。");
        }

        public static CardVisualCatalog LoadRequiredCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<CardVisualCatalog>(CatalogPath);
            if (catalog == null) throw new InvalidOperationException("Missing CardVisualCatalog asset: " + CatalogPath);
            if (!catalog.TryValidateConfiguration(out var reason)) throw new InvalidOperationException(reason);
            return catalog;
        }

        private static List<CardVisualCatalog.CharacterBackTextureEntry> BuildCharacterBackEntries() =>
            new List<CardVisualCatalog.CharacterBackTextureEntry>
            {
                CharacterBack(PlayerColor.Red, "back-red.jpg"),
                CharacterBack(PlayerColor.Yellow, "back-yellow.jpg"),
                CharacterBack(PlayerColor.Green, "back-green.jpg"),
                CharacterBack(PlayerColor.Blue, "back-blue.jpg")
            };

        private static CardVisualCatalog.CharacterBackTextureEntry CharacterBack(PlayerColor color, string fileName) =>
            new CardVisualCatalog.CharacterBackTextureEntry(color,
                LoadTexture(CardImageRoot + "Characters/" + fileName));

        private static Texture2D LoadTexture(string path)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null) throw new InvalidOperationException("Missing shared card texture asset: " + path);
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(texture, out var guid, out long localId) ||
                string.IsNullOrEmpty(guid) || localId == 0)
                throw new InvalidOperationException("Shared card texture is not a persistent asset: " + path);
            return texture;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var separator = path.LastIndexOf('/');
            EnsureFolder(path.Substring(0, separator));
            AssetDatabase.CreateFolder(path.Substring(0, separator), path.Substring(separator + 1));
        }
    }
}
