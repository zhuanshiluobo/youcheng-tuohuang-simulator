using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using YC.Domain.Rules;

namespace YC.Infrastructure.Lua
{
    /// <summary>带物理来源和稳定槽位的定义；对外只提供副本。</summary>
    public sealed class DiyCompositeDefinition
    {
        private ExternalContentDefinition definition;
        public string SourcePackId { get; internal set; }
        public string SourceVersion { get; internal set; }
        public string RuntimeId { get; internal set; }
        public string DefinitionId => definition.DefinitionId;
        public ExternalContentDefinition Definition => Clone(definition);
        internal ExternalContentDefinition Value => definition;
        internal void SetDefinition(ExternalContentDefinition value) { definition = Clone(value); }
        private static ExternalContentDefinition Clone(ExternalContentDefinition value)
        {
            var copy = JsonConvert.DeserializeObject<ExternalContentDefinition>(JsonConvert.SerializeObject(value));
            copy.RuntimeId = value.RuntimeId;
            if (value.ArtworkSlice != null)
            {
                var s = value.ArtworkSlice;
                copy.ArtworkSlice = new ExternalArtworkSlice { Id = s.Id, Artwork = s.Artwork, SourceWidth = s.SourceWidth,
                    SourceHeight = s.SourceHeight, X = s.X, Y = s.Y, Width = s.Width, Height = s.Height };
            }
            return copy;
        }
    }

    /// <summary>组合阶段的来源寻址、替换链和事件追加校验；尚需正式加载器完成其余定义校验与装配。</summary>
    public sealed class DiyCompositeCatalog
    {
        private readonly ExternalContentPack core;
        private readonly DiyResolvedContent resolved;
        public IReadOnlyList<DiyCompositeDefinition> Definitions { get; }
        private readonly Dictionary<string, DiyCompositeDefinition> byDefinitionId;
        private DiyCompositeCatalog(ExternalContentPack core, DiyResolvedContent resolved, List<DiyCompositeDefinition> active,
            Dictionary<string, DiyCompositeDefinition> byDefinitionId)
        { this.core = core; this.resolved = resolved; Definitions = active.AsReadOnly(); this.byDefinitionId = byDefinitionId; }

        public static DiyCompositeCatalog Build(ExternalContentPack core, DiyResolvedContent resolved)
        {
            if (core == null || resolved == null) throw new ArgumentNullException();
            var all = new List<DiyCompositeDefinition>();
            foreach (var value in core.Definitions)
            {
                var item = new DiyCompositeDefinition { SourcePackId = "core", SourceVersion = (string)core.Manifest["version"] };
                item.SetDefinition(value); all.Add(item);
            }
            foreach (var locked in resolved.Packs)
                foreach (var document in locked.Snapshot.Definitions)
                {
                    var item = new DiyCompositeDefinition { SourcePackId = locked.PackId, SourceVersion = locked.Version };
                    item.SetDefinition(document.Source.ToObject<ExternalContentDefinition>()); all.Add(item);
                }
            var duplicate = all.GroupBy(x => x.DefinitionId, StringComparer.Ordinal).FirstOrDefault(x => x.Count() > 1);
            if (duplicate != null) throw new InvalidDataException("定义身份跨包冲突：" + duplicate.Key);
            var mechanisms = GameplayMechanismCatalog.SelectedForComposite(core, resolved);
            var selected = all.Where(x => x.Value.Enabled && resolved.ExpansionIds.Contains(x.Value.ExpansionId) &&
                GameplayMechanismCatalog.AppliesToModes(x.Value, resolved.ModeIds, mechanisms)).ToList();
            var byId = selected.ToDictionary(x => x.DefinitionId, StringComparer.Ordinal);
            var replacements = new Dictionary<string, DiyCompositeDefinition>(StringComparer.Ordinal);
            foreach (var item in selected.Where(x => x.Value.Replaces != null))
            {
                var reference = item.Value.Replaces;
                if (!byId.TryGetValue(reference.DefinitionId, out var target)) throw new InvalidDataException("替换目标不存在、未启用或不适用：" + item.DefinitionId + " -> " + reference.DefinitionId);
                if (target.Value.ContentType != reference.ContentType || target.Value.ExpansionId != reference.ExpansionId || item.Value.ContentType != target.Value.ContentType)
                    throw new InvalidDataException("替换目标类型或扩展不符：" + item.DefinitionId);
                if (!InDependencyClosure(item.SourcePackId, target.SourcePackId, resolved)) throw new InvalidDataException("替换目标不在来源包依赖闭包中：" + item.DefinitionId);
                if (replacements.ContainsKey(target.DefinitionId)) throw new InvalidDataException("多个定义替换同一目标：" + target.DefinitionId);
                replacements.Add(target.DefinitionId, item);
            }
            var active = new List<DiyCompositeDefinition>(); var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in selected.Where(x => x.Value.Replaces == null).OrderBy(x => x.DefinitionId, StringComparer.Ordinal))
            {
                var current = root;
                do
                {
                    if (!visited.Add(current.DefinitionId)) throw new InvalidDataException("替换链重复或循环：" + current.DefinitionId);
                    current.RuntimeId = root.DefinitionId;
                } while (replacements.TryGetValue(current.DefinitionId, out current));
                // 最终定义沿用根槽位，但仍保留自己的图片、脚本和来源。
                var leaf = root;
                while (replacements.TryGetValue(leaf.DefinitionId, out var next)) leaf = next;
                leaf.Value.RuntimeId = root.DefinitionId;
                active.Add(leaf);
            }
            if (visited.Count != selected.Count) throw new InvalidDataException("替换关系存在无根循环");
            return new DiyCompositeCatalog(core, resolved, active, byId);
        }

        public string ReadScript(DiyCompositeDefinition definition, string relative)
        {
            if (definition == null || !Definitions.Contains(definition)) throw new InvalidDataException("定义不属于本活动目录");
            if (definition.SourcePackId == "core") return core.GetScript(relative);
            var pack = resolved.GetPack(definition.SourcePackId);
            if (pack.Manifest.Source["scriptDependencies"] != null) throw new InvalidDataException("DIY 冻结模块尚未由正式装配器接入：" + definition.SourcePackId);
            return pack.Files.ReadText(relative);
        }
        public byte[] ReadArtwork(DiyCompositeDefinition definition, string relative)
        {
            if (definition == null || !Definitions.Contains(definition)) throw new InvalidDataException("定义不属于本活动目录");
            return definition.SourcePackId == "core" ? core.GetArtworkBytes(relative) : resolved.GetPack(definition.SourcePackId).Files.ReadBytes(relative);
        }

        public IReadOnlyList<SetupRecipeEntry> BuildEventAdditions(string mapId)
        {
            var result = new List<SetupRecipeEntry>();
            foreach (var locked in resolved.Packs)
                foreach (var recipe in locked.Snapshot.Recipes)
                {
                    if (!resolved.ExpansionIds.Contains(recipe.ExpansionId) || recipe.ApplicableModes.Count > 0 && !resolved.ModeIds.Any(recipe.ApplicableModes.Contains)) continue;
                    foreach (var entry in recipe.Entries)
                    {
                        if (entry.ApplicableMapIds.Count > 0 && !entry.ApplicableMapIds.Contains(mapId)) continue;
                        if (!byDefinitionId.TryGetValue(entry.DefinitionId, out var original) || original.SourcePackId != locked.PackId ||
                            original.Value.ExpansionId != recipe.ExpansionId || original.Value.ContentType != "event" || original.Value.Replaces != null)
                            throw new InvalidDataException("事件追加项必须引用本包启用的新事件，不能重复追加替换项：" + entry.EntryId);
                        string expected = (int?)original.Value.Data["color"] switch { 0 => "event_green", 1 => "event_yellow", 2 => "event_red", _ => "" };
                        if (entry.InitialZoneRole != expected) throw new InvalidDataException("事件颜色与追加牌堆不一致：" + entry.EntryId);
                        var final = Definitions.SingleOrDefault(x => x.RuntimeId == original.DefinitionId);
                        if (final == null) throw new InvalidDataException("追加事件没有最终槽位：" + entry.EntryId);
                        if ((int?)final.Value.Data["color"] != (int?)original.Value.Data["color"])
                            throw new InvalidDataException("最终替换事件颜色与追加牌堆不一致：" + entry.EntryId);
                        result.Add(new SetupRecipeEntry { EntryId = entry.EntryId, DefinitionId = final.DefinitionId, TypeId = "core.event_card",
                            Quantity = entry.Quantity, InitialZoneRole = entry.InitialZoneRole });
                    }
                }
            if (result.Select(x => x.EntryId).Distinct(StringComparer.Ordinal).Count() != result.Count) throw new InvalidDataException("事件追加配方身份重复");
            return result.AsReadOnly();
        }

        internal static bool InDependencyClosure(string source, string target, DiyResolvedContent resolved)
        {
            if (source == target) return true;
            if (source == "core") return false;
            var pending = new Stack<string>(); var visited = new HashSet<string>(StringComparer.Ordinal); pending.Push(source);
            while (pending.Count > 0)
            {
                string id = pending.Pop(); if (!visited.Add(id)) continue;
                if (id == target) return true;
                if (id == "core") continue;
                foreach (var dependency in resolved.GetPack(id).Manifest.Dependencies) pending.Push(dependency.PackId);
            }
            return false;
        }
    }
}
