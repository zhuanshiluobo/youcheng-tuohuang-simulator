using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

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

        private static T GetPrivateField<T>(Component controller, string fieldName) where T : class
        {
            var field = controller.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing StartMenuController." + fieldName + ".");
            return field.GetValue(controller) as T;
        }

        private static void DestroyControllerObjects(Component controller, GameObject owner)
        {
            var roomPanel = GetPrivateField<GameObject>(controller, "roomPanel");
            if (roomPanel != null)
            {
                UnityEngine.Object.DestroyImmediate(roomPanel);
            }

            UnityEngine.Object.DestroyImmediate(owner);
        }
    }
}
