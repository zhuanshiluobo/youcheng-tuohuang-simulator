using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>提供表格内容高度输入；原生 VLG 在行的最小与首选高度之间分配空间。</summary>
    public sealed class InformationTableHeight : MonoBehaviour, ILayoutElement
    {
        [SerializeField] private VerticalLayoutGroup rows;
        [SerializeField] private RectTransform viewport;
        private Vector2 lastSize;
        public float minWidth => -1;
        public float preferredWidth => -1;
        public float flexibleWidth => -1;
        public float minHeight => rows == null ? 0 : rows.minHeight;
        public float preferredHeight => rows == null || viewport == null ? 0 :
            Mathf.Max(rows.minHeight, Mathf.Min(rows.preferredHeight, viewport.rect.height));
        public float flexibleHeight => -1;
        public int layoutPriority => 1;
        public void CalculateLayoutInputHorizontal() { }
        public void CalculateLayoutInputVertical() { if(rows != null) rows.CalculateLayoutInputVertical(); }
        private void LateUpdate()
        {
            if(viewport == null || viewport.rect.size == lastSize) return;
            lastSize = viewport.rect.size;
            LayoutRebuilder.MarkLayoutForRebuild((RectTransform)transform);
        }
    }
}
