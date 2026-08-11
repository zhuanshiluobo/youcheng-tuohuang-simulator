using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation;

namespace YC.EditorTools
{
    public static class BuildInfoPanelEditorAssetBuilder
    {
        public const string PrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/BuildInfoPanel.prefab";

        private const string CityBoardTexturePath =
            "Assets/YC/Presentation/Resources/CardImages/Boards/city_board.png";

        private static readonly Color ExternalAreaBackground = new Color32(57, 47, 26, 255);
        private static readonly Color CardBackground = new Color(0.09f, 0.07f, 0.045f, 0.72f);
        private static readonly Color CardOutline = new Color(0.78f, 0.63f, 0.38f, 0.72f);
        private static readonly Color Invisible = new Color(1f, 1f, 1f, 0f);

        public static void Rebuild()
        {
            YC.Editor.UiThemeBuildReadiness.InitializeRequiredTheme();
            EnsureFolder("Assets/YC/Presentation/Prefabs/Gameplay");
            var root = BuildPrefabContents();
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        internal static GameObject BuildPrefabContents()
        {
            var cardBoardVisualLayout =
                YC.Editor.SpatialLayoutEditorAssetBuilder.LoadRequiredCardBoardLayout();
            var cityBoardTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(CityBoardTexturePath);
            if (cityBoardTexture == null)
            {
                throw new InvalidOperationException("建设面板城市底图资源不存在：" + CityBoardTexturePath);
            }

            var markerSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            if (markerSprite == null)
            {
                throw new InvalidOperationException("无法取得 Unity 内置 UI Sprite，不能构建设施影响力标记模板。");
            }

            var root = CreateUiObject(
                "Build Info Panel",
                null,
                typeof(BuildInfoPanel),
                typeof(BuildInfoPanelView));
            Stretch(root.GetComponent<RectTransform>());

            var externalFacilityArea = BuildExternalArea(
                root.transform,
                "External Facility Supply Area",
                new Vector2(72f, -17f),
                new Vector2(365f, 350f));
            var externalCityStyleArea = BuildExternalArea(
                root.transform,
                "External City Style Area",
                new Vector2(72f, -367f),
                new Vector2(365f, 330f));

            var panel = CreateUiObject("Build Sidebar Panel", root.transform);
            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 0f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.sizeDelta = new Vector2(365f, -697f);
            panelRect.anchoredPosition = new Vector2(72f, -697f);

            var contentArea = CreateUiObject("Content Area", panel.transform);
            Stretch(contentArea.GetComponent<RectTransform>());
            var content = CreateUiObject("Content", contentArea.transform);
            Stretch(content.GetComponent<RectTransform>());

            var cityBoard = CreateUiObject("City Board", content.transform);
            Stretch(cityBoard.GetComponent<RectTransform>());
            var cityBoardImageObject = CreateUiObject(
                "城市面板底图",
                cityBoard.transform,
                typeof(RawImage),
                typeof(AspectRatioFitter));
            var cityBoardImageRect = cityBoardImageObject.GetComponent<RectTransform>();
            cityBoardImageRect.anchorMin = new Vector2(0.5f, 0.5f);
            cityBoardImageRect.anchorMax = new Vector2(0.5f, 0.5f);
            cityBoardImageRect.pivot = new Vector2(0.5f, 0.5f);
            cityBoardImageRect.anchoredPosition = Vector2.zero;
            var cityBoardImage = cityBoardImageObject.GetComponent<RawImage>();
            cityBoardImage.texture = cityBoardTexture;
            cityBoardImage.color = Color.white;
            cityBoardImage.raycastTarget = false;
            var aspectRatio = cityBoardImageObject.GetComponent<AspectRatioFitter>();
            aspectRatio.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            aspectRatio.aspectRatio = (float)cityBoardTexture.width / cityBoardTexture.height;

            var externalSlots = new BuildInfoSlotView[6];
            for (var i = 0; i < externalSlots.Length; i++)
            {
                externalSlots[i] = BuildExternalFacilitySlot(externalFacilityArea, i);
            }

            var availability = CreateText(
                externalFacilityArea,
                "Build Availability Message",
                string.Empty,
                15,
                FontStyle.Bold,
                new Color(1f, 0.22f, 0.18f, 1f),
                TextAnchor.MiddleCenter);
            var availabilityRect = availability.rectTransform;
            availabilityRect.anchorMin = new Vector2(0f, 0f);
            availabilityRect.anchorMax = new Vector2(1f, 0f);
            availabilityRect.pivot = new Vector2(0.5f, 0f);
            availabilityRect.sizeDelta = new Vector2(-12f, 32f);
            availabilityRect.anchoredPosition = new Vector2(0f, 4f);
            availability.gameObject.SetActive(false);

            var cityBoardSlots = new BuildInfoSlotView[12];
            for (var i = 0; i < cityBoardSlots.Length; i++)
            {
                cityBoardSlots[i] = BuildCityBoardSlot(
                    cityBoardImageRect,
                    cardBoardVisualLayout,
                    i);
            }

            var templates = CreateUiObject("Templates", root.transform);
            Stretch(templates.GetComponent<RectTransform>());
            var cardContentTemplate = BuildCardContentTemplate(templates.transform);
            var cityStyleCardTemplate = BuildCityStyleCardTemplate(templates.transform);
            var influenceMarkerTemplate = BuildInfluenceMarkerTemplate(templates.transform, markerSprite);
            var dragGhostTemplate = BuildDragGhostTemplate(templates.transform);
            var pendingBuildGhostTemplate = BuildPendingBuildGhostTemplate(templates.transform);

            var controller = root.GetComponent<BuildInfoPanel>();
            var view = root.GetComponent<BuildInfoPanelView>();
            SetReferences(
                controller,
                ("view", view),
                ("cardBoardVisualLayout", cardBoardVisualLayout));
            SetReferences(
                view,
                ("controller", controller),
                ("root", root.GetComponent<RectTransform>()),
                ("panelTransform", panelRect),
                ("contentArea", contentArea.GetComponent<RectTransform>()),
                ("contentRoot", content.GetComponent<RectTransform>()),
                ("externalFacilityArea", externalFacilityArea),
                ("externalCityStyleArea", externalCityStyleArea),
                ("buildAvailabilityText", availability),
                ("cityBoardRoot", cityBoard.GetComponent<RectTransform>()),
                ("cityBoardImage", cityBoardImage),
                ("cardContentTemplate", cardContentTemplate),
                ("cityStyleCardTemplate", cityStyleCardTemplate),
                ("influenceMarkerTemplate", influenceMarkerTemplate),
                ("dragGhostTemplate", dragGhostTemplate),
                ("pendingBuildGhostTemplate", pendingBuildGhostTemplate));
            SetObjectArray(view, "externalFacilitySlots", externalSlots);
            SetObjectArray(view, "cityBoardSlots", cityBoardSlots);
            return root;
        }

        private static RectTransform BuildExternalArea(
            Transform parent,
            string name,
            Vector2 anchoredPosition,
            Vector2 size)
        {
            var area = CreateUiObject(name, parent, typeof(Image));
            var rect = area.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;
            area.GetComponent<Image>().color = ExternalAreaBackground;
            area.GetComponent<Image>().raycastTarget = false;
            return rect;
        }

        private static BuildInfoSlotView BuildExternalFacilitySlot(RectTransform parent, int index)
        {
            var slot = CreateUiObject(
                "BuildSlot_" + (index + 1),
                parent,
                typeof(Image),
                typeof(Button),
                typeof(Outline),
                typeof(CardPointerInteraction),
                typeof(BuildInfoSlotView));
            SetExternalCardRect(slot.GetComponent<RectTransform>(), index, 3, 99f, 141f, 25f, 22f, 6f);
            var background = slot.GetComponent<Image>();
            background.color = CardBackground;
            var outline = slot.GetComponent<Outline>();
            outline.effectColor = CardOutline;
            outline.effectDistance = new Vector2(2f, -2f);

            var contentRoot = CreateUiObject("ContentRoot", slot.transform);
            Stretch(contentRoot.GetComponent<RectTransform>());
            var emptyLabel = CreateText(
                slot.GetComponent<RectTransform>(),
                "Fallback Label",
                "空卡位",
                12,
                FontStyle.Bold,
                UiTheme.ValueText,
                TextAnchor.MiddleCenter);
            emptyLabel.resizeTextForBestFit = true;
            emptyLabel.resizeTextMinSize = 8;
            emptyLabel.resizeTextMaxSize = 12;

            var slotView = slot.GetComponent<BuildInfoSlotView>();
            SetReferences(
                slotView,
                ("kind", (int)BuildInfoSlotKind.ExternalFacility),
                ("slotIndex", index),
                ("root", slot.GetComponent<RectTransform>()),
                ("background", background),
                ("button", slot.GetComponent<Button>()),
                ("outline", outline),
                ("contentRoot", contentRoot.GetComponent<RectTransform>()),
                ("emptyLabel", emptyLabel),
                ("pointerInteraction", slot.GetComponent<CardPointerInteraction>()));
            return slotView;
        }

        private static BuildInfoSlotView BuildCityBoardSlot(
            RectTransform parent,
            CardBoardVisualLayout layout,
            int index)
        {
            var slot = CreateUiObject(
                "槽位 " + (index + 1),
                parent,
                typeof(Image),
                typeof(Button),
                typeof(Outline),
                typeof(CityBoardSlotDropTarget),
                typeof(CardPointerInteraction),
                typeof(BuildInfoSlotView));
            ApplyCityBoardSlotLayout(slot.GetComponent<RectTransform>(), layout, index);
            var background = slot.GetComponent<Image>();
            background.color = Invisible;
            var outline = slot.GetComponent<Outline>();
            outline.effectColor = Invisible;
            outline.effectDistance = Vector2.zero;

            var contentRoot = CreateUiObject("ContentRoot", slot.transform);
            Stretch(contentRoot.GetComponent<RectTransform>());
            var emptyLabel = CreateText(
                slot.GetComponent<RectTransform>(),
                "Empty Label",
                "空卡位",
                14,
                FontStyle.Bold,
                UiTheme.ValueText,
                TextAnchor.MiddleCenter);
            emptyLabel.resizeTextForBestFit = true;
            emptyLabel.resizeTextMinSize = 10;
            emptyLabel.resizeTextMaxSize = 14;
            emptyLabel.gameObject.SetActive(false);

            var slotView = slot.GetComponent<BuildInfoSlotView>();
            SetReferences(
                slotView,
                ("kind", (int)BuildInfoSlotKind.CityBoard),
                ("slotIndex", index),
                ("root", slot.GetComponent<RectTransform>()),
                ("background", background),
                ("button", slot.GetComponent<Button>()),
                ("outline", outline),
                ("contentRoot", contentRoot.GetComponent<RectTransform>()),
                ("emptyLabel", emptyLabel),
                ("dropTarget", slot.GetComponent<CityBoardSlotDropTarget>()),
                ("pointerInteraction", slot.GetComponent<CardPointerInteraction>()));
            return slotView;
        }

        private static BuildInfoItemView BuildCardContentTemplate(Transform parent)
        {
            var root = CreateUiObject("Card Content Template", parent, typeof(BuildInfoItemView));
            Stretch(root.GetComponent<RectTransform>());
            var raw = CreateUiObject("Card Image", root.transform, typeof(RawImage));
            Stretch(raw.GetComponent<RectTransform>(), 3f);
            raw.GetComponent<RawImage>().raycastTarget = false;
            var fallback = CreateText(
                root.GetComponent<RectTransform>(),
                "Fallback Label",
                string.Empty,
                12,
                FontStyle.Bold,
                UiTheme.ValueText,
                TextAnchor.MiddleCenter);
            fallback.resizeTextForBestFit = true;
            fallback.resizeTextMinSize = 8;
            fallback.resizeTextMaxSize = 14;
            var item = root.GetComponent<BuildInfoItemView>();
            SetReferences(
                item,
                ("kind", (int)BuildInfoItemKind.CardContent),
                ("root", root.GetComponent<RectTransform>()),
                ("rawImage", raw.GetComponent<RawImage>()),
                ("fallbackText", fallback));
            root.SetActive(false);
            return item;
        }

        private static BuildInfoItemView BuildCityStyleCardTemplate(Transform parent)
        {
            var root = CreateUiObject(
                "City Style Card Template",
                parent,
                typeof(Image),
                typeof(Button),
                typeof(Outline),
                typeof(CardPointerInteraction),
                typeof(BuildInfoItemView));
            root.GetComponent<Image>().color = CardBackground;
            var outline = root.GetComponent<Outline>();
            outline.effectColor = CardOutline;
            outline.effectDistance = new Vector2(2f, -2f);
            var raw = CreateUiObject("Card Image", root.transform, typeof(RawImage));
            Stretch(raw.GetComponent<RectTransform>(), 3f);
            raw.GetComponent<RawImage>().raycastTarget = false;
            var fallback = CreateText(
                root.GetComponent<RectTransform>(),
                "Fallback Label",
                string.Empty,
                12,
                FontStyle.Bold,
                UiTheme.ValueText,
                TextAnchor.MiddleCenter);
            fallback.resizeTextForBestFit = true;
            fallback.resizeTextMinSize = 8;
            fallback.resizeTextMaxSize = 12;
            var markerRoot = CreateUiObject("MarkerRoot", root.transform);
            Stretch(markerRoot.GetComponent<RectTransform>());
            var item = root.GetComponent<BuildInfoItemView>();
            SetReferences(
                item,
                ("kind", (int)BuildInfoItemKind.CityStyleCard),
                ("root", root.GetComponent<RectTransform>()),
                ("background", root.GetComponent<Image>()),
                ("button", root.GetComponent<Button>()),
                ("outline", outline),
                ("rawImage", raw.GetComponent<RawImage>()),
                ("fallbackText", fallback),
                ("markerRoot", markerRoot.GetComponent<RectTransform>()),
                ("pointerInteraction", root.GetComponent<CardPointerInteraction>()));
            root.SetActive(false);
            return item;
        }

        private static BuildInfoItemView BuildInfluenceMarkerTemplate(Transform parent, Sprite markerSprite)
        {
            var root = CreateUiObject(
                "Influence Marker Template",
                parent,
                typeof(Image),
                typeof(Outline),
                typeof(BuildInfoItemView));
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(12f, 12f);
            var image = root.GetComponent<Image>();
            image.sprite = markerSprite;
            image.type = Image.Type.Sliced;
            image.raycastTarget = false;
            var outline = root.GetComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1f, -1f);
            var item = root.GetComponent<BuildInfoItemView>();
            SetReferences(
                item,
                ("kind", (int)BuildInfoItemKind.InfluenceMarker),
                ("root", root.GetComponent<RectTransform>()),
                ("background", image),
                ("outline", outline));
            root.SetActive(false);
            return item;
        }

        private static BuildInfoItemView BuildDragGhostTemplate(Transform parent)
        {
            var root = CreateUiObject(
                "Drag Ghost Template",
                parent,
                typeof(Canvas),
                typeof(RawImage),
                typeof(CanvasGroup),
                typeof(BuildInfoItemView));
            root.GetComponent<RawImage>().raycastTarget = false;
            var group = root.GetComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            var fallback = CreateText(
                root.GetComponent<RectTransform>(),
                "Fallback Label",
                string.Empty,
                12,
                FontStyle.Bold,
                UiTheme.ValueText,
                TextAnchor.MiddleCenter);
            var item = root.GetComponent<BuildInfoItemView>();
            SetReferences(
                item,
                ("kind", (int)BuildInfoItemKind.DragGhost),
                ("root", root.GetComponent<RectTransform>()),
                ("rawImage", root.GetComponent<RawImage>()),
                ("fallbackText", fallback),
                ("canvasGroup", group),
                ("canvas", root.GetComponent<Canvas>()));
            root.SetActive(false);
            return item;
        }

        private static BuildInfoItemView BuildPendingBuildGhostTemplate(Transform parent)
        {
            var root = CreateUiObject(
                "Pending Build Ghost Template",
                parent,
                typeof(RawImage),
                typeof(Button),
                typeof(CanvasGroup),
                typeof(CardPointerInteraction),
                typeof(BuildInfoItemView));
            var item = root.GetComponent<BuildInfoItemView>();
            SetReferences(
                item,
                ("kind", (int)BuildInfoItemKind.PendingBuildGhost),
                ("root", root.GetComponent<RectTransform>()),
                ("button", root.GetComponent<Button>()),
                ("rawImage", root.GetComponent<RawImage>()),
                ("canvasGroup", root.GetComponent<CanvasGroup>()),
                ("pointerInteraction", root.GetComponent<CardPointerInteraction>()));
            root.SetActive(false);
            return item;
        }

        private static Text CreateText(
            RectTransform parent,
            string name,
            string value,
            int fontSize,
            FontStyle style,
            Color color,
            TextAnchor alignment)
        {
            var child = CreateUiObject(name, parent, typeof(Text), typeof(Outline));
            Stretch(child.GetComponent<RectTransform>());
            var text = child.GetComponent<Text>();
            text.text = value;
            text.font = UiEditorAssetReferences.CjkFont;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            child.GetComponent<Outline>().effectColor = UiTheme.DarkShadowLight;
            child.GetComponent<Outline>().effectDistance = new Vector2(1f, -1f);
            return text;
        }

        private static void SetExternalCardRect(
            RectTransform rect,
            int index,
            int columnCount,
            float width,
            float height,
            float horizontalSpacing,
            float verticalSpacing,
            float leftPadding)
        {
            var column = index % columnCount;
            var row = index / columnCount;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(
                leftPadding + column * (width + horizontalSpacing),
                -6f - row * (height + verticalSpacing));
        }

        private static void ApplyCityBoardSlotLayout(
            RectTransform rect,
            CardBoardVisualLayout layout,
            int index)
        {
            var center = layout.GetCityBoardSlotCenter(index);
            var centerY = 1f - center.y;
            rect.anchorMin = new Vector2(
                center.x - layout.CityBoardSlotWidthRatio * 0.5f,
                centerY - layout.CityBoardSlotHeightRatio * 0.5f);
            rect.anchorMax = new Vector2(
                center.x + layout.CityBoardSlotWidthRatio * 0.5f,
                centerY + layout.CityBoardSlotHeightRatio * 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static GameObject CreateUiObject(string name, Transform parent, params Type[] components)
        {
            var types = new Type[components.Length + 1];
            types[0] = typeof(RectTransform);
            Array.Copy(components, 0, types, 1, components.Length);
            var result = new GameObject(name, types);
            if (parent != null) result.transform.SetParent(parent, false);
            return result;
        }

        private static void SetReferences(UnityEngine.Object target, params (string property, object value)[] values)
        {
            var serialized = new SerializedObject(target);
            for (var i = 0; i < values.Length; i++)
            {
                var property = serialized.FindProperty(values[i].property);
                if (property == null) throw new InvalidOperationException("Missing property: " + values[i].property);
                if (values[i].value is int intValue) property.intValue = intValue;
                else property.objectReferenceValue = values[i].value as UnityEngine.Object;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetObjectArray<T>(UnityEngine.Object target, string propertyName, T[] values)
            where T : UnityEngine.Object
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(propertyName);
            if (property == null) throw new InvalidOperationException("Missing property: " + propertyName);
            property.arraySize = values.Length;
            for (var i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Stretch(RectTransform rect, float inset = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        private static void EnsureFolder(string path)
        {
            var parts = path.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
