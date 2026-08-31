using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class GameSettingsMenuControllerTests
    {
        private const string PrefabPath = "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab";

        private GameObject root;
        private Component controller;

        [SetUp]
        public void SetUp()
        {
            var type = Type.GetType("YC.Presentation.GameSettingsMenuController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.GameSettingsMenuController.");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab, Is.Not.Null, "Missing editor-authored settings prefab.");
            root = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            Assert.That(root, Is.Not.Null);
            controller = root.GetComponent(type);
            Assert.That(controller, Is.Not.Null);
            EnsureAwakeRan(controller);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(root);
        }

        [Test]
        public void HandleEscapePressed_WhenClosed_OpensSettingsOverlay()
        {
            var overlay = root.transform.Find("Game Settings Canvas/Settings Overlay");

            InvokePublic(controller, "HandleEscapePressed");

            Assert.That(overlay, Is.Not.Null);
            Assert.That(overlay.gameObject.activeSelf, Is.True);
            Assert.That(GetPublicProperty<bool>(controller, "IsOpen"), Is.True);
            Assert.That(
                root.transform.Find("Game Settings Canvas/Settings Overlay/Settings Panel")
                    .GetComponent<RectTransform>().anchoredPosition,
                Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void HandleEscapePressed_WhenOpen_ClosesSettingsImmediately()
        {
            var overlay = root.transform.Find("Game Settings Canvas/Settings Overlay");
            InvokePublic(controller, "HandleEscapePressed");

            InvokePublic(controller, "HandleEscapePressed");

            Assert.That(GetPublicProperty<bool>(controller, "IsOpen"), Is.False);
            Assert.That(overlay.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void ClickingOutsidePanel_DoesNotCloseSettings()
        {
            var overlay = root.transform.Find("Game Settings Canvas/Settings Overlay");
            InvokePublic(controller, "Open");

            overlay.GetComponent<Button>().onClick.Invoke();

            Assert.That(GetPublicProperty<bool>(controller, "IsOpen"), Is.True);
            Assert.That(overlay.gameObject.activeSelf, Is.True);
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
        public void Prefab_DoesNotContainHintCardButton()
        {
            var hintButton = root.transform.Find("Game Settings Canvas/Hint Card Button");
            Assert.That(hintButton, Is.Null);
        }

        [Test]
        public void Prefab_DefaultsToGeneralTabWithFourResolutionOptions()
        {
            var generalContent = root.transform.Find(
                "Game Settings Canvas/Settings Overlay/Settings Panel/Settings Body/通用 Content");
            var futureContent = root.transform.Find(
                "Game Settings Canvas/Settings Overlay/Settings Panel/Settings Body/占位 Content");
            var generalTab = root.transform.Find(
                "Game Settings Canvas/Settings Overlay/Settings Panel/Settings Body/通用 Button");
            var dropdownTransform = root.transform.Find(
                "Game Settings Canvas/Settings Overlay/Settings Panel/Settings Body/通用 Content/分辨率 Dropdown");

            Assert.That(generalContent, Is.Not.Null);
            Assert.That(generalContent.gameObject.activeSelf, Is.True);
            Assert.That(futureContent, Is.Not.Null);
            Assert.That(futureContent.gameObject.activeSelf, Is.False);
            Assert.That(generalTab.GetComponent<Button>().interactable, Is.False);
            Assert.That(dropdownTransform, Is.Not.Null);

            var dropdown = dropdownTransform.GetComponent<Dropdown>();
            Assert.That(dropdown, Is.Not.Null);
            Assert.That(dropdown.options, Has.Count.EqualTo(4));
            Assert.That(dropdown.options[0].text, Is.EqualTo("1280 × 720"));
            Assert.That(dropdown.options[1].text, Is.EqualTo("1600 × 900"));
            Assert.That(dropdown.options[2].text, Is.EqualTo("1920 × 1080"));
            Assert.That(dropdown.options[3].text, Is.EqualTo("全屏"));
        }

        [Test]
        public void PlaceholderTab_ShowsFutureContentAndHidesGeneralContent()
        {
            var generalContent = root.transform.Find(
                "Game Settings Canvas/Settings Overlay/Settings Panel/Settings Body/通用 Content");
            var futureContent = root.transform.Find(
                "Game Settings Canvas/Settings Overlay/Settings Panel/Settings Body/占位 Content");
            var placeholderTab = root.transform.Find(
                "Game Settings Canvas/Settings Overlay/Settings Panel/Settings Body/占位 Button")
                .GetComponent<Button>();

            placeholderTab.onClick.Invoke();

            Assert.That(generalContent.gameObject.activeSelf, Is.False);
            Assert.That(futureContent.gameObject.activeSelf, Is.True);
            Assert.That(placeholderTab.interactable, Is.False);
        }

        [Test]
        public void HandleEscapePressed_WhileImageViewerIsOpen_DoesNotOpenSettings()
        {
            var viewerType = Type.GetType("YC.Presentation.ZoomableImageViewerController, Assembly-CSharp", false);
            Assert.That(viewerType, Is.Not.Null);
            var viewerObject = ViewerPrefabTestUtility.Instantiate(
                ViewerPrefabTestUtility.ZoomablePrefabPath);
            viewerObject.name = "Open Image Viewer";
            viewerObject.transform.SetParent(root.transform, false);
            var viewer = viewerObject.GetComponent(viewerType);
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

        private static void EnsureAwakeRan(Component component)
        {
            var field = component.GetType().GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            if ((bool)field.GetValue(component))
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
