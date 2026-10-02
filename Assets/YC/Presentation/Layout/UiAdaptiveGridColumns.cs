using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>按可用宽度计算格子；支持自适应列数或固定列数填满宽度。</summary>
    [RequireComponent(typeof(GridLayoutGroup))]
    public sealed class UiAdaptiveGridColumns : UIBehaviour, ILayoutSelfController
    {
        [SerializeField] private Vector2 preferredCardSize = new Vector2(176f, 249f);
        [SerializeField] private int maximumColumns = 12;
        [SerializeField] private float minimumCardWidth;
        [Tooltip("固定使用最大列数，并按网格实际可用宽度等比缩放格子。")]
        [SerializeField] private bool fitWidthToFixedColumns;
        private GridLayoutGroup grid;
        public void Configure(Vector2 size, float minimumWidth)
        {
            preferredCardSize = size;
            minimumCardWidth = minimumWidth;
            UpdateColumns();
        }
        protected override void OnEnable() { base.OnEnable(); UpdateColumns(); }
        protected override void OnRectTransformDimensionsChange() { base.OnRectTransformDimensionsChange(); UpdateColumns(); }
        public void SetLayoutHorizontal() { UpdateColumns(); }
        public void SetLayoutVertical() { }
        private void UpdateColumns()
        {
            if (grid == null) grid = GetComponent<GridLayoutGroup>();
            if (grid == null) return;
            var width = Mathf.Max(1f, ((RectTransform)transform).rect.width - grid.padding.horizontal);
            var columns = fitWidthToFixedColumns ? Mathf.Max(1, maximumColumns) :
                Mathf.Clamp(Mathf.FloorToInt((width + grid.spacing.x) /
                (preferredCardSize.x + grid.spacing.x)), 1, Mathf.Max(1, maximumColumns));
            if (!fitWidthToFixedColumns && columns == 1 && minimumCardWidth > 0 && width >= minimumCardWidth * 2 + grid.spacing.x)
                columns = Mathf.Clamp(Mathf.FloorToInt((width + grid.spacing.x) /
                    (minimumCardWidth + grid.spacing.x)), 1, Mathf.Max(1, maximumColumns));
            var available = Mathf.Max(0f, (width - grid.spacing.x * (columns - 1)) / columns);
            var scale = available / Mathf.Max(1f, preferredCardSize.x);
            var size = preferredCardSize * (fitWidthToFixedColumns ? scale : Mathf.Min(1f, scale));
            if (grid.constraint != GridLayoutGroup.Constraint.FixedColumnCount) grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            if (grid.constraintCount != columns) grid.constraintCount = columns;
            if (grid.cellSize != size) grid.cellSize = size;
        }
    }
}
