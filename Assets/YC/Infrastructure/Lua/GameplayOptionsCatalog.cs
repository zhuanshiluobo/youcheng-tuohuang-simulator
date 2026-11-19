using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Maps;
using YC.Domain.Rules;

namespace YC.Infrastructure.Lua
{
    public sealed class GameplayModeOption
    {
        public string Id { get; internal set; }
        public string DisplayName { get; internal set; }
        public string Description { get; internal set; }
        public string PackId { get; internal set; }
        public string PackVersion { get; internal set; }
        public IReadOnlyList<string> Requires { get; internal set; }
        public IReadOnlyList<string> RequiredCapabilities { get; internal set; }
        public IReadOnlyList<string> Conflicts { get; internal set; }
        public int? PreparationOrder { get; internal set; }
        public IReadOnlyList<string> ApplicableMapIds { get; internal set; }
        public IReadOnlyList<string> Mechanisms { get; internal set; }
        public string UnavailableReason { get; internal set; }
        public bool Available => string.IsNullOrEmpty(UnavailableReason);
    }

    public sealed class GameplayExpansionOption
    {
        public string Id { get; internal set; }
        public string DisplayName { get; internal set; }
        public string Description { get; internal set; }
        public string PackId { get; internal set; }
        public string PackVersion { get; internal set; }
        public IReadOnlyList<string> Requires { get; internal set; }
        public IReadOnlyList<string> RequiredCapabilities { get; internal set; }
        public IReadOnlyList<string> Conflicts { get; internal set; }
        public IReadOnlyList<string> ApplicableModes { get; internal set; }
        public string UnavailableReason { get; internal set; }
        public bool Available => string.IsNullOrEmpty(UnavailableReason);
    }

    public sealed class GameplayModeTransition
    {
        public IReadOnlyList<string> ExpansionIds { get; internal set; }
        public IReadOnlyList<string> RequiredExpansionIds { get; internal set; }
        public IReadOnlyList<string> RemovedExpansionIds { get; internal set; }
    }

    /// <summary>基础和 DIY 玩法同列；包与版本仅作为来源与诊断，不作为房间玩法。</summary>
    public sealed class GameplayOptionsCatalog : IGameRuleCatalog
    {
        public IReadOnlyList<GameplayModeOption> Modes { get; private set; }
        public IReadOnlyList<GameplayExpansionOption> Expansions { get; private set; }
        public IReadOnlyList<string> Diagnostics { get; private set; }

        public static GameplayOptionsCatalog CoreOnly(ExternalContentPack core, string diagnostic = "") =>
            Create(core, Array.Empty<DiyPackSnapshot>(), string.IsNullOrEmpty(diagnostic) ? new List<string>() : new List<string> { diagnostic });

        internal static GameplayOptionsCatalog Create(ExternalContentPack core, IReadOnlyList<DiyPackSnapshot> packs, List<string> diagnostics)
        {
            if (core == null || packs == null || diagnostics == null) throw new ArgumentNullException();
            var modes = new List<GameplayModeOption>(); var expansions = new List<GameplayExpansionOption>();
            var manifest = core.Manifest; string coreVersion = (string)manifest["version"];
            var mechanismCatalog = GameplayMechanismCatalog.Read(manifest);
            string[] Strings(JObject item, string name)
            {
                if (item[name] == null) return Array.Empty<string>();
                if (!(item[name] is JArray array) || array.Any(x => x.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)x)))
                    throw new InvalidDataException("玩法目录字段无效：" + name);
                var values = array.Values<string>().ToArray();
                if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new InvalidDataException("玩法目录身份重复：" + name);
                return values;
            }
            IReadOnlyList<string> ReadOnly(IEnumerable<string> value) => Array.AsReadOnly(value.ToArray());
            string Missing(IEnumerable<string> capabilities)
            {
                string missing = RuleModeReadiness.Missing(capabilities);
                return missing.Length == 0 ? "" : "需要未实现的能力：" + missing;
            }
            string Label(JObject item, string name, string fallback) => item[name]?.Type == JTokenType.String ? (string)item[name] : fallback;
            (string[] Needs, string[] Capabilities, string Problem) ModeRequirements(IEnumerable<string> needs, IEnumerable<string> mechanismIds, IEnumerable<string> capabilities)
            {
                var all = new HashSet<string>(needs, StringComparer.Ordinal); var caps = new List<string>(capabilities);
                foreach (string id in mechanismIds)
                {
                    if (!mechanismCatalog.TryGetValue(id, out var mechanism)) return (all.ToArray(), caps.ToArray(), "未知玩法机制：" + id);
                    all.UnionWith(mechanism.Requires); caps.AddRange(mechanism.RequiredCapabilities);
                }
                return (all.OrderBy(x => x, StringComparer.Ordinal).ToArray(), caps.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(), Missing(caps));
            }
            foreach (var item in (manifest["ruleModes"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string id = (string)item["modeId"];
                if (string.IsNullOrEmpty(id)) throw new InvalidDataException("基础模式身份缺失");
                var mechanismIds = GameplayMechanismCatalog.ModeMechanisms(item);
                var modeCapabilities = Strings(item, "requiredCapabilities").AsEnumerable();
                // 新准备合同只有完整实现后才登记能力，不能因为模式省略能力字段就静默运行旧流程。
                if (item["preparation"] != null) modeCapabilities = modeCapabilities.Concat(new[] { DiyPreparationDeclaration.Capability });
                if (item["preparationOrder"] != null || Strings(item, "conflicts").Length > 0)
                    modeCapabilities = modeCapabilities.Concat(new[] { "diy.multi_rule_modes.v1" });
                var requirements = ModeRequirements(Strings(item, "requires"), mechanismIds, modeCapabilities);
                modes.Add(new GameplayModeOption { Id = id, DisplayName = Label(item, "displayName", id), Description = Label(item, "description", ""),
                    PackId = "core", PackVersion = coreVersion, Requires = ReadOnly(requirements.Needs), RequiredCapabilities = ReadOnly(requirements.Capabilities), Mechanisms = ReadOnly(mechanismIds),
                    Conflicts = ReadOnly(Strings(item, "conflicts")), PreparationOrder = item["preparationOrder"] != null ? DiyPackManifest.Integer(item, "preparationOrder", id) :
                        (item["preparation"] is JObject inline && DiyPreparationDeclaration.Parse(inline, id, id).HasSteps || item["preparation"] == null && mechanismIds.Contains("entrepreneur_selection.v1") ? 0 : (int?)null),
                    ApplicableMapIds = ReadOnly(Strings(item, "applicableMapIds")), UnavailableReason = requirements.Problem });
            }
            foreach (var item in (manifest["expansionDependencies"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string id = (string)item["expansionId"];
                if (string.IsNullOrEmpty(id)) throw new InvalidDataException("基础扩展身份缺失");
                expansions.Add(new GameplayExpansionOption { Id = id, DisplayName = Label(item, "displayName", id), Description = Label(item, "description", ""),
                    PackId = "core", PackVersion = coreVersion, Requires = ReadOnly(Strings(item, "requires")), RequiredCapabilities = ReadOnly(Strings(item, "requiredCapabilities")), Conflicts = ReadOnly(Strings(item, "conflicts")),
                    ApplicableModes = ReadOnly(Strings(item, "applicableModes")), UnavailableReason = Missing(Strings(item, "requiredCapabilities")) });
            }
            var byPack = packs.ToDictionary(x => x.Manifest.PackId, StringComparer.Ordinal);
            string PackProblem(string id, HashSet<string> visiting)
            {
                if (!visiting.Add(id)) return "包依赖存在循环：" + id;
                if (!byPack.TryGetValue(id, out var pack)) return "缺少依赖包：" + id;
                string problem = Missing(pack.Manifest.RequiredCapabilities);
                if (problem.Length == 0)
                    foreach (var dependency in pack.Manifest.Dependencies)
                    {
                        string actual = dependency.PackId == "core" ? coreVersion :
                            byPack.TryGetValue(dependency.PackId, out var found) ? found.Manifest.Version : null;
                        if (actual == null) { problem = "缺少依赖包：" + dependency.PackId; break; }
                        if (!dependency.Accepts(actual)) { problem = "包依赖版本不兼容：" + dependency.PackId; break; }
                        if (dependency.PackId != "core")
                        {
                            problem = PackProblem(dependency.PackId, visiting);
                            if (problem.Length > 0) break;
                        }
                    }
                visiting.Remove(id); return problem;
            }
            foreach (var pack in packs.OrderBy(x => x.Manifest.PackId, StringComparer.Ordinal))
            {
                string problem = PackProblem(pack.Manifest.PackId, new HashSet<string>(StringComparer.Ordinal));
                foreach (var mode in pack.Manifest.RuleModes)
                {
                    var requirements = ModeRequirements(mode.Requires, mode.Mechanisms, mode.EffectiveRequiredCapabilities);
                    modes.Add(new GameplayModeOption { Id = mode.ModeId, DisplayName = mode.DisplayName, Description = mode.Description,
                        PackId = pack.Manifest.PackId, PackVersion = pack.Manifest.Version, Requires = ReadOnly(requirements.Needs), RequiredCapabilities = ReadOnly(requirements.Capabilities), Mechanisms = ReadOnly(mode.Mechanisms),
                        Conflicts = ReadOnly(mode.Conflicts), PreparationOrder = mode.PreparationOrder,
                        ApplicableMapIds = ReadOnly(mode.ApplicableMapIds), UnavailableReason = problem.Length > 0 ? problem : requirements.Problem });
                }
                foreach (var expansion in pack.Manifest.Expansions)
                    expansions.Add(new GameplayExpansionOption { Id = expansion.ExpansionId, DisplayName = expansion.DisplayName, Description = expansion.Description,
                        PackId = pack.Manifest.PackId, PackVersion = pack.Manifest.Version, Requires = ReadOnly(expansion.Requires), RequiredCapabilities = ReadOnly(expansion.EffectiveRequiredCapabilities),
                        Conflicts = ReadOnly(expansion.Conflicts), ApplicableModes = ReadOnly(expansion.ApplicableModes),
                        UnavailableReason = problem.Length > 0 ? problem : Missing(expansion.EffectiveRequiredCapabilities) });
            }
            // 同一玩法身份不能按扫描顺序择一；隔离冲突项并保留其他包的独立玩法。
            var duplicateModes = modes.GroupBy(x => x.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).ToArray();
            var duplicateExpansions = expansions.GroupBy(x => x.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).ToArray();
            foreach (var group in duplicateModes)
                diagnostics.Add("模式身份冲突：" + group.Key + "（" + string.Join("、", group.Select(x => x.PackId + "/" + x.PackVersion).OrderBy(x => x, StringComparer.Ordinal)) + "）");
            foreach (var group in duplicateExpansions)
                diagnostics.Add("扩展身份冲突：" + group.Key + "（" + string.Join("、", group.Select(x => x.PackId + "/" + x.PackVersion).OrderBy(x => x, StringComparer.Ordinal)) + "）");
            var conflictingModeIds = new HashSet<string>(duplicateModes.Select(x => x.Key), StringComparer.Ordinal);
            var conflictingExpansionIds = new HashSet<string>(duplicateExpansions.Select(x => x.Key), StringComparer.Ordinal);
            modes.RemoveAll(x => conflictingModeIds.Contains(x.Id));
            expansions.RemoveAll(x => conflictingExpansionIds.Contains(x.Id));
            return new GameplayOptionsCatalog { Modes = modes.AsReadOnly(), Expansions = expansions.AsReadOnly(), Diagnostics = diagnostics.AsReadOnly() };
        }

        public bool TryPlanModeTransition(string modeId, string mapId, IEnumerable<string> currentExpansions, out GameplayModeTransition plan, out string reason)
            => TryPlanModesTransition(new[] { modeId }, mapId, currentExpansions, out plan, out reason);

        public bool TryPlanModesTransition(IEnumerable<string> modeIds, string mapId, IEnumerable<string> currentExpansions, out GameplayModeTransition plan, out string reason)
        {
            plan = null; reason = "";
            var selectedModeIds = modeIds?.ToArray();
            if (selectedModeIds == null || selectedModeIds.Length == 0 || selectedModeIds.Any(string.IsNullOrWhiteSpace) ||
                selectedModeIds.Distinct(StringComparer.Ordinal).Count() != selectedModeIds.Length)
            { reason = "所选模式无效或重复"; return false; }
            var selectedModes = new List<GameplayModeOption>();
            foreach (string id in selectedModeIds)
            {
                var mode = Modes.SingleOrDefault(x => x.Id == id);
                if (mode == null) { reason = "模式不存在：" + id; return false; }
                if (!mode.Available) { reason = mode.UnavailableReason; return false; }
                if (mapId != StaticMapDefinitions.ThreePlayerMapId && mapId != StaticMapDefinitions.FourPlayerMapId ||
                    mode.ApplicableMapIds.Count > 0 && !mode.ApplicableMapIds.Contains(mapId))
                { reason = "模式不适用于所选地图：" + id; return false; }
                selectedModes.Add(mode);
            }
            foreach (var mode in selectedModes)
                foreach (string conflict in mode.Conflicts)
                    if (selectedModeIds.Contains(conflict)) { reason = "模式冲突：" + mode.Id + " / " + conflict; return false; }
            var orderConflict = selectedModes.Where(mode => mode.PreparationOrder.HasValue)
                .GroupBy(mode => mode.PreparationOrder.Value).FirstOrDefault(group => group.Count() > 1);
            if (orderConflict != null)
            { reason = "准备阶段序号冲突：" + string.Join(" / ", orderConflict.Select(mode => mode.DisplayName)) + "（" + orderConflict.Key + "）"; return false; }
            if (currentExpansions == null) { reason = "缺少当前扩展配置"; return false; }
            var byId = Expansions.ToDictionary(x => x.Id, StringComparer.Ordinal);
            var required = new HashSet<string>(StringComparer.Ordinal); var visiting = new HashSet<string>(StringComparer.Ordinal);
            string Require(string id)
            {
                if (required.Contains(id)) return "";
                if (!visiting.Add(id)) return "扩展依赖存在循环：" + id;
                if (!byId.TryGetValue(id, out var expansion)) return "缺少必需扩展：" + id;
                if (!expansion.Available) return expansion.UnavailableReason;
                if (expansion.ApplicableModes.Count > 0 && !expansion.ApplicableModes.Any(selectedModeIds.Contains)) return "必需扩展不适用于所选模式：" + id;
                foreach (string dependency in expansion.Requires)
                {
                    string problem = Require(dependency);
                    if (problem.Length > 0) return problem;
                }
                visiting.Remove(id); required.Add(id); return "";
            }
            foreach (string id in selectedModes.SelectMany(mode => mode.Requires).Concat(new[] { "core" }).Distinct(StringComparer.Ordinal))
            {
                reason = Require(id);
                if (reason.Length > 0) return false;
            }
            var original = currentExpansions.ToArray();
            if (original.Any(string.IsNullOrWhiteSpace) || original.Distinct(StringComparer.Ordinal).Count() != original.Length)
            { reason = "当前扩展配置无效"; return false; }
            var selected = new HashSet<string>(required, StringComparer.Ordinal);
            foreach (string id in original)
                if (byId.TryGetValue(id, out var option) && option.Available &&
                    (option.ApplicableModes.Count == 0 || option.ApplicableModes.Any(selectedModeIds.Contains))) selected.Add(id);
            bool changed;
            do
            {
                changed = false;
                foreach (string id in selected.Where(x => !required.Contains(x)).ToArray())
                    if (byId[id].Requires.Any(dependency => !selected.Contains(dependency))) { selected.Remove(id); changed = true; }
            } while (changed);
            foreach (string id in selected.OrderBy(x => x, StringComparer.Ordinal))
                foreach (string conflict in byId[id].Conflicts)
                    if (selected.Contains(conflict)) { reason = "扩展冲突：" + id + " / " + conflict; return false; }
            plan = new GameplayModeTransition { ExpansionIds = ReadOnlySorted(selected), RequiredExpansionIds = ReadOnlySorted(required),
                RemovedExpansionIds = ReadOnlySorted(original.Where(id => !selected.Contains(id))) };
            return true;
        }

        public bool TryValidateConfiguration(GameRuleConfiguration configuration, out string reasonCode)
        {
            reasonCode = "";
            if (configuration == null || !configuration.TryValidateStructure(out reasonCode)) return false;
            var modeIds = configuration.ActiveRuleModeIds;
            var modes = new List<GameplayModeOption>();
            foreach (string modeId in modeIds)
            {
                var mode = Modes.SingleOrDefault(item => item.Id == modeId);
                if (mode == null) { reasonCode = "rule_mode_not_implemented"; return false; }
                if (!mode.Available)
                {
                string missing = RuleModeReadiness.Missing(mode.RequiredCapabilities);
                reasonCode = missing.Length == 0 ? "content_not_ready" : "rule_mode_not_ready:" + missing;
                return false;
                }
                if (mode.ApplicableMapIds.Count > 0 && !mode.ApplicableMapIds.Contains(configuration.MapId))
                { reasonCode = "rule_mode_map_invalid"; return false; }
                modes.Add(mode);
            }
            foreach (var mode in modes)
                foreach (string conflict in mode.Conflicts)
                    if (modeIds.Contains(conflict)) { reasonCode = "模式冲突：" + mode.Id + " / " + conflict; return false; }
            var duplicateOrder = modes.Where(mode => mode.PreparationOrder.HasValue)
                .GroupBy(mode => mode.PreparationOrder.Value).FirstOrDefault(group => group.Count() > 1);
            if (duplicateOrder != null)
            { reasonCode = "准备阶段序号冲突：" + string.Join(" / ", duplicateOrder.Select(mode => mode.DisplayName)) + "（" + duplicateOrder.Key + "）"; return false; }
            var enabled = new HashSet<string>(configuration.EnabledExpansionIds, StringComparer.Ordinal);
            foreach (var mode in modes)
                if (mode.Requires.Any(id => !enabled.Contains(id)))
                { reasonCode = mode.Id == "entrepreneur" ? "missing_entrepreneur_dependency" : "rule_mode_dependency_missing"; return false; }
            foreach (string id in enabled)
            {
                var expansion = Expansions.SingleOrDefault(item => item.Id == id);
                if (expansion == null || !expansion.Available || expansion.ApplicableModes.Count > 0 && !expansion.ApplicableModes.Any(modeIds.Contains))
                { reasonCode = "content_not_ready"; return false; }
                if (expansion.Requires.Any(required => !enabled.Contains(required)))
                { reasonCode = id == "infected" ? "missing_enterprise_dependency" : "expansion_dependency_missing"; return false; }
                if (expansion.Conflicts.Any(enabled.Contains)) { reasonCode = "expansion_conflict"; return false; }
            }
            return true;
        }

        private static IReadOnlyList<string> ReadOnlySorted(IEnumerable<string> values) => Array.AsReadOnly(values.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }
}
