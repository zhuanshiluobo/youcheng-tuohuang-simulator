using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>唯一城市原图内的格位容器；原格位使用源图归一化坐标，不受地图相机影响。</summary>
    public sealed class UiCitySlotsLayout : LayoutGroup
    {
        [SerializeField] private RectTransform slots;
        public override void CalculateLayoutInputHorizontal() { base.CalculateLayoutInputHorizontal(); }
        public override void CalculateLayoutInputVertical() { }
        public override void SetLayoutHorizontal() { Refresh(); }
        public override void SetLayoutVertical() { Refresh(); }
        public void Refresh()
        {
            if (slots == null) return;
            SetChildAlongAxis(slots, 0, 0f, rectTransform.rect.width);
            SetChildAlongAxis(slots, 1, 0f, rectTransform.rect.height);
        }
    }
}
