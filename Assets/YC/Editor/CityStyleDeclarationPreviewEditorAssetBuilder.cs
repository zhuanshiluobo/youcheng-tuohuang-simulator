using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation;
using Object = UnityEngine.Object;

namespace YC.EditorTools
{
    public static class CityStyleDeclarationPreviewEditorAssetBuilder
    {
        public const string PrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CityStyleDeclarationPreviewDialog.prefab";

        public static void Rebuild()
        {
            YC.Editor.UiThemeBuildReadiness.InitializeRequiredTheme();
            EnsureFolder("Assets/YC/Presentation/Prefabs/Gameplay/Dialogs");
            BuildPrefab();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[CityStyleDeclarationPreviewEditorAssetBuilder] 已重建城市样式预览编辑器资产。");
        }

        private static void BuildPrefab()
        {
            var cardBoardVisualLayout =
                YC.Editor.SpatialLayoutEditorAssetBuilder.LoadRequiredCardBoardLayout();
            var cardInteractionLayoutProfile =
                YC.Editor.SecondaryLayoutEditorAssetBuilder.LoadRequiredCardProfile();
            var stagingCanvasObject = CreateUiObject(
                "City Style Preview Staging Canvas",
                null,
                typeof(Canvas));
            var root = CreateUiObject(
                "City Style Declaration Preview Canvas",
                stagingCanvasObject.transform,
                typeof(Canvas),
                typeof(GraphicRaycaster));
            try
            {
                Stretch(root.GetComponent<RectTransform>());
                var overlayCanvas = root.GetComponent<Canvas>();
                overlayCanvas.overrideSorting = true;
                overlayCanvas.sortingOrder = 130;

                var overlayObject = CreateUiObject(
                    "City Style Declaration Preview Overlay",
                    root.transform,
                    typeof(Image),
                    typeof(CityStyleDeclarationPreviewInputHandler));
                var overlayRect = overlayObject.GetComponent<RectTransform>();
                Stretch(overlayRect);
                var overlayImage = overlayObject.GetComponent<Image>();
                overlayImage.color = new Color(0f, 0f, 0f, 0.82f);
                overlayImage.raycastTarget = true;

                var panel = CreatePanel(
                    overlayRect,
                    "City Style Declaration Preview Panel",
                    new Vector2(1520f, 900f),
                    Vector2.zero,
                    UiTheme.PanelBackground);
                panel.GetComponent<Outline>().effectDistance = new Vector2(4f, -4f);
                CreateText(panel, "City Style Declaration Preview Title", "样式卡预览", 30,
                    FontStyle.Bold, UiTheme.GoldText, TextAnchor.MiddleCenter,
                    new Vector2(680f, 52f), new Vector2(0f, 410f));

                var closeButton = UguiUtility.CreateViewerCloseButton(
                    panel,
                    "Close City Style Declaration Preview Button",
                    () => { });
                closeButton.onClick.RemoveAllListeners();
                var closeLabel = closeButton.GetComponentInChildren<Text>();
                closeLabel.font = UiEditorAssetReferences.CjkFont;

                var leftPanel = CreatePanel(
                    panel,
                    "City Style Preview Section",
                    new Vector2(920f, 800f),
                    new Vector2(-280f, -24f),
                    new Color(0.04f, 0.035f, 0.028f, 0.72f));
                var rightPanel = CreatePanel(
                    panel,
                    "City Style Preview Board Section",
                    new Vector2(500f, 800f),
                    new Vector2(490f, -24f),
                    new Color(0.04f, 0.035f, 0.028f, 0.72f));

                var cityStyleTitle = CreateText(
                    leftPanel, "City Style Preview Name", string.Empty, 24, FontStyle.Bold,
                    UiTheme.GoldText, TextAnchor.MiddleCenter,
                    new Vector2(700f, 40f), new Vector2(0f, 360f));
                var cardFrame = CreatePanel(
                    leftPanel, "City Style Preview Card Frame", new Vector2(850f, 548f),
                    new Vector2(0f, 40f), UiTheme.ScrollBackground);
                var cardObject = CreateUiObject("City Style Preview Card", cardFrame, typeof(RawImage));
                var cardRect = cardObject.GetComponent<RectTransform>();
                StretchWithInset(cardRect, 6f);
                var cardImage = cardObject.GetComponent<RawImage>();
                cardImage.color = Color.white;
                cardImage.raycastTarget = false;

                var markerRootObject = CreateUiObject(
                    "City Style Preview Influence Markers", cardRect, Array.Empty<Type>());
                var markerRoot = markerRootObject.GetComponent<RectTransform>();
                Stretch(markerRoot);
                var cardPlaceholder = CreateText(
                    cardFrame, "City Style Preview Missing Image", "样式卡图片暂不可用", 20,
                    FontStyle.Bold, UiTheme.ValueText, TextAnchor.MiddleCenter,
                    new Vector2(760f, 100f), Vector2.zero);
                cardPlaceholder.gameObject.SetActive(false);

                var previousButton = CreateButton(
                    leftPanel, "Previous City Style", "<", new Vector2(58f, 118f),
                    new Vector2(-425f, 40f), 34);
                var nextButton = CreateButton(
                    leftPanel, "Next City Style", ">", new Vector2(58f, 118f),
                    new Vector2(425f, 40f), 34);
                var confirmButton = CreateButton(
                    leftPanel, "Confirm City Style Declaration", "确认宣告", new Vector2(180f, 48f),
                    new Vector2(0f, -310f), 16);
                var specialActionHint = CreateText(
                    leftPanel, "City Style Special Action Hint", string.Empty, 15, FontStyle.Bold,
                    new Color(0.48f, 1f, 0.42f, 1f), TextAnchor.MiddleCenter,
                    new Vector2(820f, 38f), new Vector2(0f, -258f));
                specialActionHint.raycastTarget = false;
                var matchStatus = CreateText(
                    leftPanel, "City Style Match Status", string.Empty, 15, FontStyle.Bold,
                    UiTheme.ValueText, TextAnchor.MiddleCenter,
                    new Vector2(840f, 54f), new Vector2(0f, -375f));
                matchStatus.raycastTarget = false;

                var boardTitle = CreateText(
                    rightPanel, "City Style Preview Board Title", "建设面板", 23, FontStyle.Bold,
                    UiTheme.ValueText, TextAnchor.MiddleCenter,
                    new Vector2(430f, 42f), new Vector2(0f, 370f));
                var boardObject = CreateUiObject(
                    "City Style Preview Board",
                    rightPanel,
                    typeof(RawImage),
                    typeof(Outline),
                    typeof(CityStyleDeclarationBoardPointerHandler));
                var boardRect = boardObject.GetComponent<RectTransform>();
                SetRect(boardRect, new Vector2(397f, 733f), new Vector2(0f, -20f));
                var boardImage = boardObject.GetComponent<RawImage>();
                boardImage.color = UiTheme.ScrollBackground;
                boardImage.raycastTarget = true;
                var boardOutline = boardObject.GetComponent<Outline>();
                boardOutline.effectColor = UiTheme.GoldOutlineThin;
                boardOutline.effectDistance = new Vector2(2f, -2f);

                var slotCount = cardBoardVisualLayout.CityBoardSlotCount;
                var slotRoots = new RectTransform[slotCount];
                var slotImages = new Image[slotCount];
                var slotButtons = new Button[slotCount];
                var slotOutlines = new Outline[slotCount];
                var slotPointerHandlers = new CityStyleDeclarationSlotPointerHandler[slotCount];
                var slotFacilityImages = new RawImage[slotCount];
                var slotUsedBadges = new GameObject[slotCount];
                for (var slotIndex = 0; slotIndex < slotCount; slotIndex++)
                {
                    var slotObject = CreateUiObject(
                        "宣告槽位 " + (slotIndex + 1),
                        boardRect,
                        typeof(Image),
                        typeof(Button),
                        typeof(Outline),
                        typeof(CityStyleDeclarationSlotPointerHandler));
                    var slotRect = slotObject.GetComponent<RectTransform>();
                    ApplyCityBoardSlotLayout(
                        slotRect,
                        cardBoardVisualLayout,
                        slotIndex);
                    var slotImage = slotObject.GetComponent<Image>();
                    slotImage.color = Color.clear;
                    slotImage.raycastTarget = true;
                    var slotButton = slotObject.GetComponent<Button>();
                    slotButton.transition = Selectable.Transition.None;
                    slotButton.interactable = false;
                    var slotOutline = slotObject.GetComponent<Outline>();
                    slotOutline.effectColor = Color.clear;
                    slotOutline.effectDistance = Vector2.zero;

                    var facilityObject = CreateUiObject("设施卡图", slotRect, typeof(RawImage));
                    var facilityImage = facilityObject.GetComponent<RawImage>();
                    Stretch(facilityImage.rectTransform);
                    facilityImage.color = Color.white;
                    facilityImage.raycastTarget = false;
                    facilityObject.SetActive(false);

                    var usedBadge = CreateUiObject(
                        "历史已使用", slotRect, typeof(Image), typeof(Outline));
                    var usedBadgeRect = usedBadge.GetComponent<RectTransform>();
                    SetRect(usedBadgeRect, new Vector2(92f, 28f), Vector2.zero);
                    usedBadgeRect.localEulerAngles = new Vector3(0f, 0f, 180f);
                    var usedImage = usedBadge.GetComponent<Image>();
                    usedImage.color = new Color(0.58f, 0.06f, 0.03f, 0.94f);
                    usedImage.raycastTarget = false;
                    var usedOutline = usedBadge.GetComponent<Outline>();
                    usedOutline.effectColor = UiTheme.DarkShadowLight;
                    usedOutline.effectDistance = new Vector2(1f, -1f);
                    var usedText = CreateText(
                        usedBadgeRect, "历史已使用 Text", "已使用", 13, FontStyle.Bold,
                        Color.white, TextAnchor.MiddleCenter, new Vector2(92f, 28f), Vector2.zero);
                    usedText.raycastTarget = false;
                    usedBadge.SetActive(false);

                    slotRoots[slotIndex] = slotRect;
                    slotImages[slotIndex] = slotImage;
                    slotButtons[slotIndex] = slotButton;
                    slotOutlines[slotIndex] = slotOutline;
                    slotPointerHandlers[slotIndex] =
                        slotObject.GetComponent<CityStyleDeclarationSlotPointerHandler>();
                    slotFacilityImages[slotIndex] = facilityImage;
                    slotUsedBadges[slotIndex] = usedBadge;
                }

                var warningObject = CreateUiObject(
                    "Special Action Warning Confirmation", overlayRect, typeof(Image));
                var warningRect = warningObject.GetComponent<RectTransform>();
                Stretch(warningRect);
                warningObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.72f);
                var warningPanel = CreatePanel(
                    warningRect, "Special Action Warning Confirmation Panel",
                    new Vector2(620f, 280f), Vector2.zero, UiTheme.PanelBackground);
                CreateText(
                    warningPanel, "Special Action Warning Confirmation Title", "确认发动特殊行动", 24,
                    FontStyle.Bold, UiTheme.GoldText, TextAnchor.MiddleCenter,
                    new Vector2(540f, 44f), new Vector2(0f, 86f));
                var warningMessage = CreateText(
                    warningPanel, "Special Action Warning Confirmation Message", string.Empty, 17,
                    FontStyle.Bold, UiTheme.ValueText, TextAnchor.MiddleCenter,
                    new Vector2(540f, 96f), new Vector2(0f, 12f));
                warningMessage.raycastTarget = false;
                var warningCancel = CreateButton(
                    warningPanel, "Cancel Special Action Warning", "取消",
                    new Vector2(160f, 46f), new Vector2(-98f, -90f), 16);
                var warningConfirm = CreateButton(
                    warningPanel, "Confirm Special Action Warning", "确认发动",
                    new Vector2(160f, 46f), new Vector2(98f, -90f), 16);
                warningObject.SetActive(false);

                var templateHostObject = CreateUiObject("Dynamic Templates", root.transform, Array.Empty<Type>());
                var templateHost = templateHostObject.GetComponent<RectTransform>();
                Stretch(templateHost);
                var markerTemplateObject = CreateUiObject(
                    "InfluenceMarker Template",
                    templateHost,
                    typeof(Image),
                    typeof(Outline),
                    typeof(Button),
                    typeof(CardPointerInteraction));
                var markerTemplate = markerTemplateObject.GetComponent<RectTransform>();
                SetRect(markerTemplate, new Vector2(20f, 20f), Vector2.zero);
                var markerTemplateImage = markerTemplateObject.GetComponent<Image>();
                markerTemplateImage.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
                markerTemplateImage.raycastTarget = false;
                var markerTemplateOutline = markerTemplateObject.GetComponent<Outline>();
                markerTemplateOutline.effectColor = Color.white;
                markerTemplateOutline.effectDistance = new Vector2(1f, -1f);
                var markerTemplateButton = markerTemplateObject.GetComponent<Button>();
                markerTemplateButton.transition = Selectable.Transition.None;
                markerTemplateButton.interactable = false;
                markerTemplateObject.SetActive(false);

                var dropTemplateObject = CreateUiObject(
                    "SpecialActionDropTarget Template",
                    templateHost,
                    typeof(Image),
                    typeof(Outline),
                    typeof(CityStyleSpecialActionDropTarget));
                var dropTemplate = dropTemplateObject.GetComponent<RectTransform>();
                var dropImage = dropTemplateObject.GetComponent<Image>();
                dropImage.color = new Color(0.18f, 0.9f, 0.32f, 0.2f);
                dropImage.raycastTarget = true;
                var dropOutline = dropTemplateObject.GetComponent<Outline>();
                dropOutline.effectColor = new Color(0.48f, 1f, 0.42f, 1f);
                dropOutline.effectDistance = new Vector2(4f, -4f);
                dropTemplateObject.SetActive(false);

                var view = root.AddComponent<CityStyleDeclarationPreviewView>();
                SetReferences(
                    view,
                    ("overlayCanvas", overlayCanvas),
                    ("rootRect", root.GetComponent<RectTransform>()),
                    ("overlayObject", overlayObject),
                    ("overlayImage", overlayImage),
                    ("panel", panel),
                    ("inputHandler", overlayObject.GetComponent<CityStyleDeclarationPreviewInputHandler>()),
                    ("closeButton", closeButton),
                    ("closeButtonLabel", closeLabel),
                    ("cardBoardVisualLayout", cardBoardVisualLayout),
                    ("cardInteractionLayoutProfile", cardInteractionLayoutProfile),
                    ("cityStyleCardImage", cardImage),
                    ("cityStyleInfluenceMarkerRoot", markerRoot),
                    ("cityStyleCardPlaceholder", cardPlaceholder),
                    ("cityStyleTitleText", cityStyleTitle),
                    ("matchStatusText", matchStatus),
                    ("specialActionHintText", specialActionHint),
                    ("boardTitleText", boardTitle),
                    ("cityBoardRect", boardRect),
                    ("cityBoardImage", boardImage),
                    ("boardOutline", boardOutline),
                    ("boardPointerHandler", boardObject.GetComponent<CityStyleDeclarationBoardPointerHandler>()),
                    ("previousButton", previousButton),
                    ("previousButtonLabel", previousButton.GetComponentInChildren<Text>()),
                    ("nextButton", nextButton),
                    ("nextButtonLabel", nextButton.GetComponentInChildren<Text>()),
                    ("confirmDeclarationButton", confirmButton),
                    ("confirmDeclarationButtonLabel", confirmButton.GetComponentInChildren<Text>()),
                    ("specialActionWarningObject", warningObject),
                    ("specialActionWarningMessage", warningMessage),
                    ("cancelSpecialActionWarningButton", warningCancel),
                    ("confirmSpecialActionWarningButton", warningConfirm),
                    ("influenceMarkerTemplate", markerTemplate),
                    ("specialActionDropTargetTemplate", dropTemplate));
                SetReferenceArray(view, "cityBoardSlotRoots", slotRoots);
                SetReferenceArray(view, "cityBoardSlotImages", slotImages);
                SetReferenceArray(view, "cityBoardSlotButtons", slotButtons);
                SetReferenceArray(view, "cityBoardSlotOutlines", slotOutlines);
                SetReferenceArray(view, "cityBoardSlotPointerHandlers", slotPointerHandlers);
                SetReferenceArray(view, "cityBoardSlotFacilityImages", slotFacilityImages);
                SetReferenceArray(view, "cityBoardSlotUsedBadges", slotUsedBadges);

                if (!view.TryValidateConfiguration(out var reason))
                {
                    throw new InvalidOperationException(reason);
                }

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(stagingCanvasObject);
            }
        }

        private static RectTransform CreatePanel(
            RectTransform parent,
            string name,
            Vector2 size,
            Vector2 position,
            Color color)
        {
            var panelObject = CreateUiObject(name, parent, typeof(Image), typeof(Outline));
            var rect = panelObject.GetComponent<RectTransform>();
            SetRect(rect, size, position);
            panelObject.GetComponent<Image>().color = color;
            var outline = panelObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(2f, -2f);
            return rect;
        }

        private static void ApplyCityBoardSlotLayout(
            RectTransform rect,
            CardBoardVisualLayout layout,
            int slotIndex)
        {
            var center = layout.GetCityBoardSlotCenter(slotIndex);
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

        private static Button CreateButton(
            RectTransform parent,
            string name,
            string label,
            Vector2 size,
            Vector2 position,
            int fontSize)
        {
            var buttonObject = CreateUiObject(name, parent, typeof(Image), typeof(Button), typeof(Outline));
            SetRect(buttonObject.GetComponent<RectTransform>(), size, position);
            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);
            var labelText = CreateText(
                buttonObject.GetComponent<RectTransform>(), name + " Label", label, fontSize,
                FontStyle.Bold, UiTheme.ValueText, TextAnchor.MiddleCenter, size, Vector2.zero);
            labelText.raycastTarget = false;
            return buttonObject.GetComponent<Button>();
        }

        private static Text CreateText(
            RectTransform parent,
            string name,
            string value,
            int fontSize,
            FontStyle fontStyle,
            Color color,
            TextAnchor alignment,
            Vector2 size,
            Vector2 position)
        {
            var textObject = CreateUiObject(name, parent, typeof(Text));
            SetRect(textObject.GetComponent<RectTransform>(), size, position);
            var text = textObject.GetComponent<Text>();
            text.text = value ?? string.Empty;
            text.font = UiEditorAssetReferences.CjkFont;
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.color = color;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static GameObject CreateUiObject(string name, Transform parent, params Type[] components)
        {
            var types = new Type[components.Length + 1];
            types[0] = typeof(RectTransform);
            Array.Copy(components, 0, types, 1, components.Length);
            var gameObject = new GameObject(name, types);
            gameObject.layer = LayerMask.NameToLayer("UI");
            gameObject.transform.SetParent(parent, false);
            return gameObject;
        }

        private static void SetReferences(Object target, params (string property, Object value)[] references)
        {
            var serialized = new SerializedObject(target);
            for (var i = 0; i < references.Length; i++)
            {
                var property = serialized.FindProperty(references[i].property);
                if (property == null)
                {
                    throw new InvalidOperationException(target.GetType().Name + " missing property " +
                                                        references[i].property + ".");
                }

                property.objectReferenceValue = references[i].value;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetReferenceArray<T>(Object target, string propertyName, T[] values)
            where T : Object
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(propertyName);
            if (property == null)
            {
                throw new InvalidOperationException(target.GetType().Name + " missing property " + propertyName + ".");
            }

            property.arraySize = values == null ? 0 : values.Length;
            for (var i = 0; values != null && i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetRect(RectTransform rect, Vector2 size, Vector2 position)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void StretchWithInset(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            var separator = path.LastIndexOf('/');
            var parent = path.Substring(0, separator);
            var name = path.Substring(separator + 1);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
