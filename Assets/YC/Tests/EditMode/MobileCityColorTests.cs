using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YC.Presentation.Maps;

namespace YC.Tests.EditMode
{
    public sealed class MobileCityColorTests
    {
        [Test]
        public void PersistentCitySprite_UsesNeutralTextureThatPreservesInfluenceMarkerHue()
        {
            var library = AssetDatabase.LoadAssetAtPath<MapVisualSpriteLibrary>(
                "Assets/YC/Presentation/Sprites/Map/MapVisualSprites.asset");
            Assert.That(library, Is.Not.Null);
            var sprite = library.MobileCity;
            Assert.That(sprite, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath(sprite),
                Is.EqualTo("Assets/YC/Presentation/Sprites/Map/MapVisualSprites.asset"));
            AssertNeutral(sprite.texture.GetPixel(2, 2), "border");
            AssertNeutral(sprite.texture.GetPixel(16, 66), "stripe");
            AssertNeutral(sprite.texture.GetPixel(28, 66), "body");
        }

        private static void AssertNeutral(Color color, string area)
        {
            Assert.That(color.r, Is.EqualTo(color.g).Within(0.001f), area);
            Assert.That(color.g, Is.EqualTo(color.b).Within(0.001f), area);
        }
    }
}
