using System;
using YC.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YC.EditorTools
{
    public static class ViewerEditorAssetBuilder
    {
        public const string FolderPath = "Assets/YC/Presentation/Prefabs/Viewers";
        public const string ZoomablePrefabPath = FolderPath + "/ZoomableImageViewer.prefab";
        public const string RulebookPrefabPath = FolderPath + "/RulebookViewer.prefab";
        public const string ActionLogPrefabPath = FolderPath + "/ActionLogViewer.prefab";

        [MenuItem("Tools/YC/Rebuild Viewer Editor Assets")]
        public static void Rebuild()
        {
            YC.Editor.UiThemeBuildReadiness.InitializeRequiredTheme();
            RebuildViewerPrefabs();
            GameSettingsMenuEditorAssetBuilder.Rebuild();
        }

        public static void RebuildViewerPrefabs()
        {
            YC.Editor.UiThemeBuildReadiness.InitializeRequiredTheme();
            EnsureFolder(FolderPath);
            var zoomablePrefab = BuildZoomablePrefab();
            BuildRulebookPrefab(zoomablePrefab);
            BuildActionLogPrefab();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[ViewerEditorAssetBuilder] Rebuilt Zoomable, Rulebook and ActionLog prefabs.");
        }

        private static GameObject BuildZoomablePrefab()
        {
            var root = new GameObject("Zoomable Image Viewer");
            try
            {
                var canvasObject = CreateCanvas("Zoomable Image Viewer Canvas", root.transform, 130);
                var view = canvasObject.AddComponent<ZoomableImageViewerView>();
                var overlay = CreateUiObject("Image Viewer", canvasObject.transform, typeof(Image));
                Stretch(overlay.GetComponent<RectTransform>());
                var rootBackground = overlay.GetComponent<Image>();
                rootBackground.color = new Color(0f, 0f, 0f, 0.74f);

                var panelObject = CreateUiObject("Image Panel", overlay.transform, typeof(Image), typeof(Outline));
                var panel = panelObject.GetComponent<RectTransform>();
                SetRect(panel, new Vector2(1200f, 860f), Vector2.zero, Anchor.Center);
                panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
                panelObject.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
                panelObject.GetComponent<Outline>().effectDistance = new Vector2(3f, -3f);

                var expanded = CreateUiObject("Image Expanded Content", panel, null);
                Stretch(expanded.GetComponent<RectTransform>());
                var title = CreateText(
                    expanded.transform,
                    "Image Title",
                    string.Empty,
                    34,
                    TextAnchor.MiddleCenter,
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                    new Vector2(0f, -64f),
                    Vector2.zero,
                    FontStyle.Bold,
                    true);
                var close = CreateButton(
                    expanded.transform,
                    "Close Image Button",
                    "×",
                    new Vector2(-18f, -12f),
                    new Vector2(42f, 42f),
                    Anchor.TopRight,
                    30);

                var viewportObject = CreateUiObject(
                    "Image Viewport",
                    expanded.transform,
                    typeof(Image),
                    typeof(Mask),
                    typeof(ScrollRect));
                var viewport = viewportObject.GetComponent<RectTransform>();
                viewport.anchorMin = Vector2.zero;
                viewport.anchorMax = Vector2.one;
                viewport.offsetMin = new Vector2(96f, 82f);
                viewport.offsetMax = new Vector2(-96f, -74f);
                viewportObject.GetComponent<Image>().color = new Color(0.025f, 0.022f, 0.02f, 0.96f);
                viewportObject.GetComponent<Mask>().showMaskGraphic = true;

                var imageObject = CreateUiObject("Image Image", viewport, typeof(RawImage));
                var imageTransform = imageObject.GetComponent<RectTransform>();
                SetRect(imageTransform, new Vector2(900f, 700f), Vector2.zero, Anchor.Center);
                var rawImage = imageObject.GetComponent<RawImage>();
                rawImage.color = Color.white;
                var scroll = viewportObject.GetComponent<ScrollRect>();
                scroll.viewport = viewport;
                scroll.content = imageTransform;
                scroll.horizontal = true;
                scroll.vertical = true;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.inertia = true;
                scroll.scrollSensitivity = 0f;

                var pageLabel = CreateText(
                    expanded.transform,
                    "Image Page Label",
                    "1 / 1",
                    20,
                    TextAnchor.MiddleCenter,
                    new Vector2(0.5f, 0f),
                    new Vector2(0.5f, 0f),
                    new Vector2(-260f, 22f),
                    new Vector2(260f, 66f),
                    FontStyle.Bold,
                    true);
                var previous = CreateButton(
                    expanded.transform,
                    "Previous Image Button",
                    "<",
                    new Vector2(28f, 0f),
                    new Vector2(58f, 118f),
                    Anchor.MiddleLeft,
                    34);
                var next = CreateButton(
                    expanded.transform,
                    "Next Image Button",
                    ">",
                    new Vector2(-28f, 0f),
                    new Vector2(58f, 118f),
                    Anchor.MiddleRight,
                    34);
                var primary = CreateButton(
                    expanded.transform,
                    "Primary Image Action Button",
                    string.Empty,
                    new Vector2(-112f, 18f),
                    new Vector2(208f, 48f),
                    Anchor.BottomCenter,
                    20);
                var secondary = CreateButton(
                    expanded.transform,
                    "Secondary Image Action Button",
                    string.Empty,
                    new Vector2(112f, 18f),
                    new Vector2(208f, 48f),
                    Anchor.BottomCenter,
                    20);

                var collapsedSummary = CreateText(
                    panel,
                    "Image Collapsed Summary",
                    string.Empty,
                    18,
                    TextAnchor.MiddleLeft,
                    new Vector2(0f, 0.5f),
                    new Vector2(1f, 0.5f),
                    new Vector2(12f, -21f),
                    new Vector2(-148f, 21f),
                    FontStyle.Bold,
                    true);
                var collapse = CreateButton(
                    panel,
                    "Image Collapse Toggle Button",
                    "▲ 收起卡牌",
                    new Vector2(0f, 18f),
                    new Vector2(208f, 48f),
                    Anchor.BottomCenter,
                    18);

                primary.gameObject.SetActive(false);
                secondary.gameObject.SetActive(false);
                collapse.gameObject.SetActive(false);
                collapsedSummary.gameObject.SetActive(false);
                overlay.SetActive(false);

                SetReferences(
                    view,
                    ("canvasObject", canvasObject),
                    ("rootObject", overlay),
                    ("rootBackgroundImage", rootBackground),
                    ("panelTransform", panel),
                    ("expandedContentObject", expanded),
                    ("viewportTransform", viewport),
                    ("imageTransform", imageTransform),
                    ("image", rawImage),
                    ("titleText", title),
                    ("pageLabel", pageLabel),
                    ("closeButton", close),
                    ("previousButton", previous),
                    ("nextButton", next),
                    ("primaryActionButton", primary),
                    ("primaryActionLabel", primary.GetComponentInChildren<Text>(true)),
                    ("secondaryActionButton", secondary),
                    ("secondaryActionLabel", secondary.GetComponentInChildren<Text>(true)),
                    ("collapseToggleButton", collapse),
                    ("collapseToggleLabel", collapse.GetComponentInChildren<Text>(true)),
                    ("collapsedSummaryText", collapsedSummary));

                var controller = root.AddComponent<ZoomableImageViewerController>();
                SetReferences(controller, ("view", view));
                return SavePrefab(root, ZoomablePrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void BuildRulebookPrefab(GameObject zoomablePrefab)
        {
            var root = new GameObject("Rulebook Viewer");
            try
            {
                var nested = (GameObject)PrefabUtility.InstantiatePrefab(zoomablePrefab);
                nested.name = "Rulebook Image Viewer";
                nested.transform.SetParent(root.transform, false);
                var imageViewer = nested.GetComponent<ZoomableImageViewerController>();
                var view = root.AddComponent<RulebookViewerView>();
                var viewData = new SerializedObject(view);
                viewData.FindProperty("imageViewer").objectReferenceValue = imageViewer;
                var pages = viewData.FindProperty("pages");
                pages.arraySize = 28;
                for (var i = 0; i < pages.arraySize; i++)
                {
                    var path = "Assets/Resources/RulebookPages/page_" + (i + 1).ToString("00") + ".jpg";
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (texture == null)
                    {
                        throw new InvalidOperationException("Missing rulebook page asset: " + path);
                    }

                    pages.GetArrayElementAtIndex(i).objectReferenceValue = texture;
                }
                viewData.ApplyModifiedPropertiesWithoutUndo();

                var controller = root.AddComponent<RulebookViewerController>();
                SetReferences(controller, ("view", view));
                SavePrefab(root, RulebookPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void BuildActionLogPrefab()
        {
            var root = new GameObject("Action Log Viewer");
            try
            {
                var canvasObject = CreateCanvas("Action Log Viewer Canvas", root.transform, 125);
                var view = canvasObject.AddComponent<ActionLogViewerView>();
                var overlay = CreateUiObject(
                    "Action Log Overlay",
                    canvasObject.transform,
                    typeof(Image),
                    typeof(Button));
                Stretch(overlay.GetComponent<RectTransform>());
                overlay.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.65f);

                var panelObject = CreateUiObject("Action Log Panel", overlay.transform, typeof(Image), typeof(Outline));
                var panel = panelObject.GetComponent<RectTransform>();
                SetRect(panel, new Vector2(864f, 680f), Vector2.zero, Anchor.Center);
                panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
                panelObject.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
                CreateText(
                    panel,
                    "Action Log Title",
                    "对局行动日志",
                    30,
                    TextAnchor.MiddleCenter,
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                    new Vector2(0f, -64f),
                    Vector2.zero,
                    FontStyle.Bold,
                    false);
                var close = CreateButton(
                    panel,
                    "Close Action Log Button",
                    "×",
                    new Vector2(-14f, -14f),
                    new Vector2(46f, 36f),
                    Anchor.TopRight,
                    24);

                var scrollObject = CreateUiObject(
                    "Action Log Scroll View",
                    panel,
                    typeof(Image),
                    typeof(ScrollRect));
                var scrollRectTransform = scrollObject.GetComponent<RectTransform>();
                scrollRectTransform.anchorMin = Vector2.zero;
                scrollRectTransform.anchorMax = Vector2.one;
                scrollRectTransform.offsetMin = new Vector2(26f, 24f);
                scrollRectTransform.offsetMax = new Vector2(-26f, -76f);
                scrollObject.GetComponent<Image>().color = UiTheme.ScrollBackground;

                var viewportObject = CreateUiObject(
                    "Action Log Viewport",
                    scrollObject.transform,
                    typeof(Image),
                    typeof(RectMask2D));
                var viewport = viewportObject.GetComponent<RectTransform>();
                viewport.anchorMin = Vector2.zero;
                viewport.anchorMax = Vector2.one;
                viewport.offsetMin = new Vector2(8f, 8f);
                viewport.offsetMax = new Vector2(-38f, -8f);
                viewportObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);

                var scrollbarObject = CreateUiObject(
                    "Action Log Scrollbar",
                    scrollObject.transform,
                    typeof(Image),
                    typeof(Scrollbar));
                var scrollbarRect = scrollbarObject.GetComponent<RectTransform>();
                scrollbarRect.anchorMin = new Vector2(1f, 0f);
                scrollbarRect.anchorMax = new Vector2(1f, 1f);
                scrollbarRect.offsetMin = new Vector2(-30f, 8f);
                scrollbarRect.offsetMax = new Vector2(-8f, -8f);
                scrollbarObject.GetComponent<Image>().color = new Color(0.08f, 0.07f, 0.04f, 0.9f);
                var slidingArea = CreateUiObject("Sliding Area", scrollbarRect, null);
                Stretch(slidingArea.GetComponent<RectTransform>(), 3f);
                var handleObject = CreateUiObject("Handle", slidingArea.transform, typeof(Image));
                Stretch(handleObject.GetComponent<RectTransform>());
                var handleImage = handleObject.GetComponent<Image>();
                handleImage.color = UiTheme.GoldOutline;
                var scrollbar = scrollbarObject.GetComponent<Scrollbar>();
                scrollbar.handleRect = handleObject.GetComponent<RectTransform>();
                scrollbar.targetGraphic = handleImage;
                scrollbar.direction = Scrollbar.Direction.BottomToTop;
                scrollbar.value = 1f;

                var contentObject = CreateUiObject(
                    "Action Log Content",
                    viewport,
                    typeof(VerticalLayoutGroup),
                    typeof(ContentSizeFitter));
                var content = contentObject.GetComponent<RectTransform>();
                content.anchorMin = new Vector2(0f, 1f);
                content.anchorMax = new Vector2(1f, 1f);
                content.pivot = new Vector2(0.5f, 1f);
                content.sizeDelta = Vector2.zero;
                var layout = contentObject.GetComponent<VerticalLayoutGroup>();
                layout.spacing = 10f;
                layout.padding = new RectOffset(8, 8, 8, 8);
                layout.childControlHeight = true;
                layout.childForceExpandHeight = false;
                contentObject.GetComponent<ContentSizeFitter>().verticalFit =
                    ContentSizeFitter.FitMode.PreferredSize;

                var rowObject = CreateUiObject(
                    "Action Log Row Template",
                    content,
                    typeof(Image),
                    typeof(LayoutElement),
                    typeof(ActionLogRowView));
                var rowImage = rowObject.GetComponent<Image>();
                var rowLayout = rowObject.GetComponent<LayoutElement>();
                rowLayout.preferredHeight = 58f;
                var rowText = CreateText(
                    rowObject.transform,
                    "Action Log Row Text",
                    string.Empty,
                    20,
                    TextAnchor.MiddleLeft,
                    Vector2.zero,
                    Vector2.one,
                    new Vector2(16f, 6f),
                    new Vector2(-16f, -6f),
                    FontStyle.Normal,
                    false);
                rowText.resizeTextForBestFit = true;
                rowText.resizeTextMinSize = 14;
                rowText.resizeTextMaxSize = 20;
                rowText.verticalOverflow = VerticalWrapMode.Truncate;
                var rowView = rowObject.GetComponent<ActionLogRowView>();
                SetReferences(rowView, ("background", rowImage), ("layout", rowLayout), ("label", rowText));
                rowObject.SetActive(false);

                var scroll = scrollObject.GetComponent<ScrollRect>();
                scroll.viewport = viewport;
                scroll.content = content;
                scroll.horizontal = false;
                scroll.vertical = true;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.scrollSensitivity = 60f;
                scroll.verticalScrollbar = scrollbar;
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
                scroll.verticalScrollbarSpacing = 8f;

                SetReferences(
                    view,
                    ("overlayObject", overlay),
                    ("contentTransform", content),
                    ("overlayCloseButton", overlay.GetComponent<Button>()),
                    ("closeButton", close),
                    ("rowTemplate", rowView));
                var controller = root.AddComponent<ActionLogViewerController>();
                SetReferences(controller, ("view", view));
                overlay.SetActive(false);
                SavePrefab(root, ActionLogPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static GameObject CreateCanvas(string name, Transform parent, int sortingOrder)
        {
            var canvasObject = CreateUiObject(
                name,
                parent,
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = UiTheme.CanvasReferenceResolution;
            scaler.matchWidthOrHeight = UiTheme.CanvasMatchWidthOrHeight;
            return canvasObject;
        }

        private static GameObject CreateUiObject(string name, Transform parent, params Type[] components)
        {
            components = components ?? new Type[0];
            var types = new Type[components.Length + 1];
            types[0] = typeof(RectTransform);
            Array.Copy(components, 0, types, 1, components.Length);
            var gameObject = new GameObject(name, types);
            gameObject.layer = LayerMask.NameToLayer("UI");
            gameObject.transform.SetParent(parent, false);
            return gameObject;
        }

        private static Text CreateText(
            Transform parent,
            string name,
            string content,
            int fontSize,
            TextAnchor alignment,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax,
            FontStyle style,
            bool outline)
        {
            var textObject = outline
                ? CreateUiObject(name, parent, typeof(Text), typeof(Outline))
                : CreateUiObject(name, parent, typeof(Text));
            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            var text = textObject.GetComponent<Text>();
            text.text = content;
            text.font = UiEditorAssetReferences.CjkFont;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = UiTheme.GoldText;
            text.alignment = alignment;
            if (outline)
            {
                textObject.GetComponent<Outline>().effectColor = UiTheme.DarkShadowLight;
            }
            return text;
        }

        private static Button CreateButton(
            Transform parent,
            string name,
            string label,
            Vector2 position,
            Vector2 size,
            Anchor anchor,
            int fontSize)
        {
            var buttonObject = CreateUiObject(name, parent, typeof(Image), typeof(Button), typeof(Outline));
            SetRect(buttonObject.GetComponent<RectTransform>(), size, position, anchor);
            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            buttonObject.GetComponent<Outline>().effectColor = UiTheme.GoldOutlineThin;
            buttonObject.GetComponent<Outline>().effectDistance = new Vector2(2f, -2f);
            var text = CreateText(
                buttonObject.transform,
                "Label",
                label,
                fontSize,
                TextAnchor.MiddleCenter,
                Vector2.zero,
                Vector2.one,
                new Vector2(8f, 0f),
                new Vector2(-8f, 0f),
                FontStyle.Bold,
                true);
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 12;
            text.resizeTextMaxSize = fontSize;
            text.raycastTarget = false;
            return buttonObject.GetComponent<Button>();
        }

        private static void SetRect(RectTransform rect, Vector2 size, Vector2 position, Anchor anchor)
        {
            switch (anchor)
            {
                case Anchor.TopRight:
                    rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
                    break;
                case Anchor.MiddleLeft:
                    rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 0.5f);
                    break;
                case Anchor.MiddleRight:
                    rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 0.5f);
                    break;
                case Anchor.BottomCenter:
                    rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0f);
                    break;
                default:
                    rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                    break;
            }

            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void Stretch(RectTransform rect, float inset = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        private static void SetReferences(Object target, params (string property, Object value)[] references)
        {
            var serialized = new SerializedObject(target);
            for (var i = 0; i < references.Length; i++)
            {
                var property = serialized.FindProperty(references[i].property);
                if (property == null)
                {
                    throw new InvalidOperationException(
                        target.GetType().Name + " missing property " + references[i].property + ".");
                }
                property.objectReferenceValue = references[i].value;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static GameObject SavePrefab(GameObject root, string path)
        {
            PrefabUtility.SaveAsPrefabAsset(root, path);
            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            var separator = path.LastIndexOf('/');
            var parent = path.Substring(0, separator);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(separator + 1));
        }

        private enum Anchor
        {
            Center,
            TopRight,
            MiddleLeft,
            MiddleRight,
            BottomCenter
        }
    }
}
