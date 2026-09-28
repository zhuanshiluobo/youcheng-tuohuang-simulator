using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class UiEventArtworkLayoutGroup : LayoutGroup
    {
        [SerializeField] private RectTransform artworkSurface;
        [SerializeField] private Vector2 sourceSize = new Vector2(850, 600);
        public override void CalculateLayoutInputHorizontal()
        { base.CalculateLayoutInputHorizontal(); SetLayoutInputForAxis(0, sourceSize.x, 1, 0); }
        public override void CalculateLayoutInputVertical() { SetLayoutInputForAxis(0, sourceSize.y, 1, 1); }
        public override void SetLayoutHorizontal() { Place(0); }
        public override void SetLayoutVertical() { Place(1); }
        private void Place(int axis)
        {
            if (artworkSurface == null) return;
            var scale = Mathf.Min(rectTransform.rect.width / sourceSize.x, rectTransform.rect.height / sourceSize.y);
            var size = sourceSize[axis] * scale;
            SetChildAlongAxis(artworkSurface, axis, (rectTransform.rect.size[axis] - size) * .5f, size);
        }
    }
}
