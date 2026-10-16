using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using YC.Domain.State;

namespace YC.Domain.Presentation
{
    /// <summary>封闭 DTO 白名单；严格 camelCase，不执行元方法，不保存运行时对象。</summary>
    public static class GameplayPresentationNormalizer
    {
        private static readonly HashSet<Type> Types = new HashSet<Type>
        {
            typeof(GameplayTextRef), typeof(GameplayTextArgument), typeof(GameplayOptionPresentation),
            typeof(GameplayQuantityRowPresentation), typeof(GameplayInteractionPresentation), typeof(EffectPresentationDefinition),
            typeof(ContentPresentationDefinition), typeof(AbilityPresentationDefinition), typeof(SettlementPresentationDefinition),
            typeof(GameplayPresentationContext), typeof(GameplayPresentationResult), typeof(ResolutionMessageView),
            typeof(GameplayTextParameterDefinition), typeof(GameplayTextTemplateDefinition), typeof(GameplayIconDefinition),
            typeof(GameplayFontBindingDefinition)
        };
        private static readonly Regex KeyPattern = new Regex(@"^[a-z][a-z0-9_.-]*:[a-zA-Z0-9_.-]+$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> Kinds = new HashSet<string> { "integer", "text", "player", "item", "definition", "resource", "location" };
        public static bool IsKey(string key) => key != null && KeyPattern.IsMatch(key);
        public static bool IsArgumentKind(string kind) => kind != null && Kinds.Contains(kind);
        public static T Clone<T>(T source) where T : class
        {
            if (!Types.Contains(typeof(T))) throw new ArgumentException("非显示合同类型。");
            return GameStateCloneService.DeepClone(source);
        }
        public static NormalizedValue ToNormalized<T>(T source) where T : class
        {
            Validate(source);
            var result = Write(source, typeof(T), 0);
            if (!result.IsValid()) throw new ArgumentException("显示声明超过归一化限制。");
            return result;
        }
        public static T FromNormalized<T>(NormalizedValue source) where T : class, new()
        {
            if (source == null || !source.IsValid()) throw new ArgumentException("显示声明归一化值无效。");
            var result = (T)Read(source, typeof(T), 0);
            Validate(result);
            return result;
        }
        public static void Validate<T>(T source) where T : class
        { ValidateObject(source, typeof(T), 0); }
        public static bool TryValidate<T>(T source, out string reason) where T : class
        {
            try { Validate(source); reason = string.Empty; return true; }
            catch (ArgumentException error) { reason = error.Message; return false; }
        }
        private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name.Substring(1);
        private static void Require(bool condition, string diagnostic) { if (!condition) throw new ArgumentException(diagnostic); }
        private static void ValidateObject(object source, Type type, int depth)
        {
            Require(depth <= NormalizedValue.MaxDepth, "显示声明嵌套过深。");
            if (source == null) return; // nullable 覆盖不提供；空 key 从不代表空覆盖。
            if (type == typeof(string)) { Require(System.Text.Encoding.UTF8.GetByteCount((string)source) <= 32768, "显示字符串过长。"); return; }
            if (type == typeof(int) || type == typeof(long) || type == typeof(bool)) return;
            if (type == typeof(NormalizedValue)) { Require(((NormalizedValue)source).IsValid(), "显示参数无效。"); return; }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var list = (IList)source;
                Require(list.Count <= NormalizedValue.MaxElements, "显示集合过长。");
                foreach (var item in list) { Require(item != null, "显示集合不能包含 null。"); ValidateObject(item, type.GetGenericArguments()[0], depth + 1); }
                return;
            }
            Require(Types.Contains(type), "非显示合同类型。");
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) ValidateObject(field.GetValue(source), field.FieldType, depth + 1);
            var reference = source as GameplayTextRef;
            if (reference != null)
            {
                Require(IsKey(reference.Key), "文案 Key 必须是 packId:业务路径。");
                Require(reference.Arguments != null && reference.Arguments.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count() == reference.Arguments.Count, "文案参数缺失或重复。");
            }
            var argument = source as GameplayTextArgument;
            if (argument != null)
            {
                Require(!string.IsNullOrWhiteSpace(argument.Name) && IsArgumentKind(argument.Kind) && argument.Value != null, "文案参数名称或类型无效。");
                var value = argument.Value;
                Require(argument.Kind == "integer" ? value.Kind == NormalizedValueKind.Integer : argument.Kind == "text" ? value.Kind == NormalizedValueKind.String :
                    value.Kind == NormalizedValueKind.StableReference && value.ReferenceType == argument.Kind, "文案参数值与 Kind 不一致。");
            }
            var interaction = source as GameplayInteractionPresentation;
            if (interaction != null)
            {
                Require(interaction.Options != null && interaction.QuantityRows != null, "交互显示集合不能为 null。");
                Require(interaction.Options.All(o => !string.IsNullOrWhiteSpace(o.CandidateId)) && interaction.Options.Select(o => o.CandidateId).Distinct().Count() == interaction.Options.Count, "选项显示 ID 为空或重复。");
                Require(interaction.QuantityRows.All(o => !string.IsNullOrWhiteSpace(o.RowId)) && interaction.QuantityRows.Select(o => o.RowId).Distinct().Count() == interaction.QuantityRows.Count, "数量行 ID 为空或重复。");
            }
            var settlement = source as SettlementPresentationDefinition;
            if (settlement != null) Require(new[] { "text", "icon_text", "icon" }.Contains(settlement.ContentMode) &&
                (settlement.ContentMode == "text" || IsKey(settlement.IconKey)) && (settlement.ContentMode != "icon" || settlement.AriaLabel != null), "结算显示模式、图标或无障碍说明无效。");
            var icon = source as GameplayIconDefinition;
            if (icon != null)
            {
                Require(IsKey(icon.Id) && icon.AltText != null && new[] { "inline_rule", "settlement_action" }.Contains(icon.Usage), "图标身份、用途或替代文案无效。");
                bool asset = !string.IsNullOrWhiteSpace(icon.AssetKey), states = !string.IsNullOrWhiteSpace(icon.Dark) && !string.IsNullOrWhiteSpace(icon.Light) && !string.IsNullOrWhiteSpace(icon.Muted);
                Require(asset != states && (asset ? string.IsNullOrEmpty(icon.Dark) && string.IsNullOrEmpty(icon.Light) && string.IsNullOrEmpty(icon.Muted) : string.IsNullOrEmpty(icon.AssetKey)), "图标单图与三态定位必须互斥且完整。");
                Require(new[] { "sprite", "number_overlay" }.Contains(icon.RenderMode) && (icon.RenderMode != "number_overlay" || icon.ValueRequired && IsKey(icon.ValueAltTextKey) && IsKey(icon.NumberOverlayStyleKey)), "图标数值覆盖声明无效。");
            }
            var parameter = source as GameplayTextParameterDefinition;
            if (parameter != null) Require(!string.IsNullOrWhiteSpace(parameter.Name) && IsArgumentKind(parameter.Kind), "模板参数声明无效。");
            var context = source as GameplayPresentationContext;
            if (context != null) Require(new[] { "resolution", "interaction", "enterprise_detail", "card_detail", "settlement" }.Contains(context.Purpose) && context.Facts != null && context.Facts.Kind == NormalizedValueKind.Object && context.StateRevision >= 0, "显示用途、事实或 revision 无效。");
        }
        private static NormalizedValue Write(object source, Type type, int depth)
        {
            if (source == null) return NormalizedValue.CreateNull();
            if (type == typeof(NormalizedValue)) return ((NormalizedValue)source).Clone();
            if (type == typeof(string)) return NormalizedValue.CreateString((string)source);
            if (type == typeof(bool)) return NormalizedValue.CreateBoolean((bool)source);
            if (type == typeof(int) || type == typeof(long)) return NormalizedValue.CreateInteger(Convert.ToInt64(source));
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            { var values = new List<NormalizedValue>(); foreach (var item in (IList)source) values.Add(Write(item, type.GetGenericArguments()[0], depth + 1)); return NormalizedValue.CreateArray(values); }
            Require(Types.Contains(type), "非显示合同类型。");
            var entries = new List<NormalizedValueEntry>();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            { var value = field.GetValue(source); if (value != null) entries.Add(new NormalizedValueEntry { Name = Camel(field.Name), Value = Write(value, field.FieldType, depth + 1) }); }
            return NormalizedValue.CreateObject(entries);
        }
        private static object Read(NormalizedValue source, Type type, int depth)
        {
            Require(depth <= NormalizedValue.MaxDepth, "显示声明嵌套过深。");
            if (source.Kind == NormalizedValueKind.Null) { Require(!type.IsValueType, "标量不能为 null。"); return null; }
            if (type == typeof(NormalizedValue)) return source.Clone();
            if (type == typeof(string)) { Require(source.Kind == NormalizedValueKind.String, "显示字段应为字符串。"); return source.StringValue; }
            if (type == typeof(bool)) { Require(source.Kind == NormalizedValueKind.Boolean, "显示字段应为布尔值。"); return source.BooleanValue; }
            if (type == typeof(int) || type == typeof(long)) { Require(source.Kind == NormalizedValueKind.Integer, "显示字段应为整数。"); return type == typeof(int) ? (object)checked((int)source.IntegerValue) : source.IntegerValue; }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            { Require(source.Kind == NormalizedValueKind.Array, "显示字段应为数组。"); var result = (IList)Activator.CreateInstance(type); foreach (var item in source.Items) result.Add(Read(item, type.GetGenericArguments()[0], depth + 1)); return result; }
            Require(Types.Contains(type) && source.Kind == NormalizedValueKind.Object, "显示字段应为白名单 DTO 对象。");
            var target = Activator.CreateInstance(type);
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance).ToDictionary(f => Camel(f.Name), StringComparer.Ordinal);
            foreach (var entry in source.Properties)
            { FieldInfo field; Require(fields.TryGetValue(entry.Name, out field), "未知显示字段：" + entry.Name); field.SetValue(target, Read(entry.Value, field.FieldType, depth + 1)); }
            return target;
        }
        public static GameplayInteractionPresentation ProjectInteraction(GameplayInteractionPresentation source, bool visible, IEnumerable<string> candidateIds, IEnumerable<string> rowIds = null,
            Func<GameplayTextArgument, bool> canSeeArgument = null)
        {
            if (!visible || source == null) return null;
            var clone = Clone(source); var candidates = new HashSet<string>(candidateIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            var rows = new HashSet<string>(rowIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            clone.Prompt = ProjectText(clone.Prompt, canSeeArgument); clone.Progress = ProjectText(clone.Progress, canSeeArgument);
            clone.Options.RemoveAll(o => !candidates.Contains(o.CandidateId)); clone.QuantityRows.RemoveAll(o => !rows.Contains(o.RowId));
            foreach (var option in clone.Options) { option.Label = ProjectText(option.Label, canSeeArgument); option.Description = ProjectText(option.Description, canSeeArgument); option.DisabledReason = ProjectText(option.DisabledReason, canSeeArgument); }
            foreach (var row in clone.QuantityRows) { row.Label = ProjectText(row.Label, canSeeArgument); row.Description = ProjectText(row.Description, canSeeArgument); row.Unit = ProjectText(row.Unit, canSeeArgument); }
            return clone;
        }
        public static GameplayTextRef ProjectText(GameplayTextRef source, Func<GameplayTextArgument, bool> canSeeArgument)
        {
            if (source == null) return null;
            // 没有实体授权证明时保守去掉整条引用；text/integer 只用于已授权宿主。
            if (source.Arguments.Any(a => a.Kind != "integer" && a.Kind != "text" && (canSeeArgument == null || !canSeeArgument(a)))) return null;
            return Clone(source);
        }
        public static EffectPresentationDefinition ProjectEffect(EffectPresentationDefinition source, Func<GameplayTextArgument, bool> canSeeArgument = null)
        {
            if (source == null) return null;
            var result = Clone(source);
            result.Started = ProjectText(result.Started, canSeeArgument); result.Waiting = ProjectText(result.Waiting, canSeeArgument);
            result.Succeeded = ProjectText(result.Succeeded, canSeeArgument); result.Failed = ProjectText(result.Failed, canSeeArgument); result.Stopped = ProjectText(result.Stopped, canSeeArgument);
            // 具体选项和行只随权威 Interaction 投影；客户端不拿脚本自行查询 Host。
            result.Interaction = null; result.PresentationScript = string.Empty;
            return result;
        }
    }
}
