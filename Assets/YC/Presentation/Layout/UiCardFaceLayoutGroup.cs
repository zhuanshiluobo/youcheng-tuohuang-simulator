using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>卡面完整等比，选中图按原图安全框映射；详情是独立命中区。</summary>
    public sealed class UiCardFaceLayoutGroup : LayoutGroup
    {
        [SerializeField] private RectTransform artwork;
        [SerializeField] private RectTransform selected;
        [SerializeField] private RectTransform details;
        [SerializeField] private float aspect = 12f / 17f;
        [SerializeField] private float detailsHeight = 32f;
        [SerializeField] private float ornamentInset = 18f;
        public override void CalculateLayoutInputHorizontal() { base.CalculateLayoutInputHorizontal(); }
        public override void CalculateLayoutInputVertical() { }
        public override void SetLayoutHorizontal() { Place(); }
        public override void SetLayoutVertical() { Place(); }
        private void Place()
        {
            var size = rectTransform.rect.size;
            var width = Mathf.Min(Mathf.Max(1, size.x - ornamentInset * 2), Mathf.Max(1, size.y - detailsHeight - ornamentInset * 2) * aspect);
            var height = width / aspect;
            var x = (size.x - width) * .5f;
            var y = ornamentInset;
            SetChildAlongAxis(artwork, 0, x, width);
            SetChildAlongAxis(artwork, 1, y, height);
            if (selected != null)
            {
                SetChildAlongAxis(selected, 0, x - 80f * width / 896f, 1054f * width / 896f);
                SetChildAlongAxis(selected, 1, y - 130f * height / 1280f, 1493f * height / 1280f);
            }
            if (details != null)
            {
                SetChildAlongAxis(details, 0, padding.left, Mathf.Max(1, size.x - padding.horizontal));
                SetChildAlongAxis(details, 1, size.y - detailsHeight, detailsHeight);
            }
        }
    }
}
