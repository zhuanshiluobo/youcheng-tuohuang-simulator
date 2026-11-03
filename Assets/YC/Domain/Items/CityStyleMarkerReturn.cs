using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Domain.Items
{
    /// <summary>二级样式返还按实际标记/轨道判断；历史资格用于核对收据，不按Effect名称授予权限。</summary>
    public static class CityStyleMarkerReturn
    {
        internal static bool EligibleSource(GameState state, int player, string itemId, string zoneId)
        {
            var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == itemId);
            var zone = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == zoneId);
            if (item?.TypeId != "core.player_marker" || item.OwnerSubject.Kind != ItemSubjectKind.Player || item.OwnerSubject.PlayerId != player ||
                zone?.TemplateId != "core.zone.board_marker_track" || zone.ParentKind != ZoneParentKind.Item) return false;
            var carrier = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == zone.ParentId);
            if (carrier == null || state.ContentDefinitions?.CityStyles.SingleOrDefault(s => s.CityStyleId == carrier.DefinitionId)?.Level != 2) return false;
            if (state.EffectRuntime.UsageTrackBindings.Any(b => b.PlayerId == player && b.SourceItemId == carrier.ItemId &&
                PlayerMarkerTrackPolicy.IsUsageArea(zone, carrier.ItemId, UsageTracks.Declaration(state, b.TrackId), player))) return true;
            var declarations = state.FindPlayer(player)?.DeclaredCityStyles.Where(d => d.CityStyleItemId == carrier.ItemId).ToList();
            return declarations != null && declarations.Select((d, index) => new { d, index }).Any(x => x.d.InfluenceMarkerId == itemId && zone.LocalZoneId == "declaration_" + player + "_" + x.index);
        }
        public static IReadOnlyList<string> Candidates(GameState state, int player) => state.ItemZones.Items
            .Where(i => EligibleSource(state, player, i.ItemId, i.ZoneId)).Select(i => i.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        internal static IEnumerable<string> SourceZones(GameState state, int player) => state.ItemZones.Zones.Where(z => z.ParentKind == ZoneParentKind.Item && z.TemplateId == "core.zone.board_marker_track" &&
            (state.FindPlayer(player)?.DeclaredCityStyles.Any(d => d.CityStyleItemId == z.ParentId) == true || state.EffectRuntime.UsageTrackBindings.Any(b => b.PlayerId == player && b.SourceItemId == z.ParentId)))
            .Select(z => z.ZoneId).OrderBy(id => id, StringComparer.Ordinal);
    }
}
