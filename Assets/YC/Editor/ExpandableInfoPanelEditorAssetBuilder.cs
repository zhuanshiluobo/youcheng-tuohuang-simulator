using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation;

namespace YC.EditorTools
{
    public static class ExpandableInfoPanelEditorAssetBuilder
    {
        public const string PrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/ExpandableInfoPanel.prefab";

        public static void Rebuild()
        {
            YC.Editor.EffectDialogLayoutEditorAssetBuilder.RebuildProfilesOnlyForBuilder();
            RebuildWithProfile(
                YC.Editor.EffectDialogLayoutEditorAssetBuilder.LoadRequiredExpandableProfile());
        }

        internal static void RebuildWithProfile(ExpandableInfoPanelLayoutProfile layoutProfile)
        {
            YC.Editor.UiThemeBuildReadiness.InitializeRequiredTheme();
            EnsureFolder("Assets/YC/Presentation/Prefabs/Gameplay");
            YC.Editor.EffectDialogLayoutControlledMetaGuid.ValidateAsset(
                PrefabPath,
                YC.Editor.EffectDialogLayoutBuildReadiness.ExpandableInfoPanelPrefabGuid);
            var root = BuildPrefabContents(layoutProfile);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceUpdate);
        }

        internal static GameObject BuildPrefabContents(
            ExpandableInfoPanelLayoutProfile layoutProfile)
        {
            var layoutReason = string.Empty;
            if (layoutProfile == null || !layoutProfile.TryValidateConfiguration(out layoutReason))
            {
                throw new InvalidOperationException(
                    "ExpandableInfoPanel Prefab 缺少有效布局 Profile：" + layoutReason);
            }

            var root = CreateUiObject(
                "Expandable Info Panel",
                null,
                typeof(ExpandableInfoPanel),
                typeof(ExpandableInfoPanelView));
            Stretch(root.GetComponent<RectTransform>());
            var controller = root.GetComponent<ExpandableInfoPanel>();
            var panel = CreateUiObject("Sidebar Panel", root.transform, typeof(Image), typeof(Outline));
            var panelRect = panel.GetComponent<RectTransform>();
            layoutProfile.PanelLayout.ApplyTo(panelRect);
            panel.GetComponent<Image>().color = UiTheme.PanelBackground;
            panel.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
            panel.GetComponent<Outline>().effectDistance = layoutProfile.PanelOutlineDistance;

            var toggle = CreateUiObject("Toggle Button", panel.transform, typeof(Image), typeof(Button));
            var toggleRect = toggle.GetComponent<RectTransform>();
            layoutProfile.ToggleLayout.ApplyTo(toggleRect);
            toggle.GetComponent<Image>().color = UiTheme.PanelBackgroundLighter;
            var toggleText = CreateText(toggleRect, "Label", "▶", 22, FontStyle.Bold, UiTheme.GoldText);
            toggleText.alignment = TextAnchor.MiddleCenter;

            var contentArea = CreateUiObject("Content Area", panel.transform, typeof(RectMask2D));
            var contentAreaRect = contentArea.GetComponent<RectTransform>();
            layoutProfile.ContentAreaLayout.ApplyTo(contentAreaRect);
            contentArea.SetActive(false);

            var header = CreateUiObject("Header", contentArea.transform, typeof(Text), typeof(Outline));
            var headerRect = header.GetComponent<RectTransform>();
            layoutProfile.HeaderLayout.ApplyTo(headerRect);
            var headerText = header.GetComponent<Text>();
            headerText.text = "信息面板";
            headerText.alignment = TextAnchor.MiddleCenter;
            headerText.color = UiTheme.GoldText;
            headerText.fontSize = 30;
            headerText.fontStyle = FontStyle.Bold;
            headerText.font = UiEditorAssetReferences.CjkFont;
            header.GetComponent<Outline>().effectColor = UiTheme.DarkShadowLight;
            header.GetComponent<Outline>().effectDistance = layoutProfile.HeaderOutlineDistance;
            var separator = CreateUiObject("Header Separator", header.transform, typeof(Image));
            var separatorRect = separator.GetComponent<RectTransform>();
            layoutProfile.HeaderSeparatorLayout.ApplyTo(separatorRect);
            separator.GetComponent<Image>().color = UiTheme.GoldSeparator;

            var scroll = CreateUiObject("Scroll View", contentArea.transform, typeof(ScrollRect), typeof(Image));
            var scrollRect = scroll.GetComponent<RectTransform>();
            layoutProfile.ScrollLayout.ApplyTo(scrollRect);
            scroll.GetComponent<Image>().color = UiTheme.ScrollBackground;
            var viewport = CreateUiObject("Viewport", scroll.transform, typeof(RectMask2D));
            var viewportRect = viewport.GetComponent<RectTransform>();
            Stretch(viewportRect);
            var content = CreateUiObject(
                "Content",
                viewport.transform,
                typeof(VerticalLayoutGroup),
                typeof(ContentSizeFitter));
            var contentRect = content.GetComponent<RectTransform>();
            layoutProfile.ScrollContentLayout.ApplyTo(contentRect);
            var layout = content.GetComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 5f;
            layout.padding = new RectOffset(4, 4, 4, 4);
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scrollComponent = scroll.GetComponent<ScrollRect>();
            scrollComponent.viewport = viewportRect;
            scrollComponent.content = contentRect;
            scrollComponent.horizontal = false;
            scrollComponent.vertical = true;
            scrollComponent.movementType = ScrollRect.MovementType.Clamped;

            var templates = CreateUiObject("Templates", root.transform);
            Stretch(templates.GetComponent<RectTransform>());
            var moduleTemplate = BuildModuleTemplate(templates.transform, layoutProfile);
            var rowTemplate = BuildRowTemplate(templates.transform, layoutProfile);
            var imageTemplate = BuildImagePreviewTemplate(templates.transform, layoutProfile);
            var stripTemplate = BuildCardStripTemplate(templates.transform);
            var thumbnailTemplate = BuildCardThumbnailTemplate(templates.transform, layoutProfile);
            var ghostTemplate = BuildDragGhostTemplate(templates.transform, layoutProfile);

            var panelView = root.GetComponent<ExpandableInfoPanelView>();
            SetReferences(
                controller,
                ("view", panelView),
                ("layoutProfile", layoutProfile));
            SetReferences(
                panelView,
                ("controller", controller),
                ("root", root.GetComponent<RectTransform>()),
                ("panelTransform", panelRect),
                ("contentArea", contentAreaRect),
                ("scrollContent", contentRect),
                ("toggleButton", toggle.GetComponent<Button>()),
                ("toggleButtonText", toggleText),
                ("moduleTemplate", moduleTemplate),
                ("rowTemplate", rowTemplate),
                ("imagePreviewTemplate", imageTemplate),
                ("cardStripTemplate", stripTemplate),
                ("cardThumbnailTemplate", thumbnailTemplate),
                ("dragGhostTemplate", ghostTemplate));
            return root;
        }

        private static ExpandableInfoItemView BuildModuleTemplate(
            Transform parent,
            ExpandableInfoPanelLayoutProfile layoutProfile)
        {
            var root = CreateUiObject(
                "Module Template",
                parent,
                typeof(VerticalLayoutGroup),
                typeof(ContentSizeFitter),
                typeof(ExpandableInfoItemView));
            root.GetComponent<RectTransform>().sizeDelta = Vector2.zero;
            var group = root.GetComponent<VerticalLayoutGroup>();
            group.childAlignment = TextAnchor.UpperCenter;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;
            group.spacing = 4f;
            group.padding = new RectOffset(4, 4, 4, 4);
            root.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var title = CreateUiObject(
                "Section Title",
                root.transform,
                typeof(Image),
                typeof(Button),
                typeof(Outline),
                typeof(LayoutElement));
            layoutProfile.ModuleTitleLayout.ApplyTo(title.GetComponent<RectTransform>());
            var titleElement = title.GetComponent<LayoutElement>();
            titleElement.minHeight = 25f;
            titleElement.preferredHeight = 25f;
            titleElement.flexibleWidth = 1f;
            title.GetComponent<Image>().color = UiTheme.SectionTitleBackground;
            title.GetComponent<Outline>().effectColor = UiTheme.GoldOutlineThin;
            title.GetComponent<Outline>().effectDistance = layoutProfile.ItemOutlineDistance;
            var titleText = CreateText(title.GetComponent<RectTransform>(), "Title Text", "▼ 模块", 14, FontStyle.Bold, UiTheme.GoldText);
            titleText.alignment = TextAnchor.MiddleLeft;
            titleText.rectTransform.offsetMin = layoutProfile.TextInsetMin;
            titleText.rectTransform.offsetMax = layoutProfile.TextInsetMax;
            var moduleContent = CreateUiObject("Section Content", root.transform, typeof(LayoutElement));
            var moduleContentRect = moduleContent.GetComponent<RectTransform>();
            layoutProfile.ModuleContentLayout.ApplyTo(moduleContentRect);
            var contentElement = moduleContent.GetComponent<LayoutElement>();
            contentElement.minWidth = layoutProfile.ModuleContentWidth;
            contentElement.preferredWidth = layoutProfile.ModuleContentWidth;
            contentElement.flexibleWidth = 1f;
            var item = root.GetComponent<ExpandableInfoItemView>();
            SetReferences(
                item,
                ("kind", (int)ExpandableInfoItemKind.Module),
                ("root", root.GetComponent<RectTransform>()),
                ("layoutElement", contentElement),
                ("background", title.GetComponent<Image>()),
                ("outline", title.GetComponent<Outline>()),
                ("button", title.GetComponent<Button>()),
                ("primaryText", titleText),
                ("contentRoot", moduleContentRect));
            root.SetActive(false);
            return item;
        }

        private static ExpandableInfoItemView BuildRowTemplate(
            Transform parent,
            ExpandableInfoPanelLayoutProfile layoutProfile)
        {
            var root = CreateUiObject(
                "Row Template",
                parent,
                typeof(Image),
                typeof(Outline),
                typeof(LayoutElement),
                typeof(Button),
                typeof(ExpandableInfoItemView));
            root.GetComponent<Image>().color = UiTheme.ScrollBackground;
            root.GetComponent<Image>().raycastTarget = false;
            root.GetComponent<Outline>().effectColor = UiTheme.ScrollBackground;
            root.GetComponent<Outline>().effectDistance = layoutProfile.ItemOutlineDistance;
            var text = CreateText(root.GetComponent<RectTransform>(), "Value", string.Empty, 14, FontStyle.Bold, UiTheme.GoldText);
            text.alignment = TextAnchor.MiddleLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.rectTransform.offsetMin = layoutProfile.TextInsetMin;
            text.rectTransform.offsetMax = layoutProfile.TextInsetMax;
            var item = root.GetComponent<ExpandableInfoItemView>();
            SetReferences(
                item,
                ("kind", (int)ExpandableInfoItemKind.Row),
                ("root", root.GetComponent<RectTransform>()),
                ("layoutElement", root.GetComponent<LayoutElement>()),
                ("background", root.GetComponent<Image>()),
                ("outline", root.GetComponent<Outline>()),
                ("button", root.GetComponent<Button>()),
                ("primaryText", text));
            root.SetActive(false);
            return item;
        }

        private static ExpandableInfoItemView BuildImagePreviewTemplate(
            Transform parent,
            ExpandableInfoPanelLayoutProfile layoutProfile)
        {
            var root = CreateUiObject(
                "Image Preview Template",
                parent,
                typeof(Image),
                typeof(Outline),
                typeof(LayoutElement),
                typeof(ExpandableInfoItemView));
            root.GetComponent<Image>().color = UiTheme.ScrollBackground;
            root.GetComponent<Image>().raycastTarget = false;
            root.GetComponent<Outline>().effectColor = UiTheme.ScrollBackground;
            var frame = CreateUiObject("Preview Frame", root.transform, typeof(Image), typeof(Outline));
            var frameRect = frame.GetComponent<RectTransform>();
            frameRect.anchorMin = frameRect.anchorMax = frameRect.pivot =
                layoutProfile.CenterAnchor;
            frame.GetComponent<Image>().color = UiTheme.ScrollBackground;
            frame.GetComponent<Outline>().effectColor = UiTheme.GoldOutlineThin;
            frame.GetComponent<Outline>().effectDistance = layoutProfile.ItemOutlineDistance;
            var imageObject = CreateUiObject("Preview", frame.transform, typeof(RawImage));
            var imageRect = imageObject.GetComponent<RectTransform>();
            imageRect.anchorMin = imageRect.anchorMax = imageRect.pivot =
                layoutProfile.CenterAnchor;
            imageObject.GetComponent<RawImage>().raycastTarget = false;
            var item = root.GetComponent<ExpandableInfoItemView>();
            SetReferences(
                item,
                ("kind", (int)ExpandableInfoItemKind.ImagePreview),
                ("root", root.GetComponent<RectTransform>()),
                ("layoutElement", root.GetComponent<LayoutElement>()),
                ("background", root.GetComponent<Image>()),
                ("outline", root.GetComponent<Outline>()),
                ("previewFrame", frameRect),
                ("rawImage", imageObject.GetComponent<RawImage>()));
            root.SetActive(false);
            return item;
        }

        private static ExpandableInfoItemView BuildCardStripTemplate(Transform parent)
        {
            var root = CreateUiObject(
                "Card Strip Template",
                parent,
                typeof(Image),
                typeof(Outline),
                typeof(LayoutElement),
                typeof(ExpandableInfoItemView));
            root.GetComponent<Image>().color = UiTheme.ScrollBackground;
            root.GetComponent<Image>().raycastTarget = false;
            root.GetComponent<Outline>().effectColor = UiTheme.ScrollBackground;
            var item = root.GetComponent<ExpandableInfoItemView>();
            SetReferences(
                item,
                ("kind", (int)ExpandableInfoItemKind.CardStrip),
                ("root", root.GetComponent<RectTransform>()),
                ("layoutElement", root.GetComponent<LayoutElement>()),
                ("background", root.GetComponent<Image>()),
                ("outline", root.GetComponent<Outline>()),
                ("contentRoot", root.GetComponent<RectTransform>()));
            root.SetActive(false);
            return item;
        }

        private static ExpandableInfoItemView BuildCardThumbnailTemplate(
            Transform parent,
            ExpandableInfoPanelLayoutProfile layoutProfile)
        {
            var root = CreateUiObject(
                "Card Thumbnail Template",
                parent,
                typeof(RawImage),
                typeof(Button),
                typeof(Outline),
                typeof(CardPointerInteraction),
                typeof(ExpandableInfoItemView));
            root.GetComponent<Outline>().effectColor = UiTheme.GoldOutlineThin;
            root.GetComponent<Outline>().effectDistance = layoutProfile.CardOutlineDistance;
            var item = root.GetComponent<ExpandableInfoItemView>();
            SetReferences(
                item,
                ("kind", (int)ExpandableInfoItemKind.CardThumbnail),
                ("root", root.GetComponent<RectTransform>()),
                ("outline", root.GetComponent<Outline>()),
                ("button", root.GetComponent<Button>()),
                ("rawImage", root.GetComponent<RawImage>()),
                ("pointerInteraction", root.GetComponent<CardPointerInteraction>()));
            root.SetActive(false);
            return item;
        }

        private static ExpandableInfoItemView BuildDragGhostTemplate(
            Transform parent,
            ExpandableInfoPanelLayoutProfile layoutProfile)
        {
            var root = CreateUiObject(
                "Drag Ghost Template",
                parent,
                typeof(RawImage),
                typeof(CanvasGroup),
                typeof(Outline),
                typeof(ExpandableInfoItemView));
            root.GetComponent<RawImage>().raycastTarget = false;
            root.GetComponent<CanvasGroup>().interactable = false;
            root.GetComponent<CanvasGroup>().blocksRaycasts = false;
            root.GetComponent<Outline>().effectColor = new Color(1f, 0.82f, 0.35f, 0.85f);
            root.GetComponent<Outline>().effectDistance = layoutProfile.CardOutlineDistance;
            var item = root.GetComponent<ExpandableInfoItemView>();
            SetReferences(
                item,
                ("kind", (int)ExpandableInfoItemKind.DragGhost),
                ("root", root.GetComponent<RectTransform>()),
                ("outline", root.GetComponent<Outline>()),
                ("rawImage", root.GetComponent<RawImage>()),
                ("canvasGroup", root.GetComponent<CanvasGroup>()));
            root.SetActive(false);
            return item;
        }

        private static Text CreateText(
            RectTransform parent,
            string name,
            string value,
            int fontSize,
            FontStyle style,
            Color color)
        {
            var child = CreateUiObject(name, parent, typeof(Text), typeof(Outline));
            Stretch(child.GetComponent<RectTransform>());
            var text = child.GetComponent<Text>();
            text.text = value;
            text.font = UiEditorAssetReferences.CjkFont;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.supportRichText = false;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            child.GetComponent<Outline>().effectColor = UiTheme.DarkShadowLight;
            child.GetComponent<Outline>().effectDistance = new Vector2(1f, -1f);
            return text;
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
                if (values[i].value is int intValue) property.enumValueIndex = intValue;
                else property.objectReferenceValue = values[i].value as UnityEngine.Object;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
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
