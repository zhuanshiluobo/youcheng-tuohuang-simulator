using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>仅执行条目启用。分配文字矩形，正文拟合仍交给条目和正式文档渲染器。</summary>
    public sealed class UiChoiceExecuteTextLayout : VerticalLayoutGroup
    {
        [SerializeField, InspectorName("标题宿主")] private Text title;
        [SerializeField, InspectorName("正文宿主")] private Text description;
        [SerializeField, Min(0), InspectorName("执行文字间距")] private float choiceSpacing;
        [SerializeField, Min(1), InspectorName("标题最大宽度")] private float maximumTitleWidth;
        [SerializeField, Min(1), InspectorName("正文最小宽度")] private float minimumDescriptionWidth;
        [SerializeField, Range(.05f, 1), InspectorName("窄屏正文保留比例")] private float narrowDescriptionShare;
        [SerializeField, InspectorName("执行文字对齐")] private TextAnchor textAlignment;

        private bool captured, executionMode;
        private TextPresentation titlePresentation, descriptionPresentation;
        private int titleBaseSize;
        private string widthKey;
        private float naturalTitleWidth;

        public bool ExecutionMode => executionMode;
        public bool HasTitle => title != null && (title.GetComponent<GameplayTextView>() is GameplayTextView document && document.HasDocument
            ? document.HasVisibleContent : !string.IsNullOrWhiteSpace(title.text));

        public bool TryValidateConfiguration(out string reason)
        {
            bool valid = title != null && description != null &&
                title.transform.parent == transform && description.transform.parent == transform &&
                maximumTitleWidth > 0 && minimumDescriptionWidth > 0 &&
                narrowDescriptionShare > 0 && narrowDescriptionShare <= 1 && choiceSpacing >= 0;
            reason = valid ? string.Empty : "执行文字横排的宿主、原详情布局或宽度边界配置不完整。";
            return valid;
        }

        public void SetExecutionMode(bool active, int baseTitleSize)
        {
            if (!captured)
            {
                if (title == null || description == null) return;
                titlePresentation = new TextPresentation(title);
                descriptionPresentation = new TextPresentation(description);
                captured = true;
            }
            bool changed = executionMode != active || titleBaseSize != baseTitleSize;
            titleBaseSize = baseTitleSize;
            executionMode = active;
            if (active)
            {
                title.horizontalOverflow = HorizontalWrapMode.Overflow;
                description.horizontalOverflow = HorizontalWrapMode.Wrap;
                title.alignment = description.alignment = textAlignment;
                title.GetComponent<GameplayTextView>()?.SetSingleLineLayout(true);
            }
            else
            {
                title.GetComponent<GameplayTextView>()?.SetSingleLineLayout(false);
                titlePresentation.Restore(title);
                descriptionPresentation.Restore(description);
            }
            if (changed) { widthKey = null; SetDirty(); }
        }

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            if (!executionMode) return;
            SetLayoutInputForAxis(padding.horizontal, padding.horizontal + minimumDescriptionWidth +
                (HasTitle ? Mathf.Min(MeasureTitle(), maximumTitleWidth) + choiceSpacing : 0), 1, 0);
        }

        public override void CalculateLayoutInputVertical()
        {
            if (!executionMode) { base.CalculateLayoutInputVertical(); return; }
            // 条目高度属于源资产；不能用换行后的正文高度再次撑开宿主，形成拟合/布局反馈。
            float height = padding.vertical + Mathf.Max(title == null ? 0 : LayoutUtility.GetMinHeight(title.rectTransform),
                description == null ? 0 : LayoutUtility.GetMinHeight(description.rectTransform));
            SetLayoutInputForAxis(height, height, 0, 1);
        }

        public override void SetLayoutHorizontal()
        {
            if (!executionMode) { base.SetLayoutHorizontal(); return; }
            if (title == null || description == null) return;
            float available = Mathf.Max(0, rectTransform.rect.width - padding.horizontal);
            float reserved = Mathf.Min(minimumDescriptionWidth, available * narrowDescriptionShare);
            float gap = HasTitle ? Mathf.Min(choiceSpacing, Mathf.Max(0, available - reserved)) : 0;
            float heading = HasTitle ? Mathf.Min(MeasureTitle(), maximumTitleWidth, Mathf.Max(0, available - reserved - gap)) : 0;
            if (heading <= 0) gap = 0;
            SetChildAlongAxis(title.rectTransform, 0, padding.left, heading);
            SetChildAlongAxis(description.rectTransform, 0, padding.left + heading + gap, Mathf.Max(0, available - heading - gap));
        }

        public override void SetLayoutVertical()
        {
            if (!executionMode) { base.SetLayoutVertical(); return; }
            if (title == null || description == null) return;
            float height = Mathf.Max(0, rectTransform.rect.height - padding.vertical);
            SetChildAlongAxis(title.rectTransform, 1, padding.top, height);
            SetChildAlongAxis(description.rectTransform, 1, padding.top, height);
        }

        private float MeasureTitle()
        {
            if (!HasTitle || titleBaseSize < 1) return 0;
            var document = title.GetComponent<GameplayTextView>();
            string key = (document != null && document.HasDocument ? document.MeasurementCacheKey : title.text) + "|" +
                titleBaseSize + "|" + (title.font == null ? 0 : title.font.GetInstanceID()) + "|" + title.fontStyle + "|" + title.pixelsPerUnit;
            if (key == widthKey) return naturalTitleWidth;
            widthKey = key;
            if (document != null && document.HasDocument) naturalTitleWidth = document.MeasureSingleLineWidthAtFontSize(titleBaseSize);
            else
            {
                var settings = title.GetGenerationSettings(Vector2.zero);
                settings.fontSize = titleBaseSize;
                settings.resizeTextForBestFit = false;
                settings.horizontalOverflow = HorizontalWrapMode.Overflow;
                naturalTitleWidth = title.cachedTextGeneratorForLayout.GetPreferredWidth(title.text, settings) / title.pixelsPerUnit;
            }
            return naturalTitleWidth;
        }

        private struct TextPresentation
        {
            private readonly TextAnchor alignment;
            private readonly HorizontalWrapMode wrap;
            public TextPresentation(Text text) { alignment = text.alignment; wrap = text.horizontalOverflow; }
            public void Restore(Text text) { text.alignment = alignment; text.horizontalOverflow = wrap; }
        }
    }

#if UNITY_EDITOR
    // 原生布局检查器只画原生字段；显示完整序列化配置，避免隐藏横排参数。
    [UnityEditor.CustomEditor(typeof(UiChoiceExecuteTextLayout)), UnityEditor.CanEditMultipleObjects]
    internal sealed class UiChoiceExecuteTextLayoutEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI() => DrawDefaultInspector();
    }
#endif
}
