using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using YC.Domain.State;

namespace YC.Domain.Presentation
{
    // 这些 DTO 只保存内容描述；没有 Unity、Lua、布局或可执行函数引用。
    [Serializable] public sealed class GameplayTextRef
    {
        public string Key = string.Empty;
        public List<GameplayTextArgument> Arguments = new List<GameplayTextArgument>();
        public GameplayTextRef() { }
        public GameplayTextRef(string key, params GameplayTextArgument[] arguments)
        { Key = key ?? string.Empty; Arguments = new List<GameplayTextArgument>(arguments ?? Array.Empty<GameplayTextArgument>()); }
        public GameplayTextRef Clone() => GameplayPresentationNormalizer.Clone(this);
    }
    [Serializable] public sealed class GameplayTextArgument
    {
        public string Name = string.Empty;
        public string Kind = string.Empty; // integer / text / player / item / definition / resource / location
        public NormalizedValue Value = NormalizedValue.CreateNull();
    }
    [Serializable] public sealed class GameplayOptionPresentation
    {
        public string CandidateId = string.Empty;
        public GameplayTextRef Label, Description, DisabledReason;
        public string IconKey = string.Empty;
    }
    [Serializable] public sealed class GameplayQuantityRowPresentation
    {
        public string RowId = string.Empty;
        public GameplayTextRef Label, Description, Unit;
        public string IconKey = string.Empty;
    }
    [Serializable] public sealed class GameplayInteractionPresentation
    {
        public GameplayTextRef Prompt, Progress;
        public List<GameplayOptionPresentation> Options = new List<GameplayOptionPresentation>();
        public List<GameplayQuantityRowPresentation> QuantityRows = new List<GameplayQuantityRowPresentation>();
    }
    [Serializable] public sealed class EffectPresentationDefinition
    {
        public GameplayTextRef Started, Waiting, Succeeded, Failed, Stopped;
        public GameplayInteractionPresentation Interaction;
        public string PresentationScript = string.Empty;
    }
    [Serializable] public sealed class ContentPresentationDefinition
    {
        public GameplayTextRef Name, Description;
        public string DetailScript = string.Empty;
    }
    [Serializable] public sealed class AbilityPresentationDefinition
    {
        public GameplayTextRef Description;
        public EffectPresentationDefinition Effect;
    }
    [Serializable] public sealed class SettlementPresentationDefinition
    {
        public GameplayTextRef Label, Description, AriaLabel;
        public string IconKey = string.Empty;
        public string ContentMode = "text"; // text / icon_text / icon
    }
    [Serializable] public sealed class GameplayPresentationContext
    {
        public string Purpose = string.Empty;
        public string SourceDefinitionId = string.Empty, SourceItemId = string.Empty, AbilityId = string.Empty;
        public string EffectId = string.Empty, InteractionId = string.Empty;
        public int ViewerPlayerId = -1, StateRevision;
        // 必须由调用者按用途和查看者预先授权。未提供的事实保持缺席。
        public NormalizedValue Facts = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
    }
    [Serializable] public sealed class GameplayPresentationResult
    {
        public GameplayTextRef Resolution;
        public GameplayInteractionPresentation Interaction;
        public List<GameplayTextRef> Details = new List<GameplayTextRef>();
    }
    [Serializable] public sealed class ResolutionMessageView
    {
        public string OwnerEffectId = string.Empty, InteractionId = string.Empty;
        public int ViewerPlayerId = -1, StateRevision;
        public GameplayTextRef Message;
    }
    [Serializable] public sealed class GameplayTextParameterDefinition
    {
        public string Name = string.Empty, Kind = string.Empty;
    }
    [Serializable] public sealed class GameplayTextTemplateDefinition
    {
        public string Key = string.Empty, Template = string.Empty;
        public List<GameplayTextParameterDefinition> Parameters = new List<GameplayTextParameterDefinition>();
    }
    [Serializable] public sealed class GameplayIconDefinition
    {
        public string Id = string.Empty, Usage = string.Empty;
        public GameplayTextRef AltText;
        public bool Monochrome;
        public string AssetKey = string.Empty, Dark = string.Empty, Light = string.Empty, Muted = string.Empty;
        public string RenderMode = "sprite";
        public bool ValueRequired;
        public string ValueAltTextKey = string.Empty, NumberOverlayStyleKey = string.Empty;
    }
    [Serializable] public sealed class GameplayFontBindingDefinition
    {
        public string SemanticRole = "Regular", WeightMode = "Inherit";
        public string FontAssetKey = string.Empty, MissingGlyphFallbackAssetKey = string.Empty;
    }
    public interface IGameplayTextCatalog
    {
        string ContentHash { get; }
        string Locale { get; }
        bool TryGetTemplate(string key, out GameplayTextTemplateDefinition definition);
        bool TryGetIcon(string key, out GameplayIconDefinition definition);
    }

    // 仅允许从查看者可见投影生成这些显示名。解析器禁止回读 Host 全局 ItemRegistry。
    public sealed class GameplayTextContext
    {
        public string VisibilityScopeId = string.Empty;
        public IReadOnlyList<GameplayTextSemanticKeyword> SemanticKeywords = Array.Empty<GameplayTextSemanticKeyword>();
        public string ContentHash = string.Empty, Locale = "zh-CN", SemanticKeywordsVersion = string.Empty;
        public int ViewerPlayerId = -1, StateRevision;
        public IGameplayTextCatalog Catalog;
        public IReadOnlyDictionary<string, string> PlayerNames = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        public IReadOnlyDictionary<string, string> ItemNames = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        public IReadOnlyDictionary<string, string> DefinitionNames = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        public IReadOnlyDictionary<string, string> ResourceNames = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        public IReadOnlyDictionary<string, string> LocationNames = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        public IReadOnlyDictionary<string, string> ResourceIconKeys = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    }
    public sealed class GameplayTextSemanticKeyword
    {
        public string Text { get; }
        public string SemanticRole { get; }
        public GameplayTextSemanticKeyword(string text, string semanticRole = "SpecialWord") { Text = text ?? string.Empty; SemanticRole = semanticRole ?? "SpecialWord"; }
    }
    public interface IGameplayTextCatalogSnapshot : IGameplayTextCatalog
    {
        IReadOnlyList<GameplayTextTemplateDefinition> Texts { get; }
    }

    // ColorRgba: 0xRRGGBBAA；null 沿宿主颜色。样式不可变，关闭标签前已折叠作用域。
    public sealed class GameplayTextRunStyle : IEquatable<GameplayTextRunStyle>
    {
        public string SemanticRole { get; }
        public string WeightMode { get; }
        public uint? ColorRgba { get; }
        public GameplayTextRunStyle(string semanticRole = "Regular", string weightMode = "Inherit", uint? colorRgba = null)
        { SemanticRole = semanticRole ?? "Regular"; WeightMode = weightMode ?? "Inherit"; ColorRgba = colorRgba; }
        public bool Equals(GameplayTextRunStyle other) => other != null && SemanticRole == other.SemanticRole && WeightMode == other.WeightMode && ColorRgba == other.ColorRgba;
        public override bool Equals(object other) => Equals(other as GameplayTextRunStyle);
        public override int GetHashCode() => SemanticRole.GetHashCode() ^ WeightMode.GetHashCode() ^ ColorRgba.GetHashCode();
    }
    public abstract class GameplayTextRun
    {
        public GameplayTextRunStyle Style { get; }
        protected GameplayTextRun(GameplayTextRunStyle style) { Style = style ?? new GameplayTextRunStyle(); }
        public abstract string ToPlainText();
    }
    public sealed class TextRun : GameplayTextRun
    {
        public string Text { get; }
        public TextRun(string text, GameplayTextRunStyle style = null) : base(style) { Text = text ?? string.Empty; }
        public override string ToPlainText() => Text;
    }
    public sealed class NumberRun : GameplayTextRun
    {
        public long Value { get; }
        public string Text { get; }
        public NumberRun(long value, GameplayTextRunStyle style = null) : base(style ?? new GameplayTextRunStyle("EffectNumber"))
        { Value = value; Text = value.ToString(CultureInfo.InvariantCulture); }
        public override string ToPlainText() => Text;
    }
    public sealed class IconRun : GameplayTextRun
    {
        public string IconKey { get; }
        public string AltText { get; }
        public int? NumberValue { get; }
        public GameplayTextRunStyle OverlayTextStyle { get; }
        public IconRun(string iconKey, string altText, int? numberValue = null, GameplayTextRunStyle style = null, GameplayTextRunStyle overlayTextStyle = null) : base(style)
        { IconKey = iconKey ?? string.Empty; AltText = altText ?? string.Empty; NumberValue = numberValue; OverlayTextStyle = overlayTextStyle ?? new GameplayTextRunStyle("EffectNumber"); }
        public override string ToPlainText() => AltText;
    }
    public sealed class LineBreak : GameplayTextRun
    {
        public LineBreak() : base(null) { }
        public override string ToPlainText() => "\n";
    }
    public sealed class GameplayTextDocument
    {
        public IReadOnlyList<GameplayTextRun> Runs { get; }
        public string Identity { get; }
        public string MeasurementIdentity { get; }
        public GameplayTextDocument(IEnumerable<GameplayTextRun> runs, string identity = "", string measurementIdentity = "")
        {
            var copy = new List<GameplayTextRun>(runs ?? Array.Empty<GameplayTextRun>());
            if (copy.Exists(run => run == null)) throw new ArgumentException("文档不能包含空文本段。", nameof(runs));
            Runs = copy.AsReadOnly(); Identity = identity ?? string.Empty; MeasurementIdentity = measurementIdentity ?? Identity;
        }
        public string ToPlainText() { var text = new StringBuilder(); foreach (var run in Runs) text.Append(run.ToPlainText()); return text.ToString(); }
    }
}
