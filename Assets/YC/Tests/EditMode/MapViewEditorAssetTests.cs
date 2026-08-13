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
        private const string PieceMaterialPath =
            "Assets/YC/Presentation/Materials/MapPiecePlayerColor.mat";
        private const string InfluencePiecePrefabPath =
            "Assets/YC/Presentation/Prefabs/Map/Pieces/InfluencePiece.prefab";
        private const string MobileCityPiecePrefabPath =
            "Assets/YC/Presentation/Prefabs/Map/Pieces/MobileCityPiece.prefab";
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
                var navigationBoundsType = GetRuntimeType("YC.Presentation.TabletopViewportNavigationBounds");
                var display = root.GetComponent(displayType);
                var navigationBounds = root.GetComponent(navigationBoundsType);
                var mapRenderer = root.GetComponent<SpriteRenderer>();
                Assert.That(display, Is.Not.Null);
                Assert.That(navigationBounds, Is.Not.Null);
                Assert.That(mapRenderer, Is.Not.Null);
                Assert.That(mapRenderer.enabled, Is.True);
                var displaySerialized = new SerializedObject(display);
                Assert.That(displaySerialized.FindProperty("mapRenderer").objectReferenceValue,
                    Is.SameAs(mapRenderer));
                Assert.That(displaySerialized.FindProperty("navigationBoundsSource").objectReferenceValue,
                    Is.SameAs(navigationBounds));
                var navigationBoundsSerialized = new SerializedObject(navigationBounds);
                Assert.That(navigationBoundsSerialized.FindProperty("horizontalMapMarginFraction").floatValue,
                    Is.EqualTo(0.15f).Within(0.0001f));
                Assert.That(navigationBoundsSerialized.FindProperty("verticalMapMarginFraction").floatValue,
                    Is.EqualTo(0.15f).Within(0.0001f));
                Assert.That(displaySerialized.FindProperty("minZoom").floatValue,
                    Is.EqualTo(0.9f).Within(0.0001f));
                Assert.That(displaySerialized.FindProperty("maxZoom").floatValue,
                    Is.EqualTo(2f).Within(0.0001f));
                Assert.That(root.GetComponents<Component>().Select(component => component.GetType()),
                    Is.EquivalentTo(new[]
                    {
                        typeof(Transform), typeof(SpriteRenderer), navigationBoundsType, displayType,
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
                var pieceVisualType = GetRuntimeType("YC.Presentation.MapPieceVisual");
                var pieceVisuals = root.GetComponentsInChildren(pieceVisualType, true);
                Assert.That(pieceVisuals, Has.Length.EqualTo(85));
                Assert.That(root.GetComponentsInChildren<MeshRenderer>(true), Has.Length.EqualTo(85));
                Assert.That(root.GetComponentsInChildren<Collider>(true), Is.Empty);
                Assert.That(root.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
                var pieceMaterial = AssetDatabase.LoadAssetAtPath<Material>(PieceMaterialPath);
                Assert.That(pieceMaterial, Is.Not.Null);
                Assert.That(pieceMaterial.GetFloat("_Metallic"), Is.EqualTo(0.05f).Within(0.0001f));
                Assert.That(pieceMaterial.GetFloat("_Glossiness"), Is.EqualTo(0.25f).Within(0.0001f));
                Assert.That(pieceMaterial.IsKeywordEnabled("_EMISSION"), Is.True);
                Assert.That(root.GetComponentsInChildren<MeshRenderer>(true),
                    Has.All.Matches<MeshRenderer>(item =>
                        !item.enabled && item.sharedMaterials.Length == 1 && item.sharedMaterial == pieceMaterial));
                var pieceLight = root.GetComponentsInChildren<Light>(true).Single();
                Assert.That(pieceLight.type, Is.EqualTo(LightType.Directional));
                Assert.That(pieceLight.intensity, Is.EqualTo(0.85f).Within(0.0001f));
                Assert.That(pieceLight.shadows, Is.EqualTo(LightShadows.Soft));
                Assert.That(pieceLight.shadowStrength, Is.EqualTo(0.45f).Within(0.0001f));

                var serializedView = new SerializedObject(view);
                AssertPieceVisualReferences(serializedView.FindProperty("influenceSlots"), 77);
                AssertPieceVisualReferences(serializedView.FindProperty("cityPool"), 4);
                AssertPieceVisualReferences(serializedView.FindProperty("scoreMarkerPool"), 4);
                AssertGroundedInfluencePieces(serializedView.FindProperty("influenceSlots"));
                AssertGroundedCityPieces(serializedView.FindProperty("cityPool"));
                AssertGroundedScorePieces(serializedView.FindProperty("scoreMarkerPool"));
                AssertPiecePrefab(InfluencePiecePrefabPath, pieceVisualType, new Vector3(0.7f, 0.7f, 0.7f));
                AssertPiecePrefab(MobileCityPiecePrefabPath, pieceVisualType, new Vector3(1.38f, 2.376f, 0.64f));
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
            var display = viewRoot.GetComponent(GetRuntimeType("YC.Presentation.MapDisplayController"));
            var navigationBounds = viewRoot.GetComponent(
                GetRuntimeType("YC.Presentation.TabletopViewportNavigationBounds"));
            Assert.That(display, Is.Not.Null);
            Assert.That(navigationBounds, Is.Not.Null);
            Assert.That(new SerializedObject(display).FindProperty("navigationBoundsSource").objectReferenceValue,
                Is.SameAs(navigationBounds));
            var navigationBoundsSerialized = new SerializedObject(navigationBounds);
            Assert.That(navigationBoundsSerialized.FindProperty("horizontalMapMarginFraction").floatValue,
                Is.EqualTo(0.15f).Within(0.0001f));
            Assert.That(navigationBoundsSerialized.FindProperty("verticalMapMarginFraction").floatValue,
                Is.EqualTo(0.15f).Within(0.0001f));
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

        private static void AssertPieceVisualReferences(SerializedProperty bindings, int expectedCount)
        {
            Assert.That(bindings, Is.Not.Null);
            Assert.That(bindings.arraySize, Is.EqualTo(expectedCount));
            for (var i = 0; i < bindings.arraySize; i++)
            {
                Assert.That(
                    bindings.GetArrayElementAtIndex(i).FindPropertyRelative("pieceVisual").objectReferenceValue,
                    Is.Not.Null,
                    "pieceVisual[" + i + "]");
            }
        }

        private static void AssertGroundedInfluencePieces(SerializedProperty bindings)
        {
            for (var i = 0; i < bindings.arraySize; i++)
            {
                var element = bindings.GetArrayElementAtIndex(i);
                var renderer = element.FindPropertyRelative("renderer").objectReferenceValue as SpriteRenderer;
                var visual = element.FindPropertyRelative("pieceVisual").objectReferenceValue as Component;
                Assert.That(renderer, Is.Not.Null);
                Assert.That(renderer.enabled, Is.True);
                Assert.That(visual, Is.Not.Null);
                var bounds = CalculateBoundsRelativeTo(
                    renderer.transform,
                    visual.GetComponentInChildren<MeshFilter>(true));
                Assert.That(bounds.center.x, Is.EqualTo(0f).Within(0.001f));
                Assert.That(bounds.center.y, Is.EqualTo(0f).Within(0.001f));
                Assert.That(bounds.max.z + renderer.transform.localPosition.z,
                    Is.EqualTo(0f).Within(0.01f),
                    "影响力方块底面必须落在地图 Z=0：" + i);
            }
        }

        private static void AssertGroundedCityPieces(SerializedProperty bindings)
        {
            for (var i = 0; i < bindings.arraySize; i++)
            {
                var element = bindings.GetArrayElementAtIndex(i);
                var renderer = element.FindPropertyRelative("renderer").objectReferenceValue as SpriteRenderer;
                var visual = element.FindPropertyRelative("pieceVisual").objectReferenceValue as Component;
                Assert.That(renderer, Is.Not.Null);
                Assert.That(renderer.enabled, Is.False);
                Assert.That(visual, Is.Not.Null);
                var bounds = CalculateBoundsRelativeTo(
                    renderer.transform,
                    visual.GetComponentInChildren<MeshFilter>(true));
                Assert.That(bounds.center.x, Is.EqualTo(0f).Within(0.001f));
                Assert.That(bounds.center.y, Is.EqualTo(0f).Within(0.001f));
                Assert.That(bounds.max.z - 0.4f,
                    Is.EqualTo(0f).Within(0.01f),
                    "城市根节点运行时位于 Z=-0.4，模型底面应补偿到地图 Z=0：" + i);
            }
        }

        private static void AssertGroundedScorePieces(SerializedProperty bindings)
        {
            for (var i = 0; i < bindings.arraySize; i++)
            {
                var element = bindings.GetArrayElementAtIndex(i);
                var renderer = element.FindPropertyRelative("renderer").objectReferenceValue as SpriteRenderer;
                var border = element.FindPropertyRelative("borderRenderer").objectReferenceValue as SpriteRenderer;
                var visual = element.FindPropertyRelative("pieceVisual").objectReferenceValue as Component;
                Assert.That(renderer, Is.Not.Null);
                Assert.That(border, Is.Not.Null);
                Assert.That(renderer.enabled, Is.False);
                Assert.That(border.enabled, Is.False);
                Assert.That(visual, Is.Not.Null);
                var bounds = CalculateBoundsRelativeTo(
                    renderer.transform,
                    visual.GetComponentInChildren<MeshFilter>(true));
                Assert.That(bounds.center.x, Is.EqualTo(0f).Within(0.001f));
                Assert.That(bounds.center.y, Is.EqualTo(0f).Within(0.001f));
                Assert.That(bounds.max.z - 0.62f,
                    Is.EqualTo(0f).Within(0.01f),
                    "分数标记运行时位于 Z=-0.62，方块底面应补偿到地图 Z=0：" + i);
            }
        }

        private static void AssertPiecePrefab(string path, Type pieceVisualType, Vector3 expectedSize)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            Assert.That(prefab.GetComponentsInChildren(pieceVisualType, true), Has.Length.EqualTo(1));
            Assert.That(prefab.GetComponentsInChildren<MeshRenderer>(true), Has.Length.EqualTo(1));
            Assert.That(prefab.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<Collider2D>(true), Is.Empty);
            var bounds = CalculateBoundsRelativeTo(prefab.transform, prefab.GetComponentInChildren<MeshFilter>(true));
            Assert.That(bounds.size.x, Is.EqualTo(expectedSize.x).Within(0.02f), path);
            Assert.That(bounds.size.y, Is.EqualTo(expectedSize.y).Within(0.02f), path);
            Assert.That(bounds.size.z, Is.EqualTo(expectedSize.z).Within(0.02f), path);
            Assert.That(bounds.max.z, Is.EqualTo(0f).Within(0.01f), path);
            Assert.That(bounds.min.z, Is.LessThan(-expectedSize.z + 0.02f), path);
        }

        private static Bounds CalculateBoundsRelativeTo(Transform root, MeshFilter filter)
        {
            Assert.That(filter, Is.Not.Null);
            Assert.That(filter.sharedMesh, Is.Not.Null);
            var meshBounds = filter.sharedMesh.bounds;
            var matrix = root.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            var initialized = false;
            var result = new Bounds();
            for (var x = 0; x < 2; x++)
            for (var y = 0; y < 2; y++)
            for (var z = 0; z < 2; z++)
            {
                var corner = new Vector3(
                    x == 0 ? meshBounds.min.x : meshBounds.max.x,
                    y == 0 ? meshBounds.min.y : meshBounds.max.y,
                    z == 0 ? meshBounds.min.z : meshBounds.max.z);
                var point = matrix.MultiplyPoint3x4(corner);
                if (!initialized)
                {
                    result = new Bounds(point, Vector3.zero);
                    initialized = true;
                }
                else
                {
                    result.Encapsulate(point);
                }
            }
            return result;
        }

        private static Type GetRuntimeType(string name)
        {
            var type = Type.GetType(name + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, name);
            return type;
        }
    }
}
