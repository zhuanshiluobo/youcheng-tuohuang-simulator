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
    public sealed class RoundTrackerEditorAssetTests
    {
        private const string PrefabPath = "Assets/YC/Presentation/Prefabs/RoundTracker/RoundTracker.prefab";
        private const string InfrastructurePath = "Assets/YC/Presentation/Prefabs/Infrastructure/SampleSceneUiInfrastructure.prefab";
        private const string CirclePath = "Assets/YC/Presentation/Sprites/RoundTrackerCircle.asset";
        private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";
        private const string StartScenePath = "Assets/Scenes/StartScene.unity";

        [Test]
        public void RoundTrackerPrefab_HasCompleteSerializedViewFixedShellAndDisabledTemplates()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab, Is.Not.Null);
            var controllerType = GetRuntimeType("YC.Presentation.RoundTrackerController");
            var viewType = GetRuntimeType("YC.Presentation.RoundTrackerView");
            var controller = prefab.GetComponent(controllerType);
            var view = prefab.GetComponent(viewType);
            Assert.That(controller, Is.Not.Null);
            Assert.That(view, Is.Not.Null);

            var controllerData = new SerializedObject(controller);
            Assert.That(controllerData.FindProperty("view").objectReferenceValue, Is.SameAs(view));
            var viewData = new SerializedObject(view);
            var references = new[]
            {
                "canvasTransform", "panelTransform", "contentArea", "trackSlotsTransform", "markerContainer",
                "gameOverOverlay", "finalScoreSummaryView", "finalScoreDetailsView", "finalScoreMessage",
                "finalScoreMessageText", "winnerText", "tiebreakText", "finalScoreDetailsButtonLabel",
                "finalScoreDetailsButton", "finalScoreDetailsBackButton", "returnStartButton",
                "rankingRowsContainer", "detailPlayerRowsContainer", "detailChartsContainer",
                "playerMarkerTemplate", "rankingRowTemplate", "detailPlayerRowTemplate",
                "scoreChartTemplate", "scoreBarTemplate"
            };
            for (var i = 0; i < references.Length; i++)
            {
                var property = viewData.FindProperty(references[i]);
                Assert.That(property, Is.Not.Null, references[i]);
                Assert.That(property.objectReferenceValue, Is.Not.Null, references[i]);
            }

            var arguments = new object[] { null };
            var validate = viewType.GetMethod("TryValidateConfiguration", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(validate.Invoke(view, arguments), Is.True, arguments[0] as string);

            Assert.That(Find(prefab.transform, "Round Panel"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Round Border Top"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Start Band"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Danger Band"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Round Label FINAL"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Game Over Dialog"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Final Score Header"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Final Score Details View"), Is.Not.Null);
            Assert.That(Find(prefab.transform, "Game Over Overlay").gameObject.activeSelf, Is.False);
            Assert.That(Find(prefab.transform, "Final Score Details View").gameObject.activeSelf, Is.False);

            var templateNames = new[]
            {
                "Player Marker Template", "Final Score Ranking Row Template",
                "Final Score Detail Player Row Template", "Final Score Chart Template", "Final Score Bar Template"
            };
            for (var i = 0; i < templateNames.Length; i++)
            {
                var template = Find(prefab.transform, templateNames[i]);
                Assert.That(template, Is.Not.Null, templateNames[i]);
                Assert.That(template.gameObject.activeSelf, Is.False, templateNames[i]);
            }

            Assert.That(Find(prefab.transform, "Final Score Ranking Rows").childCount, Is.EqualTo(0));
            Assert.That(Find(prefab.transform, "Final Score Detail Player Rows").childCount, Is.EqualTo(0));
            Assert.That(Find(prefab.transform, "Final Score Detail Charts").childCount, Is.EqualTo(0));

            var markerTemplate = Find(prefab.transform, "Player Marker Template").GetComponent<Image>();
            Assert.That(markerTemplate.sprite, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath(markerTemplate.sprite), Is.EqualTo(CirclePath));
            Assert.That(prefab.GetComponentInChildren<EventSystem>(true), Is.Null);
        }

        [TestCase(PrefabPath)]
        [TestCase(InfrastructurePath)]
        public void EditorPrefab_HasNoMissingScripts(string path)
        {
            var contents = PrefabUtility.LoadPrefabContents(path);
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
        public void SampleScene_HasConnectedRoundTrackerAndExactlyOneInfrastructureEventSystem()
        {
            WithScene(SampleScenePath, scene =>
            {
                var tracker = FindRoot(scene, "RoundTracker");
                AssertConnectedTo(tracker, PrefabPath);
                var eventSystems = FindAllInScene<EventSystem>(scene);
                Assert.That(eventSystems.Length, Is.EqualTo(1));
                AssertConnectedTo(eventSystems[0].gameObject, InfrastructurePath);
            });
        }

        [Test]
        public void StartScene_StillHasExactlyOneEventSystemFromStartMenuPrefab()
        {
            WithScene(StartScenePath, scene =>
            {
                var eventSystems = FindAllInScene<EventSystem>(scene);
                Assert.That(eventSystems.Length, Is.EqualTo(1));
                var source = PrefabUtility.GetCorrespondingObjectFromSource(eventSystems[0].gameObject);
                Assert.That(source, Is.Not.Null);
                Assert.That(
                    AssetDatabase.GetAssetPath(source),
                    Is.EqualTo("Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab"));
            });
        }

        [Test]
        public void RoundTrackerController_OnlyInstantiatesApprovedDynamicTemplates()
        {
            var source = File.ReadAllText(Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation/RoundTrackerController.cs"));
            StringAssert.DoesNotContain("new GameObject", source);
            StringAssert.DoesNotContain("AddComponent", source);
            StringAssert.DoesNotContain("EnsureEventSystem", source);
            StringAssert.DoesNotContain("Sprite.Create", source);
            StringAssert.DoesNotContain("Resources.", source);
            StringAssert.DoesNotContain("FindObjectOfType", source);
            StringAssert.DoesNotContain("BuildRoundUi", source);
            StringAssert.Contains("Instantiate(view.PlayerMarkerTemplate", source);
            StringAssert.Contains("Instantiate(view.RankingRowTemplate", source);
            StringAssert.Contains("Instantiate(view.DetailPlayerRowTemplate", source);
            StringAssert.Contains("Instantiate(view.ScoreChartTemplate", source);
            StringAssert.Contains("Instantiate(view.ScoreBarTemplate", source);

            var promptSource = File.ReadAllText(Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation/PromptPresenter.cs"));
            var declarationSource = File.ReadAllText(Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation/CityStyleDeclarationPreviewDialog.cs"));
            StringAssert.DoesNotContain("EnsureEventSystem", promptSource);
            StringAssert.DoesNotContain("EnsureEventSystem", declarationSource);
            Assert.That(
                File.Exists(Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation/UguiUtility.cs")),
                Is.False,
                "UGUI Prefab 构建工具不得留在运行时 Presentation 边界。");
        }

        [Test]
        public void ProductionPresentation_CreatesOnlyFiveApprovedDynamicGameObjects()
        {
            var presentationRoot = Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation");
            var sourcePaths = Directory.GetFiles(presentationRoot, "*.cs", SearchOption.AllDirectories);
            var expectedCounts = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "CardPointerInteraction.cs", 2 },
                { "FontHealthCheckRunner.cs", 2 },
                { "GameLaunchContext.cs", 1 }
            };
            var actualCounts = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
            var total = 0;

            for (var i = 0; i < sourcePaths.Length; i++)
            {
                var normalizedPath = sourcePaths[i].Replace('\\', '/');
                if (normalizedPath.Contains("/Editor/") || normalizedPath.Contains("/Tests/"))
                {
                    continue;
                }

                var count = Regex.Matches(
                    File.ReadAllText(sourcePaths[i]),
                    @"new\s+GameObject\s*\(").Count;
                if (count == 0)
                {
                    continue;
                }

                actualCounts.Add(Path.GetFileName(sourcePaths[i]), count);
                total += count;
            }

            Assert.That(total, Is.EqualTo(5), "生产 Presentation 的动态 GameObject 构造总数发生漂移。");
            Assert.That(actualCounts, Is.EquivalentTo(expectedCounts));
        }

        [Test]
        public void ProductionPresentation_DoesNotCreateEventSystemsAtRuntime()
        {
            var presentationRoot = Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation");
            var sourcePaths = Directory.GetFiles(presentationRoot, "*.cs", SearchOption.AllDirectories);
            var forbiddenPatterns = new[]
            {
                @"EnsureEventSystem\s*\(",
                @"AddComponent\s*<\s*(?:UnityEngine\.EventSystems\.)?EventSystem\s*>",
                @"AddComponent\s*\(\s*typeof\s*\(\s*(?:UnityEngine\.EventSystems\.)?EventSystem\s*\)",
                @"typeof\s*\(\s*(?:UnityEngine\.EventSystems\.)?EventSystem\s*\)",
                "new\\s+GameObject\\s*\\(\\s*\\\"[^\\\"]*EventSystem[^\\\"]*\\\"",
                @"StandaloneInputModule"
            };

            for (var i = 0; i < sourcePaths.Length; i++)
            {
                var normalizedPath = sourcePaths[i].Replace('\\', '/');
                if (normalizedPath.Contains("/Editor/") || normalizedPath.Contains("/Tests/"))
                {
                    continue;
                }

                var source = File.ReadAllText(sourcePaths[i]);
                for (var patternIndex = 0; patternIndex < forbiddenPatterns.Length; patternIndex++)
                {
                    Assert.That(
                        source,
                        Does.Not.Match(forbiddenPatterns[patternIndex]),
                        "生产 Presentation 中仍存在 EventSystem 运行时创建模式：" + normalizedPath);
                }
            }
        }

        [Test]
        public void BareRoundTrackerController_FailsFastWhenSerializedViewIsMissing()
        {
            var owner = new GameObject("Unconfigured RoundTracker");
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("RoundTrackerController.*配置无效"));
                var controller = owner.AddComponent(GetRuntimeType("YC.Presentation.RoundTrackerController")) as MonoBehaviour;
                var awake = controller.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
                awake.Invoke(controller, null);
                Assert.That(controller.enabled, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private static void WithScene(string path, Action<Scene> assertion)
        {
            var existing = SceneManager.GetSceneByPath(path);
            var openedForTest = !existing.IsValid() || !existing.isLoaded;
            var scene = openedForTest ? EditorSceneManager.OpenScene(path, OpenSceneMode.Additive) : existing;
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

        private static void AssertConnectedTo(GameObject instance, string assetPath)
        {
            Assert.That(instance, Is.Not.Null);
            Assert.That(PrefabUtility.IsPartOfPrefabInstance(instance), Is.True);
            var source = PrefabUtility.GetCorrespondingObjectFromSource(instance);
            Assert.That(source, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath(source), Is.EqualTo(assetPath));
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

        private static Type GetRuntimeType(string fullName)
        {
            var type = Type.GetType(fullName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing " + fullName + ".");
            return type;
        }
    }
}
