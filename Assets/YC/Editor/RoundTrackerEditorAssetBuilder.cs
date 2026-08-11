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
    public static class RoundTrackerEditorAssetBuilder
    {
        public const string RoundTrackerPrefabPath =
            "Assets/YC/Presentation/Prefabs/RoundTracker/RoundTracker.prefab";
        public const string InfrastructurePrefabPath =
            "Assets/YC/Presentation/Prefabs/Infrastructure/SampleSceneUiInfrastructure.prefab";
        public const string CircleAssetPath =
            "Assets/YC/Presentation/Sprites/RoundTrackerCircle.asset";
        public const string SampleScenePath = "Assets/Scenes/SampleScene.unity";

        private const float PanelHeight = 78f;
        private const float PanelWidth = 720f;
        private const float BorderThickness = PanelHeight * 0.2f;

        private static readonly string[] RoundLabels =
        {
            "START", "1", "2", "3", "4", "5", "6", "7", "8", "FINAL"
        };

        [MenuItem("Tools/YC/Rebuild Round Tracker Editor Assets")]
        public static void Rebuild()
        {
            YC.Editor.UiThemeBuildReadiness.InitializeRequiredTheme();
            EnsureFolder("Assets/YC/Presentation/Prefabs/RoundTracker");
            EnsureFolder("Assets/YC/Presentation/Prefabs/Infrastructure");
            EnsureFolder("Assets/YC/Presentation/Sprites");

            var circle = BuildCircleSpriteAsset();
            var roundTrackerPrefab = BuildRoundTrackerPrefab(circle);
            var infrastructurePrefab = BuildInfrastructurePrefab();
            InstallInSampleScene(roundTrackerPrefab, infrastructurePrefab);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[RoundTrackerEditorAssetBuilder] 已重建回合追踪器、场景 UI 基础设施并接入 SampleScene。");
        }

        private static Sprite BuildCircleSpriteAsset()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(CircleAssetPath);
            if (existing != null)
            {
                return existing;
            }

            const int size = 64;
            const float radius = 27f;
            const float thickness = 7f;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "RoundTrackerCircleTexture",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var distance = Vector2.Distance(new Vector2(x, y), center);
                    var alpha = distance <= radius && distance >= radius - thickness ? 1f : 0f;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            AssetDatabase.CreateAsset(texture, CircleAssetPath);
            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                size);
            sprite.name = "RoundTrackerCircle";
            AssetDatabase.AddObjectToAsset(sprite, texture);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(CircleAssetPath, ImportAssetOptions.ForceUpdate);

            var assets = AssetDatabase.LoadAllAssetsAtPath(CircleAssetPath);
            for (var i = 0; i < assets.Length; i++)
            {
                if (assets[i] is Sprite loadedSprite)
                {
                    return loadedSprite;
                }
            }

            throw new InvalidOperationException("无法创建回合标记 Sprite 资产。");
        }

        private static GameObject BuildRoundTrackerPrefab(Sprite circleSprite)
        {
            var root = new GameObject("RoundTracker");
            try
            {
                var canvasObject = CreateUiObject(
                    "Round UI Canvas",
                    root.transform,
                    typeof(Canvas),
                    typeof(CanvasScaler),
                    typeof(GraphicRaycaster));
                var canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 20;
                var scaler = canvasObject.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = UiTheme.CanvasReferenceResolution;
                scaler.matchWidthOrHeight = UiTheme.CanvasMatchWidthOrHeight;

                var panelObject = CreateUiObject("Round Panel", canvasObject.transform, typeof(Image));
                var panel = panelObject.GetComponent<RectTransform>();
                SetAnchoredRect(panel, new Vector2(PanelWidth, PanelHeight), Vector2.zero, Anchor.TopCenter);
                panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
                CreateBorder(panel, "Round Border Top", Vector2.up, Vector2.one, new Vector2(0.5f, 1f), new Vector2(0f, BorderThickness));
                CreateBorder(panel, "Round Border Bottom", Vector2.zero, Vector2.right, new Vector2(0.5f, 0f), new Vector2(0f, BorderThickness));
                CreateBorder(panel, "Round Border Left", Vector2.zero, Vector2.up, new Vector2(0f, 0.5f), new Vector2(BorderThickness, 0f));
                CreateBorder(panel, "Round Border Right", Vector2.right, Vector2.one, new Vector2(1f, 0.5f), new Vector2(BorderThickness, 0f));

                var contentObject = CreateUiObject("Content Area", panel, Array.Empty<Type>());
                var content = contentObject.GetComponent<RectTransform>();
                Stretch(content);
                content.offsetMin = new Vector2(BorderThickness, BorderThickness);
                content.offsetMax = new Vector2(-BorderThickness, -BorderThickness);

                var trackObject = CreateUiObject("Round Track", content, Array.Empty<Type>());
                var track = trackObject.GetComponent<RectTransform>();
                SetAnchoredRect(track, new Vector2(650f, 42f), Vector2.zero, Anchor.Center);
                CreateTrackBand(track, "Start Band", 0, 3, UiTheme.SafeBand);
                CreateTrackBand(track, "Danger Band", 4, 9, UiTheme.DangerBand);

                var slotsObject = CreateUiObject("Round Slots", track, Array.Empty<Type>());
                var slots = slotsObject.GetComponent<RectTransform>();
                SetAnchoredRect(slots, new Vector2(650f, 54f), new Vector2(0f, 4f), Anchor.Center);
                for (var i = 0; i < RoundLabels.Length; i++)
                {
                    CreateRoundLabel(slots, i);
                }

                var markerContainerObject = CreateUiObject("Player Marker Container", slots, Array.Empty<Type>());
                var markerContainer = markerContainerObject.GetComponent<RectTransform>();
                Stretch(markerContainer);

                var gameOver = BuildGameOverShell(canvasObject.transform, out var shell);
                var templates = BuildTemplates(canvasObject.transform, circleSprite, out var templateRefs);
                templates.SetActive(true);

                var view = root.AddComponent<RoundTrackerView>();
                SetReferences(
                    view,
                    ("canvasTransform", canvasObject.GetComponent<RectTransform>()),
                    ("panelTransform", panel),
                    ("contentArea", content),
                    ("trackSlotsTransform", slots),
                    ("markerContainer", markerContainer),
                    ("gameOverOverlay", gameOver),
                    ("finalScoreSummaryView", shell.Summary),
                    ("finalScoreDetailsView", shell.Details),
                    ("finalScoreMessage", shell.Message),
                    ("finalScoreMessageText", shell.MessageText),
                    ("winnerText", shell.WinnerText),
                    ("tiebreakText", shell.TiebreakText),
                    ("finalScoreDetailsButtonLabel", shell.DetailsButtonLabel),
                    ("finalScoreDetailsButton", shell.DetailsButton),
                    ("finalScoreDetailsBackButton", shell.BackButton),
                    ("returnStartButton", shell.ReturnButton),
                    ("rankingRowsContainer", shell.RankingRows),
                    ("detailPlayerRowsContainer", shell.DetailPlayerRows),
                    ("detailChartsContainer", shell.DetailCharts),
                    ("playerMarkerTemplate", templateRefs.PlayerMarker),
                    ("rankingRowTemplate", templateRefs.RankingRow),
                    ("detailPlayerRowTemplate", templateRefs.DetailPlayerRow),
                    ("scoreChartTemplate", templateRefs.ScoreChart),
                    ("scoreBarTemplate", templateRefs.ScoreBar));

                var controller = root.AddComponent<RoundTrackerController>();
                SetReferences(controller, ("view", view));
                PrefabUtility.SaveAsPrefabAsset(root, RoundTrackerPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            return AssetDatabase.LoadAssetAtPath<GameObject>(RoundTrackerPrefabPath);
        }

        private static GameObject BuildGameOverShell(Transform parent, out GameOverShell shell)
        {
            var overlay = CreateUiObject("Game Over Overlay", parent, typeof(Image));
            Stretch(overlay.GetComponent<RectTransform>());
            overlay.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.62f);

            var dialog = CreateUiObject("Game Over Dialog", overlay.transform, typeof(Image), typeof(Outline));
            SetAnchoredRect(dialog.GetComponent<RectTransform>(), new Vector2(900f, 650f), Vector2.zero, Anchor.Center);
            dialog.GetComponent<Image>().color = new Color(0.16f, 0.1f, 0.055f, 0.98f);
            dialog.GetComponent<Outline>().effectColor = new Color(0.78f, 0.63f, 0.38f, 0.95f);
            dialog.GetComponent<Outline>().effectDistance = new Vector2(4f, -4f);

            var title = CreateText(dialog.transform, "Game Over Text", "游戏结束", 52, FontStyle.Bold, TextAnchor.MiddleCenter);
            SetStretch(title.rectTransform, new Vector2(28f, 0f), new Vector2(-28f, -18f), new Vector2(0f, 0.84f), Vector2.one);

            var winnerBar = CreateUiObject("Final Score Winner Bar", dialog.transform, Array.Empty<Type>());
            SetStretch(winnerBar.GetComponent<RectTransform>(), new Vector2(40f, 0f), new Vector2(-40f, 0f), new Vector2(0f, 0.765f), new Vector2(1f, 0.85f));
            var winner = CreateText(winnerBar.transform, "Final Score Winner", "胜者：待定", 30, FontStyle.Bold, TextAnchor.MiddleCenter);
            SetStretch(winner.rectTransform, Vector2.zero, new Vector2(-170f, 0f));
            var detailsButton = CreateButton(winnerBar.transform, "Final Score Details Button", "详情  >", 22);
            SetAnchoredRect(detailsButton.GetComponent<RectTransform>(), new Vector2(150f, 46f), Vector2.zero, Anchor.MiddleRight);
            var detailsButtonLabel = detailsButton.GetComponentInChildren<Text>();

            var summary = CreateUiObject("Final Score Summary View", dialog.transform, Array.Empty<Type>());
            Stretch(summary.GetComponent<RectTransform>());
            var board = CreateUiObject("Final Score Board", summary.transform, typeof(Image));
            SetStretch(board.GetComponent<RectTransform>(), new Vector2(44f, 0f), new Vector2(-44f, 0f), new Vector2(0f, 0.265f), new Vector2(1f, 0.765f));
            board.GetComponent<Image>().color = new Color(0.07f, 0.045f, 0.025f, 0.72f);
            BuildScoreboardHeader(board.transform);
            var rankingRowsObject = CreateUiObject("Final Score Ranking Rows", board.transform, typeof(VerticalLayoutGroup));
            var rankingRows = rankingRowsObject.GetComponent<RectTransform>();
            SetStretch(rankingRows, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(1f, 0.8f));
            ConfigureVerticalLayout(rankingRowsObject.GetComponent<VerticalLayoutGroup>(), 0f);

            var tiebreak = CreateText(summary.transform, "Final Score Tiebreak", string.Empty, 19, FontStyle.Normal, TextAnchor.MiddleCenter);
            SetStretch(tiebreak.rectTransform, new Vector2(46f, 0f), new Vector2(-46f, 0f), new Vector2(0f, 0.145f), new Vector2(1f, 0.26f));
            var message = CreateText(summary.transform, "Final Score Message", string.Empty, 28, FontStyle.Normal, TextAnchor.MiddleCenter);
            SetStretch(message.rectTransform, new Vector2(40f, 0f), new Vector2(-40f, 0f), new Vector2(0f, 0.3f), new Vector2(1f, 0.75f));

            var details = CreateUiObject("Final Score Details View", dialog.transform, typeof(Image), typeof(Outline));
            SetStretch(details.GetComponent<RectTransform>(), new Vector2(44f, 0f), new Vector2(-44f, 0f), new Vector2(0f, 0.18f), new Vector2(1f, 0.765f));
            details.GetComponent<Image>().color = new Color(0.07f, 0.045f, 0.025f, 0.96f);
            details.GetComponent<Outline>().effectColor = new Color(0.56f, 0.42f, 0.2f, 0.9f);
            details.GetComponent<Outline>().effectDistance = new Vector2(2f, -2f);
            var backButton = CreateButton(details.transform, "Final Score Details Back Button", "< 返回", 18);
            SetStretch(backButton.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero, new Vector2(0.018f, 0.855f), new Vector2(0.17f, 0.975f));
            var detailsTitle = CreateText(details.transform, "Final Score Details Title", "计分详情  →", 25, FontStyle.Bold, TextAnchor.MiddleCenter);
            SetStretch(detailsTitle.rectTransform, Vector2.zero, Vector2.zero, new Vector2(0.19f, 0.855f), new Vector2(0.98f, 0.985f));

            var playerList = CreateUiObject("Final Score Details Player List", details.transform, typeof(Image));
            SetStretch(playerList.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero, new Vector2(0.018f, 0.035f), new Vector2(0.31f, 0.835f));
            playerList.GetComponent<Image>().color = new Color(0.13f, 0.08f, 0.04f, 0.92f);
            var playerHeader = CreateText(playerList.transform, "Final Score Details Player Header", "切换玩家", 19, FontStyle.Bold, TextAnchor.MiddleCenter);
            SetStretch(playerHeader.rectTransform, Vector2.zero, Vector2.zero, new Vector2(0f, 0.82f), Vector2.one);
            var playerRowsObject = CreateUiObject("Final Score Detail Player Rows", playerList.transform, typeof(VerticalLayoutGroup));
            var playerRows = playerRowsObject.GetComponent<RectTransform>();
            SetStretch(playerRows, new Vector2(4f, 4f), new Vector2(-4f, -2f), new Vector2(0f, 0f), new Vector2(1f, 0.8f));
            ConfigureVerticalLayout(playerRowsObject.GetComponent<VerticalLayoutGroup>(), 4f);

            var chartHost = CreateUiObject("Final Score Detail Chart Host", details.transform, typeof(Image));
            SetStretch(chartHost.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero, new Vector2(0.33f, 0.035f), new Vector2(0.982f, 0.835f));
            chartHost.GetComponent<Image>().color = new Color(0.115f, 0.072f, 0.036f, 0.94f);
            var charts = CreateUiObject("Final Score Detail Charts", chartHost.transform, Array.Empty<Type>()).GetComponent<RectTransform>();
            SetStretch(charts, new Vector2(14f, 8f), new Vector2(-14f, -8f));

            var returnButton = CreateButton(dialog.transform, "Return Start Button", "返回开始页面", 30);
            SetAnchoredRect(returnButton.GetComponent<RectTransform>(), new Vector2(390f, 68f), new Vector2(0f, 24f), Anchor.BottomCenter);

            details.SetActive(false);
            message.gameObject.SetActive(false);
            overlay.SetActive(false);

            shell = new GameOverShell
            {
                Summary = summary,
                Details = details,
                Message = message.gameObject,
                MessageText = message,
                WinnerText = winner,
                TiebreakText = tiebreak,
                DetailsButtonLabel = detailsButtonLabel,
                DetailsButton = detailsButton.GetComponent<Button>(),
                BackButton = backButton.GetComponent<Button>(),
                ReturnButton = returnButton.GetComponent<Button>(),
                RankingRows = rankingRows,
                DetailPlayerRows = playerRows,
                DetailCharts = charts
            };
            return overlay;
        }

        private static GameObject BuildTemplates(Transform parent, Sprite circleSprite, out TemplateRefs refs)
        {
            var templates = CreateUiObject("Dynamic Templates", parent, Array.Empty<Type>());
            var templatesRect = templates.GetComponent<RectTransform>();
            SetAnchoredRect(templatesRect, new Vector2(10f, 10f), Vector2.zero, Anchor.BottomLeft);

            var markerObject = CreateUiObject("Player Marker Template", templates.transform, typeof(Image), typeof(Outline));
            SetAnchoredRect(markerObject.GetComponent<RectTransform>(), new Vector2(34f, 34f), Vector2.zero, Anchor.Center);
            markerObject.GetComponent<Image>().sprite = circleSprite;
            markerObject.GetComponent<Image>().color = Color.white;
            markerObject.GetComponent<Outline>().effectColor = new Color(0.06f, 0.04f, 0.025f, 0.9f);
            markerObject.GetComponent<Outline>().effectDistance = new Vector2(2f, -2f);
            var markerView = markerObject.AddComponent<RoundTrackerPlayerMarkerView>();
            SetReferences(markerView, ("markerTransform", markerObject.GetComponent<RectTransform>()), ("markerImage", markerObject.GetComponent<Image>()));

            var ranking = BuildRankingRowTemplate(templates.transform);
            var detailPlayer = BuildDetailPlayerRowTemplate(templates.transform);
            var chart = BuildScoreChartTemplate(templates.transform);
            var bar = BuildScoreBarTemplate(templates.transform);

            markerObject.SetActive(false);
            ranking.gameObject.SetActive(false);
            detailPlayer.gameObject.SetActive(false);
            chart.gameObject.SetActive(false);
            bar.gameObject.SetActive(false);
            refs = new TemplateRefs
            {
                PlayerMarker = markerView,
                RankingRow = ranking,
                DetailPlayerRow = detailPlayer,
                ScoreChart = chart,
                ScoreBar = bar
            };
            return templates;
        }

        private static RoundTrackerRankingRowView BuildRankingRowTemplate(Transform parent)
        {
            var row = CreateUiObject("Final Score Ranking Row Template", parent, typeof(Image), typeof(LayoutElement));
            row.GetComponent<LayoutElement>().preferredHeight = 58f;
            var badge = CreateUiObject("Player Color", row.transform, typeof(Image));
            SetAnchoredRect(badge.GetComponent<RectTransform>(), new Vector2(28f, 28f), new Vector2(16f, 0f), Anchor.MiddleLeft);
            var name = CreateCell(row.transform, "Player Name", 0.065f, 0.31f, TextAnchor.MiddleLeft, true);
            var live = CreateCell(row.transform, "Live Score", 0.31f, 0.46f, TextAnchor.MiddleCenter, false);
            var region = CreateCell(row.transform, "Region Score", 0.46f, 0.59f, TextAnchor.MiddleCenter, false);
            var resource = CreateCell(row.transform, "Resource Score", 0.59f, 0.72f, TextAnchor.MiddleCenter, false);
            var total = CreateCell(row.transform, "Total Score", 0.72f, 0.85f, TextAnchor.MiddleCenter, true);
            var details = CreateButton(row.transform, "Final Score Player Details Button", "查看 >", 17);
            SetStretch(details.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero, new Vector2(0.865f, 0.16f), new Vector2(0.985f, 0.84f));
            var view = row.AddComponent<RoundTrackerRankingRowView>();
            SetReferences(view,
                ("background", row.GetComponent<Image>()), ("playerBadge", badge.GetComponent<Image>()),
                ("playerNameText", name), ("liveScoreText", live), ("regionScoreText", region),
                ("resourceScoreText", resource), ("totalScoreText", total), ("detailsButton", details.GetComponent<Button>()));
            return view;
        }

        private static RoundTrackerDetailPlayerRowView BuildDetailPlayerRowTemplate(Transform parent)
        {
            var row = CreateUiObject("Final Score Detail Player Row Template", parent, typeof(Image), typeof(LayoutElement));
            row.GetComponent<LayoutElement>().preferredHeight = 62f;
            var badge = CreateUiObject("Player Color", row.transform, typeof(Image));
            SetAnchoredRect(badge.GetComponent<RectTransform>(), new Vector2(22f, 22f), new Vector2(10f, 0f), Anchor.MiddleLeft);
            var name = CreateText(row.transform, "Player Name", string.Empty, 18, FontStyle.Bold, TextAnchor.MiddleLeft);
            SetStretch(name.rectTransform, Vector2.zero, Vector2.zero, new Vector2(0.18f, 0f), new Vector2(0.72f, 1f));
            var switchButton = CreateButton(row.transform, "Player Switch", ">", 20);
            SetStretch(switchButton.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero, new Vector2(0.75f, 0.17f), new Vector2(0.96f, 0.83f));
            var view = row.AddComponent<RoundTrackerDetailPlayerRowView>();
            SetReferences(view,
                ("background", row.GetComponent<Image>()), ("playerBadge", badge.GetComponent<Image>()),
                ("playerNameText", name), ("switchButton", switchButton.GetComponent<Button>()));
            return view;
        }

        private static RoundTrackerScoreChartView BuildScoreChartTemplate(Transform parent)
        {
            var chart = CreateUiObject("Final Score Chart Template", parent, Array.Empty<Type>());
            Stretch(chart.GetComponent<RectTransform>());
            var badge = CreateUiObject("Chart Player Color", chart.transform, typeof(Image));
            SetAnchoredRect(badge.GetComponent<RectTransform>(), new Vector2(25f, 25f), new Vector2(8f, -18f), Anchor.TopLeft);
            var name = CreateText(chart.transform, "Chart Player Name", string.Empty, 22, FontStyle.Bold, TextAnchor.MiddleLeft);
            SetStretch(name.rectTransform, Vector2.zero, Vector2.zero, new Vector2(0.09f, 0.86f), Vector2.one);
            var barsObject = CreateUiObject("Score Bars", chart.transform, typeof(VerticalLayoutGroup));
            var bars = barsObject.GetComponent<RectTransform>();
            SetStretch(bars, Vector2.zero, Vector2.zero, new Vector2(0f, 0.2f), new Vector2(1f, 0.84f));
            ConfigureVerticalLayout(barsObject.GetComponent<VerticalLayoutGroup>(), 2f);
            var formula = CreateText(chart.transform, "Score Formula", string.Empty, 17, FontStyle.Bold, TextAnchor.MiddleCenter);
            SetStretch(formula.rectTransform, Vector2.zero, Vector2.zero, new Vector2(0.01f, 0.01f), new Vector2(0.99f, 0.18f));
            var view = chart.AddComponent<RoundTrackerScoreChartView>();
            SetReferences(view,
                ("playerBadge", badge.GetComponent<Image>()), ("playerNameText", name),
                ("formulaText", formula), ("barsContainer", bars));
            return view;
        }

        private static RoundTrackerScoreBarView BuildScoreBarTemplate(Transform parent)
        {
            var row = CreateUiObject("Final Score Bar Template", parent, typeof(Image), typeof(LayoutElement));
            row.GetComponent<LayoutElement>().preferredHeight = 38f;
            var label = CreateText(row.transform, "Score Label", string.Empty, 17, FontStyle.Normal, TextAnchor.MiddleLeft);
            SetStretch(label.rectTransform, Vector2.zero, Vector2.zero, new Vector2(0.015f, 0f), new Vector2(0.25f, 1f));
            var barBack = CreateUiObject("Bar Background", row.transform, typeof(Image));
            SetStretch(barBack.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero, new Vector2(0.255f, 0.24f), new Vector2(0.84f, 0.76f));
            barBack.GetComponent<Image>().color = new Color(0.03f, 0.02f, 0.012f, 0.86f);
            var fill = CreateUiObject("Bar Fill", barBack.transform, typeof(Image));
            Stretch(fill.GetComponent<RectTransform>());
            var value = CreateText(row.transform, "Score Value", string.Empty, 19, FontStyle.Bold, TextAnchor.MiddleCenter);
            SetStretch(value.rectTransform, Vector2.zero, Vector2.zero, new Vector2(0.85f, 0f), new Vector2(0.99f, 1f));
            var view = row.AddComponent<RoundTrackerScoreBarView>();
            SetReferences(view,
                ("background", row.GetComponent<Image>()), ("labelText", label),
                ("fillTransform", fill.GetComponent<RectTransform>()), ("fillImage", fill.GetComponent<Image>()),
                ("valueText", value));
            return view;
        }

        private static GameObject BuildInfrastructurePrefab()
        {
            var root = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            try
            {
                PrefabUtility.SaveAsPrefabAsset(root, InfrastructurePrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            return AssetDatabase.LoadAssetAtPath<GameObject>(InfrastructurePrefabPath);
        }

        private static void InstallInSampleScene(GameObject roundTrackerPrefab, GameObject infrastructurePrefab)
        {
            var scene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                if (roots[i].name == "RoundTracker")
                {
                    Object.DestroyImmediate(roots[i]);
                    break;
                }
            }

            var existingEventSystems = FindAllInScene<EventSystem>(scene);
            for (var i = 0; i < existingEventSystems.Length; i++)
            {
                Object.DestroyImmediate(existingEventSystems[i].gameObject);
            }

            var roundTracker = (GameObject)PrefabUtility.InstantiatePrefab(roundTrackerPrefab, scene);
            roundTracker.name = "RoundTracker";
            var infrastructure = (GameObject)PrefabUtility.InstantiatePrefab(infrastructurePrefab, scene);
            infrastructure.name = "EventSystem";

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
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

        private static void BuildScoreboardHeader(Transform parent)
        {
            var header = CreateUiObject("Final Score Header", parent, typeof(Image));
            SetStretch(header.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero, new Vector2(0f, 0.8f), Vector2.one);
            header.GetComponent<Image>().color = new Color(0.34f, 0.22f, 0.1f, 0.9f);
            CreateCell(header.transform, "玩家", 0f, 0.31f, TextAnchor.MiddleLeft, true);
            CreateCell(header.transform, "实时分", 0.31f, 0.46f, TextAnchor.MiddleCenter, true);
            CreateCell(header.transform, "区控", 0.46f, 0.59f, TextAnchor.MiddleCenter, true);
            CreateCell(header.transform, "资源", 0.59f, 0.72f, TextAnchor.MiddleCenter, true);
            CreateCell(header.transform, "总分", 0.72f, 0.85f, TextAnchor.MiddleCenter, true);
            CreateCell(header.transform, "详情", 0.85f, 1f, TextAnchor.MiddleCenter, true);
        }

        private static Text CreateCell(Transform parent, string name, float minX, float maxX, TextAnchor alignment, bool bold)
        {
            var text = CreateText(parent, "Cell " + name, name, 23, bold ? FontStyle.Bold : FontStyle.Normal, alignment);
            SetStretch(
                text.rectTransform,
                new Vector2(alignment == TextAnchor.MiddleLeft ? 10f : 0f, 0f),
                new Vector2(-4f, 0f),
                new Vector2(minX, 0f),
                new Vector2(maxX, 1f));
            return text;
        }

        private static void CreateRoundLabel(RectTransform parent, int index)
        {
            var label = CreateText(
                parent,
                "Round Label " + RoundLabels[index],
                RoundLabels[index],
                index == 0 || index == 9 ? 22 : 28,
                FontStyle.Bold,
                TextAnchor.MiddleCenter);
            SetAnchoredRect(
                label.rectTransform,
                index == 0 || index == 9 ? new Vector2(86f, 42f) : new Vector2(48f, 42f),
                new Vector2(GetSlotX(index), 0f),
                Anchor.Center);
            label.color = index >= 4 ? Color.white : Color.black;
        }

        private static void CreateTrackBand(RectTransform parent, string name, int fromIndex, int toIndex, Color color)
        {
            var band = CreateUiObject(name, parent, typeof(Image));
            var rect = band.GetComponent<RectTransform>();
            var slotWidth = 65f;
            var width = (toIndex - fromIndex + 1) * slotWidth;
            var centerIndex = (fromIndex + toIndex) * 0.5f;
            var x = (centerIndex - (RoundLabels.Length - 1) * 0.5f) * slotWidth;
            if (fromIndex == 0)
            {
                width += 22f;
                x -= 11f;
            }

            if (toIndex == 9)
            {
                width += 22f;
                x += 11f;
            }

            SetAnchoredRect(rect, new Vector2(width, 36f), new Vector2(x, 0f), Anchor.Center);
            band.GetComponent<Image>().color = color;
        }

        private static void CreateBorder(
            RectTransform parent,
            string name,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 size)
        {
            var border = CreateUiObject(name, parent, typeof(Image));
            var rect = border.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.sizeDelta = size;
            rect.anchoredPosition = Vector2.zero;
            border.GetComponent<Image>().color = UiTheme.GoldOutline;
        }

        private static GameObject CreateButton(Transform parent, string name, string label, int fontSize)
        {
            var button = CreateUiObject(name, parent, typeof(Image), typeof(Button), typeof(Outline));
            button.GetComponent<Image>().color = new Color(0.25f, 0.16f, 0.07f, 0.98f);
            button.GetComponent<Outline>().effectColor = new Color(0.7f, 0.54f, 0.29f, 0.86f);
            button.GetComponent<Outline>().effectDistance = new Vector2(2f, -2f);
            var text = CreateText(button.transform, "Text", label, fontSize, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform);
            text.raycastTarget = false;
            return button;
        }

        private static Text CreateText(
            Transform parent,
            string name,
            string content,
            int fontSize,
            FontStyle style,
            TextAnchor alignment)
        {
            var textObject = CreateUiObject(name, parent, typeof(Text));
            var text = textObject.GetComponent<Text>();
            text.text = content;
            text.font = UiEditorAssetReferences.CjkFont;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.alignment = alignment;
            text.color = new Color(0.94f, 0.87f, 0.72f, 1f);
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

        private static void ConfigureVerticalLayout(VerticalLayoutGroup layout, float spacing)
        {
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
        }

        private static void SetAnchoredRect(RectTransform rect, Vector2 size, Vector2 position, Anchor anchor)
        {
            switch (anchor)
            {
                case Anchor.TopCenter:
                    rect.anchorMin = new Vector2(0.5f, 1f);
                    rect.anchorMax = new Vector2(0.5f, 1f);
                    rect.pivot = new Vector2(0.5f, 1f);
                    break;
                case Anchor.TopLeft:
                    rect.anchorMin = Vector2.up;
                    rect.anchorMax = Vector2.up;
                    rect.pivot = Vector2.up;
                    break;
                case Anchor.MiddleLeft:
                    rect.anchorMin = new Vector2(0f, 0.5f);
                    rect.anchorMax = new Vector2(0f, 0.5f);
                    rect.pivot = new Vector2(0f, 0.5f);
                    break;
                case Anchor.MiddleRight:
                    rect.anchorMin = new Vector2(1f, 0.5f);
                    rect.anchorMax = new Vector2(1f, 0.5f);
                    rect.pivot = new Vector2(1f, 0.5f);
                    break;
                case Anchor.BottomCenter:
                    rect.anchorMin = new Vector2(0.5f, 0f);
                    rect.anchorMax = new Vector2(0.5f, 0f);
                    rect.pivot = new Vector2(0.5f, 0f);
                    break;
                case Anchor.BottomLeft:
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.zero;
                    rect.pivot = Vector2.zero;
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
            SetStretch(rect, Vector2.zero, Vector2.zero);
        }

        private static void SetStretch(RectTransform rect, Vector2 offsetMin, Vector2 offsetMax)
        {
            SetStretch(rect, offsetMin, offsetMax, Vector2.zero, Vector2.one);
        }

        private static void SetStretch(
            RectTransform rect,
            Vector2 offsetMin,
            Vector2 offsetMax,
            Vector2 anchorMin,
            Vector2 anchorMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static float GetSlotX(int index)
        {
            return (index - (RoundLabels.Length - 1) * 0.5f) * 65f;
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

        private sealed class GameOverShell
        {
            public GameObject Summary;
            public GameObject Details;
            public GameObject Message;
            public Text MessageText;
            public Text WinnerText;
            public Text TiebreakText;
            public Text DetailsButtonLabel;
            public Button DetailsButton;
            public Button BackButton;
            public Button ReturnButton;
            public RectTransform RankingRows;
            public RectTransform DetailPlayerRows;
            public RectTransform DetailCharts;
        }

        private sealed class TemplateRefs
        {
            public RoundTrackerPlayerMarkerView PlayerMarker;
            public RoundTrackerRankingRowView RankingRow;
            public RoundTrackerDetailPlayerRowView DetailPlayerRow;
            public RoundTrackerScoreChartView ScoreChart;
            public RoundTrackerScoreBarView ScoreBar;
        }

        private enum Anchor
        {
            Center,
            TopCenter,
            TopLeft,
            MiddleLeft,
            MiddleRight,
            BottomCenter,
            BottomLeft
        }
    }
}
