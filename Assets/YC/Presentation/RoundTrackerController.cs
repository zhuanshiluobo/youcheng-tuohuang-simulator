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
        private const float ExpandedPanelHeight = 136f;
        private const float CollapsedPanelHeight = 42f;
        private const float PanelWidth = 1120f;
        private const string ExpandedArrow = "▲";
        private const string CollapsedArrow = "▼";

        private static readonly string[] RoundLabels =
        {
            "START", "1", "2", "3", "4", "5", "6", "7", "8", "FINAL"
        };

        [SerializeField] private string startSceneName = "StartScene";
        [SerializeField] private bool startExpanded = true;
        [SerializeField] private float panelLerpSpeed = 10f;
        [SerializeField] private float panelSnapThreshold = 0.5f;

        private RectTransform panelTransform;
        private RectTransform contentArea;
        private RectTransform canvasTransform;
        private RectTransform markerTransform;
        private RectTransform trackSlotsTransform;
        private Image panelImage;
        private Outline panelOutline;
        private Button toggleButton;
        private Text toggleButtonText;
        private int currentIndex = FirstRoundIndex;
        private bool isExpanded = true;
        private float targetPanelHeight = ExpandedPanelHeight;
        private bool isAnimating;
        private bool pendingExpandedState = true;
        private bool gameOverDialogShown;

        public bool IsExpanded => isExpanded;

        private void Awake()
        {
            BuildRoundUi();
            MoveMarkerToCurrentIndex();
        }

        private void Update()
        {
            StepPanelAnimation(Time.deltaTime);
        }

        private void StepPanelAnimation(float deltaTime)
        {
            if (!isAnimating || panelTransform == null)
            {
                return;
            }

            var currentHeight = panelTransform.rect.height;
            var nextHeight = Mathf.Lerp(
                currentHeight,
                targetPanelHeight,
                deltaTime * panelLerpSpeed);

            var finishedThisFrame = false;
            if (Mathf.Abs(nextHeight - targetPanelHeight) <= panelSnapThreshold)
            {
                nextHeight = targetPanelHeight;
                isAnimating = false;
                finishedThisFrame = true;
            }

            panelTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, nextHeight);

            if (finishedThisFrame)
            {
                SetPanelChromeVisible(pendingExpandedState);
            }
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
            SetExpandedImmediate(startExpanded);
        }

        public void Toggle()
        {
            SetExpanded(!isExpanded);
        }

        private void SetExpanded(bool expand)
        {
            pendingExpandedState = expand;
            isExpanded = expand;
            targetPanelHeight = expand ? ExpandedPanelHeight : CollapsedPanelHeight;
            isAnimating = true;

            SetPanelChromeVisible(expand);

            if (contentArea != null)
            {
                contentArea.gameObject.SetActive(expand);
            }

            if (toggleButtonText != null)
            {
                toggleButtonText.text = expand ? ExpandedArrow : CollapsedArrow;
            }
        }

        private void SetExpandedImmediate(bool expand)
        {
            pendingExpandedState = expand;
            isExpanded = expand;
            targetPanelHeight = expand ? ExpandedPanelHeight : CollapsedPanelHeight;
            isAnimating = false;
            panelTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, targetPanelHeight);
            contentArea.gameObject.SetActive(expand);
            SetPanelChromeVisible(expand);
            toggleButtonText.text = expand ? ExpandedArrow : CollapsedArrow;
        }

        private void CreateRoundPanel(RectTransform parent)
        {
            var panelObject = new GameObject("Round Panel", typeof(RectTransform), typeof(Image), typeof(Outline), typeof(RectMask2D));
            panelObject.transform.SetParent(parent, false);

            panelTransform = panelObject.GetComponent<RectTransform>();
            panelTransform.anchorMin = new Vector2(0.5f, 1f);
            panelTransform.anchorMax = new Vector2(0.5f, 1f);
            panelTransform.pivot = new Vector2(0.5f, 1f);
            panelTransform.sizeDelta = new Vector2(PanelWidth, ExpandedPanelHeight);
            panelTransform.anchoredPosition = Vector2.zero;

            panelImage = panelObject.GetComponent<Image>();
            panelImage.color = UiTheme.PanelBackground;
            panelImage.raycastTarget = false;

            panelOutline = panelObject.GetComponent<Outline>();
            panelOutline.effectColor = UiTheme.GoldOutline;
            panelOutline.effectDistance = new Vector2(2f, -2f);

            BuildToggleButton(panelTransform);
            BuildContentArea(panelTransform);
        }

        private void SetPanelChromeVisible(bool visible)
        {
            if (panelImage != null)
            {
                panelImage.enabled = visible;
            }

            if (panelOutline != null)
            {
                panelOutline.enabled = visible;
            }
        }

        private void BuildToggleButton(RectTransform parent)
        {
            var buttonObject = new GameObject("Toggle Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var buttonTransform = buttonObject.GetComponent<RectTransform>();
            buttonTransform.anchorMin = new Vector2(0.5f, 0f);
            buttonTransform.anchorMax = new Vector2(0.5f, 0f);
            buttonTransform.pivot = new Vector2(0.5f, 0f);
            buttonTransform.sizeDelta = new Vector2(132f, 34f);
            buttonTransform.anchoredPosition = new Vector2(0f, 4f);

            var buttonImage = buttonObject.GetComponent<Image>();
            buttonImage.color = UiTheme.PanelBackgroundLighter;

            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);

            toggleButton = buttonObject.GetComponent<Button>();
            toggleButton.onClick.AddListener(Toggle);

            toggleButtonText = CreateButtonText(buttonTransform, ExpandedArrow, 24);
        }

        private void BuildContentArea(RectTransform parent)
        {
            contentArea = new GameObject("Content Area", typeof(RectTransform)).GetComponent<RectTransform>();
            contentArea.SetParent(parent, false);
            contentArea.anchorMin = Vector2.zero;
            contentArea.anchorMax = Vector2.one;
            contentArea.pivot = new Vector2(0.5f, 0.5f);
            contentArea.offsetMin = new Vector2(16f, 38f);
            contentArea.offsetMax = new Vector2(-16f, -8f);
        }

        private void CreateRoundTrack(RectTransform parent)
        {
            var trackObject = new GameObject("Round Track", typeof(RectTransform), typeof(Image), typeof(Outline));
            trackObject.transform.SetParent(parent, false);

            var trackTransform = trackObject.GetComponent<RectTransform>();
            trackTransform.anchorMin = new Vector2(0.5f, 0f);
            trackTransform.anchorMax = new Vector2(0.5f, 0f);
            trackTransform.pivot = new Vector2(0.5f, 0f);
            trackTransform.sizeDelta = new Vector2(720f, 84f);
            trackTransform.anchoredPosition = new Vector2(0f, 3f);

            var background = trackObject.GetComponent<Image>();
            background.color = UiTheme.TrackBackground;

            var outline = trackObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(2f, -2f);

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
            markerTransform.sizeDelta = new Vector2(32f, 32f);

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
            band.sizeDelta = new Vector2(width, 36f);
            band.anchoredPosition = new Vector2((centerIndex - (RoundLabels.Length - 1) * 0.5f) * slotWidth, 0f);

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
            dialogTransform.sizeDelta = new Vector2(760f, 460f);
            dialogTransform.anchoredPosition = Vector2.zero;

            var dialogImage = dialogObject.GetComponent<Image>();
            dialogImage.color = new Color(0.16f, 0.1f, 0.055f, 0.98f);

            var dialogOutline = dialogObject.GetComponent<Outline>();
            dialogOutline.effectColor = new Color(0.78f, 0.63f, 0.38f, 0.95f);
            dialogOutline.effectDistance = new Vector2(4f, -4f);

            var titleTransform = new GameObject("Game Over Text", typeof(RectTransform), typeof(Text), typeof(Outline)).GetComponent<RectTransform>();
            titleTransform.SetParent(dialogTransform, false);
            titleTransform.anchorMin = new Vector2(0f, 0.72f);
            titleTransform.anchorMax = new Vector2(1f, 1f);
            titleTransform.offsetMin = new Vector2(28f, 0f);
            titleTransform.offsetMax = new Vector2(-28f, -18f);

            var titleText = titleTransform.GetComponent<Text>();
            titleText.text = "游戏结束";
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.color = new Color(0.86f, 0.75f, 0.55f, 1f);
            titleText.fontSize = 58;
            titleText.fontStyle = FontStyle.Bold;
            titleText.font = FontUtility.GetCjkFont(titleText.fontSize);

            var titleOutline = titleTransform.GetComponent<Outline>();
            titleOutline.effectColor = new Color(0.06f, 0.04f, 0.025f, 0.98f);
            titleOutline.effectDistance = new Vector2(3f, -3f);

            var summaryTransform = new GameObject("Final Score Summary", typeof(RectTransform), typeof(Text)).GetComponent<RectTransform>();
            summaryTransform.SetParent(dialogTransform, false);
            summaryTransform.anchorMin = new Vector2(0f, 0.26f);
            summaryTransform.anchorMax = new Vector2(1f, 0.73f);
            summaryTransform.offsetMin = new Vector2(46f, 0f);
            summaryTransform.offsetMax = new Vector2(-46f, -8f);

            var summaryText = summaryTransform.GetComponent<Text>();
            summaryText.text = BuildFinalScoreSummary(state);
            summaryText.alignment = TextAnchor.UpperCenter;
            summaryText.color = new Color(0.93f, 0.86f, 0.7f, 1f);
            summaryText.fontSize = 24;
            summaryText.font = FontUtility.GetCjkFont(summaryText.fontSize);
            summaryText.horizontalOverflow = HorizontalWrapMode.Wrap;
            summaryText.verticalOverflow = VerticalWrapMode.Truncate;

            var returnButtonObject = new GameObject("Return Start Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            returnButtonObject.transform.SetParent(dialogTransform, false);

            var returnButtonTransform = returnButtonObject.GetComponent<RectTransform>();
            returnButtonTransform.anchorMin = new Vector2(0.5f, 0f);
            returnButtonTransform.anchorMax = new Vector2(0.5f, 0f);
            returnButtonTransform.pivot = new Vector2(0.5f, 0f);
            returnButtonTransform.sizeDelta = new Vector2(430f, 86f);
            returnButtonTransform.anchoredPosition = new Vector2(0f, 38f);

            ApplyButtonStyle(returnButtonObject);
            returnButtonObject.GetComponent<Button>().onClick.AddListener(ReturnToStartScene);
            CreateButtonText(returnButtonTransform, "点击返回开始页面", 34);
        }

        private static string BuildFinalScoreSummary(GameState state)
        {
            if (state == null || state.FinalScoring == null || !state.FinalScoring.IsResolved)
            {
                return "最终计分尚未生成。";
            }

            var summary = "胜者：" + FormatWinnerIds(state.FinalScoring.WinnerPlayerIds) + "\n";
            if (!string.IsNullOrEmpty(state.FinalScoring.TiebreakSummary))
            {
                summary += state.FinalScoring.TiebreakSummary + "\n";
            }

            for (var i = 0; i < state.FinalScoring.PlayerScores.Count; i++)
            {
                var score = state.FinalScoring.PlayerScores[i];
                summary += "P" + score.PlayerId + " 总分 " + score.TotalScore +
                           "（基础 " + score.BaseScore +
                           " / 区控 " + score.RegionScore +
                           " / 资源 " + score.ResourceScore +
                           " / 设施 " + score.FacilityScore +
                           " / 样式 " + score.CityStyleScore + "）";
                if (i < state.FinalScoring.PlayerScores.Count - 1)
                {
                    summary += "\n";
                }
            }

            return summary;
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

            markerTransform.anchoredPosition = new Vector2(GetSlotX(currentIndex), 33f);
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
