using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Presentation;
using YC.Presentation.Maps;
using Object = UnityEngine.Object;

namespace YC.EditorTools
{
    public static class MapViewEditorAssetBuilder
    {
        public const string SpriteLibraryPath =
            "Assets/YC/Presentation/Sprites/Map/MapVisualSprites.asset";
        public const string PrefabPath =
            "Assets/YC/Presentation/Prefabs/Map/MapView.prefab";
        public const string PieceMaterialPath =
            "Assets/YC/Presentation/Materials/MapPiecePlayerColor.mat";
        public const string MobileCityPiecePrefabPath =
            "Assets/YC/Presentation/Prefabs/Map/Pieces/MobileCityPiece.prefab";
        public const string InfluencePiecePrefabPath =
            "Assets/YC/Presentation/Prefabs/Map/Pieces/InfluencePiece.prefab";
        public const string SampleScenePath = "Assets/Scenes/SampleScene.unity";

        private const string TargetChildName = "MapView";
        private const float HotspotZ = -0.2f;
        private const float ResourceTokenZ = -0.18f;
        private const float LocationSlotZ = -0.25f;
        private const float RouteSlotZ = -0.24f;
        private const float MobileCityZ = -0.4f;
        private const float ScoreMarkerZ = -0.62f;
        private const float PieceOverlayLocalZ = -0.78f;

        [MenuItem("Tools/YC/Rebuild Map View Editor Assets")]
        public static void Rebuild()
        {
            var prefab = RebuildAssetsOnlyCore();
            InstallInSampleScene(prefab);
            AssetDatabase.SaveAssets();
            YC.Editor.MapFeedbackVisualBuildReadiness.ValidateReadyForBuild();
            Debug.Log("[MapViewEditorAssetBuilder] 已生成 MapVisualSprites、MapView Prefab 并安全接入 SampleScene/MapRoot。");
        }

        [MenuItem("Tools/YC/Rebuild Map View Assets Only")]
        public static void RebuildAssetsOnly()
        {
            RebuildAssetsOnlyCore();
            Debug.Log("[MapViewEditorAssetBuilder] 已生成并验证 MapVisualSprites 与 MapView Prefab；未修改场景。");
        }

        private static GameObject RebuildAssetsOnlyCore()
        {
            YC.Editor.UiThemeBuildReadiness.InitializeRequiredTheme();
            EnsureFolder("Assets/YC/Presentation/Sprites/Map");
            EnsureFolder("Assets/YC/Presentation/Prefabs/Map");

            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var layout = MapDisplayLayoutCatalog.Load(map.MapId);
            var layoutErrors = MapDisplayLayoutValidator.Validate(map, layout);
            if (layoutErrors.Count > 0)
            {
                throw new InvalidOperationException("地图布局无效：\n" + string.Join("\n", layoutErrors));
            }

            var feedbackVisuals = YC.Editor.MapFeedbackVisualEditorAssetBuilder.RebuildAssetFiles();
            BuildSpriteLibrary();
            AssetDatabase.ImportAsset(SpriteLibraryPath, ImportAssetOptions.ForceUpdate);
            var library = AssetDatabase.LoadAssetAtPath<MapVisualSpriteLibrary>(SpriteLibraryPath);
            if (library == null)
            {
                throw new InvalidOperationException(
                    "MapVisualSprites.asset could not be reloaded after Atlas packing.");
            }
            var influencePiecePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(InfluencePiecePrefabPath);
            var mobileCityPiecePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MobileCityPiecePrefabPath);
            if (influencePiecePrefab == null || mobileCityPiecePrefab == null)
            {
                throw new InvalidOperationException("缺少 InfluencePiece 或 MobileCityPiece Prefab。");
            }
            var prefab = BuildPrefab(
                map,
                layout,
                library,
                feedbackVisuals,
                influencePiecePrefab,
                mobileCityPiecePrefab);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(SpriteLibraryPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceUpdate);

            var assetErrors = ValidateGeneratedAssets();
            if (assetErrors.Count > 0)
            {
                throw new InvalidOperationException("地图视觉资产验证失败：\n" + string.Join("\n", assetErrors));
            }

            return prefab;
        }

        public static IReadOnlyList<string> ValidateGeneratedAssets()
        {
            var errors = new List<string>();
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var layout = MapDisplayLayoutCatalog.Load(map.MapId);
            var library = AssetDatabase.LoadAssetAtPath<MapVisualSpriteLibrary>(SpriteLibraryPath);
            ValidateLibrary(library, errors);
            ValidatePrefab(map, layout, errors);
            return errors;
        }

        [MenuItem("Tools/YC/Validate Installed Map View")]
        public static void ValidateInstalledMapViewMenu()
        {
            var errors = ValidateInstalledMapView(SceneManager.GetActiveScene());
            if (errors.Count > 0)
            {
                throw new InvalidOperationException("SampleScene 地图接线验证失败：\n" + string.Join("\n", errors));
            }
            Debug.Log("[MapViewEditorAssetBuilder] SampleScene 重开验证通过：roots6 / EventSystem1 / MapRoot child1 / connected / missing0 / dirtyfalse。");
        }

        public static IReadOnlyList<string> ValidateInstalledMapView(Scene scene)
        {
            var errors = new List<string>();
            if (!scene.IsValid() || scene.path != SampleScenePath)
            {
                errors.Add("当前场景不是已重开的 SampleScene。");
                return errors;
            }
            var roots = scene.GetRootGameObjects();
            var expectedRootNames = new[]
            {
                "MapRoot", "Main Camera", "Game Settings Menu", "RoundTracker", "EventSystem",
                "Gameplay Interaction HUD"
            };
            if (roots.Length != 6 || expectedRootNames.Any(name => roots.Count(item => item.name == name) != 1))
                errors.Add("SampleScene roots6 名称/唯一性异常。");
            if (FindAllInScene<EventSystem>(scene).Length != 1)
                errors.Add("SampleScene EventSystem 数量不是1。");
            if (scene.isDirty)
                errors.Add("SampleScene 重开后仍为 dirty。");
            var mapRoot = roots.FirstOrDefault(item => item.name == "MapRoot");
            if (mapRoot == null || mapRoot.transform.childCount != 1)
            {
                errors.Add("MapRoot 必须有且仅有一个 MapView child。");
                return errors;
            }
            var child = mapRoot.transform.GetChild(0).gameObject;
            var view = child.GetComponent<MapView>();
            if (child.name != TargetChildName || view == null ||
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(child) != PrefabPath ||
                !PrefabUtility.IsPartOfNonAssetPrefabInstance(child))
                errors.Add("MapRoot/MapView 未保持目标 Prefab connected。");
            var controller = mapRoot.GetComponent<MobileCityInteractionController>();
            var displayController = child.GetComponent<MapDisplayController>();
            if (controller == null || displayController == null || view == null)
                errors.Add("MapRoot 固定组件缺失。");
            else
            {
                var controllerSerialized = new SerializedObject(controller);
                if (RequireProperty(controllerSerialized, "mapViewBinding").objectReferenceValue != view ||
                    RequireProperty(controllerSerialized, "mapRenderer").objectReferenceValue != view.MapRenderer)
                    errors.Add("MobileCityInteractionController 地图引用未序列化到固定 MapView。");
                var displaySerialized = new SerializedObject(displayController);
                if (RequireProperty(displaySerialized, "mapRenderer").objectReferenceValue != view.MapRenderer)
                    errors.Add("MapRoot MapCoordinateSpace 固定引用不完整。");
            }
            var mapRootComponents = mapRoot.GetComponents<Component>();
            if (mapRootComponents.Any(component => component == null) ||
                mapRootComponents.Length != 2 ||
                mapRoot.GetComponent<SpriteRenderer>() != null ||
                mapRoot.GetComponent<MapDisplayController>() != null ||
                mapRoot.GetComponent<MapCoordinateSpace>() != null)
                errors.Add("MapRoot 仍有地图显示残留组件，预期仅保留 Transform 与 MobileCityInteractionController。");
            var missing = roots.SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Sum(item => item.gameObject.GetComponents<Component>().Count(component => component == null));
            if (missing != 0) errors.Add("SampleScene missing script 数量为 " + missing + "。");
            foreach (var name in new[] { "Game Settings Menu", "RoundTracker", "Gameplay Interaction HUD" })
            {
                var other = roots.FirstOrDefault(item => item.name == name);
                if (other == null || string.IsNullOrEmpty(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(other)))
                    errors.Add(name + " 的原 Prefab 连接丢失。");
            }
            return errors;
        }

        private static MapVisualSpriteLibrary BuildSpriteLibrary()
        {
            var main = AssetDatabase.LoadMainAssetAtPath(SpriteLibraryPath);
            if (main != null && !(main is MapVisualSpriteLibrary))
            {
                throw new InvalidOperationException("目标精灵库路径已被非地图资产占用：" + SpriteLibraryPath);
            }

            var library = main as MapVisualSpriteLibrary;
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<MapVisualSpriteLibrary>();
                library.name = "MapVisualSprites";
                AssetDatabase.CreateAsset(library, SpriteLibraryPath);
            }

            RepairAtlasContaminatedGeneratedSprites(library);

            var retainedTextures = new List<Texture2D>();
            var retainedSprites = new List<Sprite>();
            var hotspot = EnsureCircleSprite(library, "MapHotspot", 96, 40f, 8f, retainedTextures, retainedSprites);
            var empty = EnsureCircleSprite(library, "MapInfluenceEmpty", 24, 8f, 2f, retainedTextures, retainedSprites);
            var occupied = EnsureSquareSprite(library, "MapInfluenceOccupied", 24, 16f, 0f, false, retainedTextures, retainedSprites);
            var border = EnsureSquareSprite(library, "MapInfluenceBorder", 32, 12f, 2f, true, retainedTextures, retainedSprites);
            var feedback = EnsureCircleSprite(library, "MapPlacementRing", 64, 27f, 3f, retainedTextures, retainedSprites);
            var city = EnsureCitySprite(library, retainedTextures, retainedSprites);
            var score = EnsureSquareSprite(library, "MapScoreMarker", 32, 24f, 0f, false, retainedTextures, retainedSprites);
            var scoreBorder = EnsureSquareSprite(library, "MapScoreMarkerBorder", 32, 29f, 4f, true, retainedTextures, retainedSprites);

            var serialized = new SerializedObject(library);
            SetObject(serialized, "hotspot", hotspot);
            SetObject(serialized, "emptyInfluenceSlot", empty);
            SetObject(serialized, "occupiedInfluenceSlot", occupied);
            SetObject(serialized, "movableInfluenceBorder", border);
            SetObject(serialized, "placementFeedbackRing", feedback);
            SetObject(serialized, "mobileCity", city);
            SetObject(serialized, "scoreMarker", score);
            SetObject(serialized, "scoreMarkerBorder", scoreBorder);
            SetObjectArray(serialized, "retainedTextures", retainedTextures.Cast<Object>().ToList());
            SetObjectArray(serialized, "retainedSprites", retainedSprites.Cast<Object>().ToList());
            SetResourceSprites(serialized);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            return library;
        }

        private static Sprite EnsureCircleSprite(
            MapVisualSpriteLibrary library,
            string name,
            int size,
            float radius,
            float thickness,
            List<Texture2D> textures,
            List<Sprite> sprites)
        {
            return EnsureGeneratedSprite(
                library,
                name,
                size,
                size,
                size,
                FilterMode.Bilinear,
                (texture, x, y) =>
                {
                    var center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
                    var distance = Vector2.Distance(new Vector2(x, y), center);
                    return new Color(1f, 1f, 1f, distance <= radius && distance >= radius - thickness ? 1f : 0f);
                },
                textures,
                sprites);
        }

        private static Sprite EnsureSquareSprite(
            MapVisualSpriteLibrary library,
            string name,
            int size,
            float side,
            float thickness,
            bool outline,
            List<Texture2D> textures,
            List<Sprite> sprites)
        {
            return EnsureGeneratedSprite(
                library,
                name,
                size,
                size,
                size,
                FilterMode.Point,
                (texture, x, y) =>
                {
                    var center = (size - 1) * 0.5f;
                    var dx = Mathf.Abs(x - center);
                    var dy = Mathf.Abs(y - center);
                    var outer = side * 0.5f;
                    var insideOuter = dx <= outer && dy <= outer;
                    var insideInner = dx <= Mathf.Max(0f, outer - thickness) &&
                                      dy <= Mathf.Max(0f, outer - thickness);
                    var alpha = insideOuter && (!outline || !insideInner) ? 1f : 0f;
                    return new Color(1f, 1f, 1f, alpha);
                },
                textures,
                sprites);
        }

        private static Sprite EnsureCitySprite(
            MapVisualSpriteLibrary library,
            List<Texture2D> textures,
            List<Sprite> sprites)
        {
            const int width = 80;
            const int height = 132;
            return EnsureGeneratedSprite(
                library,
                "MapMobileCity",
                width,
                height,
                100f,
                FilterMode.Point,
                (texture, x, y) =>
                {
                    var edge = x < 5 || x >= width - 5 || y < 5 || y >= height - 5;
                    var stripe = x > 14 && x < 18 || x > 37 && x < 41 || x > 60 && x < 64;
                    return edge ? Color.white : stripe
                        ? new Color(0.82f, 0.82f, 0.82f, 1f)
                        : new Color(0.58f, 0.58f, 0.58f, 1f);
                },
                textures,
                sprites);
        }

        private static Sprite EnsureGeneratedSprite(
            MapVisualSpriteLibrary library,
            string name,
            int width,
            int height,
            float pixelsPerUnit,
            FilterMode filterMode,
            Func<Texture2D, int, int, Color> pixel,
            List<Texture2D> textures,
            List<Sprite> sprites)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(SpriteLibraryPath);
            var matchingTextures = assets.OfType<Texture2D>()
                .Where(item => item != null && item.name == name + "Texture")
                .ToArray();
            if (matchingTextures.Length > 1)
            {
                throw new InvalidOperationException(name + " 存在重复持久纹理子资产。");
            }
            var texture = matchingTextures.SingleOrDefault();
            if (texture == null)
            {
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = name + "Texture" };
                AssetDatabase.AddObjectToAsset(texture, library);
            }
            if (texture.width != width || texture.height != height)
            {
                throw new InvalidOperationException(name + " 持久纹理尺寸与构建定义冲突。");
            }
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = filterMode;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    texture.SetPixel(x, y, pixel(texture, x, y));
                }
            }
            texture.Apply(false, false);
            EditorUtility.SetDirty(texture);

            var matchingSprites = assets.OfType<Sprite>()
                .Where(item => item != null && item.name == name)
                .ToArray();
            if (matchingSprites.Length > 1)
            {
                throw new InvalidOperationException(name + " 存在重复持久 Sprite 子资产。");
            }
            var sprite = matchingSprites.SingleOrDefault();
            if (sprite != null &&
                (sprite.packed || sprite.texture == null ||
                 AssetDatabase.GetAssetPath(sprite.texture) != SpriteLibraryPath))
            {
                // One-time migration from the unsupported state where generated
                // Sprite.Create sub-assets were packed into a SpriteAtlas. Recreating
                // only the contaminated Sprite preserves the source Texture2D and the
                // library asset GUID; the prefab is rebuilt later in the same operation.
                Object.DestroyImmediate(sprite, true);
                sprite = null;
            }
            if (sprite == null)
            {
                sprite = Sprite.Create(
                    texture,
                    new Rect(0f, 0f, width, height),
                    new Vector2(0.5f, 0.5f),
                    pixelsPerUnit);
                sprite.name = name;
                AssetDatabase.AddObjectToAsset(sprite, library);
            }
            textures.Add(texture);
            sprites.Add(sprite);
            return sprite;
        }

        private static void RepairAtlasContaminatedGeneratedSprites(MapVisualSpriteLibrary library)
        {
            const string invalidGuid = "guid: 00000000000000000000000000000000";
            if (!File.Exists(SpriteLibraryPath) ||
                !File.ReadAllText(SpriteLibraryPath).Contains(invalidGuid))
            {
                return;
            }

            var contaminatedSprites = AssetDatabase.LoadAllAssetsAtPath(SpriteLibraryPath)
                .OfType<Sprite>()
                .Where(item => item != null)
                .ToArray();
            if (contaminatedSprites.Length != 8)
            {
                throw new InvalidOperationException(
                    "MapVisualSprites.asset 全零 GUID 迁移要求恰好重建 8 个生成 Sprite，实际为 " +
                    contaminatedSprites.Length + "。");
            }

            for (var i = 0; i < contaminatedSprites.Length; i++)
            {
                Object.DestroyImmediate(contaminatedSprites[i], true);
            }
            EditorUtility.SetDirty(library);
        }

        private static void SetResourceSprites(SerializedObject serialized)
        {
            var definitions = new List<ResourceTokenIconDefinition>();
            definitions.AddRange(ResourceTokenIconDefinitions.GetRequiredIcons());
            definitions.AddRange(ResourceTokenIconDefinitions.GetAmountSpecificIcons());
            var property = RequireProperty(serialized, "resourceTokens");
            property.arraySize = definitions.Count;
            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                var path = "Assets/YC/Data/ResourceIcons/" + definition.FileName;
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null)
                {
                    throw new InvalidOperationException("缺少已导入资源 token Sprite：" + path);
                }
                var locked = YC.Editor.MapFeedbackVisualEditorAssetBuilder.LockedResourceTokens
                    .Where(item => item.ResourceType == definition.ResourceType &&
                                   item.Amount == definition.Amount)
                    .ToArray();
                if (locked.Length != 1 ||
                    !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                        sprite,
                        out var spriteGuid,
                        out long spriteLocalId) ||
                    !string.Equals(spriteGuid, locked[0].Guid, StringComparison.OrdinalIgnoreCase) ||
                    spriteLocalId != locked[0].LocalId)
                {
                    throw new InvalidOperationException(
                        "资源 token 的 key/GUID/localID 与锁定映射不一致：" +
                        definition.ResourceType + ":" + definition.Amount);
                }
                var element = property.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("resourceType").enumValueIndex = (int)definition.ResourceType;
                element.FindPropertyRelative("amount").intValue = definition.Amount;
                element.FindPropertyRelative("sprite").objectReferenceValue = sprite;
            }
        }

        private static GameObject BuildPrefab(
            GameMapDefinition map,
            MapDisplayLayout layout,
            MapVisualSpriteLibrary library,
            YC.Editor.MapFeedbackVisualAssetSet feedbackVisuals,
            GameObject influencePiecePrefab,
            GameObject mobileCityPiecePrefab)
        {
            var root = new GameObject(TargetChildName);
            try
            {
                var coordinateRenderer = root.AddComponent<SpriteRenderer>();
                coordinateRenderer.sprite = layout.MapSprite;
                coordinateRenderer.enabled = true;
                var displayController = root.AddComponent<MapDisplayController>();
                SetReferences(displayController, ("mapRenderer", coordinateRenderer));
                var coordinateSpace = root.AddComponent<MapCoordinateSpace>();
                coordinateSpace.Configure(coordinateRenderer, layout, root.transform);
                var view = root.AddComponent<MapView>();

                var locationsRoot = CreateGroup(root.transform, "Locations");
                var resourcesRoot = CreateGroup(root.transform, "Resource Tokens");
                var slotsRoot = CreateGroup(root.transform, "Influence Slots");
                var citiesRoot = CreateGroup(root.transform, "City Pool");
                var scoresRoot = CreateGroup(root.transform, "Score Marker Pool");
                BuildPieceLight(root.transform);

                var locationBindings = BuildLocations(
                    layout,
                    library,
                    feedbackVisuals,
                    coordinateSpace,
                    locationsRoot);
                var resourceBindings = BuildResources(layout, library, coordinateSpace, resourcesRoot);
                var slotBindings = BuildSlots(
                    layout,
                    library,
                    feedbackVisuals,
                    influencePiecePrefab,
                    coordinateSpace,
                    slotsRoot);
                var cityBindings = BuildCities(map, library, mobileCityPiecePrefab, citiesRoot);
                var scoreBindings = BuildScores(map, library, influencePiecePrefab, scoresRoot);
                SetViewReferences(
                    view,
                    coordinateSpace,
                    library,
                    locationBindings,
                    resourceBindings,
                    slotBindings,
                    cityBindings,
                    scoreBindings);

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
            return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        }

        private static List<LocationBuildBinding> BuildLocations(
            MapDisplayLayout layout,
            MapVisualSpriteLibrary library,
            YC.Editor.MapFeedbackVisualAssetSet feedbackVisuals,
            MapCoordinateSpace space,
            Transform parent)
        {
            var result = new List<LocationBuildBinding>();
            for (var i = 0; i < layout.Locations.Count; i++)
            {
                var definition = layout.Locations[i];
                var go = new GameObject("Hotspot " + definition.LocationId);
                go.transform.SetParent(parent, false);
                go.transform.position = space.ToWorldPosition(definition.NormalizedPosition, HotspotZ);
                var renderer = go.AddComponent<SpriteRenderer>();
                renderer.sprite = library.Hotspot;
                renderer.sharedMaterial = feedbackVisuals.FeedbackMaterial;
                renderer.color = new Color(0.25f, 0.95f, 0.45f, 0f);
                renderer.sortingOrder = 10;
                var collider = go.AddComponent<CircleCollider2D>();
                collider.radius = 0.45f;
                var hotspot = go.AddComponent<MapHotspot>();
                var pulse = AddRuntimeComponent(go, "YC.Presentation.MapHighlightPulse");
                var feedback = AddFeedback(
                    go,
                    library.PlacementFeedbackRing,
                    feedbackVisuals,
                    11);
                SetReferences(hotspot, ("spriteRenderer", renderer), ("highlightPulse", pulse), ("placementFeedback", feedback));
                SetReferences(pulse, ("border", renderer));
                result.Add(new LocationBuildBinding(definition.LocationId, hotspot));
            }
            return result;
        }

        private static List<ResourceBuildBinding> BuildResources(
            MapDisplayLayout layout,
            MapVisualSpriteLibrary library,
            MapCoordinateSpace space,
            Transform parent)
        {
            var definitions = layout.CreateResourcePointDefinitions();
            var result = new List<ResourceBuildBinding>();
            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                var go = new GameObject("ResourceToken " + definition.LocationId);
                go.transform.SetParent(parent, false);
                go.transform.position = space.ToWorldPosition(definition.ResourceTokenPosition, ResourceTokenZ);
                var renderer = go.AddComponent<SpriteRenderer>();
                renderer.sprite = library.EmptyInfluenceSlot;
                renderer.color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
                renderer.sortingOrder = 18;
                result.Add(new ResourceBuildBinding(definition.LocationId, renderer));
            }
            return result;
        }

        private static List<SlotBuildBinding> BuildSlots(
            MapDisplayLayout layout,
            MapVisualSpriteLibrary library,
            YC.Editor.MapFeedbackVisualAssetSet feedbackVisuals,
            GameObject influencePiecePrefab,
            MapCoordinateSpace space,
            Transform parent)
        {
            var result = new List<SlotBuildBinding>();
            var locations = layout.CreateResourcePointDefinitions();
            for (var i = 0; i < locations.Count; i++)
            {
                var definition = locations[i];
                for (var slotIndex = 0; slotIndex < definition.InfluenceSlots.Count; slotIndex++)
                {
                    result.Add(BuildSlot(
                        InfluenceService.GetLocationSlotId(definition.LocationId, slotIndex),
                        definition.InfluenceSlots[slotIndex],
                        LocationSlotZ,
                        library,
                        feedbackVisuals,
                        influencePiecePrefab,
                        space,
                        parent));
                }
            }
            var routes = layout.CreateRouteDefinitions();
            for (var i = 0; i < routes.Count; i++)
            {
                var definition = routes[i];
                for (var slotIndex = 0; slotIndex < definition.InfluenceSlots.Count; slotIndex++)
                {
                    result.Add(BuildSlot(
                        InfluenceService.GetRouteSlotId(definition.RouteId, slotIndex),
                        definition.InfluenceSlots[slotIndex],
                        RouteSlotZ,
                        library,
                        feedbackVisuals,
                        influencePiecePrefab,
                        space,
                        parent));
                }
            }
            return result;
        }

        private static SlotBuildBinding BuildSlot(
            string slotId,
            MapInfluenceSlotDisplayDefinition definition,
            float z,
            MapVisualSpriteLibrary library,
            YC.Editor.MapFeedbackVisualAssetSet feedbackVisuals,
            GameObject influencePiecePrefab,
            MapCoordinateSpace space,
            Transform parent)
        {
            var go = new GameObject("InfluenceSlot " + slotId);
            go.transform.SetParent(parent, false);
            go.transform.position = space.ToWorldPosition(definition.NormalizedPosition, z);
            go.transform.localScale = Vector3.one * definition.Size;
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = library.EmptyInfluenceSlot;
            renderer.color = new Color(0.25f, 0.95f, 0.45f, 0.65f);
            renderer.sortingOrder = 15;
            var collider = go.AddComponent<CircleCollider2D>();
            collider.radius = definition.ColliderRadius;
            var click = go.AddComponent<InfluenceSlotClickTarget>();
            var pieceVisual = InstantiatePiece(
                influencePiecePrefab,
                go.transform,
                "Influence Piece",
                new Vector3(0f, 0f, -z / definition.Size),
                Vector3.one / definition.Size);
            pieceVisual.SetVisible(false);

            var borderObject = new GameObject("MovableInfluenceBorder");
            borderObject.transform.SetParent(go.transform, false);
            borderObject.transform.localPosition = new Vector3(
                0f,
                0f,
                PieceOverlayLocalZ / definition.Size);
            var border = borderObject.AddComponent<SpriteRenderer>();
            border.sprite = library.MovableInfluenceBorder;
            border.sharedMaterial = feedbackVisuals.FeedbackMaterial;
            border.color = UiTheme.CyanAccent;
            border.sortingOrder = 16;
            border.enabled = false;
            var pulse = AddRuntimeComponent(borderObject, "YC.Presentation.MapHighlightPulse");
            SetReferences(pulse, ("border", border));
            var feedback = AddFeedback(
                go,
                library.PlacementFeedbackRing,
                feedbackVisuals,
                17,
                PieceOverlayLocalZ / definition.Size);
            return new SlotBuildBinding(
                slotId,
                renderer,
                collider,
                click,
                border,
                pulse,
                feedback,
                pieceVisual);
        }

        private static List<CityBuildBinding> BuildCities(
            GameMapDefinition map,
            MapVisualSpriteLibrary library,
            GameObject mobileCityPiecePrefab,
            Transform parent)
        {
            var result = new List<CityBuildBinding>();
            for (var playerId = 1; playerId <= map.MaxPlayers; playerId++)
            {
                var go = new GameObject("Mobile City P" + playerId);
                go.transform.SetParent(parent, false);
                go.transform.localScale = Vector3.one;
                var renderer = go.AddComponent<SpriteRenderer>();
                renderer.sprite = library.MobileCity;
                renderer.sortingOrder = 20 + playerId;
                renderer.enabled = false;
                var collider = go.AddComponent<BoxCollider2D>();
                collider.size = new Vector2(0.95f, 1.35f);
                var click = go.AddComponent<MobileCityClickTarget>();
                var pieceVisual = InstantiatePiece(
                    mobileCityPiecePrefab,
                    go.transform,
                    "Mobile City Piece",
                    new Vector3(0f, 0f, -MobileCityZ),
                    Vector3.one);
                pieceVisual.SetVisible(false);
                result.Add(new CityBuildBinding(playerId, renderer, collider, click, pieceVisual));
                go.SetActive(false);
            }
            return result;
        }

        private static List<ScoreBuildBinding> BuildScores(
            GameMapDefinition map,
            MapVisualSpriteLibrary library,
            GameObject influencePiecePrefab,
            Transform parent)
        {
            var result = new List<ScoreBuildBinding>();
            for (var playerId = 1; playerId <= map.MaxPlayers; playerId++)
            {
                var go = new GameObject("ScoreMarker P" + playerId);
                go.transform.SetParent(parent, false);
                go.transform.localScale = Vector3.one;
                var renderer = go.AddComponent<SpriteRenderer>();
                renderer.sprite = library.ScoreMarker;
                renderer.sortingOrder = 31;
                renderer.enabled = false;
                var borderObject = new GameObject("ScoreMarker Border");
                borderObject.transform.SetParent(go.transform, false);
                var border = borderObject.AddComponent<SpriteRenderer>();
                border.sprite = library.ScoreMarkerBorder;
                border.color = new Color(0.04f, 0.025f, 0.015f, 0.95f);
                border.sortingOrder = 30;
                border.enabled = false;
                var pieceVisual = InstantiatePiece(
                    influencePiecePrefab,
                    go.transform,
                    "Score Influence Piece",
                    new Vector3(0f, 0f, -ScoreMarkerZ),
                    Vector3.one);
                pieceVisual.SetVisible(false);
                result.Add(new ScoreBuildBinding(playerId, renderer, border, pieceVisual));
                go.SetActive(false);
            }
            return result;
        }

        private static Component AddFeedback(
            GameObject owner,
            Sprite sprite,
            YC.Editor.MapFeedbackVisualAssetSet feedbackVisuals,
            int sortingOrder,
            float localZ = 0f)
        {
            var feedback = AddRuntimeComponent(owner, "YC.Presentation.MapPlacementFeedback");
            if (!(feedback is Behaviour feedbackBehaviour))
            {
                throw new InvalidOperationException("MapPlacementFeedback 必须是可启用的 Behaviour。");
            }
            feedbackBehaviour.enabled = true;
            var animator = owner.AddComponent<Animator>();
            animator.enabled = false;
            animator.runtimeAnimatorController = feedbackVisuals.PlacementController;
            animator.updateMode = AnimatorUpdateMode.UnscaledTime;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.applyRootMotion = false;
            var flashObject = new GameObject("Placement Flash");
            flashObject.transform.SetParent(owner.transform, false);
            flashObject.transform.localPosition = new Vector3(0f, 0f, localZ);
            flashObject.transform.localRotation = Quaternion.identity;
            flashObject.transform.localScale = Vector3.one;
            var flash = flashObject.AddComponent<SpriteRenderer>();
            flash.sprite = sprite;
            flash.sharedMaterial = feedbackVisuals.FeedbackMaterial;
            flash.color = UiTheme.CyanAccent;
            flash.sortingOrder = sortingOrder;
            flash.enabled = false;
            var ringObject = new GameObject("Placement Expanding Ring");
            ringObject.transform.SetParent(owner.transform, false);
            ringObject.transform.localPosition = new Vector3(0f, 0f, localZ);
            ringObject.transform.localRotation = Quaternion.identity;
            ringObject.transform.localScale = Vector3.one;
            var ring = ringObject.AddComponent<SpriteRenderer>();
            ring.sprite = sprite;
            ring.sharedMaterial = feedbackVisuals.FeedbackMaterial;
            ring.color = UiTheme.CyanAccent;
            ring.sortingOrder = sortingOrder + 1;
            ring.enabled = false;
            SetReferences(
                feedback,
                ("flashRenderer", flash),
                ("expandingRingRenderer", ring),
                ("animator", animator));
            return feedback;
        }

        private static MapPieceVisual InstantiatePiece(
            GameObject piecePrefab,
            Transform parent,
            string name,
            Vector3 localPosition,
            Vector3 localScale)
        {
            var instance = PrefabUtility.InstantiatePrefab(piecePrefab, parent) as GameObject;
            if (instance == null)
            {
                throw new InvalidOperationException("无法实例化地图棋子 Prefab：" + piecePrefab.name);
            }
            instance.name = name;
            instance.transform.localPosition = localPosition;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = localScale;
            var visual = instance.GetComponent<MapPieceVisual>();
            if (visual == null)
            {
                throw new InvalidOperationException("地图棋子 Prefab 缺少 MapPieceVisual：" + piecePrefab.name);
            }
            if (!visual.TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException("地图棋子 Prefab 配置无效：" + reason);
            }
            return visual;
        }

        private static void BuildPieceLight(Transform parent)
        {
            var lightObject = new GameObject("Map Piece Key Light");
            lightObject.transform.SetParent(parent, false);
            lightObject.transform.localPosition = Vector3.zero;
            lightObject.transform.localRotation = Quaternion.Euler(35f, -25f, 0f);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.95f, 0.9f, 1f);
            light.intensity = 0.85f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.45f;
            light.renderMode = LightRenderMode.ForcePixel;
        }

        private static Component AddRuntimeComponent(GameObject owner, string typeName)
        {
            var type = typeof(MapView).Assembly.GetType(typeName, false);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                throw new InvalidOperationException("缺少运行时地图组件：" + typeName);
            }
            return owner.AddComponent(type);
        }

        private static void SetViewReferences(
            MapView view,
            MapCoordinateSpace space,
            MapVisualSpriteLibrary library,
            List<LocationBuildBinding> locations,
            List<ResourceBuildBinding> resources,
            List<SlotBuildBinding> slots,
            List<CityBuildBinding> cities,
            List<ScoreBuildBinding> scores)
        {
            var serialized = new SerializedObject(view);
            SetObject(serialized, "coordinateSpace", space);
            SetObject(serialized, "spriteLibrary", library);
            SetBindingArray(serialized, "locations", locations.Count, (element, i) =>
            {
                element.FindPropertyRelative("locationId").stringValue = locations[i].Id;
                element.FindPropertyRelative("hotspot").objectReferenceValue = locations[i].Hotspot;
            });
            SetBindingArray(serialized, "resourceTokens", resources.Count, (element, i) =>
            {
                element.FindPropertyRelative("locationId").stringValue = resources[i].Id;
                element.FindPropertyRelative("renderer").objectReferenceValue = resources[i].Renderer;
            });
            SetBindingArray(serialized, "influenceSlots", slots.Count, (element, i) =>
            {
                var binding = slots[i];
                element.FindPropertyRelative("slotId").stringValue = binding.Id;
                element.FindPropertyRelative("renderer").objectReferenceValue = binding.Renderer;
                element.FindPropertyRelative("collider").objectReferenceValue = binding.Collider;
                element.FindPropertyRelative("clickTarget").objectReferenceValue = binding.Click;
                element.FindPropertyRelative("borderRenderer").objectReferenceValue = binding.Border;
                element.FindPropertyRelative("borderPulse").objectReferenceValue = binding.Pulse;
                element.FindPropertyRelative("placementFeedback").objectReferenceValue = binding.Feedback;
                element.FindPropertyRelative("pieceVisual").objectReferenceValue = binding.PieceVisual;
            });
            SetBindingArray(serialized, "cityPool", cities.Count, (element, i) =>
            {
                var binding = cities[i];
                element.FindPropertyRelative("playerId").intValue = binding.PlayerId;
                element.FindPropertyRelative("renderer").objectReferenceValue = binding.Renderer;
                element.FindPropertyRelative("collider").objectReferenceValue = binding.Collider;
                element.FindPropertyRelative("clickTarget").objectReferenceValue = binding.Click;
                element.FindPropertyRelative("pieceVisual").objectReferenceValue = binding.PieceVisual;
            });
            SetBindingArray(serialized, "scoreMarkerPool", scores.Count, (element, i) =>
            {
                var binding = scores[i];
                element.FindPropertyRelative("playerId").intValue = binding.PlayerId;
                element.FindPropertyRelative("renderer").objectReferenceValue = binding.Renderer;
                element.FindPropertyRelative("borderRenderer").objectReferenceValue = binding.Border;
                element.FindPropertyRelative("pieceVisual").objectReferenceValue = binding.PieceVisual;
            });
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ValidateLibrary(MapVisualSpriteLibrary library, List<string> errors)
        {
            if (library == null)
            {
                errors.Add("缺少 MapVisualSprites.asset。");
                return;
            }
            if (!library.TryValidateConfiguration(out var reason))
            {
                errors.Add(reason);
            }
            var generated = new[]
            {
                library.Hotspot, library.EmptyInfluenceSlot, library.OccupiedInfluenceSlot,
                library.MovableInfluenceBorder, library.PlacementFeedbackRing, library.MobileCity,
                library.ScoreMarker, library.ScoreMarkerBorder
            };
            for (var i = 0; i < generated.Length; i++)
            {
                if (generated[i] == null || AssetDatabase.GetAssetPath(generated[i]) != SpriteLibraryPath)
                {
                    errors.Add("地图生成 Sprite 没有持久绑定到 MapVisualSprites.asset，索引 " + i + "。");
                }
            }
            ValidateObjectArrayReferences(library, "retainedTextures", 8, SpriteLibraryPath, errors);
            ValidateObjectArrayReferences(library, "retainedSprites", 8, SpriteLibraryPath, errors);
            var retainedSprites = RequireProperty(new SerializedObject(library), "retainedSprites");
            if (retainedSprites.arraySize == generated.Length)
            {
                for (var i = 0; i < generated.Length; i++)
                {
                    if (retainedSprites.GetArrayElementAtIndex(i).objectReferenceValue != generated[i])
                    {
                        errors.Add("retainedSprites 与公开生成 Sprite 身份不一致，索引 " + i + "。");
                    }
                }
            }
        }

        private static void ValidatePrefab(GameMapDefinition map, MapDisplayLayout layout, List<string> errors)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                errors.Add("缺少 MapView.prefab。");
                return;
            }
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var missing = root.GetComponentsInChildren<Transform>(true)
                    .Sum(item => item.gameObject.GetComponents<Component>().Count(component => component == null));
                if (missing != 0)
                {
                    errors.Add("MapView.prefab missing script 数量为 " + missing + "。");
                }
                var view = root.GetComponent<MapView>();
                if (view == null)
                {
                    errors.Add("Prefab 根缺少 MapView。");
                    return;
                }
                if (!view.TryValidateConfiguration(map, out var reason))
                {
                    errors.Add(reason);
                    return;
                }
                var mapRenderer = root.GetComponent<SpriteRenderer>();
                var displayController = root.GetComponent<MapDisplayController>();
                if (mapRenderer == null || !mapRenderer.enabled || view.MapRenderer != mapRenderer ||
                    displayController == null ||
                    RequireProperty(new SerializedObject(displayController), "mapRenderer").objectReferenceValue != mapRenderer)
                {
                    errors.Add("MapView Prefab 的地图渲染与相机适配组件接线不完整。");
                }
                var locationSlots = map.Locations.Sum(item => item.InfluenceSlotCount);
                var routeSlots = map.Routes.Sum(item => item.InfluenceSlotCount);
                if (view.Locations.Count != map.Locations.Count ||
                    view.ResourceTokens.Count != layout.CreateResourcePointDefinitions().Count ||
                    view.InfluenceSlots.Count != locationSlots + routeSlots ||
                    view.CityPool.Count != 4 || view.ScoreMarkerPool.Count != 4)
                {
                    errors.Add("Prefab 绑定数量不符合真实布局：地点/资源/槽/城市/计分池。");
                }
                if (locationSlots != 44 || routeSlots != 33 || view.InfluenceSlots.Count != 77)
                {
                    errors.Add("四人地图槽位事实异常，预期地点44+路线33=77。");
                }
                ValidateComponentCount<MapHotspot>(root, map.Locations.Count, errors);
                ValidateComponentCount<MapDisplayController>(root, 1, errors);
                ValidateComponentCount<MobileCityClickTarget>(root, map.MaxPlayers, errors);
                ValidateComponentCount<InfluenceSlotClickTarget>(root, locationSlots + routeSlots, errors);
                ValidateComponentCount<MapHighlightPulse>(root, map.Locations.Count + locationSlots + routeSlots, errors);
                ValidateComponentCount<MapPlacementFeedback>(root, map.Locations.Count + locationSlots + routeSlots, errors);
                ValidateComponentCount<Animator>(root, map.Locations.Count + locationSlots + routeSlots, errors);
                ValidateComponentCount<MapPieceVisual>(root, locationSlots + routeSlots + map.MaxPlayers * 2, errors);
                ValidateComponentCount<MeshRenderer>(root, locationSlots + routeSlots + map.MaxPlayers * 2, errors);
                ValidateComponentCount<Light>(root, 1, errors);
                ValidateComponentCount<Collider>(root, 0, errors);
                ValidateComponentCount<Rigidbody>(root, 0, errors);
                ValidatePieceAssets(root, errors);
                ValidateUniqueAndExactIds(map, layout, view, errors);
                ValidateSerializedReferences(view, errors);
                ValidateVisualSettings(map, layout, view, errors);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void ValidateUniqueAndExactIds(
            GameMapDefinition map,
            MapDisplayLayout layout,
            MapView view,
            List<string> errors)
        {
            var expectedLocations = new HashSet<string>(map.Locations.Select(item => item.LocationId));
            var actualLocations = new HashSet<string>(view.Locations.Select(item => item.LocationId));
            var resourceIds = view.ResourceTokens.Select(item => item.LocationId).ToList();
            var expectedResources = new HashSet<string>(
                layout.CreateResourcePointDefinitions().Select(item => item.LocationId));
            if (!expectedLocations.SetEquals(actualLocations) || actualLocations.Count != view.Locations.Count)
            {
                errors.Add("地点 binding ID 未完整唯一覆盖领域 Map。");
            }
            if (!expectedResources.SetEquals(resourceIds) || resourceIds.Distinct().Count() != resourceIds.Count)
            {
                errors.Add("资源 token binding ID 未完整唯一覆盖真实布局定义。");
            }
            var expectedSlots = BuildExpectedSlotIds(map);
            var actualSlots = view.InfluenceSlots.Select(item => item.SlotId).ToList();
            if (!expectedSlots.SetEquals(actualSlots) || actualSlots.Distinct().Count() != actualSlots.Count)
            {
                errors.Add("影响力槽 binding ID 未完整唯一覆盖领域 Map。");
            }
        }

        private static void ValidateSerializedReferences(MapView view, List<string> errors)
        {
            var serialized = new SerializedObject(view);
            foreach (var listName in new[] { "locations", "resourceTokens", "influenceSlots", "cityPool", "scoreMarkerPool" })
            {
                var list = RequireProperty(serialized, listName);
                for (var i = 0; i < list.arraySize; i++)
                {
                    var iterator = list.GetArrayElementAtIndex(i).Copy();
                    var end = iterator.GetEndProperty();
                    if (!iterator.Next(true))
                    {
                        continue;
                    }
                    do
                    {
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference &&
                            iterator.objectReferenceValue == null)
                        {
                            errors.Add(listName + "[" + i + "] 缺少引用 " + iterator.name + "。");
                        }
                    } while (iterator.Next(false) && !SerializedProperty.EqualContents(iterator, end));
                }
            }
            foreach (var hotspot in view.Locations.Select(item => item.Hotspot))
            {
                ValidateObjectReferences(hotspot, errors);
            }
            foreach (var component in view.GetComponentsInChildren<Component>(true))
            {
                if (component != null && (component.GetType().Name == "MapHighlightPulse" ||
                                          component.GetType().Name == "MapPlacementFeedback"))
                {
                    ValidateObjectReferences(component, errors);
                }
            }
        }

        private static void ValidateVisualSettings(
            GameMapDefinition map,
            MapDisplayLayout layout,
            MapView view,
            List<string> errors)
        {
            for (var i = 0; i < view.Locations.Count; i++)
            {
                var hotspot = view.Locations[i].Hotspot;
                var collider = hotspot.GetComponent<CircleCollider2D>();
                Check(hotspot.Renderer.sortingOrder == 10, "热点 sortingOrder", errors);
                Check(Approximately(hotspot.transform.localPosition.z, HotspotZ), "热点 z", errors);
                Check(collider != null && Approximately(collider.radius, 0.45f), "热点 collider", errors);
            }
            for (var i = 0; i < view.ResourceTokens.Count; i++)
            {
                var renderer = view.ResourceTokens[i].Renderer;
                Check(renderer.sortingOrder == 18, "资源 token sortingOrder", errors);
                Check(Approximately(renderer.transform.localPosition.z, ResourceTokenZ), "资源 token z", errors);
            }
            var expectedSlots = BuildSlotSettings(layout);
            for (var i = 0; i < view.InfluenceSlots.Count; i++)
            {
                var binding = view.InfluenceSlots[i];
                var expected = expectedSlots[binding.SlotId];
                Check(binding.PieceVisual != null, "影响力模型引用", errors);
                if (binding.PieceVisual != null)
                {
                    var pieceBounds = CalculateBoundsRelativeTo(binding.PieceVisual, binding.Renderer.transform);
                    Check(Mathf.Abs(pieceBounds.size.x - 0.7f) < 0.01f &&
                          Mathf.Abs(pieceBounds.size.y - 0.7f) < 0.01f &&
                          Mathf.Abs(pieceBounds.size.z - 0.7f) < 0.01f,
                        "影响力模型尺寸", errors);
                    Check(Mathf.Abs(pieceBounds.center.x) < 0.001f &&
                          Mathf.Abs(pieceBounds.center.y) < 0.001f,
                        "影响力模型中心对齐", errors);
                    Check(Mathf.Abs(pieceBounds.max.z + expected.Z) < 0.01f &&
                          pieceBounds.min.z + expected.Z < -0.69f,
                        "影响力模型贴合地图平面", errors);
                }
                Check(binding.Renderer.sortingOrder == 15, "影响槽 sortingOrder", errors);
                Check(binding.BorderRenderer.sortingOrder == 16, "影响槽边框 sortingOrder", errors);
                Check(Approximately(binding.Renderer.transform.localPosition.z, expected.Z), "影响槽 z", errors);
                Check(Approximately(binding.Collider.radius, expected.Radius), "影响槽 collider", errors);
                var feedback = RequireProperty(
                    new SerializedObject(binding.Renderer.GetComponent(
                        typeof(MapView).Assembly.GetType("YC.Presentation.MapPlacementFeedback"))),
                    "flashRenderer").objectReferenceValue as SpriteRenderer;
                Check(feedback != null && feedback.sortingOrder == 17, "影响槽反馈 sortingOrder", errors);
            }
            for (var i = 0; i < view.CityPool.Count; i++)
            {
                var binding = view.CityPool[i];
                Check(binding.PieceVisual != null, "移动城市模型引用", errors);
                if (binding.PieceVisual != null)
                {
                    var pieceBounds = CalculateBoundsRelativeTo(binding.PieceVisual, binding.Renderer.transform);
                    Check(Mathf.Abs(pieceBounds.size.x - 1.38f) < 0.02f &&
                          Mathf.Abs(pieceBounds.size.y - 2.376f) < 0.02f,
                        "移动城市模型投影尺寸", errors);
                    Check(Mathf.Abs(pieceBounds.center.x) < 0.001f &&
                          Mathf.Abs(pieceBounds.center.y) < 0.001f,
                        "移动城市模型中心对齐", errors);
                    Check(Mathf.Abs(pieceBounds.max.z + MobileCityZ) < 0.01f &&
                          pieceBounds.min.z + MobileCityZ < -0.63f,
                        "移动城市模型贴合地图平面", errors);
                }
                Check(binding.Renderer.sortingOrder == 20 + binding.PlayerId, "城市池 sortingOrder", errors);
                Check(binding.Collider.size == new Vector2(0.95f, 1.35f), "城市池 collider", errors);
            }
            for (var i = 0; i < view.ScoreMarkerPool.Count; i++)
            {
                var binding = view.ScoreMarkerPool[i];
                Check(binding.PieceVisual != null, "分数轨道影响力模型引用", errors);
                if (binding.PieceVisual != null)
                {
                    var pieceBounds = CalculateBoundsRelativeTo(binding.PieceVisual, binding.Renderer.transform);
                    Check(Mathf.Abs(pieceBounds.size.x - 0.7f) < 0.01f &&
                          Mathf.Abs(pieceBounds.size.y - 0.7f) < 0.01f &&
                          Mathf.Abs(pieceBounds.size.z - 0.7f) < 0.01f,
                        "分数轨道影响力模型尺寸", errors);
                    Check(Mathf.Abs(pieceBounds.center.x) < 0.001f &&
                          Mathf.Abs(pieceBounds.center.y) < 0.001f,
                        "分数轨道影响力模型中心对齐", errors);
                    Check(Mathf.Abs(pieceBounds.max.z + ScoreMarkerZ) < 0.01f &&
                          pieceBounds.min.z + ScoreMarkerZ < -0.69f,
                        "分数轨道影响力模型贴合地图平面", errors);
                }
                Check(binding.Renderer.sortingOrder == 31, "计分池 sortingOrder", errors);
                Check(binding.BorderRenderer.sortingOrder == 30, "计分池边框 sortingOrder", errors);
                Check(!binding.Renderer.enabled && !binding.BorderRenderer.enabled,
                    "分数轨道旧 Sprite 初始隐藏", errors);
            }
        }

        private static void InstallInSampleScene(GameObject prefab)
        {
            var scene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
            var rootsBefore = scene.GetRootGameObjects();
            var eventSystemsBefore = FindAllInScene<EventSystem>(scene).Length;
            if (rootsBefore.Length != 6 || eventSystemsBefore != 1)
            {
                throw new InvalidOperationException("SampleScene 接线前结构异常：要求 roots6 / EventSystem1。");
            }
            var mapRoots = rootsBefore.Where(item => item.name == "MapRoot").ToArray();
            if (mapRoots.Length != 1)
            {
                throw new InvalidOperationException("SampleScene 必须有且仅有一个 MapRoot。");
            }
            var mapRoot = mapRoots[0];
            GameObject target = null;
            for (var i = 0; i < mapRoot.transform.childCount; i++)
            {
                var child = mapRoot.transform.GetChild(i).gameObject;
                if (child.name != TargetChildName)
                {
                    throw new InvalidOperationException("MapRoot 存在未知 direct child，停止接线：" + child.name);
                }
                if (target != null)
                {
                    throw new InvalidOperationException("MapRoot 存在多个同名 MapView child。");
                }
                var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(child);
                if (!PrefabUtility.IsPartOfNonAssetPrefabInstance(child) || sourcePath != PrefabPath)
                {
                    throw new InvalidOperationException("MapRoot/MapView 是同名非目标连接，停止接线。");
                }
                target = child;
            }
            if (target == null)
            {
                target = (GameObject)PrefabUtility.InstantiatePrefab(prefab, mapRoot.transform);
                target.name = TargetChildName;
            }
            target.transform.localPosition = Vector3.zero;
            target.transform.localRotation = Quaternion.identity;
            target.transform.localScale = Vector3.one;
            var view = target.GetComponent<MapView>();
            var controller = mapRoot.GetComponent<MobileCityInteractionController>();
            var rootRenderer = mapRoot.GetComponent<SpriteRenderer>();
            var rootDisplayController = mapRoot.GetComponent<MapDisplayController>();
            var rootCoordinateSpace = mapRoot.GetComponent<MapCoordinateSpace>();
            var viewDisplayController = target.GetComponent<MapDisplayController>();
            if (view == null || controller == null || viewDisplayController == null)
            {
                throw new InvalidOperationException("MapRoot/MapView 缺少固定接线组件。");
            }
            var residualCount = (rootRenderer == null ? 0 : 1) +
                                (rootDisplayController == null ? 0 : 1) +
                                (rootCoordinateSpace == null ? 0 : 1);
            if (residualCount != 0 && residualCount != 3)
            {
                throw new InvalidOperationException("MapRoot 地图显示残留组件不完整，停止迁移。");
            }
            var mapRootComponents = mapRoot.GetComponents<Component>();
            if (mapRootComponents.Any(component => component == null))
            {
                throw new InvalidOperationException("MapRoot 存在丢失脚本，停止迁移。");
            }
            var unexpectedRootComponent = mapRootComponents.FirstOrDefault(component =>
                !(component is Transform) &&
                 !(component is MobileCityInteractionController) &&
                 !(component is SpriteRenderer) &&
                 !(component is MapDisplayController) &&
                 !(component is MapCoordinateSpace));
            if (unexpectedRootComponent != null)
            {
                throw new InvalidOperationException("MapRoot 存在未知组件，停止迁移：" + unexpectedRootComponent.GetType().FullName);
            }
            if (residualCount == 3)
            {
                Object.DestroyImmediate(rootCoordinateSpace);
                Object.DestroyImmediate(rootDisplayController);
                Object.DestroyImmediate(rootRenderer);
            }
            SetReferences(controller, ("mapRenderer", view.MapRenderer), ("mapViewBinding", view));
            EditorUtility.SetDirty(controller);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                throw new InvalidOperationException("SampleScene 保存失败。");
            }
            var rootsAfter = scene.GetRootGameObjects();
            if (rootsAfter.Length != rootsBefore.Length || !rootsBefore.All(item => rootsAfter.Contains(item)) ||
                FindAllInScene<EventSystem>(scene).Length != 1 || mapRoot.transform.childCount != 1 || scene.isDirty)
            {
                throw new InvalidOperationException("SampleScene 接线后 roots/EventSystem/dirty 状态异常。");
            }
            var installedPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target);
            if (installedPath != PrefabPath || target.GetComponent<MapView>() == null)
            {
                throw new InvalidOperationException("MapRoot 下的 MapView 未保持目标 Prefab 连接。");
            }
        }

        private static HashSet<string> BuildExpectedSlotIds(GameMapDefinition map)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var location in map.Locations)
            {
                for (var i = 0; i < location.InfluenceSlotCount; i++)
                    result.Add(InfluenceService.GetLocationSlotId(location.LocationId, i));
            }
            foreach (var route in map.Routes)
            {
                for (var i = 0; i < route.InfluenceSlotCount; i++)
                    result.Add(InfluenceService.GetRouteSlotId(route.RouteId, i));
            }
            return result;
        }

        private static Dictionary<string, SlotSetting> BuildSlotSettings(MapDisplayLayout layout)
        {
            var result = new Dictionary<string, SlotSetting>();
            foreach (var definition in layout.CreateResourcePointDefinitions())
                for (var i = 0; i < definition.InfluenceSlots.Count; i++)
                    result.Add(InfluenceService.GetLocationSlotId(definition.LocationId, i),
                        new SlotSetting(LocationSlotZ, definition.InfluenceSlots[i].ColliderRadius));
            foreach (var definition in layout.CreateRouteDefinitions())
                for (var i = 0; i < definition.InfluenceSlots.Count; i++)
                    result.Add(InfluenceService.GetRouteSlotId(definition.RouteId, i),
                        new SlotSetting(RouteSlotZ, definition.InfluenceSlots[i].ColliderRadius));
            return result;
        }

        private static void ValidateObjectReferences(Object target, List<string> errors)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.GetIterator();
            if (!property.NextVisible(true)) return;
            do
            {
                if (property.propertyType == SerializedPropertyType.ObjectReference &&
                    property.name != "m_Script" && property.objectReferenceValue == null)
                    errors.Add(target.GetType().Name + " 缺少引用 " + property.name + "。");
            } while (property.NextVisible(false));
        }

        private static void ValidatePieceAssets(GameObject root, List<string> errors)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(PieceMaterialPath);
            if (material == null)
            {
                errors.Add("缺少共享地图棋子材质。");
            }

            foreach (var visual in root.GetComponentsInChildren<MapPieceVisual>(true))
            {
                if (!visual.TryValidateConfiguration(out var reason))
                {
                    errors.Add(visual.name + " 的 MapPieceVisual 配置无效：" + reason);
                    continue;
                }
                foreach (var renderer in visual.Renderers)
                {
                    if (renderer.sharedMaterials.Length != 1 || renderer.sharedMaterial != material)
                    {
                        errors.Add(visual.name + " 未使用唯一的共享地图棋子材质。");
                    }
                    if (renderer.enabled)
                    {
                        errors.Add(visual.name + " 在 MapView Prefab 初始状态下应隐藏。");
                    }
                }
            }

            foreach (var path in new[] { InfluencePiecePrefabPath, MobileCityPiecePrefabPath })
            {
                var piecePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (piecePrefab == null || piecePrefab.GetComponent<MapPieceVisual>() == null)
                {
                    errors.Add("棋子 Prefab 缺失或没有 MapPieceVisual：" + path);
                }
                else if (piecePrefab.GetComponentsInChildren<Collider>(true).Length != 0 ||
                         piecePrefab.GetComponentsInChildren<Collider2D>(true).Length != 0)
                {
                    errors.Add("棋子 Prefab 不应包含碰撞体：" + path);
                }
            }

            var lights = root.GetComponentsInChildren<Light>(true);
            if (lights.Length == 1)
            {
                var light = lights[0];
                Check(light.type == LightType.Directional, "棋子灯光类型", errors);
                Check(Approximately(light.intensity, 0.85f), "棋子灯光强度", errors);
                Check(light.shadows == LightShadows.Soft, "棋子灯光阴影", errors);
                Check(Approximately(light.shadowStrength, 0.45f), "棋子灯光阴影强度", errors);
            }
        }

        private static Bounds CalculateBoundsRelativeTo(MapPieceVisual visual, Transform relativeRoot)
        {
            var filters = visual.GetComponentsInChildren<MeshFilter>(true);
            var initialized = false;
            var result = new Bounds();
            foreach (var filter in filters)
            {
                if (filter.sharedMesh == null)
                {
                    continue;
                }
                var meshBounds = filter.sharedMesh.bounds;
                var matrix = relativeRoot.worldToLocalMatrix * filter.transform.localToWorldMatrix;
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
            }
            return result;
        }

        private static void ValidateComponentCount<T>(GameObject root, int expected, List<string> errors)
            where T : Component
        {
            var actual = root.GetComponentsInChildren<T>(true).Length;
            if (actual != expected)
            {
                errors.Add(typeof(T).Name + " 数量为 " + actual + "，预期 " + expected + "。");
            }
        }

        private static void ValidateObjectArrayReferences(
            Object target, string propertyName, int count, string path, List<string> errors)
        {
            var property = RequireProperty(new SerializedObject(target), propertyName);
            if (property.arraySize != count)
            {
                errors.Add(propertyName + " 持久引用数量不是 " + count + "。");
                return;
            }
            for (var i = 0; i < property.arraySize; i++)
            {
                var value = property.GetArrayElementAtIndex(i).objectReferenceValue;
                if (value == null || AssetDatabase.GetAssetPath(value) != path)
                    errors.Add(propertyName + "[" + i + "] 没有持久 AssetDatabase 引用。");
            }
        }

        private static void SetReferences(Object target, params (string property, Object value)[] references)
        {
            var serialized = new SerializedObject(target);
            foreach (var reference in references) SetObject(serialized, reference.property, reference.value);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetObject(SerializedObject serialized, string name, Object value)
        {
            RequireProperty(serialized, name).objectReferenceValue = value;
        }

        private static void SetObjectArray(SerializedObject serialized, string name, List<Object> values)
        {
            var property = RequireProperty(serialized, name);
            property.arraySize = values.Count;
            for (var i = 0; i < values.Count; i++) property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        }

        private static void SetBindingArray(
            SerializedObject serialized,
            string name,
            int count,
            Action<SerializedProperty, int> write)
        {
            var property = RequireProperty(serialized, name);
            property.arraySize = count;
            for (var i = 0; i < count; i++) write(property.GetArrayElementAtIndex(i), i);
        }

        private static SerializedProperty RequireProperty(SerializedObject serialized, string name)
        {
            var property = serialized.FindProperty(name);
            if (property == null) throw new InvalidOperationException(serialized.targetObject.GetType().Name + " 缺少属性 " + name + "。");
            return property;
        }

        private static Transform CreateGroup(Transform parent, string name)
        {
            var group = new GameObject(name).transform;
            group.SetParent(parent, false);
            return group;
        }

        private static T[] FindAllInScene<T>(Scene scene) where T : Component
        {
            var result = new List<T>();
            foreach (var root in scene.GetRootGameObjects()) result.AddRange(root.GetComponentsInChildren<T>(true));
            return result.ToArray();
        }

        private static void Check(bool condition, string label, List<string> errors)
        {
            if (!condition) errors.Add(label + " 配置不符合固定地图视觉规范。");
        }

        private static bool Approximately(float left, float right) => Mathf.Abs(left - right) < 0.0001f;

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var separator = path.LastIndexOf('/');
            var parent = path.Substring(0, separator);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(separator + 1));
        }

        private sealed class LocationBuildBinding
        {
            public readonly string Id; public readonly MapHotspot Hotspot;
            public LocationBuildBinding(string id, MapHotspot hotspot) { Id = id; Hotspot = hotspot; }
        }

        private sealed class ResourceBuildBinding
        {
            public readonly string Id; public readonly SpriteRenderer Renderer;
            public ResourceBuildBinding(string id, SpriteRenderer renderer) { Id = id; Renderer = renderer; }
        }

        private sealed class SlotBuildBinding
        {
            public readonly string Id; public readonly SpriteRenderer Renderer; public readonly CircleCollider2D Collider;
            public readonly InfluenceSlotClickTarget Click; public readonly SpriteRenderer Border;
            public readonly Component Pulse; public readonly Component Feedback;
            public readonly MapPieceVisual PieceVisual;
            public SlotBuildBinding(string id, SpriteRenderer renderer, CircleCollider2D collider,
                InfluenceSlotClickTarget click, SpriteRenderer border, Component pulse, Component feedback,
                MapPieceVisual pieceVisual)
            { Id = id; Renderer = renderer; Collider = collider; Click = click; Border = border; Pulse = pulse; Feedback = feedback; PieceVisual = pieceVisual; }
        }

        private sealed class CityBuildBinding
        {
            public readonly int PlayerId; public readonly SpriteRenderer Renderer; public readonly BoxCollider2D Collider;
            public readonly MobileCityClickTarget Click;
            public readonly MapPieceVisual PieceVisual;
            public CityBuildBinding(int playerId, SpriteRenderer renderer, BoxCollider2D collider,
                MobileCityClickTarget click, MapPieceVisual pieceVisual)
            { PlayerId = playerId; Renderer = renderer; Collider = collider; Click = click; PieceVisual = pieceVisual; }
        }

        private sealed class ScoreBuildBinding
        {
            public readonly int PlayerId; public readonly SpriteRenderer Renderer; public readonly SpriteRenderer Border;
            public readonly MapPieceVisual PieceVisual;
            public ScoreBuildBinding(int playerId, SpriteRenderer renderer, SpriteRenderer border,
                MapPieceVisual pieceVisual)
            { PlayerId = playerId; Renderer = renderer; Border = border; PieceVisual = pieceVisual; }
        }

        private readonly struct SlotSetting
        {
            public readonly float Z; public readonly float Radius;
            public SlotSetting(float z, float radius) { Z = z; Radius = radius; }
        }
    }
}
