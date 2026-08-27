using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ActionButtonPressFeedback : MonoBehaviour,
        IPointerEnterHandler,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerExitHandler
    {
        public const float PressDuration = 0.08f;
        public const float PressedScale = 0.92f;
        public const float ReleaseDuration = 0.12f;

        private Button button;
        private Outline outline;
        [SerializeField] private Graphic[] hoverBorderGraphics = new Graphic[0];
        [SerializeField] private Color highlightColor;
        private Vector3 restingScale = Vector3.one;
        private Color restingOutlineColor;
        private Color[] restingHoverBorderColors = new Color[0];
        private Coroutine scaleAnimation;
        private bool configured;
        private bool hovered;
        private bool pressed;

        public void Configure(Button configuredButton, Outline configuredOutline)
        {
            Configure(
                configuredButton,
                configuredOutline,
                hoverBorderGraphics,
                UiTheme.CyanAccent);
        }

        public void Configure(
            Button configuredButton,
            Outline configuredOutline,
            Color configuredHighlightColor)
        {
            Configure(
                configuredButton,
                configuredOutline,
                hoverBorderGraphics,
                configuredHighlightColor);
        }

        public void Configure(
            Button configuredButton,
            Outline configuredOutline,
            Graphic[] configuredHoverBorderGraphics,
            Color configuredHighlightColor)
        {
            button = configuredButton;
            outline = configuredOutline;
            hoverBorderGraphics = configuredHoverBorderGraphics ?? new Graphic[0];
            highlightColor = configuredHighlightColor;
            restingScale = transform.localScale;
            restingOutlineColor = outline == null ? Color.clear : outline.effectColor;
            CaptureRestingHoverBorderColors();
            hovered = false;
            pressed = false;
            configured = true;
            RestoreVisuals();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            EnsureConfigured();
            hovered = true;
            if (!pressed)
            {
                ApplyIdleVisuals();
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            EnsureConfigured();
            if (!IsLeftPointer(eventData) || button == null || !button.IsInteractable())
            {
                return;
            }

            pressed = true;
            SetHighlighted(true);

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
            hovered = false;
            if (pressed)
            {
                Release();
                return;
            }

            ApplyIdleVisuals();
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
                transform.localScale = restingScale;
                ApplyIdleVisuals();
                return;
            }

                scaleAnimation = StartCoroutine(BounceBack());
        }

        private IEnumerator BounceBack()
        {
            var raisedScale = restingScale * 1.04f;
            yield return ScaleTo(transform.localScale, raisedScale, ReleaseDuration * 0.42f);
            yield return ScaleTo(raisedScale, restingScale, ReleaseDuration * 0.58f);
            ApplyIdleVisuals();
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
            outline = hoverBorderGraphics != null && hoverBorderGraphics.Length > 0
                ? null
                : GetComponent<Outline>();
            if (highlightColor.a <= 0f)
            {
                highlightColor = UiTheme.CyanAccent;
            }
            restingScale = transform.localScale;
            restingOutlineColor = outline == null ? Color.clear : outline.effectColor;
            CaptureRestingHoverBorderColors();
            configured = true;
        }

        private void ResetImmediately()
        {
            hovered = false;
            pressed = false;
            StopAnimation();
            transform.localScale = restingScale;
            RestoreVisuals();
        }

        private void ApplyIdleVisuals()
        {
            SetHighlighted(hovered && button != null && button.IsInteractable());
        }

        private void SetHighlighted(bool highlighted)
        {
            if (outline != null)
            {
                outline.effectColor = highlighted ? highlightColor : restingOutlineColor;
            }

            for (var i = 0; i < hoverBorderGraphics.Length; i++)
            {
                if (hoverBorderGraphics[i] != null)
                {
                    hoverBorderGraphics[i].color = highlighted
                        ? highlightColor
                        : restingHoverBorderColors[i];
                }
            }
        }

        private void CaptureRestingHoverBorderColors()
        {
            restingHoverBorderColors = new Color[hoverBorderGraphics.Length];
            for (var i = 0; i < hoverBorderGraphics.Length; i++)
            {
                restingHoverBorderColors[i] = hoverBorderGraphics[i] == null
                    ? Color.clear
                    : hoverBorderGraphics[i].color;
            }
        }

        private void RestoreVisuals()
        {
            if (outline != null)
            {
                outline.effectColor = restingOutlineColor;
            }

            for (var i = 0; i < hoverBorderGraphics.Length; i++)
            {
                if (hoverBorderGraphics[i] != null)
                {
                    hoverBorderGraphics[i].color = restingHoverBorderColors[i];
                }
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
