using System;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>合并底图的比例只转换为原生布局输入，不写任何子物体坐标。</summary>
    [ExecuteAlways]
    public sealed class CardViewerLayoutInputs : MonoBehaviour
    {
        [Serializable] public struct Item { public LayoutElement element; public Vector2 referenceSize; }
        [Serializable] public struct Label { public Text text; public int referenceSize; }
        [SerializeField] private RectTransform window;
        [SerializeField] private VerticalLayoutGroup content;
        [SerializeField] private HorizontalLayoutGroup buttons;
        [SerializeField] private Vector2 referenceSize = new Vector2(1774, 887);
        [SerializeField] private RectOffset referencePadding;
        [SerializeField] private float buttonSpacing = 24;
        [SerializeField] private Item[] items;
        [SerializeField] private Label[] labels;
        private Vector2 previous = new Vector2(-1, -1);
        private void OnEnable() { previous = new Vector2(-1, -1); Refresh(); }
        private void LateUpdate() { Refresh(); }
        private void OnRectTransformDimensionsChange() { Refresh(); }
        private void Refresh()
        {
            if (window == null || content == null || buttons == null || referencePadding == null ||
                items == null || labels == null || window.rect.size == previous) return;
            previous = window.rect.size;
            var scale = Mathf.Min(previous.x / referenceSize.x, previous.y / referenceSize.y);
            if (scale <= 0) return;
            content.padding = new RectOffset(Mathf.RoundToInt(referencePadding.left * scale),
                Mathf.RoundToInt(referencePadding.right * scale), Mathf.RoundToInt(referencePadding.top * scale),
                Mathf.RoundToInt(referencePadding.bottom * scale));
            buttons.spacing = buttonSpacing * scale;
            foreach (var item in items)
            {
                if (item.element == null) continue;
                item.element.preferredWidth = item.referenceSize.x < 0 ? -1 : item.referenceSize.x * scale;
                item.element.preferredHeight = item.referenceSize.y < 0 ? -1 : item.referenceSize.y * scale;
            }
            foreach (var label in labels)
                if (label.text != null) label.text.fontSize = Mathf.Max(1, Mathf.RoundToInt(label.referenceSize * scale));
        }
    }
}
