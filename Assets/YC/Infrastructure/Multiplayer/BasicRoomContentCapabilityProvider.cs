using System;
using System.Collections.Generic;
using System.IO;
using Unity.Profiling;
using YC.Domain.Rules;
using YC.Infrastructure.Lua;

namespace YC.Infrastructure.Multiplayer
{
    public sealed class BasicRoomContentCapabilityProvider : IRoomContentCapabilityProvider
    {
        private static readonly ProfilerMarker ContentValidationMarker = new ProfilerMarker("YC.RoomContentValidation");
        private static readonly ProfilerMarker ContentHashMarker = new ProfilerMarker("YC.RoomContentHash");
        private readonly Func<ExternalContentPack> installedPackSource;
        private readonly object cacheLock = new object();
        private readonly Dictionary<string, Capability> capabilities = new Dictionary<string, Capability>(StringComparer.Ordinal);
        private ExternalContentPack cachedPack;
        private string cachedSnapshotHash;
        internal int CatalogBuildCount { get; private set; }

        private sealed class Capability
        {
            public string Hash = string.Empty;
            public string Reason = string.Empty;
        }

        public BasicRoomContentCapabilityProvider() : this(() => YC.Presentation.ExternalContentRuntime.Pack)
        { installedPackSource(); }

        // 内容包已把定义、脚本和图片加载为隔离快照；重载时提供新快照，不能永久捕获首次加载的包。
        public BasicRoomContentCapabilityProvider(Func<ExternalContentPack> installedPackSource)
        { this.installedPackSource = installedPackSource ?? throw new ArgumentNullException(nameof(installedPackSource)); }

        public string GetContentPackHash(GameRuleConfiguration configuration)
        {
            using var scope = ContentHashMarker.Auto();
            return Resolve(configuration).Hash;
        }

        public bool TryValidate(GameRuleConfiguration configuration, out string reasonCode)
        {
            using var scope = ContentValidationMarker.Auto();
            reasonCode = "content_not_ready";
            if (configuration == null || configuration.PlayerCount < 2) return false;
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
            if (!copy.TryValidate(out var reason)) return new Capability { Reason = reason };
            copy.EnabledExpansionIds.Sort(StringComparer.Ordinal);
            copy.ContentPackHash = string.Empty;
            // 仅有效规则影响目录；房间/准备态修订不改变已安装内容。合法字段由 TryValidate 限定取值。
            var key = copy.SchemaVersion + "|" + copy.RuleContractVersion + "|" + copy.RuleModeId + "|" +
                copy.MapId + "|" + copy.PlayerCount + "|" + string.Join(",", copy.EnabledExpansionIds);
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
                    }
                    if (capabilities.TryGetValue(key, out var cached)) return cached;
                    Capability result;
                    try
                    {
                        CatalogBuildCount++;
                        result = new Capability { Hash = ActiveContentCatalogBuilder.Build(pack, copy).ContentPackHash };
                    }
                    catch (InvalidOperationException error) { result = Failure(error); }
                    catch (InvalidDataException) { result = new Capability { Reason = "content_not_ready" }; }
                    // 同一快照的内容缺失也可复用；新快照会清除失败结果。
                    capabilities.Add(key, result);
                    return result;
                }
                catch (InvalidOperationException error) { return Failure(error); }
                catch (InvalidDataException) { return new Capability { Reason = "content_not_ready" }; }
            }
        }

        private static Capability Failure(InvalidOperationException error) =>
            new Capability { Reason = string.IsNullOrEmpty(error.Message) ? "content_not_ready" : error.Message };
    }
}
