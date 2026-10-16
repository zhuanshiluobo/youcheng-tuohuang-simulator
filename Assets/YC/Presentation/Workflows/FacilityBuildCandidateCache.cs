using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using YC.Application.Gameplay;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    /// <summary>页面复用真实来源快照；费用刷新不会重复执行持续候选策略。</summary>
    public sealed class FacilityBuildCandidateCache
    {
        private readonly BuildFacilityOptionQueryService query;
        private int playerId = -1;
        private string sourceKey;
        private FacilityBuildCandidateSnapshot snapshot;

        public FacilityBuildCandidateCache(BuildFacilityOptionQueryService query)
        { this.query = query ?? throw new ArgumentNullException(nameof(query)); }

        public bool IsDefaultSupplySet => snapshot == null || snapshot.IsDefaultSupplySet;

        public void Invalidate()
        { playerId = -1; sourceKey = null; snapshot = null; }

        public IReadOnlyList<BuildFacilityOptionQueryResult> Query(GameState state, int player)
        {
            if (state == null) { Invalidate(); return Array.Empty<BuildFacilityOptionQueryResult>(); }
            if (!ItemZoneLegacyProjection.IsActive(state))
            { Invalidate(); return query.Query(state, player); }
            var key = SourceKey(state);
            if (snapshot == null || playerId != player || sourceKey != key)
            {
                snapshot = query.QueryCandidates(state, player);
                playerId = player; sourceKey = key;
            }
            return query.Query(state, player, snapshot);
        }

        private static string SourceKey(GameState state)
        {
            var key = new StringBuilder();
            void Add(string value) { value = value ?? ""; key.Append(value.Length).Append(':').Append(value); }
            Add(state.GameId); Add(state.ContentPackHash);
            var zones = state.ItemZones.Zones.Where(zone =>
                zone.TemplateId == "core.zone.facility_supply_slot" ||
                zone.TemplateId == "core.zone.card_facility_storage" ||
                zone.TemplateId == "core.zone.character_persistent").ToDictionary(zone => zone.ZoneId);
            foreach (var zone in zones.Values)
            {
                Add(zone.ZoneId); Add(zone.TemplateId); Add(zone.LocalZoneId);
                Add(zone.ParentKind.ToString()); Add(zone.ParentId); Add(zone.OwnerPlayerId.ToString());
            }
            foreach (var item in state.ItemZones.Items)
            {
                if (item.PhysicalKind != ItemPhysicalKind.Card || !zones.ContainsKey(item.ZoneId)) continue;
                Add(item.ItemId); Add(item.DefinitionId); Add(item.TypeId); Add(item.ZoneId);
                Add(item.ControllerSubject.Kind.ToString()); Add(item.ControllerSubject.PlayerId.ToString());
                Add(item.ControllerSubject.CompanyId);
            }
            foreach (var use in state.CharacterUses)
            foreach (var mode in use.Modes)
            {
                if (mode.SkillType != "persistent" || !mode.Activated || !mode.SubscriptionActive) continue;
                Add(use.CardInstanceId); Add(mode.AbilityId);
                Add(mode.Activated.ToString()); Add(mode.SubscriptionActive.ToString());
            }
            return key.ToString();
        }
    }
}
