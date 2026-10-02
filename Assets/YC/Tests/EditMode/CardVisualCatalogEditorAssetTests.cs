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
        public void Catalog_UsesExternalSpritesAndKeepsOnlySharedPersistentTextures()
        {
            var catalog = AssetDatabase.LoadMainAssetAtPath(CatalogPath);
            Assert.That(catalog, Is.Not.Null);
            AssertCatalogValid(catalog);
            Assert.That(GetProperty<int>(catalog, "FacilityCount"), Is.EqualTo(45));
            Assert.That(GetProperty<int>(catalog, "CityStyleCount"), Is.EqualTo(6));
            Assert.That(GetProperty<int>(catalog, "CharacterFrontCount"), Is.EqualTo(5));
            Assert.That(GetProperty<int>(catalog, "EventCount"), Is.EqualTo(22));
            var serialized = new SerializedObject(catalog);
            foreach (var name in new[] { "facilityTextures", "cityStyleTextures", "characterFrontTextures", "eventTextures" })
                Assert.That(serialized.FindProperty(name), Is.Null, name);
            var shared = 0;
            CheckCharacterBackEntries(serialized.FindProperty("characterBackTextures"), ref shared);
            AssertPersistent(serialized.FindProperty("cityBoardTexture").objectReferenceValue as Texture2D);
            Assert.That(shared, Is.EqualTo(4));
            var pack = YC.Infrastructure.Lua.ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var sheets = new Dictionary<string, Texture2D>();
            foreach (var definition in pack.ActiveDefinitions)
            {
                var method = definition.ContentType == "facility" ? "GetFacility" :
                    definition.ContentType == "city_style" ? "GetCityStyle" :
                    definition.ContentType == "character" ? "GetCharacterFront" : "GetEvent";
                var sprite = catalog.GetType().GetMethod(method).Invoke(catalog, new object[] { definition.RuntimeId }) as Sprite;
                Assert.That(sprite, Is.Not.Null, definition.DefinitionId);
                var slice = definition.ArtworkSlice;
                Assert.That(slice, Is.Not.Null, definition.DefinitionId);
                Assert.That(sprite.name, Is.EqualTo(definition.ArtworkSpriteId));
                Assert.That(sprite.rect, Is.EqualTo(new Rect(slice.X, slice.Y, slice.Width, slice.Height)));
                Assert.That(sprite.texture.width, Is.EqualTo(slice.SourceWidth));
                Assert.That(sprite.texture.height, Is.EqualTo(slice.SourceHeight));
                if (sheets.TryGetValue(definition.Artwork, out var sheet)) Assert.That(sprite.texture, Is.SameAs(sheet));
                else sheets.Add(definition.Artwork, sprite.texture);
            }
            Assert.That(sheets.Count, Is.EqualTo(7));
            Assert.That(catalog.GetType().GetMethod("GetFacility").Invoke(catalog, new object[] { FacilityCardDatabase.EnterpriseOffice }), Is.Null);
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
        public void ProductionCardVisuals_OnlyContentLoaderDecodesExternalArtwork()
        {
            var presentationRoot = Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation");
            foreach (var path in Directory.GetFiles(presentationRoot, "*.cs", SearchOption.AllDirectories))
            {
                var normalized = path.Replace('\\', '/');
                if (normalized.Contains("/Editor/") || normalized.Contains("/Tests/")) continue;
                var source = File.ReadAllText(path);
                StringAssert.DoesNotContain("Resources.Load<Texture2D>", source, normalized);
                if (!normalized.EndsWith("/ExternalContentRuntime.cs", StringComparison.Ordinal))
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

    }
}
