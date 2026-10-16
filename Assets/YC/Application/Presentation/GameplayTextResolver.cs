using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using YC.Domain.Presentation;
using YC.Domain.State;

namespace YC.Application.Presentation
{
    public sealed class GameplayTextFormatException : FormatException
    {
        public string TextKey { get; }
        public int Position { get; }
        public GameplayTextFormatException(string key, int position, string reason)
            : base("玩法文案 " + key + "，字符 " + position + "：" + reason)
        { TextKey = key; Position = position; }
    }

    /// <summary>唯一模板解释器。只读已授权显示数据，不调用 Lua 或规则服务。</summary>
    public sealed class GameplayTextResolver
    {
        private readonly IGameplayTextCatalog catalog;
        private readonly string scope;
        private readonly KeyValuePair<string, string>[] keywords;
        private readonly string keywordIdentity;
        private readonly Cache<CompiledTemplate> templates;
        private readonly Cache<GameplayTextDocument> documents;
        private readonly Cache<GameplayTextResolver> profiles = new Cache<GameplayTextResolver>(8);
        private readonly Cache<bool> diagnostics = new Cache<bool>(128);
        public event Action<GameplayTextFormatException> Diagnostic;
        public int CompiledTemplateCount { get; private set; }
        public int ResolvedDocumentCount { get; private set; }

        public GameplayTextResolver(IGameplayTextCatalog catalog,
            IReadOnlyDictionary<string, string> semanticKeywords = null, string cacheScopeId = "", int cacheCapacity = 256)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            scope = cacheScopeId ?? "";
            var words = new List<KeyValuePair<string, string>>();
            var source = semanticKeywords ?? DefaultSemanticKeywords;
            foreach (var pair in source)
            {
                if (string.IsNullOrEmpty(pair.Key) || !ValidRole(pair.Value))
                    throw new ArgumentException("语义词表包含空词或未知字体角色。", nameof(semanticKeywords));
                words.Add(pair);
            }
            words.Sort((a, b) => a.Key.Length != b.Key.Length ? b.Key.Length.CompareTo(a.Key.Length) : StringComparer.Ordinal.Compare(a.Key, b.Key));
            keywords = words.ToArray();
            var identity = new StringBuilder();
            foreach (var word in words) { Field(identity, word.Key); Field(identity, word.Value); }
            keywordIdentity = Hash(identity.ToString());
            templates = new Cache<CompiledTemplate>(cacheCapacity);
            documents = new Cache<GameplayTextDocument>(cacheCapacity);
        }

        public static IReadOnlyDictionary<string, string> DefaultSemanticKeywords { get; } =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
            { { "策略", "SpecialWord" }, { "计谋", "SpecialWord" } });

        // 没有快照枚举的目录由既有组合层提供已登记 key，加载阶段统一报语法错误。
        public void Precompile(IEnumerable<string> textKeys)
        {
            if (textKeys == null) throw new ArgumentNullException(nameof(textKeys));
            foreach (var key in textKeys) Compile(key);
        }

        public void Precompile()
        {
            var snapshot = catalog as IGameplayTextCatalogSnapshot;
            if (snapshot == null) throw new InvalidOperationException("目录不提供快照枚举，请传入已登记的模板 key 集合。");
            foreach (var definition in snapshot.Texts) Compile(definition.Key);
        }

        public void ClearCache() { templates.Clear(); documents.Clear(); profiles.Clear(); diagnostics.Clear(); }

        public GameplayTextDocument Resolve(GameplayTextRef reference, GameplayTextContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.Catalog != null && !ReferenceEquals(context.Catalog, catalog))
                throw new InvalidOperationException("文案上下文必须使用同一只读目录实例。");
            if ((!string.IsNullOrEmpty(context.ContentHash) && context.ContentHash != catalog.ContentHash) ||
                (!string.IsNullOrEmpty(context.Locale) && context.Locale != catalog.Locale))
                throw new InvalidOperationException("文案上下文内容版本或语言不匹配。");
            GameplayTextResolver resolver = this;
            if (context.SemanticKeywords != null && context.SemanticKeywords.Count > 0)
            {
                var words = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var word in context.SemanticKeywords)
                {
                    if (word == null || string.IsNullOrEmpty(word.Text) || !ValidRole(word.SemanticRole) || words.ContainsKey(word.Text))
                        throw new ArgumentException("上下文语义词表为空、重名或角色无效。");
                    words.Add(word.Text, word.SemanticRole);
                }
                bool same = words.Count == keywords.Length;
                foreach (var word in keywords) { string role; if (!words.TryGetValue(word.Key, out role) || role != word.Value) { same = false; break; } }
                if (!same)
                {
                    var names = new List<string>(words.Keys); names.Sort(StringComparer.Ordinal); var key = new StringBuilder();
                    foreach (var name in names) { Field(key, name); Field(key, words[name]); }
                    string profileKey = Hash(key.ToString());
                    if (!profiles.TryGet(profileKey, out resolver)) { resolver = new GameplayTextResolver(catalog, words, scope); profiles.Add(profileKey, resolver); }
                }
            }
            try { return resolver.ResolveCore(reference, context, 0); }
            catch (GameplayTextFormatException error)
            {
                Report(error);
                if (reference?.Key == "core:presentation.unavailable") throw;
                return resolver.ResolveCore(new GameplayTextRef("core:presentation.unavailable"), context, 0);
            }
        }

        private GameplayTextDocument ResolveCore(GameplayTextRef reference, GameplayTextContext context, int depth)
        {
            if (depth > 8) throw Error(reference?.Key, 0, "替代文本引用循环或过深。");
            if (reference == null || string.IsNullOrEmpty(reference.Key)) throw Error(reference?.Key, 0, "引用 key 为空。");
            var template = Compile(reference.Key);
            var args = Arguments(reference, template);
            var cacheKey = new StringBuilder();
            Field(cacheKey, catalog.ContentHash); Field(cacheKey, catalog.Locale); Field(cacheKey, scope); Field(cacheKey, context.VisibilityScopeId);
            Field(cacheKey, context.ViewerPlayerId.ToString(CultureInfo.InvariantCulture));
            Field(cacheKey, context.SemanticKeywordsVersion); Field(cacheKey, keywordIdentity); Field(cacheKey, reference.Key);
            // 只查询本条文案实际使用的授权名字，不扫描整局实体或全部名称字典。
            foreach (var parameter in template.Parameters)
            {
                var arg = args[parameter.Key];
                Field(cacheKey, parameter.Key); Field(cacheKey, arg.Value.ToDeterministicString());
                if (arg.Kind != "integer" && arg.Kind != "text")
                {
                    Field(cacheKey, Name(arg, context));
                    if (arg.Kind == "resource") Field(cacheKey, Lookup(context.ResourceIconKeys, arg.Value.ReferenceId));
                }
            }
            var identity = Hash(cacheKey.ToString());
            GameplayTextDocument previous;
            if (documents.TryGet(identity, out previous)) return previous;
            var runs = new List<GameplayTextRun>();
            foreach (var token in template.Tokens)
            {
                switch (token.Kind)
                {
                    case TokenKind.Text: AddText(runs, token.Text, token.Style); break;
                    case TokenKind.Number: runs.Add(new NumberRun(token.Number, token.Style)); break;
                    case TokenKind.Break: runs.Add(new LineBreak()); break;
                    case TokenKind.Parameter:
                        var arg = args[token.Text];
                        if (arg.Kind == "integer")
                            runs.Add(new NumberRun(arg.Value.IntegerValue, Role(token.Style, "EffectNumber")));
                        else if (arg.Kind == "text") AddText(runs, arg.Value.StringValue, token.Style);
                        else
                        {
                            var iconKey = arg.Kind == "resource" ? Lookup(context.ResourceIconKeys, arg.Value.ReferenceId) : null;
                            if (!string.IsNullOrEmpty(iconKey)) AddIcon(runs, iconKey, null, token.Style, context, depth);
                            else
                            {
                                var name = Name(arg, context);
                                if (name == null) name = ResolveCore(new GameplayTextRef("core:presentation.unknown_entity"), context, depth + 1).ToPlainText();
                                AddText(runs, name, token.Style);
                            }
                        }
                        break;
                    case TokenKind.Icon:
                        int? value = token.HasNumber ? (int?)checked((int)token.Number) : null;
                        if (token.ValueParameter != null) value = checked((int)args[token.ValueParameter].Value.IntegerValue);
                        AddIcon(runs, token.Text, value, token.Style, context, depth);
                        break;
                }
            }
            // 测量身份保留查看者隔离，但忽略颜色；不会拿常规字宽绘制粗体。
            var measure = new StringBuilder();
            Field(measure, catalog.ContentHash); Field(measure, scope); Field(measure, context.VisibilityScopeId); Field(measure, context.ViewerPlayerId.ToString(CultureInfo.InvariantCulture));
            foreach (var run in runs)
            {
                Field(measure, run.GetType().Name); Field(measure, run.Style.SemanticRole); Field(measure, run.Style.WeightMode);
                Field(measure, run.ToPlainText());
                var icon = run as IconRun;
                if (icon != null) { Field(measure, icon.IconKey); Field(measure, icon.NumberValue?.ToString(CultureInfo.InvariantCulture)); Field(measure, icon.OverlayTextStyle.WeightMode); }
            }
            var document = new GameplayTextDocument(runs, identity, Hash(measure.ToString()));
            documents.Add(identity, document); ResolvedDocumentCount++;
            return document;
        }

        private void AddIcon(List<GameplayTextRun> runs, string key, int? value, GameplayTextRunStyle style, GameplayTextContext context, int depth)
        {
            GameplayIconDefinition icon;
            if (!catalog.TryGetIcon(key, out icon) || icon == null || icon.Usage != "inline_rule") throw Error(key, 0, "未登记正文图标用途。");
            if (icon.ValueRequired && !value.HasValue) throw Error(key, 0, "图标要求数值，不能默认为零。");
            if (value.HasValue && icon.RenderMode != "number_overlay") throw Error(key, 0, "普通图标不接受叠数。");
            GameplayTextRef alt = icon.AltText;
            if (value.HasValue)
                alt = new GameplayTextRef(icon.ValueAltTextKey, new GameplayTextArgument
                { Name = "value", Kind = "integer", Value = NormalizedValue.CreateInteger(value.Value) });
            var text = ResolveCore(alt, context, depth + 1).ToPlainText();
            runs.Add(new IconRun(key, text, value, style, Role(style, "EffectNumber")));
        }

        private static Dictionary<string, GameplayTextArgument> Arguments(GameplayTextRef reference, CompiledTemplate template)
        {
            var result = new Dictionary<string, GameplayTextArgument>(StringComparer.Ordinal);
            foreach (var arg in reference.Arguments ?? new List<GameplayTextArgument>())
            {
                string kind, reason;
                if (arg == null || string.IsNullOrEmpty(arg.Name) || !template.Parameters.TryGetValue(arg.Name, out kind) ||
                    kind != arg.Kind || result.ContainsKey(arg.Name) || arg.Value == null || !arg.Value.TryValidate(out reason))
                    throw Error(reference.Key, 0, "参数缺失、重名、类型或归一化值无效。");
                bool valid = kind == "integer" ? arg.Value.Kind == NormalizedValueKind.Integer :
                    kind == "text" ? arg.Value.Kind == NormalizedValueKind.String :
                    arg.Value.Kind == NormalizedValueKind.StableReference && arg.Value.ReferenceType == kind;
                if (!valid) throw Error(reference.Key, 0, "参数值与声明类型不匹配。");
                result.Add(arg.Name, arg);
            }
            if (result.Count != template.Parameters.Count) throw Error(reference.Key, 0, "模板参数未完整提供。");
            return result;
        }

        private CompiledTemplate Compile(string key)
        {
            var cacheKey = catalog.ContentHash + "\n" + catalog.Locale + "\n" + key;
            CompiledTemplate cached;
            if (templates.TryGet(cacheKey, out cached)) return cached;
            GameplayTextTemplateDefinition definition;
            if (string.IsNullOrEmpty(key) || !catalog.TryGetTemplate(key, out definition) || definition == null || definition.Template == null)
                throw Error(key, 0, "未登记模板。");
            var template = new CompiledTemplate();
            foreach (var parameter in definition.Parameters ?? new List<GameplayTextParameterDefinition>())
            {
                if (parameter == null || string.IsNullOrEmpty(parameter.Name) || template.Parameters.ContainsKey(parameter.Name) || !ValidKind(parameter.Kind))
                    throw Error(key, 0, "模板参数声明无效。");
                template.Parameters.Add(parameter.Name, parameter.Kind);
            }
            var styles = new Stack<KeyValuePair<string, GameplayTextRunStyle>>();
            var current = new GameplayTextRunStyle();
            string text = definition.Template;
            var literal = new StringBuilder();
            Action flush = () => { EmitLiteral(template.Tokens, literal.ToString(), current, key); literal.Clear(); };
            for (int i = 0; i < text.Length;)
            {
                char ch = text[i];
                if (i + 1 < text.Length && (ch == '{' || ch == '}' || ch == '[' || ch == ']') && text[i + 1] == ch)
                { literal.Append(ch); i += 2; continue; }
                if (ch == '\r' || ch == '\n')
                { flush(); template.Tokens.Add(new Token { Kind = TokenKind.Break }); i += ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1; continue; }
                if (ch == '{')
                {
                    flush(); int end = text.IndexOf('}', i + 1);
                    if (end < 0) throw Error(key, i, "参数缺少闭合花括号。");
                    string name = text.Substring(i + 1, end - i - 1);
                    if (!template.Parameters.ContainsKey(name)) throw Error(key, i, "占位符没有类型声明。");
                    template.Tokens.Add(new Token { Kind = TokenKind.Parameter, Text = name, Style = current }); i = end + 1; continue;
                }
                if (ch == '}') throw Error(key, i, "字面花括号必须转义。");
                if (ch == '[')
                {
                    int end = text.IndexOf(']', i + 1);
                    string tag = end < 0 ? text.Substring(i + 1) : text.Substring(i + 1, end - i - 1);
                    bool known = tag == "b" || tag == "normal" || tag == "/b" || tag == "/normal" || tag == "/color" ||
                        tag.StartsWith("color:", StringComparison.Ordinal) || tag.StartsWith("icon:", StringComparison.Ordinal);
                    if (known)
                    {
                        flush(); if (end < 0) throw Error(key, i, "标记缺少闭合方括号。");
                        if (tag.StartsWith("icon:", StringComparison.Ordinal)) template.Tokens.Add(ParseIcon(tag.Substring(5), current, template, key, i));
                        else if (tag[0] == '/')
                        {
                            if (styles.Count == 0 || styles.Peek().Key != tag.Substring(1)) throw Error(key, i, "格式标记交叉或未闭合。");
                            current = styles.Pop().Value;
                        }
                        else
                        {
                            string name = tag.StartsWith("color:", StringComparison.Ordinal) ? "color" : tag;
                            styles.Push(new KeyValuePair<string, GameplayTextRunStyle>(name, current));
                            current = new GameplayTextRunStyle(current.SemanticRole, name == "b" ? "Bold" : name == "normal" ? "Normal" : current.WeightMode,
                                name == "color" ? ParseColor(tag.Substring(6), key, i) : current.ColorRgba);
                        }
                        i = end + 1; continue;
                    }
                }
                literal.Append(ch); i++;
            }
            flush(); if (styles.Count != 0) throw Error(key, text.Length, "格式标记未闭合。");
            templates.Add(cacheKey, template); CompiledTemplateCount++; return template;
        }

        private Token ParseIcon(string declaration, GameplayTextRunStyle style, CompiledTemplate template, string key, int position)
        {
            var fields = declaration.Split('|');
            if (fields.Length > 2 || string.IsNullOrEmpty(fields[0]) || fields[0].IndexOf('{') >= 0) throw Error(key, position, "图标声明无效。");
            GameplayIconDefinition icon;
            if (!catalog.TryGetIcon(fields[0], out icon) || icon == null || icon.Usage != "inline_rule") throw Error(key, position, "图标未登记正文用途。");
            var token = new Token { Kind = TokenKind.Icon, Text = fields[0], Style = style };
            if (fields.Length == 2)
            {
                if (!fields[1].StartsWith("value=", StringComparison.Ordinal) || icon.RenderMode != "number_overlay") throw Error(key, position, "图标数值声明无效。");
                var value = fields[1].Substring(6);
                if (value.StartsWith("{", StringComparison.Ordinal) && value.EndsWith("}", StringComparison.Ordinal))
                {
                    var name = value.Substring(1, value.Length - 2); string kind;
                    if (!template.Parameters.TryGetValue(name, out kind) || kind != "integer") throw Error(key, position, "叠数参数必须声明为 integer。");
                    token.ValueParameter = name;
                }
                else
                {
                    int number;
                    if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number) ||
                        number < NormalizedValue.MinInteger || number > NormalizedValue.MaxInteger) throw Error(key, position, "叠数不是有效有符号整数。");
                    token.Number = number; token.HasNumber = true;
                }
            }
            if (icon.ValueRequired && !token.HasNumber && token.ValueParameter == null) throw Error(key, position, "图标缺少必须数值。");
            return token;
        }

        private void EmitLiteral(List<Token> tokens, string text, GameplayTextRunStyle style, string key)
        {
            var ordinary = new StringBuilder();
            Action flush = () => { if (ordinary.Length > 0) { tokens.Add(new Token { Kind = TokenKind.Text, Text = ordinary.ToString(), Style = style }); ordinary.Clear(); } };
            for (int i = 0; i < text.Length;)
            {
                KeyValuePair<string, string>? match = null;
                foreach (var word in keywords) if (i + word.Key.Length <= text.Length && string.CompareOrdinal(text, i, word.Key, 0, word.Key.Length) == 0) { match = word; break; }
                if (match.HasValue)
                { flush(); tokens.Add(new Token { Kind = TokenKind.Text, Text = match.Value.Key, Style = Role(style, match.Value.Value) }); i += match.Value.Key.Length; continue; }
                if (text[i] >= '0' && text[i] <= '9')
                {
                    flush(); int end = i + 1; while (end < text.Length && text[end] >= '0' && text[end] <= '9') end++;
                    long number; bool numeric = long.TryParse(text.Substring(i, end - i), NumberStyles.None, CultureInfo.InvariantCulture, out number);
                    var digits = text.Substring(i, end - i);
                    tokens.Add(!numeric || (digits.Length > 1 && digits[0] == '0') ? new Token { Kind = TokenKind.Text, Text = digits, Style = Role(style, "EffectNumber") } :
                        new Token { Kind = TokenKind.Number, Number = number, Style = Role(style, "EffectNumber") }); i = end; continue;
                }
                ordinary.Append(text[i++]);
            }
            flush();
        }

        private static uint ParseColor(string value, string key, int position)
        {
            uint rgba;
            if ((value.Length != 7 && value.Length != 9) || value[0] != '#' ||
                !uint.TryParse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgba)) throw Error(key, position, "颜色需为 #RRGGBB 或 #RRGGBBAA。");
            return value.Length == 7 ? (rgba << 8) | 255u : rgba;
        }
        private static void AddText(List<GameplayTextRun> runs, string text, GameplayTextRunStyle style)
        {
            if (string.IsNullOrEmpty(text)) return;
            // 参数保持纯文本，合并也不会再次经过模板词法分析。
            var previous = runs.Count == 0 ? null : runs[runs.Count - 1] as TextRun;
            if (previous != null && previous.Style.Equals(style)) runs[runs.Count - 1] = new TextRun(previous.Text + text, style);
            else runs.Add(new TextRun(text, style));
        }
        private static GameplayTextRunStyle Role(GameplayTextRunStyle style, string role) => new GameplayTextRunStyle(role, style.WeightMode, style.ColorRgba);
        private static string Name(GameplayTextArgument arg, GameplayTextContext context)
        {
            var names = arg.Kind == "player" ? context.PlayerNames : arg.Kind == "item" ? context.ItemNames :
                arg.Kind == "definition" ? context.DefinitionNames : arg.Kind == "resource" ? context.ResourceNames : context.LocationNames;
            return Lookup(names, arg.Value.ReferenceId);
        }
        private static string Lookup(IReadOnlyDictionary<string, string> dictionary, string key)
        { string value; return dictionary != null && dictionary.TryGetValue(key, out value) ? value : null; }
        private static bool ValidKind(string kind) => kind == "integer" || kind == "text" || kind == "player" || kind == "item" || kind == "definition" || kind == "resource" || kind == "location";
        private static bool ValidRole(string role) => role == "Regular" || role == "SpecialWord" || role == "EffectNumber";
        private static GameplayTextFormatException Error(string key, int position, string reason) => new GameplayTextFormatException(key ?? "", position, reason);
        private void Report(GameplayTextFormatException error)
        { bool seen; var key = catalog.ContentHash + error.TextKey + error.Position + error.Message; if (!diagnostics.TryGet(key, out seen)) { diagnostics.Add(key, true); Diagnostic?.Invoke(error); } }
        internal static void Field(StringBuilder text, string value) { value = value ?? ""; text.Append(value.Length).Append(':').Append(value); }
        internal static string Hash(string text)
        { using (var hash = SHA256.Create()) { var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(text)); var result = new StringBuilder(64); foreach (var b in bytes) result.Append(b.ToString("x2", CultureInfo.InvariantCulture)); return result.ToString(); } }
        private enum TokenKind { Text, Number, Parameter, Icon, Break }
        private sealed class Token { public TokenKind Kind; public string Text, ValueParameter; public long Number; public bool HasNumber; public GameplayTextRunStyle Style; }
        private sealed class CompiledTemplate { public readonly List<Token> Tokens = new List<Token>(); public readonly Dictionary<string, string> Parameters = new Dictionary<string, string>(StringComparer.Ordinal); }
        private sealed class Cache<T>
        {
            private readonly int capacity; private readonly Dictionary<string, T> values = new Dictionary<string, T>(StringComparer.Ordinal); private readonly Queue<string> order = new Queue<string>();
            public Cache(int capacity) { this.capacity = Math.Max(1, capacity); }
            public bool TryGet(string key, out T value) => values.TryGetValue(key, out value);
            public void Add(string key, T value) { if (values.ContainsKey(key)) { values[key] = value; return; } while (values.Count >= capacity) values.Remove(order.Dequeue()); values.Add(key, value); order.Enqueue(key); }
            public void Clear() { values.Clear(); order.Clear(); }
        }
    }
}
