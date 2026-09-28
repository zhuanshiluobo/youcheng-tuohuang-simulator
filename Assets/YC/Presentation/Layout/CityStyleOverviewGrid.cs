using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>三列总览按视口宽高等比容纳完整卡；低高度只在本区滚动。</summary>
    [RequireComponent(typeof(GridLayoutGroup))]
    public sealed class CityStyleOverviewGrid : UIBehaviour, ILayoutSelfController
    {
        [SerializeField] private RectTransform viewport;
        [SerializeField] private int columns = 3;
        [SerializeField] private float minimumCardWidth = 250;
        [SerializeField] private float bandHeight = 24;
        [SerializeField] private float cardAspect = 1.55f;
        private GridLayoutGroup grid;
        protected override void OnEnable() { base.OnEnable(); Refresh(); }
        protected override void OnRectTransformDimensionsChange() { base.OnRectTransformDimensionsChange(); Refresh(); }
        public void SetLayoutHorizontal() => Refresh();
        public void SetLayoutVertical() { }
        private void Refresh()
        {
            if (viewport == null) return;
            if (grid == null) grid = GetComponent<GridLayoutGroup>();
            var width = Mathf.Max(1, viewport.rect.width - grid.padding.horizontal);
            var count = Mathf.Clamp(Mathf.FloorToInt((width + grid.spacing.x) /
                (minimumCardWidth + grid.spacing.x)), 1, columns);
            var cardWidth = (width - (count - 1) * grid.spacing.x) / count;
            // 正常六张同时可见，超过六张的供应自然延伸滚动。
            var heightWidth = ((viewport.rect.height - grid.padding.vertical - grid.spacing.y) / 2 - bandHeight) * cardAspect;
            cardWidth = Mathf.Min(cardWidth, Mathf.Max(minimumCardWidth, heightWidth));
            var size = new Vector2(cardWidth, cardWidth / cardAspect + bandHeight);
            var visibleCards = 0;
            for (var i = 0; i < transform.childCount; i++)
                if (transform.GetChild(i).gameObject.activeSelf) visibleCards++;
            var rows = Mathf.Max(1, Mathf.CeilToInt((float)visibleCards / count));
            if (grid.constraintCount != rows) grid.constraintCount = rows;
            if (grid.cellSize != size) grid.cellSize = size;
        }
    }
}
