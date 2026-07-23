using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class MobileCityColorTests
    {
        [Test]
        public void CreateCitySprite_UsesNeutralTextureThatPreservesInfluenceMarkerHue()
        {
            var presenterType = Type.GetType(
                "YC.Presentation.MapViewPresenter, Assembly-CSharp",
                false);
            Assert.That(presenterType, Is.Not.Null);

            var createCitySprite = presenterType.GetMethod(
                "CreateCitySprite",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(createCitySprite, Is.Not.Null);

            var sprite = createCitySprite.Invoke(null, null) as Sprite;
            Assert.That(sprite, Is.Not.Null);
            try
            {
                AssertNeutral(sprite.texture.GetPixel(2, 2), "border");
                AssertNeutral(sprite.texture.GetPixel(16, 66), "stripe");
                AssertNeutral(sprite.texture.GetPixel(28, 66), "body");
            }
            finally
            {
                Object.DestroyImmediate(sprite.texture);
                Object.DestroyImmediate(sprite);
            }
        }

        private static void AssertNeutral(Color color, string area)
        {
            Assert.That(color.r, Is.EqualTo(color.g).Within(0.001f), area);
            Assert.That(color.g, Is.EqualTo(color.b).Within(0.001f), area);
        }
    }
}
