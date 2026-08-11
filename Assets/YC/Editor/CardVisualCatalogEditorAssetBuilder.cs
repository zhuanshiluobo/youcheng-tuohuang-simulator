using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Presentation;

namespace YC.Editor
{
    public static class CardVisualCatalogEditorAssetBuilder
    {
        public const string CatalogPath =
            "Assets/YC/Presentation/Content/CardVisualCatalog.asset";

        private const string CardImageRoot =
            "Assets/YC/Presentation/Resources/CardImages/";

        [MenuItem("Tools/YC/Rebuild Card Visual Catalog")]
        public static void Rebuild()
        {
            var facilities = BuildFacilityEntries();
            var cityStyles = BuildCityStyleEntries();
            var characterFronts = BuildCharacterFrontEntries();
            var characterBacks = BuildCharacterBackEntries();
            var cityBoard = LoadTexture(CardImageRoot + "Boards/city_board.png");

            EnsureFolder("Assets/YC/Presentation/Content");
            var catalog = AssetDatabase.LoadAssetAtPath<CardVisualCatalog>(CatalogPath);
            var isNew = catalog == null;
            if (isNew)
            {
                catalog = ScriptableObject.CreateInstance<CardVisualCatalog>();
                catalog.name = "CardVisualCatalog";
            }

            catalog.ConfigureForEditor(
                facilities,
                cityStyles,
                characterFronts,
                characterBacks,
                cityBoard);

            if (isNew)
            {
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            if (!catalog.TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException(reason);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[CardVisualCatalogEditorAssetBuilder] Rebuilt 62 persistent card textures.");
        }

        public static CardVisualCatalog LoadRequiredCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<CardVisualCatalog>(CatalogPath);
            if (catalog == null)
            {
                throw new InvalidOperationException("Missing CardVisualCatalog asset: " + CatalogPath);
            }

            if (!catalog.TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException(reason);
            }

            return catalog;
        }

        private static List<CardVisualCatalog.IdTextureEntry> BuildFacilityEntries()
        {
            var result = new List<CardVisualCatalog.IdTextureEntry>();
            for (var i = 1; i <= 41; i++)
            {
                var id = "building_" + i.ToString("000");
                result.Add(new CardVisualCatalog.IdTextureEntry(
                    id,
                    LoadTexture(CardImageRoot + "Facilities/" + id + ".jpg")));
            }

            AddFacility(result, FacilityCardDatabase.CoreCommandTower, "reserve_001.jpg");
            AddFacility(result, FacilityCardDatabase.ExtensionHubBlue, "reserve_002.jpg");
            AddFacility(result, FacilityCardDatabase.ExtensionHubYellow, "reserve_003.jpg");
            AddFacility(result, FacilityCardDatabase.ExtensionHubRed, "reserve_004.jpg");
            AddFacility(result, FacilityCardDatabase.EnterpriseOffice, "enterprise_office.jpg");

            if (result.Count != CardVisualCatalog.ExpectedFacilityCount)
            {
                throw new InvalidOperationException("Facility texture entry count mismatch: " + result.Count);
            }

            return result;
        }

        private static List<CardVisualCatalog.IdTextureEntry> BuildCityStyleEntries()
        {
            return new List<CardVisualCatalog.IdTextureEntry>
            {
                CityStyle(CityStyleDatabase.MilitaryIndustrialArea, "military_industrial_area.jpg"),
                CityStyle(CityStyleDatabase.MobilizationSupportSystem, "mobilization_support_system.jpg"),
                CityStyle(CityStyleDatabase.CompositePowerSystem, "composite_power_system.jpg"),
                CityStyle(CityStyleDatabase.MaterialRelayStation, "material_relay_station.jpg"),
                CityStyle(CityStyleDatabase.SourceStoneIndustrialHub, "source_stone_industrial_hub.jpg"),
                CityStyle(CityStyleDatabase.EfficientMobileManagementSystem, "efficient_mobile_management_system.jpg")
            };
        }

        private static List<CardVisualCatalog.IdTextureEntry> BuildCharacterFrontEntries()
        {
            return new List<CardVisualCatalog.IdTextureEntry>
            {
                CharacterFront(CharacterCardDatabase.Liskarm, "liskarm.jpg"),
                CharacterFront(CharacterCardDatabase.Texas, "texas.jpg"),
                CharacterFront(CharacterCardDatabase.TinMan, "tin-man.jpg"),
                CharacterFront(CharacterCardDatabase.Cannot, "cannot.jpg"),
                CharacterFront(CharacterCardDatabase.Elysium, "elysium.jpg")
            };
        }

        private static List<CardVisualCatalog.CharacterBackTextureEntry> BuildCharacterBackEntries()
        {
            return new List<CardVisualCatalog.CharacterBackTextureEntry>
            {
                CharacterBack(PlayerColor.Red, "back-red.jpg"),
                CharacterBack(PlayerColor.Yellow, "back-yellow.jpg"),
                CharacterBack(PlayerColor.Green, "back-green.jpg"),
                CharacterBack(PlayerColor.Blue, "back-blue.jpg")
            };
        }

        private static void AddFacility(
            ICollection<CardVisualCatalog.IdTextureEntry> entries,
            string facilityId,
            string fileName)
        {
            entries.Add(new CardVisualCatalog.IdTextureEntry(
                facilityId,
                LoadTexture(CardImageRoot + "Facilities/" + fileName)));
        }

        private static CardVisualCatalog.IdTextureEntry CityStyle(string id, string fileName)
        {
            return new CardVisualCatalog.IdTextureEntry(
                id,
                LoadTexture(CardImageRoot + "CityStyles/" + fileName));
        }

        private static CardVisualCatalog.IdTextureEntry CharacterFront(string id, string fileName)
        {
            return new CardVisualCatalog.IdTextureEntry(
                id,
                LoadTexture(CardImageRoot + "Characters/" + fileName));
        }

        private static CardVisualCatalog.CharacterBackTextureEntry CharacterBack(
            PlayerColor color,
            string fileName)
        {
            return new CardVisualCatalog.CharacterBackTextureEntry(
                color,
                LoadTexture(CardImageRoot + "Characters/" + fileName));
        }

        private static Texture2D LoadTexture(string path)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null)
            {
                throw new InvalidOperationException("Missing card texture asset: " + path);
            }

            string guid;
            long localId;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(texture, out guid, out localId) ||
                string.IsNullOrEmpty(guid) || localId == 0)
            {
                throw new InvalidOperationException("Card texture is not persistent: " + path);
            }

            return texture;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var separator = path.LastIndexOf('/');
            var parent = path.Substring(0, separator);
            var name = path.Substring(separator + 1);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
