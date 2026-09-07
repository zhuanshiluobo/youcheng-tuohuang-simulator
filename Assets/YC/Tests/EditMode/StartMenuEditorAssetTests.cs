using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class StartMenuEditorAssetTests
    {
        private const string PrefabPath = "Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab";
        private const string ScenePath = "Assets/Scenes/StartScene.unity";
        private const string LoadingScenePath = "Assets/Scenes/LoadingScene.unity";

        [Test]
        public void StartMenuPrefab_HasCompleteSerializedEditorReferences()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab, Is.Not.Null, "Missing editor-authored StartMenuRoot prefab.");

            var controllerType = Type.GetType("YC.Presentation.StartMenuController, Assembly-CSharp", false);
            var viewType = Type.GetType("YC.Presentation.StartMenuView, Assembly-CSharp", false);
            Assert.That(controllerType, Is.Not.Null);
            Assert.That(viewType, Is.Not.Null);

            var controller = prefab.GetComponent(controllerType);
            var view = prefab.GetComponentInChildren(viewType, true);
            Assert.That(controller, Is.Not.Null);
            Assert.That(view, Is.Not.Null);

            var controllerData = new SerializedObject(controller);
            Assert.That(controllerData.FindProperty("view").objectReferenceValue, Is.SameAs(view));
            Assert.That(controllerData.FindProperty("coverTexture").objectReferenceValue, Is.Not.Null);

            var arguments = new object[] { null };
            var validate = viewType.GetMethod("TryValidateConfiguration", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(validate, Is.Not.Null);
            Assert.That(validate.Invoke(view, arguments), Is.EqualTo(true), arguments[0] as string);

            Assert.That(prefab.GetComponentInChildren<Canvas>(true), Is.Not.Null);
            Assert.That(prefab.GetComponentInChildren<UnityEngine.EventSystems.EventSystem>(true), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Map Selection Panel"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Online Mode Panel"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Achievements Panel"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Join Room Panel"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Waiting Room Panel"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Message Panel"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Seat Template"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Creator Link Button"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Steam 双人验证 Button"), Is.Null);
        }

        [Test]
        public void LoadingScene_HasOpaquePersistentTransitionCanvasAndBuildEntry()
        {
            var existing = SceneManager.GetSceneByPath(LoadingScenePath);
            var openedForTest = !existing.IsValid() || !existing.isLoaded;
            var scene = openedForTest
                ? EditorSceneManager.OpenScene(LoadingScenePath, OpenSceneMode.Additive)
                : existing;

            try
            {
                var root = FindRoot(scene, "Scene Transition");
                Assert.That(root, Is.Not.Null);
                var controllerType = Type.GetType(
                    "YC.Presentation.LoadingSceneController, Assembly-CSharp",
                    false);
                var controller = root.GetComponent(controllerType) as MonoBehaviour;
                Assert.That(controller, Is.Not.Null);
                var loadingCamera = Find(root.transform, "Loading Camera").GetComponent<Camera>();
                Assert.That(loadingCamera, Is.Not.Null);
                Assert.That(loadingCamera.clearFlags, Is.EqualTo(CameraClearFlags.SolidColor));
                Assert.That(loadingCamera.backgroundColor, Is.EqualTo(new Color(0f, 0f, 0f, 0f)));
                Assert.That(loadingCamera.cullingMask, Is.EqualTo(1 << 31));
                Assert.That(loadingCamera.targetTexture, Is.Not.Null);
                var modelPivot = Find(root.transform, "Originite Model Pivot");
                Assert.That(modelPivot, Is.Not.Null);
                Assert.That(modelPivot.GetComponentInChildren<Renderer>(true), Is.Not.Null);
                var dragType = Type.GetType(
                    "YC.Presentation.LoadingModelDragController, Assembly-CSharp",
                    false);
                Assert.That(dragType, Is.Not.Null);
                var modelDisplay = Find(root.transform, "Originite Display");
                Assert.That(modelDisplay, Is.Not.Null);
                var dragController = modelDisplay.GetComponent(dragType) as MonoBehaviour;
                Assert.That(dragController, Is.Not.Null);
                var dragData = new SerializedObject(dragController);
                Assert.That(dragData.FindProperty("rotationTarget").objectReferenceValue, Is.SameAs(modelPivot));
                var group = root.GetComponentInChildren<CanvasGroup>(true);
                Assert.That(group, Is.Not.Null);
                Assert.That(group.alpha, Is.EqualTo(0f));
                Assert.That(group.GetComponent<Canvas>().sortingOrder, Is.EqualTo(short.MaxValue));
                Assert.That(Find(root.transform, "Black Screen").GetComponent<Image>().color, Is.EqualTo(Color.black));
                var progressRoot = Find(root.transform, "Loading Progress") as RectTransform;
                Assert.That(progressRoot, Is.Not.Null);
                var progressGroup = progressRoot.GetComponent<CanvasGroup>();
                var tipsText = Find(root.transform, "Tips Text").GetComponent<Text>();
                var progressText = Find(root.transform, "Progress Text").GetComponent<Text>();
                var progressFill = Find(root.transform, "Progress Fill") as RectTransform;
                Assert.That(progressRoot.anchorMin.y, Is.EqualTo(0f));
                Assert.That(progressRoot.anchoredPosition.y, Is.GreaterThan(0f));
                Assert.That(progressGroup, Is.Not.Null);
                Assert.That(progressGroup.alpha, Is.EqualTo(0f));
                Assert.That(tipsText, Is.Not.Null);
                StringAssert.StartsWith("拓荒提示：", tipsText.text);
                Assert.That(progressText, Is.Not.Null);
                Assert.That(progressFill, Is.Not.Null);
                controllerType.GetMethod("SetProgress")?.Invoke(controller, new object[] { 0.5f });
                Assert.That(progressText.text, Is.EqualTo("加载中 50%"));
                Assert.That(progressFill.rect.width, Is.EqualTo(350f).Within(0.01f));
                Assert.That(
                    Array.Exists(EditorBuildSettings.scenes, entry => entry.path == LoadingScenePath && entry.enabled),
                    Is.True);
            }
            finally
            {
                if (openedForTest)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        [Test]
        public void StartMenuPrefab_RightButtonsUseBottomRightTransparentBandStyle()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab, Is.Not.Null);

            AssertTransparentMainButton(prefab.transform, "本地游戏 Button");
            AssertTransparentMainButton(prefab.transform, "联机模式 Button");
            AssertTransparentMainButton(prefab.transform, "成就 Button");
        }

        [Test]
        public void StartMenuPrefab_HasNoMissingScripts()
        {
            var contents = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var transforms = contents.GetComponentsInChildren<Transform>(true);
                for (var i = 0; i < transforms.Length; i++)
                {
                    Assert.That(
                        GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transforms[i].gameObject),
                        Is.EqualTo(0),
                        transforms[i].GetHierarchyPath());
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        [Test]
        public void StartScene_ContainsConnectedStartMenuPrefabInstance()
        {
            var existing = SceneManager.GetSceneByPath(ScenePath);
            var openedForTest = !existing.IsValid() || !existing.isLoaded;
            var scene = openedForTest
                ? EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive)
                : existing;

            try
            {
                var startMenu = FindRoot(scene, "Start Menu");
                Assert.That(startMenu, Is.Not.Null);
                Assert.That(PrefabUtility.IsPartOfPrefabInstance(startMenu), Is.True);
                var source = PrefabUtility.GetCorrespondingObjectFromSource(startMenu);
                Assert.That(source, Is.Not.Null);
                Assert.That(AssetDatabase.GetAssetPath(source), Is.EqualTo(PrefabPath));
            }
            finally
            {
                if (openedForTest)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        [Test]
        public void StartMenuController_DoesNotConstructStaticUiAtRuntime()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var controllerType = Type.GetType("YC.Presentation.StartMenuController, Assembly-CSharp", false);
            var controller = prefab.GetComponent(controllerType) as MonoBehaviour;
            Assert.That(controller, Is.Not.Null);

            var script = MonoScript.FromMonoBehaviour(controller);
            var source = File.ReadAllText(AssetDatabase.GetAssetPath(script));
            Assert.That(source, Does.Not.Contain("new GameObject"));
            Assert.That(source, Does.Not.Contain("FindObjectOfType<StartMenuView"));
            Assert.That(source, Does.Not.Contain("Resources.Load<StartMenuView"));
        }

        private static GameObject FindRoot(Scene scene, string name)
        {
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                if (roots[i].name == name)
                {
                    return roots[i];
                }
            }

            return null;
        }

        private static Transform Find(Transform root, string name)
        {
            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i].name == name)
                {
                    return transforms[i];
                }
            }

            return null;
        }

        private static void AssertTransparentMainButton(Transform root, string name)
        {
            var target = Find(root, name) as RectTransform;
            Assert.That(target, Is.Not.Null, name);
            Assert.That(target.anchorMin, Is.EqualTo(new Vector2(1f, 0f)), name);
            Assert.That(target.anchorMax, Is.EqualTo(new Vector2(1f, 0f)), name);
            Assert.That(target.sizeDelta, Is.EqualTo(new Vector2(420f, 84f)), name);
            Assert.That(target.GetComponent<Outline>(), Is.Null, name);

            var button = target.GetComponent<Button>();
            Assert.That(button, Is.Not.Null, name);
            Assert.That(button.colors.normalColor.a, Is.EqualTo(0f).Within(0.001f), name);
            Assert.That(button.colors.highlightedColor.a, Is.EqualTo(0.22f).Within(0.001f), name);
            Assert.That(button.colors.pressedColor.a, Is.EqualTo(0.55f).Within(0.001f), name);
            var feedbackType = Type.GetType(
                "YC.Presentation.ActionButtonPressFeedback, Assembly-CSharp",
                false);
            Assert.That(feedbackType, Is.Not.Null, name);
            Assert.That(target.GetComponent(feedbackType), Is.Not.Null, name);
        }
    }

    internal static class TransformHierarchyPathExtensions
    {
        public static string GetHierarchyPath(this Transform transform)
        {
            var path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }

            return path;
        }
    }
}
