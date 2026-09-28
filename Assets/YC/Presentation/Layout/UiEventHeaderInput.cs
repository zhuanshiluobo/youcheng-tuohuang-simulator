using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>只提供不同事件模式的页头布局输入，原生 VLG 分配实际矩形。</summary>
    public sealed class UiEventHeaderInput : MonoBehaviour
    {
        [System.Serializable]
        public struct HeaderMetrics { public float Top, TitleHeight; }
        [SerializeField] private Text title;
        [SerializeField] private HeaderMetrics[] modes;
        [SerializeField] private float metadataDescriptionGap = 8;
        public void Configure(EventChoiceDialogMode value, bool hasArtwork = false)
        {
            var layout = GetComponent<VerticalLayoutGroup>();
            if (layout == null || modes == null || (int)value >= modes.Length) return;
            var metrics = modes[(int)value];
            layout.padding.top = hasArtwork ? 0 : Mathf.RoundToInt(metrics.Top);
            layout.spacing = hasArtwork ? 0 : metadataDescriptionGap;
            if (title != null)
            {
                var input = title.GetComponent<LayoutElement>();
                if (input != null) input.preferredHeight = input.minHeight = metrics.TitleHeight;
            }
            LayoutRebuilder.MarkLayoutForRebuild((RectTransform)transform);
        }
    }
}
