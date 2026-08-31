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
    public static class StartMenuEditorAssetBuilder
    {
        public const string PrefabPath = "Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab";
        public const string ScenePath = "Assets/Scenes/StartScene.unity";

        private static readonly Color PanelColor = new Color(0.08f, 0.07f, 0.06f, 0.96f);
        private static readonly Color ButtonColor = new Color(0.16f, 0.1f, 0.055f, 0.96f);
        private static readonly Color AccentColor = new Color(0.78f, 0.63f, 0.38f, 0.9f);
        private static readonly Color TextColor = new Color(0.86f, 0.75f, 0.55f, 1f);
        private static readonly Color InputColor = new Color(0.04f, 0.035f, 0.03f, 0.98f);

        [MenuItem("Tools/YC/Rebuild Start Menu Editor Assets")]
        public static void Rebuild()
        {
            YC.Editor.UiThemeBuildReadiness.InitializeRequiredTheme();
            LoadingSceneEditorAssetBuilder.RebuildScene();
            EnsureFolder("Assets/YC/Presentation/Prefabs");
            EnsureFolder("Assets/YC/Presentation/Prefabs/StartMenu");

            var prefab = BuildPrefab();
            InstallPrefabInStartScene(prefab);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[StartMenuEditorAssetBuilder] Rebuilt " + PrefabPath + " and wired " + ScenePath + ".");
        }

        private static GameObject BuildPrefab()
        {
            var root = new GameObject("Start Menu");
            root.SetActive(false);

            try
            {
                var eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                eventSystem.transform.SetParent(root.transform, false);

                var canvasObject = CreateUiObject(
                    "Start Menu Canvas",
                    root.transform,
                    typeof(Canvas),
                    typeof(CanvasScaler),
                    typeof(GraphicRaycaster),
                    typeof(StartMenuView));
                var canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = canvasObject.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = UiTheme.CanvasReferenceResolution;
                scaler.matchWidthOrHeight = UiTheme.CanvasMatchWidthOrHeight;

                var coverFrameObject = CreateUiObject("Cover Frame", canvasObject.transform, typeof(AspectRatioFitter));
                var coverFrame = coverFrameObject.GetComponent<RectTransform>();
                SetCenteredRect(coverFrame, UiTheme.CanvasReferenceResolution, Vector2.zero);
                var aspectFitter = coverFrameObject.GetComponent<AspectRatioFitter>();
                aspectFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                aspectFitter.aspectRatio = 5888f / 3312f;

                var coverObject = CreateUiObject("Rulebook Cover", coverFrame, typeof(RawImage));
                Stretch(coverObject.GetComponent<RectTransform>());
                var coverImage = coverObject.GetComponent<RawImage>();
                coverImage.color = Color.white;
                coverImage.raycastTarget = false;

                var achievementsButton = CreateMainMenuButton(
                    coverFrame,
                    "成就 Button",
                    "成就",
                    new Vector2(-266f, 98f));
                var onlineModeButton = CreateMainMenuButton(
                    coverFrame,
                    "联机模式 Button",
                    "联机模式",
                    new Vector2(-266f, 198f));
                var startGameButton = CreateMainMenuButton(
                    coverFrame,
                    "本地游戏 Button",
                    "本地游戏",
                    new Vector2(-266f, 298f));

                var wikiButton = CreateButton(coverFrame, "Wiki Link Button", "W  进入 wiki", new Vector2(130f, 52f), new Vector2(210f, 56f), Anchor.BottomLeft);
                var officialButton = CreateButton(coverFrame, "Official Link Button", "官  官方网站", new Vector2(130f, 120f), new Vector2(210f, 56f), Anchor.BottomLeft);
                var creatorButton = CreateButton(coverFrame, "Creator Link Button", "制  制作者主页", new Vector2(130f, 188f), new Vector2(210f, 56f), Anchor.BottomLeft);

                var roomLayer = CreateUiObject("Room Panel Layer", coverFrame);
                Stretch(roomLayer.GetComponent<RectTransform>());

                var onlineModePanel = BuildOnlineModePanel(roomLayer.transform);
                var achievementsPanel = BuildAchievementsPanel(roomLayer.transform);
                var mapSelectionPanel = BuildMapSelectionPanel(roomLayer.transform);
                var joinPanel = BuildJoinPanel(roomLayer.transform);
                var waitingPanel = BuildWaitingRoomPanel(roomLayer.transform);
                var messagePanel = BuildMessagePanel(roomLayer.transform);
                var loadingPanel = BuildLoadingPanel(canvasObject.transform);

                var view = canvasObject.GetComponent<StartMenuView>();
                SetReferences(
                    view,
                    ("coverFrame", coverFrame),
                    ("coverImage", coverImage),
                    ("startGameButton", startGameButton),
                    ("onlineModeButton", onlineModeButton),
                    ("achievementsButton", achievementsButton),
                    ("creatorSiteButton", creatorButton),
                    ("officialSiteButton", officialButton),
                    ("wikiButton", wikiButton),
                    ("onlineModePanel", onlineModePanel),
                    ("achievementsPanel", achievementsPanel),
                    ("mapSelectionPanel", mapSelectionPanel),
                    ("joinPanel", joinPanel),
                    ("roomPanel", waitingPanel),
                    ("messagePanel", messagePanel),
                    ("loadingPanel", loadingPanel));

                var controller = root.AddComponent<StartMenuController>();
                SetReferences(
                    controller,
                    ("view", view),
                    ("coverTexture", AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/YC/Data/Covers/RulebookCover.jpg")));

                var controllerObject = new SerializedObject(controller);
                controllerObject.FindProperty("mapSceneName").stringValue = "SampleScene";
                controllerObject.ApplyModifiedPropertiesWithoutUndo();

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

        private static StartMenuOnlineModePanelView BuildOnlineModePanel(Transform parent)
        {
            var panelObject = CreatePanel("Online Mode Panel", parent, new Vector2(620f, 300f));
            var panel = panelObject.AddComponent<StartMenuOnlineModePanelView>();
            CreateText(
                panelObject.transform,
                "Title",
                "联机模式",
                30,
                new Vector2(0f, 94f),
                new Vector2(540f, 42f),
                FontStyle.Bold);
            var createRoom = CreateButton(
                panelObject.transform,
                "创建联机 Button",
                "创建联机",
                new Vector2(-125f, 12f),
                new Vector2(210f, 68f));
            var joinRoom = CreateButton(
                panelObject.transform,
                "加入联机 Button",
                "加入联机",
                new Vector2(125f, 12f),
                new Vector2(210f, 68f));
            var back = CreateButton(
                panelObject.transform,
                "返回 Button",
                "返回",
                new Vector2(0f, -98f),
                new Vector2(110f, 44f));

            SetReferences(
                panel,
                ("createRoomButton", createRoom),
                ("joinRoomButton", joinRoom),
                ("backButton", back));
            panelObject.SetActive(false);
            return panel;
        }

        private static StartMenuAchievementsPanelView BuildAchievementsPanel(Transform parent)
        {
            var panelObject = CreatePanel("Achievements Panel", parent, new Vector2(720f, 460f));
            var panel = panelObject.AddComponent<StartMenuAchievementsPanelView>();
            CreateText(
                panelObject.transform,
                "Title",
                "成就列表",
                30,
                new Vector2(0f, 174f),
                new Vector2(300f, 42f),
                FontStyle.Bold);
            var collectionRoom = CreateButton(
                panelObject.transform,
                "进入收藏室 Button",
                "进入收藏室",
                new Vector2(-270f, 174f),
                new Vector2(150f, 46f));
            var achievementList = CreateText(
                panelObject.transform,
                "Achievement List",
                "◆ 初来乍到    完成第一次本地游戏\n\n◆ 并肩拓荒    完成一次联机对局\n\n◆ 珍品鉴赏    进入收藏室查看藏品",
                21,
                new Vector2(0f, 5f),
                new Vector2(600f, 250f),
                FontStyle.Normal,
                TextAnchor.UpperLeft);
            achievementList.lineSpacing = 1.2f;
            var back = CreateButton(
                panelObject.transform,
                "返回 Button",
                "返回",
                new Vector2(0f, -180f),
                new Vector2(110f, 44f));

            SetReferences(
                panel,
                ("achievementListText", achievementList),
                ("collectionRoomButton", collectionRoom),
                ("backButton", back));
            panelObject.SetActive(false);
            return panel;
        }

        private static StartMenuMapSelectionPanelView BuildMapSelectionPanel(Transform parent)
        {
            var panelObject = CreatePanel("Map Selection Panel", parent, new Vector2(620f, 310f));
            var panel = panelObject.AddComponent<StartMenuMapSelectionPanelView>();
            var title = CreateText(
                panelObject.transform,
                "Title",
                "选择地图",
                30,
                new Vector2(0f, 102f),
                new Vector2(540f, 42f),
                FontStyle.Bold);
            var threePlayer = CreateButton(
                panelObject.transform,
                "三人地图 Button",
                "三人地图",
                new Vector2(-120f, 12f),
                new Vector2(190f, 64f));
            var fourPlayer = CreateButton(
                panelObject.transform,
                "四人地图 Button",
                "四人地图",
                new Vector2(120f, 12f),
                new Vector2(190f, 64f));
            var back = CreateButton(
                panelObject.transform,
                "返回 Button",
                "返回",
                new Vector2(0f, -100f),
                new Vector2(110f, 44f));

            SetReferences(
                panel,
                ("titleText", title),
                ("threePlayerButton", threePlayer),
                ("fourPlayerButton", fourPlayer),
                ("backButton", back));
            panelObject.SetActive(false);
            return panel;
        }

        private static StartMenuJoinPanelView BuildJoinPanel(Transform parent)
        {
            var panelObject = CreatePanel("Join Room Panel", parent, new Vector2(620f, 310f));
            var panel = panelObject.AddComponent<StartMenuJoinPanelView>();
            CreateText(panelObject.transform, "Title", "加入房间", 28, new Vector2(0f, 105f), new Vector2(540f, 34f), FontStyle.Bold);
            var description = CreateText(panelObject.transform, "Description", "输入房主显示的 Lobby 房间码", 18, new Vector2(0f, 62f), new Vector2(540f, 34f));

            var inputObject = CreateUiObject("Room Code Input", panelObject.transform, typeof(Image), typeof(InputField), typeof(Outline));
            SetCenteredRect(inputObject.GetComponent<RectTransform>(), new Vector2(340f, 46f), new Vector2(-46f, 10f));
            inputObject.GetComponent<Image>().color = InputColor;
            inputObject.GetComponent<Outline>().effectColor = AccentColor;
            var inputText = CreateText(inputObject.transform, "Text", string.Empty, 20, Vector2.zero, Vector2.zero, FontStyle.Normal, TextAnchor.MiddleLeft);
            SetOffsets(inputText.rectTransform, new Vector2(12f, 0f), new Vector2(-12f, 0f));
            var placeholder = CreateText(inputObject.transform, "Placeholder", "请输入 Steam Lobby ID", 20, Vector2.zero, Vector2.zero, FontStyle.Normal, TextAnchor.MiddleLeft);
            SetOffsets(placeholder.rectTransform, new Vector2(12f, 0f), new Vector2(-12f, 0f));
            placeholder.color = new Color(0.55f, 0.48f, 0.36f, 0.9f);
            var input = inputObject.GetComponent<InputField>();
            input.textComponent = inputText;
            input.placeholder = placeholder;

            var paste = CreateButton(panelObject.transform, "粘贴 Button", "粘贴", new Vector2(218f, 10f), new Vector2(96f, 44f));
            var status = CreateText(panelObject.transform, "Status", string.Empty, 16, new Vector2(0f, -38f), new Vector2(540f, 34f));
            var join = CreateButton(panelObject.transform, "加入 Button", "加入", new Vector2(-70f, -105f), new Vector2(96f, 44f));
            var back = CreateButton(panelObject.transform, "返回 Button", "返回", new Vector2(70f, -105f), new Vector2(96f, 44f));

            SetReferences(panel, ("descriptionText", description), ("roomCodeInput", input), ("statusText", status),
                ("pasteButton", paste), ("joinButton", join), ("backButton", back));
            panelObject.SetActive(false);
            return panel;
        }

        private static StartMenuRoomPanelView BuildWaitingRoomPanel(Transform parent)
        {
            var panelObject = CreatePanel("Waiting Room Panel", parent, new Vector2(620f, 430f));
            var panel = panelObject.AddComponent<StartMenuRoomPanelView>();
            var roomCode = CreateText(panelObject.transform, "Room Code", "房间号", 28, new Vector2(-58f, 170f), new Vector2(410f, 34f), FontStyle.Bold);
            var copy = CreateButton(panelObject.transform, "复制 Button", "复制", new Vector2(235f, 170f), new Vector2(96f, 44f));
            var validation = CreateText(panelObject.transform, "Validation Notice", string.Empty, 14, new Vector2(0f, 132f), new Vector2(560f, 34f), FontStyle.Bold);

            var seatListObject = CreateUiObject("Seat List", panelObject.transform, typeof(VerticalLayoutGroup));
            SetCenteredRect(seatListObject.GetComponent<RectTransform>(), new Vector2(540f, 150f), new Vector2(0f, 36f));
            var layout = seatListObject.GetComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.spacing = 4f;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            var seatTemplate = CreateText(seatListObject.transform, "Seat Template", "座位", 20, Vector2.zero, new Vector2(540f, 32f));
            var seatLayout = seatTemplate.gameObject.AddComponent<LayoutElement>();
            seatLayout.preferredHeight = 32f;
            seatTemplate.gameObject.SetActive(false);

            var status = CreateText(panelObject.transform, "Room Status", string.Empty, 16, new Vector2(0f, -66f), new Vector2(560f, 44f));
            var actionsObject = CreateUiObject("Actions", panelObject.transform, typeof(HorizontalLayoutGroup));
            SetCenteredRect(actionsObject.GetComponent<RectTransform>(), new Vector2(440f, 44f), new Vector2(0f, -125f));
            var actions = actionsObject.GetComponent<HorizontalLayoutGroup>();
            actions.childAlignment = TextAnchor.MiddleCenter;
            actions.spacing = 24f;
            actions.childControlWidth = false;
            actions.childControlHeight = false;
            actions.childForceExpandWidth = false;
            actions.childForceExpandHeight = false;
            var invite = CreateButton(actionsObject.transform, "邀请好友 Button", "邀请好友", Vector2.zero, new Vector2(116f, 44f));
            var start = CreateButton(actionsObject.transform, "开始 Button", "开始", Vector2.zero, new Vector2(96f, 44f));
            var back = CreateButton(actionsObject.transform, "返回 Button", "返回", Vector2.zero, new Vector2(96f, 44f));

            SetReferences(panel, ("roomCodeText", roomCode), ("copyButton", copy), ("validationText", validation),
                ("seatListRoot", seatListObject.GetComponent<RectTransform>()), ("seatTemplate", seatTemplate),
                ("statusText", status), ("inviteButton", invite), ("startButton", start), ("backButton", back));
            panelObject.SetActive(false);
            return panel;
        }

        private static StartMenuMessagePanelView BuildMessagePanel(Transform parent)
        {
            var panelObject = CreatePanel("Message Panel", parent, new Vector2(560f, 220f));
            var panel = panelObject.AddComponent<StartMenuMessagePanelView>();
            var title = CreateText(panelObject.transform, "Title", "提示", 28, new Vector2(0f, 46f), new Vector2(520f, 34f), FontStyle.Bold);
            var message = CreateText(panelObject.transform, "Message", string.Empty, 17, new Vector2(0f, 2f), new Vector2(520f, 52f));
            var action = CreateButton(panelObject.transform, "Action Button", "取消", new Vector2(0f, -70f), new Vector2(96f, 44f));
            var actionText = action.GetComponentInChildren<Text>(true);
            SetReferences(panel, ("titleText", title), ("messageText", message), ("actionButton", action), ("actionButtonText", actionText));
            panelObject.SetActive(false);
            return panel;
        }

        private static StartMenuLoadingPanelView BuildLoadingPanel(Transform parent)
        {
            var overlay = CreateUiObject(
                "Loading Overlay",
                parent,
                typeof(Image),
                typeof(CanvasGroup),
                typeof(StartMenuLoadingPanelView));
            Stretch(overlay.GetComponent<RectTransform>());
            var overlayImage = overlay.GetComponent<Image>();
            overlayImage.color = Color.black;
            overlayImage.raycastTarget = true;

            var canvasGroup = overlay.GetComponent<CanvasGroup>();
            canvasGroup.alpha = 0f;
            canvasGroup.blocksRaycasts = false;
            canvasGroup.interactable = false;
            var loadingView = overlay.GetComponent<StartMenuLoadingPanelView>();
            loadingView.ConfigureForEditor(canvasGroup, 0.8f, 0.8f);
            overlay.SetActive(false);
            return loadingView;
        }

        private static GameObject CreatePanel(string name, Transform parent, Vector2 size)
        {
            var panel = CreateUiObject(name, parent, typeof(Image), typeof(Outline));
            SetCenteredRect(panel.GetComponent<RectTransform>(), size, Vector2.zero);
            panel.GetComponent<Image>().color = PanelColor;
            panel.GetComponent<Outline>().effectColor = AccentColor;
            panel.GetComponent<Outline>().effectDistance = new Vector2(2f, -2f);
            return panel;
        }

        private static Button CreateButton(Transform parent, string name, string label, Vector2 position, Vector2 size, Anchor anchor = Anchor.Center)
        {
            var buttonObject = CreateUiObject(name, parent, typeof(Image), typeof(Button), typeof(Outline));
            var rect = buttonObject.GetComponent<RectTransform>();
            SetAnchoredRect(rect, size, position, anchor);
            buttonObject.GetComponent<Image>().color = ButtonColor;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = AccentColor;
            outline.effectDistance = new Vector2(2f, -2f);
            var text = CreateText(buttonObject.transform, "Text", label, size.y >= 80f ? 32 : 20, Vector2.zero, Vector2.zero, FontStyle.Bold);
            Stretch(text.rectTransform);
            text.raycastTarget = false;
            return buttonObject.GetComponent<Button>();
        }

        private static Button CreateMainMenuButton(Transform parent, string name, string label, Vector2 position)
        {
            var buttonObject = CreateUiObject(name, parent, typeof(Image), typeof(Button));
            var rect = buttonObject.GetComponent<RectTransform>();
            SetAnchoredRect(rect, new Vector2(420f, 84f), position, Anchor.BottomRight);

            var image = buttonObject.GetComponent<Image>();
            image.color = Color.white;
            image.raycastTarget = true;

            var button = buttonObject.GetComponent<Button>();
            button.transition = Selectable.Transition.ColorTint;
            var colors = button.colors;
            colors.normalColor = Color.clear;
            colors.highlightedColor = new Color(0.78f, 0.63f, 0.38f, 0.22f);
            colors.pressedColor = new Color(0.34f, 0.08f, 0.04f, 0.55f);
            colors.selectedColor = new Color(0.78f, 0.63f, 0.38f, 0.30f);
            colors.disabledColor = new Color(0.24f, 0.22f, 0.18f, 0.12f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            var text = CreateText(
                buttonObject.transform,
                "Text",
                label,
                32,
                Vector2.zero,
                Vector2.zero,
                FontStyle.Bold);
            Stretch(text.rectTransform);
            text.raycastTarget = false;

            var feedback = buttonObject.AddComponent<ActionButtonPressFeedback>();
            feedback.Configure(button, null, AccentColor);
            return button;
        }

        private static Text CreateText(Transform parent, string name, string value, int fontSize, Vector2 position, Vector2 size,
            FontStyle fontStyle = FontStyle.Normal, TextAnchor alignment = TextAnchor.MiddleCenter)
        {
            var textObject = CreateUiObject(name, parent, typeof(Text));
            var rect = textObject.GetComponent<RectTransform>();
            SetCenteredRect(rect, size, position);
            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.font = UiEditorAssetReferences.CjkFont;
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.alignment = alignment;
            text.color = TextColor;
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

        private static void SetCenteredRect(RectTransform rect, Vector2 size, Vector2 position)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void SetAnchoredRect(RectTransform rect, Vector2 size, Vector2 position, Anchor anchor)
        {
            if (anchor == Anchor.BottomLeft)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.zero;
            }
            else if (anchor == Anchor.BottomRight)
            {
                rect.anchorMin = new Vector2(1f, 0f);
                rect.anchorMax = new Vector2(1f, 0f);
            }
            else
            {
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
            }

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

        private static void SetOffsets(RectTransform rect, Vector2 min, Vector2 max)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = min;
            rect.offsetMax = max;
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

        private static void InstallPrefabInStartScene(GameObject prefab)
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                if (roots[i].name == "Start Menu")
                {
                    Object.DestroyImmediate(roots[i]);
                    break;
                }
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.name = "Start Menu";
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        private enum Anchor
        {
            Center,
            BottomLeft,
            BottomRight
        }
    }
}
