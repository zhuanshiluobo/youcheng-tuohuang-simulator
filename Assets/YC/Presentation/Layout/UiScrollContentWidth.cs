using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>横向只在内容最小可读宽度超过 viewport 时增宽，由 ScrollRect 提供访问。</summary>
    public sealed class UiScrollContentWidth : MonoBehaviour, ILayoutSelfController
    {
        [SerializeField] private RectTransform viewport;
        public void SetLayoutHorizontal()
        {
            var rect = transform as RectTransform;
            if (rect == null || viewport == null) return;
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,
                Mathf.Max(viewport.rect.width, LayoutUtility.GetMinWidth(rect)));
        }
        public void SetLayoutVertical() { }
#if UNITY_EDITOR
        public void ConfigureForEditor(RectTransform value) { viewport = value; }
#endif
    }
}
