using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class GameplayDialogEditorAssetTests
    {
        private const string EffectPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/EffectDialogShell.prefab";
        private const string DispatchPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/DispatchDecisionDialog.prefab";
        private const string EventChoicePrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/EventChoiceDialog.prefab";
        private const string HudPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";

        [Test]
        public void StandalonePrefabs_HaveCompleteSerializedViewsAndNoMissingScripts()
        {
            var effectPrefab = LoadPrefab(EffectPrefabPath);
            var dispatchPrefab = LoadPrefab(DispatchPrefabPath);
            var eventChoicePrefab = LoadPrefab(EventChoicePrefabPath);
            var effect = effectPrefab.GetComponent(GetRuntimeType("YC.Presentation.EffectDialogShellView"));
            var dispatch = dispatchPrefab.GetComponent(GetRuntimeType("YC.Presentation.DispatchDecisionDialogView"));
            var eventChoice = eventChoicePrefab.GetComponent(GetRuntimeType("YC.Presentation.EventChoiceDialogView"));

            Assert.That(effect, Is.Not.Null);
            Assert.That(dispatch, Is.Not.Null);
            Assert.That(eventChoice, Is.Not.Null);
            AssertValid(effect);
            AssertValid(dispatch);
            AssertValid(eventChoice);
            AssertNoMissingScripts(EffectPrefabPath);
            AssertNoMissingScripts(DispatchPrefabPath);
            AssertNoMissingScripts(EventChoicePrefabPath);
            AssertAllSerializedObjectReferencesHaveAssetIds(effectPrefab);
            AssertAllSerializedObjectReferencesHaveAssetIds(dispatchPrefab);
            AssertAllSerializedObjectReferencesHaveAssetIds(eventChoicePrefab);
        }

        [Test]
        public void EventChoicePrefab_HasSevenInactiveModesAndFiveInactiveDynamicTemplates()
        {
            var prefab = LoadPrefab(EventChoicePrefabPath);
            var view = prefab.GetComponent(GetRuntimeType("YC.Presentation.EventChoiceDialogView"));
            var serialized = new SerializedObject(view);
            var modeProperties = new[]
            {
                "eventCardMode",
                "explorePathMode",
                "explorePaymentMode",
                "resourceCollectionPaymentMode",
                "buildFacilityFocusMode",
                "buildFacilityConfirmationMode",
                "characterSecondEffectDecisionMode"
            };
            for (var i = 0; i < modeProperties.Length; i++)
            {
                var block = GetObjectReference<GameObject>(view, modeProperties[i]);
                Assert.That(block.activeSelf, Is.False, modeProperties[i]);
            }

            var templateProperties = new[]
            {
                "choiceRowTemplate",
                "pathRowTemplate",
                "paymentRouteRowTemplate",
                "paymentRecipientButtonTemplate",
                "resourceCollectionRecipientButtonTemplate"
            };
            for (var i = 0; i < templateProperties.Length; i++)
            {
                var container = serialized.FindProperty(templateProperties[i]);
                Assert.That(container, Is.Not.Null, templateProperties[i]);
                var root = container.FindPropertyRelative("root").objectReferenceValue as RectTransform;
                Assert.That(root, Is.Not.Null, templateProperties[i] + ".root");
                Assert.That(root.gameObject.activeSelf, Is.False, templateProperties[i]);
            }

            Assert.That(prefab.GetComponent<Canvas>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<GraphicRaycaster>(), Is.Not.Null);
            Assert.That(prefab.GetComponent(GetRuntimeType("YC.Presentation.WindowCloseInputHandler")), Is.Not.Null);
            Assert.That(GetObjectReference<Button>(view, "resourcePaymentBankButton"), Is.Not.Null);
            Assert.That(GetObjectReference<Button>(view, "exploreConfirmButton"), Is.Not.Null);
            Assert.That(GetObjectReference<Button>(view, "buildBackButton"), Is.Not.Null);
            Assert.That(GetObjectReference<Button>(view, "buildConfirmButton"), Is.Not.Null);
            Assert.That(GetObjectReference<Button>(view, "characterContinueButton"), Is.Not.Null);
            Assert.That(GetObjectReference<Button>(view, "characterFinishButton"), Is.Not.Null);
        }

        [Test]
        public void EffectPrefab_HasFixedInactiveTemplatesAndAuthoredBehaviours()
        {
            var prefab = LoadPrefab(EffectPrefabPath);
            var view = prefab.GetComponent(GetRuntimeType("YC.Presentation.EffectDialogShellView"));
            var optionTemplate = GetObjectReference<Component>(view, "optionRowTemplate");
            var resourceTemplate = GetObjectReference<Component>(view, "resourceRowTemplate");
            var facilityTemplate = GetObjectReference<Component>(view, "facilityCardTemplate");
            var optionScroll = GetObjectReference<ScrollRect>(view, "optionScroll");
            var collapsedSummary = GetObjectReference<Text>(view, "collapsedSummaryText");
            var collapseButton = GetObjectReference<Button>(view, "collapseButton");
            var title = GetObjectReference<Text>(view, "titleText");
            var dragHandle = GetObjectReference<Component>(view, "dragHandle");
            var panel = GetObjectReference<RectTransform>(view, "panel");
            var collapsible = GetObjectReference<Component>(view, "collapsiblePanel");

            Assert.That(optionTemplate.gameObject.activeSelf, Is.False);
            Assert.That(resourceTemplate.gameObject.activeSelf, Is.False);
            Assert.That(facilityTemplate.gameObject.activeSelf, Is.False);
            Assert.That(optionScroll.gameObject.activeSelf, Is.False);
            Assert.That(collapsedSummary.gameObject.activeSelf, Is.False);
            Assert.That(collapseButton.gameObject.activeSelf, Is.False);
            Assert.That(dragHandle, Is.SameAs(title.GetComponent(
                GetRuntimeType("YC.Presentation.EffectDialogDragHandle"))));
            Assert.That(collapsible, Is.SameAs(panel.GetComponent(
                GetRuntimeType("YC.Presentation.EffectDialogCollapsiblePanel"))));

            var collapsibleData = new SerializedObject(collapsible);
            var triangleUp = collapsibleData.FindProperty("triangleUpSprite").objectReferenceValue;
            var triangleDown = collapsibleData.FindProperty("triangleDownSprite").objectReferenceValue;
            Assert.That(triangleUp, Is.Not.Null);
            Assert.That(triangleDown, Is.Not.Null);
            Assert.That(EditorUtility.IsPersistent(triangleUp), Is.True);
            Assert.That(EditorUtility.IsPersistent(triangleDown), Is.True);
            Assert.That(
                AssetDatabase.GetAssetPath(triangleUp),
                Is.EqualTo("Assets/YC/Presentation/Sprites/SharedUiVisuals.asset"));
            Assert.That(AssetDatabase.GetAssetPath(triangleDown), Is.EqualTo(AssetDatabase.GetAssetPath(triangleUp)));

            var serialized = new SerializedObject(view);
            var actionButtons = serialized.FindProperty("actionButtons");
            Assert.That(actionButtons, Is.Not.Null);
            Assert.That(actionButtons.arraySize, Is.EqualTo(3));
            for (var i = 0; i < actionButtons.arraySize; i++)
            {
                var action = actionButtons.GetArrayElementAtIndex(i).objectReferenceValue as Component;
                Assert.That(action, Is.Not.Null);
                Assert.That(action.gameObject.activeSelf, Is.False);
                AssertValid(action);
            }
        }

        [Test]
        public void DispatchConsumer_BindsPrefabRecyclesAndDispatchesOnlyOnce()
        {
            var scene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            Assert.That(roots, Has.Length.EqualTo(6));
            var hudType = GetRuntimeType("YC.Presentation.GameplayInteractionHudView");
            Component hud = null;
            for (var i = 0; i < roots.Length && hud == null; i++)
            {
                hud = roots[i].GetComponentInChildren(hudType, true);
            }

            Assert.That(hud, Is.Not.Null);
            var registry = GetObjectReference<Component>(hud, "dialogRegistry");
            var canvas = GetObjectReference<Canvas>(hud, "canvas").GetComponent<RectTransform>();
            var consumerType = GetRuntimeType("YC.Presentation.DispatchDecisionView");
            var consumer = System.Activator.CreateInstance(
                consumerType,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic,
                null,
                new object[] { registry, new System.Func<RectTransform>(() => canvas) },
                null);
            var show = consumerType.GetMethod("Show");
            var hide = consumerType.GetMethod("Hide");
            Assert.That(show, Is.Not.Null);
            Assert.That(hide, Is.Not.Null);

            var continueCount = 0;
            var finishCount = 0;
            show.Invoke(consumer, new object[]
            {
                new DispatchDecisionViewModel(
                    "第一次调度",
                    "第一次消息",
                    "继续调度",
                    "完成调度",
                    () => continueCount++,
                    () => finishCount++)
            });
            var first = FindChild(canvas.gameObject, "Dispatch Decision Overlay");
            Assert.That(first, Is.Not.Null);
            Assert.That(scene.GetRootGameObjects(), Has.Length.EqualTo(6));

            show.Invoke(consumer, new object[]
            {
                new DispatchDecisionViewModel(
                    "第二次调度",
                    "第二次消息",
                    "继续下一次",
                    "立即结束",
                    () => continueCount++,
                    () => finishCount++)
            });
            Assert.That(first == null, Is.True, "Show→Show 应销毁旧的 Dispatch prefab 实例。");
            var second = FindChild(canvas.gameObject, "Dispatch Decision Overlay");
            Assert.That(second, Is.Not.Null);
            Assert.That(FindChild(second, "Dispatch Decision Title").GetComponent<Text>().text,
                Is.EqualTo("第二次调度"));
            Assert.That(FindChild(second, "Dispatch Decision Message").GetComponent<Text>().text,
                Is.EqualTo("第二次消息"));
            Assert.That(FindChild(second, "Continue Dispatch").GetComponentInChildren<Text>().text,
                Is.EqualTo("继续下一次"));
            Assert.That(FindChild(second, "Finish Dispatch").GetComponentInChildren<Text>().text,
                Is.EqualTo("立即结束"));

            var continueClick = FindChild(second, "Continue Dispatch").GetComponent<Button>().onClick;
            continueClick.Invoke();
            continueClick.Invoke();
            Assert.That(continueCount, Is.EqualTo(1));
            Assert.That(finishCount, Is.Zero);
            Assert.That(FindChild(canvas.gameObject, "Dispatch Decision Overlay"), Is.Null);

            show.Invoke(consumer, new object[]
            {
                new DispatchDecisionViewModel(
                    "结束调度",
                    string.Empty,
                    "继续",
                    "完成",
                    () => continueCount++,
                    () => finishCount++)
            });
            var third = FindChild(canvas.gameObject, "Dispatch Decision Overlay");
            var finishClick = FindChild(third, "Finish Dispatch").GetComponent<Button>().onClick;
            finishClick.Invoke();
            finishClick.Invoke();
            Assert.That(continueCount, Is.EqualTo(1));
            Assert.That(finishCount, Is.EqualTo(1));
            Assert.That(FindChild(canvas.gameObject, "Dispatch Decision Overlay"), Is.Null);

            hide.Invoke(consumer, null);
            Assert.That(scene.GetRootGameObjects(), Has.Length.EqualTo(6));
        }

        [Test]
        public void HudAndSampleScene_HaveRealDialogRegistryReferencesAndSixRoots()
        {
            var hudPrefab = LoadPrefab(HudPrefabPath);
            var hudType = GetRuntimeType("YC.Presentation.GameplayInteractionHudView");
            var registryType = GetRuntimeType("YC.Presentation.GameplayDialogRegistry");
            var hud = hudPrefab.GetComponent(hudType);
            var registry = hudPrefab.GetComponentInChildren(registryType, true);
            Assert.That(hud, Is.Not.Null);
            Assert.That(registry, Is.Not.Null);
            Assert.That(GetObjectReference<Component>(hud, "dialogRegistry"), Is.SameAs(registry));
            AssertValid(registry);
            Assert.That(AssetDatabase.GetAssetPath(GetObjectReference<Component>(registry, "effectDialogShellPrefab")),
                Is.EqualTo(EffectPrefabPath));
            Assert.That(AssetDatabase.GetAssetPath(GetObjectReference<Component>(registry, "dispatchDecisionPrefab")),
                Is.EqualTo(DispatchPrefabPath));
            var hudEventPrefab = GetObjectReference<Component>(registry, "eventChoiceDialogPrefab");
            Assert.That(AssetDatabase.GetAssetPath(hudEventPrefab), Is.EqualTo(EventChoicePrefabPath));
            Assert.That(
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(hudEventPrefab, out var hudEventGuid, out long hudEventLocalId),
                Is.True);
            Assert.That(hudEventGuid, Is.EqualTo(AssetDatabase.AssetPathToGUID(EventChoicePrefabPath)));
            Assert.That(hudEventLocalId, Is.Not.Zero);
            AssertAllSerializedObjectReferencesHaveAssetIds(hudPrefab);

            var scene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
            Assert.That(scene.GetRootGameObjects(), Has.Length.EqualTo(6));
            var eventSystems = Object.FindObjectsOfType<EventSystem>(true);
            Assert.That(eventSystems, Has.Length.EqualTo(1));
            Component sceneHud = null;
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length && sceneHud == null; i++)
            {
                sceneHud = roots[i].GetComponentInChildren(hudType, true);
            }
            Assert.That(sceneHud, Is.Not.Null);
            Assert.That(PrefabUtility.IsPartOfPrefabInstance(sceneHud.gameObject), Is.True);
            var source = PrefabUtility.GetCorrespondingObjectFromSource(sceneHud.gameObject);
            Assert.That(AssetDatabase.GetAssetPath(source), Is.EqualTo(HudPrefabPath));
            var sceneRegistry = GetObjectReference<Component>(sceneHud, "dialogRegistry");
            Assert.That(sceneRegistry, Is.Not.Null);
            AssertValid(sceneRegistry);
            Assert.That(AssetDatabase.GetAssetPath(GetObjectReference<Component>(sceneRegistry, "effectDialogShellPrefab")),
                Is.EqualTo(EffectPrefabPath));
            Assert.That(AssetDatabase.GetAssetPath(GetObjectReference<Component>(sceneRegistry, "dispatchDecisionPrefab")),
                Is.EqualTo(DispatchPrefabPath));
            var sceneEventPrefab = GetObjectReference<Component>(sceneRegistry, "eventChoiceDialogPrefab");
            Assert.That(AssetDatabase.GetAssetPath(sceneEventPrefab), Is.EqualTo(EventChoicePrefabPath));
            Assert.That(
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    sceneEventPrefab,
                    out var sceneEventGuid,
                    out long sceneEventLocalId),
                Is.True);
            Assert.That(sceneEventGuid, Is.EqualTo(hudEventGuid));
            Assert.That(sceneEventLocalId, Is.EqualTo(hudEventLocalId));
            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(sceneRegistry), Is.SameAs(registry));
        }

        private static GameObject LoadPrefab(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            return prefab;
        }

        private static GameObject FindChild(GameObject root, string objectName)
        {
            if (root == null)
            {
                return null;
            }

            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null && transforms[i].name == objectName)
                {
                    return transforms[i].gameObject;
                }
            }

            return null;
        }

        private static void AssertNoMissingScripts(string path)
        {
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var transforms = contents.GetComponentsInChildren<Transform>(true);
                for (var i = 0; i < transforms.Length; i++)
                {
                    var components = transforms[i].GetComponents<Component>();
                    for (var componentIndex = 0; componentIndex < components.Length; componentIndex++)
                    {
                        Assert.That(components[componentIndex], Is.Not.Null,
                            path + " contains a missing script on " + transforms[i].name + ".");
                    }
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private static void AssertAllSerializedObjectReferencesHaveAssetIds(GameObject prefab)
        {
            var behaviours = prefab.GetComponentsInChildren<MonoBehaviour>(true);
            for (var i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                Assert.That(behaviour, Is.Not.Null, prefab.name + " contains a missing MonoBehaviour.");
                var serialized = new SerializedObject(behaviour);
                var iterator = serialized.GetIterator();
                var enterChildren = true;
                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference)
                    {
                        continue;
                    }

                    Assert.That(iterator.objectReferenceInstanceIDValue == 0 || iterator.objectReferenceValue != null,
                        Is.True,
                        behaviour.GetType().Name + "." + iterator.propertyPath + " is dangling.");
                    var reference = iterator.objectReferenceValue;
                    if (reference == null)
                    {
                        continue;
                    }

                    Assert.That(
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(reference, out string guid, out long fileId),
                        Is.True,
                        behaviour.GetType().Name + "." + iterator.propertyPath + " has no asset identity.");
                    Assert.That(guid, Is.Not.Empty,
                        behaviour.GetType().Name + "." + iterator.propertyPath + " has no GUID.");
                    Assert.That(fileId, Is.Not.EqualTo(0),
                        behaviour.GetType().Name + "." + iterator.propertyPath + " has no fileID.");
                }
            }
        }

        private static System.Type GetRuntimeType(string typeName)
        {
            var type = System.Type.GetType(typeName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, typeName);
            return type;
        }

        private static T GetObjectReference<T>(Component target, string propertyName) where T : Object
        {
            var property = new SerializedObject(target).FindProperty(propertyName);
            Assert.That(property, Is.Not.Null, target.GetType().Name + "." + propertyName);
            var value = property.objectReferenceValue as T;
            Assert.That(value, Is.Not.Null, target.GetType().Name + "." + propertyName);
            return value;
        }

        private static void AssertValid(Component target)
        {
            var method = target.GetType().GetMethod("TryValidateConfiguration");
            Assert.That(method, Is.Not.Null, target.GetType().Name);
            var arguments = new object[] { string.Empty };
            Assert.That((bool)method.Invoke(target, arguments), Is.True, arguments[0] as string);
        }
    }
}
