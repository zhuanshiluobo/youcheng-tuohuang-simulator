using System.Collections;
using UnityEngine;

namespace YC.Presentation
{
    public sealed class MapHighlightPulse : MonoBehaviour
    {
        public const float PulseDuration = 0.6f;
        public const float MinimumAlpha = 0.4f;
        public const float MaximumAlpha = 1f;

        [SerializeField] private SpriteRenderer border;
        private Coroutine pulse;
        private bool highlighted;
        public bool IsHighlighted => highlighted;

        public bool Bind(SpriteRenderer borderRenderer, out string reason)
        {
            if (borderRenderer == null)
            {
                reason = "Map highlight pulse requires a fixed SpriteRenderer binding.";
                return false;
            }
            highlighted = false;
            StopPulse();
            border = borderRenderer;
            ApplyAlpha(0f);
            border.enabled = false;
            reason = string.Empty;
            return true;
        }

        public void SetHighlighted(bool visible)
        {
            if (highlighted == visible) return;
            highlighted = visible;
            StopPulse();
            if (!visible)
            {
                ApplyAlpha(0f);
                if (border != null) border.enabled = false;
                return;
            }
            if (border != null) border.enabled = true;
            ApplyAlpha(EvaluateAlpha(0f));
            if (UnityEngine.Application.isPlaying && isActiveAndEnabled) pulse = StartCoroutine(Pulse());
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
            if (!highlighted || border == null) return;
            border.enabled = true;
            if (UnityEngine.Application.isPlaying && pulse == null) pulse = StartCoroutine(Pulse());
        }

        private void OnDisable() => StopPulse();

        private void ApplyAlpha(float alpha)
        {
            if (border == null) return;
            border.color = new Color(UiTheme.CyanAccent.r, UiTheme.CyanAccent.g, UiTheme.CyanAccent.b, alpha);
        }

        private void StopPulse()
        {
            if (pulse == null) return;
            StopCoroutine(pulse);
            pulse = null;
        }
    }
}
