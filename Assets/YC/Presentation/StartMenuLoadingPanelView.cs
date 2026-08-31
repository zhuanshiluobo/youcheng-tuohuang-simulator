using UnityEngine;
using System.Collections;

namespace YC.Presentation
{
    public sealed class StartMenuLoadingPanelView : MonoBehaviour
    {
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField, Min(0.05f)] private float fadeToBlackDuration = 0.8f;
        [SerializeField, Min(0.05f)] private float fadeFromBlackDuration = 0.8f;

        private Coroutine fadeCoroutine;

        public float Opacity => canvasGroup == null ? 0f : canvasGroup.alpha;
        public bool IsOpaque => Opacity >= 0.999f;

        public void ShowIndeterminate(string title, string status)
        {
            Show();
        }

        public void ShowDeterminate(string title, string status)
        {
            Show();
        }

        public void Hide()
        {
            if (!gameObject.activeSelf)
            {
                SetImmediate(0f, false);
                return;
            }

            BeginFade(0f, true);
        }

        public IEnumerator FadeToBlack()
        {
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
            canvasGroup.blocksRaycasts = true;
            canvasGroup.interactable = true;

            if (fadeCoroutine != null)
            {
                StopCoroutine(fadeCoroutine);
                fadeCoroutine = null;
            }

            yield return FadeAlpha(1f, false);
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (canvasGroup == null)
            {
                reason = "全屏遮罩缺少 CanvasGroup 引用。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(CanvasGroup group, float fadeOutSeconds, float fadeInSeconds)
        {
            canvasGroup = group;
            fadeToBlackDuration = fadeOutSeconds;
            fadeFromBlackDuration = fadeInSeconds;
        }
#endif

        private void Show()
        {
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
            canvasGroup.blocksRaycasts = true;
            canvasGroup.interactable = true;
            BeginFade(1f, false);
        }

        private void BeginFade(float targetAlpha, bool disableWhenComplete)
        {
            if (fadeCoroutine != null)
            {
                StopCoroutine(fadeCoroutine);
            }

            fadeCoroutine = StartCoroutine(FadeAlpha(targetAlpha, disableWhenComplete));
        }

        private IEnumerator FadeAlpha(float targetAlpha, bool disableWhenComplete)
        {
            var startAlpha = canvasGroup.alpha;
            var duration = targetAlpha > startAlpha ? fadeToBlackDuration : fadeFromBlackDuration;
            var elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                var progress = Mathf.Clamp01(elapsed / duration);
                canvasGroup.alpha = Mathf.SmoothStep(startAlpha, targetAlpha, progress);
                yield return null;
            }

            canvasGroup.alpha = targetAlpha;
            canvasGroup.blocksRaycasts = targetAlpha > 0f;
            canvasGroup.interactable = targetAlpha > 0f;
            fadeCoroutine = null;
            if (disableWhenComplete)
            {
                gameObject.SetActive(false);
            }
        }

        private void SetImmediate(float alpha, bool blocksInput)
        {
            canvasGroup.alpha = alpha;
            canvasGroup.blocksRaycasts = blocksInput;
            canvasGroup.interactable = blocksInput;
        }
    }
}
