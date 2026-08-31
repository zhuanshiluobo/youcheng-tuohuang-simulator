using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Presentation;

namespace YC.EditorTools
{
    public static class CollectionRoomEditorAssetBuilder
    {
        public const string ScenePath = "Assets/Scenes/CollectionRoom.unity";
        private const string RenderTexturePath =
            "Assets/YC/Presentation/Models/Originite/OriginiteCollection.renderTexture";
        private const int ModelLayer = 31;

        private static readonly Color Background = new Color(0.025f, 0.026f, 0.03f, 1f);
        private static readonly Color Panel = new Color(0.07f, 0.072f, 0.08f, 0.96f);
        private static readonly Color Gold = new Color(0.93f, 0.65f, 0.25f, 1f);
        private static readonly Color PrimaryText = new Color(0.94f, 0.92f, 0.87f, 1f);
        private static readonly Color SecondaryText = new Color(0.59f, 0.58f, 0.55f, 1f);

        [MenuItem("Tools/YC/Rebuild Collection Room")]
        public static void Rebuild()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("Collection Room", typeof(CollectionRoomController));

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.08f, 0.045f, 0.018f);
            RenderSettings.reflectionIntensity = 0.45f;

            CreateDisplayCamera(root.transform);
            var renderTexture = EnsureRenderTexture();
            var modelPivot = LoadingSceneEditorAssetBuilder.CreateOriginiteModel(scene, root.transform);
            var previewCamera = CreatePreviewCamera(root.transform, renderTexture);
            CreateLight("Originite Key Light", root.transform, new Vector3(-2.5f, 2.8f, -3f),
                new Color(1f, 0.82f, 0.65f), 2f);
            CreateLight("Originite Rim Light", root.transform, new Vector3(2.6f, -1.2f, -2f),
                new Color(1f, 0.58f, 0.3f), 1.2f);

            var canvas = CreateCanvas(root.transform);
            CreateStretchImage("Background", canvas.transform, Background);
            CreateTopBar(canvas.transform, out var backButton);
            CreateSidebar(canvas.transform);
            var dragController = CreatePreview(canvas.transform, renderTexture, modelPivot.transform);
            CreateInfoPanel(canvas.transform, out var resetButton);
            CreateEventSystem(root.transform);

            root.GetComponent<CollectionRoomController>().ConfigureForEditor(
                modelPivot.transform,
                dragController,
                backButton,
                resetButton);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EnsureInBuildSettings();
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = modelPivot;
            Debug.Log("[CollectionRoomEditorAssetBuilder] 已重建 " + ScenePath +
                      "，预览相机：" + previewCamera.name + "。");
        }

        private static void CreateDisplayCamera(Transform parent)
        {
            var cameraObject = new GameObject("Display Camera", typeof(Camera));
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(parent, false);
            cameraObject.transform.localPosition = new Vector3(0f, 0f, -10f);
            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Background;
            camera.cullingMask = 0;
            camera.depth = -100f;
            camera.useOcclusionCulling = false;
        }

        private static Camera CreatePreviewCamera(Transform parent, RenderTexture texture)
        {
            var cameraObject = new GameObject("Collection Preview Camera", typeof(Camera));
            cameraObject.transform.SetParent(parent, false);
            cameraObject.transform.localPosition = new Vector3(0f, 0f, -5f);
            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.cullingMask = 1 << ModelLayer;
            camera.depth = -90f;
            camera.useOcclusionCulling = false;
            camera.allowHDR = true;
            camera.orthographic = true;
            camera.orthographicSize = 1.24f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            camera.targetTexture = texture;
            return camera;
        }

        private static Canvas CreateCanvas(Transform parent)
        {
            var canvasObject = new GameObject(
                "Collection Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasObject.layer = LayerMask.NameToLayer("UI");
            canvasObject.transform.SetParent(parent, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        private static void CreateTopBar(Transform parent, out Button backButton)
        {
            var bar = CreateUiObject("Top Bar", parent, typeof(Image));
            SetRect(bar.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 112f));
            bar.GetComponent<Image>().color = new Color(0.045f, 0.046f, 0.052f, 1f);

            backButton = CreateButton(bar.transform, "Back Button", "〈  返回",
                Vector2.zero, new Vector2(150f, 52f), TextAnchor.MiddleCenter);
            SetTopLeft(backButton.GetComponent<RectTransform>(),
                new Vector2(150f, 52f), new Vector2(36f, -30f));
            var title = CreateText(bar.transform, "Title", "收藏室", 38, PrimaryText, TextAnchor.MiddleLeft);
            SetTopLeft(title.rectTransform, new Vector2(300f, 64f), new Vector2(230f, -24f));

            var line = CreateUiObject("Gold Line", bar.transform, typeof(Image));
            SetRect(line.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 2f));
            line.GetComponent<Image>().color = new Color(Gold.r, Gold.g, Gold.b, 0.65f);
        }

        private static void CreateSidebar(Transform parent)
        {
            var sidebar = CreateUiObject("Collection List", parent, typeof(Image));
            SetRect(sidebar.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0.5f), new Vector2(36f, -56f), new Vector2(280f, -152f));
            sidebar.GetComponent<Image>().color = Panel;

            var caption = CreateText(sidebar.transform, "Caption", "模型藏品   01 / 01", 17,
                SecondaryText, TextAnchor.MiddleLeft);
            SetTopLeft(caption.rectTransform, new Vector2(232f, 38f), new Vector2(24f, -28f));

            var item = CreateUiObject("Originite Item", sidebar.transform, typeof(Image));
            SetTopLeft(item.GetComponent<RectTransform>(), new Vector2(232f, 94f), new Vector2(24f, -82f));
            item.GetComponent<Image>().color = new Color(0.14f, 0.11f, 0.075f, 1f);
            var marker = CreateUiObject("Selected Marker", item.transform, typeof(Image));
            SetRect(marker.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0.5f), Vector2.zero, new Vector2(4f, 0f));
            marker.GetComponent<Image>().color = Gold;

            var index = CreateText(item.transform, "Index", "01", 17, Gold, TextAnchor.MiddleCenter);
            SetTopLeft(index.rectTransform, new Vector2(46f, 50f), new Vector2(14f, -22f));
            var name = CreateText(item.transform, "Name", "至纯源石", 22, PrimaryText, TextAnchor.MiddleLeft);
            SetTopLeft(name.rectTransform, new Vector2(148f, 48f), new Vector2(66f, -13f));
            var type = CreateText(item.transform, "Type", "矿石模型", 15, SecondaryText, TextAnchor.MiddleLeft);
            SetTopLeft(type.rectTransform, new Vector2(148f, 28f), new Vector2(66f, -52f));
        }

        private static LoadingModelDragController CreatePreview(
            Transform parent,
            RenderTexture texture,
            Transform pivot)
        {
            var frame = CreateUiObject("Preview Frame", parent, typeof(Image));
            SetCentered(frame.GetComponent<RectTransform>(), new Vector2(800f, 800f), new Vector2(2f, -34f));
            frame.GetComponent<Image>().color = new Color(0.05f, 0.045f, 0.04f, 1f);

            var display = CreateUiObject(
                "Originite Preview",
                frame.transform,
                typeof(RawImage),
                typeof(LoadingModelDragController));
            SetCentered(display.GetComponent<RectTransform>(), new Vector2(760f, 760f), Vector2.zero);
            var rawImage = display.GetComponent<RawImage>();
            rawImage.texture = texture;
            rawImage.color = Color.white;
            rawImage.raycastTarget = false;
            var drag = display.GetComponent<LoadingModelDragController>();
            drag.ConfigureForEditor(pivot);

            var corner = CreateText(frame.transform, "Specimen Number", "COLLECTION  /  01", 15,
                new Color(0.72f, 0.55f, 0.28f, 0.9f), TextAnchor.MiddleLeft);
            SetTopLeft(corner.rectTransform, new Vector2(230f, 30f), new Vector2(24f, -18f));
            return drag;
        }

        private static void CreateInfoPanel(Transform parent, out Button resetButton)
        {
            var info = CreateUiObject("Model Information", parent, typeof(Image));
            SetRect(info.GetComponent<RectTransform>(), new Vector2(1f, 0f), new Vector2(1f, 1f),
                new Vector2(1f, 0.5f), new Vector2(-36f, -56f), new Vector2(340f, -152f));
            info.GetComponent<Image>().color = Panel;

            var label = CreateText(info.transform, "Category", "稀有藏品", 16, Gold, TextAnchor.MiddleLeft);
            SetTopLeft(label.rectTransform, new Vector2(270f, 32f), new Vector2(30f, -38f));
            var title = CreateText(info.transform, "Model Name", "至纯源石", 32, PrimaryText, TextAnchor.MiddleLeft);
            SetTopLeft(title.rectTransform, new Vector2(280f, 58f), new Vector2(30f, -80f));
            var latin = CreateText(info.transform, "Latin Name", "ORIGINITE PRIME", 14,
                SecondaryText, TextAnchor.MiddleLeft);
            SetTopLeft(latin.rectTransform, new Vector2(280f, 30f), new Vector2(30f, -140f));

            var separator = CreateUiObject("Separator", info.transform, typeof(Image));
            SetTopLeft(separator.GetComponent<RectTransform>(), new Vector2(280f, 1f), new Vector2(30f, -190f));
            separator.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);

            var description = CreateText(info.transform, "Description",
                "高纯度的源石结晶。复杂的内部结构折射出温暖而危险的橙金色光芒。",
                18, new Color(0.75f, 0.73f, 0.68f, 1f), TextAnchor.UpperLeft);
            description.horizontalOverflow = HorizontalWrapMode.Wrap;
            description.verticalOverflow = VerticalWrapMode.Overflow;
            description.lineSpacing = 1.25f;
            SetTopLeft(description.rectTransform, new Vector2(280f, 260f), new Vector2(30f, -222f));

            resetButton = CreateButton(info.transform, "Reset View Button", "复位视角",
                new Vector2(0f, -362f), new Vector2(280f, 54f), TextAnchor.MiddleCenter);
        }

        private static RenderTexture EnsureRenderTexture()
        {
            var texture = AssetDatabase.LoadAssetAtPath<RenderTexture>(RenderTexturePath);
            if (texture == null)
            {
                texture = new RenderTexture(1024, 1024, 24, RenderTextureFormat.ARGBHalf)
                {
                    name = "Originite Collection Render Texture",
                    antiAliasing = 4,
                    useMipMap = false,
                    autoGenerateMips = false
                };
                AssetDatabase.CreateAsset(texture, RenderTexturePath);
            }
            else
            {
                if (texture.width != 1024 || texture.height != 1024 ||
                    texture.format != RenderTextureFormat.ARGBHalf || texture.antiAliasing != 4)
                {
                    texture.Release();
                    texture.width = 1024;
                    texture.height = 1024;
                    texture.format = RenderTextureFormat.ARGBHalf;
                    texture.antiAliasing = 4;
                    EditorUtility.SetDirty(texture);
                }
            }

            return texture;
        }

        private static void CreateLight(
            string name,
            Transform parent,
            Vector3 position,
            Color color,
            float intensity)
        {
            var lightObject = new GameObject(name, typeof(Light));
            lightObject.transform.SetParent(parent, false);
            lightObject.transform.localPosition = position;
            var light = lightObject.GetComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = 10f;
            light.cullingMask = 1 << ModelLayer;
        }

        private static void CreateEventSystem(Transform parent)
        {
            var eventSystem = new GameObject(
                "Event System",
                typeof(EventSystem),
                typeof(StandaloneInputModule));
            eventSystem.transform.SetParent(parent, false);
        }

        private static GameObject CreateStretchImage(string name, Transform parent, Color color)
        {
            var imageObject = CreateUiObject(name, parent, typeof(Image));
            var rect = imageObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            imageObject.GetComponent<Image>().color = color;
            return imageObject;
        }

        private static Button CreateButton(
            Transform parent,
            string name,
            string label,
            Vector2 position,
            Vector2 size,
            TextAnchor alignment)
        {
            var buttonObject = CreateUiObject(name, parent, typeof(Image), typeof(Button));
            SetCentered(buttonObject.GetComponent<RectTransform>(), size, position);
            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.12f, 0.1f, 0.075f, 1f);
            var button = buttonObject.GetComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = new Color(0.12f, 0.1f, 0.075f, 1f);
            colors.highlightedColor = new Color(0.22f, 0.16f, 0.09f, 1f);
            colors.pressedColor = new Color(0.32f, 0.2f, 0.08f, 1f);
            button.colors = colors;
            var text = CreateText(buttonObject.transform, "Label", label, 18, PrimaryText, alignment);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = Vector2.zero;
            text.rectTransform.offsetMax = Vector2.zero;
            return button;
        }

        private static Text CreateText(
            Transform parent,
            string name,
            string value,
            int fontSize,
            Color color,
            TextAnchor alignment)
        {
            var textObject = CreateUiObject(name, parent, typeof(Text));
            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.font = UiEditorAssetReferences.CjkFont;
            text.fontSize = fontSize;
            text.fontStyle = FontStyle.Normal;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            return text;
        }

        private static GameObject CreateUiObject(string name, Transform parent, params System.Type[] components)
        {
            var types = new System.Type[components.Length + 1];
            types[0] = typeof(RectTransform);
            System.Array.Copy(components, 0, types, 1, components.Length);
            var gameObject = new GameObject(name, types);
            gameObject.layer = LayerMask.NameToLayer("UI");
            gameObject.transform.SetParent(parent, false);
            return gameObject;
        }

        private static void SetCentered(RectTransform rect, Vector2 size, Vector2 position)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void SetTopLeft(RectTransform rect, Vector2 size, Vector2 position)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void SetRect(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 position,
            Vector2 size)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static void EnsureInBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!scenes.Any(entry => entry.path == ScenePath))
            {
                scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = scenes.ToArray();
            }
        }
    }
}
