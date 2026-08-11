using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class ViewerEditorAssetTests
    {
        private const string SettingsPrefabPath =
            "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab";

        [Test]
        public void ZoomablePrefab_HasCompleteSerializedViewAndStartsClosed()
        {
            var prefab = LoadPrefab(ViewerPrefabTestUtility.ZoomablePrefabPath);
            var controllerType = GetRuntimeType("YC.Presentation.ZoomableImageViewerController");
            var viewType = GetRuntimeType("YC.Presentation.ZoomableImageViewerView");
            var controller = prefab.GetComponent(controllerType);
            var view = prefab.GetComponentInChildren(viewType, true);
            Assert.That(controller, Is.Not.Null);
            Assert.That(view, Is.Not.Null);
            Assert.That(new SerializedObject(controller).FindProperty("view").objectReferenceValue, Is.SameAs(view));
            AssertViewValid(viewType, view);

            var viewData = new SerializedObject(view);
            Assert.That(viewData.FindProperty("rootObject").objectReferenceValue, Is.Not.Null);
            Assert.That(viewData.FindProperty("panelTransform").objectReferenceValue, Is.Not.Null);
            Assert.That(viewData.FindProperty("viewportTransform").objectReferenceValue, Is.Not.Null);
            Assert.That(viewData.FindProperty("imageTransform").objectReferenceValue, Is.Not.Null);
            Assert.That(viewData.FindProperty("closeButton").objectReferenceValue, Is.Not.Null);
            Assert.That(viewData.FindProperty("previousButton").objectReferenceValue, Is.Not.Null);
            Assert.That(viewData.FindProperty("nextButton").objectReferenceValue, Is.Not.Null);
            Assert.That(viewData.FindProperty("primaryActionButton").objectReferenceValue, Is.Not.Null);
            Assert.That(viewData.FindProperty("secondaryActionButton").objectReferenceValue, Is.Not.Null);
            Assert.That(viewData.FindProperty("collapseToggleButton").objectReferenceValue, Is.Not.Null);
            var overlay = viewData.FindProperty("rootObject").objectReferenceValue as GameObject;
            Assert.That(overlay.activeSelf, Is.False);
        }

        [Test]
        public void RulebookPrefab_BindsAllPagesAndKeepsConnectedZoomableNestedPrefab()
        {
            var prefab = LoadPrefab(ViewerPrefabTestUtility.RulebookPrefabPath);
            var controllerType = GetRuntimeType("YC.Presentation.RulebookViewerController");
            var viewType = GetRuntimeType("YC.Presentation.RulebookViewerView");
            var zoomType = GetRuntimeType("YC.Presentation.ZoomableImageViewerController");
            var controller = prefab.GetComponent(controllerType);
            var view = prefab.GetComponent(viewType);
            var zoom = prefab.GetComponentInChildren(zoomType, true);
            Assert.That(controller, Is.Not.Null);
            Assert.That(view, Is.Not.Null);
            Assert.That(zoom, Is.Not.Null);
            Assert.That(new SerializedObject(controller).FindProperty("view").objectReferenceValue, Is.SameAs(view));
            AssertViewValid(viewType, view);

            var viewData = new SerializedObject(view);
            var pages = viewData.FindProperty("pages");
            Assert.That(pages.arraySize, Is.EqualTo(28));
            for (var i = 0; i < pages.arraySize; i++)
            {
                var texture = pages.GetArrayElementAtIndex(i).objectReferenceValue;
                Assert.That(texture, Is.Not.Null, "page " + (i + 1));
                Assert.That(
                    AssetDatabase.GetAssetPath(texture),
                    Is.EqualTo(
                        "Assets/Resources/RulebookPages/page_" +
                        (i + 1).ToString("00") +
                        ".jpg"),
                    "page order " + (i + 1));
            }

            AssertNestedPrefabSource(zoom.gameObject, ViewerPrefabTestUtility.ZoomablePrefabPath);
        }

        [Test]
        public void ActionLogPrefab_HasInactiveOverlayAndEditorRowTemplate()
        {
            var prefab = LoadPrefab(ViewerPrefabTestUtility.ActionLogPrefabPath);
            var controllerType = GetRuntimeType("YC.Presentation.ActionLogViewerController");
            var viewType = GetRuntimeType("YC.Presentation.ActionLogViewerView");
            var rowType = GetRuntimeType("YC.Presentation.ActionLogRowView");
            var controller = prefab.GetComponent(controllerType);
            var view = prefab.GetComponentInChildren(viewType, true);
            var row = prefab.GetComponentInChildren(rowType, true);
            Assert.That(controller, Is.Not.Null);
            Assert.That(view, Is.Not.Null);
            Assert.That(row, Is.Not.Null);
            Assert.That(new SerializedObject(controller).FindProperty("view").objectReferenceValue, Is.SameAs(view));
            AssertViewValid(viewType, view);
            AssertViewValid(rowType, row);

            var viewData = new SerializedObject(view);
            var overlay = viewData.FindProperty("overlayObject").objectReferenceValue as GameObject;
            Assert.That(overlay.activeSelf, Is.False);
            Assert.That(row.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void SettingsPrefab_UsesConnectedViewerPrefabsAndExplicitZoomableTemplate()
        {
            var prefab = LoadPrefab(SettingsPrefabPath);
            var settingsType = GetRuntimeType("YC.Presentation.GameSettingsMenuController");
            var rulebookType = GetRuntimeType("YC.Presentation.RulebookViewerController");
            var actionLogType = GetRuntimeType("YC.Presentation.ActionLogViewerController");
            var settings = prefab.GetComponent(settingsType);
            var rulebook = prefab.GetComponentInChildren(rulebookType, true);
            var actionLog = prefab.GetComponentInChildren(actionLogType, true);
            Assert.That(settings, Is.Not.Null);
            Assert.That(rulebook, Is.Not.Null);
            Assert.That(actionLog, Is.Not.Null);
            var data = new SerializedObject(settings);
            Assert.That(data.FindProperty("rulebookViewer").objectReferenceValue, Is.SameAs(rulebook));
            Assert.That(data.FindProperty("actionLogViewer").objectReferenceValue, Is.SameAs(actionLog));
            Assert.That(data.FindProperty("zoomableImageViewerPrefab").objectReferenceValue, Is.Not.Null);
            AssertNestedPrefabSource(rulebook.gameObject, ViewerPrefabTestUtility.RulebookPrefabPath);
            AssertNestedPrefabSource(actionLog.gameObject, ViewerPrefabTestUtility.ActionLogPrefabPath);
        }

        [TestCase(ViewerPrefabTestUtility.ZoomablePrefabPath)]
        [TestCase(ViewerPrefabTestUtility.RulebookPrefabPath)]
        [TestCase(ViewerPrefabTestUtility.ActionLogPrefabPath)]
        public void ViewerPrefab_HasNoMissingScripts(string path)
        {
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var transforms = contents.GetComponentsInChildren<Transform>(true);
                for (var i = 0; i < transforms.Length; i++)
                {
                    Assert.That(
                        GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transforms[i].gameObject),
                        Is.Zero,
                        transforms[i].GetHierarchyPath());
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        [Test]
        public void ViewerControllers_DoNotConstructOrDiscoverStaticShells()
        {
            AssertStaticShellGuard("YC/Presentation/RulebookViewerController.cs");
            AssertStaticShellGuard("YC/Presentation/ActionLogViewerController.cs");
            AssertStaticShellGuard("YC/Presentation/ZoomableImageViewerController.cs");

            var actionLog = ReadSource("YC/Presentation/ActionLogViewerController.cs");
            StringAssert.Contains("Instantiate(view.RowTemplate", actionLog);
            StringAssert.Contains("动态边界", actionLog);
            var zoomable = ReadSource("YC/Presentation/ZoomableImageViewerController.cs");
            StringAssert.Contains("Instantiate(registeredPrefab", zoomable);
            StringAssert.Contains("动态边界", zoomable);
            var rulebook = ReadSource("YC/Presentation/RulebookViewerController.cs");
            StringAssert.Contains("序列化数组绑定", rulebook);
        }

        [Test]
        public void MobileCity_SettingsPathHasNoReplacementFallback()
        {
            var source = ReadSource("YC/Presentation/MobileCityInteractionController.cs");
            var body = ExtractMethodBody(source, "private void EnsureSettingsMenu()");
            StringAssert.Contains("settingsMenu.ConfigureActionLog(session);", body);
            StringAssert.Contains("Debug.LogError", body);
            StringAssert.DoesNotContain("new GameObject", body);
            StringAssert.DoesNotContain("AddComponent", body);
            StringAssert.DoesNotContain("Find", body);
            StringAssert.DoesNotContain("Resources.", body);
            StringAssert.DoesNotContain("EnsureInScene", source);
        }

        [Test]
        public void RegisteredZoomable_OwnerDisableClosesDetachedOverlayAndDestroyCleansCanvas()
        {
            ViewerPrefabTestUtility.RegisterZoomablePrefab();
            var type = GetRuntimeType("YC.Presentation.ZoomableImageViewerController");
            var owner = new GameObject("Zoomable Lifecycle Owner");
            GameObject detachedCanvas = null;
            Texture2D texture = null;
            try
            {
                var instantiate = type.GetMethod(
                    "InstantiateRegistered",
                    BindingFlags.Public | BindingFlags.Static);
                Assert.That(instantiate, Is.Not.Null);
                var controller = instantiate.Invoke(
                    null,
                    new object[] { owner.transform, "Zoomable Lifecycle Viewer" }) as Component;
                Assert.That(controller, Is.Not.Null);
                texture = new Texture2D(32, 32);
                type.GetMethod("Configure", BindingFlags.Public | BindingFlags.Instance).Invoke(
                    controller,
                    new object[] { "Lifecycle", "生命周期", 1, new Func<int, Texture2D>(_ => texture) });
                type.GetMethod("Open", BindingFlags.Public | BindingFlags.Instance).Invoke(
                    controller,
                    new object[] { 0 });

                var view = new SerializedObject(controller).FindProperty("view").objectReferenceValue;
                Assert.That(view, Is.Not.Null);
                var viewData = new SerializedObject(view);
                detachedCanvas = viewData.FindProperty("canvasObject").objectReferenceValue as GameObject;
                var overlay = viewData.FindProperty("rootObject").objectReferenceValue as GameObject;
                Assert.That(detachedCanvas, Is.Not.Null);
                Assert.That(detachedCanvas.transform.parent, Is.Null);
                Assert.That(overlay.activeSelf, Is.True);
                Assert.That(InvokeHasOpenViewer(type), Is.True);

                owner.SetActive(false);
                // 普通 MonoBehaviour 在 EditMode 不保证派发运行时消息，显式调用同一生命周期处理器。
                type.GetMethod("OnDisable", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(controller, null);
                Assert.That(overlay.activeSelf, Is.False);
                Assert.That(InvokeHasOpenViewer(type), Is.False);

                owner.SetActive(true);
                Assert.That(overlay.activeSelf, Is.False, "Re-enabling the owner must not reopen the viewer.");
                Assert.That(InvokeHasOpenViewer(type), Is.False);

                // EditMode 不派发普通 MonoBehaviour.OnDestroy；直接验证同一销毁处理器，
                // PlayMode 冒烟再覆盖真实 Destroy(owner) 的逐帧派发。
                type.GetMethod("OnDestroy", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(controller, null);
                Assert.That(detachedCanvas == null, Is.True, "Detached canvas must be destroyed with its owner.");
                Object.DestroyImmediate(owner);
                owner = null;
            }
            finally
            {
                if (owner != null) Object.DestroyImmediate(owner);
                if (detachedCanvas != null) Object.DestroyImmediate(detachedCanvas);
                if (texture != null) Object.DestroyImmediate(texture);
            }
        }

        private static void AssertStaticShellGuard(string relativePath)
        {
            var source = ReadSource(relativePath);
            StringAssert.DoesNotContain("new GameObject", source, relativePath);
            StringAssert.DoesNotContain("AddComponent", source, relativePath);
            StringAssert.DoesNotContain("EnsureUi", source, relativePath);
            StringAssert.DoesNotContain("BuildUi", source, relativePath);
            StringAssert.DoesNotContain("CreateCanvas", source, relativePath);
            StringAssert.DoesNotContain("FindObjectOfType", source, relativePath);
            StringAssert.DoesNotContain("GameObject.Find", source, relativePath);
            StringAssert.DoesNotContain("Resources.", source, relativePath);
        }

        private static bool InvokeHasOpenViewer(Type type)
        {
            var method = type.GetMethod("HasOpenViewer", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(null, null);
        }

        private static void AssertNestedPrefabSource(GameObject instanceObject, string expectedPath)
        {
            Assert.That(PrefabUtility.IsPartOfPrefabInstance(instanceObject), Is.True, instanceObject.name);
            var source = PrefabUtility.GetCorrespondingObjectFromSource(instanceObject);
            Assert.That(source, Is.Not.Null, instanceObject.name);
            Assert.That(AssetDatabase.GetAssetPath(source), Is.EqualTo(expectedPath));
        }

        private static void AssertViewValid(Type viewType, Component view)
        {
            var validate = viewType.GetMethod("TryValidateConfiguration", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(validate, Is.Not.Null, viewType.FullName);
            var arguments = new object[] { null };
            Assert.That(validate.Invoke(view, arguments), Is.EqualTo(true), arguments[0] as string);
        }

        private static GameObject LoadPrefab(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            return prefab;
        }

        private static Type GetRuntimeType(string fullName)
        {
            return ViewerPrefabTestUtility.GetControllerType(fullName);
        }

        private static string ReadSource(string relativePath)
        {
            return File.ReadAllText(Path.Combine(UnityEngine.Application.dataPath, relativePath));
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), signature);
            start = source.IndexOf('{', start) + 1;
            var depth = 1;
            for (var i = start; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                if (source[i] != '}' || --depth != 0) continue;
                return source.Substring(start, i - start);
            }

            Assert.Fail("Method body not closed: " + signature);
            return string.Empty;
        }
    }
}
