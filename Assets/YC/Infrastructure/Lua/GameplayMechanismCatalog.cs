using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Rules;

namespace YC.Infrastructure.Lua
{
    /// <summary>模式显式选择宿主机制；未知机制和缺失依赖不会被猜成另一种玩法。</summary>
    internal static class GameplayMechanismCatalog
    {
        internal sealed class Declaration
        {
            internal string Id;
            internal string[] Requires, RequiredCapabilities;
        }

        internal static IReadOnlyDictionary<string, Declaration> Read(JObject manifest)
        {
            var result = new Dictionary<string, Declaration>(StringComparer.Ordinal);
            if (manifest["mechanismDeclarations"] == null) return result;
            if (!(manifest["mechanismDeclarations"] is JArray array)) throw new InvalidDataException("机制声明必须是数组");
            foreach (var token in array)
            {
                if (!(token is JObject item) || item.Properties().Any(p => !new[] { "mechanismId", "requires", "requiredCapabilities" }.Contains(p.Name)))
                    throw new InvalidDataException("机制声明字段无效");
                string id = DiyPackManifest.Text(item, "mechanismId", "mechanismDeclarations");
                if (!DiyIdentifiers.ValidIdentity(id) || result.ContainsKey(id)) throw new InvalidDataException("机制身份无效或重复：" + id);
                result.Add(id, new Declaration { Id = id, Requires = DiyPackManifest.Strings(item, "requires", id),
                    RequiredCapabilities = DiyPackManifest.Strings(item, "requiredCapabilities", id) });
            }
            return result;
        }

        internal static string[] ModeMechanisms(JObject mode)
            => DiyPackManifest.Strings(mode, "mechanisms", (string)mode?["modeId"] ?? "ruleMode");

        internal static string[] Selected(JObject manifest, JObject mode, IEnumerable<string> enabled, out string[] capabilities)
        {
            var declarations = Read(manifest); var ids = ModeMechanisms(mode); var required = new List<string>();
            var expansions = new HashSet<string>(enabled ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            foreach (string id in ids)
            {
                if (!declarations.TryGetValue(id, out var declaration)) throw new InvalidOperationException("rule_mechanism_unknown:" + id);
                if (declaration.Requires.Any(x => !expansions.Contains(x))) throw new InvalidOperationException("rule_mechanism_dependency_missing:" + id);
                required.AddRange(declaration.RequiredCapabilities);
            }
            capabilities = required.Distinct(StringComparer.Ordinal).ToArray();
            return ids;
        }

        internal static string[] SelectedForComposite(ExternalContentPack core, DiyResolvedContent resolved)
        {
            JObject manifest = core.Manifest;
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (string modeId in resolved.ModeIds)
            {
            var mode = (manifest["ruleModes"] as JArray ?? new JArray()).OfType<JObject>().SingleOrDefault(x => (string)x["modeId"] == modeId);
            if (mode == null)
            {
                var provided = resolved.Packs.SelectMany(x => x.Snapshot.Manifest.RuleModes).SingleOrDefault(x => x.ModeId == modeId);
                if (provided == null) throw new InvalidDataException("模式不存在：" + modeId);
                mode = new JObject { ["modeId"] = provided.ModeId, ["mechanisms"] = new JArray(provided.Mechanisms) };
            }
            result.UnionWith(Selected(manifest, mode, resolved.ExpansionIds, out _));
            }
            return result.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }

        internal static bool Applies(ExternalContentDefinition definition, string modeId, IEnumerable<string> mechanisms)
            => AppliesToModes(definition, new[] { modeId }, mechanisms);

        internal static bool AppliesToModes(ExternalContentDefinition definition, IEnumerable<string> modeIds, IEnumerable<string> mechanisms)
        {
            if (definition.ApplicableModes.Count != 0 && (modeIds == null || !modeIds.Any(definition.ApplicableModes.Contains))) return false;
            return definition.RequiredMechanisms.Count == 0 || mechanisms != null && definition.RequiredMechanisms.All(mechanisms.Contains);
        }
    }
}
