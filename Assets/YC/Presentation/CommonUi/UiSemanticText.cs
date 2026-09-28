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
        private Text label;

        public UiFontRole Role => role;
        public float BaselineOffset => baselineOffset;

        private void Awake()
        {
            ApplyRole();
        }

        private void OnEnable()
        {
            ApplyRole();
        }

        public void SetValue(string value)
        {
            if (label == null) label = GetComponent<Text>();
            label.text = value ?? string.Empty;
            ApplyRole();
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
