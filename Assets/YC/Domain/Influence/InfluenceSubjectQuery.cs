using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Influence
{
    /// <summary>基于真实位置角色及原所有者的只读候选；公司永不映射成玩家。</summary>
    public sealed class InfluenceSubjectQuery
    {
        private readonly ItemZoneQuery items;
        public InfluenceSubjectQuery(ItemTypeRegistry types = null) { items = new ItemZoneQuery(types); }
        public static string GetSubjectKey(ItemSubjectRef subject) => subject?.Kind == ItemSubjectKind.Player
            ? "player:" + subject.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : subject?.Kind == ItemSubjectKind.Company ? "company:" + subject.CompanyId : string.Empty;
        public static bool Matches(string key, int playerId, string filter)
        {
            string own = "player:" + playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            switch (filter)
            {
                case "self": return key == own;
                case "player": return key.StartsWith("player:", StringComparison.Ordinal);
                case "opponent": return key.Length > 0 && key != own;
                case "any": return key.Length > 0;
                default: throw new InvalidOperationException("subject_filter_rejected");
            }
        }
        public IReadOnlyList<string> GetCandidateItemIds(GameState state, int executingPlayerId, string subjectFilter,
            IReadOnlyList<string> excludedSubjectKeys = null, IReadOnlyList<string> legalSlotIds = null)
        {
            if (state.FindPlayer(executingPlayerId) == null) throw new InvalidOperationException("invalid_player");
            var excluded = new HashSet<string>(excludedSubjectKeys ?? Array.Empty<string>(), StringComparer.Ordinal);
            return state.ItemZones.Items.Where(item => items.IsInfluence(state, item.ItemId) &&
                Matches(GetSubjectKey(item.OwnerSubject), executingPlayerId, subjectFilter) &&
                !excluded.Contains(GetSubjectKey(item.OwnerSubject)) && (legalSlotIds == null || legalSlotIds.Contains(item.SlotId)))
                .Select(item => item.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList().AsReadOnly();
        }
        public static ZoneInstanceState GetMapParent(GameState state, string zoneId)
        {
            var zone = state.ItemZones.Zones.Find(value => value.ZoneId == zoneId);
            var seen = new HashSet<string>();
            while (zone != null && seen.Add(zone.ZoneId))
            {
                if (zone.TemplateId == "core.zone.map_location" || zone.TemplateId == "core.zone.map_route") return zone;
                if (zone.ParentKind != ZoneParentKind.Zone) return null;
                zone = state.ItemZones.Zones.Find(value => value.ZoneId == zone.ParentId);
            }
            return null;
        }
        public static string GetRegionId(GameState state, string itemId)
        {
            var item = state.ItemZones.Items.Find(value => value.ItemId == itemId);
            var parent = item == null ? null : GetMapParent(state, item.ZoneId);
            return parent == null ? string.Empty : state.ItemZones.Zones.Find(value => value.ZoneId == parent.ParentId && value.TemplateId == "core.zone.map_region")?.LocalZoneId ?? string.Empty;
        }
        public IReadOnlyList<string> GetLegalResourcePointSlotIds(GameState state, IMapQueryService map,
            Func<string, bool> placementAllowed = null)
        {
            var result = new List<string>();
            foreach (var location in map.Map.Locations)
            {
                if (!state.Map.OpenLocationIds.Contains(location.LocationId)) continue;
                var parent = state.ItemZones.Zones.Find(zone => zone.TemplateId == "core.zone.map_location" && zone.LocalZoneId == location.LocationId);
                if (parent == null) continue;
                var children = state.ItemZones.Zones.Where(zone => zone.ParentId == parent.ZoneId).ToList();
                if (!children.Any(zone => zone.TemplateId == "core.zone.resource_point_markers" && state.ItemZones.Items.Any(item => item.ZoneId == zone.ZoneId)) ||
                    children.Any(zone => zone.TemplateId == "core.zone.city_dock" && state.ItemZones.Items.Any(item => item.ZoneId == zone.ZoneId)) ||
                    children.Any(zone => zone.TemplateId == "core.zone.influence_slots" && state.ItemZones.Items.Any(item => item.ZoneId == zone.ZoneId && items.IsInfluence(state, item.ItemId)))) continue;
                foreach (var zone in children.Where(zone => zone.TemplateId == "core.zone.influence_slots"))
                    result.AddRange(zone.LegalSlotIds.Where(slot => placementAllowed == null || placementAllowed(slot)));
            }
            return result.OrderBy(id => id, StringComparer.Ordinal).ToList().AsReadOnly();
        }
        public int CountCompanyInRegion(GameState state, string regionId) => state.ItemZones.Items.Count(item =>
            items.IsInfluence(state, item.ItemId) && item.OwnerSubject.Kind == ItemSubjectKind.Company && GetRegionId(state, item.ItemId) == regionId);
        public IReadOnlyList<string> GetCollectionLocationIds(GameState state, int playerId, string scope, bool hasCompanyContract)
        {
            if (scope != "normal" && scope != "extra") throw new InvalidOperationException("collection_scope_rejected");
            var locations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in state.ItemZones.Items)
            {
                var zone = state.ItemZones.Zones.Find(value => value.ZoneId == item.ZoneId);
                bool own = item.OwnerSubject.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId == playerId;
                if (!(items.IsInfluence(state, item.ItemId) && (own || scope == "normal" && hasCompanyContract && item.OwnerSubject.Kind == ItemSubjectKind.Company) ||
                    own && item.PhysicalKind == ItemPhysicalKind.MobileCity && zone?.TemplateId == "core.zone.city_dock")) continue;
                var parent = GetMapParent(state, item.ZoneId);
                if (parent?.TemplateId == "core.zone.map_location" && state.Map.OpenLocationIds.Contains(parent.LocalZoneId)) locations.Add(parent.LocalZoneId);
            }
            return locations.OrderBy(id => id, StringComparer.Ordinal).ToList().AsReadOnly();
        }
    }
}
