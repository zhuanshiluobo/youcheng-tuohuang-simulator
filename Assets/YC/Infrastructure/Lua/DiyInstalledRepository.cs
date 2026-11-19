using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YC.Domain.Rules;

namespace YC.Infrastructure.Lua
{
    /// <summary>发现结果及房间外绑定的只读视图；房间或存档锁定优先于本机默认。</summary>
    public sealed class DiyInstalledRepository
    {
        private readonly ExternalContentPack core;
        private readonly GameplayOptionsCatalog coreOptions;
        private readonly IReadOnlyList<DiyDiscoveredPack> discovered;
        private readonly DiyVersionProfile profile;
        public DiyInstalledRepository(ExternalContentPack core, IReadOnlyList<DiyDiscoveredPack> discovered, DiyVersionProfile profile)
        {
            this.core = core ?? throw new ArgumentNullException(nameof(core));
            if (discovered == null) throw new ArgumentNullException(nameof(discovered));
            this.discovered = Array.AsReadOnly(discovered.ToArray());
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
            if (core.PackId != "core" || core.SchemaVersion != 3) throw new InvalidDataException("DIY 内容目录缺少正式 core 快照");
            coreOptions = GameplayOptionsCatalog.CoreOnly(core);
        }

        /// <summary>为本机新局推导候选锁定；调用方在确认完整配置后才发布结果。</summary>
        public DiyResolvedContent ResolveLocal(GameRuleConfiguration configuration)
        {
            ValidateShape(configuration);
            return Resolve(configuration, GetBoundPacks(null));
        }

        /// <summary>统一列出本机绑定版本提供的玩法；损坏或未绑定的包保留诊断。</summary>
        public GameplayOptionsCatalog GetOptions()
        {
            var diagnostics = new List<string>();
            foreach (var item in discovered.Where(x => x.Status != DiyDiscoveryStatus.Parsed))
                diagnostics.Add((item.PackId ?? "") + "/" + (item.Version ?? "") + "：" + item.Diagnostic);
            var bound = GetBoundPacks(diagnostics);
            return GameplayOptionsCatalog.Create(core, bound, diagnostics);
        }

        private List<DiyPackSnapshot> GetBoundPacks(List<string> diagnostics)
        {
            var bound = new List<DiyPackSnapshot>();
            foreach (string id in discovered.Where(x => x.Status == DiyDiscoveryStatus.Parsed).Select(x => x.PackId).Distinct(StringComparer.Ordinal))
            {
                try { bound.Add(profile.Resolve(id, discovered).Snapshot); }
                catch (InvalidDataException error) { diagnostics?.Add(error.Message); /* 未选择的包不阻止其他组合。 */ }
            }
            return bound;
        }

        /// <summary>按房间或存档精确锁定读取；不使用本地默认，不下载、修改或替换包。</summary>
        public DiyResolvedContent ResolveExact(GameRuleConfiguration configuration)
        {
            ValidateShape(configuration);
            if (configuration.LockedContentPacks.Count == 0) throw new InvalidDataException("房间或存档缺少精确包锁定清单");
            var coreLock = configuration.LockedContentPacks.SingleOrDefault(x => x.PackId == "core");
            if (coreLock == null) throw new InvalidDataException("包锁定清单缺少 core");
            if (coreLock.Version != (string)core.Manifest["version"]) throw new InvalidDataException("core 版本与锁定清单不一致");
            if (coreLock.ContentHash != core.ContentHash) throw new InvalidDataException("core 同版本内容与锁定清单不一致");
            var selected = new List<DiyPackSnapshot>();
            foreach (var locked in configuration.LockedContentPacks.Where(x => x.PackId != "core").OrderBy(x => x.PackId, StringComparer.Ordinal))
            {
                var rows = discovered.Where(x => x.PackId == locked.PackId && x.Version == locked.Version).ToArray();
                if (rows.Length == 0) throw new InvalidDataException("缺少锁定包或版本：" + locked.PackId + "/" + locked.Version);
                if (rows.Length != 1) throw new InvalidDataException("包身份或版本重复：" + locked.PackId + "/" + locked.Version);
                if (rows[0].Status != DiyDiscoveryStatus.Parsed || rows[0].Snapshot == null)
                    throw new InvalidDataException("锁定包损坏：" + locked.PackId + "/" + locked.Version + ":" + rows[0].Diagnostic);
                if (rows[0].Snapshot.ContentHash != locked.ContentHash)
                    throw new InvalidDataException("同版本内容被修改：" + locked.PackId + "/" + locked.Version);
                selected.Add(rows[0].Snapshot);
            }
            var resolved = Resolve(configuration, selected);
            var expected = resolved.CreateRuleLocks();
            var actual = configuration.LockedContentPacks.OrderBy(x => x.PackId, StringComparer.Ordinal).ToArray();
            if (expected.Count != actual.Length || actual.Where((x, i) => x.PackId != expected[i].PackId || x.Version != expected[i].Version || x.ContentHash != expected[i].ContentHash).Any())
                throw new InvalidDataException("包锁定清单与玩法依赖闭包不一致");
            return resolved;
        }

        private DiyResolvedContent Resolve(GameRuleConfiguration configuration, IEnumerable<DiyPackSnapshot> packs) =>
            DiyContentResolution.Resolve(configuration.RuleModeId, configuration.EnabledExpansionIds, configuration.MapId,
                packs, (string)core.Manifest["version"], core.ContentHash, RuleModeReadiness.ImplementedCapabilities,
                coreOptions, configuration.AdditionalRuleModeIds);
        private static void ValidateShape(GameRuleConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (!configuration.TryValidateStructure(out string reason)) throw new InvalidDataException(reason);
        }
    }
}
