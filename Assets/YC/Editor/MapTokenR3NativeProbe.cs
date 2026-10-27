using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Influence;
using YC.Domain.State;
using YC.Presentation;

/// <summary>只在当前场景的内存副本检查地图组件及渲染；不生成/保存任何场景或Prefab，不计正常对局。</summary>
public static class MapTokenR3NativeProbe
{
    private static string output;
    private static readonly List<object> scenes = new List<object>();
    public static void Run()
    {
        try
        {
            output = Path.GetFullPath(Environment.GetEnvironmentVariable("YC_MAP_TOKEN_R3_OUTPUT") ?? "");
            var allowed = Path.GetFullPath("prompt/企业家内容实施规划-20261019/实施记录/QY-003-MAP-TOKEN-LABELS-AND-GAVIAL-ART-R3") + Path.DirectorySeparatorChar;
            Require(output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(output), "独立输出目录无效");
            foreach (string path in new[] { "Assets/Scenes/SampleScene.unity", "Assets/Scenes/ThreePlayerScene.unity" }) CheckScene(path);
            Write("原生组件结果.json", new { scope = "隔离显示组件，不是正常游戏或合法行动验收；仅内存投影输入", succeeded = true, scenes });
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            if (!string.IsNullOrEmpty(output) && Directory.Exists(output)) Write("首次失败.json", new { succeeded = false, error = error.ToString(), scenes });
            Debug.LogException(error); EditorApplication.Exit(1);
        }
    }
    private static void CheckScene(string path)
    {
        var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        var roots = scene.GetRootGameObjects();
        var map = roots.SelectMany(r => r.GetComponentsInChildren<MapView>(true)).Single();
        var ui = roots.SelectMany(r => r.GetComponentsInChildren<GameplayExpansionUi>(true)).Single();
        var camera = roots.SelectMany(r => r.GetComponentsInChildren<Camera>(true)).First(c => c.CompareTag("MainCamera"));
        var serialized = new SerializedObject(ui);
        var template = serialized.FindProperty("mapGavialTokenTemplate").objectReferenceValue as SpriteRenderer;
        var companyTemplate = serialized.FindProperty("mapCompanyTokenTemplate").objectReferenceValue as Button;
        Require(template != null && template.sprite != null && companyTemplate != null, "实际场景未继承地图token新绑定");
        Require(companyTemplate.GetComponentsInChildren<Text>(true).Length == 0, "公司专用模板仍有Text");
        var sprite = template.sprite;
        Require(sprite.rect == new Rect(0, 0, 500, 500), "指定完整PNG未以全图导入");
        var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(sprite)) as TextureImporter;
        var importerSettings = new TextureImporterSettings();
        if (importer != null) importer.ReadTextureSettings(importerSettings);
        Require(importer != null && importer.spriteImportMode == SpriteImportMode.Single && importerSettings.spriteMeshType == SpriteMeshType.FullRect, "Sprite不是完整Single/FullRect");
        typeof(GameplayExpansionUi).GetField("mapView", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(ui, map);
        var refresh = typeof(GameplayExpansionUi).GetMethod("RefreshMapTokens", BindingFlags.Instance | BindingFlags.NonPublic);
        var first = map.CoordinateSpace.Layout.Locations[0];
        var view = new GameStateView
        {
            Players = new List<PlayerStateView> { new PlayerStateView { PlayerId = 1 } },
            MapTokens = new List<MapTokenView> {
                new MapTokenView { ItemId = "isolated.gavial", TypeId = "infected.gavial_token", DefinitionId = "infected.component.gavial_token", LocationId = first.LocationId, IsPermanent = true },
                new MapTokenView { ItemId = "isolated.company", TypeId = "infected.company_marker", DefinitionId = "infected.component.company_marker", LocationId = first.LocationId, SlotId = InfluenceService.GetLocationSlotId(first.LocationId, 0) },
                new MapTokenView { ItemId = "isolated.lock", TypeId = "infected.influence_lock_token", DefinitionId = "infected.component.influence_lock", LocationId = first.LocationId }
            }
        };
        var poses = new List<object>();
        foreach (var location in map.CoordinateSpace.Layout.Locations)
        {
            view.MapTokens[0].LocationId = location.LocationId;
            view.MapTokens[1].LocationId = location.LocationId;
            view.MapTokens[1].SlotId = InfluenceService.GetLocationSlotId(location.LocationId, 0);
            view.MapTokens[2].LocationId = location.LocationId;
            string before = JsonConvert.SerializeObject(view);
            refresh.Invoke(ui, new object[] { view });
            Require(before == JsonConvert.SerializeObject(view), "呈现代码修改了输入投影");
            var visual = map.GetComponentsInChildren<SpriteRenderer>(true).Single(r => r.name == "GavialTokenArtwork");
            var expected = map.CoordinateSpace.ToWorldPosition(location.NormalizedPosition, 0) + map.MapRenderer.transform.TransformVector(template.transform.localPosition);
            Require((visual.transform.position - expected).sqrMagnitude < .000001f, "人物没有使用移动城市的真实位置锚点");
            var local = map.MapRenderer.transform.InverseTransformPoint(visual.transform.position);
            var distances = location.InfluenceSlots.Select(s => Vector2.Distance(local,
                map.MapRenderer.transform.InverseTransformPoint(map.CoordinateSpace.ToWorldPosition(location.NormalizedPosition + s.Offset, 0)))).ToArray();
            poses.Add(new { location = location.LocationId, anchor = V(expected), actual = V(visual.transform.position), localSize = V(visual.sprite.bounds.size), influenceSlotCenterDistances = distances });
        }
        var chosen = map.CoordinateSpace.Layout.Locations[Math.Min(2, map.CoordinateSpace.Layout.Locations.Count - 1)];
        foreach (var token in view.MapTokens) token.LocationId = chosen.LocationId;
        view.MapTokens[1].SlotId = InfluenceService.GetLocationSlotId(chosen.LocationId, 0);
        refresh.Invoke(ui, new object[] { view });
        foreach (var inactiveCity in map.CityPool) inactiveCity.Renderer.gameObject.SetActive(false);
        string stem = Path.GetFileNameWithoutExtension(path);
        Capture(camera, stem + "-人物独占停靠锚点.png");
        view.Players[0].CityLocationId = chosen.LocationId;
        string sharedBefore = JsonConvert.SerializeObject(view);
        refresh.Invoke(ui, new object[] { view });
        Require(sharedBefore == JsonConvert.SerializeObject(view), "共存呈现修改了输入投影");
        var artwork = map.GetComponentsInChildren<SpriteRenderer>(true).Single(r => r.name == "GavialTokenArtwork");
        var offset = serialized.FindProperty("gavialSharedCityOffset").vector3Value;
        var sharedExpected = map.CoordinateSpace.ToWorldPosition(chosen.NormalizedPosition, 0) + map.MapRenderer.transform.TransformVector(template.transform.localPosition + offset);
        Require((artwork.transform.position - sharedExpected).sqrMagnitude < .000001f, "共存时偏移与实际资产不同");
        var company = ui.GetComponentsInChildren<Button>(true).Single(b => b.name == "CompanyMarkerInspectTarget");
        var lockLabel = ui.GetComponentsInChildren<Text>(true).Single(t => t.name == "InfluenceLockToken");
        Require(company.GetComponentsInChildren<Text>(true).Length == 0 && company.enabled && company.targetGraphic.raycastTarget, "公司无字命中入口不完整");
        Require(!lockLabel.raycastTarget && !lockLabel.GetComponent<Button>().enabled, "锁定token原有非交互语义被改变");
        var city = map.CityPool[0];
        city.Renderer.transform.position = map.CoordinateSpace.ToWorldPosition(chosen.NormalizedPosition, 0);
        city.Renderer.enabled = false; city.Renderer.gameObject.SetActive(true); city.PieceVisual.SetVisible(true); city.PieceVisual.SetPlayerColor(Color.blue);
        var influence = map.InfluenceSlots.First(s => s.SlotId == view.MapTokens[1].SlotId);
        influence.Renderer.transform.position = map.CoordinateSpace.ToWorldPosition(chosen.NormalizedPosition + chosen.InfluenceSlots[0].Offset, 0);
        influence.Renderer.enabled = false; influence.Renderer.gameObject.SetActive(true); influence.PieceVisual.SetVisible(true);
        influence.PieceVisual.SetPlayerColor(new Color(217 / 255f, 112 / 255f, 0));
        Capture(camera, stem + "-当前场景地图组件.png");
        // 局部放大只用于看清原生组件，不是正式程序缩放手感验收。
        foreach (var canvas in roots.SelectMany(r => r.GetComponentsInChildren<Canvas>(true))) canvas.gameObject.SetActive(false);
        camera.orthographic = true; camera.orthographicSize = 2.3f * map.MapRenderer.transform.lossyScale.y;
        camera.transform.position = map.CoordinateSpace.ToWorldPosition(chosen.NormalizedPosition, 0) - camera.transform.forward * 10;
        Capture(camera, stem + "-同锚点城市与人物局部组件.png");
        scenes.Add(new { scene = path, mapId = map.CoordinateSpace.Layout.MapId, prefabSource = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(ui.gameObject),
            texturePath = AssetDatabase.GetAssetPath(sprite), spriteRect = sprite.rect.ToString(), spritePPU = sprite.pixelsPerUnit,
            importedTextureSize = new[] { sprite.texture.width, sprite.texture.height }, spriteSize = V(sprite.bounds.size), mapSpriteSize = V(map.MapRenderer.sprite.bounds.size),
            actualTemplateScale = V(artwork.transform.localScale), actualWorldBoundsSize = V(artwork.bounds.size),
            companyTextComponents = company.GetComponentsInChildren<Text>(true).Length, companyInspectEnabled = company.enabled,
            lockText = lockLabel.text, gavialTextComponents = artwork.GetComponentsInChildren<Text>(true).Length,
            sharedLocation = chosen.LocationId, sharedCityOffset = V(offset), cityRendererPresent = city.PieceVisual.gameObject.activeInHierarchy,
            inputViewUnchanged = true, isolatedAnchorChecks = poses });
        ui.Clear();
        Require(!map.GetComponentsInChildren<SpriteRenderer>(true).Any(r => r.name == "GavialTokenArtwork" && r.gameObject.activeSelf), "清理遗留人物显示");
    }
    private static float[] V(Vector3 value) => new[] { value.x, value.y, value.z };
    private static void Capture(Camera camera, string name)
    {
        var texture = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
        var oldTarget = camera.targetTexture; var oldActive = RenderTexture.active;
        camera.targetTexture = texture; camera.Render(); RenderTexture.active = texture;
        var pixels = new Texture2D(1920, 1080, TextureFormat.RGB24, false); pixels.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); pixels.Apply();
        using (var file = new FileStream(Path.Combine(output, name), FileMode.CreateNew)) { var png = pixels.EncodeToPNG(); file.Write(png, 0, png.Length); }
        camera.targetTexture = oldTarget; RenderTexture.active = oldActive;
        UnityEngine.Object.DestroyImmediate(pixels); texture.Release(); UnityEngine.Object.DestroyImmediate(texture);
    }
    private static void Write(string name, object value)
    { using (var stream = new FileStream(Path.Combine(output, name), FileMode.CreateNew)) using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(JsonConvert.SerializeObject(value, Formatting.Indented)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
