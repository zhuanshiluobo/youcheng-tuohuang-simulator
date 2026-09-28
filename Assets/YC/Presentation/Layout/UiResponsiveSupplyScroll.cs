using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>普通屏分行纵向滚动；超宽屏采用单行横向滚动。</summary>
    public sealed class UiResponsiveSupplyScroll : MonoBehaviour, ILayoutSelfController
    {
        [SerializeField] private ScrollRect scroll;
        [SerializeField] private RectTransform viewport;
        [SerializeField] private UiWrappingRowLayout rows;
        [SerializeField, Min(1f)] private float horizontalAspectThreshold = 2.3333333f;
        private Vector2 lastViewportSize;
        private Vector2Int lastScreenSize;

        private void OnEnable() => MarkDirty();

        private void LateUpdate()
        {
            if (viewport == null) return;
            var screen = new Vector2Int(Screen.width, Screen.height);
            if (lastViewportSize == viewport.rect.size && lastScreenSize == screen) return;
            lastViewportSize = viewport.rect.size;
            lastScreenSize = screen;
            MarkDirty();
        }

        private void MarkDirty() => LayoutRebuilder.MarkLayoutForRebuild((RectTransform)transform);

        public void SetLayoutHorizontal()
        {
            if (scroll == null || viewport == null || rows == null) return;
            var column = GetComponent<VerticalLayoutGroup>();
            var innerWidth = viewport.rect.width - (column == null ? 0 : column.padding.horizontal);
            var horizontal = Screen.width / (float)Mathf.Max(1, Screen.height) >= horizontalAspectThreshold ||
                rows.WantsSingleRow(innerWidth);
            if (rows.HorizontalMode != horizontal)
            {
                rows.SetHorizontalMode(horizontal);
                scroll.StopMovement();
                ((RectTransform)transform).anchoredPosition = Vector2.zero;
            }
            scroll.horizontal = horizontal;
            scroll.vertical = !horizontal;
            var contentWidth = rows.HorizontalContentWidth + (column == null ? 0 : column.padding.horizontal);
            ((RectTransform)transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,
                horizontal ? Mathf.Max(viewport.rect.width, contentWidth) : viewport.rect.width);
        }

        public void SetLayoutVertical() { }
    }
}
