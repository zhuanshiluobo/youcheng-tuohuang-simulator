using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>只按 Token 原图宽高比决定分行和布局输入；行与条目矩形交给原生 VLG／HLG。</summary>
    public sealed class InformationTokenRows : MonoBehaviour
    {
        [SerializeField] private ScrollRect scroll;
        [SerializeField] private RectTransform rowTemplate;
        [SerializeField] private float normalWidth = 168, wideWidth = 228, rowHeight = 126, gap = 14;
        private readonly List<RectTransform> rows = new List<RectTransform>();
        private IReadOnlyList<GameBoxTokenItemView> items;
        private float lastWidth = -1;
        public bool IsConfigured => scroll != null && scroll.content != null && scroll.viewport != null && rowTemplate != null &&
            rowTemplate.GetComponent<HorizontalLayoutGroup>() != null &&
            scroll.content.GetComponent<VerticalLayoutGroup>() != null &&
            scroll.content.GetComponent<ContentSizeFitter>() != null;

        public void SetItems(IReadOnlyList<GameBoxTokenItemView> value)
        { items = value; Reflow(); }

        private void OnEnable() => lastWidth = -1;
        private void LateUpdate()
        {
            if (items != null && scroll.viewport.rect.width != lastWidth) Reflow();
        }

        private void Reflow()
        {
            if (!IsConfigured || items == null) return;
            var width = Mathf.Max(1, scroll.viewport.rect.width);
            lastWidth = width;
            var position = scroll.verticalNormalizedPosition;
            var rowIndex = -1;
            var usedWidth = 0f;
            foreach (var item in items)
            {
                if (!item.gameObject.activeSelf) continue;
                var sprite = item.Artwork;
                var preferred = Mathf.Min(width, sprite != null && sprite.rect.height > 0 &&
                    sprite.rect.width / sprite.rect.height > 2.5f ? wideWidth : normalWidth);
                if (rowIndex < 0 || usedWidth + gap + preferred > width)
                {
                    rowIndex++; usedWidth = 0;
                    while (rows.Count <= rowIndex) rows.Add(Instantiate(rowTemplate, scroll.content, false));
                    rows[rowIndex].gameObject.SetActive(true);
                    rows[rowIndex].SetSiblingIndex(rowIndex);
                }
                item.transform.SetParent(rows[rowIndex], false);
                item.transform.SetAsLastSibling();
                var input = item.GetComponent<LayoutElement>();
                input.preferredWidth = preferred;
                input.preferredHeight = rowHeight;
                usedWidth += (usedWidth > 0 ? gap : 0) + preferred;
            }
            rowTemplate.gameObject.SetActive(false);
            for (var i = rowIndex + 1; i < rows.Count; i++) rows[i].gameObject.SetActive(false);
            LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
            scroll.verticalNormalizedPosition = position;
        }
    }
}
