using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>只用于供应纯选择模式；下层上移125个牌面贴图像素，并计入滚动占位。</summary>
    public sealed class FacilitySupplyStackLayout : LayoutGroup
    {
        private IReadOnlyList<FacilityEffectCardView> cards;
        private float sourcePixelsWidth;
        private float preferredSlotWidth;
        private float lastStep = -1f;
        public void Configure(IReadOnlyList<FacilityEffectCardView> topFirst, float artworkWidth, float slotWidth)
        { cards = topFirst; sourcePixelsWidth = artworkWidth; preferredSlotWidth = slotWidth; SetDirty(); }
        public float LayerStep
        {
            get
            {
                if (cards == null || cards.Count == 0) return 0f;
                var image = cards[0].CardImage;
                if (image.texture == null) return 0f;
                // 使用Sprite原始裁片尺寸，不把RawImage防串图的半像素采样内缩当作素材尺寸。
                return sourcePixelsWidth <= 0f ? 0f : 125f * image.rectTransform.rect.width / sourcePixelsWidth;
            }
        }
        private float CardHeight => cards == null || cards.Count == 0 ? 0f : cards[0].MeasureSlotSize(rectTransform.rect.width).y;
        public override void CalculateLayoutInputHorizontal()
        { base.CalculateLayoutInputHorizontal(); SetLayoutInputForAxis(preferredSlotWidth, preferredSlotWidth, 1f, 0); }
        public override void CalculateLayoutInputVertical()
        {
            var height = CardHeight + Mathf.Max(0, (cards?.Count ?? 0) - 1) * LayerStep;
            SetLayoutInputForAxis(height, height, 0f, 1);
        }
        public override void SetLayoutHorizontal()
        {
            if (cards == null) return;
            foreach (var card in cards) SetChildAlongAxis(card.CardRect, 0, 0f, rectTransform.rect.width);
        }
        public override void SetLayoutVertical()
        {
            if (cards == null) return;
            var step = LayerStep; var height = CardHeight;
            for (int n = 0; n < cards.Count; n++)
                SetChildAlongAxis(cards[n].CardRect, 1, (cards.Count - 1 - n) * step, height);
        }
        private void LateUpdate()
        {
            var step = LayerStep;
            if (Mathf.Approximately(step, lastStep)) return;
            lastStep = step; SetDirty();
        }
    }
}
