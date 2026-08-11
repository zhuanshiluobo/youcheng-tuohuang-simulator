using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YC.Presentation.Maps;

namespace YC.Tests.EditMode
{
    public sealed class SpatialLayoutEditorAssetTests
    {
        private const string ManifestPath =
            "Assets/YC/Editor/Data/spatial_layout_manifest.json";
        private const string CardLayoutPath =
            "Assets/YC/Presentation/Content/CardBoardVisualLayout.asset";
        private const string MapLayoutPath =
            "Assets/Resources/MapLayouts/map-four-players.asset";
        private const string ExpectedSha =
            "03C979B63F8B036225E061A59BE9E0EA2E86BB9F5CDE81C854396894E124DB5B";

        [Test]
        public void PersistentAssets_AreUniqueLockedAndExactlyMatchManifest()
        {
            var card = LoadCardLayout();
            var map = LoadMapLayout();
            Assert.That(AssetDatabase.AssetPathToGUID(ManifestPath),
                Is.EqualTo("f746bcfc63b342d29fbdcb9e134a1f03"));
            Assert.That(AssetDatabase.AssetPathToGUID(CardLayoutPath),
                Is.EqualTo("dd760d7d5af84c1990edbfc132128f68"));
            Assert.That(AssetDatabase.AssetPathToGUID(MapLayoutPath),
                Is.EqualTo("07db2f96f43c41d6be8c2c15966f760e"));
            Assert.That(ComputeSha256(ManifestPath), Is.EqualTo(ExpectedSha));
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(CardLayoutPath), Has.Length.EqualTo(1));
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(MapLayoutPath), Has.Length.EqualTo(1));
            Assert.That(CardProperty<string>(card, "SourceManifestSha256"), Is.EqualTo(ExpectedSha));
            Assert.That(map.SpatialLayoutManifestSha256, Is.EqualTo(ExpectedSha));

            var builder = BuilderType();
            Assert.That(builder.GetMethod(
                    "ComputeCurrentSourceSha256",
                    BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null),
                Is.EqualTo(ExpectedSha));
            Assert.That(builder.GetMethod(
                    "MatchesCardBoardSource",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { CardType() },
                    null).Invoke(null, new[] { card }),
                Is.True);
            Assert.That(builder.GetMethod(
                    "MatchesMapScoreSource",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(MapDisplayLayout) },
                    null).Invoke(null, new object[] { map }),
                Is.True);
        }

        [Test]
        public void CardBoardLayout_PreservesAllFixedGeometryOrderAndFallback()
        {
            var card = LoadCardLayout();
            var centers = new[]
            {
                new Vector2(0.176f, 0.152f), new Vector2(0.502f, 0.152f),
                new Vector2(0.827f, 0.152f), new Vector2(0.176f, 0.383f),
                new Vector2(0.502f, 0.383f), new Vector2(0.827f, 0.383f),
                new Vector2(0.176f, 0.615f), new Vector2(0.502f, 0.615f),
                new Vector2(0.827f, 0.615f), new Vector2(0.176f, 0.846f),
                new Vector2(0.502f, 0.846f), new Vector2(0.827f, 0.846f)
            };
            for (var i = 0; i < centers.Length; i++)
            {
                AssertVector(CardVector(card, "GetCityBoardSlotCenter", i), centers[i]);
            }

            Assert.That(CardProperty<float>(card, "CityBoardSlotWidthRatio"),
                Is.EqualTo(0.292f).Within(0.000001f));
            Assert.That(CardProperty<float>(card, "CityBoardSlotHeightRatio"),
                Is.EqualTo(0.224f).Within(0.000001f));
            AssertAnchor(card, "unused", 0.69f, 0.73f);
            AssertAnchor(card, "used", 0.69f, 0.28f);
            AssertAnchor(card, "2", 0.69f, 0.82f);
            AssertAnchor(card, "used_from_2", 0.69f, 0.66f);
            AssertAnchor(card, "1", 0.69f, 0.50f);
            AssertAnchor(card, "used_from_1", 0.69f, 0.34f);
            AssertAnchor(card, "0", 0.69f, 0.18f);
            AssertVector(CardVector(card, "GetMarkerAreaAnchor", "unknown-area"),
                new Vector2(0.69f, 0.50f));

            Assert.That(CardProperty<int>(card, "RegularMarkerColumnCount"), Is.EqualTo(4));
            Assert.That(CardProperty<float>(card, "RegularMarkerColumnSpacingX"),
                Is.EqualTo(0.055f).Within(0.000001f));
            Assert.That(CardProperty<float>(card, "RegularMarkerRowSpacingY"),
                Is.EqualTo(0.11f).Within(0.000001f));
            Assert.That(CardProperty<int>(card, "MilitaryUnusedPlayerLaneCount"), Is.EqualTo(4));
            Assert.That(CardProperty<int>(card, "MilitaryUnusedMarkerRowCount"), Is.EqualTo(3));
            AssertVector(CardProperty<Vector2>(card, "BuildInfoMarkerSize"), new Vector2(12f, 12f));
            AssertVector(
                CardProperty<Vector2>(card, "DeclarationPreviewMarkerSize"),
                new Vector2(20f, 20f));
            AssertRect(CardProperty<Rect>(card, "LevelOneUsedSpecialActionArea"),
                0.54f, 0.08f, 0.945f, 0.485f);
            AssertRect(CardProperty<Rect>(card, "LevelTwoUsedFromTwoSpecialActionArea"),
                0.54f, 0.60f, 0.945f, 0.75f);
            AssertRect(CardProperty<Rect>(card, "LevelTwoUsedFromOneSpecialActionArea"),
                0.54f, 0.23f, 0.945f, 0.38f);
        }

        [Test]
        public void MapScoreLayout_PreservesInterpolationClampAndAllOverlapOffsets()
        {
            var map = LoadMapLayout();
            AssertVector(map.GetScoreTrackNormalizedPosition(-2), new Vector2(0.1013f, 0.98065f));
            AssertVector(map.GetScoreTrackNormalizedPosition(23), new Vector2(0.98015f, 0.98065f));
            AssertVector(map.GetScoreTrackNormalizedPosition(24), new Vector2(0.98015f, 0.9437f));
            AssertVector(map.GetScoreTrackNormalizedPosition(50), new Vector2(0.98015f, 0.02535f));
            AssertVector(map.GetScoreTrackNormalizedPosition(-99),
                map.GetScoreTrackNormalizedPosition(-2));
            AssertVector(map.GetScoreTrackNormalizedPosition(99),
                map.GetScoreTrackNormalizedPosition(50));
            var expectedZeroX = Mathf.Lerp(0.1013f, 0.98015f, 2f / 25f);
            Assert.That(map.GetScoreTrackNormalizedPosition(0).x,
                Is.EqualTo(expectedZeroX).Within(0.000001f));

            var expected = new[]
            {
                new[] { Vector2.zero },
                new[] { new Vector2(-0.0055f, 0f), new Vector2(0.0055f, 0f) },
                new[]
                {
                    new Vector2(-0.0055f, 0.0045f), new Vector2(0.0055f, 0.0045f),
                    new Vector2(0f, -0.005f)
                },
                new[]
                {
                    new Vector2(-0.0055f, 0.005f), new Vector2(0.0055f, 0.005f),
                    new Vector2(-0.0055f, -0.005f), new Vector2(0.0055f, -0.005f)
                }
            };
            for (var count = 1; count <= 4; count++)
                for (var index = 0; index < count; index++)
                    AssertVector(map.GetScoreMarkerOffset(index, count), expected[count - 1][index]);
        }

        [Test]
        public void MapPresenter_FailsFastWhenViewHasNoInjectedMapLayout()
        {
            var presenterType = Type.GetType(
                "YC.Presentation.MapViewPresenter, Assembly-CSharp",
                true);
            var presenter = Activator.CreateInstance(
                presenterType,
                new object[] { null, null, null, null });
            var exception = Assert.Throws<TargetInvocationException>(() =>
                presenterType.GetMethod("RefreshScoreTrackDisplay")
                    .Invoke(presenter, new object[] { null }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void RuntimeResolvers_ReadMutatedAssetPayloadInsteadOfHiddenConstants()
        {
            var cardClone = UnityEngine.Object.Instantiate(LoadCardLayout());
            var mapClone = UnityEngine.Object.Instantiate(LoadMapLayout());
            try
            {
                var cardSerialized = new SerializedObject(cardClone);
                cardSerialized.FindProperty("cityBoardSlotCenters")
                    .GetArrayElementAtIndex(0).vector2Value = new Vector2(0.222f, 0.333f);
                cardSerialized.ApplyModifiedPropertiesWithoutUndo();
                var slotLayout = Type.GetType(
                    "YC.Presentation.CityBoardSlotLayout, Assembly-CSharp",
                    true);
                var resolved = (Vector2)slotLayout.GetMethod("GetCenter")
                    .Invoke(null, new object[] { cardClone, 0 });
                AssertVector(resolved, new Vector2(0.222f, 0.333f));

                var mapSerialized = new SerializedObject(mapClone);
                mapSerialized.FindProperty("scoreTrackSegments")
                    .GetArrayElementAtIndex(0).FindPropertyRelative("Start").vector2Value =
                    new Vector2(0.20f, 0.90f);
                mapSerialized.FindProperty("scoreMarkerOffsets")
                    .GetArrayElementAtIndex(1).FindPropertyRelative("Offsets")
                    .GetArrayElementAtIndex(0).vector2Value = new Vector2(-0.02f, 0.01f);
                mapSerialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(mapClone.TryValidateScoreTrack(out var reason), Is.True, reason);
                AssertVector(mapClone.GetScoreTrackNormalizedPosition(-2), new Vector2(0.20f, 0.90f));
                AssertVector(mapClone.GetScoreMarkerOffset(0, 2), new Vector2(-0.02f, 0.01f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cardClone);
                UnityEngine.Object.DestroyImmediate(mapClone);
            }
        }

        [Test]
        public void Validators_RejectMissingDuplicateAndWrongLengthPayloads()
        {
            var cardClone = UnityEngine.Object.Instantiate(LoadCardLayout());
            var mapClone = UnityEngine.Object.Instantiate(LoadMapLayout());
            try
            {
                var cardSerialized = new SerializedObject(cardClone);
                cardSerialized.FindProperty("cityBoardSlotCenters").arraySize = 11;
                cardSerialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(TryValidateCard(cardClone, out _), Is.False);

                UnityEngine.Object.DestroyImmediate(cardClone);
                cardClone = UnityEngine.Object.Instantiate(LoadCardLayout());
                cardSerialized = new SerializedObject(cardClone);
                var anchors = cardSerialized.FindProperty("markerAreaAnchors");
                anchors.GetArrayElementAtIndex(1).FindPropertyRelative("MarkerArea").stringValue =
                    anchors.GetArrayElementAtIndex(0).FindPropertyRelative("MarkerArea").stringValue;
                cardSerialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(TryValidateCard(cardClone, out _), Is.False);

                var mapSerialized = new SerializedObject(mapClone);
                mapSerialized.FindProperty("scoreMarkerOffsets").arraySize = 3;
                mapSerialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(mapClone.TryValidateScoreTrack(out _), Is.False);

                UnityEngine.Object.DestroyImmediate(mapClone);
                mapClone = UnityEngine.Object.Instantiate(LoadMapLayout());
                mapSerialized = new SerializedObject(mapClone);
                mapSerialized.FindProperty("scoreMarkerOffsets")
                    .GetArrayElementAtIndex(1).FindPropertyRelative("MarkerCount").intValue = 1;
                mapSerialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(mapClone.TryValidateScoreTrack(out _), Is.False);
            }
            finally
            {
                if (cardClone != null) UnityEngine.Object.DestroyImmediate(cardClone);
                if (mapClone != null) UnityEngine.Object.DestroyImmediate(mapClone);
            }
        }

        [Test]
        public void Prefabs_ExplicitlyReferenceExpectedAssetsAndFullGatePasses()
        {
            var card = LoadCardLayout();
            AssertConsumerReference(
                "Assets/YC/Presentation/Prefabs/Gameplay/BuildInfoPanel.prefab",
                "YC.Presentation.BuildInfoPanel, Assembly-CSharp",
                card);
            AssertConsumerReference(
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CityStyleDeclarationPreviewDialog.prefab",
                "YC.Presentation.CityStyleDeclarationPreviewView, Assembly-CSharp",
                card);

            var mapPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Map/MapView.prefab");
            Assert.That(mapPrefab, Is.Not.Null);
            var coordinateSpace = mapPrefab.GetComponentsInChildren<MapCoordinateSpace>(true);
            Assert.That(coordinateSpace, Has.Length.EqualTo(1));
            Assert.That(coordinateSpace[0].Layout, Is.SameAs(LoadMapLayout()));

            Assert.DoesNotThrow(() => GateType().GetMethod("ValidateReadyForBuild")
                .Invoke(null, null));
        }

        [Test]
        public void CardBoardPrefabGate_RejectsNullAndWrongLayoutReferences()
        {
            const string path =
                "Assets/YC/Presentation/Prefabs/Gameplay/BuildInfoPanel.prefab";
            var root = PrefabUtility.LoadPrefabContents(path);
            var wrong = ScriptableObject.CreateInstance(CardType());
            try
            {
                var consumer = root.GetComponent(Type.GetType(
                    "YC.Presentation.BuildInfoPanel, Assembly-CSharp",
                    true));
                var validate = GateType().GetMethod(
                    "ValidateCardBoardConsumerReference",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(validate, Is.Not.Null);
                var expected = LoadCardLayout();
                Assert.DoesNotThrow(() => validate.Invoke(
                    null,
                    new object[] { consumer, expected, "Temporary", "cardBoardVisualLayout" }));

                var serialized = new SerializedObject(consumer);
                serialized.FindProperty("cardBoardVisualLayout").objectReferenceValue = null;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssertRejected(validate, new object[]
                {
                    consumer, expected, "Temporary", "cardBoardVisualLayout"
                });

                serialized.FindProperty("cardBoardVisualLayout").objectReferenceValue = wrong;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssertRejected(validate, new object[]
                {
                    consumer, expected, "Temporary", "cardBoardVisualLayout"
                });
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(wrong);
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        public void SceneYamlGate_RejectsDestructiveOverridesRemovalDuplicateAndDirectComponent()
        {
            const string prefabGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string mapGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            const string scriptGuid = "cccccccccccccccccccccccccccccccc";
            const long rootId = 11;
            const long coordinateId = 22;
            const long mapId = 11400000;
            var baseYaml =
                "--- !u!1001 &1\nPrefabInstance:\n  m_Modification:\n" +
                "    m_Modifications: []\n    m_RemovedComponents: []\n" +
                "    m_RemovedGameObjects: []\n" +
                "  m_SourcePrefab: {fileID: 100100000, guid: " + prefabGuid +
                ", type: 3}\n";
            var validate = GateType().GetMethod(
                "ValidateSavedSceneYaml",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(validate, Is.Not.Null);
            object[] Args(string yaml) => new object[]
            {
                "Synthetic.unity", yaml, prefabGuid, rootId, coordinateId,
                mapGuid, mapId, scriptGuid
            };

            Assert.DoesNotThrow(() => validate.Invoke(null, Args(baseYaml)));
            AssertRejected(validate, Args(baseYaml + baseYaml));
            AssertRejected(validate, Args(baseYaml.Replace(
                "m_RemovedComponents: []",
                "m_RemovedComponents:\n    - {fileID: 999, guid: " + prefabGuid +
                ", type: 3}\n    - {fileID: " + coordinateId +
                ", guid: " + prefabGuid + ", type: 3}")));
            AssertRejected(validate, Args(baseYaml.Replace(
                "m_RemovedGameObjects: []",
                "m_RemovedGameObjects:\n    - {fileID: 999, guid: " + prefabGuid +
                ", type: 3}\n    - {fileID: " + rootId +
                ", guid: " + prefabGuid + ", type: 3}")));
            AssertRejected(validate, Args(WithOverride(
                baseYaml, rootId, prefabGuid, "m_IsActive", "0", "{fileID: 0}")));
            AssertRejected(validate, Args(WithOverride(
                baseYaml, coordinateId, prefabGuid, "m_Enabled", "0", "{fileID: 0}")));
            AssertRejected(validate, Args(WithOverride(
                baseYaml, coordinateId, prefabGuid, "layout", string.Empty,
                "{fileID: 0}")));
            AssertRejected(validate, Args(WithOverride(
                baseYaml, coordinateId, prefabGuid, "layout", string.Empty,
                "{fileID: 999, guid: dddddddddddddddddddddddddddddddd, type: 2}")));
            AssertRejected(validate, Args(baseYaml +
                "--- !u!114 &2\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: " +
                scriptGuid + ", type: 3}\n"));
        }

        private static Type CardType()
        {
            return Type.GetType("YC.Presentation.CardBoardVisualLayout, Assembly-CSharp", true);
        }

        private static Type BuilderType()
        {
            return Type.GetType(
                "YC.Editor.SpatialLayoutEditorAssetBuilder, Assembly-CSharp-Editor",
                true);
        }

        private static Type GateType()
        {
            return Type.GetType(
                "YC.Editor.SpatialLayoutBuildReadiness, Assembly-CSharp-Editor",
                true);
        }

        private static UnityEngine.Object LoadCardLayout()
        {
            var result = AssetDatabase.LoadAssetAtPath(CardLayoutPath, CardType());
            Assert.That(result, Is.Not.Null);
            return result;
        }

        private static MapDisplayLayout LoadMapLayout()
        {
            var result = AssetDatabase.LoadAssetAtPath<MapDisplayLayout>(MapLayoutPath);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.TryValidateScoreTrack(out var reason), Is.True, reason);
            return result;
        }

        private static T CardProperty<T>(UnityEngine.Object card, string property)
        {
            return (T)CardType().GetProperty(property).GetValue(card);
        }

        private static Vector2 CardVector(
            UnityEngine.Object card,
            string method,
            object argument)
        {
            return (Vector2)CardType().GetMethod(method).Invoke(card, new[] { argument });
        }

        private static bool TryValidateCard(UnityEngine.Object card, out string reason)
        {
            var arguments = new object[] { null };
            var result = (bool)CardType().GetMethod("TryValidateConfiguration")
                .Invoke(card, arguments);
            reason = arguments[0] as string;
            return result;
        }

        private static void AssertConsumerReference(
            string path,
            string typeName,
            UnityEngine.Object expected)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var type = Type.GetType(typeName, true);
            Assert.That(prefab, Is.Not.Null);
            var consumers = prefab.GetComponentsInChildren(type, true);
            Assert.That(consumers, Has.Length.EqualTo(1));
            Assert.That(consumers[0].gameObject, Is.SameAs(prefab));
            Assert.That(new SerializedObject(consumers[0]).FindProperty("cardBoardVisualLayout")
                .objectReferenceValue, Is.SameAs(expected));
            Assert.That(
                File.ReadLines(path).Count(line =>
                    line.TrimStart().StartsWith("cardBoardVisualLayout:", StringComparison.Ordinal)),
                Is.EqualTo(1));
        }

        private static void AssertAnchor(
            UnityEngine.Object card,
            string area,
            float x,
            float y)
        {
            AssertVector(CardVector(card, "GetMarkerAreaAnchor", area), new Vector2(x, y));
        }

        private static void AssertRect(Rect value, float xMin, float yMin, float xMax, float yMax)
        {
            Assert.That(value.xMin, Is.EqualTo(xMin).Within(0.000001f));
            Assert.That(value.yMin, Is.EqualTo(yMin).Within(0.000001f));
            Assert.That(value.xMax, Is.EqualTo(xMax).Within(0.000001f));
            Assert.That(value.yMax, Is.EqualTo(yMax).Within(0.000001f));
        }

        private static void AssertVector(Vector2 actual, Vector2 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.000001f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.000001f));
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path)))
                    .Replace("-", string.Empty);
        }

        private static string WithOverride(
            string yaml,
            long targetId,
            string prefabGuid,
            string property,
            string value,
            string reference)
        {
            var modification =
                "    - target: {fileID: " + targetId + ", guid: " + prefabGuid +
                ", type: 3}\n" +
                "      propertyPath: " + property + "\n" +
                "      value: " + value + "\n" +
                "      objectReference: " + reference + "\n";
            return yaml.Replace(
                "    m_Modifications: []",
                "    m_Modifications:\n" + modification);
        }

        private static void AssertRejected(MethodInfo method, object[] arguments)
        {
            var exception = Assert.Throws<TargetInvocationException>(
                () => method.Invoke(null, arguments));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }
    }
}
