using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    internal sealed class PromptPresenter
    {
        private const float VisibleSeconds = 3f;
        private const float FadeSeconds = 0.25f;
        private const float PanelWidth = 820f;
        private const float MinPanelHeight = 68f;
        private const float HorizontalPadding = 44f;
        private const float VerticalPadding = 28f;

        private readonly Text promptText;
        private readonly RectTransform panelTransform;
        private readonly CanvasGroup promptCanvasGroup;
        private float hideAt;
        private float targetAlpha;
        private bool hideScheduled;

        private PromptPresenter(
            Canvas canvas,
            Text promptText,
            RectTransform panelTransform,
            CanvasGroup promptCanvasGroup)
        {
            Canvas = canvas;
            this.promptText = promptText;
            this.panelTransform = panelTransform;
            this.promptCanvasGroup = promptCanvasGroup;
        }

        public Canvas Canvas { get; private set; }

        public static PromptPresenter Build(Transform parent)
        {
            UguiUtility.EnsureEventSystem();

            var canvasObject = new GameObject("Mobile City UI Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(parent, false);

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 15;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var panelObject = new GameObject("Prompt Panel", typeof(RectTransform), typeof(Image), typeof(Outline), typeof(CanvasGroup));
            panelObject.transform.SetParent(canvasObject.transform, false);

            var panelTransform = panelObject.GetComponent<RectTransform>();
            panelTransform.anchorMin = new Vector2(0.5f, 1f);
            panelTransform.anchorMax = new Vector2(0.5f, 1f);
            panelTransform.pivot = new Vector2(0.5f, 1f);
            panelTransform.sizeDelta = new Vector2(PanelWidth, MinPanelHeight);
            panelTransform.anchoredPosition = new Vector2(0f, -32f);

            panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
            var outline = panelObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(3f, -3f);

            var promptCanvasGroup = panelObject.GetComponent<CanvasGroup>();
            promptCanvasGroup.alpha = 0f;
            promptCanvasGroup.interactable = false;
            promptCanvasGroup.blocksRaycasts = false;

            var textObject = new GameObject("Prompt Text", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(panelTransform, false);

            var textTransform = textObject.GetComponent<RectTransform>();
            textTransform.anchorMin = Vector2.zero;
            textTransform.anchorMax = Vector2.one;
            textTransform.offsetMin = new Vector2(22f, 0f);
            textTransform.offsetMax = new Vector2(-22f, 0f);

            var promptText = textObject.GetComponent<Text>();
            promptText.alignment = TextAnchor.MiddleCenter;
            promptText.color = UiTheme.GoldText;
            promptText.fontSize = 30;
            promptText.fontStyle = FontStyle.Bold;
            promptText.font = FontUtility.GetCjkFont(promptText.fontSize);
            promptText.horizontalOverflow = HorizontalWrapMode.Wrap;
            promptText.verticalOverflow = VerticalWrapMode.Overflow;

            return new PromptPresenter(canvas, promptText, panelTransform, promptCanvasGroup);
        }

        public void SetPrompt(string message)
        {
            if (promptText != null)
            {
                promptText.text = message ?? string.Empty;
                ResizePanelToText();
            }

            if (promptCanvasGroup != null)
            {
                targetAlpha = 1f;
                hideAt = 0f;
                hideScheduled = false;
            }
        }

        private void ResizePanelToText()
        {
            if (promptText == null || panelTransform == null)
            {
                return;
            }

            var textWidth = Mathf.Max(1f, PanelWidth - HorizontalPadding);
            var settings = promptText.GetGenerationSettings(new Vector2(textWidth, 0f));
            var preferredHeight = promptText.cachedTextGeneratorForLayout.GetPreferredHeight(
                promptText.text,
                settings) / promptText.pixelsPerUnit;
            var height = Mathf.Max(MinPanelHeight, Mathf.Ceil(preferredHeight + VerticalPadding));
            panelTransform.sizeDelta = new Vector2(PanelWidth, height);
        }

        public void Update(bool keepVisible)
        {
            if (promptCanvasGroup == null)
            {
                return;
            }

            if (targetAlpha > 0f && hideScheduled && !keepVisible && Time.unscaledTime >= hideAt)
            {
                targetAlpha = 0f;
                hideScheduled = false;
            }

            if (keepVisible)
            {
                hideScheduled = false;
            }

            var fadeDuration = Mathf.Max(0.01f, FadeSeconds);
            promptCanvasGroup.alpha = Mathf.MoveTowards(
                promptCanvasGroup.alpha,
                targetAlpha,
                Time.unscaledDeltaTime / fadeDuration);

            if (targetAlpha > 0f && !hideScheduled && !keepVisible && promptCanvasGroup.alpha >= 0.999f)
            {
                promptCanvasGroup.alpha = 1f;
                hideAt = Time.unscaledTime + VisibleSeconds;
                hideScheduled = true;
            }
        }
    }
}
