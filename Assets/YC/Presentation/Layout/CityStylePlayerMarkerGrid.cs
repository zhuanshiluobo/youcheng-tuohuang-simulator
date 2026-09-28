using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>只计算等比格尺寸，四个玩家槽的位置由原生 GridLayoutGroup 管理。</summary>
    [RequireComponent(typeof(GridLayoutGroup))]
    public sealed class CityStylePlayerMarkerGrid : UIBehaviour, ILayoutSelfController
    {
        [SerializeField, Range(0.1f, 1f)] private float heightFraction = 0.8f;
        [SerializeField, Min(0)] private float gapRatio = 0.15f;
        [SerializeField] private RectTransform sizeReference;
        private GridLayoutGroup grid;
        protected override void OnEnable() { base.OnEnable(); Refresh(); }
        protected override void OnRectTransformDimensionsChange() { base.OnRectTransformDimensionsChange(); Refresh(); }
        public void SetLayoutHorizontal() => Refresh();
        public void SetLayoutVertical() { }
        private void Refresh()
        {
            if (grid == null) grid = GetComponent<GridLayoutGroup>();
            var rect = (sizeReference != null ? sizeReference : (RectTransform)transform).rect;
            var width = Mathf.Max(0, rect.width - grid.padding.horizontal);
            var height = Mathf.Max(0, rect.height - grid.padding.vertical);
            var referenceGrid = sizeReference != null ? sizeReference.GetComponent<GridLayoutGroup>() : null;
            var columns = Mathf.Max(1, referenceGrid != null ? referenceGrid.constraintCount : grid.constraintCount);
            var rows = Mathf.CeilToInt(4f / columns);
            var cell = Mathf.Min(width / (columns + (columns - 1) * gapRatio),
                height * heightFraction / (rows + (rows - 1) * gapRatio));
            var size = Vector2.one * cell;
            var spacing = Vector2.one * (cell * gapRatio);
            if (grid.cellSize != size) grid.cellSize = size;
            if (grid.spacing != spacing) grid.spacing = spacing;
        }
    }
}
