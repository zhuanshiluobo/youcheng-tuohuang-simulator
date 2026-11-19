using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Scoring
{
    public sealed class RegionInformationRow
    {
        public string RegionId;
        public string Label;
        public int Points;
        public Dictionary<int, int> Influence;
        public Dictionary<int, string> InfluenceDisplay;
        public int? EmptySlots;
        public int TotalSlots;
    }

    // 预览只携带页面所需的公开数字，不携带来源实例、规则查询结果或房主内部状态。
    [Serializable] public sealed class RegionControlPreview
    {
        public string GameId = "", ContentHash = "", MapId = "";
        public int StateRevision;
        public List<RegionControlPreviewRow> Regions = new List<RegionControlPreviewRow>();
    }
    [Serializable] public sealed class RegionControlPreviewRow
    {
        public string RegionId = "";
        public int ScoreValue;
        public List<RegionInfluencePreview> Players = new List<RegionInfluencePreview>();
    }
    [Serializable] public sealed class RegionInfluencePreview
    {
        public int PlayerId, MarkerCount;
        public List<int> Contributions = new List<int>();
    }

    /// <summary>影响力复用计分口径（城市计 2）；空位按实际槽位逐个判断，不能用影响力相减。</summary>
    public static class RegionInformationQuery
    {
        public static List<RegionInformationRow> Build(GameState state, IMapQueryService map)
        {
            var rows = new List<RegionInformationRow>();
            if (state == null || map == null) return rows;
            var influence = new InfluenceQueryService(map);
            var occupied = new HashSet<string>();
            var complete = state.Map != null && state.Map.Influences != null;
            if (complete)
                foreach (var placement in state.Map.Influences)
                {
                    if (placement == null || !InfluenceSlotReference.TryParse(map, placement.SlotId,
                        out var slot, out _)) { complete = false; continue; }
                    occupied.Add(slot.SlotId);
                }
            foreach (var region in map.Map.Regions)
            {
                var empty = 0; var total = 0;
                foreach (var location in map.Map.Locations)
                    if (region.LocationIds.Contains(location.LocationId))
                        for (var i = 0; i < location.InfluenceSlotCount; i++)
                        { total++; if (!occupied.Contains(InfluenceSlotReference.ForLocation(location.LocationId, i).SlotId)) empty++; }
                foreach (var route in map.Map.Routes)
                    if (region.RouteIds.Contains(route.RouteId))
                        for (var i = 0; i < route.InfluenceSlotCount; i++)
                        { total++; if (!occupied.Contains(InfluenceSlotReference.ForRoute(route.RouteId, i).SlotId)) empty++; }
                rows.Add(new RegionInformationRow
                {
                    RegionId = region.RegionId, Label = region.DisplayName,
                    Points = region.ScoreValue,
                    Influence = state.Map == null ? new Dictionary<int, int>() : influence.CountAllPlayersInRegion(state, region.RegionId, true),
                    InfluenceDisplay = new Dictionary<int, string>(),
                    EmptySlots = complete ? (int?)empty : null, TotalSlots = total
                });
            }
            return rows;
        }

        public static RegionControlPreview CreatePreview(GameState state, IMapQueryService map, EffectRegistry registry)
        {
            if (state == null || map == null || registry == null) throw new ArgumentNullException();
            // 终局已冻结时展示该次正式结算快照；其他阶段按当前局面作只读预览。
            var control = state.FinalScoring?.Session?.Control ??
                new ScoringModifierQueryService(map, registry).BuildControlSnapshot(state, "region-information.preview");
            if (ItemZoneLegacyProjection.IsActive(state))
            {
                var publicItems = new HashSet<string>(new ItemZoneQuery().Project(state, GameStateViewer.Spectator)
                    .Items.Select(item => item.ItemId), StringComparer.Ordinal);
                if (control.Sources.Any(source => source?.Source == null || !publicItems.Contains(source.Source.ItemId)))
                    throw new InvalidOperationException("region_preview_private_source");
            }
            var preview = new RegionControlPreview { GameId = state.GameId, ContentHash = state.ContentPackHash,
                MapId = state.MapId, StateRevision = state.EffectRuntime.StateRevision };
            foreach (var region in control.Regions)
            {
                var row = new RegionControlPreviewRow { RegionId = region.RegionId, ScoreValue = region.ScoreValue };
                foreach (var player in state.Players.OrderBy(value => value.PlayerId))
                {
                    var facts = control.InfluenceFacts.Where(value => value.RegionId == region.RegionId &&
                        value.SubjectKind == "player" && value.PlayerId == player.PlayerId).ToArray();
                    // 旧无实体路径只有玩家影响力；实体路径则按真实类型区分帮派等额外方块。
                    var parts = new RegionInfluencePreview { PlayerId = player.PlayerId,
                        MarkerCount = facts.Count(value => value.TypeId == "core.player_marker" || value.TypeId == "legacy.influence") };
                    foreach (var extra in facts.Where(value => value.TypeId != "core.player_marker" && value.TypeId != "legacy.influence"))
                        parts.Contributions.Add(1);
                    int city = region.CityCounts.TryGetValue(player.PlayerId, out int cityCount) ? cityCount : 0;
                    if (city != 0) parts.Contributions.Add(city);
                    foreach (var modifier in region.InfluenceModifiers.Where(value => value.PlayerId == player.PlayerId))
                        if (modifier.Delta != 0) parts.Contributions.Add(modifier.Delta);
                    if (!region.InfluenceCounts.TryGetValue(player.PlayerId, out int total) ||
                        checked(parts.MarkerCount + parts.Contributions.Sum()) != total)
                        throw new InvalidOperationException("区控预览分项与正式总数不一致。");
                    row.Players.Add(parts);
                }
                preview.Regions.Add(row);
            }
            return preview;
        }

        public static List<RegionInformationRow> Build(GameState state, IMapQueryService map, RegionControlPreview preview)
        {
            if (state == null || map == null || preview == null || preview.GameId != state.GameId ||
                preview.ContentHash != state.ContentPackHash || preview.MapId != state.MapId ||
                preview.StateRevision != state.EffectRuntime?.StateRevision || preview.Regions == null)
                throw new ArgumentException("区控预览与当前对局不一致。", nameof(preview));
            var rows = Build(state, map);
            if (preview.Regions.Count != rows.Count) throw new ArgumentException("区控预览区域不完整。", nameof(preview));
            var byId = new Dictionary<string, RegionControlPreviewRow>(StringComparer.Ordinal);
            foreach (var item in preview.Regions)
                if (item == null || string.IsNullOrEmpty(item.RegionId) || item.Players == null || byId.ContainsKey(item.RegionId))
                    throw new ArgumentException("区控预览区域无效。", nameof(preview));
                else byId.Add(item.RegionId, item);
            var playerIds = new HashSet<int>(state.Players.Select(player => player.PlayerId));
            foreach (var row in rows)
            {
                if (!byId.TryGetValue(row.RegionId, out var result) || result.Players.Count != playerIds.Count ||
                    result.Players.Any(player => player == null || player.MarkerCount < 0 || player.Contributions == null || !playerIds.Contains(player.PlayerId)) ||
                    result.Players.Select(player => player.PlayerId).Distinct().Count() != playerIds.Count)
                    throw new ArgumentException("区控预览玩家不完整。", nameof(preview));
                row.Points = result.ScoreValue;
                row.Influence.Clear(); row.InfluenceDisplay.Clear();
                foreach (var player in result.Players)
                {
                    int additional = player.Contributions.Sum();
                    row.Influence[player.PlayerId] = checked(player.MarkerCount + additional);
                    string label = player.MarkerCount.ToString(CultureInfo.InvariantCulture);
                    if (additional != 0)
                        label += additional > 0 ? "+" + additional.ToString(CultureInfo.InvariantCulture) : additional.ToString(CultureInfo.InvariantCulture);
                    row.InfluenceDisplay[player.PlayerId] = label;
                }
            }
            return rows;
        }
    }
}
