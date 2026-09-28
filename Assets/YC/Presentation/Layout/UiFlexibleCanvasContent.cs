using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>ScrollRect 内容至少填满 viewport，只在布局首选高度超过空间时增高。</summary>
    public sealed class UiFlexibleCanvasContent : MonoBehaviour, ILayoutSelfController
    {
        [SerializeField] private RectTransform viewport;
        private RectTransform Rect => transform as RectTransform;
        public void SetLayoutHorizontal() { }
        public void SetLayoutVertical()
        {
            if (viewport == null || Rect == null) return;
            Rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                Mathf.Max(viewport.rect.height, LayoutUtility.GetPreferredHeight(Rect)));
        }
#if UNITY_EDITOR
        public void ConfigureForEditor(RectTransform value) { viewport = value; }
#endif
    }
}
