using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Profiling;
using YC.Domain.Rules;
using YC.Infrastructure.Lua;

namespace YC.Infrastructure.Multiplayer
{
    public sealed class BasicRoomContentCapabilityProvider : IRoomContentCapabilityProvider, IRoomContentLockProvider, IRoomOfficialPreparationProvider
    {
        private static readonly ProfilerMarker ContentValidationMarker = new ProfilerMarker("YC.RoomContentValidation");
        private static readonly ProfilerMarker ContentHashMarker = new ProfilerMarker("YC.RoomContentHash");
        private readonly Func<ExternalContentPack> installedPackSource;
        private readonly Func<DiyInstalledRepository> diyRepositorySource;
        private readonly object cacheLock = new object();
        private readonly Dictionary<string, Capability> capabilities = new Dictionary<string, Capability>(StringComparer.Ordinal);
        private ExternalContentPack cachedPack;
        private GameplayOptionsCatalog cachedCoreOptions;
        private string cachedSnapshotHash;
        internal int CatalogBuildCount { get; private set; }

        private sealed class Capability
        {
            public string Hash = string.Empty;
            public string Reason = string.Empty;
        }

        public BasicRoomContentCapabilityProvider() : this(() => YC.Presentation.ExternalContentRuntime.Pack, () => YC.Presentation.ExternalContentRuntime.InstalledDiy)
        { installedPackSource(); }

        // 内容包已把定义、脚本和图片加载为隔离快照；重载时提供新快照，不能永久捕获首次加载的包。
        public BasicRoomContentCapabilityProvider(Func<ExternalContentPack> installedPackSource) : this(installedPackSource, null) { }
        public BasicRoomContentCapabilityProvider(Func<ExternalContentPack> installedPackSource, Func<DiyInstalledRepository> diyRepositorySource)
        {
            this.installedPackSource = installedPackSource ?? throw new ArgumentNullException(nameof(installedPackSource));
            this.diyRepositorySource = diyRepositorySource;
        }

        public string NewGamePreparationContract(GameRuleConfiguration configuration) =>
            configuration == null ? null : GameRuleConfiguration.OfficialPreparationVersion;

        public bool TryPrepareLock(GameRuleConfiguration configuration, out List<ContentPackLock> lockedPacks, out string reasonCode)
        {
            lockedPacks = null; reasonCode = "content_not_ready";
            if (configuration == null || !configuration.TryValidateStructure(out reasonCode)) return false;
            try
            {
                var core = installedPackSource();
                if (core == null) { reasonCode = "content_not_ready"; return false; }
                var coreOptions = GameplayOptionsCatalog.CoreOnly(core);
                bool fromCore = configuration.ActiveRuleModeIds.All(id => coreOptions.Modes.Any(item => item.Id == id)) &&
                    configuration.EnabledExpansionIds.All(id => coreOptions.Expansions.Any(item => item.Id == id));
                if (fromCore || diyRepositorySource == null)
                {
                    if (!configuration.TryValidate(coreOptions, out reasonCode)) return false;
                    lockedPacks = new List<ContentPackLock>(); reasonCode = ""; return true;
                }
                var local = configuration.Clone(); local.LockedContentPacks.Clear();
                var resolved = diyRepositorySource().ResolveLocal(local);
                lockedPacks = resolved.CreateRuleLocks(); reasonCode = ""; return true;
            }
            catch (InvalidDataException error) { reasonCode = error.Message; return false; }
            catch (InvalidOperationException error) { reasonCode = string.IsNullOrEmpty(error.Message) ? "content_not_ready" : error.Message; return false; }
            catch (IOException error) { reasonCode = error.Message; return false; }
            catch (UnauthorizedAccessException error) { reasonCode = error.Message; return false; }
        }

        public string GetContentPackHash(GameRuleConfiguration configuration)
        {
            using var scope = ContentHashMarker.Auto();
            return Resolve(configuration).Hash;
        }

        public bool TryValidate(GameRuleConfiguration configuration, out string reasonCode)
        {
            using var scope = ContentValidationMarker.Auto();
            reasonCode = "content_not_ready";
            // 房间在只有房主时也可先配置模式/扩展；实际开局人数由房间就绪策略检查。
            if (configuration == null) return false;
            var result = Resolve(configuration);
            reasonCode = result.Reason;
            // 调用方声称的指纹不是缓存键。每次都比对，避免错误指纹污染成功缓存或绕过最终门禁。
            if (reasonCode.Length == 0 && !string.IsNullOrEmpty(configuration.ContentPackHash) && configuration.ContentPackHash != result.Hash)
                reasonCode = "content_version_mismatch";
            return reasonCode.Length == 0;
        }

        private Capability Resolve(GameRuleConfiguration configuration)
        {
            if (configuration == null) return new Capability { Reason = "content_not_ready" };
            var copy = configuration.Clone();
            // 先验证，不能排序去重后把重复扩展或不支持的组合变成合法键。
            bool hasLocks = copy.LockedContentPacks != null && copy.LockedContentPacks.Count > 0;
            string reason;
            if (!copy.TryValidateStructure(out reason)) return new Capability { Reason = reason };
            copy.EnabledExpansionIds.Sort(StringComparer.Ordinal);
            copy.ContentPackHash = string.Empty;
            // 仅有效规则影响目录；房间/准备态修订不改变已安装内容。合法字段由 TryValidate 限定取值。
            var key = copy.SchemaVersion + "|" + copy.RuleContractVersion + "|" + copy.RuleModeId + "|" + copy.OfficialPreparationContract + "|" +
                string.Join(",", (copy.AdditionalRuleModeIds ?? new List<string>()).OrderBy(id => id, StringComparer.Ordinal)) + "|" +
                copy.MapId + "|" + copy.PlayerCount + "|" + string.Join(",", copy.EnabledExpansionIds) + "|" +
                string.Join(";", copy.LockedContentPacks.OrderBy(x => x.PackId, StringComparer.Ordinal).Select(x => x.PackId + "@" + x.Version + ":" + x.ContentHash));
            lock (cacheLock)
            {
                try
                {
                    var pack = installedPackSource();
                    if (pack == null) return new Capability { Reason = "content_not_ready" };
                    if (!ReferenceEquals(cachedPack, pack) || cachedSnapshotHash != pack.ContentHash)
                    {
                        capabilities.Clear();
                        cachedPack = pack;
                        cachedSnapshotHash = pack.ContentHash;
                        cachedCoreOptions = null;
                    }
                    if (!hasLocks && !copy.TryValidate(cachedCoreOptions ?? (cachedCoreOptions = GameplayOptionsCatalog.CoreOnly(pack)), out reason))
                        return new Capability { Reason = reason };
                    if (capabilities.TryGetValue(key, out var cached)) return cached;
                    Capability result;
                    try
                    {
                        CatalogBuildCount++;
                        if (hasLocks && diyRepositorySource == null) result = new Capability { Reason = "content_not_ready" };
                        else result = new Capability { Hash = hasLocks
                            ? ActiveContentCatalogBuilder.Build(pack, copy, diyRepositorySource().ResolveExact(copy)).ContentPackHash
                            : ActiveContentCatalogBuilder.Build(pack, copy).ContentPackHash };
                    }
                    catch (InvalidOperationException error) { result = Failure(error); }
                    catch (InvalidDataException error) { result = new Capability { Reason = hasLocks ? error.Message : "content_not_ready" }; }
                    catch (IOException error) { result = new Capability { Reason = hasLocks ? error.Message : "content_not_ready" }; }
                    catch (UnauthorizedAccessException error) { result = new Capability { Reason = hasLocks ? error.Message : "content_not_ready" }; }
                    // 同一快照的内容缺失也可复用；新快照会清除失败结果。
                    capabilities.Add(key, result);
                    return result;
                }
                catch (InvalidOperationException error) { return Failure(error); }
                catch (InvalidDataException) { return new Capability { Reason = "content_not_ready" }; }
                catch (IOException error) { return new Capability { Reason = hasLocks ? error.Message : "content_not_ready" }; }
                catch (UnauthorizedAccessException error) { return new Capability { Reason = hasLocks ? error.Message : "content_not_ready" }; }
            }
        }

        private static Capability Failure(InvalidOperationException error) =>
            new Capability { Reason = string.IsNullOrEmpty(error.Message) ? "content_not_ready" : error.Message };
    }
}
