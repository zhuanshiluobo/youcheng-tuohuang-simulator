using System;
using YC.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YC.EditorTools
{
    public static class GameSettingsMenuEditorAssetBuilder
    {
        public const string PrefabPath = "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab";
        public const string StartScenePath = "Assets/Scenes/StartScene.unity";
        public const string GameScenePath = "Assets/Scenes/SampleScene.unity";

        [MenuItem("Tools/YC/Rebuild Game Settings Editor Assets")]
        public static void Rebuild()
        {
            UiTheme.Initialize(YC.Editor.UiThemeBuildReadiness.RebuildCatalogAsset());
            YC.Editor.EventCharacterCardCatalogEditorAssetBuilder.RebuildCatalogAsset();
            EnsureFolder("Assets/YC/Presentation/Prefabs/GameSettings");
            if (AssetDatabase.LoadAssetAtPath<GameObject>(ViewerEditorAssetBuilder.ZoomablePrefabPath) == null ||
                AssetDatabase.LoadAssetAtPath<GameObject>(ViewerEditorAssetBuilder.RulebookPrefabPath) == null ||
                AssetDatabase.LoadAssetAtPath<GameObject>(ViewerEditorAssetBuilder.ActionLogPrefabPath) == null)
            {
                ViewerEditorAssetBuilder.RebuildViewerPrefabs();
            }
            var prefab = BuildPrefab();
            InstallInScene(prefab, StartScenePath, false, false);
            InstallInScene(prefab, GameScenePath, true, true);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[GameSettingsMenuEditorAssetBuilder] Rebuilt shared settings prefab and wired both scenes.");
        }

        private static GameObject BuildPrefab()
        {
            var facilityCatalog =
                YC.Editor.FacilityCardCatalogEditorAssetBuilder.LoadRequiredCatalog();
            var cityStyleSpecialActionCatalog =
                YC.Editor.CityStyleSpecialActionBuildReadiness.LoadRequiredCatalog();
            var eventCharacterCatalog =
                YC.Editor.EventCharacterCardCatalogEditorAssetBuilder.LoadRequiredCatalog();
            var uiThemeCatalog = YC.Editor.UiThemeBuildReadiness.RebuildCatalogAsset();
            UiTheme.Initialize(uiThemeCatalog);
            var zoomablePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ViewerEditorAssetBuilder.ZoomablePrefabPath);
            var rulebookPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ViewerEditorAssetBuilder.RulebookPrefabPath);
            var actionLogPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ViewerEditorAssetBuilder.ActionLogPrefabPath);
            var root = new GameObject("Game Settings Menu");
            root.SetActive(false);

            try
            {
                var canvasObject = CreateUiObject(
                    "Game Settings Canvas",
                    root.transform,
                    typeof(Canvas),
                    typeof(CanvasScaler),
                    typeof(GraphicRaycaster),
                    typeof(GameSettingsMenuView));
                var canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 120;
                var scaler = canvasObject.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = UiTheme.CanvasReferenceResolution;
                scaler.matchWidthOrHeight = UiTheme.CanvasMatchWidthOrHeight;

                var gear = CreateButton(
                    canvasObject.transform,
                    "Settings Gear Button",
                    "设置",
                    new Vector2(-34f, -34f),
                    new Vector2(64f, 64f),
                    Anchor.TopRight,
                    18);
                var actionLog = CreateButton(
                    canvasObject.transform,
                    "Action Log Button",
                    "日志",
                    new Vector2(-110.8f, -34f),
                    new Vector2(64f, 64f),
                    Anchor.TopRight,
                    18);
                actionLog.gameObject.SetActive(false);

                var overlayObject = CreateUiObject("Settings Overlay", canvasObject.transform, typeof(Image), typeof(Button));
                Stretch(overlayObject.GetComponent<RectTransform>());
                overlayObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.38f);

                var panelObject = CreateUiObject(
                    "Settings Panel",
                    overlayObject.transform,
                    typeof(Image),
                    typeof(Outline),
                    typeof(WindowCloseInputHandler));
                var menuPanel = panelObject.GetComponent<RectTransform>();
                SetCenteredRect(menuPanel, new Vector2(640f, 430f), Vector2.zero);
                panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
                panelObject.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
                panelObject.GetComponent<Outline>().effectDistance = new Vector2(3f, -3f);

                CreateText(panelObject.transform, "Settings Title", "设置", 30, new Vector2(0f, -8f), new Vector2(520f, 54f), FontStyle.Bold, Anchor.TopCenter);
                var separator = CreateUiObject("Header Separator", panelObject.transform, typeof(Image));
                SetAnchoredRect(separator.GetComponent<RectTransform>(), new Vector2(640f, 2f), new Vector2(0f, -64f), Anchor.TopCenter);
                separator.GetComponent<Image>().color = UiTheme.GoldSeparator;
                var headerClose = CreateButton(panelObject.transform, "Close Settings Button", "×", new Vector2(-14f, -14f), new Vector2(46f, 36f), Anchor.TopRight, 24);

                var bodyObject = CreateUiObject("Settings Body", panelObject.transform, typeof(Image));
                var bodyRect = bodyObject.GetComponent<RectTransform>();
                bodyRect.anchorMin = Vector2.zero;
                bodyRect.anchorMax = Vector2.one;
                bodyRect.offsetMin = new Vector2(28f, 84f);
                bodyRect.offsetMax = new Vector2(-28f, -76f);
                bodyObject.GetComponent<Image>().color = UiTheme.ScrollBackground;
                var generalTab = CreateButton(bodyObject.transform, "通用 Button", "通用", new Vector2(18f, -18f), new Vector2(174f, 46f), Anchor.TopLeft, 21);
                var rulebook = CreateButton(bodyObject.transform, "规则书 Button", "规则书", new Vector2(205f, -18f), new Vector2(174f, 46f), Anchor.TopLeft, 21);
                var placeholderTab = CreateButton(bodyObject.transform, "占位 Button", "占位", new Vector2(392f, -18f), new Vector2(174f, 46f), Anchor.TopLeft, 21);
                generalTab.interactable = false;

                var generalContent = CreateUiObject("通用 Content", bodyObject.transform, typeof(Image));
                var generalContentRect = generalContent.GetComponent<RectTransform>();
                generalContentRect.anchorMin = Vector2.zero;
                generalContentRect.anchorMax = Vector2.one;
                generalContentRect.offsetMin = new Vector2(18f, 18f);
                generalContentRect.offsetMax = new Vector2(-18f, -78f);
                generalContent.GetComponent<Image>().color = UiTheme.SectionTitleBackground;
                var resolutionLabel = CreateText(
                    generalContent.transform,
                    "分辨率 Label",
                    "分辨率",
                    20,
                    new Vector2(24f, -24f),
                    new Vector2(120f, 46f),
                    FontStyle.Bold,
                    Anchor.TopLeft);
                resolutionLabel.alignment = TextAnchor.MiddleLeft;
                var resolutionDropdown = CreateDropdown(
                    generalContent.transform,
                    "分辨率 Dropdown",
                    new Vector2(168f, -24f),
                    new Vector2(260f, 46f),
                    Anchor.TopLeft,
                    19);

                var futureContent = CreateUiObject("占位 Content", bodyObject.transform, typeof(Image));
                var futureContentRect = futureContent.GetComponent<RectTransform>();
                futureContentRect.anchorMin = Vector2.zero;
                futureContentRect.anchorMax = Vector2.one;
                futureContentRect.offsetMin = new Vector2(18f, 18f);
                futureContentRect.offsetMax = new Vector2(-18f, -78f);
                futureContent.GetComponent<Image>().color = UiTheme.SectionTitleBackground;
                CreateText(
                    futureContent.transform,
                    "Future Content Label",
                    "功能待接入",
                    20,
                    Vector2.zero,
                    new Vector2(300f, 48f));
                futureContent.SetActive(false);
                var returnButton = CreateButton(panelObject.transform, "返回主菜单 Button", "返回主菜单", new Vector2(-28f, 20f), new Vector2(174f, 48f), Anchor.BottomRight, 20);

                var confirmationObject = CreateUiObject("Return Confirmation", panelObject.transform, typeof(Image));
                Stretch(confirmationObject.GetComponent<RectTransform>());
                confirmationObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.42f);
                var dialogObject = CreateUiObject("Return Confirmation Dialog", confirmationObject.transform, typeof(Image), typeof(Outline));
                SetCenteredRect(dialogObject.GetComponent<RectTransform>(), new Vector2(480f, 220f), Vector2.zero);
                dialogObject.GetComponent<Image>().color = UiTheme.PanelBackgroundLighter;
                dialogObject.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
                dialogObject.GetComponent<Outline>().effectDistance = new Vector2(3f, -3f);
                CreateText(dialogObject.transform, "Confirmation Title", "确认返回主菜单？", 28, new Vector2(0f, 50f), new Vector2(450f, 40f), FontStyle.Bold);
                CreateText(dialogObject.transform, "Confirmation Message", "当前对局进度不会自动保存。", 17, new Vector2(0f, 10f), new Vector2(450f, 40f));
                var confirmReturn = CreateButton(dialogObject.transform, "确认返回 Button", "确认返回", new Vector2(-104f, -62f), new Vector2(154f, 42f), Anchor.Center, 20);
                var cancelReturn = CreateButton(dialogObject.transform, "取消 Button", "取消", new Vector2(104f, -62f), new Vector2(154f, 42f), Anchor.Center, 20);

                confirmationObject.SetActive(false);
                overlayObject.SetActive(false);

                var rulebookViewerObject = (GameObject)PrefabUtility.InstantiatePrefab(rulebookPrefab);
                rulebookViewerObject.name = "RulebookViewer";
                rulebookViewerObject.transform.SetParent(root.transform, false);
                var rulebookViewer = rulebookViewerObject.GetComponent<RulebookViewerController>();

                var actionLogViewerObject = (GameObject)PrefabUtility.InstantiatePrefab(actionLogPrefab);
                actionLogViewerObject.name = "ActionLogViewer";
                actionLogViewerObject.transform.SetParent(root.transform, false);
                var actionLogViewer = actionLogViewerObject.GetComponent<ActionLogViewerController>();

                var view = canvasObject.GetComponent<GameSettingsMenuView>();
                SetReferences(
                    view,
                    ("canvasTransform", canvasObject.GetComponent<RectTransform>()),
                    ("menuPanel", menuPanel),
                    ("overlayObject", overlayObject),
                    ("confirmationObject", confirmationObject),
                    ("returnButtonObject", returnButton.gameObject),
                    ("actionLogButtonObject", actionLog.gameObject),
                    ("generalContentObject", generalContent),
                    ("futureContentObject", futureContent),
                    ("closeInputHandler", panelObject.GetComponent<WindowCloseInputHandler>()),
                    ("gearButton", gear),
                    ("actionLogButton", actionLog),
                    ("overlayCloseButton", overlayObject.GetComponent<Button>()),
                    ("headerCloseButton", headerClose),
                    ("generalTabButton", generalTab),
                    ("rulebookButton", rulebook),
                    ("placeholderTabButton", placeholderTab),
                    ("returnButton", returnButton),
                    ("confirmReturnButton", confirmReturn),
                    ("cancelReturnButton", cancelReturn),
                    ("resolutionDropdown", resolutionDropdown));

                var fontRefreshDriver = root.AddComponent<FontRefreshDriver>();
                SetReferences(
                    fontRefreshDriver,
                    ("cjkFont", UiEditorAssetReferences.CjkFont),
                    ("latinFont", UiEditorAssetReferences.LatinFont));
                if (!fontRefreshDriver.TryValidateConfiguration(out var fontReason))
                {
                    throw new InvalidOperationException(fontReason);
                }

                var controller = root.AddComponent<GameSettingsMenuController>();
                SetReferences(
                    controller,
                    ("view", view),
                    ("rulebookViewer", rulebookViewer),
                    ("actionLogViewer", actionLogViewer),
                    ("zoomableImageViewerPrefab", zoomablePrefab.GetComponent<ZoomableImageViewerController>()));

                ConfigureContentBootstraps(
                    root,
                    facilityCatalog,
                    cityStyleSpecialActionCatalog,
                    eventCharacterCatalog,
                    uiThemeCatalog);

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            var contents = PrefabUtility.LoadPrefabContents(PrefabPath);
            contents.SetActive(true);
            PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
            PrefabUtility.UnloadPrefabContents(contents);
            return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        }

        internal static void ConfigureContentBootstraps(
            GameObject root,
            FacilityCardCatalog facilityCatalog,
            CityStyleSpecialActionCatalog cityStyleSpecialActionCatalog,
            EventCharacterCardCatalog eventCharacterCatalog,
            UiThemeCatalog uiThemeCatalog)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            string facilityReason = null;
            if (facilityCatalog == null ||
                !facilityCatalog.TryValidateConfiguration(out facilityReason))
            {
                throw new InvalidOperationException(
                    "无法配置 FacilityCatalogBootstrap：" +
                    (facilityReason ?? "目录引用为空。"));
            }

            string contentReason = null;
            if (cityStyleSpecialActionCatalog == null ||
                !cityStyleSpecialActionCatalog.TryValidateConfiguration(out contentReason))
            {
                throw new InvalidOperationException(
                    "无法配置联合目录 Bootstrap：" +
                    (contentReason ?? "目录引用为空。"));
            }

            string themeReason = null;
            if (uiThemeCatalog == null ||
                !uiThemeCatalog.TryValidateConfiguration(out themeReason))
            {
                throw new InvalidOperationException(
                    "无法配置 UiThemeBootstrap：" +
                    (themeReason ?? "目录引用为空。"));
            }

            string eventCharacterReason = null;
            if (eventCharacterCatalog == null ||
                !eventCharacterCatalog.TryValidateConfiguration(out eventCharacterReason))
            {
                throw new InvalidOperationException(
                    "无法配置 EventCharacterCatalogBootstrap：" +
                    (eventCharacterReason ?? "目录引用为空。"));
            }

            var facilityBootstraps =
                root.GetComponentsInChildren<FacilityCatalogBootstrap>(true);
            if (facilityBootstraps.Length > 1)
            {
                throw new InvalidOperationException(
                    "GameSettings 根对象中存在多个 FacilityCatalogBootstrap。");
            }

            var facilityBootstrap = facilityBootstraps.Length == 1
                ? facilityBootstraps[0]
                : root.AddComponent<FacilityCatalogBootstrap>();
            if (facilityBootstrap.gameObject != root)
            {
                throw new InvalidOperationException(
                    "FacilityCatalogBootstrap 必须位于 GameSettings 根对象。");
            }

            SetReferences(
                facilityBootstrap,
                ("facilityCardCatalog", facilityCatalog));

            var contentBootstraps =
                root.GetComponentsInChildren<CityStyleSpecialActionCatalogBootstrap>(true);
            if (contentBootstraps.Length > 1)
            {
                throw new InvalidOperationException(
                    "GameSettings 根对象中存在多个联合目录 Bootstrap。");
            }

            var contentBootstrap = contentBootstraps.Length == 1
                ? contentBootstraps[0]
                : root.AddComponent<CityStyleSpecialActionCatalogBootstrap>();
            if (contentBootstrap.gameObject != root)
            {
                throw new InvalidOperationException(
                    "联合目录 Bootstrap 必须位于 GameSettings 根对象。");
            }

            SetReferences(
                contentBootstrap,
                ("catalog", cityStyleSpecialActionCatalog));

            var themeBootstraps = root.GetComponentsInChildren<UiThemeBootstrap>(true);
            if (themeBootstraps.Length > 1)
            {
                throw new InvalidOperationException("GameSettings 根对象中存在多个 UiThemeBootstrap。");
            }

            var themeBootstrap = themeBootstraps.Length == 1
                ? themeBootstraps[0]
                : root.AddComponent<UiThemeBootstrap>();
            if (themeBootstrap.gameObject != root)
            {
                throw new InvalidOperationException("UiThemeBootstrap 必须位于 GameSettings 根对象。");
            }

            SetReferences(themeBootstrap, ("themeCatalog", uiThemeCatalog));

            var eventCharacterBootstraps =
                root.GetComponentsInChildren<EventCharacterCatalogBootstrap>(true);
            if (eventCharacterBootstraps.Length > 1)
            {
                throw new InvalidOperationException(
                    "GameSettings 根对象中存在多个 EventCharacterCatalogBootstrap。");
            }

            var eventCharacterBootstrap = eventCharacterBootstraps.Length == 1
                ? eventCharacterBootstraps[0]
                : root.AddComponent<EventCharacterCatalogBootstrap>();
            if (eventCharacterBootstrap.gameObject != root)
            {
                throw new InvalidOperationException(
                    "EventCharacterCatalogBootstrap 必须位于 GameSettings 根对象。");
            }

            eventCharacterBootstrap.enabled = true;
            SetReferences(eventCharacterBootstrap, ("catalog", eventCharacterCatalog));

            if (!facilityBootstrap.TryValidateConfiguration(out facilityReason) ||
                !contentBootstrap.TryValidateConfiguration(out contentReason) ||
                !themeBootstrap.TryValidateConfiguration(out themeReason) ||
                !eventCharacterBootstrap.TryValidateConfiguration(out eventCharacterReason))
            {
                throw new InvalidOperationException(
                    "GameSettings 内容 Bootstrap 接线验证失败：" +
                    facilityReason + contentReason + themeReason + eventCharacterReason);
            }
        }

        private static void InstallInScene(GameObject prefab, string scenePath, bool showReturn, bool bindGameController)
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                if (roots[i].name == "Game Settings Menu")
                {
                    Object.DestroyImmediate(roots[i]);
                    break;
                }
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.name = "Game Settings Menu";
            var controller = instance.GetComponent<GameSettingsMenuController>();
            MobileCityInteractionController cityController = null;
            if (bindGameController)
            {
                cityController = FindInScene<MobileCityInteractionController>(scene);
                if (cityController == null)
                {
                    throw new InvalidOperationException("SampleScene missing MobileCityInteractionController.");
                }
            }

            var controllerData = new SerializedObject(controller);
            controllerData.FindProperty("showReturnToStartButton").boolValue = showReturn;
            controllerData.FindProperty("startSceneName").stringValue = "StartScene";
            controllerData.FindProperty("cityInteractionController").objectReferenceValue = cityController;
            controllerData.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.RecordPrefabInstancePropertyModifications(controller);

            if (cityController != null)
            {
                var cityData = new SerializedObject(cityController);
                cityData.FindProperty("settingsMenu").objectReferenceValue = controller;
                cityData.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        private static T FindInScene<T>(Scene scene) where T : Component
        {
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                var component = roots[i].GetComponentInChildren<T>(true);
                if (component != null)
                {
                    return component;
                }
            }

            return null;
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
            SetAnchoredRect(buttonObject.GetComponent<RectTransform>(), size, position, anchor);
            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            buttonObject.GetComponent<Outline>().effectColor = UiTheme.GoldOutlineThin;
            buttonObject.GetComponent<Outline>().effectDistance = new Vector2(2f, -2f);
            ConfigureSelectableColors(buttonObject.GetComponent<Button>());
            var labelText = CreateText(buttonObject.transform, "Label", label, fontSize, Vector2.zero, Vector2.zero, FontStyle.Bold);
            Stretch(labelText.rectTransform);
            labelText.rectTransform.offsetMin = new Vector2(8f, 0f);
            labelText.rectTransform.offsetMax = new Vector2(-8f, 0f);
            labelText.resizeTextForBestFit = true;
            labelText.resizeTextMinSize = 12;
            labelText.resizeTextMaxSize = fontSize;
            labelText.raycastTarget = false;
            return buttonObject.GetComponent<Button>();
        }

        private static Dropdown CreateDropdown(
            Transform parent,
            string name,
            Vector2 position,
            Vector2 size,
            Anchor anchor,
            int fontSize)
        {
            var dropdownObject = CreateUiObject(name, parent, typeof(Image), typeof(Dropdown), typeof(Outline));
            SetAnchoredRect(dropdownObject.GetComponent<RectTransform>(), size, position, anchor);
            dropdownObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            dropdownObject.GetComponent<Outline>().effectColor = UiTheme.GoldOutlineThin;
            dropdownObject.GetComponent<Outline>().effectDistance = new Vector2(2f, -2f);

            var caption = CreateText(
                dropdownObject.transform,
                "Label",
                string.Empty,
                fontSize,
                Vector2.zero,
                Vector2.zero,
                FontStyle.Bold);
            Stretch(caption.rectTransform);
            caption.rectTransform.offsetMin = new Vector2(14f, 0f);
            caption.rectTransform.offsetMax = new Vector2(-42f, 0f);
            caption.alignment = TextAnchor.MiddleLeft;
            caption.raycastTarget = false;

            var arrow = CreateText(
                dropdownObject.transform,
                "Arrow",
                "▼",
                16,
                new Vector2(-12f, 0f),
                new Vector2(30f, 46f),
                FontStyle.Bold,
                Anchor.TopRight);
            arrow.rectTransform.pivot = new Vector2(1f, 0.5f);
            arrow.rectTransform.anchorMin = Vector2.one;
            arrow.rectTransform.anchorMax = Vector2.one;
            arrow.rectTransform.anchoredPosition = new Vector2(-10f, -23f);
            arrow.raycastTarget = false;

            var templateObject = CreateUiObject(
                "Template",
                dropdownObject.transform,
                typeof(Image),
                typeof(ScrollRect));
            var templateRect = templateObject.GetComponent<RectTransform>();
            templateRect.anchorMin = Vector2.zero;
            templateRect.anchorMax = Vector2.right;
            templateRect.pivot = new Vector2(0.5f, 1f);
            templateRect.anchoredPosition = new Vector2(0f, -2f);
            templateRect.sizeDelta = new Vector2(0f, 168f);
            templateObject.GetComponent<Image>().color = UiTheme.PanelBackgroundLighter;

            var viewportObject = CreateUiObject("Viewport", templateObject.transform, typeof(Image), typeof(Mask));
            Stretch(viewportObject.GetComponent<RectTransform>());
            viewportObject.GetComponent<Image>().color = Color.white;
            viewportObject.GetComponent<Mask>().showMaskGraphic = false;

            var contentObject = CreateUiObject("Content", viewportObject.transform);
            var contentRect = contentObject.GetComponent<RectTransform>();
            contentRect.anchorMin = Vector2.up;
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0f, 168f);

            var itemObject = CreateUiObject("Item", contentObject.transform, typeof(Toggle));
            var itemRect = itemObject.GetComponent<RectTransform>();
            itemRect.anchorMin = Vector2.up;
            itemRect.anchorMax = Vector2.one;
            itemRect.pivot = new Vector2(0.5f, 1f);
            itemRect.anchoredPosition = Vector2.zero;
            itemRect.sizeDelta = new Vector2(0f, 42f);

            var itemBackground = CreateUiObject("Item Background", itemObject.transform, typeof(Image));
            Stretch(itemBackground.GetComponent<RectTransform>());
            itemBackground.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var itemLabel = CreateText(
                itemObject.transform,
                "Item Label",
                "分辨率",
                fontSize,
                Vector2.zero,
                Vector2.zero);
            Stretch(itemLabel.rectTransform);
            itemLabel.rectTransform.offsetMin = new Vector2(14f, 0f);
            itemLabel.rectTransform.offsetMax = new Vector2(-14f, 0f);
            itemLabel.alignment = TextAnchor.MiddleLeft;
            itemLabel.raycastTarget = false;

            var itemToggle = itemObject.GetComponent<Toggle>();
            itemToggle.targetGraphic = itemBackground.GetComponent<Image>();
            itemToggle.graphic = null;
            ConfigureSelectableColors(itemToggle);

            var scrollRect = templateObject.GetComponent<ScrollRect>();
            scrollRect.content = contentRect;
            scrollRect.viewport = viewportObject.GetComponent<RectTransform>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;

            var dropdown = dropdownObject.GetComponent<Dropdown>();
            dropdown.targetGraphic = dropdownObject.GetComponent<Image>();
            dropdown.template = templateRect;
            dropdown.captionText = caption;
            dropdown.itemText = itemLabel;
            ConfigureSelectableColors(dropdown);
            templateObject.SetActive(false);
            return dropdown;
        }

        private static void ConfigureSelectableColors(Selectable selectable)
        {
            var colors = selectable.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 0.92f, 0.78f, 1f);
            colors.pressedColor = new Color(0.86f, 0.75f, 0.55f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.72f, 0.58f, 0.36f, 0.92f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            selectable.colors = colors;
        }

        private static Text CreateText(
            Transform parent,
            string name,
            string content,
            int fontSize,
            Vector2 position,
            Vector2 size,
            FontStyle style = FontStyle.Normal,
            Anchor anchor = Anchor.Center)
        {
            var textObject = CreateUiObject(name, parent, typeof(Text), typeof(Outline));
            SetAnchoredRect(textObject.GetComponent<RectTransform>(), size, position, anchor);
            var text = textObject.GetComponent<Text>();
            text.text = content;
            text.font = UiEditorAssetReferences.CjkFont;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = UiTheme.GoldText;
            textObject.GetComponent<Outline>().effectColor = UiTheme.DarkShadowLight;
            textObject.GetComponent<Outline>().effectDistance = new Vector2(1f, -1f);
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
                    throw new InvalidOperationException(target.GetType().Name + " missing property " + references[i].property + ".");
                }

                property.objectReferenceValue = references[i].value;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetCenteredRect(RectTransform rect, Vector2 size, Vector2 position)
        {
            SetAnchoredRect(rect, size, position, Anchor.Center);
        }

        private static void SetAnchoredRect(RectTransform rect, Vector2 size, Vector2 position, Anchor anchor)
        {
            switch (anchor)
            {
                case Anchor.TopLeft:
                    rect.anchorMin = Vector2.up;
                    rect.anchorMax = Vector2.up;
                    rect.pivot = Vector2.up;
                    break;
                case Anchor.TopRight:
                    rect.anchorMin = Vector2.one;
                    rect.anchorMax = Vector2.one;
                    rect.pivot = Vector2.one;
                    break;
                case Anchor.BottomRight:
                    rect.anchorMin = Vector2.right;
                    rect.anchorMax = Vector2.right;
                    rect.pivot = Vector2.right;
                    break;
                case Anchor.TopCenter:
                    rect.anchorMin = new Vector2(0.5f, 1f);
                    rect.anchorMax = new Vector2(0.5f, 1f);
                    rect.pivot = new Vector2(0.5f, 1f);
                    break;
                default:
                    rect.anchorMin = new Vector2(0.5f, 0.5f);
                    rect.anchorMax = new Vector2(0.5f, 0.5f);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    break;
            }

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

        private enum Anchor
        {
            Center,
            TopLeft,
            TopRight,
            BottomRight,
            TopCenter
        }
    }
}
