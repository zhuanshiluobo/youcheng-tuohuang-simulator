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
            Assert.That(Find(prefab.transform, "Join Room Panel"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Waiting Room Panel"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Message Panel"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Seat Template"), Is.Not.Null);
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
