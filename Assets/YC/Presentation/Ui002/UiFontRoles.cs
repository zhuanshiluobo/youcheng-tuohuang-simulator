using UnityEngine;

namespace YC.Presentation
{
    public enum UiFontRole
    {
        Regular,
        Emphasis,
        SpecialWord,
        EffectNumber,
        UiNumber
    }

    [CreateAssetMenu(fileName = "UiFontRoles", menuName = "YC/UI/Font Roles")]
    public sealed class UiFontRoles : ScriptableObject
    {
        [SerializeField] private Font regular;
        [SerializeField] private Font emphasis;
        [SerializeField] private Font specialWord;
        [SerializeField] private Font effectNumber;
        [SerializeField] private Font uiNumber;
        [SerializeField] private Font missingGlyphFallback;

        public Font Regular => regular;
        public Font Emphasis => emphasis;
        public Font SpecialWord => specialWord;
        public Font EffectNumber => effectNumber;
        public Font UiNumber => uiNumber;
        public Font MissingGlyphFallback => missingGlyphFallback;

        public Font Get(UiFontRole role)
        {
            switch (role)
            {
                case UiFontRole.Emphasis: return emphasis;
                case UiFontRole.SpecialWord: return specialWord;
                case UiFontRole.EffectNumber: return effectNumber;
                case UiFontRole.UiNumber: return uiNumber;
                default: return regular;
            }
        }

        public bool TryValidate(out string reason)
        {
            if (regular == null || emphasis == null || specialWord == null ||
                effectNumber == null || uiNumber == null || missingGlyphFallback == null)
            {
                reason = "Five semantic fonts and the missing-glyph fallback must be assigned.";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
