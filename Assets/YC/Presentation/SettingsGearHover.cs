using UnityEngine;
using UnityEngine.EventSystems;

namespace YC.Presentation
{
    [DisallowMultipleComponent]
    public sealed class SettingsGearHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] private RectTransform visual;
        [SerializeField, Min(1f)] private float hoverScale = 1.1f;
        [SerializeField, Min(0f)] private float duration = 0.1f;
        private Vector3 restingScale;
        private Vector3 transitionStart;
        private float elapsed;
        private bool hovered;

        private void Awake()
        {
            if (visual == null) visual = (RectTransform)transform;
            restingScale = visual.localScale;
            transitionStart = restingScale;
        }

        public void OnPointerEnter(PointerEventData eventData) => SetHovered(true);
        public void OnPointerExit(PointerEventData eventData) => SetHovered(false);

        private void SetHovered(bool value)
        {
            hovered = value;
            transitionStart = visual.localScale;
            elapsed = 0f;
        }

        private void Update()
        {
            elapsed += Time.unscaledDeltaTime;
            float progress = duration <= 0f ? 1f : Mathf.Clamp01(elapsed / duration);
            visual.localScale = Vector3.Lerp(transitionStart,
                restingScale * (hovered ? hoverScale : 1f), Mathf.SmoothStep(0f, 1f, progress));
        }

        private void OnDisable()
        {
            hovered = false;
            if (visual != null) visual.localScale = restingScale;
            transitionStart = restingScale;
            elapsed = duration;
        }
    }
}