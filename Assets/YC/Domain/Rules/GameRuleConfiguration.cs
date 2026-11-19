using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Maps;
using Newtonsoft.Json;

namespace YC.Domain.Rules
{
    public enum PlayerStageSchedulingMode { Concurrent = 0, Sequential = 1 }

    /// <summary>由冻结内容提供的玩法目录校验身份、适用范围及依赖。</summary>
    public interface IGameRuleCatalog
    {
        bool TryValidateConfiguration(GameRuleConfiguration configuration, out string reasonCode);
    }

    [Serializable]
    public sealed class ContentPackLock
    {
        public string PackId = string.Empty;
        public string Version = string.Empty;
        public string ContentHash = string.Empty;
        public ContentPackLock Clone() => new ContentPackLock { PackId = PackId, Version = Version, ContentHash = ContentHash };
    }

    [Serializable]
    public sealed class GameRuleConfiguration
    {
        public const int CurrentSchemaVersion = 2;
        public const string CurrentRuleContractVersion = "expansion-rules.v2";
        public const string OfficialPreparationVersion = "official.preparation.v1";
        public int SchemaVersion = CurrentSchemaVersion;
        public string MapId = "map-three-players";
        public string RuleModeId = "standard";
        // RuleModeId 是基础模式；其余模式与其共同生效。空值保留原有单模式存档形状。
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> AdditionalRuleModeIds;
        [JsonIgnore]
        public IReadOnlyList<string> ActiveRuleModeIds => new[] { RuleModeId }
            .Concat((IEnumerable<string>)AdditionalRuleModeIds ?? Enumerable.Empty<string>()).ToArray();
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> EnabledExpansionIds = new List<string> { "core" };
        public int PlayerCount;
        public int ConfigurationRevision;
        public string ContentPackHash = string.Empty;
        public string RuleContractVersion = CurrentRuleContractVersion;
        // 空值保留旧模式声明与旧存档指纹；新官方流程显式锁定声明版本。
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string OfficialPreparationContract;
        // 本局精确包来源；空列表兼容未启用 DIY 的既有配置，不读取本机默认版本。
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<ContentPackLock> LockedContentPacks = new List<ContentPackLock>();
        // 旧快照缺省维持已有并发语义；正式开局由 LaunchMode 显式装配。
        public PlayerStageSchedulingMode PlayerStageSchedulingMode = PlayerStageSchedulingMode.Concurrent;

        public GameRuleConfiguration Clone() => new GameRuleConfiguration
        {
            SchemaVersion = SchemaVersion, MapId = MapId, RuleModeId = RuleModeId,
            AdditionalRuleModeIds = AdditionalRuleModeIds == null ? null : new List<string>(AdditionalRuleModeIds),
            EnabledExpansionIds = EnabledExpansionIds == null ? null : new List<string>(EnabledExpansionIds),
            PlayerCount = PlayerCount, ConfigurationRevision = ConfigurationRevision,
            ContentPackHash = ContentPackHash, RuleContractVersion = RuleContractVersion, OfficialPreparationContract = OfficialPreparationContract,
            PlayerStageSchedulingMode = PlayerStageSchedulingMode,
            LockedContentPacks = LockedContentPacks == null ? null : LockedContentPacks.ConvertAll(item => item?.Clone())
        };

        /// <summary>只校验协议形状；玩法身份及依赖必须再由冻结目录验证。</summary>
        public bool TryValidateStructure(out string reasonCode)
        {
            reasonCode = string.Empty;
            if (SchemaVersion != CurrentSchemaVersion || RuleContractVersion != CurrentRuleContractVersion)
                reasonCode = "content_version_mismatch";
            else if (OfficialPreparationContract != null && OfficialPreparationContract != OfficialPreparationVersion)
                reasonCode = "preparation_contract_invalid";
            else if (AdditionalRuleModeIds != null && AdditionalRuleModeIds.Count > 0 && OfficialPreparationContract == null)
                reasonCode = "preparation_contract_invalid";
            else if (!Enum.IsDefined(typeof(PlayerStageSchedulingMode), PlayerStageSchedulingMode)) reasonCode = "player_stage_scheduling_invalid";
            else if (!ValidIdentity(RuleModeId)) reasonCode = "rule_mode_not_implemented";
            else if (PlayerCount < 0 || PlayerCount > 4) reasonCode = "unsupported_player_count";
            else if (MapId != StaticMapDefinitions.ThreePlayerMapId && MapId != StaticMapDefinitions.FourPlayerMapId) reasonCode = "room_protocol_mismatch";
            else if (PlayerCount == 4 && MapId != StaticMapDefinitions.FourPlayerMapId) reasonCode = "map_requires_four_player";
            else if (EnabledExpansionIds == null || !EnabledExpansionIds.Contains("core")) reasonCode = "content_not_ready";
            else if (LockedContentPacks == null || LockedContentPacks.Count > 256) reasonCode = "content_lock_invalid";
            else
            {
                var modes = new HashSet<string>(StringComparer.Ordinal) { RuleModeId };
                foreach (var id in (IEnumerable<string>)AdditionalRuleModeIds ?? Enumerable.Empty<string>())
                    if (!ValidIdentity(id) || !modes.Add(id)) { reasonCode = "rule_mode_not_implemented"; break; }
                var expansions = new HashSet<string>(StringComparer.Ordinal);
                if (reasonCode.Length == 0)
                    foreach (var id in EnabledExpansionIds)
                        if (!ValidIdentity(id) || !expansions.Add(id)) { reasonCode = "content_not_ready"; break; }
                var packs = new HashSet<string>(StringComparer.Ordinal);
                if (reasonCode.Length == 0)
                    foreach (var item in LockedContentPacks)
                        if (item == null || !ValidIdentity(item.PackId) || !packs.Add(item.PackId) || !ValidVersion(item.Version) || !ValidHash(item.ContentHash))
                        { reasonCode = "content_lock_invalid"; break; }
            }
            return reasonCode.Length == 0;
        }

        private static bool ValidIdentity(string value) => !string.IsNullOrEmpty(value) && value.Length <= 192 &&
            System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9]+(?:[._-][A-Za-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        private static bool ValidVersion(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            var parts = value.Split('.');
            if (parts.Length != 3) return false;
            foreach (string part in parts)
                if (part.Length == 0 || part.Length > 1 && part[0] == '0' || !int.TryParse(part, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out _)) return false;
            return true;
        }
        private static bool ValidHash(string value) => value != null && value.Length == 64 &&
            System.Text.RegularExpressions.Regex.IsMatch(value, @"^[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>已由内容提供者校验的开局/恢复入口使用；此方法本身只校验锁定形状。</summary>
        public bool TryValidateFrozenShape(out string reasonCode)
        {
            if (!TryValidateStructure(out reasonCode)) return false;
            if (LockedContentPacks.Count == 0) return true;
            if (LockedContentPacks.Count < 2 || !LockedContentPacks.Exists(p => p.PackId == "core") || string.IsNullOrEmpty(ContentPackHash))
            { reasonCode = "content_lock_invalid"; return false; }
            return true;
        }

        public bool TryValidate(IGameRuleCatalog catalog, out string reasonCode)
        {
            if (!TryValidateStructure(out reasonCode)) return false;
            if (catalog == null) { reasonCode = "content_not_ready"; return false; }
            return catalog.TryValidateConfiguration(this, out reasonCode);
        }

        // 未提供内容快照的旧调用仅支持既有内置协议；正式装配使用带目录的重载。
        public bool TryValidate(out string reasonCode)
        {
            reasonCode = string.Empty;
            if (SchemaVersion != CurrentSchemaVersion || RuleContractVersion != CurrentRuleContractVersion)
                reasonCode = "content_version_mismatch";
            else if (!Enum.IsDefined(typeof(PlayerStageSchedulingMode), PlayerStageSchedulingMode))
                reasonCode = "player_stage_scheduling_invalid";
            else if (AdditionalRuleModeIds != null && AdditionalRuleModeIds.Count > 0) reasonCode = "content_not_ready";
            else if (RuleModeId != "standard" && RuleModeId != "entrepreneur") reasonCode = "rule_mode_not_implemented";
            else if (RuleModeId == "entrepreneur" && (EnabledExpansionIds == null || !EnabledExpansionIds.Contains("enterprise") || !EnabledExpansionIds.Contains("infected"))) reasonCode = "missing_entrepreneur_dependency";
            else if (PlayerCount < 0 || PlayerCount > 4) reasonCode = "unsupported_player_count";
            else if (MapId != StaticMapDefinitions.ThreePlayerMapId && MapId != StaticMapDefinitions.FourPlayerMapId)
                reasonCode = "room_protocol_mismatch";
            else if (PlayerCount == 4 && MapId != StaticMapDefinitions.FourPlayerMapId)
                reasonCode = "map_requires_four_player";
            else if (EnabledExpansionIds == null || !EnabledExpansionIds.Contains("core"))
                reasonCode = "content_not_ready";
            else if (EnabledExpansionIds.Contains("infected") && !EnabledExpansionIds.Contains("enterprise"))
                reasonCode = "missing_enterprise_dependency";
            else
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in EnabledExpansionIds)
                    if (!ids.Add(id) || (id != "core" && id != "enterprise" && id != "infected"))
                    { reasonCode = "content_not_ready"; break; }
            }
            if (reasonCode.Length == 0) TryValidateStructure(out reasonCode);
            return reasonCode.Length == 0;
        }
    }
}
