using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MoonSharp.Interpreter;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.SpecialActions;

namespace YC.Infrastructure.Lua
{
    public sealed class ExternalAbilityDefinition
    {
        public string AbilityId = "";
        public string Mode = "";
        public string SkillType = "";
        public bool? HasSpecialZone;
        public string SpecialZoneId = "";
        public string SubscriptionId = "";
        public string Script = "";
        public string Version = "";
        public string CompletionHandlerId = "";
        public string CleanupScript = "";
    }

    public sealed class ExternalReplacementReference
    {
        public string ExpansionId = "";
        public string ContentType = "";
        public string DefinitionId = "";
    }

    public sealed class ExternalContentDefinition
    {
        public bool Enabled = true;
        public string ExpansionId = "";
        public ExternalReplacementReference Replaces;
        public string DefinitionId = "";
        public string ContentType = "";
        public string Version = "";
        public string DisplayName = "";
        public string Artwork = "";
        public string[] Tags = Array.Empty<string>();
        public JArray PlayerMarkerZones = new JArray();
        public List<ExternalAbilityDefinition> Abilities = new List<ExternalAbilityDefinition>();
        public string DataTemplate = "";
        [JsonIgnore] public string RuntimeId = "";
        public JObject Data = new JObject();
    }

    /// <summary>只读取外部内容与脚本；不依赖 Unity，不执行对局规则，不持有 GameState。</summary>
    public sealed class ExternalContentPack
    {
        private static ExternalContentPack current;
        private readonly List<ExternalContentDefinition> definitions = new List<ExternalContentDefinition>();
        private readonly List<ExternalContentDefinition> activeDefinitions = new List<ExternalContentDefinition>();
        private readonly Dictionary<string, string> runtimeIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> scripts = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> sharedArtwork = new Dictionary<string, string>(StringComparer.Ordinal);
        public string RootPath { get; private set; }
        public string ContentHash { get; private set; }
        public string PackId { get; private set; }
        public static ExternalContentPack Current => current ?? (current = Load(Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core")));
        public static void Configure(string root) { current = Load(root); }
        public IReadOnlyList<ExternalContentDefinition> Definitions => definitions.Select(Clone).ToList().AsReadOnly();
        public IReadOnlyList<ExternalContentDefinition> ActiveDefinitions => activeDefinitions.Select(Clone).ToList().AsReadOnly();
        private static ExternalContentDefinition Clone(ExternalContentDefinition value)
        {
            var result = JsonConvert.DeserializeObject<ExternalContentDefinition>(JsonConvert.SerializeObject(value));
            result.RuntimeId = value.RuntimeId;
            return result;
        }

        public static ExternalContentPack Load(string root)
        {
            var pack = new ExternalContentPack { RootPath = Path.GetFullPath(root) };
            JObject manifest = pack.ReadJson("pack.json");
            if ((int?)manifest["schemaVersion"] != 2 || string.IsNullOrWhiteSpace((string)manifest["packId"]) ||
                string.IsNullOrWhiteSpace((string)manifest["version"]) || !(manifest["definitions"] is JArray))
                throw new InvalidDataException("内容包 pack.json 缺少必需字段或 schemaVersion 不支持。");
            pack.PackId = (string)manifest["packId"];
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var abilityIds = new HashSet<string>(StringComparer.Ordinal);
            var subscriptionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var document in pack.ReadDefinitionDocuments(manifest))
            {
                string relative = document.Key;
                var json = document.Value;
                if (json["expansionId"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)json["expansionId"]) ||
                    json.Property("replaces") == null || (json["replaces"].Type != JTokenType.Null && json["replaces"].Type != JTokenType.Object))
                    throw new InvalidDataException("内容必须填写 expansionId 和 replaces（不替换时为 null）：" + relative);
                var definition = json.ToObject<ExternalContentDefinition>();
                if (definition == null || string.IsNullOrWhiteSpace(definition.DefinitionId) || !ids.Add(definition.DefinitionId) ||
                    string.IsNullOrWhiteSpace(definition.Version) || string.IsNullOrWhiteSpace(definition.DisplayName) || definition.Data == null || definition.Abilities == null)
                    throw new InvalidDataException("内容定义缺字段或 ID 重复：" + relative);
                if (!new[] { "character", "facility", "event", "city_style", "enterprise_board", "entrepreneur_ability", "entrepreneur_round" }.Contains(definition.ContentType))
                    throw new InvalidDataException("未知内容类型：" + definition.ContentType);
                ValidateReplacement(definition);
                if (!string.IsNullOrEmpty(definition.DataTemplate))
                {
                    var template = pack.ReadJson(definition.DataTemplate);
                    if (definition.ContentType != "facility" || (string)template["contentType"] != "facility" || !(template["data"] is JObject))
                        throw new InvalidDataException("设施数据模板类型或 data 无效：" + definition.DataTemplate);
                    var data = (JObject)template["data"].DeepClone();
                    data.Merge(definition.Data, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace, MergeNullValueHandling = MergeNullValueHandling.Merge });
                    definition.Data = data;
                }
                pack.ReadArtwork(definition.Artwork);
                if (definition.Data["effectScript"] != null) pack.LoadScript((string)definition.Data["effectScript"]);
                var zones = new HashSet<string>(StringComparer.Ordinal);
                if (definition.PlayerMarkerZones == null) throw new InvalidDataException("playerMarkerZones 不能为空。");
                foreach (var zone in definition.PlayerMarkerZones)
                    if (string.IsNullOrWhiteSpace((string)zone["zoneId"]) || !zones.Add((string)zone["zoneId"]) || (int?)zone["capacity"] < 0 || zone["capacity"] == null)
                        throw new InvalidDataException("玩家标记区 ID 或容量无效：" + definition.DefinitionId);
                foreach (var ability in definition.Abilities)
                {
                    if (ability == null || string.IsNullOrWhiteSpace(ability.AbilityId) || !abilityIds.Add(ability.AbilityId) ||
                        string.IsNullOrWhiteSpace(ability.SubscriptionId) || !subscriptionIds.Add(ability.SubscriptionId) || string.IsNullOrWhiteSpace(ability.Version))
                        throw new InvalidDataException("能力 ID、订阅或版本缺失/重复：" + definition.DefinitionId);
                    if (ability.SkillType != "normal" && ability.SkillType != "persistent" && ability.SkillType != "one_shot")
                        throw new InvalidDataException("技能类型必须为 normal、persistent 或 one_shot：" + ability.AbilityId);
                    if (ability.SkillType == "persistent" && !ability.HasSpecialZone.HasValue)
                        throw new InvalidDataException("永续技能必须明确填写 hasSpecialZone：" + ability.AbilityId);
                    if (ability.HasSpecialZone == true && (ability.SkillType != "persistent" || !zones.Contains(ability.SpecialZoneId)))
                        throw new InvalidDataException("特殊区域只适用于永续技能，且必须引用本卡已定义的 zoneId：" + ability.AbilityId);
                    if (ability.HasSpecialZone != true && !string.IsNullOrEmpty(ability.SpecialZoneId))
                        throw new InvalidDataException("未启用特殊区域时 specialZoneId 必须为空：" + ability.AbilityId);
                    pack.LoadScript(ability.Script);
                    if (!string.IsNullOrEmpty(ability.CleanupScript)) pack.LoadScript(ability.CleanupScript);
                }
                if (definition.ContentType == "character" && (definition.Abilities.Count != 2 ||
                    definition.Abilities.Count(a => a.Mode == "strategy") != 1 || definition.Abilities.Count(a => a.Mode == "tactic") != 1))
                    throw new InvalidDataException("角色牌必须定义策略和计谋两个能力：" + definition.DefinitionId);
                pack.definitions.Add(definition);
            }
            foreach (var definition in pack.definitions)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal) { definition.DefinitionId };
                var currentDefinition = definition;
                while (currentDefinition.Replaces != null)
                {
                    var target = pack.definitions.FirstOrDefault(d => d.DefinitionId == currentDefinition.Replaces.DefinitionId);
                    if (target == null) break; // 允许声明另一个尚未装载扩展中的目标；此处不执行扩展替换。
                    if (target.ContentType != currentDefinition.Replaces.ContentType || target.ExpansionId != currentDefinition.Replaces.ExpansionId)
                        throw new InvalidDataException("替换目标的类型或扩展包与声明不一致：" + currentDefinition.DefinitionId);
                    if (!visited.Add(target.DefinitionId)) throw new InvalidDataException("内容替换关系存在循环：" + definition.DefinitionId);
                    currentDefinition = target;
                }
            }
            pack.ResolveActiveDefinitions(manifest["enabledExpansionIds"]);
            if (pack.activeDefinitions.Count == 0) throw new InvalidDataException("内容包没有启用的定义。");
            if (manifest["sharedArtwork"] is JObject shared)
                foreach (var item in shared.Properties()) { pack.ReadArtwork((string)item.Value); pack.sharedArtwork.Add(item.Name, (string)item.Value); }
            var hashInput = new StringBuilder();
            foreach (var item in pack.files.OrderBy(p => p.Key, StringComparer.Ordinal))
                hashInput.Append(item.Key).Append(':').Append(Convert.ToBase64String(item.Value)).Append('\n');
            pack.ContentHash = LuaContentHasher.ComputeSha256(hashInput.ToString());
            return pack;
        }

        private IEnumerable<KeyValuePair<string, JObject>> ReadDefinitionDocuments(JObject manifest)
        {
            foreach (string path in manifest["definitions"].Values<string>())
                yield return new KeyValuePair<string, JObject>(path, ReadJson(path));
            if (manifest["facilityInventory"] == null) yield break;
            string inventoryPath = (string)manifest["facilityInventory"];
            var inventory = ReadJson(inventoryPath);
            if (!(inventory["groups"] is JArray groups)) throw new InvalidDataException("设施清单必须包含 groups 数组。");
            var generated = new List<JObject>();
            foreach (var group in groups)
            {
                string prefix = (string)group["idPrefix"];
                if (string.IsNullOrWhiteSpace(prefix) || !(group["definition"] is JObject basis) ||
                    (string)basis["contentType"] != "facility" || !(group["variants"] is JArray variants))
                    throw new InvalidDataException("设施清单组缺少 idPrefix、设施 definition 或 variants。");
                var colors = new HashSet<string>(StringComparer.Ordinal);
                foreach (var variant in variants)
                {
                    string color = (string)variant["color"];
                    if (!new[] { "blue", "yellow", "red", "rainbow" }.Contains(color) || !colors.Add(color) || variant["count"]?.Type != JTokenType.Integer)
                        throw new InvalidDataException("设施颜色无效、重复或数量不是整数。");
                    int count = (int)variant["count"];
                    var instances = variant["instances"] as JArray ?? new JArray();
                    if (count < 0 || count > 128 || instances.Count > count || generated.Count + count > 512)
                        throw new InvalidDataException("设施数量无效、少于显式实例数或超出清单上限。");
                    for (int i = 0; i < count; i++)
                    {
                        var entry = i < instances.Count ? instances[i] : null;
                        string id = entry == null ? prefix + "_" + color + "_" + (i + 1).ToString("000", System.Globalization.CultureInfo.InvariantCulture) : (string)entry["id"];
                        var definition = (JObject)basis.DeepClone();
                        definition["definitionId"] = id;
                        definition["artwork"] = (string)entry?["artwork"] ?? (string)variant["artwork"];
                        var data = definition["data"] as JObject ?? new JObject();
                        data["color"] = color; data["facilityId"] = id; data["manifestId"] = id;
                        definition["data"] = data;
                        generated.Add(definition);
                    }
                }
            }
            foreach (var definition in generated.OrderBy(d => (string)d["definitionId"], StringComparer.Ordinal))
                yield return new KeyValuePair<string, JObject>(inventoryPath + ":" + (string)definition["definitionId"], definition);
        }

        // 替换的是稳定牌组槽位，来源 definitionId 保留以便追踪扩展；不由加载顺序决定胜者。
        private void ResolveActiveDefinitions(JToken selection)
        {
            HashSet<string> expansions = null;
            if (selection != null)
            {
                if (!(selection is JArray values) || values.Any(v => v.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)v)))
                    throw new InvalidDataException("enabledExpansionIds 必须为非空字符串数组。");
                expansions = new HashSet<string>(values.Values<string>(), StringComparer.Ordinal);
                if (expansions.Count != values.Count || expansions.Any(id => !definitions.Any(d => d.ExpansionId == id)))
                    throw new InvalidDataException("启用扩展 ID 重复或未在内容清单中定义。");
            }
            var selected = definitions.Where(d => d.Enabled && (expansions == null || expansions.Contains(d.ExpansionId))).ToList();
            var byId = selected.ToDictionary(d => d.DefinitionId, StringComparer.Ordinal);
            var replacements = new Dictionary<string, ExternalContentDefinition>(StringComparer.Ordinal);
            foreach (var definition in selected)
            {
                if (definition.ContentType.StartsWith("enterprise", StringComparison.Ordinal) || definition.ContentType.StartsWith("entrepreneur", StringComparison.Ordinal))
                    throw new InvalidDataException("该内容类型尚未接入对局，只能作为未启用的定义示例：" + definition.ContentType);
                if (definition.Replaces == null) continue;
                if (!byId.ContainsKey(definition.Replaces.DefinitionId))
                    throw new InvalidDataException("启用替换项必须同时加载并启用其目标扩展与定义：" + definition.DefinitionId);
                if (replacements.ContainsKey(definition.Replaces.DefinitionId))
                    throw new InvalidDataException("多个启用内容替换同一目标，必须明确选择扩展：" + definition.Replaces.DefinitionId);
                replacements.Add(definition.Replaces.DefinitionId, definition);
            }
            foreach (var root in selected.Where(d => d.Replaces == null))
            {
                var leaf = root;
                runtimeIds.Add(leaf.DefinitionId, root.DefinitionId);
                while (replacements.TryGetValue(leaf.DefinitionId, out var next))
                {
                    leaf = next;
                    runtimeIds.Add(leaf.DefinitionId, root.DefinitionId);
                }
                leaf.RuntimeId = root.DefinitionId;
                activeDefinitions.Add(leaf);
            }
        }

        private ExternalContentDefinition FindActiveDefinition(string type, string id)
        {
            if (!runtimeIds.TryGetValue(id ?? "", out var runtimeId)) return null;
            return activeDefinitions.FirstOrDefault(d => d.ContentType == type && d.RuntimeId == runtimeId);
        }

        private static void ValidateReplacement(ExternalContentDefinition definition)
        {
            var replacement = definition.Replaces;
            if (replacement == null) return;
            if (string.IsNullOrWhiteSpace(replacement.ExpansionId) || string.IsNullOrWhiteSpace(replacement.DefinitionId) ||
                replacement.ContentType != definition.ContentType || replacement.DefinitionId == definition.DefinitionId)
                throw new InvalidDataException("替换必须引用同类资源，明确目标扩展包和 ID，且不能替换自身：" + definition.DefinitionId);
        }

        public string ResolvePath(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("内容资源必须使用包内相对路径。");
            string result = Path.GetFullPath(Path.Combine(RootPath, relative));
            string prefix = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("内容路径越出包目录：" + relative);
            return result;
        }
        private byte[] ReadFile(string relative)
        {
            if (files.TryGetValue(relative, out var bytes)) return bytes;
            string path = ResolvePath(relative);
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("内容文件不存在或超过 16 MiB：" + relative);
            bytes = File.ReadAllBytes(path); files.Add(relative, bytes); return bytes;
        }
        private JObject ReadJson(string relative)
        {
            return JObject.Parse(Encoding.UTF8.GetString(ReadFile(relative)), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        private void ReadArtwork(string relative)
        {
            string ext = Path.GetExtension(relative ?? "").ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") throw new InvalidDataException("贴图必须为 PNG 或 JPEG：" + relative);
            ReadFile(relative);
        }
        private void LoadScript(string relative)
        {
            if (scripts.ContainsKey(relative)) return;
            if (Path.GetExtension(relative) != ".lua") throw new InvalidDataException("脚本必须为 .lua：" + relative);
            string source = Encoding.UTF8.GetString(ReadFile(relative));
            new Script(CoreModules.None).LoadString(source, null, relative); // 只编译，不运行定义中的规则。
            scripts.Add(relative, source);
        }
        public string GetContentScript(string type, string id)
        {
            var definition = FindActiveDefinition(type, id) ?? throw new InvalidDataException("未启用的内容：" + id);
            return GetScript((string)definition.Data["effectScript"]);
        }
        public string GetFacilityScript(string effectId)
        {
            var matching = activeDefinitions.Where(d => d.ContentType == "facility" && (string)d.Data["effectId"] == effectId).ToList();
            if (matching.Count == 0) throw new InvalidDataException("设施效果没有外部定义：" + effectId);
            string path = (string)matching[0].Data["effectScript"];
            if (matching.Any(d => (string)d.Data["effectScript"] != path)) throw new InvalidDataException("相同设施 effectId 的脚本引用必须一致：" + effectId);
            return GetScript(path);
        }
        public string GetScript(string relative) => scripts.TryGetValue(relative, out var source) ? source : throw new InvalidDataException("未登记脚本：" + relative);
        public byte[] GetArtworkBytes(string relative) => (byte[])ReadFile(relative).Clone();
        public string FindArtwork(string type, string id) => FindActiveDefinition(type, id)?.Artwork;
        public string FindSharedArtwork(string id) => sharedArtwork.TryGetValue(id, out var path) ? path : null;
        public IReadOnlyList<CharacterCardDefinition> CreateCharacters() => activeDefinitions.Where(d => d.ContentType == "character").Select(d =>
        {
            var value = d.Data.ToObject<CharacterCardDefinition>();
            value.TemplateId = d.RuntimeId; value.CardId = d.RuntimeId; value.Name = d.DisplayName;
            value.ConfiguredStrategyAbilityId = d.Abilities.Single(a => a.Mode == "strategy").AbilityId;
            value.ConfiguredTacticAbilityId = d.Abilities.Single(a => a.Mode == "tactic").AbilityId;
            value.IsExternalDefinition = true;
            if (value.StrategyEffect == CharacterCardEffectKind.Unsupported) value.StrategyEffect = CharacterCardEffectKind.External;
            if (value.TacticEffect == CharacterCardEffectKind.Unsupported) value.TacticEffect = CharacterCardEffectKind.External;
            return value;
        }).ToList().AsReadOnly();
        public IReadOnlyList<EventCardDefinition> CreateEvents() => activeDefinitions.Where(d => d.ContentType == "event").Select(d =>
        {
            var value = d.Data.ToObject<EventCardDefinition>(); value.CardId = d.RuntimeId; value.Name = d.DisplayName; return value;
        }).ToList().AsReadOnly();
        public IReadOnlyList<FacilityCardDefinition> CreateFacilities() => activeDefinitions.Where(d => d.ContentType == "facility").Select(d =>
        {
            var value = d.Data.ToObject<FacilityCardDefinition>(); value.FacilityId = d.RuntimeId; value.ManifestId = d.RuntimeId; value.Name = d.DisplayName; return value;
        }).ToList().AsReadOnly();
        public IReadOnlyList<CityStyleDefinition> CreateCityStyles() => activeDefinitions.Where(d => d.ContentType == "city_style").Select(d =>
        {
            var value = d.Data.ToObject<CityStyleDefinition>(); value.CityStyleId = d.RuntimeId; value.Name = d.DisplayName;
            value.SpecialActionId = (string)definitions.Single(r => r.DefinitionId == d.RuntimeId).Data["specialActionId"] ?? "";
            return value;
        }).ToList().AsReadOnly();
        public IReadOnlyList<SpecialActionDefinition> CreateSpecialActions() => activeDefinitions.Where(d => d.ContentType == "city_style" && d.Data["specialAction"]?.Type == JTokenType.Object).Select(d =>
        {
            var value = d.Data["specialAction"].ToObject<SpecialActionDefinition>(); value.CityStyleId = d.RuntimeId;
            value.SpecialActionId = (string)definitions.Single(r => r.DefinitionId == d.RuntimeId).Data["specialActionId"] ?? "";
            return value;
        }).ToList().AsReadOnly();
    }
}
