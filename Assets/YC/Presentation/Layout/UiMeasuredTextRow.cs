using System.Collections.Generic;
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
        private readonly List<RectTransform> measurementRoots = new List<RectTransform>();
        private readonly List<Text> observedTexts = new List<Text>();
        private readonly List<GameplayTextView> observedDocuments = new List<GameplayTextView>();
        private bool layoutMarkPending;
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
                foreach (var root in measurementRoots)
                {
                    if (root == null || !root.gameObject.activeInHierarchy) continue;
                    var text = root.GetComponent<Text>();
                    float contentHeight;
                    if (text != null)
                    {
                        var document = root.GetComponent<GameplayTextView>();
                        if (document == null || !document.TryGetPreferredLayoutHeight(out contentHeight))
                            contentHeight = text.preferredHeight;
                        contentHeight += verticalPadding;
                    }
                    // 只读严格后代的原布局组：横排取最高子项，纵排沿其实际间距/边距累加。
                    // 组内已有实际边距，不再叠加单文本框的verticalPadding补偿。
                    // 不向自身LayoutUtility求高度，也不在这里另算一套布局几何。
                    else contentHeight = LayoutUtility.GetPreferredHeight(root);
                    height = Mathf.Max(height, contentHeight);
                }
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
            ObserveLayoutInputs();
            MarkDirty();
        }
        protected override void OnDisable()
        {
            StopObservingLayoutInputs();
            layoutMarkPending = false;
            base.OnDisable();
        }
        private void ObserveLayoutInputs()
        {
            StopObservingLayoutInputs();
            if (texts == null) return;
            foreach (var text in texts)
            {
                if (text == null || text.transform == transform || !text.transform.IsChildOf(transform)) continue;
                var root = text.rectTransform;
                for (var parent = root.parent; parent != null && parent != transform; parent = parent.parent)
                {
                    var group = parent.GetComponent<HorizontalOrVerticalLayoutGroup>();
                    if (group != null && group.isActiveAndEnabled) root = (RectTransform)parent;
                }
                if (measurementRoots.Contains(root)) continue;
                measurementRoots.Add(root);
                ObserveTextHosts(root);
            }
        }
        private void ObserveTextHosts(Transform node)
        {
            var text = node.GetComponent<Text>();
            if (text != null)
            {
                if (observedTexts.Contains(text)) return;
                observedTexts.Add(text); text.RegisterDirtyLayoutCallback(MarkDirty);
                var document = node.GetComponent<GameplayTextView>();
                if (document != null)
                {
                    observedDocuments.Add(document); document.LayoutInputChanged += MarkDirty;
                }
                // 原Text之下是可视池，不是另一份行高来源。
                return;
            }
            foreach (Transform child in node) ObserveTextHosts(child);
        }
        private void StopObservingLayoutInputs()
        {
            foreach (var text in observedTexts) if (text != null) text.UnregisterDirtyLayoutCallback(MarkDirty);
            foreach (var document in observedDocuments) if (document != null) document.LayoutInputChanged -= MarkDirty;
            observedTexts.Clear(); observedDocuments.Clear(); measurementRoots.Clear();
        }
        private void LateUpdate() => FlushLayoutRequest();
        protected override void OnRectTransformDimensionsChange() { base.OnRectTransformDimensionsChange(); MarkDirty(); }
        private void OnTransformChildrenChanged()
        {
            if (!isActiveAndEnabled) return;
            ObserveLayoutInputs(); MarkDirty();
        }
        private void MarkDirty()
        {
            if (!isActiveAndEnabled) return;
            layoutMarkPending = true;
            FlushLayoutRequest();
        }
        private void FlushLayoutRequest()
        {
            if (!layoutMarkPending || !isActiveAndEnabled || CanvasUpdateRegistry.IsRebuildingLayout() ||
                CanvasUpdateRegistry.IsRebuildingGraphics()) return;
            layoutMarkPending = false;
            LayoutRebuilder.MarkLayoutForRebuild(transform as RectTransform);
        }
#if UNITY_EDITOR
        public void ConfigureForEditor(Text[] rowTexts, float initialHeight)
        { texts = rowTexts; minimumRowHeight = initialHeight; if (isActiveAndEnabled) ObserveLayoutInputs(); MarkDirty(); }
#endif
    }
}
