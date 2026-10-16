using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Maps;

namespace YC.Tests.EditMode
{
    public sealed class MapHighlightIntegrationTests
    {
        private const string DataRoot = "Assets/YC/Presentation/Maps/Highlights/";
        private static Type RuntimeType(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static T Get<T>(object value, string property) => (T)value.GetType().GetProperty(property).GetValue(value);

        [Serializable] private sealed class DeliveryMap { public Entry[] resources; public Entry[] routes; public Slot[] influence_slots; }
        [Serializable] private sealed class Entry { public string id; public float x; public float y; }
        [Serializable] private sealed class Slot { public string id; public string parent_kind; public string parent_id; public int local_index; public float x; public float y; }

        [TestCase("four", 77, 22)]
        [TestCase("three", 65, 20)]
        public void DeliveryMapping_CoversEveryTargetAndPreservesIndependentAnchors(string map, int slotCount, int routeCount)
        {
            var source = JsonUtility.FromJson<DeliveryMap>(File.ReadAllText(DataRoot + map + "-player-map.json"));
            var layout = MapDisplayLayoutCatalog.Load("map-" + map + "-players");
            var definition = StaticMapDefinitions.Resolve(layout.MapId);
            Assert.That(MapDisplayLayoutValidator.Validate(definition, layout), Is.Empty);
            Assert.That(layout.Routes.Count, Is.EqualTo(routeCount));
            Assert.That(layout.Routes.Select(r => r.SourceButtonId).Distinct().Count(), Is.EqualTo(routeCount));
            var mapped = new HashSet<string>();
            foreach (var entry in source.resources)
                AssertPosition(layout.Locations.Find(l => l.LocationId == entry.id).ButtonPosition, entry.x, entry.y, entry.id);
            foreach (var entry in source.routes)
                AssertPosition(layout.Routes.Find(r => r.SourceButtonId == entry.id).ButtonPosition, entry.x, entry.y, entry.id);
            foreach (var slot in source.influence_slots)
            {
                Vector2 position;
                string id;
                if (slot.parent_kind == "resource")
                {
                    var parent = layout.Locations.Find(l => l.LocationId == slot.parent_id);
                    position = parent.NormalizedPosition + parent.InfluenceSlots[slot.local_index - 1].Offset;
                    id = InfluenceService.GetLocationSlotId(parent.LocationId, slot.local_index - 1);
                }
                else
                {
                    var parent = layout.Routes.Find(r => r.SourceButtonId == slot.parent_id);
                    position = parent.NormalizedPosition + parent.InfluenceSlots[slot.local_index - 1].Offset;
                    id = InfluenceService.GetRouteSlotId(parent.RouteId, slot.local_index - 1);
                }
                Assert.That(mapped.Add(id), Is.True, id);
                AssertPosition(position, slot.x, slot.y, slot.id);
            }
            Assert.That(mapped.Count, Is.EqualTo(slotCount));
        }

        [TestCase("Assets/Scenes/SampleScene.unity", 121)]
        [TestCase("Assets/Scenes/ThreePlayerScene.unity", 103)]
        public void ActualScene_UsesConfiguredGeometryAndAllPiecesShareSlotCenters(string scenePath, int count)
        {
            var previous = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.OpenScene(scenePath);
                var view = UnityEngine.Object.FindObjectOfType(RuntimeType("MapView"));
                Assert.That(view, Is.Not.Null);
                var space = Get<MapCoordinateSpace>(view, "CoordinateSpace");
                var catalog = Get<MapHighlightCatalog>(view, "ButtonCatalog");
                var buttons = Get<IEnumerable>(view, "Buttons").Cast<Component>().ToArray();
                Assert.That(buttons.Length, Is.EqualTo(count));
                foreach (var button in buttons)
                {
                    var pos = (Vector2)view.GetType().GetMethod("GetButtonPosition").Invoke(view, new object[] { button });
                    button.GetType().GetMethod("Bind").Invoke(button, new object[] { null, space, catalog, pos });
                    button.GetType().GetMethod("SetState").Invoke(button, new object[] { true, false, true });
                    var renderer = Get<SpriteRenderer>(button, "Image");
                    var collider = Get<PolygonCollider2D>(button, "HitArea");
                    var originalScale = renderer.transform.localScale;
                    Physics2D.SyncTransforms();
                    Assert.That(collider.OverlapPoint(button.transform.position), Is.True, button.name + " 透明内部命中");
                    Assert.That(space.MapRenderer.transform.InverseTransformPoint(button.transform.position).z, Is.EqualTo(0).Within(.0001f));
                    button.GetType().GetMethod("SetState").Invoke(button, new object[] { true, true, true });
                    Assert.That(renderer.transform.localScale, Is.EqualTo(originalScale), "selected 不改变轮廓尺寸");
                    Assert.That(collider.enabled, Is.True);
                    button.GetType().GetMethod("SetState").Invoke(button, new object[] { true, false, false });
                    Assert.That(renderer.enabled, Is.True);
                    Assert.That(collider.enabled, Is.False, "available 可独立禁用输入");
                }
                foreach (var binding in Get<IEnumerable>(view, "InfluenceSlots"))
                {
                    var id = Get<string>(binding, "SlotId");
                    var root = Get<SpriteRenderer>(binding, "Renderer").transform;
                    var button = buttons.Single(b => Get<string>(b, "TargetId") == id);
                    Assert.That(Vector3.Distance(root.position, button.transform.position), Is.LessThan(.001f), id);
                    Assert.That(Get<CircleCollider2D>(binding, "Collider").enabled, Is.False, "停用旧圆形命中");
                    var piece = Get<Component>(binding, "PieceVisual");
                    var before = piece.transform.localPosition;
                    piece.GetType().GetMethod("SetGhosted").Invoke(piece, new object[] { true });
                    Assert.That(piece.transform.localPosition, Is.EqualTo(before));
                    piece.GetType().GetMethod("SetGhosted").Invoke(piece, new object[] { false });
                    var zMax = float.NegativeInfinity;
                    foreach (var filter in piece.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var bounds = filter.sharedMesh.bounds;
                        for (var x = -1; x <= 1; x += 2)
                            for (var y = -1; y <= 1; y += 2)
                                for (var z = -1; z <= 1; z += 2)
                                {
                                    var vertex = bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z));
                                    zMax = Mathf.Max(zMax, space.MapRenderer.transform.InverseTransformPoint(filter.transform.TransformPoint(vertex)).z);
                                }
                    }
                    Assert.That(zMax, Is.EqualTo(0f).Within(.01f), id + " 模型底面应贴地");
                }
            }
            finally
            {
                if (previous.Any(scene => scene.isLoaded && scene.isActive)) EditorSceneManager.RestoreSceneManagerSetup(previous);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            }
        }

        [Test]
        public void SpriteCatalog_PreservesFullCanvasAndSamplingScale()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<MapHighlightCatalog>(DataRoot + "MapHighlightCatalog.asset");
            Assert.That(catalog.SourceManifest, Is.Not.Null);
            Assert.That(catalog.Shapes.Count, Is.EqualTo(4));
            foreach (var shape in catalog.Shapes)
            {
                Assert.That(shape.Selected.rect, Is.EqualTo(shape.Available.rect));
                foreach (var sprite in new[] { shape.Available, shape.Selected })
                {
                    Assert.That(sprite.rect.size, Is.EqualTo(shape.CanvasSize * 4));
                    Assert.That(sprite.pivot, Is.EqualTo(Vector2.Scale(sprite.rect.size, shape.Pivot)));
                    var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(sprite));
                    Assert.That(importer.spriteImportMode, Is.EqualTo(SpriteImportMode.Single));
                }
                var points = shape.GetLocalPolygon(new Vector2(50, 50), catalog.SourceMapSize);
                Assert.That(points.Max(p => p.x) - points.Min(p => p.x), Is.EqualTo(shape.OutlineSize.x / catalog.SourceMapSize.x * 50).Within(.0001f));
            }
        }

        [TestCase("location:A-01:0")]
        [TestCase("A-01#0")]
        [TestCase("influence.source")]
        public void DispatchPreview_UsesSnapshotAndPreservesSourceTargetResolution(string sourceId)
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var presenter = Activator.CreateInstance(RuntimeType("MapViewPresenter"), null, null, mapQuery, new InfluenceService(mapQuery));
            var resolve = presenter.GetType().GetMethod("FindDisplayedInfluence", BindingFlags.Instance | BindingFlags.NonPublic);
            var source = new InfluencePlacement { InfluenceId = "influence.source", PlayerId = 1, SlotId = "location:A-01:0" };
            var target = new InfluencePlacement { InfluenceId = "influence.target", PlayerId = 2, SlotId = "route:A1:0" };
            var state = new GameState();
            var projectionReads = 0;
            state.Map.Influences.Bind(() => { projectionReads++; return new List<InfluencePlacement> { source, target }; });
            var snapshot = state.Map.Influences.ToArray();
            InfluencePlacement Resolve(string slot, bool pending, string from, string to) =>
                (InfluencePlacement)resolve.Invoke(presenter, new object[] { state, snapshot, slot, pending, from, to });

            Assert.That(Resolve(sourceId, true, sourceId, target.SlotId), Is.Null);
            Assert.That(Resolve(target.SlotId, true, sourceId, target.SlotId), Is.SameAs(source));
            Assert.That(Resolve(target.SlotId, false, sourceId, target.SlotId), Is.SameAs(target));
            Assert.That(Resolve(target.SlotId, true, "missing", target.SlotId), Is.Null);
            Assert.That(Resolve(sourceId, true, sourceId, sourceId), Is.Null, "源槽隐藏优先于目标槽预览");
            Assert.That(Resolve(source.SlotId, true, target.SlotId, "location:A-02:0"), Is.SameAs(source));
            Assert.That(source.SlotId, Is.EqualTo("location:A-01:0"), "预览不能移动状态中的势力");
            Assert.That(projectionReads, Is.EqualTo(1));
        }

        [TestCase("Assets/Scenes/SampleScene.unity")]
        [TestCase("Assets/Scenes/ThreePlayerScene.unity")]
        public void ActualScene_MapRefreshProjectsEachListOnceIncludingEmptyAndNextRefresh(string scenePath)
        {
            var previous = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.OpenScene(scenePath);
                var view = UnityEngine.Object.FindObjectOfType(RuntimeType("MapView"));
                Assert.That(view, Is.Not.Null);
                var mapId = Get<MapCoordinateSpace>(view, "CoordinateSpace").Layout.MapId;
                var mapQuery = new MapQueryService(StaticMapDefinitions.Resolve(mapId));
                var controller = UnityEngine.Object.FindObjectOfType(RuntimeType("MobileCityInteractionController"));
                var presenter = Activator.CreateInstance(RuntimeType("MapViewPresenter"), controller, view, mapQuery, new InfluenceService(mapQuery));
                var buildArguments = new object[] { null };
                Assert.That(presenter.GetType().GetMethod("BuildViews").Invoke(presenter, buildArguments), Is.True, buildArguments[0] as string);
                var influenceBindings = Get<IEnumerable>(view, "InfluenceSlots").Cast<object>().ToArray();
                var resourceBindings = Get<IEnumerable>(view, "ResourceTokens").Cast<object>().ToArray();
                Assert.That(influenceBindings, Is.Not.Empty);
                Assert.That(resourceBindings, Is.Not.Empty);
                var state = new GameState();
                state.Players.Add(new PlayerState { PlayerId = 1, Color = PlayerColor.Red });
                var influences = new List<InfluencePlacement>();
                var tokens = new List<ResourceTokenState>();
                var influenceReads = 0;
                var tokenReads = 0;
                state.Map.Influences.Bind(() => { influenceReads++; return influences; });
                state.Map.ResourceTokens.Bind(() => { tokenReads++; return tokens; });
                var refreshInfluences = presenter.GetType().GetMethod("RefreshInfluenceDisplay");
                var refreshTokens = presenter.GetType().GetMethod("RefreshResourceTokenDisplay");

                for (var refresh = 1; refresh <= 3; refresh++)
                {
                    if (refresh == 2)
                    {
                        influences.Add(new InfluencePlacement { InfluenceId = "influence.refresh", PlayerId = 1, SlotId = Get<string>(influenceBindings[0], "SlotId") });
                        tokens.Add(new ResourceTokenState { LocationId = Get<string>(resourceBindings[0], "LocationId"), ResourceType = ResourceType.Originium, Amount = 1 });
                    }
                    else if (refresh == 3)
                    {
                        influences.Clear();
                        tokens.Clear();
                    }
                    refreshInfluences.Invoke(presenter, new object[] { state, false, null, null });
                    refreshTokens.Invoke(presenter, new object[] { state });
                    Assert.That(influenceReads, Is.EqualTo(refresh), "每轮刷新只投影一次势力列表");
                    Assert.That(tokenReads, Is.EqualTo(refresh), "每轮刷新只投影一次资源列表");
                }
                foreach (var binding in resourceBindings)
                    Assert.That(Get<SpriteRenderer>(binding, "Renderer").enabled, Is.False, "移除标记后不能继续显示上轮缓存");
            }
            finally
            {
                if (previous.Any(scene => scene.isLoaded && scene.isActive)) EditorSceneManager.RestoreSceneManagerSetup(previous);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            }
        }

        private static void AssertPosition(Vector2 actual, float x, float y, string id)
        {
            Assert.That(actual.x, Is.EqualTo(x / 2048).Within(.0000001f), id);
            Assert.That(actual.y, Is.EqualTo(1 - y / 2048).Within(.0000001f), id);
        }
    }
}
