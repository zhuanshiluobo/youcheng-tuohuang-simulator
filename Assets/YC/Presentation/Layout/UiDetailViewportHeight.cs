using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>向父布局报告一行或一行多一点的视口高度；两栏仍由同一个 ScrollRect 滚动。</summary>
    public sealed class UiDetailViewportHeight : UIBehaviour, ILayoutElement
    {
        [SerializeField] private RectTransform viewport;
        [SerializeField] private VerticalLayoutGroup[] columns;
        [SerializeField] private GameObject[] emptyHints;
        [SerializeField, Range(0f, 1f)] private float nextRowVisibleFraction = .2f;
        private float measuredHeight;

        public float minWidth => -1f;
        public float preferredWidth => -1f;
        public float flexibleWidth => -1f;
        public float minHeight => measuredHeight;
        public float preferredHeight => measuredHeight;
        public float flexibleHeight => 0f;
        public int layoutPriority => 2;

        protected override void OnEnable()
        {
            base.OnEnable();
            Canvas.willRenderCanvases += ReadCompletedLayout;
            RefreshHeight();
            MarkDirty();
        }

        protected override void OnDisable()
        {
            Canvas.willRenderCanvases -= ReadCompletedLayout;
            MarkDirty();
            base.OnDisable();
        }

        private void LateUpdate() => RefreshHeight();
        // 条目自身的原生布局完成后，再接收换行导致的高度变化。
        private void ReadCompletedLayout() => RefreshHeight();
        public void CalculateLayoutInputHorizontal() { }
        public void CalculateLayoutInputVertical() => measuredHeight = MeasureHeight();

        private void RefreshHeight()
        {
            var height = MeasureHeight();
            if (Mathf.Approximately(measuredHeight, height)) return;
            measuredHeight = height;
            MarkDirty();
        }

        private float MeasureHeight()
        {
            var visibleHeight = 0f;
            for (var columnIndex = 0; columns != null && columnIndex < columns.Length; columnIndex++)
            {
                var column = columns[columnIndex];
                if (column == null) continue;
                var count = 0;
                var firstHeight = 0f;
                var secondHeight = 0f;
                foreach (Transform child in column.transform)
                {
                    var hint = emptyHints != null && columnIndex < emptyHints.Length ? emptyHints[columnIndex] : null;
                    if (!GameplayMainModules.IsDetailRow(child, hint)) continue;
                    var height = LayoutUtility.GetPreferredHeight(child as RectTransform);
                    if (count == 0) firstHeight = height;
                    else if (count == 1) secondHeight = height;
                    count++;
                }
                if (count == 0) continue;
                var heightForRows = firstHeight;
                if (count > 1) heightForRows += column.spacing + secondHeight * nextRowVisibleFraction;
                visibleHeight = Mathf.Max(visibleHeight, column.padding.vertical + heightForRows);
            }
            // 视口的留白和列内边距都来自预制体，不能占用要求显示的条目高度。
            var viewportInset = viewport == null ? 0f : Mathf.Max(0f, -viewport.sizeDelta.y);
            return viewportInset + visibleHeight;
        }

        private void MarkDirty()
        {
            if (!CanvasUpdateRegistry.IsRebuildingLayout())
                LayoutRebuilder.MarkLayoutForRebuild(transform as RectTransform);
        }
    }
}
