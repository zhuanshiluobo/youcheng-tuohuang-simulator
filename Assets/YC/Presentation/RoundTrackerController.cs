using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation
{
    public sealed class RoundTrackerController : MonoBehaviour
    {
        private const int FirstRoundIndex = RoundTrackRule.FirstRoundIndex;
        private const int FinalIndex = RoundTrackRule.FinalIndex;
        private const float PanelHeight = 78f;
        private const float PanelWidth = 720f;
        private const float BorderHeightRatio = 0.2f;
        private const float BorderThickness = PanelHeight * BorderHeightRatio;

        private static readonly string[] RoundLabels =
        {
            "START", "1", "2", "3", "4", "5", "6", "7", "8", "FINAL"
        };

        [SerializeField] private string startSceneName = "StartScene";

        private RectTransform panelTransform;
        private RectTransform contentArea;
        private RectTransform canvasTransform;
        private RectTransform markerTransform;
        private RectTransform trackSlotsTransform;
        private int currentIndex = FirstRoundIndex;
        private bool gameOverDialogShown;
        private GameObject finalScoreSummaryView;
        private GameObject finalScoreDetailsView;
        private Text finalScoreDetailsButtonLabel;
        private readonly Dictionary<int, GameObject> finalScoreDetailCharts =
            new Dictionary<int, GameObject>();
        private readonly Dictionary<int, Image> finalScoreDetailSelectorBackgrounds =
            new Dictionary<int, Image>();

        public bool IsExpanded => true;
        public bool IsFinalScoreDetailsOpen { get; private set; }
        public int SelectedFinalScorePlayerId { get; private set; } = -1;

        private void Awake()
        {
            BuildRoundUi();
            MoveMarkerToCurrentIndex();
        }

        private void Update()
        {
        }

        private void StepPanelAnimation(float deltaTime)
        {
        }

        public void RefreshFromState(GameState state)
        {
            if (state == null)
            {
                return;
            }

            currentIndex = RoundTrackRule.GetRoundIndex(state);
            MoveMarkerToCurrentIndex();

            if (currentIndex >= FinalIndex)
            {
                ShowGameOverDialog(state);
            }
        }

        public void ReturnToStartScene()
        {
            GameLaunchContext.ShutdownOnlineSession();
            SceneManager.LoadScene(startSceneName);
        }

        private void BuildRoundUi()
        {
            EnsureEventSystem();

            var canvasObject = new GameObject("Round UI Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 20;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasTransform = canvasObject.GetComponent<RectTransform>();
            CreateRoundPanel(canvasTransform);
            CreateRoundTrack(contentArea);
        }

        public void Toggle()
        {
        }

        private void CreateRoundPanel(RectTransform parent)
        {
            var panelObject = new GameObject("Round Panel", typeof(RectTransform), typeof(Image));
            panelObject.transform.SetParent(parent, false);

            panelTransform = panelObject.GetComponent<RectTransform>();
            panelTransform.anchorMin = new Vector2(0.5f, 1f);
            panelTransform.anchorMax = new Vector2(0.5f, 1f);
            panelTransform.pivot = new Vector2(0.5f, 1f);
            panelTransform.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            panelTransform.anchoredPosition = Vector2.zero;

            panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
            CreateBorderFrame(panelTransform);
            BuildContentArea(panelTransform);
        }

        private void BuildContentArea(RectTransform parent)
        {
            contentArea = new GameObject("Content Area", typeof(RectTransform)).GetComponent<RectTransform>();
            contentArea.SetParent(parent, false);
            contentArea.anchorMin = Vector2.zero;
            contentArea.anchorMax = Vector2.one;
            contentArea.pivot = new Vector2(0.5f, 0.5f);
            contentArea.offsetMin = new Vector2(BorderThickness, BorderThickness);
            contentArea.offsetMax = new Vector2(-BorderThickness, -BorderThickness);
        }

        private static void CreateBorderFrame(RectTransform parent)
        {
            CreateBorder(parent, "Round Border Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, BorderThickness), Vector2.zero);
            CreateBorder(parent, "Round Border Bottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, BorderThickness), Vector2.zero);
            CreateBorder(parent, "Round Border Left", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(BorderThickness, 0f), Vector2.zero);
            CreateBorder(parent, "Round Border Right", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(BorderThickness, 0f), Vector2.zero);
        }

        private static void CreateBorder(
            RectTransform parent,
            string name,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 sizeDelta,
            Vector2 anchoredPosition)
        {
            var borderObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            borderObject.transform.SetParent(parent, false);

            var rect = borderObject.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.sizeDelta = sizeDelta;
            rect.anchoredPosition = anchoredPosition;

            borderObject.GetComponent<Image>().color = UiTheme.GoldOutline;
        }

        private void CreateRoundTrack(RectTransform parent)
        {
            var trackObject = new GameObject("Round Track", typeof(RectTransform));
            trackObject.transform.SetParent(parent, false);

            var trackTransform = trackObject.GetComponent<RectTransform>();
            trackTransform.anchorMin = new Vector2(0.5f, 0.5f);
            trackTransform.anchorMax = new Vector2(0.5f, 0.5f);
            trackTransform.pivot = new Vector2(0.5f, 0.5f);
            trackTransform.sizeDelta = new Vector2(650f, 42f);
            trackTransform.anchoredPosition = Vector2.zero;

            CreateTrackBand(trackTransform, "Start Band", 0, 3, UiTheme.SafeBand);
            CreateTrackBand(trackTransform, "Danger Band", 4, FinalIndex, UiTheme.DangerBand);

            trackSlotsTransform = new GameObject("Round Slots", typeof(RectTransform)).GetComponent<RectTransform>();
            trackSlotsTransform.SetParent(trackTransform, false);
            trackSlotsTransform.anchorMin = new Vector2(0.5f, 0.5f);
            trackSlotsTransform.anchorMax = new Vector2(0.5f, 0.5f);
            trackSlotsTransform.pivot = new Vector2(0.5f, 0.5f);
            trackSlotsTransform.sizeDelta = new Vector2(650f, 54f);
            trackSlotsTransform.anchoredPosition = new Vector2(0f, 4f);

            for (var i = 0; i < RoundLabels.Length; i++)
            {
                CreateRoundLabel(trackSlotsTransform, i);
            }

            markerTransform = new GameObject("Round Marker", typeof(RectTransform), typeof(Image), typeof(Outline)).GetComponent<RectTransform>();
            markerTransform.SetParent(trackSlotsTransform, false);
            markerTransform.anchorMin = new Vector2(0.5f, 0.5f);
            markerTransform.anchorMax = new Vector2(0.5f, 0.5f);
            markerTransform.pivot = new Vector2(0.5f, 0.5f);
            markerTransform.sizeDelta = new Vector2(34f, 34f);

            var markerImage = markerTransform.GetComponent<Image>();
            markerImage.sprite = CreateCircleSprite();
            markerImage.color = Color.white;

            var markerOutline = markerTransform.GetComponent<Outline>();
            markerOutline.effectColor = new Color(0.06f, 0.04f, 0.025f, 0.9f);
            markerOutline.effectDistance = new Vector2(2f, -2f);
        }

        private static void CreateTrackBand(RectTransform parent, string name, int fromIndex, int toIndex, Color color)
        {
            var bandObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            bandObject.transform.SetParent(parent, false);

            var band = bandObject.GetComponent<RectTransform>();
            band.anchorMin = new Vector2(0.5f, 0.5f);
            band.anchorMax = new Vector2(0.5f, 0.5f);
            band.pivot = new Vector2(0.5f, 0.5f);

            var slotWidth = 65f;
            var width = (toIndex - fromIndex + 1) * slotWidth;
            var centerIndex = (fromIndex + toIndex) * 0.5f;
            var x = (centerIndex - (RoundLabels.Length - 1) * 0.5f) * slotWidth;
            if (fromIndex == 0)
            {
                width += 22f;
                x -= 11f;
            }

            if (toIndex == FinalIndex)
            {
                width += 22f;
                x += 11f;
            }

            band.sizeDelta = new Vector2(width, 36f);
            band.anchoredPosition = new Vector2(x, 0f);

            bandObject.GetComponent<Image>().color = color;
        }

        private static void CreateRoundLabel(RectTransform parent, int index)
        {
            var labelObject = new GameObject("Round Label " + RoundLabels[index], typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(parent, false);

            var labelTransform = labelObject.GetComponent<RectTransform>();
            labelTransform.anchorMin = new Vector2(0.5f, 0.5f);
            labelTransform.anchorMax = new Vector2(0.5f, 0.5f);
            labelTransform.pivot = new Vector2(0.5f, 0.5f);
            labelTransform.sizeDelta = index == 0 || index == FinalIndex
                ? new Vector2(86f, 42f)
                : new Vector2(48f, 42f);
            labelTransform.anchoredPosition = new Vector2(GetSlotX(index), 0f);

            var label = labelObject.GetComponent<Text>();
            label.text = RoundLabels[index];
            label.alignment = TextAnchor.MiddleCenter;
            label.color = index >= 4 ? Color.white : Color.black;
            label.fontSize = index == 0 || index == FinalIndex ? 22 : 28;
            label.fontStyle = FontStyle.Bold;
            label.font = FontUtility.GetCjkFont(label.fontSize);
        }

        private void ShowGameOverDialog(GameState state)
        {
            if (gameOverDialogShown)
            {
                return;
            }

            gameOverDialogShown = true;
            var overlayObject = new GameObject("Game Over Overlay", typeof(RectTransform), typeof(Image));
            overlayObject.transform.SetParent(canvasTransform, false);

            var overlayTransform = overlayObject.GetComponent<RectTransform>();
            overlayTransform.anchorMin = Vector2.zero;
            overlayTransform.anchorMax = Vector2.one;
            overlayTransform.offsetMin = Vector2.zero;
            overlayTransform.offsetMax = Vector2.zero;

            var overlayImage = overlayObject.GetComponent<Image>();
            overlayImage.color = new Color(0f, 0f, 0f, 0.62f);

            var dialogObject = new GameObject("Game Over Dialog", typeof(RectTransform), typeof(Image), typeof(Outline));
            dialogObject.transform.SetParent(overlayTransform, false);

            var dialogTransform = dialogObject.GetComponent<RectTransform>();
            dialogTransform.anchorMin = new Vector2(0.5f, 0.5f);
            dialogTransform.anchorMax = new Vector2(0.5f, 0.5f);
            dialogTransform.pivot = new Vector2(0.5f, 0.5f);
            dialogTransform.sizeDelta = new Vector2(900f, 650f);
            dialogTransform.anchoredPosition = Vector2.zero;

            var dialogImage = dialogObject.GetComponent<Image>();
            dialogImage.color = new Color(0.16f, 0.1f, 0.055f, 0.98f);

            var dialogOutline = dialogObject.GetComponent<Outline>();
            dialogOutline.effectColor = new Color(0.78f, 0.63f, 0.38f, 0.95f);
            dialogOutline.effectDistance = new Vector2(4f, -4f);

            var titleTransform = new GameObject("Game Over Text", typeof(RectTransform), typeof(Text), typeof(Outline)).GetComponent<RectTransform>();
            titleTransform.SetParent(dialogTransform, false);
            titleTransform.anchorMin = new Vector2(0f, 0.84f);
            titleTransform.anchorMax = new Vector2(1f, 1f);
            titleTransform.offsetMin = new Vector2(28f, 0f);
            titleTransform.offsetMax = new Vector2(-28f, -18f);

            var titleText = titleTransform.GetComponent<Text>();
            titleText.text = "游戏结束";
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.color = new Color(0.86f, 0.75f, 0.55f, 1f);
            titleText.fontSize = 52;
            titleText.fontStyle = FontStyle.Bold;
            titleText.font = FontUtility.GetCjkFont(titleText.fontSize);

            var titleOutline = titleTransform.GetComponent<Outline>();
            titleOutline.effectColor = new Color(0.06f, 0.04f, 0.025f, 0.98f);
            titleOutline.effectDistance = new Vector2(3f, -3f);

            CreateFinalScoreboard(dialogTransform, state);

            var returnButtonObject = new GameObject("Return Start Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            returnButtonObject.transform.SetParent(dialogTransform, false);

            var returnButtonTransform = returnButtonObject.GetComponent<RectTransform>();
            returnButtonTransform.anchorMin = new Vector2(0.5f, 0f);
            returnButtonTransform.anchorMax = new Vector2(0.5f, 0f);
            returnButtonTransform.pivot = new Vector2(0.5f, 0f);
            returnButtonTransform.sizeDelta = new Vector2(390f, 68f);
            returnButtonTransform.anchoredPosition = new Vector2(0f, 24f);

            ApplyButtonStyle(returnButtonObject);
            returnButtonObject.GetComponent<Button>().onClick.AddListener(ReturnToStartScene);
            CreateButtonText(returnButtonTransform, "返回开始页面", 30);
        }

        private void CreateFinalScoreboard(RectTransform parent, GameState state)
        {
            if (state == null || state.FinalScoring == null || !state.FinalScoring.IsResolved)
            {
                CreateScoreboardMessage(parent, "最终计分尚未生成。");
                return;
            }

            var orderedScores = BuildOrderedFinalScores(state.FinalScoring.PlayerScores);
            SelectedFinalScorePlayerId = ResolveDefaultDetailsPlayerId(state.FinalScoring, orderedScores);
            IsFinalScoreDetailsOpen = false;
            finalScoreDetailCharts.Clear();
            finalScoreDetailSelectorBackgrounds.Clear();

            var winnerBar = new GameObject("Final Score Winner Bar", typeof(RectTransform));
            winnerBar.transform.SetParent(parent, false);
            var winnerBarTransform = winnerBar.GetComponent<RectTransform>();
            winnerBarTransform.anchorMin = new Vector2(0f, 0.765f);
            winnerBarTransform.anchorMax = new Vector2(1f, 0.85f);
            winnerBarTransform.offsetMin = new Vector2(40f, 0f);
            winnerBarTransform.offsetMax = new Vector2(-40f, 0f);

            var winnerTransform = CreateScoreboardText(
                winnerBarTransform,
                "Final Score Winner",
                "胜者：" + FormatWinnerIds(state.FinalScoring.WinnerPlayerIds),
                30,
                FontStyle.Bold,
                TextAnchor.MiddleCenter);
            winnerTransform.anchorMin = Vector2.zero;
            winnerTransform.anchorMax = Vector2.one;
            winnerTransform.offsetMin = Vector2.zero;
            winnerTransform.offsetMax = new Vector2(-170f, 0f);

            CreateFinalScoreDetailsEntry(winnerBarTransform, SelectedFinalScorePlayerId >= 0);

            finalScoreSummaryView = new GameObject("Final Score Summary View", typeof(RectTransform));
            finalScoreSummaryView.transform.SetParent(parent, false);
            var summaryTransform = finalScoreSummaryView.GetComponent<RectTransform>();
            summaryTransform.anchorMin = Vector2.zero;
            summaryTransform.anchorMax = Vector2.one;
            summaryTransform.offsetMin = Vector2.zero;
            summaryTransform.offsetMax = Vector2.zero;

            var boardObject = new GameObject("Final Score Board", typeof(RectTransform), typeof(Image));
            boardObject.transform.SetParent(summaryTransform, false);
            var boardTransform = boardObject.GetComponent<RectTransform>();
            boardTransform.anchorMin = new Vector2(0f, 0.265f);
            boardTransform.anchorMax = new Vector2(1f, 0.765f);
            boardTransform.offsetMin = new Vector2(44f, 0f);
            boardTransform.offsetMax = new Vector2(-44f, 0f);
            boardObject.GetComponent<Image>().color = new Color(0.07f, 0.045f, 0.025f, 0.72f);

            CreateScoreboardHeader(boardTransform);
            var rowHeight = 0.8f / Mathf.Max(4, orderedScores.Count);
            for (var i = 0; i < orderedScores.Count; i++)
            {
                CreateScoreboardRow(boardTransform, state, orderedScores[i], i, rowHeight);
            }

            var tiebreak = string.IsNullOrEmpty(state.FinalScoring.TiebreakSummary)
                ? "同分时依次比较剩余金券、至纯源石。"
                : state.FinalScoring.TiebreakSummary;
            var tiebreakTransform = CreateScoreboardText(
                summaryTransform,
                "Final Score Tiebreak",
                tiebreak,
                19,
                FontStyle.Normal,
                TextAnchor.MiddleCenter);
            tiebreakTransform.anchorMin = new Vector2(0f, 0.145f);
            tiebreakTransform.anchorMax = new Vector2(1f, 0.26f);
            tiebreakTransform.offsetMin = new Vector2(46f, 0f);
            tiebreakTransform.offsetMax = new Vector2(-46f, 0f);

            CreateFinalScoreDetailsView(parent, state, orderedScores);
        }

        private static void CreateScoreboardHeader(RectTransform parent)
        {
            var header = new GameObject("Final Score Header", typeof(RectTransform), typeof(Image));
            header.transform.SetParent(parent, false);
            var headerTransform = header.GetComponent<RectTransform>();
            headerTransform.anchorMin = new Vector2(0f, 0.8f);
            headerTransform.anchorMax = Vector2.one;
            headerTransform.offsetMin = Vector2.zero;
            headerTransform.offsetMax = Vector2.zero;
            header.GetComponent<Image>().color = new Color(0.34f, 0.22f, 0.1f, 0.9f);

            CreateScoreboardCell(headerTransform, "玩家", 0f, 0.31f, TextAnchor.MiddleLeft, true);
            CreateScoreboardCell(headerTransform, "实时分", 0.31f, 0.46f, TextAnchor.MiddleCenter, true);
            CreateScoreboardCell(headerTransform, "区控", 0.46f, 0.59f, TextAnchor.MiddleCenter, true);
            CreateScoreboardCell(headerTransform, "资源", 0.59f, 0.72f, TextAnchor.MiddleCenter, true);
            CreateScoreboardCell(headerTransform, "总分", 0.72f, 0.85f, TextAnchor.MiddleCenter, true);
            CreateScoreboardCell(headerTransform, "详情", 0.85f, 1f, TextAnchor.MiddleCenter, true);
        }

        private void CreateScoreboardRow(
            RectTransform parent,
            GameState state,
            FinalPlayerScoreState score,
            int rowIndex,
            float rowHeight)
        {
            var isWinner = state.FinalScoring.WinnerPlayerIds.Contains(score.PlayerId);
            var row = new GameObject("Final Score Row P" + score.PlayerId, typeof(RectTransform), typeof(Image));
            row.transform.SetParent(parent, false);
            var rowTransform = row.GetComponent<RectTransform>();
            var rowTop = 0.8f - rowIndex * rowHeight;
            rowTransform.anchorMin = new Vector2(0f, rowTop - rowHeight);
            rowTransform.anchorMax = new Vector2(1f, rowTop);
            rowTransform.offsetMin = Vector2.zero;
            rowTransform.offsetMax = Vector2.zero;
            row.GetComponent<Image>().color = isWinner
                ? new Color(0.48f, 0.36f, 0.13f, 0.72f)
                : rowIndex % 2 == 0
                    ? new Color(0.15f, 0.09f, 0.045f, 0.7f)
                    : new Color(0.1f, 0.065f, 0.035f, 0.7f);

            var player = state.FindPlayer(score.PlayerId);
            var playerName = player == null || string.IsNullOrEmpty(player.Name)
                ? "P" + score.PlayerId
                : player.Name;
            if (isWinner)
            {
                playerName = "★ " + playerName;
            }

            var badgeObject = new GameObject("Player Color", typeof(RectTransform), typeof(Image));
            badgeObject.transform.SetParent(rowTransform, false);
            var badge = badgeObject.GetComponent<RectTransform>();
            badge.anchorMin = new Vector2(0.018f, 0.5f);
            badge.anchorMax = new Vector2(0.018f, 0.5f);
            badge.pivot = new Vector2(0f, 0.5f);
            badge.sizeDelta = new Vector2(28f, 28f);
            badgeObject.GetComponent<Image>().color = player == null
                ? Color.white
                : UiTheme.GetPlayerColor(player.Color, 1f);

            CreateScoreboardCell(rowTransform, playerName, 0.065f, 0.31f, TextAnchor.MiddleLeft, isWinner);
            CreateScoreboardCell(
                rowTransform,
                (score.BaseScore + score.FacilityScore + score.CityStyleScore).ToString(),
                0.31f,
                0.46f,
                TextAnchor.MiddleCenter,
                false);
            CreateScoreboardCell(rowTransform, score.RegionScore.ToString(), 0.46f, 0.59f, TextAnchor.MiddleCenter, false);
            CreateScoreboardCell(rowTransform, score.ResourceScore.ToString(), 0.59f, 0.72f, TextAnchor.MiddleCenter, false);
            CreateScoreboardCell(rowTransform, score.TotalScore.ToString(), 0.72f, 0.85f, TextAnchor.MiddleCenter, true);
            CreateScoreboardPlayerDetailsButton(rowTransform, score.PlayerId);
        }

        private void CreateFinalScoreDetailsEntry(RectTransform parent, bool interactable)
        {
            var buttonObject = new GameObject(
                "Final Score Details Button",
                typeof(RectTransform),
                typeof(Image),
                typeof(Button),
                typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var buttonTransform = buttonObject.GetComponent<RectTransform>();
            buttonTransform.anchorMin = new Vector2(1f, 0.5f);
            buttonTransform.anchorMax = new Vector2(1f, 0.5f);
            buttonTransform.pivot = new Vector2(1f, 0.5f);
            buttonTransform.sizeDelta = new Vector2(150f, 46f);
            buttonTransform.anchoredPosition = Vector2.zero;

            ApplyButtonStyle(buttonObject);
            var button = buttonObject.GetComponent<Button>();
            button.interactable = interactable;
            button.onClick.AddListener(ToggleFinalScoreDetails);
            finalScoreDetailsButtonLabel = CreateButtonText(buttonTransform, "详情  >", 22);
        }

        private void CreateScoreboardPlayerDetailsButton(RectTransform parent, int playerId)
        {
            var buttonObject = new GameObject(
                "Final Score Player Details Button P" + playerId,
                typeof(RectTransform),
                typeof(Image),
                typeof(Button),
                typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var buttonTransform = buttonObject.GetComponent<RectTransform>();
            buttonTransform.anchorMin = new Vector2(0.865f, 0.16f);
            buttonTransform.anchorMax = new Vector2(0.985f, 0.84f);
            buttonTransform.offsetMin = Vector2.zero;
            buttonTransform.offsetMax = Vector2.zero;

            ApplyCompactButtonStyle(buttonObject);
            buttonObject.GetComponent<Button>().onClick.AddListener(
                () => ShowFinalScoreDetailsForPlayer(playerId));
            CreateButtonText(buttonTransform, "查看 >", 17);
        }

        private void CreateFinalScoreDetailsView(
            RectTransform parent,
            GameState state,
            List<FinalPlayerScoreState> orderedScores)
        {
            finalScoreDetailsView = new GameObject(
                "Final Score Details View",
                typeof(RectTransform),
                typeof(Image),
                typeof(Outline));
            finalScoreDetailsView.transform.SetParent(parent, false);

            var detailsTransform = finalScoreDetailsView.GetComponent<RectTransform>();
            detailsTransform.anchorMin = new Vector2(0f, 0.18f);
            detailsTransform.anchorMax = new Vector2(1f, 0.765f);
            detailsTransform.offsetMin = new Vector2(44f, 0f);
            detailsTransform.offsetMax = new Vector2(-44f, 0f);
            finalScoreDetailsView.GetComponent<Image>().color = new Color(0.07f, 0.045f, 0.025f, 0.96f);
            var outline = finalScoreDetailsView.GetComponent<Outline>();
            outline.effectColor = new Color(0.56f, 0.42f, 0.2f, 0.9f);
            outline.effectDistance = new Vector2(2f, -2f);

            var backButtonObject = new GameObject(
                "Final Score Details Back Button",
                typeof(RectTransform),
                typeof(Image),
                typeof(Button),
                typeof(Outline));
            backButtonObject.transform.SetParent(detailsTransform, false);
            var backButtonTransform = backButtonObject.GetComponent<RectTransform>();
            backButtonTransform.anchorMin = new Vector2(0.018f, 0.855f);
            backButtonTransform.anchorMax = new Vector2(0.17f, 0.975f);
            backButtonTransform.offsetMin = Vector2.zero;
            backButtonTransform.offsetMax = Vector2.zero;
            ApplyCompactButtonStyle(backButtonObject);
            backButtonObject.GetComponent<Button>().onClick.AddListener(CloseFinalScoreDetails);
            CreateButtonText(backButtonTransform, "< 返回", 18);

            var detailsTitle = CreateScoreboardText(
                detailsTransform,
                "Final Score Details Title",
                "计分详情  →",
                25,
                FontStyle.Bold,
                TextAnchor.MiddleCenter);
            detailsTitle.anchorMin = new Vector2(0.19f, 0.855f);
            detailsTitle.anchorMax = new Vector2(0.98f, 0.985f);
            detailsTitle.offsetMin = Vector2.zero;
            detailsTitle.offsetMax = Vector2.zero;

            var selectorObject = new GameObject(
                "Final Score Details Player List",
                typeof(RectTransform),
                typeof(Image));
            selectorObject.transform.SetParent(detailsTransform, false);
            var selectorTransform = selectorObject.GetComponent<RectTransform>();
            selectorTransform.anchorMin = new Vector2(0.018f, 0.035f);
            selectorTransform.anchorMax = new Vector2(0.31f, 0.835f);
            selectorTransform.offsetMin = Vector2.zero;
            selectorTransform.offsetMax = Vector2.zero;
            selectorObject.GetComponent<Image>().color = new Color(0.13f, 0.08f, 0.04f, 0.92f);

            var selectorHeader = CreateScoreboardText(
                selectorTransform,
                "Final Score Details Player Header",
                "切换玩家",
                19,
                FontStyle.Bold,
                TextAnchor.MiddleCenter);
            selectorHeader.anchorMin = new Vector2(0f, 0.82f);
            selectorHeader.anchorMax = Vector2.one;
            selectorHeader.offsetMin = Vector2.zero;
            selectorHeader.offsetMax = Vector2.zero;

            var chartHost = new GameObject(
                "Final Score Detail Chart Host",
                typeof(RectTransform),
                typeof(Image));
            chartHost.transform.SetParent(detailsTransform, false);
            var chartHostTransform = chartHost.GetComponent<RectTransform>();
            chartHostTransform.anchorMin = new Vector2(0.33f, 0.035f);
            chartHostTransform.anchorMax = new Vector2(0.982f, 0.835f);
            chartHostTransform.offsetMin = Vector2.zero;
            chartHostTransform.offsetMax = Vector2.zero;
            chartHost.GetComponent<Image>().color = new Color(0.115f, 0.072f, 0.036f, 0.94f);

            if (orderedScores.Count == 0)
            {
                CreateScoreboardMessage(chartHostTransform, "没有可展示的玩家计分。");
            }
            else
            {
                var rowHeight = 0.8f / Mathf.Max(4, orderedScores.Count);
                for (var i = 0; i < orderedScores.Count; i++)
                {
                    CreateFinalScoreDetailsPlayerRow(
                        selectorTransform,
                        state,
                        orderedScores[i],
                        i,
                        rowHeight);
                    CreateFinalScoreDetailChart(chartHostTransform, state, orderedScores[i]);
                }
            }

            ApplyFinalScoreDetailsSelection();
            finalScoreDetailsView.SetActive(false);
        }

        private void CreateFinalScoreDetailsPlayerRow(
            RectTransform parent,
            GameState state,
            FinalPlayerScoreState score,
            int rowIndex,
            float rowHeight)
        {
            var rowObject = new GameObject(
                "Final Score Detail Player Row P" + score.PlayerId,
                typeof(RectTransform),
                typeof(Image));
            rowObject.transform.SetParent(parent, false);
            var rowTransform = rowObject.GetComponent<RectTransform>();
            var rowTop = 0.8f - rowIndex * rowHeight;
            rowTransform.anchorMin = new Vector2(0.02f, rowTop - rowHeight + 0.01f);
            rowTransform.anchorMax = new Vector2(0.98f, rowTop - 0.01f);
            rowTransform.offsetMin = Vector2.zero;
            rowTransform.offsetMax = Vector2.zero;

            var rowImage = rowObject.GetComponent<Image>();
            rowImage.color = GetDetailsSelectorColor(false);
            finalScoreDetailSelectorBackgrounds[score.PlayerId] = rowImage;

            var player = state.FindPlayer(score.PlayerId);
            var badgeObject = new GameObject(
                "Final Score Detail Player Color P" + score.PlayerId,
                typeof(RectTransform),
                typeof(Image));
            badgeObject.transform.SetParent(rowTransform, false);
            var badgeTransform = badgeObject.GetComponent<RectTransform>();
            badgeTransform.anchorMin = new Vector2(0.04f, 0.5f);
            badgeTransform.anchorMax = new Vector2(0.04f, 0.5f);
            badgeTransform.pivot = new Vector2(0f, 0.5f);
            badgeTransform.sizeDelta = new Vector2(22f, 22f);
            badgeObject.GetComponent<Image>().color = player == null
                ? Color.white
                : UiTheme.GetPlayerColor(player.Color, 1f);

            var playerName = player == null || string.IsNullOrEmpty(player.Name)
                ? "P" + score.PlayerId
                : player.Name;
            var nameTransform = CreateScoreboardText(
                rowTransform,
                "Final Score Detail Player Name P" + score.PlayerId,
                playerName,
                18,
                FontStyle.Bold,
                TextAnchor.MiddleLeft);
            nameTransform.anchorMin = new Vector2(0.18f, 0f);
            nameTransform.anchorMax = new Vector2(0.72f, 1f);
            nameTransform.offsetMin = Vector2.zero;
            nameTransform.offsetMax = Vector2.zero;

            var switchButtonObject = new GameObject(
                "Final Score Detail Player Switch P" + score.PlayerId,
                typeof(RectTransform),
                typeof(Image),
                typeof(Button),
                typeof(Outline));
            switchButtonObject.transform.SetParent(rowTransform, false);
            var switchButtonTransform = switchButtonObject.GetComponent<RectTransform>();
            switchButtonTransform.anchorMin = new Vector2(0.75f, 0.17f);
            switchButtonTransform.anchorMax = new Vector2(0.96f, 0.83f);
            switchButtonTransform.offsetMin = Vector2.zero;
            switchButtonTransform.offsetMax = Vector2.zero;
            ApplyCompactButtonStyle(switchButtonObject);
            switchButtonObject.GetComponent<Button>().onClick.AddListener(
                () => SelectFinalScoreDetailsPlayer(score.PlayerId));
            CreateButtonText(switchButtonTransform, ">", 20);
        }

        private void CreateFinalScoreDetailChart(
            RectTransform parent,
            GameState state,
            FinalPlayerScoreState score)
        {
            var chartObject = new GameObject(
                "Final Score Detail Chart P" + score.PlayerId,
                typeof(RectTransform));
            chartObject.transform.SetParent(parent, false);
            var chartTransform = chartObject.GetComponent<RectTransform>();
            chartTransform.anchorMin = Vector2.zero;
            chartTransform.anchorMax = Vector2.one;
            chartTransform.offsetMin = new Vector2(14f, 8f);
            chartTransform.offsetMax = new Vector2(-14f, -8f);
            finalScoreDetailCharts[score.PlayerId] = chartObject;

            var player = state.FindPlayer(score.PlayerId);
            var playerName = player == null || string.IsNullOrEmpty(player.Name)
                ? "P" + score.PlayerId
                : player.Name;

            var badgeObject = new GameObject(
                "Final Score Detail Chart Player Color P" + score.PlayerId,
                typeof(RectTransform),
                typeof(Image));
            badgeObject.transform.SetParent(chartTransform, false);
            var badgeTransform = badgeObject.GetComponent<RectTransform>();
            badgeTransform.anchorMin = new Vector2(0.02f, 0.925f);
            badgeTransform.anchorMax = new Vector2(0.02f, 0.925f);
            badgeTransform.pivot = new Vector2(0f, 0.5f);
            badgeTransform.sizeDelta = new Vector2(25f, 25f);
            badgeObject.GetComponent<Image>().color = player == null
                ? Color.white
                : UiTheme.GetPlayerColor(player.Color, 1f);

            var titleTransform = CreateScoreboardText(
                chartTransform,
                "Final Score Detail Chart Player Name P" + score.PlayerId,
                playerName + "（P" + score.PlayerId + "）",
                22,
                FontStyle.Bold,
                TextAnchor.MiddleLeft);
            titleTransform.anchorMin = new Vector2(0.09f, 0.86f);
            titleTransform.anchorMax = new Vector2(1f, 1f);
            titleTransform.offsetMin = Vector2.zero;
            titleTransform.offsetMax = Vector2.zero;

            var maxMagnitude = Mathf.Max(
                1,
                Mathf.Abs(score.BaseScore),
                Mathf.Abs(score.FacilityScore),
                Mathf.Abs(score.CityStyleScore),
                Mathf.Abs(score.RegionScore),
                Mathf.Abs(score.ResourceScore),
                Mathf.Abs(score.TotalScore));
            const float chartTop = 0.84f;
            const float chartBottom = 0.2f;
            var rowHeight = (chartTop - chartBottom) / 6f;
            CreateFinalScoreDetailBar(
                chartTransform,
                "Base",
                "基础分",
                score.BaseScore,
                0,
                rowHeight,
                chartTop,
                maxMagnitude,
                new Color(0.62f, 0.43f, 0.21f, 1f));
            CreateFinalScoreDetailBar(
                chartTransform,
                "Facility",
                "设施分",
                score.FacilityScore,
                1,
                rowHeight,
                chartTop,
                maxMagnitude,
                new Color(0.28f, 0.58f, 0.66f, 1f));
            CreateFinalScoreDetailBar(
                chartTransform,
                "CityStyle",
                "城市样式分",
                score.CityStyleScore,
                2,
                rowHeight,
                chartTop,
                maxMagnitude,
                new Color(0.75f, 0.47f, 0.2f, 1f));
            CreateFinalScoreDetailBar(
                chartTransform,
                "Region",
                "区控分",
                score.RegionScore,
                3,
                rowHeight,
                chartTop,
                maxMagnitude,
                new Color(0.32f, 0.62f, 0.38f, 1f));
            CreateFinalScoreDetailBar(
                chartTransform,
                "Resource",
                "资源分",
                score.ResourceScore,
                4,
                rowHeight,
                chartTop,
                maxMagnitude,
                new Color(0.55f, 0.43f, 0.72f, 1f));
            CreateFinalScoreDetailBar(
                chartTransform,
                "Total",
                "总分",
                score.TotalScore,
                5,
                rowHeight,
                chartTop,
                maxMagnitude,
                new Color(0.84f, 0.66f, 0.27f, 1f));

            var formula = "计分公式：基础分 " + score.BaseScore +
                          " + 设施分 " + score.FacilityScore +
                          " + 城市样式分 " + score.CityStyleScore +
                          " + 区控分 " + score.RegionScore +
                          " + 资源分 " + score.ResourceScore +
                          " = 总分 " + score.TotalScore;
            var formulaTransform = CreateScoreboardText(
                chartTransform,
                "Final Score Detail Formula P" + score.PlayerId,
                formula,
                17,
                FontStyle.Bold,
                TextAnchor.MiddleCenter);
            formulaTransform.anchorMin = new Vector2(0.01f, 0.01f);
            formulaTransform.anchorMax = new Vector2(0.99f, 0.18f);
            formulaTransform.offsetMin = Vector2.zero;
            formulaTransform.offsetMax = Vector2.zero;
        }

        private static void CreateFinalScoreDetailBar(
            RectTransform parent,
            string scoreKey,
            string label,
            int value,
            int rowIndex,
            float rowHeight,
            float chartTop,
            int maxMagnitude,
            Color fillColor)
        {
            var rowObject = new GameObject(
                "Final Score Detail " + scoreKey + " Row",
                typeof(RectTransform),
                typeof(Image));
            rowObject.transform.SetParent(parent, false);
            var rowTransform = rowObject.GetComponent<RectTransform>();
            var rowTop = chartTop - rowIndex * rowHeight;
            rowTransform.anchorMin = new Vector2(0f, rowTop - rowHeight + 0.006f);
            rowTransform.anchorMax = new Vector2(1f, rowTop - 0.006f);
            rowTransform.offsetMin = Vector2.zero;
            rowTransform.offsetMax = Vector2.zero;
            rowObject.GetComponent<Image>().color = scoreKey == "Total"
                ? new Color(0.3f, 0.22f, 0.09f, 0.58f)
                : new Color(0.04f, 0.025f, 0.015f, 0.34f);

            var labelTransform = CreateScoreboardText(
                rowTransform,
                "Final Score Detail " + scoreKey + " Label",
                label,
                17,
                scoreKey == "Total" ? FontStyle.Bold : FontStyle.Normal,
                TextAnchor.MiddleLeft);
            labelTransform.anchorMin = new Vector2(0.015f, 0f);
            labelTransform.anchorMax = new Vector2(0.25f, 1f);
            labelTransform.offsetMin = Vector2.zero;
            labelTransform.offsetMax = Vector2.zero;

            var barBackObject = new GameObject(
                "Final Score Detail " + scoreKey + " Bar Background",
                typeof(RectTransform),
                typeof(Image));
            barBackObject.transform.SetParent(rowTransform, false);
            var barBackTransform = barBackObject.GetComponent<RectTransform>();
            barBackTransform.anchorMin = new Vector2(0.255f, 0.24f);
            barBackTransform.anchorMax = new Vector2(0.84f, 0.76f);
            barBackTransform.offsetMin = Vector2.zero;
            barBackTransform.offsetMax = Vector2.zero;
            barBackObject.GetComponent<Image>().color = new Color(0.03f, 0.02f, 0.012f, 0.86f);

            var fillObject = new GameObject(
                "Final Score Detail " + scoreKey + " Bar",
                typeof(RectTransform),
                typeof(Image));
            fillObject.transform.SetParent(barBackTransform, false);
            var fillTransform = fillObject.GetComponent<RectTransform>();
            fillTransform.anchorMin = Vector2.zero;
            fillTransform.anchorMax = new Vector2(
                Mathf.Clamp01(Mathf.Abs(value) / (float)maxMagnitude),
                1f);
            fillTransform.offsetMin = Vector2.zero;
            fillTransform.offsetMax = Vector2.zero;
            fillObject.GetComponent<Image>().color = value < 0
                ? new Color(0.72f, 0.25f, 0.19f, 1f)
                : fillColor;

            var valueTransform = CreateScoreboardText(
                rowTransform,
                "Final Score Detail " + scoreKey + " Value",
                value.ToString(),
                19,
                FontStyle.Bold,
                TextAnchor.MiddleCenter);
            valueTransform.anchorMin = new Vector2(0.85f, 0f);
            valueTransform.anchorMax = new Vector2(0.99f, 1f);
            valueTransform.offsetMin = Vector2.zero;
            valueTransform.offsetMax = Vector2.zero;
        }

        public void ToggleFinalScoreDetails()
        {
            if (IsFinalScoreDetailsOpen)
            {
                CloseFinalScoreDetails();
                return;
            }

            OpenFinalScoreDetails();
        }

        public void OpenFinalScoreDetails()
        {
            if (finalScoreSummaryView == null ||
                finalScoreDetailsView == null ||
                SelectedFinalScorePlayerId < 0)
            {
                return;
            }

            finalScoreSummaryView.SetActive(false);
            finalScoreDetailsView.SetActive(true);
            IsFinalScoreDetailsOpen = true;
            UpdateFinalScoreDetailsEntryLabel();
        }

        public void CloseFinalScoreDetails()
        {
            if (finalScoreSummaryView == null || finalScoreDetailsView == null)
            {
                return;
            }

            finalScoreDetailsView.SetActive(false);
            finalScoreSummaryView.SetActive(true);
            IsFinalScoreDetailsOpen = false;
            UpdateFinalScoreDetailsEntryLabel();
        }

        public void ShowFinalScoreDetailsForPlayer(int playerId)
        {
            if (!SelectFinalScoreDetailsPlayer(playerId))
            {
                return;
            }

            OpenFinalScoreDetails();
        }

        public bool SelectFinalScoreDetailsPlayer(int playerId)
        {
            if (!finalScoreDetailCharts.ContainsKey(playerId))
            {
                return false;
            }

            SelectedFinalScorePlayerId = playerId;
            ApplyFinalScoreDetailsSelection();
            return true;
        }

        private void ApplyFinalScoreDetailsSelection()
        {
            foreach (var pair in finalScoreDetailCharts)
            {
                pair.Value.SetActive(pair.Key == SelectedFinalScorePlayerId);
            }

            foreach (var pair in finalScoreDetailSelectorBackgrounds)
            {
                pair.Value.color = GetDetailsSelectorColor(pair.Key == SelectedFinalScorePlayerId);
            }
        }

        private void UpdateFinalScoreDetailsEntryLabel()
        {
            if (finalScoreDetailsButtonLabel != null)
            {
                finalScoreDetailsButtonLabel.text = IsFinalScoreDetailsOpen
                    ? "<  收起"
                    : "详情  >";
            }
        }

        private static int ResolveDefaultDetailsPlayerId(
            FinalScoringState scoring,
            List<FinalPlayerScoreState> orderedScores)
        {
            if (orderedScores == null || orderedScores.Count == 0)
            {
                return -1;
            }

            if (scoring != null && scoring.WinnerPlayerIds != null)
            {
                for (var i = 0; i < orderedScores.Count; i++)
                {
                    if (scoring.WinnerPlayerIds.Contains(orderedScores[i].PlayerId))
                    {
                        return orderedScores[i].PlayerId;
                    }
                }
            }

            return orderedScores[0].PlayerId;
        }

        private static Color GetDetailsSelectorColor(bool selected)
        {
            return selected
                ? new Color(0.48f, 0.36f, 0.13f, 0.9f)
                : new Color(0.17f, 0.105f, 0.05f, 0.82f);
        }

        private static void ApplyCompactButtonStyle(GameObject buttonObject)
        {
            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.25f, 0.16f, 0.07f, 0.98f);

            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = new Color(0.7f, 0.54f, 0.29f, 0.86f);
            outline.effectDistance = new Vector2(2f, -2f);
        }

        private static void CreateScoreboardCell(
            RectTransform parent,
            string content,
            float minX,
            float maxX,
            TextAnchor alignment,
            bool bold)
        {
            var cell = CreateScoreboardText(
                parent,
                "Cell " + content,
                content,
                23,
                bold ? FontStyle.Bold : FontStyle.Normal,
                alignment);
            cell.anchorMin = new Vector2(minX, 0f);
            cell.anchorMax = new Vector2(maxX, 1f);
            cell.offsetMin = new Vector2(alignment == TextAnchor.MiddleLeft ? 10f : 0f, 0f);
            cell.offsetMax = new Vector2(-4f, 0f);
        }

        private static RectTransform CreateScoreboardText(
            RectTransform parent,
            string objectName,
            string content,
            int fontSize,
            FontStyle fontStyle,
            TextAnchor alignment)
        {
            var transform = new GameObject(objectName, typeof(RectTransform), typeof(Text)).GetComponent<RectTransform>();
            transform.SetParent(parent, false);
            var text = transform.GetComponent<Text>();
            text.text = content;
            text.alignment = alignment;
            text.color = new Color(0.94f, 0.87f, 0.72f, 1f);
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.font = FontUtility.GetCjkFont(fontSize);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return transform;
        }

        private static void CreateScoreboardMessage(RectTransform parent, string message)
        {
            var transform = CreateScoreboardText(
                parent,
                "Final Score Message",
                message,
                28,
                FontStyle.Normal,
                TextAnchor.MiddleCenter);
            transform.anchorMin = new Vector2(0f, 0.3f);
            transform.anchorMax = new Vector2(1f, 0.75f);
            transform.offsetMin = new Vector2(40f, 0f);
            transform.offsetMax = new Vector2(-40f, 0f);
        }

        private static List<FinalPlayerScoreState> BuildOrderedFinalScores(List<FinalPlayerScoreState> scores)
        {
            var ordered = scores == null
                ? new List<FinalPlayerScoreState>()
                : new List<FinalPlayerScoreState>(scores);
            ordered.Sort((left, right) =>
            {
                var comparison = right.TotalScore.CompareTo(left.TotalScore);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = right.GoldVoucherTiebreaker.CompareTo(left.GoldVoucherTiebreaker);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = right.PureOriginiumTiebreaker.CompareTo(left.PureOriginiumTiebreaker);
                return comparison != 0 ? comparison : left.PlayerId.CompareTo(right.PlayerId);
            });
            return ordered;
        }

        private static string FormatWinnerIds(System.Collections.Generic.List<int> winnerPlayerIds)
        {
            if (winnerPlayerIds == null || winnerPlayerIds.Count == 0)
            {
                return "无";
            }

            var result = string.Empty;
            for (var i = 0; i < winnerPlayerIds.Count; i++)
            {
                if (i > 0)
                {
                    result += "、";
                }

                result += "P" + winnerPlayerIds[i];
            }

            return result;
        }

        private static void ApplyButtonStyle(GameObject buttonObject)
        {
            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.16f, 0.1f, 0.055f, 0.96f);

            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = new Color(0.78f, 0.63f, 0.38f, 0.9f);
            outline.effectDistance = new Vector2(4f, -4f);
        }

        private static Text CreateButtonText(RectTransform parent, string content, int fontSize)
        {
            var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text), typeof(Outline));
            textObject.transform.SetParent(parent, false);

            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textObject.GetComponent<Text>();
            text.text = content;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(0.86f, 0.75f, 0.55f, 1f);
            text.fontSize = fontSize;
            text.fontStyle = FontStyle.Bold;
            text.font = FontUtility.GetCjkFont(text.fontSize);

            var outline = textObject.GetComponent<Outline>();
            outline.effectColor = new Color(0.06f, 0.04f, 0.025f, 0.98f);
            outline.effectDistance = new Vector2(3f, -3f);
            return text;
        }

        private void MoveMarkerToCurrentIndex()
        {
            if (markerTransform == null)
            {
                return;
            }

            markerTransform.anchoredPosition = new Vector2(GetSlotX(currentIndex), 0f);
        }

        private static float GetSlotX(int index)
        {
            return (index - (RoundLabels.Length - 1) * 0.5f) * 65f;
        }

        private static bool IsNetworkLaunch()
        {
            return GameLaunchContext.Instance != null && GameLaunchContext.Instance.Mode != LaunchMode.Local;
        }

        private static Sprite CreateCircleSprite()
        {
            const int size = 64;
            const float radius = 27f;
            const float thickness = 7f;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

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
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        }

        private static void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null)
            {
                return;
            }

            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }
    }
}
