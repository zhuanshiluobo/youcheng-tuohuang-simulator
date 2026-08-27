using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.Rules;

namespace YC.Tests.EditMode
{
    public sealed class CardVisualCatalogEditorAssetTests
    {
        private const string CatalogPath =
            "Assets/YC/Presentation/Content/CardVisualCatalog.asset";
        private const string GameplayHudPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";

        [Test]
        public void Catalog_HasExactPersistentRuntimeMappings()
        {
            var path = CatalogPath;
            var allAssets = AssetDatabase.LoadAllAssetsAtPath(path);
            Assert.That(allAssets, Has.Length.EqualTo(1));
            var catalog = allAssets[0];
            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.GetType().FullName, Is.EqualTo("YC.Presentation.CardVisualCatalog"));
            AssertCatalogValid(catalog);
            Assert.That(GetProperty<int>(catalog, "FacilityCount"), Is.EqualTo(46));
            Assert.That(GetProperty<int>(catalog, "CityStyleCount"), Is.EqualTo(6));
            Assert.That(GetProperty<int>(catalog, "CharacterFrontCount"), Is.EqualTo(5));
            Assert.That(GetProperty<int>(catalog, "CharacterBackCount"), Is.EqualTo(4));
            Assert.That(GetProperty<int>(catalog, "EventCount"), Is.EqualTo(22));
            Assert.That(GetProperty<int>(catalog, "TextureCount"), Is.EqualTo(84));

            var serialized = new SerializedObject(catalog);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var persistentTextures = 0;
            CheckIdEntries(serialized.FindProperty("facilityTextures"), "facility", ids, ref persistentTextures);
            CheckIdEntries(serialized.FindProperty("cityStyleTextures"), "cityStyle", ids, ref persistentTextures);
            CheckIdEntries(serialized.FindProperty("characterFrontTextures"), "characterFront", ids, ref persistentTextures);
            CheckIdEntries(serialized.FindProperty("eventTextures"), "event", ids, ref persistentTextures);
            CheckCharacterBackEntries(
                serialized.FindProperty("characterBackTextures"),
                ref persistentTextures);
            AssertPersistent(
                serialized.FindProperty("cityBoardTexture").objectReferenceValue as Texture2D);
            persistentTextures++;
            Assert.That(persistentTextures, Is.EqualTo(84));

            for (var i = 1; i <= 41; i++)
            {
                Assert.That(InvokeTexture(catalog, "GetFacility", "building_" + i.ToString("000")), Is.Not.Null);
            }

            Assert.That(InvokeTexture(catalog, "GetFacility", FacilityCardDatabase.CoreCommandTower), Is.Not.Null);
            Assert.That(InvokeTexture(catalog, "GetFacility", FacilityCardDatabase.ExtensionHubBlue), Is.Not.Null);
            Assert.That(InvokeTexture(catalog, "GetFacility", FacilityCardDatabase.ExtensionHubYellow), Is.Not.Null);
            Assert.That(InvokeTexture(catalog, "GetFacility", FacilityCardDatabase.ExtensionHubRed), Is.Not.Null);
            Assert.That(InvokeTexture(catalog, "GetFacility", FacilityCardDatabase.EnterpriseOffice), Is.Not.Null);

            foreach (var cityStyleId in CityStyleDatabase.DefaultSupplyIds)
            {
                Assert.That(InvokeTexture(catalog, "GetCityStyle", cityStyleId), Is.Not.Null, cityStyleId);
            }

            Assert.That(InvokeTexture(catalog, "GetCharacterFront", CharacterCardDatabase.Liskarm), Is.Not.Null);
            Assert.That(InvokeTexture(catalog, "GetCharacterFront", CharacterCardDatabase.Texas), Is.Not.Null);
            Assert.That(InvokeTexture(catalog, "GetCharacterFront", CharacterCardDatabase.TinMan), Is.Not.Null);
            Assert.That(InvokeTexture(catalog, "GetCharacterFront", CharacterCardDatabase.Cannot), Is.Not.Null);
            Assert.That(InvokeTexture(catalog, "GetCharacterFront", CharacterCardDatabase.Elysium), Is.Not.Null);
            Assert.That(InvokeTexture(catalog, "GetCharacterBack", PlayerColor.Red), Is.Not.Null);
            Assert.That(InvokeTexture(catalog, "GetCharacterBack", PlayerColor.Yellow), Is.Not.Null);
            Assert.That(InvokeTexture(catalog, "GetCharacterBack", PlayerColor.Green), Is.Not.Null);
            Assert.That(InvokeTexture(catalog, "GetCharacterBack", PlayerColor.Blue), Is.Not.Null);
            foreach (var eventCardId in EventCardDatabase.GreenCardIds)
            {
                Assert.That(InvokeTexture(catalog, "GetEvent", eventCardId), Is.Not.Null, eventCardId);
            }
            foreach (var eventCardId in EventCardDatabase.RedCardIds)
            {
                Assert.That(InvokeTexture(catalog, "GetEvent", eventCardId), Is.Not.Null, eventCardId);
            }
            foreach (var eventCardId in EventCardDatabase.YellowCardIds)
            {
                Assert.That(InvokeTexture(catalog, "GetEvent", eventCardId), Is.Not.Null, eventCardId);
            }
            Assert.That(catalog.GetType().GetMethod("GetCityBoard").Invoke(catalog, null), Is.Not.Null);
        }

        [Test]
        public void GameplayHud_UsesCatalogThroughConnectedRegistry()
        {
            var catalog = AssetDatabase.LoadMainAssetAtPath(CatalogPath);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                GameplayHudPrefabPath);
            Assert.That(catalog, Is.Not.Null);
            Assert.That(prefab, Is.Not.Null);

            Component hud = null;
            foreach (var component in prefab.GetComponents<Component>())
            {
                if (component != null && component.GetType().FullName == "YC.Presentation.GameplayInteractionHudView")
                {
                    hud = component;
                    break;
                }
            }
            Assert.That(hud, Is.Not.Null);
            var registry = new SerializedObject(hud).FindProperty("dialogRegistry").objectReferenceValue;
            Assert.That(registry, Is.Not.Null);
            Assert.That(
                new SerializedObject(registry).FindProperty("cardVisualCatalog").objectReferenceValue,
                Is.SameAs(catalog));
        }

        [Test]
        public void ProductionCardVisuals_DoNotDiscoverRelativePathsAtRuntime()
        {
            var presentationRoot = Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation");
            foreach (var path in Directory.GetFiles(presentationRoot, "*.cs", SearchOption.AllDirectories))
            {
                var normalized = path.Replace('\\', '/');
                if (normalized.Contains("/Editor/") || normalized.Contains("/Tests/")) continue;
                var source = File.ReadAllText(path);
                StringAssert.DoesNotContain("Resources.Load<Texture2D>", source, normalized);
                StringAssert.DoesNotContain("LoadImage(", source, normalized);
                StringAssert.DoesNotContain("CardImages/", source, normalized);
                StringAssert.DoesNotContain("FrontImageRelativePath", source, normalized);
                StringAssert.DoesNotContain("CoveredBackImageRelativePath", source, normalized);
                StringAssert.DoesNotContain("class CardTextureCatalog", source, normalized);
                StringAssert.DoesNotContain("class CardImagePathCatalog", source, normalized);
                StringAssert.DoesNotContain("class CharacterCardImagePathCatalog", source, normalized);
            }

            var legacyLoader = File.ReadAllText(Path.Combine(
                presentationRoot,
                "CardAndCityBoardPresentation.cs"));
            StringAssert.DoesNotContain("File.", legacyLoader);
            StringAssert.DoesNotContain("Directory.", legacyLoader);
            StringAssert.DoesNotContain("Path.", legacyLoader);
            StringAssert.DoesNotContain("new Texture2D(", legacyLoader);
        }

        private static void CheckIdEntries(
            SerializedProperty entries,
            string label,
            ISet<string> ids,
            ref int persistentTextures)
        {
            Assert.That(entries, Is.Not.Null);
            for (var i = 0; i < entries.arraySize; i++)
            {
                var entry = entries.GetArrayElementAtIndex(i);
                var id = entry.FindPropertyRelative("id").stringValue;
                Assert.That(id, Is.Not.Empty);
                Assert.That(ids.Add(label + ":" + id), Is.True, id);
                AssertPersistent(entry.FindPropertyRelative("texture").objectReferenceValue as Texture2D);
                persistentTextures++;
            }
        }

        private static void CheckCharacterBackEntries(
            SerializedProperty entries,
            ref int persistentTextures)
        {
            Assert.That(entries, Is.Not.Null);
            Assert.That(entries.arraySize, Is.EqualTo(4));
            var colors = new HashSet<int>();
            for (var i = 0; i < entries.arraySize; i++)
            {
                var entry = entries.GetArrayElementAtIndex(i);
                Assert.That(colors.Add(entry.FindPropertyRelative("color").enumValueIndex), Is.True);
                AssertPersistent(entry.FindPropertyRelative("texture").objectReferenceValue as Texture2D);
                persistentTextures++;
            }
        }

        private static void AssertPersistent(Texture2D texture)
        {
            Assert.That(texture, Is.Not.Null);
            Assert.That(
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(texture, out string guid, out long localId),
                Is.True);
            Assert.That(guid, Is.Not.Empty);
            Assert.That(localId, Is.Not.EqualTo(0));
        }

        private static void AssertCatalogValid(UnityEngine.Object catalog)
        {
            var arguments = new object[] { null };
            var valid = (bool)catalog.GetType().GetMethod("TryValidateConfiguration").Invoke(
                catalog,
                arguments);
            Assert.That(valid, Is.True, arguments[0] as string);
        }

        private static T GetProperty<T>(UnityEngine.Object target, string propertyName)
        {
            return (T)target.GetType().GetProperty(propertyName).GetValue(target, null);
        }

        private static Texture2D InvokeTexture(UnityEngine.Object catalog, string methodName, object argument)
        {
            return catalog.GetType().GetMethod(methodName).Invoke(
                catalog,
                new[] { argument }) as Texture2D;
        }
    }
}
