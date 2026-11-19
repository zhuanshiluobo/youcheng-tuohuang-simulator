using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class EventArtworkAspectTests
    {
        private GameObject root;
        private GameObject outsider;
        private Texture2D atlas;
        private Sprite tall;
        private Sprite wide;
        private RectTransform surface;
        private RawImage image;
        private LayoutGroup layout;
        private Type artworkView;

        [SetUp]
        public void CreateFixture()
        {
            var groupType = Type.GetType("YC.Presentation.UiEventArtworkLayoutGroup, Assembly-CSharp", true);
            artworkView = Type.GetType("YC.Presentation.CardArtworkView, Assembly-CSharp", true);
            root = new GameObject("夹具容器", typeof(RectTransform));
            var rootRect = root.GetComponent<RectTransform>();
            rootRect.sizeDelta = new Vector2(700, 500);
            layout = (LayoutGroup)root.AddComponent(groupType);
            surface = new GameObject("卡面", typeof(RectTransform)).GetComponent<RectTransform>();
            surface.SetParent(rootRect, false);
            groupType.GetField("artworkSurface", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(layout, surface);
            image = new GameObject("图片", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage)).GetComponent<RawImage>();
            image.transform.SetParent(surface, false);
            outsider = new GameObject("不属于卡面的图片", typeof(RectTransform));
            outsider.transform.SetParent(rootRect, false);
            atlas = new Texture2D(300, 200);
            tall = Sprite.Create(atlas, new Rect(10, 10, 40, 80), new Vector2(.5f, .5f));
            wide = Sprite.Create(atlas, new Rect(70, 10, 120, 40), new Vector2(.5f, .5f));
        }

        [TearDown]
        public void DisposeFixture()
        {
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(tall);
            UnityEngine.Object.DestroyImmediate(wide);
            UnityEngine.Object.DestroyImmediate(atlas);
        }

        private void SetSprite(Sprite sprite)
        {
            artworkView.GetMethod("Set", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { image, sprite });
            layout.SetLayoutHorizontal();
            layout.SetLayoutVertical();
        }

        [Test]
        public void UsesActualAtlasSliceAndUpdatesWhenArtworkChanges()
        {
            SetSprite(tall);
            Assert.That(surface.rect.width / surface.rect.height, Is.EqualTo(.5f).Within(.001f));
            Assert.That(image.texture, Is.SameAs(atlas));

            SetSprite(wide);
            Assert.That(surface.rect.width / surface.rect.height, Is.EqualTo(3f).Within(.001f));
            Assert.That(surface.rect.width, Is.LessThanOrEqualTo(root.GetComponent<RectTransform>().rect.width + .001f));
            Assert.That(surface.rect.height, Is.LessThanOrEqualTo(root.GetComponent<RectTransform>().rect.height + .001f));

            root.GetComponent<RectTransform>().sizeDelta = new Vector2(300, 900);
            layout.SetLayoutHorizontal();
            layout.SetLayoutVertical();
            Assert.That(surface.rect.width / surface.rect.height, Is.EqualTo(3f).Within(.001f));
        }

        [Test]
        public void UnrelatedSourceCannotChangeArtworkSize()
        {
            SetSprite(tall);
            layout.GetType().GetMethod("SetArtworkSize").Invoke(layout,
                new object[] { outsider.GetComponent<RectTransform>(), new Vector2(100, 1) });
            layout.SetLayoutHorizontal();
            layout.SetLayoutVertical();
            Assert.That(surface.rect.width / surface.rect.height, Is.EqualTo(.5f).Within(.001f));
        }
    }
}
