using System;
using System.Collections.Generic;
using YC.Domain.Maps;
using Newtonsoft.Json;

namespace YC.Domain.Rules
{
    public enum PlayerStageSchedulingMode { Concurrent = 0, Sequential = 1 }

    [Serializable]
    public sealed class GameRuleConfiguration
    {
        public const string CurrentRuleContractVersion = "expansion-rules.v1";
        public int SchemaVersion = 1;
        public string MapId = "map-three-players";
        public string RuleModeId = "standard";
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> EnabledExpansionIds = new List<string> { "core" };
        public int PlayerCount;
        public int ConfigurationRevision;
        public string ContentPackHash = string.Empty;
        public string RuleContractVersion = CurrentRuleContractVersion;
        // 旧快照缺省维持已有并发语义；正式开局由 LaunchMode 显式装配。
        public PlayerStageSchedulingMode PlayerStageSchedulingMode = PlayerStageSchedulingMode.Concurrent;

        public GameRuleConfiguration Clone() => new GameRuleConfiguration
        {
            SchemaVersion = SchemaVersion, MapId = MapId, RuleModeId = RuleModeId,
            EnabledExpansionIds = EnabledExpansionIds == null ? null : new List<string>(EnabledExpansionIds),
            PlayerCount = PlayerCount, ConfigurationRevision = ConfigurationRevision,
            ContentPackHash = ContentPackHash, RuleContractVersion = RuleContractVersion,
            PlayerStageSchedulingMode = PlayerStageSchedulingMode
        };

        public bool TryValidate(out string reasonCode)
        {
            reasonCode = string.Empty;
            if (SchemaVersion != 1 || RuleContractVersion != CurrentRuleContractVersion)
                reasonCode = "content_version_mismatch";
            else if (!Enum.IsDefined(typeof(PlayerStageSchedulingMode), PlayerStageSchedulingMode))
                reasonCode = "player_stage_scheduling_invalid";
            else if (RuleModeId != "standard") reasonCode = "rule_mode_not_implemented";
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
            return reasonCode.Length == 0;
        }
    }
}
