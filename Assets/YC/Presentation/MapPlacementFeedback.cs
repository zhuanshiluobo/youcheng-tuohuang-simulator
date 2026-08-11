using UnityEngine;

namespace YC.Presentation
{
    public sealed class MapPlacementFeedback : MonoBehaviour
    {
        public const float Duration = 0.15f;
        public const float FlashStartScale = 1.2f;
        public const float FlashEndScale = 1f;
        public const float FlashEndAlpha = 0.35f;
        public const float RingStartScale = 1f;
        public const float RingEndScale = 1.8f;
        public const string AnimationStatePath = "Base Layer.MapPlacementFeedback";
        public const string CompletionEventName = "OnPlacementAnimationCompleted";

        [SerializeField] private SpriteRenderer flashRenderer;
        [SerializeField] private SpriteRenderer expandingRingRenderer;
        [SerializeField] private Animator animator;

        private static readonly int AnimationStateHash = Animator.StringToHash(AnimationStatePath);

        public bool Bind(out string reason)
        {
            if (flashRenderer == null || expandingRingRenderer == null || animator == null ||
                animator.runtimeAnimatorController == null)
            {
                reason = "Map placement feedback requires two fixed ring renderers and an Animator controller.";
                return false;
            }
            if (animator.gameObject != gameObject ||
                animator.updateMode != AnimatorUpdateMode.UnscaledTime ||
                animator.cullingMode != AnimatorCullingMode.AlwaysAnimate ||
                animator.applyRootMotion)
            {
                reason = "Map placement feedback Animator must be fixed on the owner, unscaled, always animated, and root-motion free.";
                return false;
            }
            ResetFeedback();
            reason = string.Empty;
            return true;
        }

        public void Play()
        {
            if (flashRenderer == null || expandingRingRenderer == null || animator == null ||
                animator.runtimeAnimatorController == null)
            {
                Debug.LogError("Map placement feedback is not bound.", this);
                return;
            }

            DisableAnimator();
            ApplyFrame(0f);
            flashRenderer.enabled = true;
            expandingRingRenderer.enabled = true;
            if (!UnityEngine.Application.isPlaying || !isActiveAndEnabled)
            {
                return;
            }

            animator.enabled = true;
            animator.Play(AnimationStateHash, 0, 0f);
            animator.Update(0f);
        }

        public void ResetFeedback()
        {
            DisableAnimator();
            Hide();
        }

        public static float EvaluateFlashScale(float progress) =>
            Mathf.Lerp(FlashStartScale, FlashEndScale, Mathf.Clamp01(progress));
        public static float EvaluateRingScale(float progress) =>
            Mathf.Lerp(RingStartScale, RingEndScale, Mathf.Clamp01(progress));
        public static float EvaluateFlashAlpha(float progress) =>
            Mathf.Lerp(1f, FlashEndAlpha, Mathf.Clamp01(progress));
        public static float EvaluateRingAlpha(float progress) => 1f - Mathf.Clamp01(progress);

        public void OnPlacementAnimationCompleted()
        {
            ApplyFrame(1f);
            Hide();
            DisableAnimator();
        }

        private void OnDisable() => ResetFeedback();

        private void ApplyFrame(float progress)
        {
            var clamped = Mathf.Clamp01(progress);
            flashRenderer.transform.localScale = Vector3.one * EvaluateFlashScale(clamped);
            expandingRingRenderer.transform.localScale = Vector3.one * EvaluateRingScale(clamped);
            flashRenderer.color = WithAlpha(UiTheme.CyanAccent, EvaluateFlashAlpha(clamped));
            expandingRingRenderer.color = WithAlpha(UiTheme.CyanAccent, EvaluateRingAlpha(clamped));
        }

        private void Hide()
        {
            if (flashRenderer != null) flashRenderer.enabled = false;
            if (expandingRingRenderer != null) expandingRingRenderer.enabled = false;
        }

        private void DisableAnimator()
        {
            if (animator != null)
            {
                animator.enabled = false;
            }
        }

        private static Color WithAlpha(Color color, float alpha) => new Color(color.r, color.g, color.b, alpha);
    }
}
