using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>只扩展装饰边框，视觉包住相邻明细；模块和明细仍是原生布局中的独立项。</summary>
    public sealed class UiModuleDetailFrame : UIBehaviour, ILayoutController
    {
        [SerializeField] private RectTransform frame;
        [SerializeField] private RectTransform detail;
        private Vector2 originalMin;
        private Vector2 originalMax;
        private bool captured;
        private readonly Vector3[] corners = new Vector3[4];

        protected override void OnEnable()
        {
            base.OnEnable();
            if (frame != null && !captured)
            {
                originalMin = frame.offsetMin;
                originalMax = frame.offsetMax;
                captured = true;
            }
            Canvas.willRenderCanvases += RefreshFrame;
            LayoutRebuilder.MarkLayoutForRebuild(transform as RectTransform);
        }

        protected override void OnDisable()
        {
            Canvas.willRenderCanvases -= RefreshFrame;
            if (frame != null && captured) ApplyOffsets(originalMin, originalMax);
            base.OnDisable();
        }

        public void SetLayoutHorizontal() { }
        public void SetLayoutVertical() => RefreshFrame();

        private void RefreshFrame()
        {
            if (!captured || frame == null) return;
            var owner = transform as RectTransform;
            var minimum = owner.rect.min;
            var maximum = owner.rect.max;
            if (detail != null && detail.gameObject.activeInHierarchy)
            {
                detail.GetWorldCorners(corners);
                foreach (var corner in corners)
                {
                    var local = (Vector2)owner.InverseTransformPoint(corner);
                    minimum = Vector2.Min(minimum, local);
                    maximum = Vector2.Max(maximum, local);
                }
            }
            ApplyOffsets(originalMin + minimum - owner.rect.min, originalMax + maximum - owner.rect.max);
        }

        private void ApplyOffsets(Vector2 minimum, Vector2 maximum)
        {
            if ((frame.offsetMin - minimum).sqrMagnitude > .0001f) frame.offsetMin = minimum;
            if ((frame.offsetMax - maximum).sqrMagnitude > .0001f) frame.offsetMax = maximum;
        }
    }
}
