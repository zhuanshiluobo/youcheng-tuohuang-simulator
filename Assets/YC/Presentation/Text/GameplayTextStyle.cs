using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using YC.Application.Presentation;
using YC.Domain.Presentation;
using UnityEngine;

namespace YC.Presentation
{
    [Serializable] public sealed class GameplayTextFontBinding
    {
        public string SemanticRole = "Regular", WeightMode = "Inherit";
        public Font Font, MissingGlyphFallback;
    }
    [Serializable] public sealed class GameplayTextKeywordBinding
    { public string Text, SemanticRole = "SpecialWord"; }
    [Serializable] public sealed class GameplayNumberOverlayStyle
    {
        public string Key = "core:score_number_overlay";
        public Rect OverlayRect = new Rect(.22f, .25f, .54f, .56f);
        public Color Ink = Color.white;
        [Range(.1f, 2f)] public float FontSizeRatio = .8f;

        // 输入是本次 Place 已确定的图像尺寸，不查询尚待 Canvas 更新的 RectTransform.rect。
        public Rect BoundsForIconSize(float width, float height) => new Rect(
            width * OverlayRect.x, height * (1 - OverlayRect.y - OverlayRect.height),
            width * OverlayRect.width, height * OverlayRect.height);
    }

    [CreateAssetMenu(fileName = "GameplayTextStyle", menuName = "YC/UI/玩法文字样式")]
    public sealed class GameplayTextStyle : ScriptableObject
    {
        [SerializeField] private UiFontRoles fontRoles;
        [SerializeField] private List<GameplayTextFontBinding> fontBindings = new List<GameplayTextFontBinding>();
        [SerializeField] private List<GameplayTextKeywordBinding> semanticKeywords = new List<GameplayTextKeywordBinding>();
        [SerializeField] private bool useDefaultSemanticKeywords = true;
        [SerializeField] private List<GameplayNumberOverlayStyle> numberOverlays = new List<GameplayNumberOverlayStyle> { new GameplayNumberOverlayStyle() };
        [SerializeField, Min(0)] private float lineSpacing = 2f, iconSpacing = 2f;
        [SerializeField, Min(.1f)] private float iconHeightRatio = 1.15f;
        [SerializeField] private float iconBaselineOffset = 0f;
        private int revision;
        public int Revision => revision;
        public float LineSpacing => lineSpacing;
        public float IconSpacing => iconSpacing;
        public float IconHeightRatio => iconHeightRatio;
        public float IconBaselineOffset => iconBaselineOffset;
        public UiFontRoles FontRoles => fontRoles;
        public int FontConfigurationIdentity
        {
            get
            {
                unchecked
                {
                    int hash = revision;
                    if (fontRoles != null)
                    {
                        hash = hash * 31 + FontId(fontRoles.Regular); hash = hash * 31 + FontId(fontRoles.Emphasis);
                        hash = hash * 31 + FontId(fontRoles.SpecialWord); hash = hash * 31 + FontId(fontRoles.EffectNumber);
                        hash = hash * 31 + FontId(fontRoles.UiNumber); hash = hash * 31 + FontId(fontRoles.MissingGlyphFallback);
                    }
                    foreach (var binding in fontBindings) if (binding != null) { hash = hash * 31 + (binding.Font != null ? binding.Font.GetInstanceID() : 0); hash = hash * 31 + (binding.MissingGlyphFallback != null ? binding.MissingGlyphFallback.GetInstanceID() : 0); }
                    return hash;
                }
            }
        }

        public void Configure(UiFontRoles roles) { fontRoles = roles; NotifyChanged(); }
        public void NotifyChanged() { unchecked { revision++; } }
        private void OnValidate() => NotifyChanged();

        public IReadOnlyDictionary<string, string> GetSemanticKeywords()
        {
            var words = new Dictionary<string, string>(StringComparer.Ordinal);
            if (useDefaultSemanticKeywords) foreach (var pair in GameplayTextResolver.DefaultSemanticKeywords) words.Add(pair.Key, pair.Value);
            foreach (var word in semanticKeywords)
            {
                if (word == null || string.IsNullOrEmpty(word.Text)) throw new InvalidOperationException("语义词表存在空条目。");
                words[word.Text] = word.SemanticRole;
            }
            return new ReadOnlyDictionary<string, string>(words);
        }

        public GameplayTextResolver CreateResolver(IGameplayTextCatalog catalog, string cacheScopeId)
            => new GameplayTextResolver(catalog, GetSemanticKeywords(), cacheScopeId);

        public IReadOnlyList<GameplayTextSemanticKeyword> GetKeywordSnapshot()
        {
            var result = new List<GameplayTextSemanticKeyword>();
            foreach (var pair in GetSemanticKeywords()) result.Add(new GameplayTextSemanticKeyword(pair.Key, pair.Value));
            return result.AsReadOnly();
        }

        public bool TryResolveFont(GameplayTextRunStyle run, Font hostFont, out Font font)
        {
            font = null;
            if (run == null || fontRoles == null || (run.WeightMode != "Inherit" && run.WeightMode != "Normal" && run.WeightMode != "Bold")) return false;
            var binding = Find(run.SemanticRole, run.WeightMode);
            if (binding != null) { font = binding.Font; return font != null; }
            switch (run.SemanticRole)
            {
                case "Regular": font = run.WeightMode == "Bold" ? fontRoles.Emphasis : run.WeightMode == "Normal" ? fontRoles.Regular : hostFont != null ? hostFont : fontRoles.Regular; break;
                case "SpecialWord": font = run.WeightMode == "Bold" ? fontRoles.Emphasis : fontRoles.SpecialWord; break;
                case "EffectNumber": font = run.WeightMode == "Normal" ? fontRoles.UiNumber : fontRoles.EffectNumber; break;
                default: return false;
            }
            return font != null;
        }

        public Font ResolveGlyphFont(GameplayTextRunStyle run, Font resolvedFont, string element)
        {
            if (Contains(resolvedFont, element)) return resolvedFont;
            var binding = Find(run.SemanticRole, run.WeightMode);
            if (binding?.MissingGlyphFallback != null && Contains(binding.MissingGlyphFallback, element)) return binding.MissingGlyphFallback;
            bool bold = run.WeightMode == "Bold" || (run.WeightMode == "Inherit" && fontRoles != null &&
                (resolvedFont == fontRoles.Emphasis || resolvedFont == fontRoles.EffectNumber));
            // 现有常规 fallback 没有粗体许可，绝不拿它冒充加粗。
            if (!bold && fontRoles != null && Contains(fontRoles.MissingGlyphFallback, element)) return fontRoles.MissingGlyphFallback;
            return null;
        }

        public bool TryGetNumberOverlay(string key, out GameplayNumberOverlayStyle overlay)
        {
            overlay = null;
            foreach (var item in numberOverlays) if (item != null && item.Key == key) { overlay = item; return true; }
            return false;
        }
        private GameplayTextFontBinding Find(string role, string weight)
        { foreach (var binding in fontBindings) if (binding != null && binding.SemanticRole == role && binding.WeightMode == weight) return binding; return null; }
        private static int FontId(Font font) => font != null ? font.GetInstanceID() : 0;
        private static bool Contains(Font font, string value)
        {
            if (font == null) return false;
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsSurrogate(value[i]) && !(char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))) return false;
                int scalar = char.ConvertToUtf32(value, i);
                // Unity 2022 UI Font 的公开查询只接受 char；补充平面字形不能伪称已验证。
                if (scalar > char.MaxValue || !font.HasCharacter((char)scalar)) return false;
                if (char.IsHighSurrogate(value[i])) i++;
            }
            return true;
        }
    }
}
