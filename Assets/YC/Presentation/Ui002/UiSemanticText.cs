using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    [RequireComponent(typeof(Text))]
    public sealed class UiSemanticText : MonoBehaviour
    {
        [SerializeField] private UiFontRoles fonts;
        [SerializeField] private UiFontRole role;
        [SerializeField] private FontStyle style = FontStyle.Normal;
        [SerializeField] private float baselineOffset;
        [SerializeField, Tooltip("开启后按语义字体配置覆盖 Text；默认保留预制体手动设置。")] private bool applyRoleAtRuntime;
        private Text label;

        public UiFontRole Role => role;
        public float BaselineOffset => baselineOffset;

        private void Awake()
        {
            if (applyRoleAtRuntime) ApplyRole();
        }

        private void OnEnable()
        {
            if (applyRoleAtRuntime) ApplyRole();
        }

        public void SetValue(string value)
        {
            if (label == null) label = GetComponent<Text>();
            label.text = value ?? string.Empty;
            if (applyRoleAtRuntime) ApplyRole();
        }

        public void ApplyRole()
        {
            if (label == null) label = GetComponent<Text>();
            if (fonts == null) return;
            var font = fonts.Get(role);
            if (font != null && fonts.MissingGlyphFallback != null)
            {
                for (var i = 0; i < label.text.Length; i++)
                {
                    var glyph = label.text[i];
                    if (glyph == '\n' || glyph == '\r' || glyph == '\t') continue;
                    if (!font.HasCharacter(glyph))
                    {
                        font = fonts.MissingGlyphFallback;
                        break;
                    }
                }
            }
            if (font != null) label.font = font;
            label.fontStyle = style;
        }
    }
}
