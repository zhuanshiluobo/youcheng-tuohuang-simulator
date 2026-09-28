using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>只提供窗口首选尺寸；原生 VLG 是面板矩形的唯一驱动者。</summary>
    public sealed class UiWindowSizeInput : MonoBehaviour
    {
        [SerializeField] private RectTransform panel;
        [SerializeField] private Vector2 preferredSize = new Vector2(960f, 700f);
        [SerializeField] private Vector2 preferredOffset;
        private RectOffset basePadding;
        public Vector2 PreferredSize => preferredSize;
        public Vector2 PreferredOffset => preferredOffset;
        private void Awake() => Configure(preferredSize, preferredOffset);
        public void Configure(Vector2 size, Vector2 offset)
        {
            preferredSize = size;
            preferredOffset = offset;
            var layout = GetComponent<VerticalLayoutGroup>();
            var input = panel == null ? null : panel.GetComponent<LayoutElement>();
            if (layout == null || input == null) return;
            if (basePadding == null) basePadding = new RectOffset(layout.padding.left, layout.padding.right,
                layout.padding.top, layout.padding.bottom);
            input.preferredWidth = size.x;
            input.preferredHeight = size.y;
            layout.padding = new RectOffset(basePadding.left + Mathf.RoundToInt(Mathf.Max(0, offset.x * 2)),
                basePadding.right + Mathf.RoundToInt(Mathf.Max(0, -offset.x * 2)),
                basePadding.top + Mathf.RoundToInt(Mathf.Max(0, -offset.y * 2)),
                basePadding.bottom + Mathf.RoundToInt(Mathf.Max(0, offset.y * 2)));
            LayoutRebuilder.MarkLayoutForRebuild((RectTransform)transform);
        }
    }
}
