using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace YC.Tests.EditMode
{
    public sealed class GameSettingsMenuControllerTests
    {
        private GameObject root;
        private Component controller;

        [SetUp]
        public void SetUp()
        {
            var type = Type.GetType("YC.Presentation.GameSettingsMenuController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.GameSettingsMenuController.");

            root = new GameObject("GameSettingsMenuControllerTests");
            controller = root.AddComponent(type);
            EnsureAwakeRan(controller, "canvasTransform");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(root);
        }

        [Test]
        public void HandleEscapePressed_WhenClosed_OpensSettingsOverlay()
        {
            var overlay = root.transform.Find("Settings Menu Canvas/Settings Overlay");

            InvokePublic(controller, "HandleEscapePressed");

            Assert.That(overlay, Is.Not.Null);
            Assert.That(overlay.gameObject.activeSelf, Is.True);
            Assert.That(GetPublicProperty<bool>(controller, "IsOpen"), Is.True);
        }

        [Test]
        public void HandleEscapePressed_WhenOpen_StartsClosingSettings()
        {
            InvokePublic(controller, "HandleEscapePressed");

            InvokePublic(controller, "HandleEscapePressed");

            Assert.That(GetPublicProperty<bool>(controller, "IsOpen"), Is.False);
        }

        [Test]
        public void HandleEscapePressed_OnStartPageStillOpensSettings()
        {
            var setVisible = controller.GetType().GetMethod(
                "SetReturnToStartButtonVisible",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(setVisible, Is.Not.Null);
            setVisible.Invoke(controller, new object[] { false });

            InvokePublic(controller, "HandleEscapePressed");

            Assert.That(GetPublicProperty<bool>(controller, "IsOpen"), Is.True);
        }

        [Test]
        public void BuildUi_DoesNotCreateHintCardButton()
        {
            var hintButton = root.transform.Find("Settings Menu Canvas/Hint Card Button");
            Assert.That(hintButton, Is.Null);
        }

        [Test]
        public void HandleEscapePressed_WhileImageViewerIsOpen_DoesNotOpenSettings()
        {
            var viewerType = Type.GetType("YC.Presentation.ZoomableImageViewerController, Assembly-CSharp", false);
            Assert.That(viewerType, Is.Not.Null);
            var viewerObject = new GameObject("Open Image Viewer");
            viewerObject.transform.SetParent(root.transform, false);
            var viewer = viewerObject.AddComponent(viewerType);
            var texture = new Texture2D(32, 32);

            var configure = viewerType.GetMethod("Configure", BindingFlags.Instance | BindingFlags.Public);
            configure.Invoke(viewer, new object[] { "Test", "Test", 1, new Func<int, Texture2D>(_ => texture) });
            var open = viewerType.GetMethod("Open", BindingFlags.Instance | BindingFlags.Public);
            open.Invoke(viewer, new object[] { 0 });

            InvokePublic(controller, "HandleEscapePressed");

            Assert.That(GetPublicProperty<bool>(controller, "IsOpen"), Is.False);
            UnityEngine.Object.DestroyImmediate(texture);
        }

        [Test]
        public void HandleEscapePressed_WhileCityStylePreviewIsOpen_DoesNotOpenSettings()
        {
            var inputHandlerType = Type.GetType(
                "YC.Presentation.CityStyleDeclarationPreviewInputHandler, Assembly-CSharp",
                false);
            Assert.That(inputHandlerType, Is.Not.Null);
            var previewObject = new GameObject("Open City Style Preview");
            previewObject.transform.SetParent(root.transform, false);
            previewObject.AddComponent(inputHandlerType);

            InvokePublic(controller, "HandleEscapePressed");

            Assert.That(GetPublicProperty<bool>(controller, "IsOpen"), Is.False);
        }

        private static void EnsureAwakeRan(Component component, string readyFieldName)
        {
            var field = component.GetType().GetField(readyFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            if (field.GetValue(component) != null)
            {
                return;
            }

            var awake = component.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(awake, Is.Not.Null);
            awake.Invoke(component, null);
        }

        private static void InvokePublic(object target, string methodName)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null);
            method.Invoke(target, null);
        }

        private static T GetPublicProperty<T>(object target, string propertyName)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null);
            return (T)property.GetValue(target, null);
        }
    }
}
