using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Rules;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class CardVisualCatalogEditorAssetTests
    {
        private const string CatalogPath =
            "Assets/YC/Presentation/Content/CardVisualCatalog.asset";
        private const string GameplayHudPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";

        [Test]
        public void Catalog_UsesDecodedExternalArtworkWithoutPersistentTextureDependencies()
        {
            var catalog = AssetDatabase.LoadMainAssetAtPath(CatalogPath);
            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.GetType().FullName, Is.EqualTo("YC.Presentation.CardVisualCatalog"));
            AssertCatalogValid(catalog);

            foreach (var dependency in AssetDatabase.GetDependencies(CatalogPath, true))
                Assert.That(AssetDatabase.LoadMainAssetAtPath(dependency), Is.Not.InstanceOf<Texture2D>(), dependency);

            var runtime = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            var pack = (ExternalContentPack)runtime.GetProperty("Pack").GetValue(null);
            foreach (var definition in pack.ActiveDefinitions)
            {
                var method = definition.ContentType == "facility" ? "GetFacility" :
                    definition.ContentType == "city_style" ? "GetCityStyle" :
                    definition.ContentType == "character" ? "GetCharacterFront" :
                    definition.ContentType == "event" ? "GetEvent" : null;
                if (method == null) continue;
                var texture = InvokeTexture(catalog, method, definition.RuntimeId);
                AssertDecoded(texture, definition.RuntimeId);
                var externalTexture = runtime.GetMethod("GetArtwork").Invoke(null,
                    new object[] { definition.ContentType, definition.RuntimeId });
                Assert.That(texture, Is.SameAs(externalTexture), definition.RuntimeId);
                Assert.That(InvokeTexture(catalog, method, definition.RuntimeId), Is.SameAs(texture));
            }

            foreach (PlayerColor color in Enum.GetValues(typeof(PlayerColor)))
            {
                var texture = InvokeTexture(catalog, "GetCharacterBack", color);
                AssertDecoded(texture, color.ToString());
                Assert.That(texture, Is.SameAs(runtime.GetMethod("GetSharedArtwork").Invoke(null,
                    new object[] { "character_back_" + (int)color })));
            }
            var board = catalog.GetType().GetMethod("GetCityBoard").Invoke(catalog, null) as Texture2D;
            AssertDecoded(board, "city_board");
            Assert.That(board, Is.SameAs(runtime.GetMethod("GetSharedArtwork").Invoke(null,
                new object[] { "city_board" })));
            Assert.That(InvokeTexture(catalog, "GetFacility", "unknown_card"), Is.Null);
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
        public void CityBoardPrefabs_BindTheSharedExternalTextureWithoutLegacyArtworkDependencies()
        {
            var catalog = AssetDatabase.LoadMainAssetAtPath(CatalogPath);
            Assert.That(catalog, Is.Not.Null);
            var board = catalog.GetType().GetMethod("GetCityBoard").Invoke(catalog, null) as Texture2D;
            AssertDecoded(board, "city_board");
            var bindingType = Type.GetType("YC.Presentation.SharedArtworkImage, Assembly-CSharp", true);
            var boardViewType = Type.GetType("YC.Presentation.BuildInfoPanelView, Assembly-CSharp", true);
            var paths = new[]
            {
                "Assets/YC/Presentation/Prefabs/Gameplay/BuildInfoPanel.prefab",
                GameplayHudPrefabPath,
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/FacilityBuildDialog.prefab"
            };
            var expectedBindings = new[] { 1, 2, 1 };
            for (var i = 0; i < paths.Length; i++)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(paths[i]);
                Assert.That(prefab, Is.Not.Null, paths[i]);
                foreach (var dependency in AssetDatabase.GetDependencies(paths[i], true))
                {
                    Assert.That(dependency, Does.Not.StartWith("Assets/YC/Presentation/Resources/CardImages/"), paths[i]);
                    Assert.That(dependency, Is.Not.EqualTo(
                        "Assets/YC/Presentation/CardPicker/Artwork/facility-city-board.png"), paths[i]);
                }
                var instance = UnityEngine.Object.Instantiate(prefab);
                try
                {
                    foreach (var view in instance.GetComponentsInChildren(boardViewType, true))
                    {
                        var image = (RawImage)boardViewType.GetProperty("CityBoardImage").GetValue(view);
                        image.texture = null;
                        var arguments = new object[] { null };
                        var valid = (bool)boardViewType.GetMethod("TryValidateConfiguration").Invoke(view, arguments);
                        Assert.That(valid, Is.True, arguments[0] as string);
                        Assert.That(image.texture, Is.SameAs(board), paths[i]);
                    }
                    var bindings = instance.GetComponentsInChildren(bindingType, true);
                    Assert.That(bindings.Length, Is.EqualTo(expectedBindings[i]), paths[i]);
                    foreach (var binding in bindings)
                    {
                        for (var parent = binding.transform; parent != null; parent = parent.parent)
                            parent.gameObject.SetActive(true);
                        var image = binding.GetComponent<RawImage>();
                        Assert.That(image, Is.Not.Null, paths[i]);
                        Assert.That(image.texture, Is.SameAs(board), paths[i] + ":" + binding.name);
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
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

        private static void AssertDecoded(Texture2D texture, string label)
        {
            Assert.That(texture, Is.Not.Null, label);
            Assert.That(texture.width, Is.GreaterThan(0), label);
            Assert.That(texture.height, Is.GreaterThan(0), label);
            Assert.That(AssetDatabase.Contains(texture), Is.False, label);
        }

        private static void AssertCatalogValid(UnityEngine.Object catalog)
        {
            var arguments = new object[] { null };
            var valid = (bool)catalog.GetType().GetMethod("TryValidateConfiguration").Invoke(
                catalog,
                arguments);
            Assert.That(valid, Is.True, arguments[0] as string);
        }

        private static Texture2D InvokeTexture(UnityEngine.Object catalog, string methodName, object argument)
        {
            return catalog.GetType().GetMethod(methodName).Invoke(
                catalog,
                new[] { argument }) as Texture2D;
        }
    }
}
