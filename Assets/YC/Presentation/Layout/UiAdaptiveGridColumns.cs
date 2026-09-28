using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>保留已有 GridLayoutGroup 及其引用，只在宽度改变时重算可读卡宽与列数。</summary>
    [RequireComponent(typeof(GridLayoutGroup))]
    public sealed class UiAdaptiveGridColumns : UIBehaviour, ILayoutSelfController
    {
        [SerializeField] private Vector2 preferredCardSize = new Vector2(176f, 249f);
        [SerializeField] private int maximumColumns = 12;
        [SerializeField] private float minimumCardWidth;
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
            var columns = Mathf.Clamp(Mathf.FloorToInt((width + grid.spacing.x) /
                (preferredCardSize.x + grid.spacing.x)), 1, Mathf.Max(1, maximumColumns));
            if (columns == 1 && minimumCardWidth > 0 && width >= minimumCardWidth * 2 + grid.spacing.x)
                columns = Mathf.Clamp(Mathf.FloorToInt((width + grid.spacing.x) /
                    (minimumCardWidth + grid.spacing.x)), 1, Mathf.Max(1, maximumColumns));
            var available = (width - grid.spacing.x * (columns - 1)) / columns;
            var size = preferredCardSize * Mathf.Min(1f, available / Mathf.Max(1f, preferredCardSize.x));
            if (grid.constraint != GridLayoutGroup.Constraint.FixedColumnCount) grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            if (grid.constraintCount != columns) grid.constraintCount = columns;
            if (grid.cellSize != size) grid.cellSize = size;
        }
    }
}
