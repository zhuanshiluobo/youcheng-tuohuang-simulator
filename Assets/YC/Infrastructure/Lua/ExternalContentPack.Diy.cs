using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YC.Infrastructure.Lua
{
    public sealed partial class ExternalContentPack
    {
        private ExternalContentPack officialPreparationOrigin;
        private JObject officialPreparationDeclarations;

        private JObject ReadOfficialPreparationDeclarations()
        {
            var owner = officialPreparationOrigin ?? this;
            lock (owner)
            {
                if (owner.officialPreparationDeclarations == null)
                {
                    string path = owner.ResolvePath("preparation/official-v1.json");
                    if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024)
                        throw new InvalidDataException("official_preparation_declaration_missing");
                    owner.officialPreparationDeclarations = JObject.Parse(File.ReadAllText(path, Encoding.UTF8),
                        new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                }
                return (JObject)owner.officialPreparationDeclarations.DeepClone();
            }
        }

        // 官方新合同使用单独版本的模式声明；旧包清单字节和旧档内容身份保持不变。
        internal void ApplyOfficialPreparation(JObject manifest, YC.Domain.Rules.GameRuleConfiguration configuration)
        {
            if (configuration.OfficialPreparationContract == null) return;
            var declarations = ReadOfficialPreparationDeclarations();
            if (declarations.Properties().Any(property => !new[] { "schemaVersion", "version", "modes" }.Contains(property.Name)) ||
                (int?)declarations["schemaVersion"] != 1 || (string)declarations["version"] != configuration.OfficialPreparationContract ||
                !(declarations["modes"] is JArray modes) || modes.Any(item => !(item is JObject)))
                throw new InvalidDataException("official_preparation_declaration_invalid");
            var selected = configuration.ActiveRuleModeIds.Select(id => (manifest["ruleModes"] as JArray)?.OfType<JObject>()
                .SingleOrDefault(item => (string)item["modeId"] == id) ?? throw new InvalidDataException("official_preparation_mode_missing:" + id)).ToArray();
            bool entrepreneurProvided = selected.Any(mode => (string)mode["modeId"] == "entrepreneur" ||
                ((mode["preparation"] as JObject)?["betweenDistributionAndConfirmation"] as JArray ?? new JArray())
                    .OfType<JObject>().Any(step => (string)step["kind"] == "builtin" && (string)step["handlerId"] == YC.Domain.Rules.EntrepreneurFlow.PreparationHandlerId));
            string entrepreneurFallback = entrepreneurProvided ? null : selected
                .Where(mode => mode["preparation"] == null && (string)mode["modeId"] != "standard" && (string)mode["modeId"] != "entrepreneur" &&
                    GameplayMechanismCatalog.ModeMechanisms(mode).Contains("entrepreneur_selection.v1"))
                .OrderBy(mode => mode["preparationOrder"] == null ? 0 : DiyPackManifest.Integer(mode, "preparationOrder", (string)mode["modeId"]))
                .ThenBy(mode => (string)mode["modeId"], StringComparer.Ordinal).Select(mode => (string)mode["modeId"]).FirstOrDefault();
            foreach (var mode in selected)
            {
                string modeId = (string)mode["modeId"];
                bool official = modeId == "standard" || modeId == "entrepreneur";
                if (mode["preparation"] != null)
                {
                    if (official) throw new InvalidDataException("official_preparation_mode_conflict");
                    continue;
                }
                // 只有基础模式继承普通准备；共享企业家机制由唯一来源提供，避免重复选择。
                if (!official && modeId != configuration.RuleModeId && modeId != entrepreneurFallback) continue;
                string templateMode = official ? modeId : modeId == entrepreneurFallback ? "entrepreneur" : "standard";
                var variants = modes.OfType<JObject>().Where(item => (string)item["modeId"] == templateMode).ToArray();
                foreach (var variant in variants)
                {
                    if (variant.Properties().Any(property => !new[] { "modeId", "requires", "excludes", "preparation" }.Contains(property.Name)) ||
                        !(variant["requires"] is JArray required) || required.Count == 0 ||
                        required.Any(item => item.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)item)) ||
                        required.Values<string>().Distinct(StringComparer.Ordinal).Count() != required.Count ||
                        variant["excludes"] != null && (!(variant["excludes"] is JArray excluded) ||
                            excluded.Any(item => item.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)item)) ||
                            excluded.Values<string>().Distinct(StringComparer.Ordinal).Count() != excluded.Count) ||
                        !(variant["preparation"] is JObject))
                        throw new InvalidDataException("official_preparation_mode_invalid:" + templateMode);
                }
                var matching = variants.Where(item => ((JArray)item["requires"]).Values<string>().All(configuration.EnabledExpansionIds.Contains) &&
                    !(item["excludes"] as JArray ?? new JArray()).Values<string>().Any(configuration.EnabledExpansionIds.Contains)).ToArray();
                if (matching.Length != 1) throw new InvalidDataException("official_preparation_mode_missing:" + modeId);
                var preparation = (JObject)matching[0]["preparation"];
                DiyPreparationDeclaration.Parse(preparation, modeId, "official.preparation");
                mode["preparation"] = preparation.DeepClone();
            }
        }

        /// <summary>
        /// 从已冻结来源构造新的候选快照并重走正式内容加载器。失败不会替换当前包。
        /// 包内文件映射为带来源的虚拟路径，原包和磁盘内容均不改写。
        /// </summary>
        public static ExternalContentPack ComposeDiy(ExternalContentPack core, DiyResolvedContent resolved)
        {
            if (core == null || resolved == null) throw new ArgumentNullException();
            if (core.PackId != "core" || core.SchemaVersion != 3 || core.DiyComposition != null)
                throw new InvalidDataException("组合来源必须是独立的 schema 3 core 快照");
            if ((string)core.Manifest["version"] != resolved.CoreVersion || core.ContentHash != resolved.CoreHash)
                throw new InvalidDataException("core 的版本或内容身份与包锁定清单不一致");
            if (resolved.Packs.Count == 0) return core;
            var composite = DiyCompositeCatalog.Build(core, resolved); // 先验证已选集合的来源、替换链和冲突。
            var additions = composite.BuildEventAdditions(resolved.MapId);
            ValidateDiyModuleOrigins(core, resolved);
            var input = new Dictionary<string, byte[]>(core.files, StringComparer.Ordinal);
            var manifest = core.Manifest;
            // core 来源的准备脚本可能没有被其他定义引用；组合快照也须冻结这部分输入。
            var preparations = resolved.Packs.SelectMany(locked => locked.Snapshot.Manifest.RuleModes)
                .Where(mode => resolved.ModeIds.Contains(mode.ModeId) && mode.Preparation != null).Select(mode => mode.Preparation).ToList();
            foreach (var coreMode in (manifest["ruleModes"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(mode => resolved.ModeIds.Contains((string)mode["modeId"]) && mode["preparation"] is JObject))
                preparations.Add(DiyPreparationDeclaration.Parse((JObject)coreMode["preparation"], (string)coreMode["modeId"], "ruleMode.preparation"));
            foreach (var preparation in preparations)
                foreach (var step in preparation.UnresolvedSteps.Where(value => value.SourcePackId == "core" && value.Kind == "lua"))
                {
                    if (step.SourcePackVersion != resolved.CoreVersion)
                        throw new InvalidDataException("准备步骤来源版本不匹配：" + step.StepId);
                    foreach (string path in new[] { step.ScriptPath, step.Completion?.ScriptPath }.Where(value => !string.IsNullOrEmpty(value)))
                    {
                        EventChoiceContract.ValidateReferencedScript(path, "core", "preparation[" + step.StepId + "]",
                            relative => Encoding.UTF8.GetString(core.ReadFile(relative)));
                        input[path] = core.ReadFile(path);
                    }
                }
            var definitions = (JArray)manifest["definitions"];
            var expansions = (JArray)manifest["expansionDependencies"];
            if (!(manifest["ruleModes"] is JArray)) manifest["ruleModes"] = new JArray();
            if (!(manifest["luaModules"] is JArray)) manifest["luaModules"] = new JArray();
            if (!(manifest["scriptDependencies"] is JArray)) manifest["scriptDependencies"] = new JArray();
            var modes = (JArray)manifest["ruleModes"];
            var modules = (JArray)manifest["luaModules"];
            var bindings = (JArray)manifest["scriptDependencies"];
            var ruleBindings = new JArray(); manifest["diyRuleBindings"] = ruleBindings;
            var presentations = new JArray(); manifest["diyPresentations"] = presentations;
            var scriptOrigins = new Dictionary<string, (string PackId, string Relative)>(StringComparer.Ordinal);
            var definitionSources = new Dictionary<string, string>(StringComparer.Ordinal);
            JObject sliceCatalog = null;
            string slicePath = (string)manifest["artworkSlices"];
            if (!string.IsNullOrEmpty(slicePath)) sliceCatalog = JObject.Parse(Encoding.UTF8.GetString(input[slicePath]));
            foreach (var locked in resolved.Packs)
            {
                var source = locked.Snapshot; var declaration = source.Manifest.Source;
                string Mount(string relative)
                {
                    relative = DiyPackPaths.ValidateRelative(relative);
                    if (!source.Files.Contains(relative)) throw new InvalidDataException(locked.PackId + "/" + relative + ": 引用文件不存在");
                    return relative.StartsWith("lua/", StringComparison.Ordinal)
                        ? "lua/.diy/" + locked.PackId + "/" + locked.Version + "/" + relative.Substring(4)
                        : ".diy/" + locked.PackId + "/" + locked.Version + "/" + relative;
                }
                foreach (string path in source.Files.Paths)
                {
                    string mounted = Mount(path);
                    if (input.ContainsKey(mounted)) throw new InvalidDataException("虚拟资源路径冲突：" + mounted);
                    input.Add(mounted, source.Files.ReadBytes(path));
                    if (path.EndsWith(".lua", StringComparison.Ordinal)) scriptOrigins.Add(mounted, (locked.PackId, path));
                }
                if (declaration["presentation"] is JObject rawPresentation)
                {
                    var presentation = (JObject)rawPresentation.DeepClone();
                    if (!(presentation["texts"] is JArray texts) || presentation["icons"]?.Type != JTokenType.String || presentation["defaults"]?.Type != JTokenType.String)
                        throw new InvalidDataException(locked.PackId + ":presentation 需要 texts、icons 和 defaults");
                    string RewritePresentationDocument(string path)
                    {
                        var document = source.Files.ReadJson(path);
                        if (document["assets"] is JObject assets)
                            foreach (var asset in assets.Properties().ToArray())
                            {
                                if (asset.Value.Type != JTokenType.String) throw new InvalidDataException(locked.PackId + "/" + path + ": 资源路径无效");
                                string original = (string)asset.Value;
                                DiyImageHeader.Validate(source.Files.ReadBytes(original), original);
                                asset.Value = Mount(original);
                            }
                        RewritePaths(document, Mount);
                        string mounted = Mount(path); input[mounted] = Encoding.UTF8.GetBytes(document.ToString(Formatting.None)); return mounted;
                    }
                    var mountedTexts = new JArray();
                    foreach (var item in texts)
                    {
                        if (item.Type != JTokenType.String) throw new InvalidDataException(locked.PackId + ":presentation.texts 必须为路径数组");
                        mountedTexts.Add(RewritePresentationDocument((string)item));
                    }
                    presentation["texts"] = mountedTexts;
                    presentation["icons"] = RewritePresentationDocument((string)presentation["icons"]);
                    presentation["defaults"] = RewritePresentationDocument((string)presentation["defaults"]);
                    presentations.Add(new JObject { ["packId"] = locked.PackId, ["presentation"] = presentation });
                }
                foreach (var document in source.Definitions)
                {
                    string mounted = Mount(document.RelativePath);
                    var body = document.Source;
                    RewritePaths(body, Mount);
                    input[mounted] = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
                    definitions.Add(mounted);
                    if (definitionSources.ContainsKey(document.DefinitionId)) throw new InvalidDataException("跨包定义 ID 冲突：" + document.DefinitionId);
                    definitionSources.Add(document.DefinitionId, locked.PackId);
                }
                foreach (var expansion in source.Manifest.Expansions)
                {
                    if (expansions.OfType<JObject>().Any(x => (string)x["expansionId"] == expansion.ExpansionId))
                        throw new InvalidDataException("扩展身份跨包冲突：" + expansion.ExpansionId);
                    expansions.Add(new JObject { ["expansionId"] = expansion.ExpansionId, ["requires"] = new JArray(expansion.Requires),
                        ["conflicts"] = new JArray(expansion.Conflicts), ["requiredCapabilities"] = new JArray(expansion.RequiredCapabilities),
                        ["applicableModes"] = new JArray(expansion.ApplicableModes) });
                }
                foreach (var mode in (declaration["ruleModes"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    if (modes.OfType<JObject>().Any(x => (string)x["modeId"] == (string)mode["modeId"]))
                        throw new InvalidDataException("模式身份跨包冲突：" + (string)mode["modeId"]);
                    var copy = (JObject)mode.DeepClone();
                    if (copy["requires"] == null) copy["requires"] = new JArray();
                    if (copy["requiredCapabilities"] == null) copy["requiredCapabilities"] = new JArray();
                    modes.Add(copy);
                }
                void AddRuleBindings(string scope, IEnumerable<DiyRuleBinding> entries)
                {
                    foreach (var entry in entries)
                    {
                        var record = new JObject { ["packId"] = locked.PackId, ["scopeId"] = scope, ["bindingId"] = entry.BindingId,
                            ["eventType"] = entry.EventType, ["script"] = Mount(entry.Script), ["version"] = entry.Version };
                        if (!string.IsNullOrEmpty(entry.CandidateKind)) record["candidateKind"] = entry.CandidateKind;
                        if (entry.EventType == "EffectCompleted")
                        {
                            record["completedEffectTypes"] = new JArray(entry.CompletedEffectTypes);
                            record["completionPolicy"] = entry.CompletionPolicy;
                        }
                        if (!string.IsNullOrEmpty(entry.CompletionHandlerId))
                            record["completion"] = new JObject { ["handlerId"] = entry.CompletionHandlerId, ["script"] = Mount(entry.CompletionScript), ["version"] = entry.CompletionVersion };
                        ruleBindings.Add(record);
                    }
                }
                foreach (var mode in source.Manifest.RuleModes.Where(x => resolved.ModeIds.Contains(x.ModeId))) AddRuleBindings(mode.ModeId, mode.RuleBindings);
                foreach (var expansion in source.Manifest.Expansions.Where(x => resolved.ExpansionIds.Contains(x.ExpansionId))) AddRuleBindings(expansion.ExpansionId, expansion.RuleBindings);
                foreach (var token in declaration["luaModules"] as JArray ?? new JArray())
                {
                    if (!(token is JObject module)) throw new InvalidDataException(locked.PackId + ":luaModules 项必须是对象");
                    var copy = (JObject)module.DeepClone();
                    DiyIdentifiers.RequireOwned((string)copy["moduleId"], locked.PackId, locked.PackId + ":luaModules.moduleId");
                    copy["script"] = Mount((string)copy["script"]); modules.Add(copy);
                }
                foreach (var token in declaration["scriptDependencies"] as JArray ?? new JArray())
                {
                    if (!(token is JObject binding)) throw new InvalidDataException(locked.PackId + ":scriptDependencies 项必须是对象");
                    var copy = (JObject)binding.DeepClone(); copy["script"] = Mount((string)copy["script"]); bindings.Add(copy);
                }
                if (declaration["artworkSlices"] != null)
                {
                    if (sliceCatalog == null || string.IsNullOrEmpty(slicePath)) throw new InvalidDataException("core 缺少可组合的切片目录");
                    string file = (string)declaration["artworkSlices"];
                    var slices = source.Files.ReadJson(file);
                    if ((int?)slices["schemaVersion"] != 1 || (string)slices["coordinateOrigin"] != "bottom-left" ||
                        !(slices["sheets"] is JArray sheets) || !(slices["slices"] is JArray entries))
                        throw new InvalidDataException(locked.PackId + "/" + file + ": 切片格式无效");
                    foreach (var token in sheets)
                    {
                        if (!(token is JObject sheet)) throw new InvalidDataException(locked.PackId + ": 切片整图格式无效");
                        var copy = (JObject)sheet.DeepClone(); DiyIdentifiers.RequireOwned((string)copy["id"], locked.PackId, locked.PackId + ":sheets.id");
                        copy["artwork"] = Mount((string)copy["artwork"]); ((JArray)sliceCatalog["sheets"]).Add(copy);
                    }
                    foreach (var token in entries)
                    {
                        if (!(token is JObject entry)) throw new InvalidDataException(locked.PackId + ": 切片格式无效");
                        DiyIdentifiers.RequireOwned((string)entry["id"], locked.PackId, locked.PackId + ":slices.id");
                        DiyIdentifiers.RequireOwned((string)entry["sheetId"], locked.PackId, locked.PackId + ":slices.sheetId");
                        ((JArray)sliceCatalog["slices"]).Add(entry.DeepClone());
                    }
                }
            }
            if (sliceCatalog != null) input[slicePath] = Encoding.UTF8.GetBytes(sliceCatalog.ToString(Formatting.None));
            input["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToString(Formatting.None));
            var candidate = LoadCombined(core.RootPath, Array.Empty<string>(), "", input, core);
            candidate.DiyComposition = resolved;
            candidate.officialPreparationOrigin = core.officialPreparationOrigin ?? core;
            candidate.diyAppendEntries.AddRange(additions.Select(YC.Domain.State.GameStateCloneService.DeepClone));
            foreach (var pair in scriptOrigins) candidate.mountedScriptOrigins.Add(pair.Key, pair.Value);
            foreach (var pair in definitionSources) candidate.definitionOrigins.Add(pair.Key, pair.Value);
            return candidate;
        }

        private static void RewritePaths(JToken token, Func<string, string> mount)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties().ToArray())
                {
                    if (property.Value.Type == JTokenType.String &&
                        new[] { "artwork", "script", "cleanupScript", "effectScript", "presentationScript", "detailScript", "dataTemplate" }.Contains(property.Name) &&
                        !string.IsNullOrEmpty((string)property.Value)) property.Value = mount((string)property.Value);
                    else RewritePaths(property.Value, mount);
                }
            }
            else if (token is JArray array) foreach (var item in array) RewritePaths(item, mount);
        }

        private static void ValidateDiyModuleOrigins(ExternalContentPack core, DiyResolvedContent resolved)
        {
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            void AddModules(string owner, JArray items)
            {
                foreach (var item in items ?? new JArray())
                {
                    if (!(item is JObject module) || module["moduleId"]?.Type != JTokenType.String || module["version"]?.Type != JTokenType.String)
                        throw new InvalidDataException(owner + ": Lua 模块身份无效");
                    string key = (string)module["moduleId"] + "\n" + (string)module["version"];
                    if (owners.ContainsKey(key)) throw new InvalidDataException("Lua 模块身份跨包冲突：" + key);
                    owners.Add(key, owner);
                }
            }
            AddModules("core", core.Manifest["luaModules"] as JArray);
            foreach (var locked in resolved.Packs) AddModules(locked.PackId, locked.Snapshot.Manifest.Source["luaModules"] as JArray);
            foreach (var locked in resolved.Packs)
                foreach (var item in locked.Snapshot.Manifest.Source["scriptDependencies"] as JArray ?? new JArray())
                {
                    if (!(item is JObject binding) || !(binding["modules"] is JArray references))
                        throw new InvalidDataException(locked.PackId + ": Lua 模块依赖格式无效");
                    foreach (var token in references)
                    {
                        if (!(token is JObject reference) || reference["moduleId"]?.Type != JTokenType.String || reference["version"]?.Type != JTokenType.String)
                            throw new InvalidDataException(locked.PackId + ": Lua 模块引用身份无效");
                        string key = (string)reference["moduleId"] + "\n" + (string)reference["version"];
                        if (!owners.TryGetValue(key, out string owner)) throw new InvalidDataException(locked.PackId + ": Lua 模块不存在：" + key);
                        if (!DiyCompositeCatalog.InDependencyClosure(locked.PackId, owner, resolved))
                            throw new InvalidDataException(locked.PackId + ": Lua 模块来源不在依赖闭包中：" + owner);
                    }
                }
        }
    }
}
