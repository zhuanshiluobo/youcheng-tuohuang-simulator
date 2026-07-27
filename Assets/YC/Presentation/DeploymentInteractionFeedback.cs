using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    internal sealed class ActionButtonPressFeedback : MonoBehaviour,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerExitHandler
    {
        public const float PressDuration = 0.08f;
        public const float PressedScale = 0.92f;
        public const float ReleaseDuration = 0.12f;

        private Button button;
        private Outline outline;
        private Vector3 restingScale = Vector3.one;
        private Color restingOutlineColor;
        private Coroutine scaleAnimation;
        private bool configured;
        private bool pressed;

        public void Configure(Button configuredButton, Outline configuredOutline)
        {
            button = configuredButton;
            outline = configuredOutline;
            restingScale = transform.localScale;
            restingOutlineColor = outline == null ? Color.clear : outline.effectColor;
            configured = true;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            EnsureConfigured();
            if (!IsLeftPointer(eventData) || button == null || !button.IsInteractable())
            {
                return;
            }

            pressed = true;
            if (outline != null)
            {
                outline.effectColor = UiTheme.CyanAccent;
            }

            StopAnimation();
            var targetScale = restingScale * PressedScale;
            if (!UnityEngine.Application.isPlaying)
            {
                transform.localScale = targetScale;
                return;
            }

                scaleAnimation = StartCoroutine(ScaleTo(transform.localScale, targetScale, PressDuration));
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (IsLeftPointer(eventData))
            {
                Release();
            }
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (pressed)
            {
                Release();
            }
        }

        private void Awake()
        {
            EnsureConfigured();
        }

        private void OnDisable()
        {
            ResetImmediately();
        }

        private void Release()
        {
            if (!pressed)
            {
                return;
            }

            pressed = false;
            StopAnimation();
            if (!UnityEngine.Application.isPlaying)
            {
                ResetImmediately();
                return;
            }

                scaleAnimation = StartCoroutine(BounceBack());
        }

        private IEnumerator BounceBack()
        {
            var raisedScale = restingScale * 1.04f;
            yield return ScaleTo(transform.localScale, raisedScale, ReleaseDuration * 0.42f);
            yield return ScaleTo(raisedScale, restingScale, ReleaseDuration * 0.58f);
            RestoreOutline();
            scaleAnimation = null;
        }

        private IEnumerator ScaleTo(Vector3 from, Vector3 to, float duration)
        {
            var elapsed = 0f;
            while (elapsed < duration)
            {
                var progress = duration <= 0f ? 1f : Mathf.Clamp01(elapsed / duration);
                transform.localScale = Vector3.LerpUnclamped(from, to, SmoothOut(progress));
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
            transform.localScale = to;
        }

        private void EnsureConfigured()
        {
            if (configured)
            {
                return;
            }

            button = GetComponent<Button>();
            outline = GetComponent<Outline>();
            restingScale = transform.localScale;
            restingOutlineColor = outline == null ? Color.clear : outline.effectColor;
            configured = true;
        }

        private void ResetImmediately()
        {
            pressed = false;
            StopAnimation();
            transform.localScale = restingScale;
            RestoreOutline();
        }

        private void RestoreOutline()
        {
            if (outline != null)
            {
                outline.effectColor = restingOutlineColor;
            }
        }

        private void StopAnimation()
        {
            if (scaleAnimation == null)
            {
                return;
            }

            StopCoroutine(scaleAnimation);
            scaleAnimation = null;
        }

        private static bool IsLeftPointer(PointerEventData eventData)
        {
            return eventData != null && eventData.button == PointerEventData.InputButton.Left;
        }

        private static float SmoothOut(float value)
        {
            var inverse = 1f - value;
            return 1f - inverse * inverse * inverse;
        }
    }

    internal sealed class MapHighlightPulse : MonoBehaviour
    {
        public const float PulseDuration = 0.6f;
        public const float MinimumAlpha = 0.4f;
        public const float MaximumAlpha = 1f;

        private SpriteRenderer border;
        private Coroutine pulse;
        private bool highlighted;

        public bool IsHighlighted => highlighted;

        public void Configure(SpriteRenderer borderRenderer)
        {
            highlighted = false;
            StopPulse();
            border = borderRenderer;
            ApplyAlpha(0f);
            if (border != null)
            {
                border.enabled = false;
            }
        }

        public void SetHighlighted(bool visible)
        {
            if (highlighted == visible)
            {
                return;
            }

            highlighted = visible;
            StopPulse();
            if (!visible)
            {
                ApplyAlpha(0f);
                if (border != null)
                {
                    border.enabled = false;
                }
                return;
            }

            if (border != null)
            {
                border.enabled = true;
            }
            ApplyAlpha(EvaluateAlpha(0f));
            if (UnityEngine.Application.isPlaying && isActiveAndEnabled)
            {
                pulse = StartCoroutine(Pulse());
            }
        }

        public static float EvaluateAlpha(float elapsedSeconds)
        {
            var phase = elapsedSeconds / PulseDuration * Mathf.PI * 2f;
            var wave = (Mathf.Sin(phase) + 1f) * 0.5f;
            return Mathf.Lerp(MinimumAlpha, MaximumAlpha, wave);
        }

        private IEnumerator Pulse()
        {
            var elapsed = 0f;
            while (highlighted)
            {
                elapsed += Time.unscaledDeltaTime;
                ApplyAlpha(EvaluateAlpha(elapsed));
                yield return null;
            }
            pulse = null;
        }

        private void OnEnable()
        {
            if (!highlighted || border == null)
            {
                return;
            }

            border.enabled = true;
            if (UnityEngine.Application.isPlaying && pulse == null)
            {
                pulse = StartCoroutine(Pulse());
            }
        }

        private void OnDisable()
        {
            StopPulse();
        }

        private void ApplyAlpha(float alpha)
        {
            if (border == null)
            {
                return;
            }

            border.color = new Color(
                UiTheme.CyanAccent.r,
                UiTheme.CyanAccent.g,
                UiTheme.CyanAccent.b,
                alpha);
        }

        private void StopPulse()
        {
            if (pulse == null)
            {
                return;
            }

            StopCoroutine(pulse);
            pulse = null;
        }
    }

    internal sealed class MapPlacementFeedback : MonoBehaviour
    {
        public const float Duration = 0.15f;
        public const float FlashStartScale = 1.2f;
        public const float FlashEndScale = 1f;
        public const float RingStartScale = 1f;
        public const float RingEndScale = 1.8f;

        private static Sprite feedbackRingSprite;

        private SpriteRenderer flashRenderer;
        private SpriteRenderer expandingRingRenderer;
        private Coroutine placementAnimation;

        public void Configure(int sortingOrder)
        {
            EnsureRenderers();
            flashRenderer.sortingOrder = sortingOrder;
            expandingRingRenderer.sortingOrder = sortingOrder + 1;
            Hide();
        }

        public void Play()
        {
            EnsureRenderers();
            StopAnimation();
            ApplyFrame(0f);
            flashRenderer.enabled = true;
            expandingRingRenderer.enabled = true;
            if (UnityEngine.Application.isPlaying && isActiveAndEnabled)
            {
                placementAnimation = StartCoroutine(Animate());
            }
        }

        public void ResetFeedback()
        {
            StopAnimation();
            Hide();
        }

        public static float EvaluateFlashScale(float progress)
        {
            return Mathf.Lerp(FlashStartScale, FlashEndScale, Mathf.Clamp01(progress));
        }

        public static float EvaluateRingScale(float progress)
        {
            return Mathf.Lerp(RingStartScale, RingEndScale, Mathf.Clamp01(progress));
        }

        public static float EvaluateRingAlpha(float progress)
        {
            return 1f - Mathf.Clamp01(progress);
        }

        private IEnumerator Animate()
        {
            var elapsed = 0f;
            while (elapsed < Duration)
            {
                ApplyFrame(elapsed / Duration);
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            ApplyFrame(1f);
            placementAnimation = null;
            Hide();
        }

        private void OnDisable()
        {
            ResetFeedback();
        }

        private void EnsureRenderers()
        {
            if (feedbackRingSprite == null)
            {
                feedbackRingSprite = UguiUtility.CreateCircleSprite(64, 27f, 3f);
            }

            if (flashRenderer == null)
            {
                flashRenderer = CreateRingRenderer("Placement Flash");
            }
            if (expandingRingRenderer == null)
            {
                expandingRingRenderer = CreateRingRenderer("Placement Expanding Ring");
            }
        }

        private SpriteRenderer CreateRingRenderer(string objectName)
        {
            var ringObject = new GameObject(objectName, typeof(SpriteRenderer));
            ringObject.transform.SetParent(transform, false);
            ringObject.transform.localPosition = Vector3.zero;
            var renderer = ringObject.GetComponent<SpriteRenderer>();
            renderer.sprite = feedbackRingSprite;
            renderer.color = UiTheme.CyanAccent;
            renderer.enabled = false;
            return renderer;
        }

        private void ApplyFrame(float progress)
        {
            var clamped = Mathf.Clamp01(progress);
            flashRenderer.transform.localScale = Vector3.one * EvaluateFlashScale(clamped);
            expandingRingRenderer.transform.localScale = Vector3.one * EvaluateRingScale(clamped);
            flashRenderer.color = WithAlpha(UiTheme.CyanAccent, Mathf.Lerp(1f, 0.35f, clamped));
            expandingRingRenderer.color = WithAlpha(UiTheme.CyanAccent, EvaluateRingAlpha(clamped));
        }

        private void Hide()
        {
            if (flashRenderer != null)
            {
                flashRenderer.enabled = false;
            }
            if (expandingRingRenderer != null)
            {
                expandingRingRenderer.enabled = false;
            }
        }

        private void StopAnimation()
        {
            if (placementAnimation == null)
            {
                return;
            }

            StopCoroutine(placementAnimation);
            placementAnimation = null;
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            return new Color(color.r, color.g, color.b, alpha);
        }
    }
}
