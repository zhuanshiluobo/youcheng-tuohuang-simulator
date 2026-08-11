using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class CityStyleDeclarationPreviewEditorAssetTests
    {
        private const string CityStylePrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CityStyleDeclarationPreviewDialog.prefab";
        private const string HudPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";

        private GameObject host;

        [TearDown]
        public void TearDown()
        {
            var previewType = GetRuntimeType("YC.Presentation.CityStyleDeclarationPreviewView");
            var previews = UnityEngine.Object.FindObjectsOfType(previewType);
            for (var i = 0; i < previews.Length; i++)
            {
                if (previews[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(((Component)previews[i]).gameObject);
                }
            }

            if (host != null)
            {
                UnityEngine.Object.DestroyImmediate(host);
                host = null;
            }
        }

        [Test]
        public void Prefab_PersistsFixedShellSlotsWarningAndOnlyApprovedDynamicTemplates()
        {
            Assert.That(AssetDatabase.AssetPathToGUID(
                CityStylePrefabPath), Is.Not.Empty);
            var root = PrefabUtility.LoadPrefabContents(CityStylePrefabPath);
            try
            {
                var viewType = GetRuntimeType("YC.Presentation.CityStyleDeclarationPreviewView");
                var inputType = GetRuntimeType("YC.Presentation.CityStyleDeclarationPreviewInputHandler");
                var boardType = GetRuntimeType("YC.Presentation.CityStyleDeclarationBoardPointerHandler");
                var slotType = GetRuntimeType("YC.Presentation.CityStyleDeclarationSlotPointerHandler");
                var dropType = GetRuntimeType("YC.Presentation.CityStyleSpecialActionDropTarget");
                var view = root.GetComponent(viewType);
                Assert.That(view, Is.Not.Null);
                AssertValid(view);
                Assert.That(CountMissingScripts(root), Is.Zero);
                Assert.That(root.name, Is.EqualTo("CityStyleDeclarationPreviewDialog"));
                var requiredSlotCount = (int)viewType.GetProperty(
                    "RequiredCityBoardSlotCount",
                    BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                var actualSlotCount = GetProperty<int>(view, "CityBoardSlotCount");
                Assert.That(actualSlotCount, Is.EqualTo(requiredSlotCount));
                Assert.That(actualSlotCount, Is.EqualTo(12));
                Assert.That(root.GetComponentsInChildren(inputType, true).Length, Is.EqualTo(1));
                Assert.That(root.GetComponentsInChildren(boardType, true).Length, Is.EqualTo(1));
                Assert.That(root.GetComponentsInChildren(slotType, true).Length, Is.EqualTo(requiredSlotCount));
                Assert.That(root.GetComponentsInChildren(dropType, true).Length, Is.EqualTo(1));

                var canvas = root.GetComponent<Canvas>();
                Assert.That(canvas, Is.Not.Null);
                Assert.That(canvas.overrideSorting, Is.True);
                Assert.That(canvas.sortingOrder, Is.EqualTo(130));
                Assert.That(Find(root, "InfluenceMarker Template").gameObject.activeSelf, Is.False);
                Assert.That(Find(root, "SpecialActionDropTarget Template").gameObject.activeSelf, Is.False);
                Assert.That(GetProperty<GameObject>(view, "SpecialActionWarningObject").activeSelf, Is.False);
                Assert.That(FindAll(root, "宣告槽位 ").Count,
                    Is.EqualTo(requiredSlotCount));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        public void RegistryHudAndSampleScene_KeepStrongPrefabConnectionAndSceneBaseline()
        {
            var hudRoot = PrefabUtility.LoadPrefabContents(
                HudPrefabPath);
            try
            {
                var hudType = GetRuntimeType("YC.Presentation.GameplayInteractionHudView");
                var hud = hudRoot.GetComponent(hudType);
                Assert.That(hud, Is.Not.Null);
                AssertValid(hud);
                Assert.That(CountMissingScripts(hudRoot), Is.Zero);
                var registry = GetProperty<Component>(hud, "DialogRegistry");
                var cityStylePrefab = GetProperty<Component>(registry, "CityStyleDeclarationPreviewPrefab");
                Assert.That(cityStylePrefab, Is.Not.Null);
                Assert.That(
                    AssetDatabase.GetAssetPath(cityStylePrefab),
                    Is.EqualTo(CityStylePrefabPath));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(hudRoot);
            }

            var scene = EditorSceneManager.OpenScene(
                SampleScenePath,
                OpenSceneMode.Single);
            Assert.That(scene.GetRootGameObjects().Length, Is.EqualTo(6));
            Assert.That(FindAllInScene<EventSystem>(scene).Length, Is.EqualTo(1));
            var hudViews = FindAllInScene(
                scene,
                GetRuntimeType("YC.Presentation.GameplayInteractionHudView"));
            Assert.That(hudViews.Length, Is.EqualTo(1));
            Assert.That(CountMissingScripts(scene), Is.Zero);
            Assert.That(scene.isDirty, Is.False);
            Assert.That(
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(
                    hudViews[0].gameObject),
                Is.EqualTo(HudPrefabPath));
        }

        [Test]
        public void ProductionDialog_UsesRegistryViewAndContainsNoRuntimeUiConstruction()
        {
            var source = File.ReadAllText(Path.Combine(
                UnityEngine.Application.dataPath,
                "YC/Presentation/CityStyleDeclarationPreviewDialog.cs"));
            StringAssert.DoesNotContain("new GameObject", source);
            StringAssert.DoesNotContain("AddComponent", source);
            StringAssert.DoesNotContain("Resources.", source);
            StringAssert.DoesNotContain("GameObject.Find", source);
            StringAssert.Contains("InstantiateCityStyleDeclarationPreview", source);
            StringAssert.Contains("view.CreateInfluenceMarker", source);
            StringAssert.Contains("view.CreateSpecialActionDropTarget", source);
            StringAssert.Contains("FacilityCardDragUtility.CreateDragGhost", source);
        }

        [Test]
        public void TerminalConfirmAndCancel_DispatchOnceAfterImmediateHide_AndShowToShowDoesNotCancel()
        {
            host = new GameObject("City Style Asset Lifecycle Host", typeof(RectTransform));
            var registry = LoadRegistry();
            var dialogType = Type.GetType(
                "YC.Presentation.CityStyleDeclarationPreviewDialog, Assembly-CSharp",
                true);
            var dialog = Activator.CreateInstance(
                dialogType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[]
                {
                    registry,
                    new Func<RectTransform>(() => host.GetComponent<RectTransform>())
                },
                null);
            var show = dialogType.GetMethod("Show", BindingFlags.Instance | BindingFlags.Public);

            var confirmCount = 0;
            var confirmObservedHidden = false;
            var confirmModel = CreateModel(
                () => { },
                (styleId, slots) =>
                {
                    confirmCount += 1;
                    confirmObservedHidden = !(bool)dialogType.GetProperty("IsShowing").GetValue(dialog, null);
                    return true;
                });
            show.Invoke(dialog, new object[] { confirmModel });
            var viewType = GetRuntimeType("YC.Presentation.CityStyleDeclarationPreviewView");
            var confirmView = host.GetComponentInChildren(viewType, true);
            Assert.That(confirmView.gameObject.name, Is.EqualTo("City Style Declaration Preview Canvas"));
            Assert.That(confirmView.transform.parent, Is.SameAs(host.transform));
            Assert.That(confirmView.gameObject.activeSelf, Is.True);
            var confirmButton = GetProperty<Button>(confirmView, "ConfirmDeclarationButton");
            Assert.That(confirmButton.interactable, Is.True);
            var confirmEvent = confirmButton.onClick;
            confirmEvent.Invoke();
            confirmEvent.Invoke();
            Assert.That(confirmCount, Is.EqualTo(1));
            Assert.That(confirmObservedHidden, Is.True);
            Assert.That((bool)dialogType.GetProperty("IsShowing").GetValue(dialog, null), Is.False);

            var cancelCount = 0;
            var cancelObservedHidden = false;
            var cancelModel = CreateModel(
                () =>
                {
                    cancelCount += 1;
                    cancelObservedHidden = !(bool)dialogType.GetProperty("IsShowing").GetValue(dialog, null);
                },
                (styleId, slots) => false);
            show.Invoke(dialog, new object[] { cancelModel });
            var cancelView = host.GetComponentInChildren(viewType, true);
            var closeEvent = GetProperty<Button>(cancelView, "CloseButton").onClick;
            closeEvent.Invoke();
            closeEvent.Invoke();
            Assert.That(cancelCount, Is.EqualTo(1));
            Assert.That(cancelObservedHidden, Is.True);

            var replacementCancelCount = 0;
            var replacementModel = CreateModel(
                () => replacementCancelCount += 1,
                (styleId, slots) => false);
            show.Invoke(dialog, new object[] { replacementModel });
            var oldView = host.GetComponentInChildren(viewType, true);
            show.Invoke(dialog, new object[] { replacementModel });
            var newView = host.GetComponentInChildren(viewType, true);
            Assert.That(replacementCancelCount, Is.Zero);
            Assert.That(newView, Is.Not.Null);
            Assert.That(newView, Is.Not.SameAs(oldView));
            Assert.That(oldView == null || !oldView.gameObject.activeSelf, Is.True);
        }

        private static CityStyleOptionsViewModel CreateModel(
            Action cancel,
            Func<string, IReadOnlyList<int>, bool> confirm)
        {
            return new CityStyleOptionsViewModel(
                new List<CityStyleOptionViewModel>
                {
                    new CityStyleOptionViewModel
                    {
                        CityStyleId = "asset-test-style",
                        Name = "资产测试样式",
                        CanDeclare = true
                    }
                }.AsReadOnly(),
                new List<CityBoardSlotViewModel>().AsReadOnly(),
                new List<CityStyleMarkerViewModel>().AsReadOnly(),
                "asset-test-style",
                (styleId, slots) => new CityStyleSelectionValidationViewModel(
                    true, string.Empty, 0, 0, slots.Count),
                confirm,
                null,
                cancel);
        }

        private static Component LoadRegistry()
        {
            var hudPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(HudPrefabPath);
            Assert.That(hudPrefab, Is.Not.Null);
            var registry = hudPrefab.GetComponentInChildren(
                GetRuntimeType("YC.Presentation.GameplayDialogRegistry"),
                true);
            Assert.That(registry, Is.Not.Null);
            return registry;
        }

        private static int CountMissingScripts(GameObject root)
        {
            var missing = 0;
            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                missing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(
                    transforms[i].gameObject);
            }

            return missing;
        }

        private static int CountMissingScripts(UnityEngine.SceneManagement.Scene scene)
        {
            var missing = 0;
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                missing += CountMissingScripts(roots[i]);
            }

            return missing;
        }

        private static Transform Find(GameObject root, string objectName)
        {
            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i].name == objectName)
                {
                    return transforms[i];
                }
            }

            return null;
        }

        private static List<Transform> FindAll(GameObject root, string namePrefix)
        {
            var result = new List<Transform>();
            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i].name.StartsWith(namePrefix, StringComparison.Ordinal))
                {
                    result.Add(transforms[i]);
                }
            }

            return result;
        }

        private static T[] FindAllInScene<T>(UnityEngine.SceneManagement.Scene scene)
            where T : Component
        {
            var result = new List<T>();
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                result.AddRange(roots[i].GetComponentsInChildren<T>(true));
            }

            return result.ToArray();
        }

        private static Component[] FindAllInScene(
            UnityEngine.SceneManagement.Scene scene,
            Type componentType)
        {
            var result = new List<Component>();
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                result.AddRange(roots[i].GetComponentsInChildren(componentType, true));
            }

            return result.ToArray();
        }

        private static void AssertValid(Component component)
        {
            var arguments = new object[] { string.Empty };
            var valid = (bool)component.GetType().GetMethod("TryValidateConfiguration")
                .Invoke(component, arguments);
            Assert.That(valid, Is.True, arguments[0] as string);
        }

        private static T GetProperty<T>(object owner, string propertyName)
        {
            return (T)owner.GetType().GetProperty(propertyName).GetValue(owner, null);
        }

        private static Type GetRuntimeType(string fullName)
        {
            var type = Type.GetType(fullName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "缺少运行时类型：" + fullName);
            return type;
        }
    }
}
