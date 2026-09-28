using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>仅选择列表/卡牌内容并更新原生网格参数，不写任何子项矩形。</summary>
    public sealed class UiCardCollectionLayout : UIBehaviour, ILayoutSelfController
    {
        [SerializeField] private Vector2 preferredCardSize = new Vector2(200, 300);
        [SerializeField] private float minimumCardWidth = 140f;
        [SerializeField] private GridLayoutGroup cardGrid;
        [SerializeField] private UiAdaptiveGridColumns adaptive;
        private bool listMode = true;
        public int Columns => cardGrid == null ? 1 : cardGrid.constraintCount;
        public RectTransform Content => listMode || cardGrid == null
            ? (RectTransform)transform : (RectTransform)cardGrid.transform;

        public void Configure(Vector2 size, float minimumWidth)
        {
            preferredCardSize = size;
            minimumCardWidth = minimumWidth;
            listMode = false;
            if (cardGrid != null) cardGrid.gameObject.SetActive(true);
            UpdateColumns();
        }
        public void UseList()
        {
            listMode = true;
            if (cardGrid != null) cardGrid.gameObject.SetActive(false);
        }
        protected override void OnRectTransformDimensionsChange()
        { base.OnRectTransformDimensionsChange(); UpdateColumns(); }
        public void SetLayoutHorizontal() => UpdateColumns();
        public void SetLayoutVertical() { }
        private void UpdateColumns()
        {
            if (listMode || cardGrid == null) return;
            if (adaptive != null) adaptive.Configure(preferredCardSize, minimumCardWidth);
        }
    }
}
