using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YC.Domain.Rules;

namespace YC.Infrastructure.Lua
{
    public sealed class DiyLockedPack
    {
        public string PackId { get; internal set; }
        public string Version { get; internal set; }
        public string ContentHash { get; internal set; }
        internal DiyPackSnapshot Snapshot;
    }

    /// <summary>只表示包锁定，不代替包含配方、规则和执行器合同的活动对局指纹。</summary>
    public sealed class DiyResolvedContent
    {
        public string ModeId { get; internal set; }
        public IReadOnlyList<string> ModeIds { get; internal set; }
        public string MapId { get; internal set; }
        public string CoreVersion { get; internal set; }
        public string CoreHash { get; internal set; }
        public IReadOnlyList<string> ExpansionIds { get; internal set; }
        public IReadOnlyList<DiyLockedPack> Packs { get; internal set; }
        public string LockHash { get; internal set; }
        public DiyPackSnapshot GetPack(string packId)
        {
            var found = Packs.SingleOrDefault(x => x.PackId == packId);
            return found?.Snapshot ?? throw new InvalidDataException("包不在本局锁定清单中：" + packId);
        }
        public List<ContentPackLock> CreateRuleLocks()
        {
            var items = new List<ContentPackLock> { new ContentPackLock { PackId = "core", Version = CoreVersion, ContentHash = CoreHash } };
            items.AddRange(Packs.Select(x => new ContentPackLock { PackId = x.PackId, Version = x.Version, ContentHash = x.ContentHash }));
            return items.OrderBy(x => x.PackId, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>按玩法选择解析精确版本的依赖闭包；不读取其他包的 Lua，也不改变已选组合。</summary>
    public static class DiyContentResolution
    {
        private sealed class ProvidedMode { internal DiyPackSnapshot Pack; internal DiyRuleMode Mode; }
        private sealed class ProvidedExpansion { internal DiyPackSnapshot Pack; internal DiyExpansion Expansion; }

        public static DiyResolvedContent Resolve(string modeId, IEnumerable<string> enabledExpansions, string mapId,
            IEnumerable<DiyPackSnapshot> boundPacks, string coreVersion, string coreHash, IEnumerable<string> hostCapabilities,
            GameplayOptionsCatalog coreOptions, IEnumerable<string> additionalModeIds = null)
        {
            if (string.IsNullOrWhiteSpace(modeId) || enabledExpansions == null || boundPacks == null || hostCapabilities == null || coreOptions == null)
                throw new ArgumentException("缺少玩法选择或内容目录");
            DiyVersion.Parse(coreVersion, "core/version");
            if (string.IsNullOrWhiteSpace(coreHash)) throw new ArgumentException("缺少 core 内容身份", nameof(coreHash));
            var additionalModes = (additionalModeIds ?? Enumerable.Empty<string>()).ToArray();
            if (additionalModes.Any(string.IsNullOrWhiteSpace) || additionalModes.Contains(modeId) ||
                additionalModes.Distinct(StringComparer.Ordinal).Count() != additionalModes.Length)
                throw new InvalidDataException("启用模式包含重复或无效身份");
            var activeModes = new[] { modeId }.Concat(additionalModes.OrderBy(id => id, StringComparer.Ordinal)).ToArray();
            var enabled = enabledExpansions.ToArray();
            if (enabled.Any(string.IsNullOrWhiteSpace) || enabled.Distinct(StringComparer.Ordinal).Count() != enabled.Length || !enabled.Contains("core"))
                throw new InvalidDataException("启用扩展缺少 core 或包含重复身份");
            var enabledSet = new HashSet<string>(enabled, StringComparer.Ordinal);
            var capabilities = new HashSet<string>(hostCapabilities, StringComparer.Ordinal);
            var coreModes = coreOptions.Modes.ToDictionary(x => x.Id, StringComparer.Ordinal);
            var coreExpansions = coreOptions.Expansions.ToDictionary(x => x.Id, StringComparer.Ordinal);
            var packs = new Dictionary<string, DiyPackSnapshot>(StringComparer.Ordinal);
            foreach (var pack in boundPacks)
            {
                if (pack == null || packs.ContainsKey(pack.Manifest.PackId)) throw new InvalidDataException("同一包存在多个活动版本或包为空");
                packs.Add(pack.Manifest.PackId, pack);
            }
            var modes = new Dictionary<string, List<ProvidedMode>>(StringComparer.Ordinal);
            var expansions = new Dictionary<string, List<ProvidedExpansion>>(StringComparer.Ordinal);
            foreach (var pack in packs.Values)
            {
                foreach (var mode in pack.Manifest.RuleModes)
                {
                    if (!modes.TryGetValue(mode.ModeId, out var providers)) modes.Add(mode.ModeId, providers = new List<ProvidedMode>());
                    providers.Add(new ProvidedMode { Pack = pack, Mode = mode });
                }
                foreach (var expansion in pack.Manifest.Expansions)
                {
                    if (!expansions.TryGetValue(expansion.ExpansionId, out var providers)) expansions.Add(expansion.ExpansionId, providers = new List<ProvidedExpansion>());
                    providers.Add(new ProvidedExpansion { Pack = pack, Expansion = expansion });
                }
            }
            var roots = new HashSet<string>(StringComparer.Ordinal);
            var selectedModes = new List<(DiyRuleMode Mode, DiyPackSnapshot Pack)>();
            var modePolicies = new List<(string Id, string Name, int? Order, IReadOnlyList<string> Conflicts)>();
            foreach (string selectedId in activeModes)
            {
            if (coreModes.TryGetValue(selectedId, out var coreMode))
            {
                if (modes.ContainsKey(selectedId)) throw new InvalidDataException("多个包声明相同模式：" + selectedId);
                if (!coreMode.Available) throw new InvalidDataException("模式不可用：" + selectedId + "：" + coreMode.UnavailableReason);
                if (coreMode.ApplicableMapIds.Count != 0 && !coreMode.ApplicableMapIds.Contains(mapId))
                    throw new InvalidDataException("模式不适用于所选地图：" + selectedId + "/" + mapId);
                RequireEnabled(coreMode.Requires, enabledSet, "模式 " + selectedId);
                RequireCapabilities(coreMode.RequiredCapabilities, capabilities, "模式 " + selectedId);
                modePolicies.Add((selectedId, coreMode.DisplayName, coreMode.PreparationOrder, coreMode.Conflicts));
            }
            else
            {
                if (!modes.TryGetValue(selectedId, out var providers) || providers.Count == 0) throw new InvalidDataException("模式不存在：" + selectedId);
                if (providers.Count != 1) throw new InvalidDataException("多个包声明相同模式：" + selectedId);
                var selectedModePack = providers[0].Pack; var selectedMode = providers[0].Mode; roots.Add(selectedModePack.Manifest.PackId);
                if (selectedMode.ApplicableMapIds.Count != 0 && !selectedMode.ApplicableMapIds.Contains(mapId))
                    throw new InvalidDataException("模式不适用于所选地图：" + selectedId + "/" + mapId);
                RequireEnabled(selectedMode.Requires, enabledSet, "模式 " + selectedId);
                RequireCapabilities(selectedMode.EffectiveRequiredCapabilities, capabilities, "模式 " + selectedId);
                selectedModes.Add((selectedMode, selectedModePack));
                modePolicies.Add((selectedId, selectedMode.DisplayName, selectedMode.PreparationOrder, selectedMode.Conflicts));
            }
            }
            foreach (var policy in modePolicies)
                foreach (string conflict in policy.Conflicts)
                    if (activeModes.Contains(conflict)) throw new InvalidDataException("模式冲突：" + policy.Id + "/" + conflict);
            var conflictingOrder = modePolicies.Where(policy => policy.Order.HasValue).GroupBy(policy => policy.Order.Value)
                .FirstOrDefault(group => group.Count() > 1);
            if (conflictingOrder != null)
                throw new InvalidDataException("准备阶段序号冲突：" + string.Join(" / ", conflictingOrder.Select(policy => policy.Name)) + "（" + conflictingOrder.Key + "）");
            foreach (string id in enabled.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (coreExpansions.TryGetValue(id, out var coreExpansion))
                {
                    if (expansions.ContainsKey(id)) throw new InvalidDataException("多个包声明相同扩展：" + id);
                    if (!coreExpansion.Available) throw new InvalidDataException("扩展不可用：" + id + "：" + coreExpansion.UnavailableReason);
                    if (coreExpansion.ApplicableModes.Count != 0 && !coreExpansion.ApplicableModes.Any(activeModes.Contains))
                        throw new InvalidDataException("扩展不适用于所选模式：" + id + "/" + modeId);
                    RequireEnabled(coreExpansion.Requires, enabledSet, "扩展 " + id);
                    foreach (string conflict in coreExpansion.Conflicts)
                        if (enabledSet.Contains(conflict)) throw new InvalidDataException("扩展冲突：" + id + "/" + conflict);
                    RequireCapabilities(coreExpansion.RequiredCapabilities, capabilities, "扩展 " + id);
                    continue;
                }
                if (!expansions.TryGetValue(id, out var providers) || providers.Count == 0) throw new InvalidDataException("扩展不存在：" + id);
                if (providers.Count != 1) throw new InvalidDataException("多个包声明相同扩展：" + id);
                var provider = providers[0]; var declaration = provider.Expansion;
                roots.Add(provider.Pack.Manifest.PackId);
                if (declaration.ApplicableModes.Count != 0 && !declaration.ApplicableModes.Any(activeModes.Contains))
                    throw new InvalidDataException("扩展不适用于所选模式：" + id + "/" + modeId);
                RequireEnabled(declaration.Requires, enabledSet, "扩展 " + id);
                foreach (string conflict in declaration.Conflicts)
                    if (enabledSet.Contains(conflict)) throw new InvalidDataException("扩展冲突：" + id + "/" + conflict);
                RequireCapabilities(declaration.EffectiveRequiredCapabilities, capabilities, "扩展 " + id);
            }
            var ordered = new List<DiyPackSnapshot>();
            var marks = new Dictionary<string, int>(StringComparer.Ordinal);
            void Visit(string id)
            {
                if (marks.TryGetValue(id, out int mark))
                { if (mark == 1) throw new InvalidDataException("包依赖存在循环：" + id); return; }
                if (!packs.TryGetValue(id, out var pack)) throw new InvalidDataException("缺少依赖包：" + id);
                marks.Add(id, 1);
                RequireCapabilities(pack.Manifest.RequiredCapabilities, capabilities, "包 " + id + "/" + pack.Manifest.Version);
                foreach (var dependency in pack.Manifest.Dependencies.OrderBy(x => x.PackId, StringComparer.Ordinal))
                {
                    string actual;
                    if (dependency.PackId == "core") actual = coreVersion;
                    else
                    {
                        if (!packs.TryGetValue(dependency.PackId, out var source)) throw new InvalidDataException("缺少依赖包：" + id + " -> " + dependency.PackId);
                        actual = source.Manifest.Version;
                    }
                    if (!dependency.Accepts(actual)) throw new InvalidDataException("包依赖版本不兼容：" + id + " -> " + dependency.PackId + " " + dependency.VersionRange + "，实际 " + actual);
                    if (dependency.PackId != "core") Visit(dependency.PackId);
                }
                marks[id] = 2; ordered.Add(pack);
            }
            foreach (string root in roots.OrderBy(x => x, StringComparer.Ordinal)) Visit(root);
            // 所有可同时装配的包按 ID 排序；不能让 DFS 根节点的偶然顺序改变同级顺序。
            var selectedById = ordered.ToDictionary(x => x.Manifest.PackId, StringComparer.Ordinal);
            var remaining = new HashSet<string>(selectedById.Keys, StringComparer.Ordinal);
            var stableOrder = new List<DiyPackSnapshot>();
            while (remaining.Count > 0)
            {
                string next = remaining.Where(id => selectedById[id].Manifest.Dependencies.All(d => d.PackId == "core" || !remaining.Contains(d.PackId)))
                    .OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
                if (next == null) throw new InvalidDataException("包依赖存在循环");
                stableOrder.Add(selectedById[next]); remaining.Remove(next);
            }
            ordered = stableOrder;
            // 非空适用模式必须真实存在，且其来源在声明者的包依赖闭包中。
            var selectedPacks = ordered.ToDictionary(x => x.Manifest.PackId, StringComparer.Ordinal);
            var modeOwners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var source in ordered)
                foreach (var mode in source.Manifest.RuleModes)
                {
                    if (modeOwners.ContainsKey(mode.ModeId)) throw new InvalidDataException("多个包声明相同模式：" + mode.ModeId);
                    modeOwners.Add(mode.ModeId, source.Manifest.PackId);
                }
            bool DependsOn(string sourceId, string targetId)
            {
                var pending = new Stack<string>(); var seen = new HashSet<string>(StringComparer.Ordinal); pending.Push(sourceId);
                while (pending.Count > 0)
                {
                    string id = pending.Pop();
                    if (!seen.Add(id)) continue;
                    if (id == targetId) return true;
                    if (id == "core" || !selectedPacks.TryGetValue(id, out var source)) continue;
                    foreach (var dependency in source.Manifest.Dependencies) pending.Push(dependency.PackId);
                }
                return false;
            }
            void ValidateApplicableModes(string owner, IEnumerable<string> declared, string subject)
            {
                foreach (string id in declared)
                {
                    if (coreModes.ContainsKey(id)) continue;
                    if (!modeOwners.TryGetValue(id, out string modeOwner))
                        throw new InvalidDataException(subject + "引用不存在或未声明依赖的模式：" + id);
                    if (!DependsOn(owner, modeOwner))
                        throw new InvalidDataException(subject + "的模式来源不在包依赖闭包中：" + id);
                }
            }
            foreach (var source in ordered)
            {
                string owner = source.Manifest.PackId;
                foreach (var expansion in source.Manifest.Expansions)
                    ValidateApplicableModes(owner, expansion.ApplicableModes, "扩展 " + expansion.ExpansionId);
                foreach (var recipe in source.Recipes)
                    ValidateApplicableModes(owner, recipe.ApplicableModes, "配方 " + recipe.RecipeId);
                foreach (var document in source.Definitions)
                    ValidateApplicableModes(owner, DiyPackManifest.Strings(document.Source, "applicableModes", document.RelativePath), "定义 " + document.DefinitionId);
            }
            foreach (var selected in selectedModes)
            {
            var selectedMode = selected.Mode;
            var selectedModePack = selected.Pack;
            bool InModeClosure(string target)
            {
                var pending = new Stack<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
                pending.Push(selectedModePack.Manifest.PackId);
                while (pending.Count > 0)
                {
                    string id = pending.Pop();
                    if (!seen.Add(id)) continue;
                    if (id == target) return true;
                    if (id == "core" || !packs.TryGetValue(id, out var source)) continue;
                    foreach (var dependency in source.Manifest.Dependencies) pending.Push(dependency.PackId);
                }
                return false;
            }
                foreach (var reference in selectedMode.SetupRecipeRefs)
                {
                    if (!InModeClosure(reference.PackId)) throw new InvalidDataException("模式配方来源不在其依赖闭包中：" + reference.PackId);
                    if (reference.PackId == "core")
                    {
                        continue; // core 的配方身份与适用性由正式装配器核验。
                    }
                    var owner = ordered.SingleOrDefault(x => x.Manifest.PackId == reference.PackId);
                    if (owner == null || !owner.Recipes.Any(x => x.RecipeId == reference.RecipeId))
                        throw new InvalidDataException("模式配方不在依赖闭包中或不存在：" + reference.PackId + "/" + reference.RecipeId);
                }
            if (selectedMode.Preparation != null)
                foreach (var step in selectedMode.Preparation.UnresolvedSteps)
                {
                    if (!InModeClosure(step.SourcePackId))
                        throw new InvalidDataException("准备步骤来源不在模式依赖闭包中：" + step.StepId + "/" + step.SourcePackId);
                    if (step.SourcePackId == "core")
                    {
                        if (step.SourcePackVersion != coreVersion) throw new InvalidDataException("准备步骤来源版本不匹配：" + step.StepId);
                        continue; // core 脚本由活动目录装配时核验并冻结。
                    }
                    if (!selectedPacks.TryGetValue(step.SourcePackId, out var source) || source.Manifest.Version != step.SourcePackVersion)
                        throw new InvalidDataException("准备步骤来源版本不匹配：" + step.StepId);
                    if (step.Kind != "lua") continue;
                    EventChoiceContract.ValidateReferencedScript(step.ScriptPath, source.Manifest.PackId, "preparation[" + step.StepId + "]", source.Files.ReadText);
                    if (step.Completion != null)
                        EventChoiceContract.ValidateReferencedScript(step.Completion.ScriptPath, source.Manifest.PackId, "preparation[" + step.StepId + "].completion", source.Files.ReadText);
                }
            }
            var locked = ordered.Select(x => new DiyLockedPack { PackId = x.Manifest.PackId, Version = x.Manifest.Version, ContentHash = x.ContentHash, Snapshot = x }).ToArray();
            string[] orderedExpansions = enabled.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            return new DiyResolvedContent { ModeId = modeId, ModeIds = Array.AsReadOnly(activeModes), MapId = mapId, CoreVersion = coreVersion, CoreHash = coreHash, ExpansionIds = Array.AsReadOnly(orderedExpansions), Packs = Array.AsReadOnly(locked),
                LockHash = Fingerprint(modeId, mapId, coreVersion, coreHash, orderedExpansions, locked, activeModes.Skip(1)) };
        }

        private static void RequireEnabled(IEnumerable<string> needed, HashSet<string> enabled, string owner)
        {
            foreach (string id in needed) if (!enabled.Contains(id)) throw new InvalidDataException(owner + "缺少必需扩展：" + id);
        }
        private static void RequireCapabilities(IEnumerable<string> needed, HashSet<string> available, string owner)
        {
            var missing = needed.Where(x => !available.Contains(x)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (missing.Length > 0) throw new InvalidDataException(owner + "需要未实现的能力：" + string.Join(",", missing));
        }
        private static string Fingerprint(string mode, string map, string coreVersion, string coreHash, IEnumerable<string> expansions, IEnumerable<DiyLockedPack> packs, IEnumerable<string> additionalModes)
        {
            using (var input = new MemoryStream())
            using (var writer = new BinaryWriter(input, Encoding.UTF8, true))
            {
                writer.Write("nomadcity.diy.lock.v1"); writer.Write(mode); writer.Write(map ?? ""); writer.Write(coreVersion); writer.Write(coreHash);
                var enabled = expansions.ToArray(); writer.Write(enabled.Length); foreach (string id in enabled) writer.Write(id);
                var selected = packs.ToArray(); writer.Write(selected.Length);
                foreach (var pack in selected) { writer.Write(pack.PackId); writer.Write(pack.Version); writer.Write(pack.ContentHash); }
                var extra = additionalModes.ToArray();
                if (extra.Length > 0) { writer.Write("additional-modes.v1"); writer.Write(extra.Length); foreach (string id in extra) writer.Write(id); }
                writer.Flush(); input.Position = 0; using (var hash = SHA256.Create()) return DiyFrozenFiles.Hex(hash.ComputeHash(input));
            }
        }
    }
}
