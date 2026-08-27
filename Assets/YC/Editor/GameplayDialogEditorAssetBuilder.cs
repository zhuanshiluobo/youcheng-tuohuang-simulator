using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation;
using Object = UnityEngine.Object;

namespace YC.EditorTools
{
    public static class GameplayDialogEditorAssetBuilder
    {
        public const string DialogFolder = "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs";
        public const string EffectDialogShellPrefabPath = DialogFolder + "/EffectDialogShell.prefab";
        public const string EffectDialogShellPrefabGuid = "a247544ff081c484cb987604277e8d90";
        public const string DispatchDecisionPrefabPath = DialogFolder + "/DispatchDecisionDialog.prefab";
        public const string EventChoiceDialogPrefabPath = DialogFolder + "/EventChoiceDialog.prefab";
        public const string EventChoiceDialogPrefabGuid = "3fe2d5d7571d9e4488d62bd519017c21";
        public const string EventResourcePointRibbonSpritePath =
            "Assets/YC/Presentation/Sprites/EventResourcePointRibbon.png";
        public const string SharedUiVisualsAssetPath =
            SharedUiVisualEditorAssetBuilder.AssetPath;
        public const string SharedUiVisualsAssetGuid = "accc4ac362a0a4b4b824d05cbff434f8";
        public const long TriangleUpSpriteLocalId = 8710250400556581170L;
        public const long TriangleDownSpriteLocalId = 9192213887596980247L;

        [MenuItem("Tools/YC/Rebuild Gameplay Dialog Editor Assets")]
        public static void Rebuild()
        {
            YC.Editor.UiThemeBuildReadiness.InitializeRequiredTheme();
            YC.Editor.EffectDialogLayoutEditorAssetBuilder.RebuildProfilesOnlyForBuilder();
            EnsureFolder(DialogFolder);
            BuildEffectDialogShellPrefab(
                YC.Editor.EffectDialogLayoutEditorAssetBuilder.LoadRequiredEffectProfile(),
                SharedUiVisualEditorAssetBuilder.BuildOrUpdate());
            BuildDispatchDecisionPrefab();
            BuildEventChoiceDialogPrefab();
            CityStyleDeclarationPreviewEditorAssetBuilder.Rebuild();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[GameplayDialogEditorAssetBuilder] 已重建游戏流程对话框编辑器资产。");
        }

        public static void RebuildEventChoiceDialogOnly()
        {
            YC.Editor.UiThemeBuildReadiness.InitializeRequiredTheme();
            EnsureFolder(DialogFolder);
            BuildEventChoiceDialogPrefab();
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(EventChoiceDialogPrefabPath, ImportAssetOptions.ForceUpdate);
        }

        public static void RebuildEffectDialogOnly()
        {
            YC.Editor.UiThemeBuildReadiness.InitializeRequiredTheme();
            EnsureFolder(DialogFolder);
            YC.Editor.EffectDialogLayoutControlledMetaGuid.ValidateAsset(
                EffectDialogShellPrefabPath,
                EffectDialogShellPrefabGuid);
            BuildEffectDialogShellPrefab(
                YC.Editor.EffectDialogLayoutEditorAssetBuilder.LoadRequiredEffectProfile(),
                LoadRequiredSharedUiVisuals());
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(EffectDialogShellPrefabPath, ImportAssetOptions.ForceUpdate);
        }

        internal static UiVisualAssetLibrary LoadRequiredSharedUiVisuals()
        {
            var library = AssetDatabase.LoadAssetAtPath<UiVisualAssetLibrary>(
                SharedUiVisualsAssetPath);
            if (library == null ||
                AssetDatabase.LoadMainAssetAtPath(SharedUiVisualsAssetPath) != library ||
                !EditorUtility.IsPersistent(library) ||
                !string.Equals(
                    AssetDatabase.AssetPathToGUID(SharedUiVisualsAssetPath),
                    SharedUiVisualsAssetGuid,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog requires the canonical persistent SharedUiVisuals asset.");
            }

            if (!library.TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog SharedUiVisuals configuration is invalid: " + reason);
            }

            ValidateSharedUiSprite(
                library.TriangleUp,
                "UI Triangle Up",
                TriangleUpSpriteLocalId);
            ValidateSharedUiSprite(
                library.TriangleDown,
                "UI Triangle Down",
                TriangleDownSpriteLocalId);
            if (library.TriangleUp == library.TriangleDown)
            {
                throw new InvalidOperationException(
                    "SharedUiVisuals up/down sprites must be distinct persistent sub-assets.");
            }

            return library;
        }

        private static void ValidateSharedUiSprite(
            Sprite sprite,
            string expectedName,
            long expectedLocalId)
        {
            if (sprite == null || sprite.name != expectedName ||
                !EditorUtility.IsPersistent(sprite) ||
                !string.Equals(
                    AssetDatabase.GetAssetPath(sprite),
                    SharedUiVisualsAssetPath,
                    StringComparison.Ordinal) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    sprite,
                    out var guid,
                    out long localId) ||
                !string.Equals(guid, SharedUiVisualsAssetGuid, StringComparison.OrdinalIgnoreCase) ||
                localId != expectedLocalId)
            {
                throw new InvalidOperationException(
                    "SharedUiVisuals sprite identity drifted: " + expectedName);
            }
        }

        private static void BuildEffectDialogShellPrefab(
            EffectDialogLayoutProfile layoutProfile,
            UiVisualAssetLibrary sharedVisuals)
        {
            var layoutReason = string.Empty;
            if (layoutProfile == null || !layoutProfile.TryValidateConfiguration(out layoutReason))
            {
                throw new InvalidOperationException(
                    "EffectDialogShell Prefab 缺少有效布局 Profile：" + layoutReason);
            }

            if (sharedVisuals == null)
            {
                throw new InvalidOperationException("EffectDialogShell Prefab 缺少共享 UI 图形资产。");
            }

            var root = CreateUiObject(
                "Effect Dialog Overlay",
                null,
                typeof(Canvas),
                typeof(GraphicRaycaster),
                typeof(Image));
            try
            {
                Stretch(root.GetComponent<RectTransform>());
                var overlayCanvas = root.GetComponent<Canvas>();
                overlayCanvas.overrideSorting = true;
                overlayCanvas.sortingOrder = 119;
                var overlayImage = root.GetComponent<Image>();
                overlayImage.color = layoutProfile.OverlayColor;
                overlayImage.raycastTarget = false;

                var panelObject = CreateUiObject(
                    "Effect Dialog Panel",
                    root.transform,
                    typeof(Image),
                    typeof(Outline),
                    typeof(EffectDialogCollapsiblePanel));
                var panel = panelObject.GetComponent<RectTransform>();
                layoutProfile.PanelLayout.ApplyTo(panel);
                panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
                var panelOutline = panelObject.GetComponent<Outline>();
                panelOutline.effectColor = UiTheme.GoldOutline;
                panelOutline.effectDistance = layoutProfile.PanelOutlineDistance;

                var expandedObject = CreateUiObject("Expanded Content", panel, Array.Empty<Type>());
                var expandedContent = expandedObject.GetComponent<RectTransform>();
                Stretch(expandedContent);

                var title = CreateText(expandedContent, "Title", string.Empty, 27, FontStyle.Bold,
                    TextAnchor.MiddleCenter);
                layoutProfile.TitleLayout.ApplyTo(title.rectTransform);
                title.raycastTarget = true;
                var dragHandle = title.gameObject.AddComponent<EffectDialogDragHandle>();
                var description = CreateText(expandedContent, "Description", string.Empty, 16, FontStyle.Normal,
                    TextAnchor.UpperLeft);
                layoutProfile.DescriptionLayout.ApplyTo(description.rectTransform);

                var collapsedSummary = CreateText(panel, "Collapsed Summary", string.Empty, 18,
                    FontStyle.Bold, TextAnchor.MiddleLeft);
                layoutProfile.CollapsedSummaryLayout.ApplyTo(collapsedSummary.rectTransform);
                collapsedSummary.gameObject.SetActive(false);

                var collapseButton = CreateButton(
                    panel,
                    "Collapse Toggle",
                    "收起卡片",
                    layoutProfile.ExpandedToggleLayout.SizeDelta,
                    layoutProfile.ExpandedToggleLayout.AnchoredPosition,
                    14);
                layoutProfile.ExpandedToggleLayout.ApplyTo(
                    collapseButton.GetComponent<RectTransform>());
                var collapseLabel = collapseButton.GetComponentInChildren<Text>();
                var collapseIconObject = CreateUiObject("Collapse Triangle", collapseButton.transform, typeof(Image));
                var collapseIconRect = collapseIconObject.GetComponent<RectTransform>();
                layoutProfile.CollapseIconLayout.ApplyTo(collapseIconRect);
                var collapseIcon = collapseIconObject.GetComponent<Image>();
                collapseIcon.sprite = sharedVisuals.TriangleUp;
                collapseIcon.color = UiTheme.GoldText;
                collapseIcon.preserveAspect = true;
                collapseIcon.raycastTarget = false;
                collapseButton.gameObject.SetActive(false);

                var optionScrollObject = CreateUiObject(
                    "Options Scroll",
                    expandedContent,
                    typeof(Image),
                    typeof(ScrollRect));
                var optionScrollRect = optionScrollObject.GetComponent<RectTransform>();
                layoutProfile.OptionScrollLayout.ApplyTo(optionScrollRect);
                optionScrollObject.GetComponent<Image>().color = UiTheme.ScrollBackground;
                var viewportObject = CreateUiObject("Viewport", optionScrollRect, typeof(Image), typeof(Mask));
                var viewport = viewportObject.GetComponent<RectTransform>();
                Stretch(viewport);
                viewportObject.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.01f);
                viewportObject.GetComponent<Mask>().showMaskGraphic = false;
                var contentObject = CreateUiObject(
                    "Content",
                    viewport,
                    typeof(VerticalLayoutGroup),
                    typeof(ContentSizeFitter));
                var optionContent = contentObject.GetComponent<RectTransform>();
                optionContent.anchorMin = new Vector2(0f, 1f);
                optionContent.anchorMax = new Vector2(1f, 1f);
                optionContent.pivot = new Vector2(0.5f, 1f);
                optionContent.sizeDelta = Vector2.zero;
                var layout = contentObject.GetComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(12, 12, 12, 12);
                layout.spacing = 9f;
                layout.childControlHeight = false;
                layout.childControlWidth = true;
                layout.childForceExpandHeight = false;
                layout.childForceExpandWidth = true;
                contentObject.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var optionScroll = optionScrollObject.GetComponent<ScrollRect>();
                optionScroll.viewport = viewport;
                optionScroll.content = optionContent;
                optionScroll.horizontal = false;
                optionScroll.vertical = true;
                optionScroll.movementType = ScrollRect.MovementType.Clamped;
                optionScroll.scrollSensitivity = 28f;
                var optionTemplate = BuildOptionTemplate(optionContent);
                optionScrollObject.SetActive(false);

                var resourceTemplate = BuildResourceRowTemplate(expandedContent, layoutProfile);
                var resourceSummary = CreateText(expandedContent, "Resource Summary", string.Empty, 18,
                    FontStyle.Bold, TextAnchor.MiddleCenter);
                layoutProfile.ResourceSummaryLayout.ApplyTo(resourceSummary.rectTransform);
                resourceSummary.gameObject.SetActive(false);

                var actionButtons = new EffectDialogActionButtonView[3];
                for (var i = 0; i < actionButtons.Length; i++)
                {
                    actionButtons[i] = BuildActionButtonTemplate(
                        expandedContent,
                        "Action Button Slot " + (i + 1),
                        layoutProfile);
                }

                var facilityCardTemplate = BuildFacilityCardTemplate(expandedContent, layoutProfile);
                var collapsiblePanel = panelObject.GetComponent<EffectDialogCollapsiblePanel>();
                SetReferences(
                    collapsiblePanel,
                    ("triangleUpSprite", sharedVisuals.TriangleUp),
                    ("triangleDownSprite", sharedVisuals.TriangleDown));

                var view = root.AddComponent<EffectDialogShellView>();
                SetReferences(
                    view,
                    ("layoutProfile", layoutProfile),
                    ("overlayCanvas", overlayCanvas),
                    ("overlayImage", overlayImage),
                    ("panel", panel),
                    ("expandedContent", expandedContent),
                    ("titleText", title),
                    ("descriptionText", description),
                    ("dragHandle", dragHandle),
                    ("collapsedSummaryText", collapsedSummary),
                    ("collapseButton", collapseButton),
                    ("collapseButtonText", collapseLabel),
                    ("collapseButtonIcon", collapseIcon),
                    ("collapsiblePanel", collapsiblePanel),
                    ("optionScroll", optionScroll),
                    ("optionContent", optionContent),
                    ("optionRowTemplate", optionTemplate),
                    ("resourceRowTemplate", resourceTemplate),
                    ("resourceSummaryText", resourceSummary),
                    ("facilityCardTemplate", facilityCardTemplate));
                SetObjectArray(view, "actionButtons", actionButtons);

                if (!view.TryValidateConfiguration(out var reason))
                {
                    throw new InvalidOperationException(reason);
                }

                PrefabUtility.SaveAsPrefabAsset(root, EffectDialogShellPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static EffectDialogOptionRowView BuildOptionTemplate(RectTransform parent)
        {
            var button = CreateButton(parent, "Option Row Template", string.Empty,
                new Vector2(0f, 54f), Vector2.zero, 18);
            var layout = button.gameObject.AddComponent<LayoutElement>();
            layout.preferredHeight = 54f;
            var view = button.gameObject.AddComponent<EffectDialogOptionRowView>();
            SetReferences(
                view,
                ("button", button),
                ("background", button.GetComponent<Image>()),
                ("label", button.GetComponentInChildren<Text>()),
                ("layoutElement", layout));
            view.gameObject.SetActive(false);
            return view;
        }

        private static EffectDialogResourceRowView BuildResourceRowTemplate(
            RectTransform parent,
            EffectDialogLayoutProfile layoutProfile)
        {
            var root = CreateUiObject("Resource Row Template", parent, Array.Empty<Type>());
            Stretch(root.GetComponent<RectTransform>());
            var label = CreateText(root.transform, "Resource Label", string.Empty, 18, FontStyle.Normal,
                TextAnchor.MiddleLeft);
            var decrease = CreateButton(
                root.transform,
                "Decrease",
                "−",
                layoutProfile.ResourceDecreaseButtonSize,
                Vector2.zero,
                24);
            var value = CreateText(root.transform, "Value", "0", 22, FontStyle.Normal,
                TextAnchor.MiddleCenter);
            var increase = CreateButton(
                root.transform,
                "Increase",
                "+",
                layoutProfile.ResourceIncreaseButtonSize,
                Vector2.zero,
                24);
            var view = root.AddComponent<EffectDialogResourceRowView>();
            SetReferences(
                view,
                ("label", label),
                ("decreaseButton", decrease),
                ("decreaseLabel", decrease.GetComponentInChildren<Text>()),
                ("valueText", value),
                ("increaseButton", increase),
                ("increaseLabel", increase.GetComponentInChildren<Text>()));
            root.SetActive(false);
            return view;
        }

        private static EffectDialogActionButtonView BuildActionButtonTemplate(
            RectTransform parent,
            string name,
            EffectDialogLayoutProfile layoutProfile)
        {
            var button = CreateButton(
                parent,
                name,
                string.Empty,
                layoutProfile.ActionButtonSize,
                Vector2.zero,
                18);
            var view = button.gameObject.AddComponent<EffectDialogActionButtonView>();
            SetReferences(
                view,
                ("button", button),
                ("background", button.GetComponent<Image>()),
                ("label", button.GetComponentInChildren<Text>()));
            view.gameObject.SetActive(false);
            return view;
        }

        private static FacilityEffectCardView BuildFacilityCardTemplate(
            RectTransform parent,
            EffectDialogLayoutProfile layoutProfile)
        {
            var cardObject = CreateUiObject(
                "Facility Card Template",
                parent,
                typeof(Image),
                typeof(Button),
                typeof(Outline),
                typeof(CanvasGroup),
                typeof(CardPointerInteraction));
            var cardRect = cardObject.GetComponent<RectTransform>();
            SetTopAnchored(cardRect, layoutProfile.ExtensionHubCardSize, Vector2.zero);
            cardObject.GetComponent<Image>().color = UiTheme.ScrollBackground;
            var outline = cardObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = layoutProfile.FacilityCardOutlineDistance;
            var imageObject = CreateUiObject("Card Image", cardRect, typeof(RawImage));
            var imageRect = imageObject.GetComponent<RectTransform>();
            layoutProfile.FacilityCardImageLayout.ApplyTo(imageRect);
            var rawImage = imageObject.GetComponent<RawImage>();
            rawImage.raycastTarget = false;
            var fallback = CreateText(cardRect, "Facility Card Template Label", string.Empty, 18, FontStyle.Bold,
                TextAnchor.MiddleCenter);
            layoutProfile.FacilityCardFallbackLayout.ApplyTo(fallback.rectTransform);
            var view = cardObject.AddComponent<FacilityEffectCardView>();
            SetReferences(
                view,
                ("cardRect", cardRect),
                ("background", cardObject.GetComponent<Image>()),
                ("button", cardObject.GetComponent<Button>()),
                ("outline", outline),
                ("canvasGroup", cardObject.GetComponent<CanvasGroup>()),
                ("cardImage", rawImage),
                ("fallbackLabel", fallback),
                ("pointerInteraction", cardObject.GetComponent<CardPointerInteraction>()));
            cardObject.SetActive(false);
            return view;
        }

        private static void BuildDispatchDecisionPrefab()
        {
            var root = CreateUiObject("Dispatch Decision Overlay", null, typeof(Image));
            try
            {
                Stretch(root.GetComponent<RectTransform>());
                var overlayImage = root.GetComponent<Image>();
                overlayImage.color = new Color(0f, 0f, 0f, 0.35f);

                var panelObject = CreateUiObject(
                    "Dispatch Decision Panel",
                    root.transform,
                    typeof(Image),
                    typeof(Outline));
                var panel = panelObject.GetComponent<RectTransform>();
                SetRect(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(520f, 190f), new Vector2(0f, -20f));
                panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
                var panelOutline = panelObject.GetComponent<Outline>();
                panelOutline.effectColor = UiTheme.GoldOutline;
                panelOutline.effectDistance = new Vector2(3f, -3f);

                var title = CreateText(panel, "Dispatch Decision Title", string.Empty, 20, FontStyle.Bold,
                    TextAnchor.MiddleCenter);
                SetTopAnchored(title.rectTransform, new Vector2(460f, 30f), new Vector2(0f, -42f));
                var message = CreateText(panel, "Dispatch Decision Message", string.Empty, 15, FontStyle.Normal,
                    TextAnchor.MiddleCenter);
                SetTopAnchored(message.rectTransform, new Vector2(460f, 30f), new Vector2(0f, -78f));
                var continueButton = CreateButton(panel, "Continue Dispatch", string.Empty,
                    new Vector2(170f, 44f), new Vector2(-120f, -130f), 16);
                var finishButton = CreateButton(panel, "Finish Dispatch", string.Empty,
                    new Vector2(170f, 44f), new Vector2(120f, -130f), 16);

                var view = root.AddComponent<DispatchDecisionDialogView>();
                SetReferences(
                    view,
                    ("overlayRect", root.GetComponent<RectTransform>()),
                    ("overlayImage", overlayImage),
                    ("panel", panel),
                    ("titleText", title),
                    ("messageText", message),
                    ("continueButton", continueButton),
                    ("continueLabel", continueButton.GetComponentInChildren<Text>()),
                    ("finishButton", finishButton),
                    ("finishLabel", finishButton.GetComponentInChildren<Text>()));

                if (!view.TryValidateConfiguration(out var reason))
                {
                    throw new InvalidOperationException(reason);
                }

                PrefabUtility.SaveAsPrefabAsset(root, DispatchDecisionPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void BuildEventChoiceDialogPrefab()
        {
            EnsureControlledMetaGuid(EventChoiceDialogPrefabPath, EventChoiceDialogPrefabGuid);
            var layoutProfile = YC.Editor.EventChoiceDialogLayoutEditorAssetBuilder.LoadRequiredProfile();
            var sharedVisuals = LoadRequiredSharedUiVisuals();
            var resourcePointRibbonSprite = AssetDatabase.LoadAssetAtPath<Sprite>(
                EventResourcePointRibbonSpritePath);
            if (resourcePointRibbonSprite == null)
            {
                throw new InvalidOperationException(
                    "Missing event resource point ribbon sprite: " +
                    EventResourcePointRibbonSpritePath);
            }
            var root = CreateUiObject(
                "Event Choice Overlay",
                null,
                typeof(Canvas),
                typeof(GraphicRaycaster),
                typeof(Image),
                typeof(WindowCloseInputHandler));
            try
            {
                Stretch(root.GetComponent<RectTransform>());
                var overlayCanvas = root.GetComponent<Canvas>();
                overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                overlayCanvas.targetDisplay = 0;
                overlayCanvas.overrideSorting = true;
                overlayCanvas.sortingOrder = 118;
                var overlayImage = root.GetComponent<Image>();
                overlayImage.color = new Color(0f, 0f, 0f, layoutProfile.OverlayAlpha);
                overlayImage.raycastTarget = layoutProfile.EventOverlayRaycastTarget;

                var panelObject = CreateUiObject(
                    "Event Choice Panel",
                    root.transform,
                    typeof(Image),
                    typeof(Outline),
                    typeof(EffectDialogCollapsiblePanel));
                var panel = panelObject.GetComponent<RectTransform>();
                layoutProfile.PanelLayout.ApplyTo(panel);
                panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
                var panelOutline = panelObject.GetComponent<Outline>();
                panelOutline.effectColor = UiTheme.GoldOutline;
                panelOutline.effectDistance = layoutProfile.PanelOutlineDistance;

                var expandedObject = CreateUiObject("Expanded Content", panel, Array.Empty<Type>());
                var expandedContent = expandedObject.GetComponent<RectTransform>();
                Stretch(expandedContent);
                var title = CreateProfileText(
                    expandedContent,
                    "Title",
                    string.Empty,
                    layoutProfile.EventTitleLayout,
                    layoutProfile.EventTitleTextStyle,
                    UiTheme.GoldText);
                var dragHandle = title.gameObject.AddComponent<EffectDialogDragHandle>();
                var metadata = CreateProfileText(
                    expandedContent,
                    "Metadata",
                    string.Empty,
                    layoutProfile.EventMetadataLayout,
                    layoutProfile.EventMetadataTextStyle,
                    UiTheme.LabelText);
                var description = CreateProfileText(
                    expandedContent,
                    "Description",
                    string.Empty,
                    layoutProfile.EventDescriptionLayout,
                    layoutProfile.EventDescriptionTextStyle,
                    UiTheme.ValueText);

                var actionObject = CreateUiObject("Action Area", expandedContent, Array.Empty<Type>());
                var actionArea = actionObject.GetComponent<RectTransform>();
                Stretch(actionArea);

                var closeButton = CreateProfileButton(
                    panel,
                    "Close",
                    "×",
                    layoutProfile.BuildCloseButtonLayout,
                    layoutProfile.BuildCloseButtonStyle);
                var collapseButton = CreateProfileButton(
                    panel,
                    "Collapse",
                    "收起卡片",
                    layoutProfile.CollapseButtonLayout,
                    layoutProfile.CollapseButtonStyle);
                var collapseIconObject = CreateUiObject("Collapse Triangle", collapseButton.transform, typeof(Image));
                var collapseIconRect = collapseIconObject.GetComponent<RectTransform>();
                layoutProfile.CollapseIconLayout.ApplyTo(collapseIconRect);
                var collapseIcon = collapseIconObject.GetComponent<Image>();
                collapseIcon.sprite = sharedVisuals.TriangleUp;
                collapseIcon.color = UiTheme.GoldText;
                collapseIcon.preserveAspect = true;
                collapseIcon.raycastTarget = false;
                var collapsedSummary = CreateProfileText(
                    panel,
                    "Collapsed Summary",
                    string.Empty,
                    layoutProfile.CollapsedSummaryLayout,
                    layoutProfile.CollapsedSummaryTextStyle,
                    UiTheme.GoldText);
                collapsedSummary.gameObject.SetActive(false);
                closeButton.gameObject.SetActive(false);
                collapseButton.gameObject.SetActive(false);

                var eventCardMode = CreateModeBlock("EventCard Mode", actionArea);
                var eventArtworkObject = CreateUiObject(
                    "Event Card Artwork",
                    eventCardMode.transform,
                    typeof(RawImage));
                var eventArtwork = eventArtworkObject.GetComponent<RawImage>();
                Stretch(eventArtwork.rectTransform);
                eventArtwork.color = Color.white;
                eventArtwork.raycastTarget = false;
                eventArtworkObject.SetActive(false);

                var metadataRibbon = CreateUiObject(
                    "Resource Point Ribbon",
                    eventCardMode.transform,
                    typeof(Image));
                var metadataRibbonRect = metadataRibbon.GetComponent<RectTransform>();
                metadataRibbonRect.anchorMin = new Vector2(0f, 1f);
                metadataRibbonRect.anchorMax = new Vector2(0f, 1f);
                metadataRibbonRect.pivot = new Vector2(0f, 1f);
                metadataRibbonRect.sizeDelta = new Vector2(380f, 42f);
                metadataRibbonRect.anchoredPosition = new Vector2(56f, 0f);
                var metadataRibbonImage = metadataRibbon.GetComponent<Image>();
                metadataRibbonImage.sprite = resourcePointRibbonSprite;
                metadataRibbonImage.type = Image.Type.Simple;
                metadataRibbonImage.preserveAspect = false;
                metadataRibbonImage.color = new Color(0.31f, 0.62f, 0.2f, 0.98f);
                metadataRibbonImage.raycastTarget = false;

                var metadataRibbonLabel = CreateText(
                    metadataRibbonRect,
                    "Resource Point Label",
                    string.Empty,
                    22,
                    FontStyle.Normal,
                    TextAnchor.MiddleLeft);
                Stretch(metadataRibbonLabel.rectTransform);
                metadataRibbonLabel.rectTransform.offsetMin = new Vector2(16f, 0f);
                metadataRibbonLabel.rectTransform.offsetMax = new Vector2(-4f, 0f);
                metadataRibbonLabel.color = Color.white;
                metadataRibbonLabel.resizeTextMinSize = 14;
                metadataRibbonLabel.resizeTextMaxSize = 22;
                metadataRibbon.SetActive(false);

                var eventPaymentHost = CreateStretchedHost(
                    eventCardMode.transform,
                    "Event Payment Route Host");
                var eventChoiceHost = CreateStretchedHost(
                    eventCardMode.transform,
                    "Event Choice Host");

                var explorePathMode = CreateModeBlock("ExplorePath Mode", actionArea);
                var explorePathHost = CreateStretchedHost(
                    explorePathMode.transform,
                    "Explore Path Host");

                var explorePaymentMode = CreateModeBlock("ExplorePayment Mode", actionArea);
                var explorePaymentHost = CreateStretchedHost(
                    explorePaymentMode.transform,
                    "Explore Payment Route Host");
                var exploreConfirm = CreateProfileButton(
                    explorePaymentMode.transform,
                    "Confirm Explore",
                    "支付过路费并探索",
                    layoutProfile.ExploreConfirmTemplateLayout,
                    layoutProfile.ExploreConfirmButtonStyle);

                var resourcePaymentMode = CreateModeBlock("ResourceCollectionPayment Mode", actionArea);
                var resourceReceiver = CreateProfileText(
                    resourcePaymentMode.transform,
                    "Receiver",
                    string.Empty,
                    layoutProfile.ResourceReceiverLayout,
                    layoutProfile.ResourceReceiverTextStyle,
                    UiTheme.ValueText);
                var resourceRecipientHost = CreateStretchedHost(
                    resourcePaymentMode.transform,
                    "Resource Collection Recipient Host");
                var payBank = CreateProfileButton(
                    resourcePaymentMode.transform,
                    "Pay Bank",
                    string.Empty,
                    layoutProfile.ResourceBankButtonLayout,
                    layoutProfile.ResourceBankButtonStyle);
                payBank.gameObject.SetActive(false);

                var buildFocusMode = CreateModeBlock("BuildFacilityFocus Mode", actionArea);
                var previewContainer = CreateUiObject("Facility Card Preview", buildFocusMode.transform,
                    typeof(Image));
                var previewContainerRect = previewContainer.GetComponent<RectTransform>();
                layoutProfile.FacilityPreviewContainerLayout.ApplyTo(previewContainerRect);
                previewContainer.GetComponent<Image>().color = new Color(0.03f, 0.025f, 0.02f, 0.96f);
                var previewObject = CreateUiObject("Facility Card Image", previewContainerRect,
                    typeof(RawImage), typeof(AspectRatioFitter));
                var previewImage = previewObject.GetComponent<RawImage>();
                Stretch(previewImage.rectTransform);
                previewImage.raycastTarget = false;
                var previewAspect = previewObject.GetComponent<AspectRatioFitter>();
                previewAspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                var previewFallback = CreateText(previewContainerRect, "Facility Preview Fallback", string.Empty,
                    18, FontStyle.Bold, TextAnchor.MiddleCenter);
                Stretch(previewFallback.rectTransform);
                previewObject.SetActive(false);
                previewFallback.gameObject.SetActive(false);
                var buildDetails = CreateProfileText(
                    buildFocusMode.transform,
                    "Build Details",
                    string.Empty,
                    layoutProfile.BuildFocusDetailsLayout,
                    layoutProfile.BuildFocusDetailsTextStyle,
                    UiTheme.ValueText);
                var resourceButton = CreateProfileButton(
                    buildFocusMode.transform,
                    "Choose Resource Payment",
                    "资源支付",
                    layoutProfile.BuildResourceButtonLayout,
                    layoutProfile.BuildResourceButtonStyle);
                var goldButton = CreateProfileButton(
                    buildFocusMode.transform,
                    "Choose Gold Payment",
                    "金券支付",
                    layoutProfile.BuildGoldButtonLayout,
                    layoutProfile.BuildGoldButtonStyle);
                var resourceReason = CreateProfileText(
                    buildFocusMode.transform,
                    "Resource Payment Reason",
                    string.Empty,
                    layoutProfile.BuildResourceReasonLayout,
                    layoutProfile.BuildResourceReasonTextStyle,
                    UiTheme.LabelText);
                var goldReason = CreateProfileText(
                    buildFocusMode.transform,
                    "Gold Payment Reason",
                    string.Empty,
                    layoutProfile.BuildGoldReasonLayout,
                    layoutProfile.BuildGoldReasonTextStyle,
                    UiTheme.LabelText);
                var focusError = CreateProfileText(
                    buildFocusMode.transform,
                    "Build Error",
                    string.Empty,
                    layoutProfile.BuildFocusErrorLayout,
                    layoutProfile.BuildFocusErrorTextStyle,
                    new Color(1f, 0.45f, 0.32f));

                var buildConfirmationMode = CreateModeBlock("BuildFacilityConfirmation Mode", actionArea);
                var confirmationSummary = CreateProfileText(
                    buildConfirmationMode.transform,
                    "Summary",
                    string.Empty,
                    layoutProfile.BuildConfirmationSummaryLayout,
                    layoutProfile.BuildConfirmationSummaryTextStyle,
                    UiTheme.ValueText);
                var confirmationError = CreateProfileText(
                    buildConfirmationMode.transform,
                    "Build Error",
                    string.Empty,
                    layoutProfile.BuildConfirmationErrorLayout,
                    layoutProfile.BuildConfirmationErrorTextStyle,
                    new Color(1f, 0.45f, 0.32f));
                var backButton = CreateProfileButton(
                    buildConfirmationMode.transform,
                    "Back To Build Payment",
                    "返回修改",
                    layoutProfile.BuildBackButtonLayout,
                    layoutProfile.BuildBackButtonStyle);
                var confirmBuildButton = CreateProfileButton(
                    buildConfirmationMode.transform,
                    "Confirm Build Facility",
                    "确认建设",
                    layoutProfile.BuildConfirmButtonLayout,
                    layoutProfile.BuildConfirmButtonStyle);

                var legacyMode = CreateModeBlock("LegacyCityStyleOptions Mode", actionArea);
                var legacyHost = CreateStretchedHost(
                    legacyMode.transform,
                    "Legacy City Style Host");
                var legacyEmpty = CreateProfileText(
                    legacyMode.transform,
                    "Empty",
                    "当前没有城市样式牌。",
                    layoutProfile.LegacyCityStyleEmptyLayout,
                    layoutProfile.LegacyCityStyleEmptyTextStyle,
                    UiTheme.ValueText);
                legacyEmpty.gameObject.SetActive(false);

                var characterMode = CreateModeBlock("CharacterSecondEffectDecision Mode", actionArea);
                var continueButton = CreateProfileButton(
                    characterMode.transform,
                    "Continue Character Second Effect",
                    string.Empty,
                    layoutProfile.CharacterContinueButtonLayout,
                    layoutProfile.CharacterContinueButtonStyle);
                var finishButton = CreateProfileButton(
                    characterMode.transform,
                    "Finish Character Use",
                    "不发动，结束使用",
                    layoutProfile.CharacterFinishButtonLayout,
                    layoutProfile.CharacterFinishButtonStyle);

                var templateHostObject = CreateUiObject("Dynamic Templates", panel, Array.Empty<Type>());
                var templateHost = templateHostObject.GetComponent<RectTransform>();
                Stretch(templateHost);
                var choiceTemplate = BuildSimpleButtonRowTemplate(
                    templateHost,
                    "ChoiceRow",
                    layoutProfile.ChoiceRowTemplateLayout,
                    layoutProfile.ChoiceRowButtonStyle,
                    true);
                var pathTemplate = BuildSimpleButtonRowTemplate(
                    templateHost,
                    "PathRow",
                    layoutProfile.PathRowTemplateLayout,
                    layoutProfile.PathRowButtonStyle);
                var paymentRouteTemplate = BuildPaymentRouteRowTemplate(
                    templateHost,
                    layoutProfile);
                var recipientTemplate = BuildSimpleButtonRowTemplate(
                    templateHost,
                    "PaymentRecipientButton",
                    layoutProfile.PaymentRecipientTemplateLayout,
                    layoutProfile.PaymentRecipientButtonStyle);
                var resourceRecipientTemplate = BuildSimpleButtonRowTemplate(
                    templateHost,
                    "ResourceCollectionRecipientButton",
                    layoutProfile.ResourceRecipientTemplateLayout,
                    layoutProfile.ResourceRecipientButtonStyle);
                var legacyTemplate = BuildLegacyCityStyleRowTemplate(
                    templateHost,
                    layoutProfile);

                var collapsiblePanel = panelObject.GetComponent<EffectDialogCollapsiblePanel>();
                SetReferences(
                    collapsiblePanel,
                    ("triangleUpSprite", sharedVisuals.TriangleUp),
                    ("triangleDownSprite", sharedVisuals.TriangleDown));

                var view = root.AddComponent<EventChoiceDialogView>();
                SetReferences(
                    view,
                    ("overlayCanvas", overlayCanvas),
                    ("overlayRect", root.GetComponent<RectTransform>()),
                    ("overlayImage", overlayImage),
                    ("panel", panel),
                    ("expandedContent", expandedContent),
                    ("titleText", title),
                    ("metadataText", metadata),
                    ("descriptionText", description),
                    ("collapsedSummaryText", collapsedSummary),
                    ("actionArea", actionArea),
                    ("closeButton", closeButton),
                    ("closeButtonLabel", closeButton.GetComponentInChildren<Text>()),
                    ("collapseButton", collapseButton),
                    ("collapseButtonLabel", collapseButton.GetComponentInChildren<Text>()),
                    ("collapseButtonIcon", collapseIcon),
                     ("collapsiblePanel", collapsiblePanel),
                     ("dragHandle", dragHandle),
                     ("closeInputHandler", root.GetComponent<WindowCloseInputHandler>()),
                     ("layoutProfile", layoutProfile),
                    ("eventCardMode", eventCardMode),
                    ("explorePathMode", explorePathMode),
                    ("explorePaymentMode", explorePaymentMode),
                    ("resourceCollectionPaymentMode", resourcePaymentMode),
                    ("buildFacilityFocusMode", buildFocusMode),
                    ("buildFacilityConfirmationMode", buildConfirmationMode),
                    ("legacyCityStyleOptionsMode", legacyMode),
                     ("characterSecondEffectDecisionMode", characterMode),
                    ("eventCardArtworkImage", eventArtwork),
                    ("eventCardMetadataRibbon", metadataRibbon),
                    ("eventCardMetadataRibbonImage", metadataRibbonImage),
                    ("eventCardMetadataRibbonLabel", metadataRibbonLabel),
                    ("eventChoiceHost", eventChoiceHost),
                    ("eventPaymentRouteHost", eventPaymentHost),
                    ("explorePathHost", explorePathHost),
                    ("explorePaymentRouteHost", explorePaymentHost),
                    ("exploreConfirmButton", exploreConfirm),
                    ("exploreConfirmLabel", exploreConfirm.GetComponentInChildren<Text>()),
                    ("resourcePaymentReceiverText", resourceReceiver),
                    ("resourcePaymentRecipientHost", resourceRecipientHost),
                    ("resourcePaymentBankButton", payBank),
                    ("resourcePaymentBankLabel", payBank.GetComponentInChildren<Text>()),
                    ("facilityPreviewImage", previewImage),
                    ("facilityPreviewAspect", previewAspect),
                    ("facilityPreviewFallback", previewFallback),
                    ("buildFocusDetailsText", buildDetails),
                    ("buildResourceButton", resourceButton),
                    ("buildResourceLabel", resourceButton.GetComponentInChildren<Text>()),
                    ("buildResourceReasonText", resourceReason),
                    ("buildGoldButton", goldButton),
                    ("buildGoldLabel", goldButton.GetComponentInChildren<Text>()),
                    ("buildGoldReasonText", goldReason),
                    ("buildFocusErrorText", focusError),
                    ("buildConfirmationSummaryText", confirmationSummary),
                    ("buildConfirmationErrorText", confirmationError),
                    ("buildBackButton", backButton),
                    ("buildBackLabel", backButton.GetComponentInChildren<Text>()),
                    ("buildConfirmButton", confirmBuildButton),
                    ("buildConfirmLabel", confirmBuildButton.GetComponentInChildren<Text>()),
                    ("legacyCityStyleHost", legacyHost),
                    ("legacyCityStyleEmptyText", legacyEmpty),
                    ("characterContinueButton", continueButton),
                    ("characterContinueLabel", continueButton.GetComponentInChildren<Text>()),
                    ("characterFinishButton", finishButton),
                    ("characterFinishLabel", finishButton.GetComponentInChildren<Text>()));
                SetNestedReferences(view, "choiceRowTemplate", choiceTemplate);
                SetNestedReferences(view, "pathRowTemplate", pathTemplate);
                SetNestedReferences(view, "paymentRouteRowTemplate", paymentRouteTemplate);
                SetNestedReferences(view, "paymentRecipientButtonTemplate", recipientTemplate);
                SetNestedReferences(view, "resourceCollectionRecipientButtonTemplate", resourceRecipientTemplate);
                SetNestedReferences(view, "legacyCityStyleRowTemplate", legacyTemplate);

                if (!view.TryValidateConfiguration(out var reason))
                {
                    throw new InvalidOperationException(reason);
                }

                PrefabUtility.SaveAsPrefabAsset(root, EventChoiceDialogPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static GameObject CreateModeBlock(string name, RectTransform parent)
        {
            var block = CreateUiObject(name, parent, Array.Empty<Type>());
            Stretch(block.GetComponent<RectTransform>());
            block.SetActive(false);
            return block;
        }

        private static RectTransform CreateHost(Transform parent, string name, Vector2 size, Vector2 position)
        {
            var host = CreateUiObject(name, parent, Array.Empty<Type>()).GetComponent<RectTransform>();
            SetTopAnchored(host, size, position);
            return host;
        }

        private static RectTransform CreateStretchedHost(Transform parent, string name)
        {
            var host = CreateUiObject(name, parent, Array.Empty<Type>()).GetComponent<RectTransform>();
            Stretch(host);
            return host;
        }

        private static (string property, Object value)[] BuildSimpleButtonRowTemplate(
            RectTransform parent,
            string name,
            EventChoiceDialogRectLayout layout,
            EventChoiceDialogButtonStyle style,
            bool includeActionFeedback = false)
        {
            var button = CreateProfileButton(parent, name, string.Empty, layout, style);
            if (includeActionFeedback)
            {
                var feedback = button.gameObject.AddComponent<ActionButtonPressFeedback>();
                feedback.Configure(
                    button,
                    null,
                    BuildChoiceHoverBorder(button.transform),
                    UiTheme.CyanAccent);
            }
            button.gameObject.SetActive(false);
            return new[]
            {
                ("root", (Object)button.GetComponent<RectTransform>()),
                ("button", (Object)button),
                ("label", (Object)button.GetComponentInChildren<Text>())
            };
        }

        private static Graphic[] BuildChoiceHoverBorder(Transform parent)
        {
            var borderRoot = CreateUiObject("Hover Border", parent, Array.Empty<Type>())
                .GetComponent<RectTransform>();
            Stretch(borderRoot);
            var top = CreateChoiceHoverBorderEdge(
                borderRoot,
                "Top",
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0.5f, 1f),
                new Vector2(0f, 3f));
            var bottom = CreateChoiceHoverBorderEdge(
                borderRoot,
                "Bottom",
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(0f, 3f));
            var left = CreateChoiceHoverBorderEdge(
                borderRoot,
                "Left",
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(0f, 0.5f),
                new Vector2(3f, 0f));
            var right = CreateChoiceHoverBorderEdge(
                borderRoot,
                "Right",
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0.5f),
                new Vector2(3f, 0f));
            return new Graphic[] { top, bottom, left, right };
        }

        private static Image CreateChoiceHoverBorderEdge(
            RectTransform parent,
            string name,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 sizeDelta)
        {
            var edge = CreateUiObject(name, parent, typeof(Image)).GetComponent<Image>();
            var rect = edge.rectTransform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.sizeDelta = sizeDelta;
            rect.anchoredPosition = Vector2.zero;
            edge.color = Color.clear;
            edge.raycastTarget = false;
            return edge;
        }

        private static (string property, Object value)[] BuildPaymentRouteRowTemplate(
            RectTransform parent,
            EventChoiceDialogLayoutProfile profile)
        {
            var root = CreateUiObject("PaymentRouteRow", parent, Array.Empty<Type>());
            var rect = root.GetComponent<RectTransform>();
            profile.PaymentRouteTemplateLayout.ApplyTo(rect);
            var label = CreateProfileText(
                rect,
                "Route Label",
                string.Empty,
                profile.PaymentRouteLabelLayout,
                profile.PaymentRouteLabelTextStyle,
                UiTheme.ValueText);
            var recipientHost = CreateStretchedHost(rect, "Recipient Host");
            root.SetActive(false);
            return new[]
            {
                ("root", (Object)rect),
                ("label", (Object)label),
                ("recipientHost", (Object)recipientHost)
            };
        }

        private static (string property, Object value)[] BuildLegacyCityStyleRowTemplate(
            RectTransform parent,
            EventChoiceDialogLayoutProfile profile)
        {
            var root = CreateUiObject("LegacyCityStyleRow", parent, Array.Empty<Type>());
            var rect = root.GetComponent<RectTransform>();
            profile.LegacyCityStyleTemplateLayout.ApplyTo(rect);
            var summary = CreateProfileText(
                rect,
                "Summary",
                string.Empty,
                profile.LegacyCityStyleSummaryLayout,
                profile.LegacyCityStyleSummaryTextStyle,
                UiTheme.ValueText);
            var reason = CreateProfileText(
                rect,
                "Reason",
                string.Empty,
                profile.LegacyCityStyleReasonLayout,
                profile.LegacyCityStyleReasonTextStyle,
                UiTheme.ValueText);
            var declare = CreateProfileButton(
                rect,
                "Declare",
                string.Empty,
                profile.LegacyCityStyleDeclareLayout,
                profile.LegacyCityStyleDeclareButtonStyle);
            declare.GetComponentInChildren<Text>().gameObject.name = "Declare Label";
            root.SetActive(false);
            return new[]
            {
                ("root", (Object)rect),
                ("summary", (Object)summary),
                ("reason", (Object)reason),
                ("declareButton", (Object)declare),
                ("declareLabel", (Object)declare.GetComponentInChildren<Text>())
            };
        }

        private static GameObject CreateUiObject(string name, Transform parent, params Type[] components)
        {
            var types = new Type[components.Length + 1];
            types[0] = typeof(RectTransform);
            Array.Copy(components, 0, types, 1, components.Length);
            var result = new GameObject(name, types);
            result.layer = LayerMask.NameToLayer("UI");
            if (parent != null)
            {
                result.transform.SetParent(parent, false);
            }

            return result;
        }

        private static Text CreateProfileText(
            Transform parent,
            string name,
            string value,
            EventChoiceDialogRectLayout layout,
            EventChoiceDialogTextStyle style,
            Color color)
        {
            var text = CreateText(parent, name, value, style.FontSize, style.FontStyle, style.Alignment);
            layout.ApplyTo(text.rectTransform);
            text.color = color;
            ApplyTextStyle(text, style);
            return text;
        }

        private static Button CreateProfileButton(
            Transform parent,
            string name,
            string label,
            EventChoiceDialogRectLayout layout,
            EventChoiceDialogButtonStyle style)
        {
            var buttonObject = CreateUiObject(name, parent, typeof(Image), typeof(Button), typeof(Outline));
            layout.ApplyTo(buttonObject.GetComponent<RectTransform>());
            var image = buttonObject.GetComponent<Image>();
            image.color = UiTheme.ButtonBackground;
            var button = buttonObject.GetComponent<Button>();
            button.enabled = true;
            button.interactable = true;
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = style.OutlineDistance;

            var labelObject = CreateUiObject("Label", buttonObject.transform, typeof(Text));
            var labelText = labelObject.GetComponent<Text>();
            labelText.text = label ?? string.Empty;
            labelText.font = UiEditorAssetReferences.CjkFont;
            labelText.color = UiTheme.GoldText;
            ApplyTextStyle(labelText, style.LabelStyle);
            var labelRect = labelText.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = style.LabelOffsetMin;
            labelRect.offsetMax = style.LabelOffsetMax;
            if (style.LabelHasOutline)
            {
                var labelOutline = labelObject.AddComponent<Outline>();
                labelOutline.effectColor = UiTheme.DarkShadowLight;
                labelOutline.effectDistance = style.LabelOutlineDistance;
            }

            return button;
        }

        private static void ApplyTextStyle(Text text, EventChoiceDialogTextStyle style)
        {
            text.fontSize = style.FontSize;
            text.fontStyle = style.FontStyle;
            text.alignment = style.Alignment;
            text.horizontalOverflow = style.HorizontalOverflow;
            text.verticalOverflow = style.VerticalOverflow;
            text.resizeTextForBestFit = style.ResizeTextForBestFit;
            text.resizeTextMinSize = style.ResizeMinSize;
            text.resizeTextMaxSize = style.ResizeMaxSize;
            text.raycastTarget = style.RaycastTarget;
        }

        private static Text CreateText(
            Transform parent,
            string name,
            string value,
            int fontSize,
            FontStyle fontStyle,
            TextAnchor alignment)
        {
            var textObject = CreateUiObject(name, parent, typeof(Text));
            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.font = UiEditorAssetReferences.CjkFont;
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.color = UiTheme.GoldText;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 12;
            text.resizeTextMaxSize = fontSize;
            text.raycastTarget = false;
            return text;
        }

        private static Button CreateButton(
            Transform parent,
            string name,
            string label,
            Vector2 size,
            Vector2 position,
            int fontSize)
        {
            var buttonObject = CreateUiObject(name, parent, typeof(Image), typeof(Button), typeof(Outline));
            SetTopAnchored(buttonObject.GetComponent<RectTransform>(), size, position);
            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);
            var labelText = CreateText(buttonObject.transform, "Label", label, fontSize, FontStyle.Bold,
                TextAnchor.MiddleCenter);
            Stretch(labelText.rectTransform);
            labelText.rectTransform.offsetMin = new Vector2(10f, 0f);
            labelText.rectTransform.offsetMax = new Vector2(-10f, 0f);
            return buttonObject.GetComponent<Button>();
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

        private static void SetNestedReferences(
            Object target,
            string containerName,
            params (string property, Object value)[] references)
        {
            var serialized = new SerializedObject(target);
            var container = serialized.FindProperty(containerName);
            if (container == null)
            {
                throw new InvalidOperationException(target.GetType().Name + " missing property " +
                                                    containerName + ".");
            }

            for (var i = 0; i < references.Length; i++)
            {
                var property = container.FindPropertyRelative(references[i].property);
                if (property == null)
                {
                    throw new InvalidOperationException(target.GetType().Name + "." + containerName +
                                                        " missing property " + references[i].property + ".");
                }

                property.objectReferenceValue = references[i].value;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetObjectArray<T>(Object target, string propertyName, T[] values)
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

        private static void SetTopAnchored(RectTransform rect, Vector2 size, Vector2 position)
        {
            SetRect(rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), size, position);
        }

        private static void SetRect(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 size,
            Vector2 position)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
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

        private static void EnsureFolder(string path)
        {
            var segments = path.Split('/');
            var current = segments[0];
            for (var i = 1; i < segments.Length; i++)
            {
                var next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, segments[i]);
                }

                current = next;
            }
        }

        private static void EnsureControlledMetaGuid(string assetPath, string expectedGuid)
        {
            YC.Editor.EventChoiceDialogControlledMetaGuid.ValidateAsset(
                assetPath,
                expectedGuid);
        }
    }
}
