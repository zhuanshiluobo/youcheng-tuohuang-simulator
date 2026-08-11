using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class GameSettingsMenuEditorAssetTests
    {
        private const string PrefabPath = "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab";
        private const string StartScenePath = "Assets/Scenes/StartScene.unity";
        private const string GameScenePath = "Assets/Scenes/SampleScene.unity";

        [Test]
        public void SettingsPrefab_HasCompleteSerializedEditorReferencesAndStaticShell()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab, Is.Not.Null, "Missing editor-authored settings prefab.");

            var controllerType = GetRuntimeType("YC.Presentation.GameSettingsMenuController");
            var viewType = GetRuntimeType("YC.Presentation.GameSettingsMenuView");
            var rulebookType = GetRuntimeType("YC.Presentation.RulebookViewerController");
            var actionLogType = GetRuntimeType("YC.Presentation.ActionLogViewerController");
            var controller = prefab.GetComponent(controllerType);
            var view = prefab.GetComponentInChildren(viewType, true);
            Assert.That(controller, Is.Not.Null);
            Assert.That(view, Is.Not.Null);

            var controllerData = new SerializedObject(controller);
            Assert.That(controllerData.FindProperty("view").objectReferenceValue, Is.SameAs(view));
            Assert.That(controllerData.FindProperty("rulebookViewer").objectReferenceValue, Is.Not.Null);
            Assert.That(controllerData.FindProperty("actionLogViewer").objectReferenceValue, Is.Not.Null);
            Assert.That(controllerData.FindProperty("zoomableImageViewerPrefab").objectReferenceValue, Is.Not.Null);
            Assert.That(prefab.GetComponentInChildren(rulebookType, true), Is.Not.Null);
            Assert.That(prefab.GetComponentInChildren(actionLogType, true), Is.Not.Null);

            var viewData = new SerializedObject(view);
            var referenceNames = new[]
            {
                "canvasTransform",
                "menuPanel",
                "overlayObject",
                "confirmationObject",
                "returnButtonObject",
                "actionLogButtonObject",
                "gearButton",
                "actionLogButton",
                "overlayCloseButton",
                "headerCloseButton",
                "rulebookButton",
                "returnButton",
                "confirmReturnButton",
                "cancelReturnButton"
            };
            for (var i = 0; i < referenceNames.Length; i++)
            {
                var property = viewData.FindProperty(referenceNames[i]);
                Assert.That(property, Is.Not.Null, referenceNames[i]);
                Assert.That(property.objectReferenceValue, Is.Not.Null, referenceNames[i]);
            }

            var arguments = new object[] { null };
            var validate = viewType.GetMethod("TryValidateConfiguration", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(validate, Is.Not.Null);
            Assert.That(validate.Invoke(view, arguments), Is.EqualTo(true), arguments[0] as string);

            Assert.That(prefab.GetComponentInChildren<Canvas>(true), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Settings Gear Button"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Action Log Button"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Settings Overlay"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Settings Panel"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "规则书 Button"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "返回主菜单 Button"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Return Confirmation"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "确认返回 Button"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "取消 Button"), Is.Not.Null);
        }

        [Test]
        public void SettingsPrefab_HasNoMissingScripts()
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
        public void SettingsPrefab_OwnsSingleSerializedFontDriverAndPersistentFonts()
        {
            const string cjkPath = "Assets/YC/Resources/Fonts/CJK/NotoSansCJKsc-Regular.otf";
            const string latinPath = "Assets/YC/Resources/Fonts/Latin/NotoSans-Regular.ttf";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var driverType = GetRuntimeType("YC.Presentation.FontRefreshDriver");
            var drivers = prefab.GetComponentsInChildren(driverType, true);
            Assert.That(drivers, Has.Length.EqualTo(1));
            Assert.That(drivers[0].gameObject, Is.SameAs(prefab));

            var data = new SerializedObject(drivers[0]);
            Assert.That(
                AssetDatabase.GetAssetPath(data.FindProperty("cjkFont").objectReferenceValue),
                Is.EqualTo(cjkPath));
            Assert.That(
                AssetDatabase.GetAssetPath(data.FindProperty("latinFont").objectReferenceValue),
                Is.EqualTo(latinPath));

            var texts = prefab.GetComponentsInChildren<Text>(true);
            Assert.That(texts, Is.Not.Empty);
            for (var i = 0; i < texts.Length; i++)
            {
                Assert.That(AssetDatabase.GetAssetPath(texts[i].font), Is.EqualTo(cjkPath), texts[i].name);
            }
        }

        [TestCase(StartScenePath, false, false)]
        [TestCase(GameScenePath, true, true)]
        public void Scene_ContainsConnectedSettingsPrefabWithExpectedOverrides(
            string scenePath,
            bool expectedReturnButton,
            bool expectsCityBinding)
        {
            var existing = SceneManager.GetSceneByPath(scenePath);
            var openedForTest = !existing.IsValid() || !existing.isLoaded;
            var scene = openedForTest
                ? EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive)
                : existing;

            try
            {
                var root = FindRoot(scene, "Game Settings Menu");
                Assert.That(root, Is.Not.Null, scenePath);
                Assert.That(PrefabUtility.IsPartOfPrefabInstance(root), Is.True, scenePath);
                var source = PrefabUtility.GetCorrespondingObjectFromSource(root);
                Assert.That(source, Is.Not.Null, scenePath);
                Assert.That(AssetDatabase.GetAssetPath(source), Is.EqualTo(PrefabPath), scenePath);

                var controllerType = GetRuntimeType("YC.Presentation.GameSettingsMenuController");
                var controller = root.GetComponent(controllerType);
                Assert.That(controller, Is.Not.Null, scenePath);
                var controllerData = new SerializedObject(controller);
                Assert.That(
                    controllerData.FindProperty("showReturnToStartButton").boolValue,
                    Is.EqualTo(expectedReturnButton),
                    scenePath);

                var city = controllerData.FindProperty("cityInteractionController").objectReferenceValue;
                if (!expectsCityBinding)
                {
                    Assert.That(city, Is.Null, scenePath);
                    return;
                }

                Assert.That(city, Is.Not.Null, scenePath);
                var cityData = new SerializedObject(city);
                Assert.That(cityData.FindProperty("settingsMenu").objectReferenceValue, Is.SameAs(controller));
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
        public void SettingsRuntimeControllers_DoNotConstructOrDiscoverStaticUi()
        {
            var settingsSource = ReadSource("YC/Presentation/GameSettingsMenuController.cs");
            StringAssert.DoesNotContain("new GameObject", settingsSource);
            StringAssert.DoesNotContain("AddComponent", settingsSource);
            StringAssert.DoesNotContain("FindObjectOfType", settingsSource);
            StringAssert.DoesNotContain("GameObject.Find", settingsSource);
            StringAssert.DoesNotContain("Resources.", settingsSource);
            StringAssert.DoesNotContain("EnsureInScene", settingsSource);

            var startMenuSource = ReadSource("YC/Presentation/StartMenuController.cs");
            StringAssert.DoesNotContain("EnsureInScene", startMenuSource);

            var citySource = ReadSource("YC/Presentation/MobileCityInteractionController.cs");
            StringAssert.Contains(
                "[SerializeField] private GameSettingsMenuController settingsMenu;",
                citySource);
            StringAssert.DoesNotContain("GameSettingsMenuController.EnsureInScene", citySource);
        }

        private static Type GetRuntimeType(string fullName)
        {
            var type = Type.GetType(fullName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing " + fullName + ".");
            return type;
        }

        private static string ReadSource(string relativePath)
        {
            return File.ReadAllText(Path.Combine(UnityEngine.Application.dataPath, relativePath));
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
}
