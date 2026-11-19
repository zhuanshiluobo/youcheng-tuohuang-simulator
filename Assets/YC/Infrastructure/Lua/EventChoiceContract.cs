using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using MoonSharp.Interpreter;
using Newtonsoft.Json.Linq;
using YC.Domain.Cards;
using YC.Domain.Rules;

namespace YC.Infrastructure.Lua
{
    /// <summary>
    /// 事件选择格式的共享校验器；正式内容读取时仅编译脚本，不执行脚本。
    /// </summary>
    public sealed class EventChoiceContract
    {
        public sealed class Entry
        {
            internal Entry(string id, string description, double top, double bottom, string script, string version, string completion)
            { ChoiceId = id; Description = description; TopPercent = top; BottomPercent = bottom; Script = script; Version = version; CompletionHandlerId = completion; }
            public string ChoiceId { get; }
            public string Description { get; }
            public double TopPercent { get; }
            public double BottomPercent { get; }
            public string Script { get; }
            public string Version { get; }
            public string CompletionHandlerId { get; }
        }

        public sealed class ImmediateEntry
        {
            internal ImmediateEntry(string script, string version) { Script = script; Version = version; }
            public string Script { get; }
            public string Version { get; }
        }

        private EventChoiceContract(List<Entry> entries, ImmediateEntry immediate)
        { Choices = entries.AsReadOnly(); ImmediateResolve = immediate; }
        public IReadOnlyList<Entry> Choices { get; }
        public ImmediateEntry ImmediateResolve { get; }

        /// <param name="readScript">调用者必须从所属包内安全读取文件；本方法只编译，不执行源码。</param>
        public static EventChoiceContract Parse(JObject data, string sourcePath, Func<string, string> readScript,
            IReadOnlyCollection<string> registeredCompletionHandlers)
        {
            if (data == null) throw Invalid(sourcePath, "data", "必须是对象");
            if (readScript == null) throw new ArgumentNullException(nameof(readScript));
            CheckFields(data, sourcePath, "data", "cardId", "name", "description", "color", "resourceType",
                "resourceAmount", "representativeResourceType", "representativeResourceAmount",
                "eventChoiceSchemaVersion", "choices", "immediateResolve");
            if (data["cardId"] != null) Identifier(data, "cardId", sourcePath, "data");
            if (data["name"] != null && data["name"].Type != JTokenType.String)
                throw Invalid(sourcePath, "data.name", "必须是字符串");
            RequiredString(data, "description", sourcePath, "data");
            if (!Enum.IsDefined(typeof(EventColor), Integer(data, "color", sourcePath)) ||
                !Enum.IsDefined(typeof(ResourceType), Integer(data, "resourceType", sourcePath)) ||
                !Enum.IsDefined(typeof(ResourceType), Integer(data, "representativeResourceType", sourcePath)))
                throw Invalid(sourcePath, "data", "颜色或资源类型不在正式枚举中");
            if (Integer(data, "resourceAmount", sourcePath) <= 0 || Integer(data, "representativeResourceAmount", sourcePath) <= 0)
                throw Invalid(sourcePath, "data", "资源数量必须是正整数");
            if (data["eventChoiceSchemaVersion"]?.Type != JTokenType.Integer ||
                !string.Equals(data["eventChoiceSchemaVersion"].ToString(), "1", StringComparison.Ordinal))
                throw Invalid(sourcePath, "data.eventChoiceSchemaVersion", "只支持版本 1");
            if (!(data["choices"] is JArray rawChoices)) throw Invalid(sourcePath, "data.choices", "必须是数组");
            if (rawChoices.Count > 16) throw Invalid(sourcePath, "data.choices", "最多允许 16 项");

            var entries = new List<Entry>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var compiled = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < rawChoices.Count; i++)
            {
                string field = "data.choices[" + i + "]";
                if (!(rawChoices[i] is JObject choice)) throw Invalid(sourcePath, field, "必须是对象");
                CheckFields(choice, sourcePath, field, "choiceId", "description", "button", "script", "version", "completionHandlerId");
                string id = Identifier(choice, "choiceId", sourcePath, field);
                if (!ids.Add(id)) throw Invalid(sourcePath, field + ".choiceId", "重复的选择 ID：" + id);
                string description = RequiredString(choice, "description", sourcePath, field);
                if (!(choice["button"] is JObject button)) throw Invalid(sourcePath, field + ".button", "必须是对象");
                CheckFields(button, sourcePath, field + ".button", "topPercent", "bottomPercent");
                double top = Number(button, "topPercent", sourcePath, field + ".button");
                double bottom = Number(button, "bottomPercent", sourcePath, field + ".button");
                if (top < 0 || top >= bottom || bottom > 100)
                    throw Invalid(sourcePath, field + ".button", "区间必须满足 0 ≤ topPercent < bottomPercent ≤ 100");
                string script = Script(choice, sourcePath, field, readScript, compiled);
                string version = Identifier(choice, "version", sourcePath, field);
                string completion = "";
                if (choice["completionHandlerId"] != null)
                {
                    if (choice["completionHandlerId"].Type != JTokenType.String)
                        throw Invalid(sourcePath, field + ".completionHandlerId", "必须是字符串");
                    completion = (string)choice["completionHandlerId"];
                    if (completion.Length > 0 && (string.IsNullOrWhiteSpace(completion) || completion.Length > 128 ||
                        completion.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 ||
                        registeredCompletionHandlers == null || !registeredCompletionHandlers.Contains(completion)))
                        throw Invalid(sourcePath, field + ".completionHandlerId", "未登记的完成处理器：" + completion);
                }
                entries.Add(new Entry(id, description, top, bottom, script, version, completion));
            }
            var ordered = entries.OrderBy(entry => entry.TopPercent).ToList();
            for (int i = 1; i < ordered.Count; i++)
                if (ordered[i].TopPercent < ordered[i - 1].BottomPercent)
                    throw Invalid(sourcePath, "data.choices", "按钮区域重叠：" + ordered[i - 1].ChoiceId + " / " + ordered[i].ChoiceId);

            ImmediateEntry immediate = null;
            if (rawChoices.Count == 0)
            {
                if (!(data["immediateResolve"] is JObject rawImmediate))
                    throw Invalid(sourcePath, "data.immediateResolve", "零选项事件必须声明立即入口");
                CheckFields(rawImmediate, sourcePath, "data.immediateResolve", "script", "version");
                immediate = new ImmediateEntry(Script(rawImmediate, sourcePath, "data.immediateResolve", readScript, compiled),
                    Identifier(rawImmediate, "version", sourcePath, "data.immediateResolve"));
            }
            else if (data["immediateResolve"] != null)
                throw Invalid(sourcePath, "data.immediateResolve", "有选项时不能声明立即入口");
            return new EventChoiceContract(entries, immediate);
        }

        private static string Script(JObject value, string source, string field, Func<string, string> readScript, HashSet<string> compiled)
        {
            string path = RequiredString(value, "script", source, field);
            string normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.IndexOf(':') >= 0 ||
                normalized.Split('/').Any(segment => segment.Length == 0 || segment == "." || segment == "..") ||
                normalized.Any(char.IsControl) || !normalized.EndsWith(".lua", StringComparison.Ordinal))
                throw Invalid(source, field + ".script", "必须是包内 Lua 相对路径");
            if (compiled.Add(normalized))
            {
                string text;
                try { text = readScript(normalized); }
                catch (Exception error) { throw new InvalidDataException(source + ":" + field + ".script: 无法读取 " + path + "：" + error.Message, error); }
                if (text == null || Encoding.UTF8.GetByteCount(text) > 32 * 1024)
                    throw Invalid(source, field + ".script", "脚本为空或超过 32 KiB：" + path);
                try { new Script(CoreModules.None).LoadString(text, null, path); }
                catch (Exception error) { throw new InvalidDataException(source + ":" + field + ".script: Lua 编译失败 " + path + "：" + error.Message, error); }
            }
            return normalized;
        }

        internal static string ValidateReferencedScript(string path, string source, string field, Func<string, string> readScript)
        {
            var value = new JObject { ["script"] = path };
            return Script(value, source, field, readScript, new HashSet<string>(StringComparer.Ordinal));
        }

        private static double Number(JObject obj, string key, string source, string field)
        {
            var token = obj[key];
            if (token == null || token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw Invalid(source, field + "." + key, "必须是有限数值");
            double value;
            try { value = (double)token; }
            catch (Exception error) when (error is OverflowException || error is FormatException || error is InvalidCastException)
            { throw Invalid(source, field + "." + key, "必须是有限数值"); }
            if (double.IsNaN(value) || double.IsInfinity(value)) throw Invalid(source, field + "." + key, "必须是有限数值");
            return value;
        }

        private static int Integer(JObject obj, string key, string source)
        {
            if (obj[key]?.Type != JTokenType.Integer || !int.TryParse(obj[key].ToString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int value))
                throw Invalid(source, "data." + key, "必须是有效整数");
            return value;
        }

        private static string Identifier(JObject obj, string key, string source, string field)
        {
            string value = RequiredString(obj, key, source, field);
            if (value.Length > 128 || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw Invalid(source, field + "." + key, "身份或版本无效");
            return value;
        }

        private static string RequiredString(JObject obj, string key, string source, string field)
        {
            if (obj[key]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)obj[key]))
                throw Invalid(source, field + "." + key, "必须是非空字符串");
            return (string)obj[key];
        }

        private static void CheckFields(JObject obj, string source, string field, params string[] allowed)
        {
            var unknown = obj.Properties().FirstOrDefault(property => !allowed.Contains(property.Name));
            if (unknown != null) throw Invalid(source, field + "." + unknown.Name, "未知字段或与旧格式混写");
        }

        private static InvalidDataException Invalid(string source, string field, string reason) =>
            new InvalidDataException((source ?? "") + ":" + field + ": " + reason);
    }
}
