using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;
using YC.Domain.Rules;

namespace YC.Domain.Items
{
    public static class PlayerAreaPolicy
    {
        public const int NamingVersion = 2;
        public static IReadOnlyList<ItemInstanceState> Supply(GameState state, int player, string typeId)
        {
            var types = ItemTypeRegistry.CreateBuiltIn();
            if (types.Find(typeId) == null) throw new InvalidOperationException("supply_type_unknown");
            var zone = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.player_supply" && z.OwnerPlayerId == player);
            return state.ItemZones.Items.Where(i => i.ZoneId == zone.ZoneId && types.IsAssignableTo(i.TypeId, typeId)).OrderBy(i => i.ItemId, StringComparer.Ordinal).Select(GameStateCloneService.DeepClone).ToList();
        }
        public static void ConfigureSupplies(GameState state, IEnumerable<ItemRecipeEntry> entries)
        {
            var allowed = entries.Where(e => e.PerPlayer && e.InitialZoneRole == "supply").Select(e => e.TypeId)
                .Concat(state.SetupRecipe?.Entries.Where(e => (e.PerPlayer || e.Condition.Kind == "entrepreneur_selected") && e.InitialZoneRole == "supply").Select(e => e.TypeId) ?? Enumerable.Empty<string>())
                .Concat(new[] { "core.player_marker" }).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var types = ItemTypeRegistry.CreateBuiltIn();
            foreach (var zone in state.ItemZones.Zones.Where(z => z.TemplateId == "core.zone.player_supply"))
            {
                zone.Configuration = ItemZoneStateFactory.Configuration(zone, ItemPhysicalKind.Cube, "core.player_marker");
                zone.Configuration.AcceptedTypeIds = new List<string>(allowed);
                zone.Configuration.AcceptedItemKinds = allowed.Select(id => types.Find(id)?.PhysicalKind ?? throw new InvalidOperationException("supply_recipe_type_unknown")).Distinct().ToList();
            }
        }
        public static bool Validate(GameState state, out string reason)
        {
            reason = "player_area_invalid";
            if (state.ItemZones.Zones.Count == 0) { reason = ""; return true; }
            var areaNames = new[] { "core.zone.player_supply", "core.zone.player_rewards", "core.zone.entrepreneur_container", "core.zone.entrepreneur_pending", "core.zone.entrepreneur_active" };
            if (state.ItemZones.Zones.Any(z => areaNames.Contains(z.TemplateId) && state.FindPlayer(z.OwnerPlayerId) == null)) return false;
            // 只在本次校验内建索引；原先的首个owner检查及其拒绝顺序保持。
            var byTemplateAndOwner = new Dictionary<(string, int), List<ZoneInstanceState>>();
            var panels = new HashSet<(string, int)>();
            var containers = new HashSet<(string, int)>();
            foreach (var zone in state.ItemZones.Zones)
            {
                var key = (zone.TemplateId, zone.OwnerPlayerId);
                if (!byTemplateAndOwner.TryGetValue(key, out var values)) byTemplateAndOwner.Add(key, values = new List<ZoneInstanceState>());
                values.Add(zone);
                if (zone.TemplateId == "core.zone.player_panel") panels.Add((zone.ZoneId, zone.OwnerPlayerId));
                if (zone.TemplateId == "core.zone.entrepreneur_container") containers.Add((zone.ZoneId, zone.OwnerPlayerId));
            }
            foreach (var player in state.Players)
            {
                foreach (string template in new[] { "player_supply", "player_rewards", "entrepreneur_container", "entrepreneur_pending", "entrepreneur_active" })
                {
                    IReadOnlyList<ZoneInstanceState> zones = byTemplateAndOwner.TryGetValue(("core.zone." + template, player.PlayerId), out var found) ? found : (IReadOnlyList<ZoneInstanceState>)Array.Empty<ZoneInstanceState>();
                    if (zones.Count != 1) return false;
                    if ((template == "player_supply" || template == "player_rewards" || template == "entrepreneur_container") && !panels.Contains((zones[0].ParentId, player.PlayerId))) return false;
                    if (template == "player_supply" && zones[0].Configuration == null) return false;
                    if (template == "player_supply" && state.SetupRecipe != null)
                    {
                        var expected = state.SetupRecipe.Entries.Where(e => (e.PerPlayer || e.Condition.Kind == "entrepreneur_selected") && e.InitialZoneRole == "supply").Select(e => e.TypeId).Concat(new[] { "core.player_marker" }).Distinct().OrderBy(id => id, StringComparer.Ordinal);
                        if (!zones[0].Configuration.AcceptedTypeIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(expected)) { reason = "supply_recipe_permission_mismatch"; return false; }
                    }
                    if (template.StartsWith("entrepreneur_", StringComparison.Ordinal) && template != "entrepreneur_container" &&
                        !containers.Contains((zones[0].ParentId, player.PlayerId))) return false;
                }
            }
            reason = ""; return true;
        }
        /// <summary>只迁移显式传入的副本。区域实例ID保持不变，因此所有原供应、收据、父子和物品引用仍指同一实例。</summary>
        public static bool TryMigrateV1(GameState source, out GameState migrated, out string reason)
        {
            migrated = null; reason = "player_area_migration_evidence_required";
            if (source?.ItemZones?.NamingVersion != 1 || source.SetupRecipe == null || source.ContentDefinitions == null || source.EffectRuntime == null) return false;
            if (source.RuleConfiguration?.RuleModeId != "standard" || source.EffectRuntime.UsageTrackBindings.Count > 0 || source.ItemZones.Items.Any(i => i.TypeId == "entrepreneur.ability_card" || i.TypeId == "entrepreneur.round_card")) return false;
            if (source.EffectRuntime.AbilityActionInvocations.Count > 0 || source.Players.Any(p => p.DeclaredCityStyles.Count > 0) || source.EffectRuntime.EffectNodes.Any(n => n.Status != EffectNodeStatus.Completed && n.Status != EffectNodeStatus.Failed)) { reason = "usage_history_migration_requires_matching_executor"; return false; }
            var copy = GameStateCloneService.DeepClone(source);
            foreach (var player in copy.Players)
            {
                foreach (var pair in new[] { new[] { "player_marker_supply", "player_supply", "supply" }, new[] { "player_awards", "player_rewards", "rewards" } })
                {
                    var zones = copy.ItemZones.Zones.Where(z => z.OwnerPlayerId == player.PlayerId && (z.TemplateId == "core.zone." + pair[0] || z.TemplateId == "core.zone." + pair[1])).ToList();
                    if (zones.Count != 1 || zones[0].TemplateId != "core.zone." + pair[0]) return false;
                    zones[0].TemplateId = "core.zone." + pair[1]; zones[0].LocalZoneId = pair[2];
                    if (zones[0].Configuration != null) { zones[0].Configuration.ZoneTemplateId = zones[0].TemplateId; zones[0].Configuration.LocalZoneId = pair[2]; }
                }
                var panel = copy.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.player_panel" && z.OwnerPlayerId == player.PlayerId);
                var factory = new ItemZoneStateFactory(); string anchor = player.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var container = factory.AddZone(copy, "entrepreneur_container", "entrepreneur", panel, anchor, player.PlayerId);
                factory.AddZone(copy, "entrepreneur_pending", "entrepreneur_pending", container, anchor, player.PlayerId);
                factory.AddZone(copy, "entrepreneur_active", "entrepreneur_active", container, anchor, player.PlayerId);
            }
            foreach (var entry in copy.SetupRecipe.Entries) if (entry.InitialZoneRole == "player_markers") entry.InitialZoneRole = "supply";
            foreach (var declaration in copy.ContentDefinitions.AbilitySources)
                for (int i = 0; i < declaration.EffectiveZones.Count; i++) if (declaration.EffectiveZones[i] == "core.zone.player_awards") declaration.EffectiveZones[i] = "core.zone.player_rewards";
            ConfigureSupplies(copy, Array.Empty<ItemRecipeEntry>()); copy.ItemZones.NamingVersion = NamingVersion;
            ItemZoneLegacyProjection.Bind(copy, true);
            if (!new ItemZoneQuery().TryValidate(copy, out reason)) return false;
            migrated = copy; return true;
        }
    }
}
