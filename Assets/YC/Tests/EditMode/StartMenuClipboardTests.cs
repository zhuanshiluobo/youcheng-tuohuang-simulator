using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class StartMenuClipboardTests
    {
        [Test]
        public void CopyRoomCodeToClipboard_WritesRoomId()
        {
            var controller = CreateController(out var owner);
            try
            {
                GUIUtility.systemCopyBuffer = string.Empty;

                InvokePrivate(controller, "CopyRoomCodeToClipboard", "AB12CD");

                Assert.That(GUIUtility.systemCopyBuffer, Is.EqualTo("AB12CD"));
            }
            finally
            {
                DestroyControllerObjects(controller, owner);
            }
        }

        [Test]
        public void PasteRoomCodeFromClipboard_FillsJoinRoomInput()
        {
            var controller = CreateController(out var owner);
            try
            {
                Invoke(controller, "JoinRoom");
                GUIUtility.systemCopyBuffer = " AB12CD ";

                InvokePrivate(controller, "PasteRoomCodeFromClipboard");

                var input = GetPrivateField<InputField>(controller, "joinRoomInput");
                Assert.That(input, Is.Not.Null);
                Assert.That(input.text, Is.EqualTo("AB12CD"));
            }
            finally
            {
                DestroyControllerObjects(controller, owner);
            }
        }

        [Test]
        public void ExternalLinkButtons_ShowCollapsedMarksAndUseBottomNotchedBookmarks()
        {
            var type = Type.GetType("YC.Presentation.StartMenuController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.StartMenuController.");

            var parentObject = new GameObject("External Link Layout Test", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            try
            {
                parentObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var parent = parentObject.GetComponent<RectTransform>();
                parent.sizeDelta = new Vector2(800f, 400f);

                InvokePrivateStatic(type, "CreateExternalLinkButton", parent, "官方网站", "https://example.invalid", "官", 1);
                InvokePrivateStatic(type, "CreateExternalLinkButton", parent, "进入wiki", "https://example.invalid/wiki", "W", 0);

                var buttons = parent.GetComponentsInChildren<Button>(true);
                Assert.That(buttons.Length, Is.EqualTo(2));
                for (var i = 0; i < buttons.Length; i++)
                {
                    var rect = buttons[i].GetComponent<RectTransform>();
                    Assert.That(rect.sizeDelta.x, Is.EqualTo(52f).Within(0.01f));
                }

                Assert.That(HasBookmarkMark(parent, "官"), Is.True);
                Assert.That(HasBookmarkMark(parent, "W"), Is.True);

                AssertBookmarkMarksCanGenerateVertices(parent);

                var icon = FindChildRect(parent, "Bookmark Icon");
                Assert.That(icon, Is.Not.Null);

                var sprite = icon.GetComponent<Image>().sprite;
                Assert.That(sprite, Is.Not.Null);

                var texture = sprite.texture;
                var centerX = texture.width / 2;
                Assert.That(texture.GetPixel(centerX, 0).a, Is.EqualTo(0f).Within(0.01f));
                Assert.That(texture.GetPixel(0, 0).a, Is.EqualTo(1f).Within(0.01f));
                Assert.That(texture.GetPixel(1, 0).a, Is.EqualTo(0f).Within(0.01f));
                Assert.That(texture.GetPixel(texture.width - 1, 0).a, Is.EqualTo(1f).Within(0.01f));
                Assert.That(texture.GetPixel(texture.width - 2, 0).a, Is.EqualTo(0f).Within(0.01f));
                Assert.That(texture.GetPixel(centerX, texture.height - 1).a, Is.EqualTo(1f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(parentObject);
            }
        }

        private static Component CreateController(out GameObject owner)
        {
            var type = Type.GetType("YC.Presentation.StartMenuController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.StartMenuController.");

            owner = new GameObject("Start Menu Clipboard Test");
            return owner.AddComponent(type);
        }

        private static void Invoke(Component controller, string methodName, params object[] args)
        {
            var method = controller.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Missing StartMenuController." + methodName + ".");
            method.Invoke(controller, args);
        }

        private static void InvokePrivate(Component controller, string methodName, params object[] args)
        {
            var method = controller.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing StartMenuController." + methodName + ".");
            method.Invoke(controller, args);
        }

        private static void InvokePrivateStatic(Type type, string methodName, params object[] args)
        {
            var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing StartMenuController." + methodName + ".");
            method.Invoke(null, args);
        }

        private static T GetPrivateField<T>(Component controller, string fieldName) where T : class
        {
            var field = controller.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing StartMenuController." + fieldName + ".");
            return field.GetValue(controller) as T;
        }

        private static bool HasBookmarkMark(Transform parent, string value)
        {
            var texts = parent.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < texts.Length; i++)
            {
                if (texts[i] != null &&
                    texts[i].name == "Bookmark Mark" &&
                    texts[i].text == value &&
                    texts[i].color.a > 0.99f &&
                    texts[i].gameObject.activeInHierarchy)
                {
                    return true;
                }
            }

            return false;
        }

        private static void AssertBookmarkMarksCanGenerateVertices(Transform parent)
        {
            var marks = parent.GetComponentsInChildren<Text>(true);
            var checkedCount = 0;
            for (var i = 0; i < marks.Length; i++)
            {
                var mark = marks[i];
                if (mark == null || mark.name != "Bookmark Mark")
                {
                    continue;
                }

                checkedCount++;
                var rect = mark.rectTransform.rect;
                var settings = mark.GetGenerationSettings(rect.size);
                var generator = new TextGenerator();

                Assert.That(mark.verticalOverflow, Is.EqualTo(VerticalWrapMode.Overflow));
                Assert.That(rect.height, Is.GreaterThanOrEqualTo(42f));
                Assert.That(generator.Populate(mark.text, settings), Is.True);
                Assert.That(generator.vertexCount, Is.GreaterThan(0), mark.text + " should generate visible UGUI vertices.");
            }

            Assert.That(checkedCount, Is.EqualTo(2));
        }

        private static RectTransform FindChildRect(Transform parent, string childName)
        {
            var rects = parent.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < rects.Length; i++)
            {
                if (rects[i] != null && rects[i].name == childName)
                {
                    return rects[i];
                }
            }

            return null;
        }

        private static void DestroyControllerObjects(Component controller, GameObject owner)
        {
            var roomPanel = GetPrivateField<GameObject>(controller, "roomPanel");
            if (roomPanel != null)
            {
                Object.DestroyImmediate(roomPanel);
            }

            Object.DestroyImmediate(owner);
        }
    }
}
