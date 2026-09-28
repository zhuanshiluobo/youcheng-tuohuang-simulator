using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>保持内容自然布局高度；可用高度不足时整体等比缩小，避免底部溢出。</summary>
    public sealed class UiFitContentHeight : UIBehaviour, ILayoutSelfController
    {
        private Vector2 lastParentSize;
        private RectTransform Rect => (RectTransform)transform;
        private RectTransform Parent => transform.parent as RectTransform;

        protected override void OnEnable() { base.OnEnable(); MarkDirty(); }
        protected override void OnRectTransformDimensionsChange() { MarkDirty(); }
        protected override void OnTransformParentChanged() { base.OnTransformParentChanged(); MarkDirty(); }

        private void LateUpdate()
        {
            if (Parent == null || lastParentSize == Parent.rect.size) return;
            lastParentSize = Parent.rect.size;
            MarkDirty();
        }

        private void MarkDirty()
        {
            if (IsActive() && !CanvasUpdateRegistry.IsRebuildingLayout())
                LayoutRebuilder.MarkLayoutForRebuild(Rect);
        }

        public void SetLayoutHorizontal()
        {
            if (Parent != null)
                Rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Mathf.Max(0f, Parent.rect.width));
        }

        public void SetLayoutVertical()
        {
            if (Parent == null) return;
            var height = Mathf.Max(0f, LayoutUtility.GetPreferredHeight(Rect));
            var scale = height > 0f ? Mathf.Min(1f, Mathf.Max(0f, Parent.rect.height) / height) : 1f;
            Rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            Rect.localScale = new Vector3(scale, scale, 1f);
            Rect.anchoredPosition = new Vector2((Parent.rect.width - Rect.rect.width * scale) * .5f, 0f);
        }
    }
}
