using System;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Cards;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class StartMenuClipboardTests
    {
        [Test]
        public void CreateLocalGameSeedSource_CreatesDifferentShuffleSeedForEachGame()
        {
            var type = Type.GetType("YC.Presentation.StartMenuController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.StartMenuController.");

            var method = type.GetMethod(
                "CreateLocalGameSeedSource",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing StartMenuController.CreateLocalGameSeedSource.");

            var firstSource = method.Invoke(null, null) as string;
            var secondSource = method.Invoke(null, null) as string;

            Assert.That(firstSource, Does.StartWith("LOCAL_GAME_"));
            Assert.That(secondSource, Does.StartWith("LOCAL_GAME_"));
            Assert.That(secondSource, Is.Not.EqualTo(firstSource));
            Assert.That(
                EventDeckService.CreateSeed(secondSource),
                Is.Not.EqualTo(EventDeckService.CreateSeed(firstSource)));
        }

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
        public void ExternalLinkButtons_AreAuthoredInStartMenuPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab");
            Assert.That(prefab, Is.Not.Null);
            Assert.That(FindChildRect(prefab.transform, "Official Link Button"), Is.Not.Null);
            Assert.That(FindChildRect(prefab.transform, "Wiki Link Button"), Is.Not.Null);
        }

        private static Component CreateController(out GameObject owner)
        {
            var type = Type.GetType("YC.Presentation.StartMenuController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.StartMenuController.");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab");
            Assert.That(prefab, Is.Not.Null, "Missing editor-authored StartMenuRoot prefab.");
            owner = Object.Instantiate(prefab);
            owner.name = "Start Menu Clipboard Test";
            return owner.GetComponent(type);
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
            Object.DestroyImmediate(owner);
        }
    }
}
