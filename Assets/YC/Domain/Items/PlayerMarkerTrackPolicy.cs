using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;
using YC.Domain.Effects;

namespace YC.Domain.Items
{
    /// <summary>API-25：按实际载体、权限域和标记所有者校验，不按执行节点或执行玩家授权。</summary>
    public static class PlayerMarkerTrackPolicy
    {
        public static bool IsUsageArea(ZoneInstanceState zone, string carrierId, UsageTrackDeclaration declaration, int owner) =>
            zone != null && declaration != null && zone.TemplateId == "core.zone.board_marker_track" &&
            zone.ParentKind == ZoneParentKind.Item && zone.ParentId == carrierId &&
            (zone.LocalZoneId == declaration.CardZoneLocalId || declaration.InitializationPolicy == "public_declaration" &&
                zone.LocalZoneId == "declaration_" + owner + "_0");
        public static bool TryValidate(GameState state, ItemTypeRegistry types, out string reason)
        {
            reason = "";
            var zones = state.ItemZones.Zones.ToDictionary(z => z.ZoneId, StringComparer.Ordinal);
            var items = state.ItemZones.Items.ToDictionary(i => i.ItemId, StringComparer.Ordinal);
            var occupied = new HashSet<string>(StringComparer.Ordinal);
            foreach (var marker in items.Values.Where(i => types.IsAssignableTo(i.TypeId, "core.player_marker")))
            {
                var zone = zones[marker.ZoneId];
                if (zone.TemplateId != "core.zone.board_marker_track" || zone.ParentKind != ZoneParentKind.Item) continue;
                var carrier = items[zone.ParentId];
                string domain;
                if (zones[carrier.ZoneId].TemplateId == "core.zone.enterprise_display")
                    domain = StableIdFactory.Create("marker_domain", "enterprise", carrier.ItemId);
                else
                {
                    // 第一条宣告记录承载共用权限；后续记录保留各自的历史计分标记。
                    // usage 区与 declaration_<owner>_0 是同一权限域，不因换 slot/子区增加容量。
                    var declarations = state.ContentDefinitions?.UsageTracks?.Where(d => d.DefinitionId == carrier.DefinitionId &&
                        IsUsageArea(zone, carrier.ItemId, d, marker.OwnerSubject.PlayerId)).ToList();
                    if (declarations?.Count > 1) { reason = "usage_track_domain_ambiguous"; return false; }
                    var usage = declarations?.SingleOrDefault();
                    domain = usage == null ? zone.ZoneId : StableIdFactory.Create("marker_domain", "usage", carrier.ItemId, usage.TrackId);
                }
                if (marker.OwnerSubject.Kind != ItemSubjectKind.Player ||
                    !occupied.Add(StableIdFactory.Create("marker_occupancy", domain, marker.OwnerSubject.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                {
                    reason = "player_marker_track_duplicate";
                    return false;
                }
            }
            return true;
        }
    }
}
