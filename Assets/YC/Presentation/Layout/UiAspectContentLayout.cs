using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>比例图像的唯一尺寸驱动者；坐标映射继续使用被驱动的最终图像矩形。</summary>
    public sealed class UiAspectContentLayout : LayoutGroup
    {
        [SerializeField] private RectTransform artwork;
        [SerializeField] private Vector2 preferredSize = new Vector2(270f, 500f);
        [SerializeField] private UiCitySlotsLayout projectedSlots;
        public override void CalculateLayoutInputHorizontal() { base.CalculateLayoutInputHorizontal(); }
        public override void CalculateLayoutInputVertical() { }
        public override void SetLayoutHorizontal() { Arrange(); }
        public override void SetLayoutVertical() { Arrange(); }
        private void Arrange()
        {
            if (artwork == null) return;
            var available = new Vector2(Mathf.Max(0f, rectTransform.rect.width - padding.horizontal),
                Mathf.Max(0f, rectTransform.rect.height - padding.vertical));
            var scale = Mathf.Min(1f, available.x / Mathf.Max(1f, preferredSize.x), available.y / Mathf.Max(1f, preferredSize.y));
            var size = preferredSize * scale;
            SetChildAlongAxis(artwork, 0, padding.left + (available.x - size.x) * .5f, size.x);
            SetChildAlongAxis(artwork, 1, padding.top, size.y);
            if (projectedSlots != null) projectedSlots.Refresh();
        }
    }
}
