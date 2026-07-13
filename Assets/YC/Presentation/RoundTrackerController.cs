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

        public bool IsExpanded => true;

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

        private static void CreateFinalScoreboard(RectTransform parent, GameState state)
        {
            if (state == null || state.FinalScoring == null || !state.FinalScoring.IsResolved)
            {
                CreateScoreboardMessage(parent, "最终计分尚未生成。");
                return;
            }

            var winnerTransform = CreateScoreboardText(
                parent,
                "Final Score Winner",
                "胜者：" + FormatWinnerIds(state.FinalScoring.WinnerPlayerIds),
                30,
                FontStyle.Bold,
                TextAnchor.MiddleCenter);
            winnerTransform.anchorMin = new Vector2(0f, 0.765f);
            winnerTransform.anchorMax = new Vector2(1f, 0.85f);
            winnerTransform.offsetMin = new Vector2(40f, 0f);
            winnerTransform.offsetMax = new Vector2(-40f, 0f);

            var boardObject = new GameObject("Final Score Board", typeof(RectTransform), typeof(Image));
            boardObject.transform.SetParent(parent, false);
            var boardTransform = boardObject.GetComponent<RectTransform>();
            boardTransform.anchorMin = new Vector2(0f, 0.265f);
            boardTransform.anchorMax = new Vector2(1f, 0.765f);
            boardTransform.offsetMin = new Vector2(44f, 0f);
            boardTransform.offsetMax = new Vector2(-44f, 0f);
            boardObject.GetComponent<Image>().color = new Color(0.07f, 0.045f, 0.025f, 0.72f);

            CreateScoreboardHeader(boardTransform);
            var orderedScores = BuildOrderedFinalScores(state.FinalScoring.PlayerScores);
            var rowHeight = 0.8f / Mathf.Max(4, orderedScores.Count);
            for (var i = 0; i < orderedScores.Count; i++)
            {
                CreateScoreboardRow(boardTransform, state, orderedScores[i], i, rowHeight);
            }

            var tiebreak = string.IsNullOrEmpty(state.FinalScoring.TiebreakSummary)
                ? "同分时依次比较剩余金券、至纯源石。"
                : state.FinalScoring.TiebreakSummary;
            var tiebreakTransform = CreateScoreboardText(
                parent,
                "Final Score Tiebreak",
                tiebreak,
                19,
                FontStyle.Normal,
                TextAnchor.MiddleCenter);
            tiebreakTransform.anchorMin = new Vector2(0f, 0.145f);
            tiebreakTransform.anchorMax = new Vector2(1f, 0.26f);
            tiebreakTransform.offsetMin = new Vector2(46f, 0f);
            tiebreakTransform.offsetMax = new Vector2(-46f, 0f);
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

            CreateScoreboardCell(headerTransform, "玩家", 0f, 0.36f, TextAnchor.MiddleLeft, true);
            CreateScoreboardCell(headerTransform, "实时分", 0.36f, 0.53f, TextAnchor.MiddleCenter, true);
            CreateScoreboardCell(headerTransform, "区控", 0.53f, 0.68f, TextAnchor.MiddleCenter, true);
            CreateScoreboardCell(headerTransform, "资源", 0.68f, 0.83f, TextAnchor.MiddleCenter, true);
            CreateScoreboardCell(headerTransform, "总分", 0.83f, 1f, TextAnchor.MiddleCenter, true);
        }

        private static void CreateScoreboardRow(
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

            CreateScoreboardCell(rowTransform, playerName, 0.065f, 0.36f, TextAnchor.MiddleLeft, isWinner);
            CreateScoreboardCell(
                rowTransform,
                (score.BaseScore + score.FacilityScore + score.CityStyleScore).ToString(),
                0.36f,
                0.53f,
                TextAnchor.MiddleCenter,
                false);
            CreateScoreboardCell(rowTransform, score.RegionScore.ToString(), 0.53f, 0.68f, TextAnchor.MiddleCenter, false);
            CreateScoreboardCell(rowTransform, score.ResourceScore.ToString(), 0.68f, 0.83f, TextAnchor.MiddleCenter, false);
            CreateScoreboardCell(rowTransform, score.TotalScore.ToString(), 0.83f, 1f, TextAnchor.MiddleCenter, true);
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
