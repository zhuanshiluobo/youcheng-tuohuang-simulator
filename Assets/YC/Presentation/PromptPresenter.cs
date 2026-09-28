using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    internal sealed class PromptPresenter
    {
        private const float VisibleSeconds = 3f;
        private const float FadeSeconds = 0.16f;
        private const float SlideSpeed = 2200f;
        private const float PanelWidth = 520f;
        private const float MinPanelHeight = 82f;
        private const float HorizontalPadding = 44f;
        private const float VerticalPadding = 28f;
        private const float VisibleTopInset = 128f;
        private const float HiddenRightOffset = PanelWidth + 24f;

        private readonly bool useRuntimeLayout;
        private readonly float visibleX;
        private readonly Text promptText;
        private readonly RectTransform panelTransform;
        private readonly CanvasGroup promptCanvasGroup;
        private float hideAt;
        private float targetX;
        private float targetAlpha;
        private bool hideScheduled;

        private PromptPresenter(GameplayPromptView view)
        {
            useRuntimeLayout = view.UseRuntimeLayout;
            visibleX = view.PanelTransform.anchoredPosition.x;
            Canvas = view.Canvas;
            promptText = view.PromptText;
            panelTransform = view.PanelTransform;
            promptCanvasGroup = view.CanvasGroup;
            targetX = GetHiddenX();
            targetAlpha = 0f;
        }

        public Canvas Canvas { get; private set; }

        public static PromptPresenter Bind(GameplayPromptView view)
        {
            var reason = string.Empty;
            if (view == null || !view.TryValidateConfiguration(out reason))
            {
                Debug.LogError("[PromptPresenter] 无法绑定交互提示 View：" +
                               (view == null ? "引用为空。" : reason));
                return null;
            }

            return new PromptPresenter(view);
        }

        public void SetPrompt(string message, string pendingPrompt = null)
        {
            if (!string.IsNullOrEmpty(pendingPrompt) && (string.IsNullOrEmpty(message) || !message.Contains(pendingPrompt)))
                message = string.IsNullOrEmpty(message) ? pendingPrompt : message + "\n" + pendingPrompt;
            if (string.IsNullOrWhiteSpace(message))
            {
                Hide();
                return;
            }

            if (promptText != null)
            {
                promptText.text = message;
                if (useRuntimeLayout) ResizePanelToText();
            }

            if (promptCanvasGroup != null)
            {
                targetX = GetVisibleX();
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
            if (promptCanvasGroup == null || panelTransform == null)
            {
                return;
            }

            if (targetAlpha > 0f &&
                hideScheduled &&
                !keepVisible &&
                Time.unscaledTime >= hideAt)
            {
                Hide();
            }

            if (keepVisible)
            {
                targetX = GetVisibleX();
                targetAlpha = 1f;
                hideScheduled = false;
            }

            if (useRuntimeLayout)
            {
                var currentPosition = panelTransform.anchoredPosition;
                currentPosition.x = Mathf.MoveTowards(
                    currentPosition.x,
                    targetX,
                    SlideSpeed * Time.unscaledDeltaTime);
                panelTransform.anchoredPosition = currentPosition;
            }

            var fadeDuration = Mathf.Max(0.01f, FadeSeconds);
            promptCanvasGroup.alpha = Mathf.MoveTowards(
                promptCanvasGroup.alpha,
                targetAlpha,
                Time.unscaledDeltaTime / fadeDuration);

            if (targetAlpha > 0f &&
                !hideScheduled &&
                !keepVisible &&
                (!useRuntimeLayout || Mathf.Abs(panelTransform.anchoredPosition.x - targetX) <= 0.1f))
            {
                hideAt = Time.unscaledTime + VisibleSeconds;
                hideScheduled = true;
            }
        }

        private void Hide()
        {
            targetX = GetHiddenX();
            targetAlpha = 0f;
            hideScheduled = false;
        }

        private float GetVisibleX()
        {
            return visibleX;
        }

        private static float GetVisibleY()
        {
            return -VisibleTopInset;
        }

        private float GetHiddenX()
        {
            return visibleX + HiddenRightOffset;
        }
    }
}
