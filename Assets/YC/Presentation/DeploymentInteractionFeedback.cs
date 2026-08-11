using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ActionButtonPressFeedback : MonoBehaviour,
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

}
