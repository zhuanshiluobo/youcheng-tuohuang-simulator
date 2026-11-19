using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YC.Infrastructure.Lua
{
    /// <summary>游戏通过 Application.persistentDataPath 传入真实构建的当前用户目录。</summary>
    public sealed class DiyDataPaths
    {
        public string DataRoot { get; }
        public string Mods => Path.Combine(DataRoot, "Mods");
        public string Profiles => Path.Combine(DataRoot, "ModProfiles");
        public string Receipts => Path.Combine(DataRoot, "ModInstallReceipts");
        public string Staging => Path.Combine(DataRoot, "ModStaging");
        public DiyDataPaths(string persistentDataPath)
        {
            if (string.IsNullOrWhiteSpace(persistentDataPath) || !Path.IsPathRooted(persistentDataPath))
                throw new ArgumentException("必须提供当前构建的绝对用户数据路径", nameof(persistentDataPath));
            DataRoot = Path.GetFullPath(persistentDataPath);
        }
    }

    public enum DiyDiscoveryStatus { Parsed, Damaged, Conflict }

    public sealed class DiyDiscoveredPack
    {
        public string PackId { get; internal set; }
        public string Version { get; internal set; }
        public string Directory { get; internal set; }
        public DiyDiscoveryStatus Status { get; internal set; }
        public string Diagnostic { get; internal set; }
        public DiyPackSnapshot Snapshot { get; internal set; }
    }

    /// <summary>只发现 Mods/包 ID/版本；隔离暂存、备份及损坏包，不运行包内代码。</summary>
    public static class DiyPackDiscovery
    {
        public static IReadOnlyList<DiyDiscoveredPack> Scan(DiyDataPaths paths, Action<byte[], string, int, int> imageDecoder = null)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            var result = new List<DiyDiscoveredPack>();
            if (!Directory.Exists(paths.Mods)) return result.AsReadOnly();
            try
            {
                DiyPackPaths.RejectLinksToRoot(paths.Mods);
                var packs = Directory.GetDirectories(paths.Mods).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var packNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string packDirectory in packs)
                {
                    string id = Path.GetFileName(packDirectory);
                    if (!DiyIdentifiers.ValidPackId(id) || id == "core" || !packNames.Add(id))
                    { result.Add(Bad(id, "", packDirectory, "包目录名无效或大小写冲突")); continue; }
                    try
                    {
                        DiyPackPaths.RejectLinksToRoot(packDirectory);
                        var versions = Directory.GetDirectories(packDirectory).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                        foreach (string versionDirectory in versions)
                        {
                            string version = Path.GetFileName(versionDirectory);
                            try
                            {
                                DiyVersion.Parse(version, "版本目录");
                                DiyPackPaths.RejectLinksToRoot(versionDirectory);
                                var snapshot = DiyPackSnapshot.Load(versionDirectory);
                                if (imageDecoder != null) snapshot.ValidateImages(imageDecoder);
                                if (snapshot.Manifest.PackId != id || snapshot.Manifest.Version != version)
                                    throw new InvalidDataException("目录身份与 pack.json 的 packId/version 不一致");
                                result.Add(new DiyDiscoveredPack { PackId = id, Version = version, Directory = versionDirectory,
                                    Status = DiyDiscoveryStatus.Parsed, Diagnostic = "包内初步校验通过，依赖和活动装配尚未验证", Snapshot = snapshot });
                            }
                            catch (Exception error) when (Expected(error)) { result.Add(Bad(id, version, versionDirectory, error.Message)); }
                        }
                    }
                    catch (Exception error) when (Expected(error)) { result.Add(Bad(id, "", packDirectory, error.Message)); }
                }
            }
            catch (Exception error) when (Expected(error)) { result.Add(Bad("", "", paths.Mods, error.Message)); }
            return result.AsReadOnly();
        }
        private static bool Expected(Exception error) => error is InvalidDataException || error is IOException || error is UnauthorizedAccessException ||
            error is ArgumentException || error is NotSupportedException || error is System.Security.SecurityException || error is JsonException;
        private static DiyDiscoveredPack Bad(string id, string version, string directory, string diagnostic) =>
            new DiyDiscoveredPack { PackId = id, Version = version, Directory = directory, Status = DiyDiscoveryStatus.Damaged, Diagnostic = diagnostic };
    }

    /// <summary>房间外的本地版本绑定。不会在发现新版时替换已有选择。</summary>
    public sealed class DiyVersionProfile
    {
        private readonly Dictionary<string, string> bindings;
        public IReadOnlyDictionary<string, string> Bindings => new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(bindings);
        private DiyVersionProfile(Dictionary<string, string> bindings) { this.bindings = bindings; }
        public static DiyVersionProfile Empty() => new DiyVersionProfile(new Dictionary<string, string>(StringComparer.Ordinal));
        public static DiyVersionProfile Load(string path)
        {
            if (!File.Exists(path)) return Empty();
            DiyPackPaths.RejectLinksToRoot(path);
            JObject json;
            try { json = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); }
            catch (JsonException error) { throw new InvalidDataException("DIY 版本配置无效：" + path, error); }
            DiyPackManifest.Fields(json, path, "schemaVersion", "bindings");
            if (DiyPackManifest.Integer(json, "schemaVersion", path) != 1 || !(json["bindings"] is JObject items)) throw new InvalidDataException("DIY 版本配置版本或绑定无效：" + path);
            var result = Empty();
            foreach (var property in items.Properties())
            {
                if (!DiyIdentifiers.ValidPackId(property.Name) || property.Name == "core" || property.Value.Type != JTokenType.String)
                    throw new InvalidDataException("DIY 版本绑定无效：" + property.Name);
                string version = (string)property.Value; DiyVersion.Parse(version, path + ":" + property.Name);
                result.bindings.Add(property.Name, version);
            }
            return result;
        }
        public DiyDiscoveredPack Resolve(string packId, IReadOnlyList<DiyDiscoveredPack> discovered)
        {
            if (!DiyIdentifiers.ValidPackId(packId) || discovered == null) throw new ArgumentException("包 ID 或发现结果无效");
            var candidates = discovered.Where(x => x.PackId == packId && x.Status == DiyDiscoveryStatus.Parsed).ToArray();
            if (bindings.TryGetValue(packId, out string pinned))
                return candidates.SingleOrDefault(x => x.Version == pinned) ?? throw new InvalidDataException("已绑定的包版本不存在或损坏：" + packId + "/" + pinned);
            if (candidates.Length != 1) throw new InvalidDataException(candidates.Length == 0 ? "没有可用的包版本：" + packId : "存在多个版本，必须在房间外配置绑定：" + packId);
            return candidates[0];
        }
        public bool BindUniqueDefaults(IReadOnlyList<DiyDiscoveredPack> discovered)
        {
            if (discovered == null) throw new ArgumentNullException(nameof(discovered));
            bool changed = false;
            foreach (var group in discovered.Where(x => x.Status == DiyDiscoveryStatus.Parsed).GroupBy(x => x.PackId, StringComparer.Ordinal))
                if (!bindings.ContainsKey(group.Key) && group.Count() == 1)
                { bindings.Add(group.Key, group.Single().Version); changed = true; }
            return changed;
        }
        public void SetBinding(string packId, string version, IReadOnlyList<DiyDiscoveredPack> discovered)
        {
            if (!DiyIdentifiers.ValidPackId(packId) || packId == "core") throw new ArgumentException("包 ID 无效", nameof(packId));
            DiyVersion.Parse(version, "version");
            if (discovered == null || !discovered.Any(x => x.PackId == packId && x.Version == version && x.Status == DiyDiscoveryStatus.Parsed))
                throw new InvalidDataException("指定包版本不存在或损坏：" + packId + "/" + version);
            bindings[packId] = version;
        }
        public void Save(string path)
        {
            var items = new JObject();
            foreach (var pair in bindings.OrderBy(x => x.Key, StringComparer.Ordinal)) items.Add(pair.Key, pair.Value);
            SaveJson(path, new JObject { ["schemaVersion"] = 1, ["bindings"] = items });
        }

        internal static void SaveJson(string path, JObject value)
        {
            string full = Path.GetFullPath(path), parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent)) throw new InvalidDataException("版本配置缺少父目录");
            // 创建缺失目录前先检查最近的已有祖先，避免经目录联接创建到预期用户目录之外。
            string existing = parent;
            while (!Directory.Exists(existing) && !File.Exists(existing))
            {
                existing = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(existing)) throw new InvalidDataException("版本配置无法定位已有父目录");
            }
            DiyPackPaths.RejectLinksToRoot(existing);
            Directory.CreateDirectory(parent);
            DiyPackPaths.RejectLinksToRoot(parent);
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("版本配置不能是链接：" + full);
            string temp = Path.Combine(parent, ".mod-profile-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, value.ToString(Formatting.Indented), new System.Text.UTF8Encoding(false));
            if (File.Exists(full)) File.Replace(temp, full, null); else File.Move(temp, full);
        }
    }

    public sealed class DiyLocalPackVersion
    {
        public string PackId { get; internal set; }
        public string Version { get; internal set; }
        public string DisplayName { get; internal set; }
        public string Author { get; internal set; }
        public string Description { get; internal set; }
        public string ContentHash { get; internal set; }
        public string Diagnostic { get; internal set; }
        public bool Available { get; internal set; }
        public bool IsBound { get; internal set; }
    }

    /// <summary>房间外的本地配置；保存仅影响下次进程启动，不替换当前冻结目录。</summary>
    public sealed class DiyLocalContentConfiguration
    {
        private readonly DiyDataPaths paths;
        private readonly Action<byte[], string, int, int> imageDecoder;
        public DiyLocalContentConfiguration(DiyDataPaths paths, Action<byte[], string, int, int> imageDecoder = null)
        { this.paths = paths ?? throw new ArgumentNullException(nameof(paths)); this.imageDecoder = imageDecoder; }

        public IReadOnlyList<DiyLocalPackVersion> Inspect()
        {
            var found = DiyPackDiscovery.Scan(paths, imageDecoder);
            var profile = DiyVersionProfile.Load(Path.Combine(paths.Profiles, "default.json"));
            var result = new List<DiyLocalPackVersion>();
            foreach (var row in found)
            {
                var manifest = row.Snapshot?.Manifest;
                result.Add(new DiyLocalPackVersion { PackId = row.PackId ?? "", Version = row.Version ?? "",
                    DisplayName = manifest?.DisplayName ?? "", Author = manifest?.Author ?? "", Description = manifest?.Description ?? "",
                    ContentHash = row.Snapshot?.ContentHash ?? "", Diagnostic = row.Diagnostic ?? "",
                    Available = row.Status == DiyDiscoveryStatus.Parsed,
                    IsBound = profile.Bindings.TryGetValue(row.PackId ?? "", out string selected) && selected == row.Version });
            }
            foreach (var pair in profile.Bindings)
                if (!result.Any(row => row.PackId == pair.Key && row.Version == pair.Value))
                    result.Add(new DiyLocalPackVersion { PackId = pair.Key, Version = pair.Value, DisplayName = "", Author = "", Description = "",
                        ContentHash = "", Diagnostic = "已绑定的版本不存在", Available = false, IsBound = true });
            return result.OrderBy(row => row.PackId, StringComparer.Ordinal).ThenBy(row => row.Version, StringComparer.Ordinal).ToList().AsReadOnly();
        }

        public void SetNextLaunchBinding(string packId, string version)
        {
            var found = DiyPackDiscovery.Scan(paths, imageDecoder);
            var profilePath = Path.Combine(paths.Profiles, "default.json");
            var next = DiyVersionProfile.Load(profilePath);
            next.SetBinding(packId, version, found);
            next.Save(profilePath);
        }

        public YC.Domain.Rules.GameRuleConfiguration LoadNewGamePreference()
        {
            string path = Path.Combine(paths.Profiles, "new-game.json");
            if (!File.Exists(path)) return null;
            DiyPackPaths.RejectLinksToRoot(path);
            JObject value;
            try { value = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); }
            catch (JsonException error) { throw new InvalidDataException("新局玩法配置无效：" + path, error); }
            DiyPackManifest.Fields(value, path, "schemaVersion", "mapId", "modeId", "additionalModeIds", "expansionIds");
            if (DiyPackManifest.Integer(value, "schemaVersion", path) != 1) throw new InvalidDataException("新局玩法配置版本不支持");
            var configuration = new YC.Domain.Rules.GameRuleConfiguration { MapId = DiyPackManifest.Text(value, "mapId", path),
                RuleModeId = DiyPackManifest.Text(value, "modeId", path), AdditionalRuleModeIds = value["additionalModeIds"] == null ? null : DiyPackManifest.Strings(value, "additionalModeIds", path).ToList(),
                EnabledExpansionIds = DiyPackManifest.Strings(value, "expansionIds", path, true).ToList() };
            if (configuration.AdditionalRuleModeIds?.Count > 0)
                configuration.OfficialPreparationContract = YC.Domain.Rules.GameRuleConfiguration.OfficialPreparationVersion;
            if (!configuration.TryValidateStructure(out string reason)) throw new InvalidDataException("新局玩法配置无效：" + reason);
            return configuration;
        }

        public void SaveNewGamePreference(YC.Domain.Rules.GameRuleConfiguration configuration)
        {
            if (configuration == null || !configuration.TryValidateStructure(out _)) throw new InvalidDataException("新局玩法配置无效");
            var value = new JObject { ["schemaVersion"] = 1, ["mapId"] = configuration.MapId, ["modeId"] = configuration.RuleModeId,
                ["expansionIds"] = new JArray(configuration.EnabledExpansionIds.OrderBy(x => x, StringComparer.Ordinal)) };
            if (configuration.AdditionalRuleModeIds?.Count > 0)
                value["additionalModeIds"] = new JArray(configuration.AdditionalRuleModeIds.OrderBy(id => id, StringComparer.Ordinal));
            DiyVersionProfile.SaveJson(Path.Combine(paths.Profiles, "new-game.json"), value);
        }

        public string EnsureModsDirectory()
        {
            string directory = paths.Mods, existing = directory;
            while (!Directory.Exists(existing) && !File.Exists(existing))
            {
                existing = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(existing)) throw new InvalidDataException("DIY 目录无法定位已有父目录");
            }
            if (!Directory.Exists(existing)) throw new InvalidDataException("DIY 目录的父路径不是目录：" + existing);
            DiyPackPaths.RejectLinksToRoot(existing);
            Directory.CreateDirectory(directory);
            DiyPackPaths.RejectLinksToRoot(directory);
            return directory;
        }
    }
}
