using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class UiEventArtworkLayoutGroup : LayoutGroup
    {
        [SerializeField] private RectTransform artworkSurface;
        [SerializeField] private Vector2 sourceSize = new Vector2(850, 600);
        private Vector2 artworkSize;

        /// <summary>使用实际单张卡面的尺寸，而非图集或弹窗尺寸。</summary>
        public void SetArtworkSize(RectTransform source, Vector2 size)
        {
            if (source == null || artworkSurface == null || !source.IsChildOf(artworkSurface)) return;
            if (float.IsNaN(size.x) || float.IsInfinity(size.x) || size.x <= 0f ||
                float.IsNaN(size.y) || float.IsInfinity(size.y) || size.y <= 0f)
                size = Vector2.zero;
            if (artworkSize == size) return;
            artworkSize = size;
            SetDirty();
        }

        private Vector2 EffectiveSize => artworkSize.x > 0f && artworkSize.y > 0f ? artworkSize : sourceSize;
        public override void CalculateLayoutInputHorizontal()
        { base.CalculateLayoutInputHorizontal(); SetLayoutInputForAxis(0, EffectiveSize.x, 1, 0); }
        public override void CalculateLayoutInputVertical() { SetLayoutInputForAxis(0, EffectiveSize.y, 1, 1); }
        public override void SetLayoutHorizontal() { Place(0); }
        public override void SetLayoutVertical() { Place(1); }
        private void Place(int axis)
        {
            if (artworkSurface == null) return;
            var source = EffectiveSize;
            if (source.x <= 0f || source.y <= 0f) return;
            var scale = Mathf.Min(rectTransform.rect.width / source.x, rectTransform.rect.height / source.y);
            var size = source[axis] * scale;
            SetChildAlongAxis(artworkSurface, axis, (rectTransform.rect.size[axis] - size) * .5f, size);
        }
    }
}
