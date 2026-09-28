using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation.Editor
{
    /// <summary>仅在 UI-003 明确授权的共享 HUD 源上手动搭建静态视觉节点。</summary>
    public static class Ui003VisualBuilder
    {
        private const string HudPath = "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        private const string SpriteRoot = "Assets/YC/Presentation/Ui002/Sprites/Frontier31/";
        private const string FontRoot = "Assets/YC/Presentation/Ui002/Fonts/";
        private static readonly Color Ink = new Color(.17f, .16f, .14f, 1f);
        private static readonly Color PaperInk = new Color(.87f, .82f, .73f, 1f);

        public static void RunMapCameraInsets()
        {
            var paths = new[]
            {
                "Assets/YC/Presentation/Prefabs/Map/MapView.prefab",
                "Assets/Resources/MapViews/map-three-players.prefab"
            };
            foreach (var path in paths)
            {
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var display = root.GetComponentInChildren<MapDisplayController>(true);
                    if (display == null) throw new InvalidOperationException(path + " 缺少地图显示控制器。");
                    var serialized = new SerializedObject(display);
                    var zoom = serialized.FindProperty("framedViewportZoom");
                    if (zoom == null) throw new InvalidOperationException(path + " 缺少初始视口缩放配置。");
                    zoom.floatValue = 1.2f;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    Debug.Log("UI003 MAP INSET OK " + path);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        public static void RunLegacyBuildFallback()
        {
            var root = PrefabUtility.LoadPrefabContents(HudPath);
            try
            {
                var panel = root.GetComponentInChildren<BuildInfoPanel>(true);
                if (panel == null || panel.View == null) throw new InvalidOperationException("缺少建设面板。");
                var viewRoot = panel.View.Root.gameObject;
                var canvas = viewRoot.GetComponent<Canvas>();
                if (canvas == null) canvas = viewRoot.AddComponent<Canvas>();
                canvas.overrideSorting = true;
                canvas.sortingOrder = 110;
                if (viewRoot.GetComponent<GraphicRaycaster>() == null)
                    viewRoot.AddComponent<GraphicRaycaster>();
                var group = viewRoot.GetComponent<CanvasGroup>();
                if (group == null) group = viewRoot.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                group.interactable = false;
                group.blocksRaycasts = false;
                PrefabUtility.SaveAsPrefabAsset(root, HudPath);
                Debug.Log("UI003 LEGACY BUILD FALLBACK OK " + HudPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static void RunCompactNavigation()
        {
            var root = PrefabUtility.LoadPrefabContents(HudPath);
            try
            {
                var surface = Find(root.transform, "UI003 Main Surface");
                if (surface == null) throw new InvalidOperationException("缺少新版主界面。");
                if (Find(surface, "Compact Region Navigation") != null)
                    throw new InvalidOperationException("紧凑分区导航已存在；拒绝重复覆盖。");
                var nav = DesignRect("Compact Region Navigation", surface, 0, 0, 360, 36);
                var buttons = new Button[3];
                var titles = new[] { "左侧信息", "地图", "行动" };
                for (var i = 0; i < buttons.Length; i++)
                {
                    var rect = SpriteRect(nav, "Region Button " + i, i * 120, 0, 116, 34,
                        i == 1 ? "button-primary" : "button-secondary", true);
                    var image = rect.GetComponent<Image>();
                    image.raycastTarget = true;
                    buttons[i] = rect.gameObject.AddComponent<Button>();
                    buttons[i].targetGraphic = image;
                    TextRect(rect, "Region Label " + i, 2, 0, 112, 34, titles[i], 18,
                        i == 1 ? Ink : PaperInk, TextAnchor.MiddleCenter);
                }
                nav.SetAsLastSibling();
                nav.gameObject.SetActive(false);
                var frame = root.GetComponentInChildren<GameplayHudFrame>(true);
                var serialized = new SerializedObject(frame);
                serialized.FindProperty("compactNavigation").objectReferenceValue = nav;
                serialized.FindProperty("compactNavigationButtons").arraySize = buttons.Length;
                for (var i = 0; i < buttons.Length; i++)
                    serialized.FindProperty("compactNavigationButtons").GetArrayElementAtIndex(i)
                        .objectReferenceValue = buttons[i];
                serialized.FindProperty("compactSelectedSprite").objectReferenceValue = Sprite("button-primary");
                serialized.FindProperty("compactIdleSprite").objectReferenceValue = Sprite("button-secondary");
                serialized.FindProperty("compactSelectedTextColor").colorValue = Ink;
                serialized.FindProperty("compactIdleTextColor").colorValue = PaperInk;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, HudPath);
                Debug.Log("UI003 COMPACT NAVIGATION OK " + HudPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static void RunOuterFrame()
        {
            var root = PrefabUtility.LoadPrefabContents(HudPath);
            try
            {
                var canvas = Find(root.transform, "Mobile City UI Canvas");
                if (canvas == null) throw new InvalidOperationException("共享 HUD 缺少主 UI Canvas。");
                if (Find(canvas, "UI003 Main Surface") != null)
                    throw new InvalidOperationException("UI003 主视觉根节点已存在；拒绝重复覆盖。");

                var surface = Rect("UI003 Main Surface", canvas, Vector2.zero, Vector2.one,
                    Vector2.zero, Vector2.zero);
                var frame = Rect("Outer Frame", surface, Vector2.zero, Vector2.one,
                    Vector2.zero, Vector2.zero);
                var image = frame.gameObject.AddComponent<Image>();
                image.sprite = Sprite("outer-frame");
                image.type = Image.Type.Sliced;
                image.raycastTarget = false;
                PrefabUtility.SaveAsPrefabAsset(root, HudPath);
                Debug.Log("UI003 VISUAL OUTER OK " + HudPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static void RunMainSurface()
        {
            var root = PrefabUtility.LoadPrefabContents(HudPath);
            try
            {
                var canvas = Find(root.transform, "Mobile City UI Canvas");
                var surface = Find(canvas, "UI003 Main Surface");
                if (surface == null) throw new InvalidOperationException("请先运行 RunOuterFrame。");
                if (Find(surface, "Main Regions") != null)
                    throw new InvalidOperationException("主分区已存在；拒绝重复覆盖。");
                var regions = Rect("Main Regions", surface, Vector2.zero, Vector2.one,
                    Vector2.zero, Vector2.zero);
                // 外框只作为主模块的底层装饰，不能覆盖两侧席位和城市边框。
                regions.SetAsLastSibling();

                // 中央透出真实 MapView；其余区域铺独立底层，避免旧世界桌面透到新版模块间隙。
                SpriteRect(regions, "Left Ground", 0, 64, 444, 922, "module-bg");
                SpriteRect(regions, "Right Ground", 1340, 64, 580, 922, "module-bg");
                SpriteRect(regions, "Lower Ground", 444, 742, 896, 244, "module-bg");
                SpriteRect(regions, "Top Seam", 0, 64, 1920, 8, "module-bg");

                for (var i = 0; i < 3; i++)
                {
                    var opponent = Module(regions, "Opponent Seat " + (i + 1), 16, 72 + 116 * i, 416, 108,
                        "对手席位 " + (i + 1), 32);
                    SpriteRect(opponent, "Paper Content", 8, 38, 400, 62, "paper-bg");
                    CardSlot(opponent, "Portrait Slot", 16, 42, 54, 54);
                    TextRect(opponent, "Opponent Empty State", 82, 49, 300, 40,
                        "等待玩家", 17, Ink);
                }

                var city = Module(regions, "City Region", 16, 424, 416, 554, "我的城市", 36);
                var cityArt = DesignRect("City Board Artwork", city, 70, 43, 276, 500);
                var cityImage = cityArt.gameObject.AddComponent<RawImage>();
                cityImage.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                    "Assets/YC/Presentation/Resources/CardImages/Boards/city_board.png");
                if (cityImage.texture == null) throw new InvalidOperationException("缺少原城市板贴图。");
                cityImage.raycastTarget = false;
                var cityAspect = cityArt.gameObject.AddComponent<AspectRatioFitter>();
                cityAspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                cityAspect.aspectRatio = (float)cityImage.texture.width / cityImage.texture.height;

                var map = DesignRect("Map Region", regions, 444, 72, 896, 670);
                SpriteRect(map, "Map Frame", 0, 0, 896, 670, "module-frame", true);
                SpriteRect(map, "Map Label Plate", 16, 16, 148, 34, "header-bg", true);
                TextRect(map, "Map Title", 28, 20, 126, 28, "拓荒地图", 20, Ink);

                var self = Module(regions, "Self Summary Region", 444, 750, 896, 48, null, 0, "paper-bg");
                TextRect(self, "Self Summary Title", 16, 5, 140, 38, "自身状态", 21, Ink);
                TextRect(self, "Self Summary Value", 172, 5, 704, 38,
                    "", 18, Ink);

                var entrepreneurs = Module(regions, "Entrepreneurs Region", 444, 806, 424, 172,
                    "企业家", 34);
                for (var i = 0; i < 5; i++)
                    CardSlot(entrepreneurs, "Entrepreneur Slot " + (i + 1), 12 + 82 * i, 43, 72, 116);

                var hand = Module(regions, "Hand Region", 880, 806, 460, 172, "手牌", 34);
                var handContent = DesignRect("Hand Content", hand, 10, 40, 440, 122);
                SpriteRect(hand, "Hand Content Border", 8, 38, 444, 128, "module-frame", true);

                var cooperation = Module(regions, "Cooperation Region", 1352, 72, 552, 282,
                    "企业合作", 40);
                SpriteRect(cooperation, "Cooperation Paper", 8, 46, 536, 228, "paper-bg");
                TextRect(cooperation, "Cooperation Empty State", 24, 130, 504, 48,
                    "暂无合作资料", 20, Ink);

                var discard = Module(regions, "Discard Region", 1352, 366, 270, 220,
                    "弃牌区", 36);
                var discardContent = DesignRect("Discard Content", discard, 12, 45, 246, 163);
                CardSlot(discardContent, "Discard Card Slot", 80, 8, 86, 142);
                TextRect(discard, "Discard Empty State", 12, 185, 246, 25,
                    "暂无弃牌", 15, PaperInk);

                var faceDown = Module(regions, "Face Down Region", 1634, 366, 270, 220,
                    "盖放角色", 36);
                CardSlot(faceDown, "Face Down Card Slot", 92, 52, 86, 142);
                TextRect(faceDown, "Face Down Empty State", 12, 185, 246, 25,
                    "暂无盖放牌", 15, PaperInk);

                var tabs = DesignRect("Action Tabs", regions, 1352, 598, 552, 48);
                Tab(tabs, 0, "主要行动", "button-primary");
                Tab(tabs, 1, "快速行动", "button-secondary");
                Tab(tabs, 2, "城市样式", "button-secondary");
                var actions = Module(regions, "Action Region", 1352, 654, 552, 324,
                    "主要行动", 42);
                var actionContent = DesignRect("Action Content", actions, 10, 48, 532, 266);

                MoveExistingContent(canvas, handContent, discardContent, actionContent, map);
                PrefabUtility.SaveAsPrefabAsset(root, HudPath);
                Debug.Log("UI003 VISUAL MODULES OK " + HudPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static void RunBarsDetails()
        {
            var root = PrefabUtility.LoadPrefabContents(HudPath);
            try
            {
                var top = Find(root.transform, "Persistent Top Bar") as RectTransform;
                var bottom = Find(root.transform, "Persistent Bottom Bar") as RectTransform;
                var frame = root.GetComponentInChildren<GameplayHudFrame>(true);
                if (top == null || bottom == null || frame == null)
                    throw new InvalidOperationException("共享 HUD 常驻栏接线不完整。");
                if (Find(top, "Brand Mark") != null)
                    throw new InvalidOperationException("顶栏完整视觉节点已存在；拒绝重复覆盖。");

                top.sizeDelta = new Vector2(0, 64);
                SpriteRect(top, "Brand Mark", 28, 12, 40, 40, "icon-frontier");
                TextRect(top, "Brand Title", 83, 8, 150, 50, "游城拓荒", 28, Ink);
                TextRect(top, "Brand Subtitle", 230, 10, 170, 46, "铸基者", 23, Ink);
                TextRect(top, "Round Prefix", 526, 13, 26, 38, "第", 17, Ink);
                var plaque = SpriteRect(top, "Round Plaque", 557, 9, 60, 44,
                    "round-plaque", true);
                var round = Find(top, "Round Number") as RectTransform;
                round.SetParent(plaque, false);
                Stretch(round);
                round.GetComponent<Text>().alignment = TextAnchor.MiddleCenter;
                TextRect(top, "Round Total", 633, 13, 125, 38, "", 20, Ink);
                var ticks = DesignRect("Round Progress", top, 558, 56, 270, 5);
                for (var i = 0; i < 12; i++)
                {
                    var tick = DesignRect("Round Tick " + (i + 1), ticks, i * 22, 0, 16, 3);
                    var image = tick.gameObject.AddComponent<Image>();
                    image.color = new Color(.72f, .63f, .43f, 1f);
                    image.raycastTarget = false;
                }
                TextRect(top, "Phase Caption", 812, 8, 180, 20, "回合阶段", 11, Ink);
                var phase = Find(top, "Current Phase") as RectTransform;
                SetDesign(phase, 811, 27, 220, 32);
                phase.GetComponent<Text>().alignment = TextAnchor.MiddleLeft;
                SpriteRect(top, "Current Action Flag", 1048, 10, 224, 44,
                    "turn-tag-refined", true);
                SpriteRect(top, "Current Action Icon", 1070, 21, 22, 22, "icon-action");
                var actionStatus = TextRect(top, "Current Action Label", 1100, 13, 154, 38,
                    "", 20, Ink, TextAnchor.MiddleCenter);
                SpriteRect(top, "Red Zone Plaque", 1324, 10, 252, 44,
                    "redzone-plaque", true);
                SpriteRect(top, "Red Zone Icon", 1339, 18, 27, 27, "icon-redzone");
                TextRect(top, "Red Zone Caption", 1378, 9, 118, 20,
                    "红区开放", 12, Ink);
                var openRound = TextRect(top, "Red Zone Open Round", 1378, 28, 130, 26,
                    "", 20, Ink);
                var redStatus = Find(top, "Red Zone State") as RectTransform;
                SetDesign(redStatus, 1504, 21, 66, 24);
                redStatus.GetComponent<Text>().fontSize = 12;
                redStatus.GetComponent<Text>().alignment = TextAnchor.MiddleCenter;

                var fold = Find(top, "Effect Fold Button") as RectTransform;
                SetDesign(fold, 1610, 10, 216, 44);
                var foldImage = fold.GetComponent<Image>();
                foldImage.sprite = Sprite("topbar-tool-default");
                foldImage.type = Image.Type.Sliced;
                SpriteRect(fold, "Fold Icon", 15, 10, 24, 24, "icon-panels");
                SpriteRect(fold, "Fold Chevron", 174, 13, 18, 18, "icon-chevron");
                var foldLabel = Find(fold, "Effect Fold Label") as RectTransform;
                SetDesign(foldLabel, 44, 0, 125, 44);
                foldLabel.GetComponent<Text>().alignment = TextAnchor.MiddleCenter;

                var settings = Find(top, "Settings Button") as RectTransform;
                SetDesign(settings, 1840, 10, 60, 44);
                var settingsImage = settings.GetComponent<Image>();
                settingsImage.sprite = Sprite("topbar-tool-default");
                settingsImage.type = Image.Type.Sliced;
                var settingsLabel = Find(settings, "Settings Label");
                settingsLabel.gameObject.SetActive(false);
                SpriteRect(settings, "Settings Icon", 17, 9, 26, 26, "icon-settings");

                bottom.anchorMin = new Vector2(0, 0);
                bottom.anchorMax = new Vector2(1, 0);
                bottom.offsetMin = new Vector2(16, 12);
                bottom.offsetMax = new Vector2(-16, 94);
                var slot = SpriteRect(bottom, "Resolution Summary Slot", 172, 13, 1424, 60,
                    "chain-slot-bg");
                SpriteRect(slot, "Resolution Slot Frame", 0, 0, 1424, 60,
                    "chain-slot-frame", true);
                TextRect(slot, "Resolution Caption", 26, 3, 180, 28,
                    "结算区域", 19, PaperInk);
                var summary = Find(bottom, "Action Summary") as RectTransform;
                var shortSummary = Find(bottom, "Short Action Summary") as RectTransform;
                summary.SetParent(slot, false);
                shortSummary.SetParent(slot, false);
                SetDesign(summary, 232, 7, 1120, 48);
                SetDesign(shortSummary, 232, 7, 1120, 48);

                var undo = Find(bottom, "Undo Button") as RectTransform;
                SetDesign(undo, 20, 19, 132, 48);
                var undoImage = undo.GetComponent<Image>();
                undoImage.sprite = Sprite("undo-disabled");
                undoImage.type = Image.Type.Sliced;
                SpriteRect(undo, "Undo Icon", 12, 10, 28, 28,
                    "icon-undo-muted");
                var undoLabel = Find(undo, "Undo Label") as RectTransform;
                SetDesign(undoLabel, 40, 3, 87, 42);
                undoLabel.GetComponent<Text>().alignment = TextAnchor.MiddleCenter;
                undoLabel.GetComponent<Text>().text = "撤销\n暂不可用";
                undoLabel.GetComponent<Text>().fontSize = 15;

                var end = Find(bottom, "结束本回合 Button") as RectTransform;
                SetDesign(end, 1624, 17, 236, 52);
                var endImage = end.GetComponent<Image>();
                endImage.sprite = Sprite("button-primary");
                endImage.type = Image.Type.Sliced;
                var endButton = end.GetComponent<Button>();
                endButton.transition = Selectable.Transition.SpriteSwap;
                var spriteState = endButton.spriteState;
                spriteState.disabledSprite = Sprite("button-disabled");
                endButton.spriteState = spriteState;
                var endLabel = Find(end, "Label").GetComponent<Text>();
                endLabel.text = "结束行动";
                endLabel.font = Font("HanYiCuHeiJian-1.ttf");
                endLabel.fontStyle = FontStyle.Normal;
                endLabel.fontSize = 22;

                var serialized = new SerializedObject(frame);
                serialized.FindProperty("wideTopHeight").floatValue = 64;
                serialized.FindProperty("wideBottomHeight").floatValue = 82;
                serialized.FindProperty("bottomScreenMargin").floatValue = 12;
                serialized.FindProperty("redZoneClosedText").stringValue = "未开放";
                serialized.FindProperty("redZoneOpenText").stringValue = "已开放";
                serialized.FindProperty("roundTotalText").objectReferenceValue =
                    Find(top, "Round Total").GetComponent<Text>();
                serialized.FindProperty("redZoneOpenRoundText").objectReferenceValue =
                    openRound.GetComponent<Text>();
                serialized.FindProperty("turnTagText").objectReferenceValue =
                    actionStatus.GetComponent<Text>();
                serialized.FindProperty("roundTicks").arraySize = 12;
                for (var i = 0; i < 12; i++)
                    serialized.FindProperty("roundTicks").GetArrayElementAtIndex(i).objectReferenceValue =
                        Find(ticks, "Round Tick " + (i + 1)).GetComponent<Image>();
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, HudPath);
                Debug.Log("UI003 VISUAL BARS OK " + HudPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static void RunSurfaceLayer()
        {
            var root = PrefabUtility.LoadPrefabContents(HudPath);
            try
            {
                var surface = Find(root.transform, "UI003 Main Surface");
                if (surface == null) throw new InvalidOperationException("缺少 UI003 主视觉根。");
                var canvas = surface.GetComponent<Canvas>();
                if (canvas == null) canvas = surface.gameObject.AddComponent<Canvas>();
                canvas.overrideSorting = true;
                canvas.sortingOrder = 100;
                if (surface.GetComponent<GraphicRaycaster>() == null)
                    surface.gameObject.AddComponent<GraphicRaycaster>();
                var frame = root.GetComponentInChildren<GameplayHudFrame>(true);
                var mapRegion = Find(surface, "Map Region") as RectTransform;
                var cityArtwork = Find(surface, "City Board Artwork");
                if (cityArtwork != null)
                {
                    cityArtwork.gameObject.SetActive(true);
                    var artworkRect = (RectTransform)cityArtwork;
                    var fitter = artworkRect.GetComponent<AspectRatioFitter>();
                    if (fitter != null) UnityEngine.Object.DestroyImmediate(fitter, true);
                    SetDesign(artworkRect, 76, 43, 270, 500);
                }
                var frameData = new SerializedObject(frame);
                frameData.FindProperty("mapRegion").objectReferenceValue = mapRegion;
                frameData.ApplyModifiedPropertiesWithoutUndo();
                foreach (var name in new[] { "Left Ground", "Right Ground", "Lower Ground" })
                {
                    var ground = Find(surface, name);
                    if (ground != null) ground.GetComponent<Image>().raycastTarget = true;
                }
                foreach (var image in surface.GetComponentsInChildren<Image>(true))
                    if (image.gameObject.name == "Module Background") image.raycastTarget = true;
                var actions = Find(surface, "Action Region");
                if (actions != null)
                {
                    var face = Find(actions, "Main Action Face");
                    if (face != null)
                    {
                        foreach (var name in new[] { "主要行动 Text", "Local Player Color Swatch",
                                 "Remaining Influence Text" })
                        {
                            var item = Find(face, name);
                            if (item != null) item.gameObject.SetActive(false);
                        }
                    }
                }
                var round = Find(root.transform, "Round Number").GetComponent<Text>();
                round.color = Color.white;
                PrefabUtility.SaveAsPrefabAsset(root, HudPath);
                Debug.Log("UI003 VISUAL LAYER OK " + HudPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static void ReportCityBoard()
        {
            var root = PrefabUtility.LoadPrefabContents(HudPath);
            try
            {
                var panel = root.GetComponentInChildren<BuildInfoPanel>(true);
                var view = panel.View;
                var board = view.CityBoardRoot;
                foreach (var target in new[] { view.Root, view.PanelTransform, view.ContentArea,
                         view.ContentRoot, view.ExternalFacilityArea, view.ExternalCityStyleArea })
                    Debug.Log("UI003 BUILD PANEL " + target.name + " parent=" + target.parent.name +
                              " anchor=" + target.anchorMin + "/" + target.anchorMax +
                              " pos=" + target.anchoredPosition + " size=" + target.sizeDelta +
                              " scale=" + target.localScale);
                Debug.Log("UI003 CITY BOARD root=" + board.name + " parent=" + board.parent.name +
                          " size=" + board.sizeDelta + " anchor=" + board.anchorMin +
                          " scale=" + board.localScale + " children=" + board.childCount +
                          " worldCanvas=" + board.GetComponentInParent<Canvas>().name +
                          " image=" + view.CityBoardImage.name);
                foreach (var rect in board.GetComponentsInChildren<RectTransform>(true))
                    Debug.Log("UI003 CITY NODE " + rect.name + " anchor=" + rect.anchorMin +
                              "/" + rect.anchorMax + " pos=" + rect.anchoredPosition +
                              " size=" + rect.sizeDelta + " child=" + rect.childCount);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static void RunContentDetails()
        {
            var root = PrefabUtility.LoadPrefabContents(HudPath);
            try
            {
                var surface = Find(root.transform, "UI003 Main Surface");
                var self = Find(surface, "Self Summary Region");
                var cooperation = Find(surface, "Cooperation Region");
                if (self == null || cooperation == null)
                    throw new InvalidOperationException("主视觉结构尚未搭建。");
                if (Find(self, "Resource Values") != null)
                    throw new InvalidOperationException("信息槽已存在；拒绝重复覆盖。");

                var values = DesignRect("Resource Values", self, 210, 4, 670, 40);
                var resourceFiles = new[] { "源岩.png", "源石碎片.png", "异铁.png",
                    "至纯源石.png" };
                var resourceTexts = new Text[5];
                for (var i = 0; i < resourceFiles.Length; i++)
                {
                    var chip = DesignRect("Resource Chip " + i, values, i * 128, 2, 116, 36);
                    var icon = DesignRect("Resource Icon", chip, 0, 2, 30, 30)
                        .gameObject.AddComponent<RawImage>();
                    icon.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                        "Assets/YC/Data/ResourceIcons/" + resourceFiles[i]);
                    if (icon.texture == null) throw new InvalidOperationException("缺少资源图标：" + resourceFiles[i]);
                    icon.raycastTarget = false;
                    resourceTexts[i] = TextRect(chip, "Resource Amount", 38, 0, 74, 36,
                        "—", 21, Ink).GetComponent<Text>();
                    resourceTexts[i].font = Font("Novecento NarrowBold.otf");
                }
                var voucher = DesignRect("Voucher Chip", values, 512, 2, 150, 36);
                TextRect(voucher, "Voucher Caption", 0, 0, 54, 36, "金券", 17, Ink);
                resourceTexts[4] = TextRect(voucher, "Voucher Amount", 56, 0, 90, 36,
                    "—", 21, Ink).GetComponent<Text>();
                resourceTexts[4].font = Font("Novecento NarrowBold.otf");

                var playerText = Find(self, "Self Summary Value").GetComponent<Text>();
                SetDesign(playerText.rectTransform, 110, 5, 96, 38);
                var frame = root.GetComponentInChildren<GameplayHudFrame>(true);
                var serialized = new SerializedObject(frame);
                serialized.FindProperty("selfNameText").objectReferenceValue = playerText;
                serialized.FindProperty("resourceValueTexts").arraySize = resourceTexts.Length;
                for (var i = 0; i < resourceTexts.Length; i++)
                    serialized.FindProperty("resourceValueTexts").GetArrayElementAtIndex(i)
                        .objectReferenceValue = resourceTexts[i];
                serialized.ApplyModifiedPropertiesWithoutUndo();

                var rows = DesignRect("Cooperation Rows", cooperation, 12, 48, 528, 222);
                for (var i = 0; i < 4; i++)
                {
                    var row = DesignRect("Cooperation Row " + (i + 1), rows, 0, i * 53, 528, 49);
                    SpriteRect(row, "Row Border", 0, 0, 528, 49, "module-frame", true);
                    SpriteRect(row, "Emblem Slot", 8, 7, 35, 35, "card-bg");
                    TextRect(row, "Row Empty", 54, 3, 150, 43,
                        "未建立合作", 16, Ink);
                    for (var grade = 0; grade < 6; grade++)
                        SpriteRect(row, "Grade Cell " + grade,
                            224 + grade * 49, 14, 23, 23, "card-bg", true);
                }
                Find(cooperation, "Cooperation Empty State").gameObject.SetActive(false);
                PrefabUtility.SaveAsPrefabAsset(root, HudPath);
                Debug.Log("UI003 VISUAL DETAILS OK " + HudPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static void RunBarResponsiveContainers()
        {
            var root = PrefabUtility.LoadPrefabContents(HudPath);
            try
            {
                var top = Find(root.transform, "Persistent Top Bar") as RectTransform;
                var bottom = Find(root.transform, "Persistent Bottom Bar") as RectTransform;
                if (Find(top, "Top Bar Content") != null || Find(bottom, "Bottom Bar Content") != null)
                    throw new InvalidOperationException("响应式栏容器已存在；拒绝重复覆盖。");
                var topContent = DesignRect("Top Bar Content", top, 0, 0, 1920, 64);
                var bottomContent = DesignRect("Bottom Bar Content", bottom, 0, 0, 1888, 82);
                ReparentBarChildren(top, topContent, "Top Frame");
                ReparentBarChildren(bottom, bottomContent, "Bottom Frame");
                var frame = root.GetComponentInChildren<GameplayHudFrame>(true);
                var serialized = new SerializedObject(frame);
                serialized.FindProperty("topBarContent").objectReferenceValue = topContent;
                serialized.FindProperty("bottomBarContent").objectReferenceValue = bottomContent;
                serialized.FindProperty("mainSurface").objectReferenceValue =
                    Find(root.transform, "UI003 Main Surface") as RectTransform;
                serialized.FindProperty("mainRegions").objectReferenceValue =
                    Find(root.transform, "Main Regions") as RectTransform;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, HudPath);
                Debug.Log("UI003 RESPONSIVE BAR CONTENT OK " + HudPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static void RunMapBackdrops()
        {
            var root = PrefabUtility.LoadPrefabContents(HudPath);
            try
            {
                var surface = Find(root.transform, "UI003 Main Surface");
                if (Find(surface, "Map Backdrop Left") != null)
                    throw new InvalidOperationException("地图外侧底层已存在；拒绝重复覆盖。");
                var names = new[] { "Map Backdrop Left", "Map Backdrop Right",
                    "Map Backdrop Top", "Map Backdrop Bottom" };
                var backdrops = new RectTransform[names.Length];
                for (var i = 0; i < names.Length; i++)
                {
                    backdrops[i] = SpriteRect(surface, names[i], 0, 0, 1, 1, "module-bg");
                    backdrops[i].GetComponent<Image>().raycastTarget = true;
                    backdrops[i].SetSiblingIndex(i);
                }
                var frame = root.GetComponentInChildren<GameplayHudFrame>(true);
                var serialized = new SerializedObject(frame);
                serialized.FindProperty("mapBackdrops").arraySize = backdrops.Length;
                for (var i = 0; i < backdrops.Length; i++)
                    serialized.FindProperty("mapBackdrops").GetArrayElementAtIndex(i)
                        .objectReferenceValue = backdrops[i];
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, HudPath);
                Debug.Log("UI003 MAP BACKDROPS OK " + HudPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void ReparentBarChildren(RectTransform bar, RectTransform content,
            string frameName)
        {
            var children = new Transform[bar.childCount];
            for (var i = 0; i < children.Length; i++) children[i] = bar.GetChild(i);
            foreach (var child in children)
            {
                if (child == content || child.name == frameName) continue;
                child.SetParent(content, false);
            }
            content.SetAsFirstSibling();
            var frame = Find(bar, frameName);
            if (frame != null) frame.SetAsLastSibling();
        }

        private static void MoveExistingContent(Transform canvas, Transform hand, Transform discard,
            Transform actions, Transform map)
        {
            var handPanel = Find(canvas, "CharacterHandPanel");
            if (handPanel != null)
            {
                var cards = Find(handPanel, "Hand Cards");
                if (cards != null)
                {
                    cards.SetParent(hand, false);
                    Stretch((RectTransform)cards);
                }
                var discardButton = Find(handPanel, "Discard Pile Button");
                if (discardButton != null)
                {
                    var rect = (RectTransform)discardButton;
                    rect.SetParent(discard, false);
                    rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
                    rect.anchoredPosition = Vector2.zero;
                }
            }
            var panel = Find(canvas, "Action Panel") as RectTransform;
            if (panel != null)
            {
                panel.SetParent(actions, false);
                Stretch(panel);
                var background = panel.GetComponent<Image>();
                if (background != null)
                {
                    background.color = Color.clear;
                    background.raycastTarget = false;
                }
                var face = Find(panel, "Main Action Face");
                if (face != null)
                {
                    foreach (var item in new[] { "当前玩家 Text", "阶段 Text", "快速行动 Text",
                             "状态 Text" })
                    {
                        var node = Find(face, item);
                        if (node != null) node.gameObject.SetActive(false);
                    }
                    var positions = new[]
                    {
                        ("使用角色牌 Button", -136f, -87f), ("宣告样式 Button", 136f, -87f),
                        ("部署 Button", -136f, -150f), ("调度 Button", 136f, -150f),
                        ("探索 Button", -136f, -213f), ("城市移动 Button", 136f, -213f)
                    };
                    foreach (var item in positions)
                    {
                        var button = Find(face, item.Item1) as RectTransform;
                        if (button == null) continue;
                        button.anchoredPosition = new Vector2(item.Item2, item.Item3);
                        button.sizeDelta = new Vector2(246, 54);
                        var image = button.GetComponent<Image>();
                        if (image != null)
                        {
                            image.sprite = Sprite("button-secondary");
                            image.type = Image.Type.Sliced;
                        }
                    }
                }
            }
            var prompt = Find(canvas, "Prompt Panel") as RectTransform;
            if (prompt != null)
            {
                prompt.SetParent(map, false);
                prompt.anchorMin = prompt.anchorMax = new Vector2(.5f, 1f);
                prompt.pivot = new Vector2(.5f, 1f);
                prompt.anchoredPosition = new Vector2(0, -58);
                prompt.sizeDelta = new Vector2(590, 80);
            }
            var resource = Find(canvas, "ResourceCounterBoard");
            if (resource != null)
            {
                var group = resource.GetComponent<CanvasGroup>();
                if (group == null) group = resource.gameObject.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                group.blocksRaycasts = false;
                group.interactable = false;
            }
        }

        private static RectTransform Module(Transform parent, string name, float x, float y,
            float width, float height, string title, float headerHeight, string fill = "module-bg")
        {
            var root = DesignRect(name, parent, x, y, width, height);
            SpriteRect(root, "Module Background", 0, 0, width, height, fill);
            if (!string.IsNullOrEmpty(title))
            {
                SpriteRect(root, "Header Background", 4, 4, width - 8, headerHeight, "header-bg", true);
                TextRect(root, "Header Title", 16, 5, width - 32, headerHeight - 2,
                    title, 20, Ink);
            }
            SpriteRect(root, "Module Frame", 0, 0, width, height, "module-frame", true);
            return root;
        }

        private static void Tab(Transform parent, int index, string title, string id)
        {
            var x = index * 186f;
            SpriteRect(parent, "Tab Background " + index, x, 0, 180, 48, id, true);
            TextRect(parent, "Tab Label " + index, x, 2, 180, 44, title, 21,
                index == 0 ? Ink : PaperInk, TextAnchor.MiddleCenter);
        }

        private static void CardSlot(Transform parent, string name, float x, float y,
            float width, float height)
        {
            var slot = DesignRect(name, parent, x, y, width, height);
            SpriteRect(slot, "Card Background", 0, 0, width, height, "card-bg");
            SpriteRect(slot, "Card Frame", 0, 0, width, height, "card-frame", true);
        }

        private static RectTransform SpriteRect(Transform parent, string name, float x, float y,
            float width, float height, string id, bool sliced = false)
        {
            var rect = DesignRect(name, parent, x, y, width, height);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = Sprite(id);
            image.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
            image.raycastTarget = false;
            return rect;
        }

        private static RectTransform TextRect(Transform parent, string name, float x, float y,
            float width, float height, string value, int size, Color color,
            TextAnchor alignment = TextAnchor.MiddleLeft)
        {
            var rect = DesignRect(name, parent, x, y, width, height);
            var label = rect.gameObject.AddComponent<Text>();
            label.text = value;
            label.font = AssetDatabase.LoadAssetAtPath<Font>(FontRoot +
                "FangZhengHeiTiJianTi-1.ttf");
            label.fontSize = size;
            label.fontStyle = FontStyle.Normal;
            label.color = color;
            label.alignment = alignment;
            label.raycastTarget = false;
            return rect;
        }

        private static RectTransform DesignRect(string name, Transform parent, float x, float y,
            float width, float height)
        {
            var rect = Rect(name, parent, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero);
            rect.pivot = Vector2.up;
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        private static void SetDesign(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = Vector2.up;
            rect.pivot = Vector2.up;
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static Font Font(string file)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontRoot + file);
            if (font == null) throw new InvalidOperationException("缺少 UI-002 字体：" + file);
            return font;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static Transform Find(Transform root, string name)
        {
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = Find(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 min, Vector2 max,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            return rect;
        }

        private static Sprite Sprite(string id)
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteRoot + id + ".png");
            if (sprite == null) throw new InvalidOperationException("缺少 UI-002 Sprite：" + id);
            return sprite;
        }
    }
}
