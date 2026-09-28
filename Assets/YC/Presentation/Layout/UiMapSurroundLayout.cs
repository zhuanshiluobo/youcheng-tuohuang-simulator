using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>围绕实时地图矩形铺底；装饰不再保留旧地图开口。</summary>
    public sealed class UiMapSurroundLayout : LayoutGroup
    {
        [SerializeField] private RectTransform map;
        [SerializeField] private RectTransform content;
        [SerializeField] private RectTransform left;
        [SerializeField] private RectTransform right;
        [SerializeField] private RectTransform lower;
        [SerializeField] private RectTransform seam;
        [SerializeField] private float edgeGap = 8f;
        public override void CalculateLayoutInputHorizontal() { base.CalculateLayoutInputHorizontal(); }
        public override void CalculateLayoutInputVertical() { }
        public override void SetLayoutHorizontal() { Refresh(); }
        public override void SetLayoutVertical() { Refresh(); }
        public void Refresh()
        {
            if (map == null || content == null) return;
            var bounds = LocalRect(content);
            var mapBounds = LocalRect(map);
            var bottom = Mathf.Max(0f, bounds.yMin - edgeGap);
            var top = Mathf.Min(rectTransform.rect.height, bounds.yMax + edgeGap);
            if (!map.gameObject.activeInHierarchy)
            {
                Place(left, 0f, bottom, rectTransform.rect.width, top - bottom);
                Place(right, 0f, bottom, 0f, 0f); Place(lower, 0f, bottom, 0f, 0f);
            }
            else
            {
                Place(left, 0f, bottom, mapBounds.xMin, top - bottom);
                Place(right, mapBounds.xMax, bottom, Mathf.Max(0f, rectTransform.rect.width - mapBounds.xMax), top - bottom);
                Place(lower, mapBounds.xMin, bottom, mapBounds.width, Mathf.Max(0f, mapBounds.yMin - bottom));
            }
            Place(seam, 0f, bounds.yMax, rectTransform.rect.width, edgeGap);
        }
        private Rect LocalRect(RectTransform item)
        {
            var corners = new Vector3[4]; item.GetWorldCorners(corners);
            var a = rectTransform.InverseTransformPoint(corners[0]); var b = rectTransform.InverseTransformPoint(corners[2]);
            return Rect.MinMaxRect(a.x - rectTransform.rect.xMin, a.y - rectTransform.rect.yMin,
                b.x - rectTransform.rect.xMin, b.y - rectTransform.rect.yMin);
        }
        private void Place(RectTransform item, float x, float y, float width, float height)
        {
            if (item == null) return;
            SetChildAlongAxis(item, 0, x, width);
            SetChildAlongAxis(item, 1, rectTransform.rect.height - y - height, height);
        }
    }
}
