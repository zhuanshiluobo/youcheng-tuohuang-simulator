using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using YC.Domain.Maps;
using YC.Presentation.Maps;

namespace YC.Tests.EditMode
{
    public sealed class MapViewEditorAssetTests
    {
        private const string SpriteLibraryPath =
            "Assets/YC/Presentation/Sprites/Map/MapVisualSprites.asset";
        private const string PrefabPath =
            "Assets/YC/Presentation/Prefabs/Map/MapView.prefab";
        private const string FeedbackMaterialPath =
            "Assets/YC/Presentation/Materials/MapFeedbackAdditive.mat";
        private const string FeedbackControllerPath =
            "Assets/YC/Presentation/Animations/MapPlacementFeedback.controller";
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";

        [Test]
        public void GeneratedAssets_PassBuilderValidationAndUsePersistentSubAssets()
        {
            var library = AssetDatabase.LoadAssetAtPath<MapVisualSpriteLibrary>(
                SpriteLibraryPath);
            Assert.That(library, Is.Not.Null);
            Assert.That(library.TryValidateConfiguration(out var reason), Is.True, reason);
            var sprites = new[]
            {
                library.Hotspot, library.EmptyInfluenceSlot, library.OccupiedInfluenceSlot,
                library.MovableInfluenceBorder, library.PlacementFeedbackRing, library.MobileCity,
                library.ScoreMarker, library.ScoreMarkerBorder
            };
            Assert.That(sprites, Has.All.Not.Null);
            Assert.That(sprites.Select(AssetDatabase.GetAssetPath),
                Has.All.EqualTo(SpriteLibraryPath));
            Assert.That(sprites, Has.All.Matches<Sprite>(item => !item.packed));
            var serialized = new SerializedObject(library);
            var retainedTextures = serialized.FindProperty("retainedTextures");
            var retainedSprites = serialized.FindProperty("retainedSprites");
            Assert.That(retainedTextures.arraySize, Is.EqualTo(sprites.Length));
            Assert.That(retainedSprites.arraySize, Is.EqualTo(sprites.Length));
            for (var i = 0; i < sprites.Length; i++)
            {
                Assert.That(
                    retainedSprites.GetArrayElementAtIndex(i).objectReferenceValue,
                    Is.SameAs(sprites[i]));
                var sourceTexture = retainedTextures.GetArrayElementAtIndex(i).objectReferenceValue as Texture2D;
                Assert.That(sourceTexture, Is.Not.Null);
                Assert.That(AssetDatabase.GetAssetPath(sourceTexture), Is.EqualTo(SpriteLibraryPath));
                Assert.That(sprites[i].texture, Is.SameAs(sourceTexture));
            }
            Assert.That(
                File.ReadAllText(SpriteLibraryPath),
                Does.Not.Contain("guid: 00000000000000000000000000000000"));
        }

        [Test]
        public void MapViewPrefab_ExactlyMatchesFourPlayerTopologyAndHasNoMissingScripts()
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var map = StaticMapDefinitions.CreateFourPlayerMap();
                var viewType = GetRuntimeType("YC.Presentation.MapView");
                var view = root.GetComponent(viewType);
                Assert.That(view, Is.Not.Null);
                var displayType = GetRuntimeType("YC.Presentation.MapDisplayController");
                var display = root.GetComponent(displayType);
                var mapRenderer = root.GetComponent<SpriteRenderer>();
                Assert.That(display, Is.Not.Null);
                Assert.That(mapRenderer, Is.Not.Null);
                Assert.That(mapRenderer.enabled, Is.True);
                Assert.That(new SerializedObject(display).FindProperty("mapRenderer").objectReferenceValue,
                    Is.SameAs(mapRenderer));
                Assert.That(root.GetComponents<Component>().Select(component => component.GetType()),
                    Is.EquivalentTo(new[]
                    {
                        typeof(Transform), typeof(SpriteRenderer), displayType,
                        typeof(MapCoordinateSpace), viewType
                    }));
                var validateArguments = new object[] { map, string.Empty };
                Assert.That(
                    viewType.GetMethod("TryValidateConfiguration").Invoke(view, validateArguments),
                    Is.True,
                    validateArguments[1] as string);
                Assert.That(GetCount(view, "Locations"), Is.EqualTo(22));
                Assert.That(GetCount(view, "ResourceTokens"), Is.EqualTo(22));
                Assert.That(GetCount(view, "InfluenceSlots"), Is.EqualTo(77));
                Assert.That(GetCount(view, "CityPool"), Is.EqualTo(4));
                Assert.That(GetCount(view, "ScoreMarkerPool"), Is.EqualTo(4));
                Assert.That(root.GetComponentsInChildren(GetRuntimeType("YC.Presentation.MapHotspot"), true).Length, Is.EqualTo(22));
                Assert.That(root.GetComponentsInChildren(GetRuntimeType("YC.Presentation.InfluenceSlotClickTarget"), true).Length, Is.EqualTo(77));
                Assert.That(root.GetComponentsInChildren(GetRuntimeType("YC.Presentation.MobileCityClickTarget"), true).Length, Is.EqualTo(4));
                Assert.That(root.GetComponentsInChildren(GetRuntimeType("YC.Presentation.MapHighlightPulse"), true).Length, Is.EqualTo(99));
                var feedbackType = GetRuntimeType("YC.Presentation.MapPlacementFeedback");
                var feedbacks = root.GetComponentsInChildren(feedbackType, true);
                Assert.That(feedbacks.Length, Is.EqualTo(99));
                var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                    FeedbackControllerPath);
                var material = AssetDatabase.LoadAssetAtPath<Material>(FeedbackMaterialPath);
                Assert.That(controller, Is.Not.Null);
                Assert.That(material, Is.Not.Null);
                var animators = root.GetComponentsInChildren<Animator>(true);
                Assert.That(animators, Has.Length.EqualTo(99));
                Assert.That(animators.Select(item => item.gameObject).Distinct().Count(), Is.EqualTo(99));
                Assert.That(animators, Has.All.Matches<Animator>(item =>
                    !item.enabled && item.runtimeAnimatorController == controller &&
                    item.updateMode == AnimatorUpdateMode.UnscaledTime &&
                    item.cullingMode == AnimatorCullingMode.AlwaysAnimate &&
                    !item.applyRootMotion));
                for (var feedbackIndex = 0; feedbackIndex < feedbacks.Length; feedbackIndex++)
                {
                    var feedback = feedbacks[feedbackIndex] as Component;
                    var serializedFeedback = new SerializedObject(feedback);
                    Assert.That(
                        serializedFeedback.FindProperty("animator").objectReferenceValue,
                        Is.SameAs(feedback.GetComponent<Animator>()));
                }
                Assert.That(
                    root.GetComponentsInChildren<SpriteRenderer>(true)
                        .Count(item => item.sharedMaterial == material),
                    Is.EqualTo(297));
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    Assert.That(
                        transform.gameObject.GetComponents<Component>().Count(component => component == null),
                        Is.Zero,
                        transform.name);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        public void SampleScene_HasOneConnectedMapViewWithoutChangingInfrastructureRoots()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            Assert.That(roots.Length, Is.EqualTo(6));
            Assert.That(roots.Select(item => item.name), Is.EquivalentTo(new[]
            {
                "MapRoot", "Main Camera", "Game Settings Menu", "RoundTracker", "EventSystem",
                "Gameplay Interaction HUD"
            }));
            Assert.That(roots.SelectMany(item => item.GetComponentsInChildren<EventSystem>(true)).Count(),
                Is.EqualTo(1));
            Assert.That(scene.isDirty, Is.False);
            var mapRoot = roots.Single(item => item.name == "MapRoot");
            Assert.That(mapRoot.transform.childCount, Is.EqualTo(1));
            var controllerType = GetRuntimeType("YC.Presentation.MobileCityInteractionController");
            Assert.That(mapRoot.GetComponents<Component>().Select(component => component.GetType()),
                Is.EquivalentTo(new[] { typeof(Transform), controllerType }));
            var viewRoot = mapRoot.transform.GetChild(0).gameObject;
            Assert.That(viewRoot.name, Is.EqualTo("MapView"));
            Assert.That(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(viewRoot), Is.EqualTo(PrefabPath));
            var controller = mapRoot.GetComponent(controllerType);
            var serialized = new SerializedObject(controller);
            var view = viewRoot.GetComponent(GetRuntimeType("YC.Presentation.MapView"));
            Assert.That(serialized.FindProperty("mapViewBinding").objectReferenceValue,
                Is.SameAs(view));
            Assert.That(serialized.FindProperty("mapRenderer").objectReferenceValue,
                Is.SameAs(viewRoot.GetComponent<SpriteRenderer>()));
            Assert.That(viewRoot.GetComponent(GetRuntimeType("YC.Presentation.MapDisplayController")), Is.Not.Null);
            Assert.That(roots.SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Sum(transform => transform.gameObject.GetComponents<Component>().Count(component => component == null)),
                Is.Zero);
        }

        private static int GetCount(Component target, string propertyName)
        {
            var value = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
                .GetValue(target) as ICollection;
            Assert.That(value, Is.Not.Null, propertyName);
            return value.Count;
        }

        private static Type GetRuntimeType(string name)
        {
            var type = Type.GetType(name + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, name);
            return type;
        }
    }
}
