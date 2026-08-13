using System;
using YC.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YC.EditorTools
{
    public static class GameplayInteractionHudEditorAssetBuilder
    {
        public const string PrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        public const string SampleScenePath = "Assets/Scenes/SampleScene.unity";
        public const string HintCardTexturePath =
            "Assets/Resources/ProjectAssetLibrary/HintCards/提示卡.jpg";

        private const float ActionPanelWidth = 360f;
        private const float ActionPanelHeight = 502f;

        [MenuItem("Tools/YC/Rebuild Gameplay Interaction HUD Editor Assets")]
        public static void Rebuild()
        {
            YC.Editor.UiThemeBuildReadiness.InitializeRequiredTheme();
            EnsureFolder("Assets/YC/Presentation/Prefabs/Gameplay");
            ExpandableInfoPanelEditorAssetBuilder.Rebuild();
            BuildInfoPanelEditorAssetBuilder.Rebuild();
            GameplayDialogEditorAssetBuilder.Rebuild();
            var actionPanelLayoutProfile = YC.Editor.SecondaryLayoutEditorAssetBuilder.LoadRequiredActionProfile();
            var cardInteractionLayoutProfile = YC.Editor.SecondaryLayoutEditorAssetBuilder.LoadRequiredCardProfile();
            var cardVisualCatalog = YC.Editor.CardVisualCatalogEditorAssetBuilder.LoadRequiredCatalog();
            var hintCardTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(HintCardTexturePath);
            if (hintCardTexture == null)
            {
                throw new InvalidOperationException("缺少固定提示卡纹理：" + HintCardTexturePath);
            }

            var infoPanelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                ExpandableInfoPanelEditorAssetBuilder.PrefabPath);
            var buildInfoPanelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                BuildInfoPanelEditorAssetBuilder.PrefabPath);
            var effectDialogShellPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                GameplayDialogEditorAssetBuilder.EffectDialogShellPrefabPath);
            var dispatchDecisionPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                GameplayDialogEditorAssetBuilder.DispatchDecisionPrefabPath);
            var eventChoiceDialogPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                GameplayDialogEditorAssetBuilder.EventChoiceDialogPrefabPath);
            var cityStyleDeclarationPreviewPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                CityStyleDeclarationPreviewEditorAssetBuilder.PrefabPath);
            if (infoPanelPrefab == null || buildInfoPanelPrefab == null || effectDialogShellPrefab == null ||
                dispatchDecisionPrefab == null || eventChoiceDialogPrefab == null ||
                cityStyleDeclarationPreviewPrefab == null)
            {
                throw new InvalidOperationException("交互 HUD 缺少信息面板 Prefab 依赖。");
            }

            var prefab = BuildPrefab(
                hintCardTexture,
                cardVisualCatalog,
                infoPanelPrefab,
                buildInfoPanelPrefab,
                effectDialogShellPrefab.GetComponent<EffectDialogShellView>(),
                dispatchDecisionPrefab.GetComponent<DispatchDecisionDialogView>(),
                eventChoiceDialogPrefab.GetComponent<EventChoiceDialogView>(),
                cityStyleDeclarationPreviewPrefab.GetComponent<CityStyleDeclarationPreviewView>(),
                actionPanelLayoutProfile,
                cardInteractionLayoutProfile);
            InstallInSampleScene(prefab);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[GameplayInteractionHudEditorAssetBuilder] 已重建交互 HUD 并接入 SampleScene。");
        }

        private static GameObject BuildPrefab(
            Texture2D hintCardTexture,
            CardVisualCatalog cardVisualCatalog,
            GameObject infoPanelPrefab,
            GameObject buildInfoPanelPrefab,
            EffectDialogShellView effectDialogShellPrefab,
            DispatchDecisionDialogView dispatchDecisionPrefab,
            EventChoiceDialogView eventChoiceDialogPrefab,
            CityStyleDeclarationPreviewView cityStyleDeclarationPreviewPrefab,
            ActionPanelLayoutProfile actionPanelLayoutProfile,
            CardInteractionLayoutProfile cardInteractionLayoutProfile)
        {
            var root = new GameObject("Gameplay Interaction HUD");
            try
            {
                var canvasObject = CreateUiObject(
                    "Mobile City UI Canvas",
                    root.transform,
                    typeof(Canvas),
                    typeof(CanvasScaler),
                    typeof(GraphicRaycaster));
                var canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 15;
                var scaler = canvasObject.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = UiTheme.CanvasReferenceResolution;
                scaler.matchWidthOrHeight = UiTheme.CanvasMatchWidthOrHeight;

                var tabletopCanvasObject = CreateUiObject(
                    "Tabletop UI Canvas",
                    root.transform,
                    typeof(Canvas),
                    typeof(CanvasScaler),
                    typeof(GraphicRaycaster),
                    typeof(TabletopCanvasLayout),
                    typeof(TabletopBoundsContributor));
                var tabletopCanvas = tabletopCanvasObject.GetComponent<Canvas>();
                tabletopCanvas.renderMode = RenderMode.WorldSpace;
                tabletopCanvas.sortingOrder = 60;
                var tabletopRect = tabletopCanvasObject.GetComponent<RectTransform>();
                tabletopRect.sizeDelta = TabletopCanvasLayout.ReferenceResolution;
                var tabletopScaler = tabletopCanvasObject.GetComponent<CanvasScaler>();
                tabletopScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                tabletopScaler.referenceResolution = TabletopCanvasLayout.ReferenceResolution;
                tabletopScaler.matchWidthOrHeight = 0.5f;
                var tabletopRaycaster = tabletopCanvasObject.GetComponent<GraphicRaycaster>();
                var tabletopLayout = tabletopCanvasObject.GetComponent<TabletopCanvasLayout>();
                SetReferences(
                    tabletopLayout,
                    ("canvas", tabletopCanvas),
                    ("rectTransform", tabletopRect),
                    ("graphicRaycaster", tabletopRaycaster),
                    ("canvasScaler", tabletopScaler));

                var promptView = BuildPromptView(canvasObject.transform, canvas);
                var actionPanelView = BuildActionPanelView(
                    canvasObject.transform,
                    hintCardTexture,
                    actionPanelLayoutProfile);
                var infoPanelInstance = (GameObject)PrefabUtility.InstantiatePrefab(
                    infoPanelPrefab,
                    canvasObject.transform);
                var buildInfoPanelInstance = (GameObject)PrefabUtility.InstantiatePrefab(
                    buildInfoPanelPrefab,
                    tabletopCanvasObject.transform);
                infoPanelInstance.transform.SetAsLastSibling();
                var infoPanel = infoPanelInstance.GetComponent<ExpandableInfoPanel>();
                var buildInfoPanel = buildInfoPanelInstance.GetComponent<BuildInfoPanel>();
                if (infoPanel == null || buildInfoPanel == null)
                {
                    throw new InvalidOperationException("信息面板 nested prefab 缺少控制器组件。");
                }

                var buildInfoView = buildInfoPanel.View;
                var boundsContributor = tabletopCanvasObject.GetComponent<TabletopBoundsContributor>();
                SetObjectReferenceArray(
                    boundsContributor,
                    "rectTransforms",
                    buildInfoView.ExternalFacilityArea,
                    buildInfoView.ExternalCityStyleArea,
                    buildInfoView.PanelTransform);

                var registryObject = new GameObject("Gameplay Dialog Registry");
                registryObject.transform.SetParent(root.transform, false);
                var dialogRegistry = registryObject.AddComponent<GameplayDialogRegistry>();
                SetReferences(
                    dialogRegistry,
                    ("effectDialogShellPrefab", effectDialogShellPrefab),
                    ("dispatchDecisionPrefab", dispatchDecisionPrefab),
                    ("eventChoiceDialogPrefab", eventChoiceDialogPrefab),
                    ("cityStyleDeclarationPreviewPrefab", cityStyleDeclarationPreviewPrefab),
                    ("cardInteractionLayoutProfile", cardInteractionLayoutProfile),
                    ("cardVisualCatalog", cardVisualCatalog));
                if (!dialogRegistry.TryValidateConfiguration(out var registryReason))
                {
                    throw new InvalidOperationException(registryReason);
                }

                var hudView = root.AddComponent<GameplayInteractionHudView>();
                SetReferences(
                    hudView,
                    ("canvas", canvas),
                    ("tabletopCanvas", tabletopLayout),
                    ("promptView", promptView),
                    ("actionPanelView", actionPanelView),
                    ("infoPanel", infoPanel),
                    ("buildInfoPanel", buildInfoPanel),
                    ("dialogRegistry", dialogRegistry));

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        }

        private static GameplayPromptView BuildPromptView(Transform parent, Canvas canvas)
        {
            var panelObject = CreateUiObject(
                "Prompt Panel",
                parent,
                typeof(Image),
                typeof(Outline),
                typeof(CanvasGroup));
            var panel = panelObject.GetComponent<RectTransform>();
            panel.anchorMin = Vector2.one;
            panel.anchorMax = Vector2.one;
            panel.pivot = Vector2.one;
            panel.sizeDelta = new Vector2(520f, 82f);
            panel.anchoredPosition = new Vector2(544f, -128f);
            panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
            panelObject.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
            panelObject.GetComponent<Outline>().effectDistance = new Vector2(3f, -3f);
            var group = panelObject.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;

            var promptText = CreateText(panel, "Prompt Text", string.Empty, 30, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(promptText.rectTransform);
            promptText.rectTransform.offsetMin = new Vector2(22f, 0f);
            promptText.rectTransform.offsetMax = new Vector2(-22f, 0f);
            promptText.verticalOverflow = VerticalWrapMode.Overflow;

            var view = panelObject.AddComponent<GameplayPromptView>();
            SetReferences(
                view,
                ("canvas", canvas),
                ("panelTransform", panel),
                ("promptText", promptText),
                ("canvasGroup", group));
            return view;
        }

        private static ActionPanelView BuildActionPanelView(
            Transform parent,
            Texture2D hintCardTexture,
            ActionPanelLayoutProfile layoutProfile)
        {
            var panelObject = CreateUiObject("Action Panel", parent, typeof(Image), typeof(Outline));
            var panel = panelObject.GetComponent<RectTransform>();
            SetTopAnchored(panel, new Vector2(ActionPanelWidth, ActionPanelHeight), Vector2.zero);
            panel.anchorMin = Vector2.right;
            panel.anchorMax = Vector2.right;
            panel.pivot = Vector2.right;
            panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
            panelObject.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
            panelObject.GetComponent<Outline>().effectDistance = new Vector2(3f, -3f);

            var mainFace = CreateUiObject("Main Action Face", panel, Array.Empty<Type>());
            Stretch(mainFace.GetComponent<RectTransform>());
            var mainRect = mainFace.GetComponent<RectTransform>();
            var localPlayerColor = CreateColorSwatch(mainRect, new Vector2(-150f, -98f));
            var remainingInfluence = CreateText(mainRect, "Remaining Influence Text", "× 0", 16, FontStyle.Bold, TextAnchor.MiddleLeft);
            SetTopAnchored(remainingInfluence.rectTransform, new Vector2(64f, 30f), new Vector2(-105f, -98f));
            var currentPlayer = CreateText(mainRect, "当前玩家 Text", "当前玩家", 20, FontStyle.Bold, TextAnchor.MiddleCenter);
            SetTopAnchored(currentPlayer.rectTransform, new Vector2(316f, 30f), new Vector2(0f, -68f));
            var phase = CreateText(mainRect, "阶段 Text", "阶段", 16, FontStyle.Normal, TextAnchor.MiddleCenter);
            SetTopAnchored(phase.rectTransform, new Vector2(316f, 30f), new Vector2(0f, -98f));
            var quickLabel = CreateText(mainRect, "快速行动 Text", "快速行动", 16, FontStyle.Bold, TextAnchor.MiddleCenter);
            SetTopAnchored(quickLabel.rectTransform, new Vector2(316f, 30f), new Vector2(0f, -130f));
            var useCharacter = CreateActionButton(mainRect, "使用角色牌 Button", "使用角色牌", new Vector2(-86f, -162f));
            var declareStyle = CreateActionButton(mainRect, "宣告样式 Button", "宣告样式", new Vector2(86f, -162f));
            var mainLabel = CreateText(mainRect, "主要行动 Text", "主要行动", 16, FontStyle.Bold, TextAnchor.MiddleCenter);
            SetTopAnchored(mainLabel.rectTransform, new Vector2(316f, 30f), new Vector2(0f, -204f));
            var deploy = CreateActionButton(mainRect, "部署 Button", "部署", new Vector2(-86f, -236f));
            var dispatch = CreateActionButton(mainRect, "调度 Button", "调度", new Vector2(86f, -236f));
            var explore = CreateActionButton(mainRect, "探索 Button", "探索", new Vector2(-86f, -288f));
            var moveCity = CreateActionButton(mainRect, "城市移动 Button", "城市移动", new Vector2(86f, -288f));
            var endRound = CreateActionButton(mainRect, "结束本回合 Button", "结束本回合", new Vector2(0f, -340f));
            var status = CreateText(mainRect, "状态 Text", "状态", 15, FontStyle.Normal, TextAnchor.MiddleCenter);
            SetTopAnchored(status.rectTransform, new Vector2(316f, 56f), new Vector2(0f, -424f));
            status.resizeTextForBestFit = true;
            status.resizeTextMinSize = 11;
            status.resizeTextMaxSize = 15;

            var cardFace = CreateUiObject("Action Card Face", panel, Array.Empty<Type>());
            Stretch(cardFace.GetComponent<RectTransform>());
            var cardRect = cardFace.GetComponent<RectTransform>();
            var cardContainer = CreateUiObject(
                "Action Card Image Container",
                cardRect,
                typeof(Image),
                typeof(Outline));
            var cardContainerRect = cardContainer.GetComponent<RectTransform>();
            ConfigureCharacterContainerRect(cardContainerRect);
            var cardContainerBackground = cardContainer.GetComponent<Image>();
            cardContainerBackground.color = UiTheme.ScrollBackground;
            cardContainerBackground.raycastTarget = false;
            var cardOutline = cardContainer.GetComponent<Outline>();
            cardOutline.effectColor = UiTheme.GoldOutlineThin;
            cardOutline.effectDistance = new Vector2(2f, -2f);

            var cardImageObject = CreateUiObject("Action Card Image", cardContainerRect, typeof(RawImage), typeof(Button));
            StretchInset(cardImageObject.GetComponent<RectTransform>(), 6f);
            var cardImage = cardImageObject.GetComponent<RawImage>();
            cardImage.color = Color.white;
            cardImage.raycastTarget = true;
            var cardImageButton = cardImageObject.GetComponent<Button>();
            cardImageButton.transition = Selectable.Transition.None;
            cardImageButton.interactable = false;

            var shadeObject = CreateUiObject("Action Card Shade", cardContainerRect, typeof(Image));
            Stretch(shadeObject.GetComponent<RectTransform>());
            var shade = shadeObject.GetComponent<Image>();
            shade.color = Color.clear;
            shade.raycastTarget = false;
            var placeholder = CreateText(
                cardContainerRect,
                "Action Card Empty Message",
                string.Empty,
                18,
                FontStyle.Bold,
                TextAnchor.MiddleCenter);
            StretchInset(placeholder.rectTransform, 14f);
            placeholder.gameObject.SetActive(false);

            var cardTitle = CreateText(cardRect, "Action Card Title", string.Empty, 19, FontStyle.Bold, TextAnchor.MiddleCenter);
            SetTopAnchored(cardTitle.rectTransform, new Vector2(316f, 30f), new Vector2(0f, -28f));
            cardTitle.color = Color.white;
            var cardHint = CreateText(cardRect, "Action Card Hint", string.Empty, 15, FontStyle.Bold, TextAnchor.MiddleCenter);
            SetTopAnchored(cardHint.rectTransform, new Vector2(316f, 58f), new Vector2(0f, -382f));
            cardHint.color = Color.white;
            var primary = CreateActionButton(cardRect, "Character Strategy Button", "策略", new Vector2(-86f, -454f));
            var secondary = CreateActionButton(cardRect, "Character Tactic Button", "计谋", new Vector2(86f, -454f));
            var flip = CreateActionButton(panel, "Action Panel Flip Button", "翻转", new Vector2(132f, -24f), new Vector2(74f, 32f), 14);
            flip.transform.SetAsLastSibling();

            cardFace.SetActive(false);
            mainFace.SetActive(true);
            var view = panelObject.AddComponent<ActionPanelView>();
            SetReferences(
                view,
                ("layoutProfile", layoutProfile),
                ("panelObject", panelObject),
                ("mainFaceObject", mainFace),
                ("cardFaceObject", cardFace),
                ("cardImageContainer", cardContainerRect),
                ("cardImageContainerBackground", cardContainerBackground),
                ("cardImage", cardImage),
                ("cardImageButton", cardImageButton),
                ("cardShade", shade),
                ("cardFaceOutline", cardOutline),
                ("cardPlaceholderText", placeholder),
                ("cardTitleText", cardTitle),
                ("cardHintText", cardHint),
                ("cardPrimaryButton", primary),
                ("cardPrimaryLabel", primary.GetComponentInChildren<Text>()),
                ("cardSecondaryButton", secondary),
                ("cardSecondaryLabel", secondary.GetComponentInChildren<Text>()),
                ("hintCardTexture", hintCardTexture),
                ("flipButton", flip),
                ("currentPlayerText", currentPlayer),
                ("phaseText", phase),
                ("localPlayerColorSwatch", localPlayerColor),
                ("remainingInfluenceText", remainingInfluence),
                ("statusText", status),
                ("useCharacterButton", useCharacter),
                ("declareCityStyleButton", declareStyle),
                ("deployButton", deploy),
                ("dispatchButton", dispatch),
                ("exploreButton", explore),
                ("moveCityButton", moveCity),
                ("endRoundButton", endRound));
            return view;
        }

        private static Button CreateActionButton(
            RectTransform parent,
            string name,
            string label,
            Vector2 position,
            Vector2? size = null,
            int fontSize = 16)
        {
            var buttonObject = CreateUiObject(
                name,
                parent,
                typeof(Image),
                typeof(Button),
                typeof(Outline),
                typeof(ActionButtonPressFeedback));
            SetTopAnchored(buttonObject.GetComponent<RectTransform>(), size ?? new Vector2(150f, 42f), position);
            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);
            var labelText = CreateText(buttonObject.transform, "Label", label, fontSize, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(labelText.rectTransform);
            labelText.rectTransform.offsetMin = new Vector2(8f, 0f);
            labelText.rectTransform.offsetMax = new Vector2(-8f, 0f);
            labelText.resizeTextForBestFit = true;
            labelText.resizeTextMinSize = 11;
            labelText.resizeTextMaxSize = fontSize;
            labelText.raycastTarget = false;
            return buttonObject.GetComponent<Button>();
        }

        private static Image CreateColorSwatch(RectTransform parent, Vector2 position)
        {
            var swatch = CreateUiObject("Local Player Color Swatch", parent, typeof(Image), typeof(Outline));
            SetTopAnchored(swatch.GetComponent<RectTransform>(), new Vector2(22f, 22f), position);
            swatch.GetComponent<Image>().color = Color.white;
            swatch.GetComponent<Outline>().effectColor = UiTheme.GoldOutlineThin;
            swatch.GetComponent<Outline>().effectDistance = new Vector2(1f, -1f);
            return swatch.GetComponent<Image>();
        }

        private static Text CreateText(
            Transform parent,
            string name,
            string value,
            int size,
            FontStyle style,
            TextAnchor alignment)
        {
            var textObject = CreateUiObject(name, parent, typeof(Text));
            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.alignment = alignment;
            text.color = UiTheme.GoldText;
            text.fontSize = size;
            text.fontStyle = style;
            text.font = UiEditorAssetReferences.CjkFont;
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
                    throw new InvalidOperationException(target.GetType().Name + " missing property " + references[i].property + ".");
                }

                property.objectReferenceValue = references[i].value;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetObjectReferenceArray(
            Object target,
            string propertyName,
            params Object[] values)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(propertyName);
            if (property == null || !property.isArray)
            {
                throw new InvalidOperationException(
                    target.GetType().Name + " missing array property " + propertyName + ".");
            }

            property.arraySize = values.Length;
            for (var i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void InstallInSampleScene(GameObject prefab)
        {
            var scene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            GameObject instance = null;
            for (var i = 0; i < roots.Length; i++)
            {
                if (roots[i].name == "Gameplay Interaction HUD")
                {
                    if (instance != null ||
                        PrefabUtility.GetCorrespondingObjectFromSource(roots[i]) != prefab)
                    {
                        throw new InvalidOperationException(
                            "SampleScene 的 Gameplay Interaction HUD 必须是唯一的目标 Prefab 实例。");
                    }

                    instance = roots[i];
                }
                else if (roots[i].name == "InfoPanel")
                {
                    if (roots[i].GetComponent<ExpandableInfoPanel>() == null)
                    {
                        throw new InvalidOperationException("SampleScene 的 InfoPanel 根不是预期旧控制器，停止替换。");
                    }

                    Object.DestroyImmediate(roots[i]);
                }
            }

            var cityController = FindInScene<MobileCityInteractionController>(scene);
            if (cityController == null)
            {
                throw new InvalidOperationException("SampleScene 缺少 MobileCityInteractionController。");
            }

            if (FindAllInScene<EventSystem>(scene).Length != 1)
            {
                throw new InvalidOperationException("SampleScene 必须且只能保留一个场景 EventSystem。");
            }

            if (instance == null)
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            }
            instance.name = "Gameplay Interaction HUD";
            var hudView = instance.GetComponent<GameplayInteractionHudView>();
            SetReferences(hudView, ("cityInteractionController", cityController));
            PrefabUtility.RecordPrefabInstancePropertyModifications(hudView);

            var cityData = new SerializedObject(cityController);
            cityData.FindProperty("gameplayInteractionHud").objectReferenceValue = hudView;
            cityData.FindProperty("infoPanel").objectReferenceValue = hudView.InfoPanel;
            cityData.FindProperty("buildInfoPanel").objectReferenceValue = hudView.BuildInfoPanel;
            cityData.ApplyModifiedPropertiesWithoutUndo();

            if (!GameplayInteractionHudView.TryValidateSceneBinding(
                    hudView,
                    cityController,
                    hudView.InfoPanel,
                    hudView.BuildInfoPanel,
                    out var reason))
            {
                throw new InvalidOperationException("SampleScene 三方引用校验失败：" + reason);
            }

            if (FindAllInScene<ExpandableInfoPanel>(scene).Length != 1 ||
                FindAllInScene<BuildInfoPanel>(scene).Length != 1 ||
                scene.GetRootGameObjects().Length != 6)
            {
                throw new InvalidOperationException("SampleScene 信息面板或根节点数量不符合收口约束。");
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

        private static T[] FindAllInScene<T>(Scene scene) where T : Component
        {
            var results = new System.Collections.Generic.List<T>();
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                results.AddRange(roots[i].GetComponentsInChildren<T>(true));
            }

            return results.ToArray();
        }

        private static void SetTopAnchored(RectTransform rect, Vector2 size, Vector2 position)
        {
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void ConfigureCharacterContainerRect(RectTransform rect)
        {
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(222f, 310f);
            rect.anchoredPosition = new Vector2(0f, -58f);
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void StretchInset(RectTransform rect, float inset)
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
