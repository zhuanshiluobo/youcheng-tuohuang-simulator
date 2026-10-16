using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Domain.Enterprises
{
    public sealed class EnterpriseStateQuery
    {
        public const string ActiveDepartmentRelation = "enterprise.active_department";
        private readonly EnterpriseDefinitions definitions;
        public EnterpriseStateQuery(EnterpriseDefinitions definitions = null) { this.definitions = definitions ?? new EnterpriseDefinitions(); }

        public int GetLevel(GameState state, int playerId, string enterpriseItemId)
        {
            var marker = GetCooperationMarker(state, playerId, enterpriseItemId);
            if (!marker.SlotId.StartsWith("level_", StringComparison.Ordinal) || !int.TryParse(marker.SlotId.Substring(6), NumberStyles.None, CultureInfo.InvariantCulture, out var level) ||
                level < 0 || level > 5 || marker.SlotId != "level_" + level) throw new InvalidOperationException("enterprise_level_slot_invalid");
            return level;
        }
        internal ItemInstanceState GetCooperationMarker(GameState state, int playerId, string enterpriseItemId)
        {
            if (state?.FindPlayer(playerId) == null) throw new InvalidOperationException("invalid_player");
            var enterprise = state.ItemZones.Items.SingleOrDefault(item => item.ItemId == enterpriseItemId);
            if (enterprise == null || enterprise.TypeId != "core.board_card" || state.ItemZones.Zones.SingleOrDefault(zone => zone.ZoneId == enterprise.ZoneId)?.TemplateId != "core.zone.enterprise_display")
                throw new InvalidOperationException("enterprise_not_active");
            var tracks = state.ItemZones.Zones.Where(zone => zone.ParentKind == ZoneParentKind.Item && zone.ParentId == enterpriseItemId &&
                zone.TemplateId == "core.zone.board_marker_track" && zone.LocalZoneId == "cooperation_" + playerId && zone.OwnerPlayerId == playerId).ToList();
            if (tracks.Count != 1) throw new InvalidOperationException("enterprise_cooperation_track_missing");
            var markers = state.ItemZones.Items.Where(item => item.ZoneId == tracks[0].ZoneId).ToList();
            if (markers.Count != 1 || markers[0].OwnerSubject.Kind != ItemSubjectKind.Player || markers[0].OwnerSubject.PlayerId != playerId || markers[0].TypeId != "core.player_marker")
                throw new InvalidOperationException("enterprise_cooperation_marker_missing");
            return markers[0];
        }

        public IReadOnlyList<EnterpriseSpecialCandidate> GetSpecialCandidates(GameState state, int executingPlayerId, int unlockedByPlayerId, int levelDelta, IReadOnlyList<string> enterpriseItemIds)
        {
            if (state?.FindPlayer(executingPlayerId) == null || state.FindPlayer(unlockedByPlayerId) == null) throw new InvalidOperationException("invalid_player");
            if (levelDelta < 0) throw new InvalidOperationException("enterprise_candidate_delta_invalid");
            var result = new List<EnterpriseSpecialCandidate>();
            foreach (string enterpriseItemId in GetEnterpriseItemIds(state, enterpriseItemIds))
            {
                var definition = RequireDefinition(state, enterpriseItemId);
                int currentLevel = GetLevel(state, unlockedByPlayerId, enterpriseItemId);
                int effectiveLevelCeiling = (int)Math.Min(5L, (long)currentLevel + levelDelta);
                foreach (var level in definition.Levels.Where(level => level.Kind == "special" && level.Level <= effectiveLevelCeiling).OrderBy(level => level.Level))
                    result.Add(new EnterpriseSpecialCandidate { CandidateId = StableIdFactory.Create("candidate", enterpriseItemId, level.AbilityId), EnterpriseItemId = enterpriseItemId,
                        SpecialId = level.AbilityId, CurrentLevel = currentLevel, EffectiveLevelCeiling = effectiveLevelCeiling, DisplayNameKey = level.AbilityId });
            }
            return result.OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal).ToList().AsReadOnly();
        }

        public IReadOnlyList<string> GetEnterpriseItemIds(GameState state, IReadOnlyList<string> requested = null)
        {
            if (state.ItemZones.Items.Any(item => state.ItemZones.Zones.Find(zone => zone.ZoneId == item.ZoneId)?.TemplateId == "core.zone.enterprise_display" &&
                state.ItemZones.Zones.Any(zone => zone.ParentKind == ZoneParentKind.Item && zone.ParentId == item.ItemId && zone.LocalZoneId.StartsWith("cooperation_", StringComparison.Ordinal)) &&
                definitions.FindEnterprise(item.DefinitionId) == null)) throw new InvalidOperationException("enterprise_definition_missing");
            var ids = state.ItemZones.Items.Where(item => definitions.FindEnterprise(item.DefinitionId) != null && state.ItemZones.Zones.Find(zone => zone.ZoneId == item.ZoneId)?.TemplateId == "core.zone.enterprise_display")
                .Select(item => item.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (requested == null) return ids.AsReadOnly();
            if (requested.Distinct(StringComparer.Ordinal).Count() != requested.Count || requested.Any(id => !ids.Contains(id))) throw new InvalidOperationException("enterprise_not_active");
            return ids.Where(requested.Contains).ToList().AsReadOnly();
        }
        internal EnterpriseDefinition RequireDefinition(GameState state, string enterpriseItemId)
        {
            var item = state.ItemZones.Items.SingleOrDefault(value => value.ItemId == enterpriseItemId);
            var definition = definitions.FindEnterprise(item?.DefinitionId);
            if (definition == null || !GetEnterpriseItemIds(state).Contains(enterpriseItemId)) throw new InvalidOperationException("enterprise_definition_missing");
            return definition;
        }
        public string GetActiveDepartmentItemId(GameState state, string enterpriseItemId)
        {
            var definition = RequireDefinition(state, enterpriseItemId);
            var relations = state.ItemZones.Relations.Where(relation => relation.RelationTypeId == ActiveDepartmentRelation && relation.SourceItemId == enterpriseItemId).ToList();
            if (relations.Count != 1) throw new InvalidOperationException("enterprise_active_department_missing");
            var item = state.ItemZones.Items.SingleOrDefault(value => value.ItemId == relations[0].TargetItemId);
            var department = definitions.FindDepartment(item?.DefinitionId);
            if (department?.EnterpriseRuntimeId != definition.RuntimeId || state.ItemZones.Zones.Find(zone => zone.ZoneId == item.ZoneId)?.TemplateId != "core.zone.department_display")
                throw new InvalidOperationException("enterprise_active_department_invalid");
            return item.ItemId;
        }
        public IReadOnlyList<string> GetDepartmentCandidates(GameState state, string enterpriseItemId)
        {
            var definition = RequireDefinition(state, enterpriseItemId);
            if (definition.DepartmentRewardPolicy != "active_department") throw new InvalidOperationException("enterprise_department_policy_missing");
            string active = GetActiveDepartmentItemId(state, enterpriseItemId);
            return state.ItemZones.Items.Where(item => item.ItemId != active && definitions.FindDepartment(item.DefinitionId)?.EnterpriseRuntimeId == definition.RuntimeId &&
                state.ItemZones.Zones.Find(zone => zone.ZoneId == item.ZoneId)?.TemplateId == "core.zone.department_display").Select(item => item.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList().AsReadOnly();
        }
        internal string GetRewardAbilityId(GameState state, string enterpriseItemId, int level, out string departmentItemId)
        {
            departmentItemId = "";
            var definition = RequireDefinition(state, enterpriseItemId);
            var entry = definition.Levels.Single(value => value.Level == level);
            if (entry.Kind == "reward") return entry.AbilityId;
            if (entry.Kind != "department_reward") return "";
            string activeDepartmentItemId = GetActiveDepartmentItemId(state, enterpriseItemId);
            departmentItemId = activeDepartmentItemId;
            return definitions.FindDepartment(state.ItemZones.Items.Single(item => item.ItemId == activeDepartmentItemId).DefinitionId).Rewards.Single(value => value.Level == level).AbilityId;
        }
    }
}
