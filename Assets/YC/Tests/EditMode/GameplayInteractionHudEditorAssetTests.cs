using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class GameplayInteractionHudEditorAssetTests
    {
        private const string PrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";
        private GameObject instance;

        [TearDown]
        public void TearDown()
        {
            if (instance != null)
            {
                UnityEngine.Object.DestroyImmediate(instance);
                instance = null;
            }

            var viewer = GameObject.Find("Hint Card Image Viewer");
            if (viewer != null)
            {
                UnityEngine.Object.DestroyImmediate(viewer);
            }

            var viewerCanvas = GameObject.Find("Hint Card Viewer Canvas");
            if (viewerCanvas != null)
            {
                UnityEngine.Object.DestroyImmediate(viewerCanvas);
            }
        }

        [Test]
        public void GameplayHudPrefab_HasCompleteSerializedViewsAndFixedInitialState()
        {
            var prefab = LoadPrefab();
            var hud = prefab.GetComponent(GetRuntimeType("YC.Presentation.GameplayInteractionHudView"));
            var prompt = prefab.GetComponentInChildren(GetRuntimeType("YC.Presentation.GameplayPromptView"), true);
            var action = prefab.GetComponentInChildren(GetRuntimeType("YC.Presentation.ActionPanelView"), true);
            var info = GetProperty<Component>(hud, "InfoPanel");
            var build = GetProperty<Component>(hud, "BuildInfoPanel");
            Assert.That(hud, Is.Not.Null);
            Assert.That(prompt, Is.Not.Null);
            Assert.That(action, Is.Not.Null);
            Assert.That(info, Is.Not.Null);
            Assert.That(build, Is.Not.Null);
            Assert.That(info.transform.parent, Is.SameAs(build.transform.parent));
            Assert.That(
                info.transform.GetSiblingIndex(),
                Is.GreaterThan(build.transform.GetSiblingIndex()),
                "展开信息面板必须位于建设卡区和建设面板之后，才能渲染在其上方。");
            Assert.That(GetProperty<Component>(hud, "PromptView"), Is.SameAs(prompt));
            Assert.That(GetProperty<Component>(hud, "ActionPanelView"), Is.SameAs(action));
            Assert.That(
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(info.gameObject),
                Is.EqualTo("Assets/YC/Presentation/Prefabs/Gameplay/ExpandableInfoPanel.prefab"));
            Assert.That(
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(build.gameObject),
                Is.EqualTo("Assets/YC/Presentation/Prefabs/Gameplay/BuildInfoPanel.prefab"));
            Assert.That(GetProperty<Canvas>(hud, "Canvas"), Is.Not.Null);
            AssertTryValidate(hud);
            Assert.That(GetProperty<Component>(hud, "CityInteractionController"), Is.Null);

            AssertReferences(new SerializedObject(prompt), "canvas", "panelTransform", "promptText", "canvasGroup");
            AssertReferences(
                new SerializedObject(action),
                "panelObject", "mainFaceObject", "cardFaceObject", "cardImageContainer",
                "cardImageContainerBackground", "cardImage", "cardImageButton", "cardShade",
                "cardFaceOutline", "cardPlaceholderText", "cardTitleText", "cardHintText",
                "cardPrimaryButton", "cardPrimaryLabel", "cardSecondaryButton", "cardSecondaryLabel",
                "hintCardTexture", "flipButton", "currentPlayerText", "phaseText",
                "localPlayerColorSwatch", "remainingInfluenceText", "statusText",
                "useCharacterButton", "declareCityStyleButton", "deployButton", "dispatchButton",
                "exploreButton", "moveCityButton", "endRoundButton");

            var hintTexture = GetProperty<Texture2D>(action, "HintCardTexture");
            Assert.That(hintTexture, Is.Not.Null);
            Assert.That(
                AssetDatabase.GetAssetPath(hintTexture),
                Is.EqualTo("Assets/Resources/ProjectAssetLibrary/HintCards/提示卡.jpg"));
            Assert.That(GetProperty<GameObject>(action, "MainFaceObject").activeSelf, Is.True);
            Assert.That(GetProperty<GameObject>(action, "CardFaceObject").activeSelf, Is.False);
            var promptGroup = GetProperty<CanvasGroup>(prompt, "CanvasGroup");
            var promptPanel = GetProperty<RectTransform>(prompt, "PanelTransform");
            Assert.That(promptGroup.alpha, Is.EqualTo(0f).Within(0.001f));
            Assert.That(promptPanel.anchoredPosition, Is.EqualTo(new Vector2(544f, -128f)));
            Assert.That(prefab.GetComponentInChildren<EventSystem>(true), Is.Null);

            var feedbackType = GetRuntimeType("YC.Presentation.ActionButtonPressFeedback");
            var feedback = prefab.GetComponentsInChildren(feedbackType, true);
            Assert.That(feedback.Length, Is.EqualTo(10));
            for (var i = 0; i < feedback.Length; i++)
            {
                Assert.That(feedback[i].GetComponent<Button>(), Is.Not.Null, feedback[i].name);
                Assert.That(feedback[i].GetComponent<Outline>(), Is.Not.Null, feedback[i].name);
            }
        }

        [Test]
        public void GameplayHudPrefab_AllSerializedObjectReferencesHaveGuidAndFileId()
        {
            var prefab = LoadPrefab();
            var behaviours = prefab.GetComponentsInChildren<MonoBehaviour>(true);
            for (var i = 0; i < behaviours.Length; i++)
            {
                var serialized = new SerializedObject(behaviours[i]);
                var iterator = serialized.GetIterator();
                var enterChildren = true;
                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference ||
                        iterator.objectReferenceValue == null ||
                        iterator.propertyPath == "m_Script")
                    {
                        continue;
                    }

                    Assert.That(
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                            iterator.objectReferenceValue,
                            out var guid,
                            out long localId),
                        Is.True,
                        behaviours[i].name + "." + iterator.propertyPath);
                    Assert.That(guid, Is.Not.Empty, behaviours[i].name + "." + iterator.propertyPath);
                    Assert.That(localId, Is.Not.Zero, behaviours[i].name + "." + iterator.propertyPath);
                }
            }
        }

        [Test]
        public void GameplayHudPrefab_HasNoMissingScripts()
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
        public void SampleScene_HasOneConnectedHudAndBidirectionalMobileCityBinding()
        {
            WithScene(scene =>
            {
                var matches = FindRoots(scene, "Gameplay Interaction HUD");
                Assert.That(matches.Length, Is.EqualTo(1));
                var root = matches[0];
                Assert.That(PrefabUtility.IsPartOfPrefabInstance(root), Is.True);
                var source = PrefabUtility.GetCorrespondingObjectFromSource(root);
                Assert.That(AssetDatabase.GetAssetPath(source), Is.EqualTo(PrefabPath));

                var hudType = GetRuntimeType("YC.Presentation.GameplayInteractionHudView");
                var cityType = GetRuntimeType("YC.Presentation.MobileCityInteractionController");
                var hud = root.GetComponent(hudType);
                var city = FindInScene(scene, cityType);
                Assert.That(city, Is.Not.Null);
                Assert.That(GetProperty<Component>(hud, "CityInteractionController"), Is.SameAs(city));
                var cityData = new SerializedObject(city);
                Assert.That(cityData.FindProperty("gameplayInteractionHud").objectReferenceValue, Is.SameAs(hud));
                Assert.That(cityData.FindProperty("infoPanel").objectReferenceValue,
                    Is.SameAs(GetProperty<Component>(hud, "InfoPanel")));
                Assert.That(cityData.FindProperty("buildInfoPanel").objectReferenceValue,
                    Is.SameAs(GetProperty<Component>(hud, "BuildInfoPanel")));
                Assert.That(
                    hudType.GetMethod("IsBoundTo", BindingFlags.Instance | BindingFlags.Public)
                        .Invoke(hud, new object[] { city }),
                    Is.True);
                AssertOnlyExpectedSceneOverrides(root);
                Assert.That(FindAllInScene<EventSystem>(scene).Length, Is.EqualTo(1));
                Assert.That(root.GetComponentInChildren<EventSystem>(true), Is.Null);
                Assert.That(FindAllInScene(scene, GetRuntimeType("YC.Presentation.ExpandableInfoPanel")).Length,
                    Is.EqualTo(1));
                Assert.That(FindAllInScene(scene, GetRuntimeType("YC.Presentation.BuildInfoPanel")).Length,
                    Is.EqualTo(1));
                Assert.That(FindRoots(scene, "InfoPanel"), Is.Empty);
                var roots = scene.GetRootGameObjects();
                Assert.That(
                    roots.Length,
                    Is.EqualTo(6),
                    "Actual roots: " + string.Join(", ", Array.ConvertAll(roots, item => item.name)));
            });
        }

        [Test]
        public void BoundActionPanel_InvokesButtonsFlipsFacesUpdatesPromptAndOpensHintViewer()
        {
            ViewerPrefabTestUtility.RegisterZoomablePrefab();
            instance = PrefabUtility.InstantiatePrefab(LoadPrefab()) as GameObject;
            var actionView = instance.GetComponentInChildren(GetRuntimeType("YC.Presentation.ActionPanelView"), true);
            var promptView = instance.GetComponentInChildren(GetRuntimeType("YC.Presentation.GameplayPromptView"), true);
            var registry = instance.GetComponentInChildren(
                GetRuntimeType("YC.Presentation.GameplayDialogRegistry"),
                true);
            var cardVisualCatalog = GetProperty<UnityEngine.Object>(registry, "CardVisualCatalog");
            var actionType = GetRuntimeType("YC.Presentation.ActionPanelController");
            var promptType = GetRuntimeType("YC.Presentation.PromptPresenter");
            var counts = new int[7];
            var callbacks = new Action[7];
            for (var i = 0; i < callbacks.Length; i++)
            {
                var index = i;
                callbacks[i] = () => counts[index]++;
            }

            actionType.GetMethod("Bind", BindingFlags.Static | BindingFlags.Public).Invoke(
                null,
                new object[]
                {
                    actionView, cardVisualCatalog, callbacks[0], callbacks[1], callbacks[2], callbacks[3],
                    callbacks[4], callbacks[5], callbacks[6]
                });
            var buttons = new[]
            {
                GetProperty<Button>(actionView, "UseCharacterButton"),
                GetProperty<Button>(actionView, "DeclareCityStyleButton"),
                GetProperty<Button>(actionView, "DeployButton"),
                GetProperty<Button>(actionView, "DispatchButton"),
                GetProperty<Button>(actionView, "ExploreButton"),
                GetProperty<Button>(actionView, "MoveCityButton"),
                GetProperty<Button>(actionView, "EndRoundButton")
            };
            for (var i = 0; i < buttons.Length; i++)
            {
                buttons[i].onClick.Invoke();
                Assert.That(counts[i], Is.EqualTo(1), buttons[i].name);
            }

            GetProperty<Button>(actionView, "FlipButton").onClick.Invoke();
            Assert.That(GetProperty<GameObject>(actionView, "MainFaceObject").activeSelf, Is.False);
            Assert.That(GetProperty<GameObject>(actionView, "CardFaceObject").activeSelf, Is.True);
            Assert.That(
                GetProperty<RawImage>(actionView, "CardImage").texture,
                Is.SameAs(GetProperty<Texture2D>(actionView, "HintCardTexture")));
            GetProperty<Button>(actionView, "CardImageButton").onClick.Invoke();
            Assert.That(GameObject.Find("Hint Card Image Viewer"), Is.Not.Null);

            var prompt = promptType.GetMethod("Bind", BindingFlags.Static | BindingFlags.Public)
                .Invoke(null, new object[] { promptView });
            promptType.GetMethod("SetPrompt", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(prompt, new object[] { "交互 HUD 提示更新" });
            Assert.That(GetProperty<Text>(promptView, "PromptText").text, Is.EqualTo("交互 HUD 提示更新"));
            Assert.That(GetPrivateField<float>(prompt, "targetAlpha"), Is.EqualTo(1f));
            Assert.That(GetPrivateField<float>(prompt, "targetX"), Is.EqualTo(0f));
            promptType.GetMethod("SetPrompt", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(prompt, new object[] { string.Empty });
            Assert.That(GetPrivateField<float>(prompt, "targetAlpha"), Is.EqualTo(0f));
        }

        [Test]
        public void HudRuntimeClasses_DoNotConstructOrDiscoverFixedUi()
        {
            var prompt = ReadSource("YC/Presentation/PromptPresenter.cs");
            StringAssert.DoesNotContain("new GameObject", prompt);
            StringAssert.DoesNotContain("AddComponent", prompt);
            StringAssert.DoesNotContain(" Build(", prompt);
            StringAssert.Contains("Bind(GameplayPromptView view)", prompt);

            var action = ReadSource("YC/Presentation/ActionPanelController.cs");
            StringAssert.DoesNotContain("new GameObject", action);
            StringAssert.DoesNotContain("AddComponent", action);
            StringAssert.DoesNotContain("CreateFace", action);
            StringAssert.DoesNotContain("CreateButton", action);
            StringAssert.DoesNotContain("CreateText", action);
            StringAssert.DoesNotContain("HintCardResourcePath", action);
            StringAssert.DoesNotContain(" Build(", action);
            StringAssert.DoesNotContain("FindObjectOfType", action);
            StringAssert.Contains("view.HintCardTexture", action);

            var city = ReadSource("YC/Presentation/MobileCityInteractionController.cs");
            StringAssert.Contains("[SerializeField] private GameplayInteractionHudView gameplayInteractionHud;", city);
            StringAssert.DoesNotContain("PromptPresenter.Build", city);
            StringAssert.DoesNotContain("ActionPanelController.Build", city);
            StringAssert.DoesNotContain("BuildPromptPresenter", city);
            StringAssert.DoesNotContain("BuildActionPanel()", city);
            StringAssert.Contains("PromptPresenter.Bind(gameplayInteractionHud.PromptView)", city);
            StringAssert.Contains("ActionPanelController.Bind(", city);

            var fontHealth = ReadSource("YC/Presentation/FontHealthCheckRunner.cs");
            StringAssert.DoesNotContain("PromptPresenter", fontHealth);
            StringAssert.Contains("YC Font Health Probe", fontHealth);
            StringAssert.Contains("Prompt Text", fontHealth);
        }

        [Test]
        public void NullPromptAndActionViews_FailFastWithoutFallbackConstruction()
        {
            var promptType = GetRuntimeType("YC.Presentation.PromptPresenter");
            LogAssert.Expect(LogType.Error, new Regex("PromptPresenter.*引用为空"));
            var prompt = promptType.GetMethod("Bind", BindingFlags.Static | BindingFlags.Public)
                .Invoke(null, new object[] { null });
            Assert.That(prompt, Is.Null);

            var actionType = GetRuntimeType("YC.Presentation.ActionPanelController");
            LogAssert.Expect(LogType.Error, new Regex("ActionPanelController.*引用为空"));
            Action noop = () => { };
            var action = actionType.GetMethod("Bind", BindingFlags.Static | BindingFlags.Public)
                .Invoke(null, new object[] { null, null, noop, noop, noop, noop, noop, noop, noop });
            Assert.That(action, Is.Null);
        }

        private static GameObject LoadPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab, Is.Not.Null);
            return prefab;
        }

        private static void AssertTryValidate(Component component)
        {
            var args = new object[] { null };
            var result = component.GetType().GetMethod("TryValidateConfiguration", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(component, args);
            Assert.That(result, Is.True, args[0] as string);
        }

        private static void AssertReferences(SerializedObject serialized, params string[] names)
        {
            for (var i = 0; i < names.Length; i++)
            {
                var property = serialized.FindProperty(names[i]);
                Assert.That(property, Is.Not.Null, names[i]);
                Assert.That(property.objectReferenceValue, Is.Not.Null, names[i]);
            }
        }

        private static T GetProperty<T>(object target, string name)
        {
            return (T)target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                .GetValue(target, null);
        }

        private static T GetPrivateField<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(target);
        }

        private static string ReadSource(string relativePath)
        {
            return File.ReadAllText(Path.Combine(UnityEngine.Application.dataPath, relativePath));
        }

        private static Type GetRuntimeType(string fullName)
        {
            var type = Type.GetType(fullName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, fullName);
            return type;
        }

        private static void WithScene(Action<Scene> assertion)
        {
            var existing = SceneManager.GetSceneByPath(ScenePath);
            var openedForTest = !existing.IsValid() || !existing.isLoaded;
            var scene = openedForTest ? EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive) : existing;
            try
            {
                assertion(scene);
            }
            finally
            {
                if (openedForTest)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private static GameObject[] FindRoots(Scene scene, string name)
        {
            return Array.FindAll(scene.GetRootGameObjects(), root => root.name == name);
        }

        private static Component FindInScene(Scene scene, Type type)
        {
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                var component = roots[i].GetComponentInChildren(type, true);
                if (component != null)
                {
                    return component;
                }
            }

            return null;
        }

        private static T[] FindAllInScene<T>(Scene scene) where T : Component
        {
            var results = new System.Collections.Generic.List<T>();
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                results.AddRange(roots[i].GetComponentsInChildren<T>(true));
            }

            return results.ToArray();
        }

        private static Component[] FindAllInScene(Scene scene, Type componentType)
        {
            var results = new System.Collections.Generic.List<Component>();
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                results.AddRange(roots[i].GetComponentsInChildren(componentType, true));
            }

            return results.ToArray();
        }

        private static void AssertOnlyExpectedSceneOverrides(GameObject root)
        {
            var modifications = PrefabUtility.GetPropertyModifications(root) ??
                                Array.Empty<PropertyModification>();
            for (var i = 0; i < modifications.Length; i++)
            {
                var modification = modifications[i];
                if (modification == null || modification.target == null)
                {
                    continue;
                }

                var targetName = modification.target.name;
                var targetType = modification.target.GetType().Name;
                var path = modification.propertyPath;
                var expectedCityBinding = targetType == "GameplayInteractionHudView" &&
                                          path == "cityInteractionController";
                var expectedOuterName = targetName == "GameplayInteractionHud" &&
                                        targetType == "GameObject" && path == "m_Name";
                var expectedOuterTransform = targetName == "GameplayInteractionHud" &&
                                             targetType == "Transform" &&
                                             (path.StartsWith("m_LocalPosition.", StringComparison.Ordinal) ||
                                              path.StartsWith("m_LocalRotation.", StringComparison.Ordinal) ||
                                              path.StartsWith("m_LocalEulerAnglesHint.", StringComparison.Ordinal));
                var expectedDrivenBoardRect = targetName == "城市面板底图" &&
                                              targetType == "RectTransform" &&
                                              (path == "m_AnchorMax.x" ||
                                               path == "m_AnchorMax.y" ||
                                               path == "m_SizeDelta.x");
                Assert.That(
                    expectedCityBinding || expectedOuterName || expectedOuterTransform || expectedDrivenBoardRect,
                    Is.True,
                    "发现非预期 HUD 场景 override：" + targetName + "." + path);
            }
        }
    }
}
