using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>动态正文换行参与所在行的首选高度；不会修改正文内容或字体。</summary>
    public sealed class UiMeasuredTextRow : UIBehaviour, ILayoutElement
    {
        [SerializeField] private Text[] texts = new Text[0];
        [SerializeField] private float minimumRowHeight;
        [SerializeField] private float verticalPadding;
        public void CalculateLayoutInputHorizontal() { }
        public void CalculateLayoutInputVertical() { }
        public float minWidth => -1;
        public float preferredWidth => -1;
        public float flexibleWidth => -1;
        public float minHeight => ConfiguredMinimum;
        public float preferredHeight
        {
            get
            {
                var height = ConfiguredMinimum;
                foreach (var text in texts)
                    if (text != null && text.gameObject.activeSelf)
                        height = Mathf.Max(height, text.preferredHeight + verticalPadding);
                return height;
            }
        }
        public float flexibleHeight => -1;
        public int layoutPriority => 2;
        private float ConfiguredMinimum
        {
            get
            {
                var element = GetComponent<LayoutElement>();
                return element == null ? minimumRowHeight :
                    Mathf.Max(minimumRowHeight, element.minHeight, element.preferredHeight);
            }
        }
        protected override void OnEnable()
        {
            base.OnEnable();
            foreach (var text in texts) if (text != null) text.RegisterDirtyLayoutCallback(MarkDirty);
            MarkDirty();
        }
        protected override void OnDisable()
        {
            foreach (var text in texts) if (text != null) text.UnregisterDirtyLayoutCallback(MarkDirty);
            base.OnDisable();
        }
        private void MarkDirty()
        {
            if (isActiveAndEnabled) LayoutRebuilder.MarkLayoutForRebuild(transform as RectTransform);
        }
#if UNITY_EDITOR
        public void ConfigureForEditor(Text[] rowTexts, float initialHeight)
        { texts = rowTexts; minimumRowHeight = initialHeight; MarkDirty(); }
#endif
    }
}
