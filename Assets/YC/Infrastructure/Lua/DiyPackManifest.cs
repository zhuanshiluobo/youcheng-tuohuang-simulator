using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace YC.Infrastructure.Lua
{
    /// <summary>独立 DIY 封装；与 core 的 schema 3 是不同合同。</summary>
    public sealed class DiyPackManifest
    {
        public const int SchemaVersion = 1;
        public const int ContentApiVersion = 1;
        public string PackId { get; private set; }
        public string Version { get; private set; }
        public string DisplayName { get; private set; }
        public string Author { get; private set; }
        public string Description { get; private set; }
        public IReadOnlyList<string> RequiredCapabilities { get; private set; }
        public IReadOnlyList<DiyPackDependency> Dependencies { get; private set; }
        public IReadOnlyList<DiyRuleMode> RuleModes { get; private set; }
        public IReadOnlyList<DiyExpansion> Expansions { get; private set; }
        public IReadOnlyList<string> Definitions { get; private set; }
        public IReadOnlyList<string> SetupRecipes { get; private set; }
        // 扩展字段仍由各自的正式读取器校验；不把外部 JObject 暴露给调用者。
        private JObject source;
        public JObject Source => (JObject)source.DeepClone();

        public static DiyPackManifest Parse(JObject value, string sourcePath = "pack.json")
        {
            if (value == null) throw Error(sourcePath, "根节点必须是对象");
            Fields(value, sourcePath, "format", "schemaVersion", "packId", "version", "displayName", "author", "description",
                "contentApiVersion", "requiredCapabilities", "dependencies", "ruleModes", "expansions", "definitions", "setupRecipes",
                "presentation", "artworkSlices", "luaModules", "scriptDependencies");
            if (Text(value, "format", sourcePath) != "nomadcity.diy" || Integer(value, "schemaVersion", sourcePath) != SchemaVersion)
                throw Error(sourcePath, "format 或 schemaVersion 不支持");
            if (Integer(value, "contentApiVersion", sourcePath) != ContentApiVersion)
                throw Error(sourcePath + ":contentApiVersion", "内容 API 版本不支持");
            string id = Text(value, "packId", sourcePath);
            if (!DiyIdentifiers.ValidPackId(id) || id == "core") throw Error(sourcePath + ":packId", "包 ID 无效或为保留身份");
            string version = Text(value, "version", sourcePath);
            DiyVersion.Parse(version, sourcePath + ":version");
            var dependencies = Objects(value, "dependencies", sourcePath).Select((entry, index) =>
                DiyPackDependency.Parse(entry, id, sourcePath + ":dependencies[" + index + "]")).ToArray();
            Unique(dependencies.Select(x => x.PackId), sourcePath + ":dependencies");
            var modes = Objects(value, "ruleModes", sourcePath).Select((entry, index) =>
                DiyRuleMode.Parse(entry, id, sourcePath + ":ruleModes[" + index + "]")).ToArray();
            var expansions = Objects(value, "expansions", sourcePath).Select((entry, index) =>
                DiyExpansion.Parse(entry, id, sourcePath + ":expansions[" + index + "]")).ToArray();
            var requiredCapabilities = Strings(value, "requiredCapabilities", sourcePath, true);
            const string completedCapability = "diy.rule_bindings.completed.v1";
            foreach (var mode in modes)
                if (mode.RuleBindings.Any(binding => binding.EventType == "EffectCompleted") &&
                    !requiredCapabilities.Contains(completedCapability) && !mode.RequiredCapabilities.Contains(completedCapability))
                    throw Error(sourcePath + ":ruleModes:" + mode.ModeId, "完成事件绑定缺少能力声明 " + completedCapability);
            foreach (var expansion in expansions)
                if (expansion.RuleBindings.Any(binding => binding.EventType == "EffectCompleted") &&
                    !requiredCapabilities.Contains(completedCapability) && !expansion.RequiredCapabilities.Contains(completedCapability))
                    throw Error(sourcePath + ":expansions:" + expansion.ExpansionId, "完成事件绑定缺少能力声明 " + completedCapability);
            Unique(modes.Select(x => x.ModeId), sourcePath + ":ruleModes");
            Unique(expansions.Select(x => x.ExpansionId), sourcePath + ":expansions");
            Unique(modes.SelectMany(x => x.RuleBindings).Concat(expansions.SelectMany(x => x.RuleBindings)).Select(x => x.BindingId), sourcePath + ":ruleBindings");
            if (modes.Length == 0 && expansions.Length == 0) throw Error(sourcePath, "至少声明一个模式或扩展");
            var definitions = Strings(value, "definitions", sourcePath);
            var recipes = Strings(value, "setupRecipes", sourcePath);
            foreach (string path in definitions.Concat(recipes)) DiyPackPaths.ValidateRelative(path);
            Unique(definitions.Concat(recipes), sourcePath + ":文件引用", StringComparer.OrdinalIgnoreCase);
            if (value["presentation"] != null && !(value["presentation"] is JObject)) throw Error(sourcePath + ":presentation", "必须是对象");
            if (value["artworkSlices"] != null) DiyPackPaths.ValidateRelative(Text(value, "artworkSlices", sourcePath));
            foreach (string name in new[] { "luaModules", "scriptDependencies" })
                if (value[name] != null && !(value[name] is JArray)) throw Error(sourcePath + ":" + name, "必须是数组");
            return new DiyPackManifest
            {
                PackId = id, Version = version, DisplayName = Text(value, "displayName", sourcePath),
                Author = Text(value, "author", sourcePath), Description = Text(value, "description", sourcePath),
                RequiredCapabilities = Array.AsReadOnly(requiredCapabilities),
                Dependencies = Array.AsReadOnly(dependencies), RuleModes = Array.AsReadOnly(modes), Expansions = Array.AsReadOnly(expansions),
                Definitions = Array.AsReadOnly(definitions), SetupRecipes = Array.AsReadOnly(recipes), source = (JObject)value.DeepClone()
            };
        }

        internal static InvalidDataException Error(string source, string message) => new InvalidDataException(source + ": " + message);
        internal static void Fields(JObject value, string source, params string[] names)
        {
            var allowed = new HashSet<string>(names, StringComparer.Ordinal);
            foreach (var property in value.Properties())
                if (!allowed.Contains(property.Name)) throw Error(source + ":" + property.Name, "未知字段");
        }
        internal static string Text(JObject value, string name, string source)
        {
            if (value[name]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)value[name]))
                throw Error(source + ":" + name, "必须是非空字符串");
            return (string)value[name];
        }
        internal static int Integer(JObject value, string name, string source)
        {
            if (value[name]?.Type != JTokenType.Integer || !int.TryParse(value[name].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                throw Error(source + ":" + name, "必须是非负整数");
            return number;
        }
        internal static string[] Strings(JObject value, string name, string source, bool required = false)
        {
            if (value[name] == null && !required) return Array.Empty<string>();
            if (!(value[name] is JArray array) || array.Any(x => x.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)x)))
                throw Error(source + ":" + name, "必须是非空字符串组成的数组");
            var result = array.Values<string>().ToArray();
            Unique(result, source + ":" + name);
            return result;
        }
        internal static JObject[] Objects(JObject value, string name, string source)
        {
            if (value[name] == null) return Array.Empty<JObject>();
            if (!(value[name] is JArray array) || array.Any(x => !(x is JObject)))
                throw Error(source + ":" + name, "必须是对象数组");
            return array.Cast<JObject>().ToArray();
        }
        internal static void Unique(IEnumerable<string> values, string source, StringComparer comparer = null)
        {
            var seen = new HashSet<string>(comparer ?? StringComparer.Ordinal);
            foreach (string value in values) if (!seen.Add(value)) throw Error(source, "重复身份：" + value);
        }
    }

    public static class DiyIdentifiers
    {
        private static readonly Regex Pack = new Regex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant);
        private static readonly Regex Identity = new Regex(@"^[A-Za-z0-9]+(?:[._-][A-Za-z0-9]+)*$", RegexOptions.CultureInvariant);
        public static bool ValidPackId(string value) => value != null && value.Length <= 128 && Pack.IsMatch(value);
        public static bool ValidIdentity(string value) => value != null && value.Length <= 192 && Identity.IsMatch(value);
        public static void RequireOwned(string value, string packId, string source)
        {
            if (!ValidIdentity(value) || !value.StartsWith(packId + ".", StringComparison.Ordinal))
                throw DiyPackManifest.Error(source, "身份必须位于所属包命名空间 " + packId + ".");
        }
    }

    public readonly struct DiyVersion : IComparable<DiyVersion>
    {
        public readonly int Major, Minor, Patch;
        private DiyVersion(int major, int minor, int patch) { Major = major; Minor = minor; Patch = patch; }
        public static DiyVersion Parse(string value, string source)
        {
            var parts = (value ?? "").Split('.');
            if (parts.Length != 3 || parts.Any(p => p.Length == 0 || p.Length > 1 && p[0] == '0' ||
                !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                throw DiyPackManifest.Error(source, "版本必须是三段非负整数，不能含前导零");
            return new DiyVersion(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
        }
        public int CompareTo(DiyVersion other)
        { int result = Major.CompareTo(other.Major); if (result != 0) return result; result = Minor.CompareTo(other.Minor); return result != 0 ? result : Patch.CompareTo(other.Patch); }
    }

    public sealed class DiyPackDependency
    {
        public string PackId { get; private set; }
        public string VersionRange { get; private set; }
        private DiyVersion minimum, maximum;
        private bool exact;
        internal static DiyPackDependency Parse(JObject value, string owner, string source)
        {
            DiyPackManifest.Fields(value, source, "packId", "versionRange");
            string id = DiyPackManifest.Text(value, "packId", source), range = DiyPackManifest.Text(value, "versionRange", source);
            if (!DiyIdentifiers.ValidPackId(id) || id == owner) throw DiyPackManifest.Error(source + ":packId", "包依赖 ID 无效或指向自身");
            var result = new DiyPackDependency { PackId = id, VersionRange = range };
            if (range.StartsWith("=", StringComparison.Ordinal)) { result.exact = true; result.minimum = DiyVersion.Parse(range.Substring(1), source + ":versionRange"); }
            else
            {
                var parts = range.Split(' ');
                if (parts.Length != 2 || !parts[0].StartsWith(">=", StringComparison.Ordinal) || !parts[1].StartsWith("<", StringComparison.Ordinal))
                    throw DiyPackManifest.Error(source + ":versionRange", "仅支持 =x.y.z 或 >=x.y.z <a.b.c");
                result.minimum = DiyVersion.Parse(parts[0].Substring(2), source + ":versionRange");
                result.maximum = DiyVersion.Parse(parts[1].Substring(1), source + ":versionRange");
                if (result.minimum.CompareTo(result.maximum) >= 0) throw DiyPackManifest.Error(source + ":versionRange", "版本区间为空");
            }
            return result;
        }
        public bool Accepts(string version)
        { var candidate = DiyVersion.Parse(version, "version"); int lower = candidate.CompareTo(minimum); return exact ? lower == 0 : lower >= 0 && candidate.CompareTo(maximum) < 0; }
    }

    public sealed class DiyRecipeReference
    {
        public string PackId { get; private set; }
        public string RecipeId { get; private set; }
        internal static DiyRecipeReference Parse(JObject value, string source)
        {
            DiyPackManifest.Fields(value, source, "packId", "recipeId");
            string pack = DiyPackManifest.Text(value, "packId", source), recipe = DiyPackManifest.Text(value, "recipeId", source);
            if (!DiyIdentifiers.ValidPackId(pack) || !DiyIdentifiers.ValidIdentity(recipe)) throw DiyPackManifest.Error(source, "配方来源或身份无效");
            return new DiyRecipeReference { PackId = pack, RecipeId = recipe };
        }
    }

    public sealed class DiyRuleMode
    {
        public string ModeId { get; private set; }
        public string DisplayName { get; private set; }
        public string Description { get; private set; }
        public IReadOnlyList<string> Requires { get; private set; }
        public IReadOnlyList<string> RequiredCapabilities { get; private set; }
        public IReadOnlyList<string> Conflicts { get; private set; }
        public int? PreparationOrder { get; private set; }
        public bool UsesMultiModeContract { get; private set; }
        // 兼容旧包未显式声明绑定能力；只用于就绪检查，不改写包清单或其指纹。
        public IReadOnlyList<string> EffectiveRequiredCapabilities => Array.AsReadOnly(RequiredCapabilities.Concat(
            RuleBindings.Select(binding => DiyRuleBinding.CapabilityFor(binding.EventType)))
            .Concat(Preparation == null ? Array.Empty<string>() : new[] { DiyPreparationDeclaration.Capability })
            .Concat(UsesMultiModeContract ? new[] { "diy.multi_rule_modes.v1" } : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).ToArray());
        public IReadOnlyList<string> ApplicableMapIds { get; private set; }
        public IReadOnlyList<string> Mechanisms { get; private set; }
        public IReadOnlyList<DiyRecipeReference> SetupRecipeRefs { get; private set; }
        public IReadOnlyList<DiyRuleBinding> RuleBindings { get; private set; }
        public DiyPreparationDeclaration Preparation { get; private set; }
        internal static DiyRuleMode Parse(JObject value, string owner, string source)
        {
            DiyPackManifest.Fields(value, source, "modeId", "displayName", "description", "requires", "conflicts", "preparationOrder", "requiredCapabilities", "applicableMapIds", "mechanisms", "setupRecipeRefs", "ruleBindings", "preparation");
            string id = DiyPackManifest.Text(value, "modeId", source); DiyIdentifiers.RequireOwned(id, owner, source + ":modeId");
            var conflicts = DiyPackManifest.Strings(value, "conflicts", source);
            if (conflicts.Contains(id) || conflicts.Any(item => !DiyIdentifiers.ValidIdentity(item)))
                throw DiyPackManifest.Error(source + ":conflicts", "模式冲突身份无效或指向自身");
            DiyPreparationDeclaration preparation = null;
            if (value["preparation"] != null)
            {
                if (!(value["preparation"] is JObject declaration)) throw DiyPackManifest.Error(source + ":preparation", "必须是对象");
                preparation = DiyPreparationDeclaration.Parse(declaration, id, source + ":preparation");
            }
            var recipes = DiyPackManifest.Objects(value, "setupRecipeRefs", source).Select((entry, i) => DiyRecipeReference.Parse(entry, source + ":setupRecipeRefs[" + i + "]")).ToArray();
            if (recipes.Length == 0) throw DiyPackManifest.Error(source + ":setupRecipeRefs", "模式至少需要一个开局配方");
            DiyPackManifest.Unique(recipes.Select(r => r.PackId + "/" + r.RecipeId), source + ":setupRecipeRefs");
            var maps = DiyPackManifest.Strings(value, "applicableMapIds", source);
            var mechanisms = DiyPackManifest.Strings(value, "mechanisms", source);
            if (maps.Any(map => map != "map-three-players" && map != "map-four-players")) throw DiyPackManifest.Error(source + ":applicableMapIds", "存在未知地图");
            return new DiyRuleMode { ModeId = id, DisplayName = DiyPackManifest.Text(value, "displayName", source), Description = DiyPackManifest.Text(value, "description", source),
                Requires = Array.AsReadOnly(DiyPackManifest.Strings(value, "requires", source)), RequiredCapabilities = Array.AsReadOnly(DiyPackManifest.Strings(value, "requiredCapabilities", source)),
                Conflicts = Array.AsReadOnly(conflicts), PreparationOrder = value["preparationOrder"] == null ?
                    (preparation?.HasSteps == true || preparation == null && mechanisms.Contains("entrepreneur_selection.v1") ? 0 : (int?)null) : DiyPackManifest.Integer(value, "preparationOrder", source),
                UsesMultiModeContract = value["preparationOrder"] != null || conflicts.Length > 0,
                ApplicableMapIds = Array.AsReadOnly(maps), Mechanisms = Array.AsReadOnly(mechanisms), SetupRecipeRefs = Array.AsReadOnly(recipes),
                RuleBindings = Array.AsReadOnly(DiyPackManifest.Objects(value, "ruleBindings", source).Select((entry, i) => DiyRuleBinding.Parse(entry, owner, source + ":ruleBindings[" + i + "]")).ToArray()),
                Preparation = preparation };
        }
    }

    public sealed class DiyExpansion
    {
        public string ExpansionId { get; private set; }
        public string DisplayName { get; private set; }
        public string Description { get; private set; }
        public IReadOnlyList<string> ApplicableModes { get; private set; }
        public IReadOnlyList<string> Requires { get; private set; }
        public IReadOnlyList<string> Conflicts { get; private set; }
        public IReadOnlyList<string> RequiredCapabilities { get; private set; }
        public IReadOnlyList<string> EffectiveRequiredCapabilities => Array.AsReadOnly(RequiredCapabilities.Concat(
            RuleBindings.Select(binding => DiyRuleBinding.CapabilityFor(binding.EventType))).Distinct(StringComparer.Ordinal).ToArray());
        public IReadOnlyList<DiyRuleBinding> RuleBindings { get; private set; }
        internal static DiyExpansion Parse(JObject value, string owner, string source)
        {
            DiyPackManifest.Fields(value, source, "expansionId", "displayName", "description", "applicableModes", "requires", "conflicts", "requiredCapabilities", "ruleBindings");
            string id = DiyPackManifest.Text(value, "expansionId", source); DiyIdentifiers.RequireOwned(id, owner, source + ":expansionId");
            var needs = DiyPackManifest.Strings(value, "requires", source); var conflicts = DiyPackManifest.Strings(value, "conflicts", source);
            if (needs.Contains(id) || conflicts.Contains(id) || needs.Intersect(conflicts, StringComparer.Ordinal).Any()) throw DiyPackManifest.Error(source, "扩展不能依赖或排斥自身，也不能同时依赖并排斥同一扩展");
            return new DiyExpansion { ExpansionId = id, DisplayName = DiyPackManifest.Text(value, "displayName", source), Description = DiyPackManifest.Text(value, "description", source),
                ApplicableModes = Array.AsReadOnly(DiyPackManifest.Strings(value, "applicableModes", source)), Requires = Array.AsReadOnly(needs), Conflicts = Array.AsReadOnly(conflicts),
                RequiredCapabilities = Array.AsReadOnly(DiyPackManifest.Strings(value, "requiredCapabilities", source)),
                RuleBindings = Array.AsReadOnly(DiyPackManifest.Objects(value, "ruleBindings", source).Select((entry, i) => DiyRuleBinding.Parse(entry, owner, source + ":ruleBindings[" + i + "]")).ToArray()) };
        }
    }

    /// <summary>只声明已有的规则事件与候选入口；Lua 仍使用现行上下文及返回合同。</summary>
    public sealed class DiyRuleBinding
    {
        internal static string CapabilityFor(string eventType) => eventType == "PlayerCleanupStarted" ? "diy.rule_bindings.cleanup.v1" :
            DiyRuleBindingCatalog.SupportsPatchEvent(eventType) ? "diy.rule_bindings.dispatch.v1" :
            eventType == "CandidateSetBuilding" ? "diy.rule_bindings.candidate.v1" :
            eventType == "EffectCompleted" ? "diy.rule_bindings.completed.v1" :
            DiyRuleBindingCatalog.SupportsEffectEvent(eventType) ? "diy.rule_bindings.events.v1" :
            throw new InvalidDataException("diy_rule_binding_capability_unknown:" + eventType);
        internal static bool ValidCompletedTypes(IReadOnlyList<string> types) => types != null && types.Count > 0 && types.Count <= 64 &&
            types.Distinct(StringComparer.Ordinal).Count() == types.Count && types.All(t => !string.IsNullOrWhiteSpace(t) &&
                t.Length <= 256 && t != YC.Domain.Effects.AbilitySourceEffectExecutor.TypeId &&
                t.All(c => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '.' || c == '_' || c == '-'));
        public string BindingId { get; private set; }
        public string EventType { get; private set; }
        public string CandidateKind { get; private set; } = string.Empty;
        public IReadOnlyList<string> CompletedEffectTypes { get; private set; } = Array.Empty<string>();
        public string CompletionPolicy { get; private set; } = string.Empty;
        public string Script { get; private set; }
        public string Version { get; private set; }
        public string CompletionHandlerId { get; private set; } = string.Empty;
        public string CompletionScript { get; private set; } = string.Empty;
        public string CompletionVersion { get; private set; } = string.Empty;
        internal static DiyRuleBinding Parse(JObject value, string owner, string source)
        {
            DiyPackManifest.Fields(value, source, "bindingId", "eventType", "candidateKind", "completedEffectTypes", "completionPolicy", "script", "version", "completion");
            string id = DiyPackManifest.Text(value, "bindingId", source); DiyIdentifiers.RequireOwned(id, owner, source + ":bindingId");
            string eventType = DiyPackManifest.Text(value, "eventType", source);
            if (!DiyRuleBindingCatalog.SupportsEffectEvent(eventType) && !DiyRuleBindingCatalog.SupportsPatchEvent(eventType) && eventType != "CandidateSetBuilding" && eventType != "EffectCompleted")
                throw DiyPackManifest.Error(source + ":eventType", "不支持的规则入口");
            if (DiyRuleBindingCatalog.SupportsPatchEvent(eventType) && value["completion"] != null)
                throw DiyPackManifest.Error(source + ":completion", "特派数量前置修正不支持完成处理器");
            string candidateKind = string.Empty;
            if (eventType == "CandidateSetBuilding")
            {
                candidateKind = DiyPackManifest.Text(value, "candidateKind", source);
                if (candidateKind != YC.Domain.Movement.CityDestinationQueryService.CandidateKind &&
                    candidateKind != YC.Domain.Facilities.FacilityBuildCandidateQuery.CandidateKind)
                    throw DiyPackManifest.Error(source + ":candidateKind", "不支持的候选类型");
                if (value["completion"] != null) throw DiyPackManifest.Error(source + ":completion", "只读候选入口不支持完成处理器");
            }
            else if (value["candidateKind"] != null) throw DiyPackManifest.Error(source + ":candidateKind", "效果事件不接受候选类型");
            var completedTypes = Array.Empty<string>();
            string completionPolicy = string.Empty;
            if (eventType == "EffectCompleted")
            {
                completedTypes = DiyPackManifest.Strings(value, "completedEffectTypes", source, true);
                if (!ValidCompletedTypes(completedTypes))
                    throw DiyPackManifest.Error(source + ":completedEffectTypes", "必须声明 1 至 64 个有效的公开效果类型");
                completionPolicy = value["completionPolicy"] == null ? "succeeded" : DiyPackManifest.Text(value, "completionPolicy", source);
                if (completionPolicy != "succeeded" && completionPolicy != "terminal")
                    throw DiyPackManifest.Error(source + ":completionPolicy", "只支持 succeeded 或 terminal");
            }
            else if (value["completedEffectTypes"] != null || value["completionPolicy"] != null)
                throw DiyPackManifest.Error(source, "完成效果过滤只适用于 EffectCompleted");
            string script = DiyPackManifest.Text(value, "script", source); DiyPackPaths.ValidateRelative(script);
            var result = new DiyRuleBinding { BindingId = id, EventType = eventType, CandidateKind = candidateKind,
                CompletedEffectTypes = Array.AsReadOnly(completedTypes), CompletionPolicy = completionPolicy,
                Script = script, Version = DiyPackManifest.Text(value, "version", source) };
            if (value["completion"] != null)
            {
                if (!(value["completion"] is JObject completion)) throw DiyPackManifest.Error(source + ":completion", "必须是对象");
                DiyPackManifest.Fields(completion, source + ":completion", "handlerId", "script", "version");
                result.CompletionHandlerId = DiyPackManifest.Text(completion, "handlerId", source + ":completion");
                DiyIdentifiers.RequireOwned(result.CompletionHandlerId, owner, source + ":completion.handlerId");
                result.CompletionScript = DiyPackManifest.Text(completion, "script", source + ":completion");
                DiyPackPaths.ValidateRelative(result.CompletionScript);
                result.CompletionVersion = DiyPackManifest.Text(completion, "version", source + ":completion");
            }
            return result;
        }
    }
}
