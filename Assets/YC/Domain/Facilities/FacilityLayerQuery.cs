using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    public static class FacilityLayerQuery
    {
        public const string Version = "1.0.0", CoveredTemplate = "core.zone.city_facility_covered";
        public const string CellTemplate = "core.zone.city_facility_cell", SupplyTemplate = "core.zone.facility_supply_slot";
        private static readonly ItemTypeRegistry Types = FrozenTypes();
        private static ItemTypeRegistry FrozenTypes() { var t = ItemTypeRegistry.CreateBuiltIn(); t.Freeze(); return t; }
        public static bool IsFacility(ItemInstanceState item) => item != null && item.PhysicalKind == ItemPhysicalKind.Card && Types.IsAssignableTo(item.TypeId, "core.facility_card");

        public static bool TryGetCityCell(GameState state, string zoneId, out ZoneInstanceState cell)
        {
            cell = null;
            var zone = state?.ItemZones?.Zones?.SingleOrDefault(z => z.ZoneId == zoneId);
            if (zone?.TemplateId == CoveredTemplate)
            {
                if (zone.ParentKind != ZoneParentKind.Zone) return false;
                var parent = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == zone.ParentId);
                if (parent?.TemplateId != CellTemplate || zone.OwnerPlayerId != parent.OwnerPlayerId) return false;
                zone = parent;
            }
            if (zone?.TemplateId != CellTemplate || zone.ParentKind != ZoneParentKind.Zone) return false;
            var city = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == zone.ParentId);
            if (city?.TemplateId != "core.zone.city_panel" || city.OwnerPlayerId != zone.OwnerPlayerId ||
                city.ParentKind != ZoneParentKind.Zone || state.FindPlayer(zone.OwnerPlayerId) == null) return false;
            var panel = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == city.ParentId);
            if (panel?.TemplateId != "core.zone.player_panel" || panel.OwnerPlayerId != zone.OwnerPlayerId) return false;
            cell = zone; return true;
        }
        public static bool IsInCity(GameState state, string itemId, int playerId) =>
            state?.ItemZones?.Items?.SingleOrDefault(i => i.ItemId == itemId) is ItemInstanceState item &&
            IsFacility(item) && item.ControllerSubject.Kind == ItemSubjectKind.Player && item.ControllerSubject.PlayerId == playerId &&
            TryGetCityCell(state, item.ZoneId, out var cell) && cell.OwnerPlayerId == playerId;
        public static int CitySlot(GameState state, string itemId)
        {
            var item = state?.ItemZones?.Items?.SingleOrDefault(i => i.ItemId == itemId);
            return item != null && TryGetCityCell(state, item.ZoneId, out var cell) ? Slot(cell) : -1;
        }
        public static bool IsDirectTop(GameState state, string itemId, int playerId)
        {
            var item = state?.ItemZones?.Items?.SingleOrDefault(i => i.ItemId == itemId);
            return item != null && IsInCity(state, itemId, playerId) &&
                state.ItemZones.Zones.Single(z => z.ZoneId == item.ZoneId).TemplateId == CellTemplate;
        }
        public static int Slot(ZoneInstanceState cell)
        {
            if (cell?.TemplateId != CellTemplate || !cell.LocalZoneId.StartsWith("city_cell_", StringComparison.Ordinal) ||
                !int.TryParse(cell.LocalZoneId.Substring(10), NumberStyles.None, CultureInfo.InvariantCulture, out int slot) ||
                slot < 0 || slot >= BuildFacilityService.CityBoardSlotCount) throw new InvalidOperationException("facility_cell_identity_invalid");
            return slot;
        }
        public static FacilityCellLayerSnapshot ResolveCell(GameState state, int playerId, int slotIndex)
        {
            var cell = state.ItemZones.Zones.SingleOrDefault(z => z.TemplateId == CellTemplate && z.OwnerPlayerId == playerId && z.LocalZoneId == "city_cell_" + slotIndex);
            if (cell == null || !TryGetCityCell(state, cell.ZoneId, out _)) throw new InvalidOperationException("facility_cell_identity_invalid");
            var top = state.ItemZones.Items.Where(i => i.ZoneId == cell.ZoneId).ToList();
            if (top.Count > 1 || top.Any(i => !IsFacility(i))) throw new InvalidOperationException("facility_cell_members_invalid");
            var lower = state.ItemZones.Zones.Where(z => z.TemplateId == CoveredTemplate && z.ParentKind == ZoneParentKind.Zone && z.ParentId == cell.ZoneId).ToList();
            if (lower.Count > 1) throw new InvalidOperationException("facility_covered_zone_duplicate");
            var result = new FacilityCellLayerSnapshot { PlayerId = playerId, SlotIndex = slotIndex, CellZoneId = cell.ZoneId, TopItemId = top.FirstOrDefault()?.ItemId ?? "" };
            if (lower.Count == 1)
            {
                if (lower[0].OwnerPlayerId != playerId || lower[0].VisibilityPolicyId != cell.VisibilityPolicyId)
                    throw new InvalidOperationException("facility_covered_owner_invalid");
                result.CoveredZoneId = lower[0].ZoneId;
                result.CoveredItemIds = Ordered(state, lower[0].ZoneId);
                result.OrderRevision = state.ItemZones.Orders.Single(o => o.ZoneId == lower[0].ZoneId).OrderRevision;
            }
            if (result.TopItemId.Length == 0 && result.CoveredItemIds.Count != 0) throw new InvalidOperationException("facility_covered_without_top");
            if (result.TopItemId.Length > 0) result.AllItemIds.Add(result.TopItemId);
            result.AllItemIds.AddRange(result.CoveredItemIds);
            if (result.AllItemIds.Any(id => !IsInCity(state, id, playerId))) throw new InvalidOperationException("facility_city_controller_invalid");
            result.OrderDigest = Digest(state, cell.ZoneId) + ":" + (result.CoveredZoneId.Length == 0 ? "legacy" : Digest(state, result.CoveredZoneId));
            return result;
        }
        public static IReadOnlyList<FacilityCellLayerSnapshot> ResolveCity(GameState state, int playerId) =>
            state.ItemZones.Zones.Where(z => z.TemplateId == CellTemplate && z.OwnerPlayerId == playerId)
                .Select(Slot).OrderBy(i => i).Select(i => ResolveCell(state, playerId, i)).ToList().AsReadOnly();
        public static IReadOnlyList<string> AllCityItems(GameState state, int playerId) => ResolveCity(state, playerId).SelectMany(c => c.AllItemIds).ToList().AsReadOnly();
        public static IReadOnlyList<string> TopCityItems(GameState state, int playerId) => ResolveCity(state, playerId).Where(c => c.TopItemId.Length > 0).Select(c => c.TopItemId).ToList().AsReadOnly();

        public static List<string> Ordered(GameState state, string zoneId)
        {
            var order = state.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == zoneId);
            var members = state.ItemZones.Items.Where(i => i.ZoneId == zoneId).Select(i => i.ItemId).ToList();
            if (order == null || order.ItemIds == null || order.OrderRevision < 0 || order.ItemIds.Distinct(StringComparer.Ordinal).Count() != order.ItemIds.Count ||
                !new HashSet<string>(members, StringComparer.Ordinal).SetEquals(order.ItemIds)) throw new InvalidOperationException("facility_order_invalid");
            return new List<string>(order.ItemIds);
        }
        public static string Digest(GameState state, string zoneId)
        {
            var zone = state.ItemZones.Zones.Single(z => z.ZoneId == zoneId);
            var order = state.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == zoneId);
            var members = order == null ? state.ItemZones.Items.Where(i => i.ZoneId == zoneId).Select(i => i.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList() : Ordered(state, zoneId);
            var text = new StringBuilder().Append(zoneId.Length).Append(':').Append(zoneId).Append(':').Append(order?.OrderRevision ?? 0);
            foreach (var id in members) text.Append(':').Append(id.Length).Append(':').Append(id);
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }
        public static FacilitySupplyLayerSnapshot ResolveSupply(GameState state, string slotZoneId)
        {
            var zone = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == slotZoneId);
            if (zone?.TemplateId != SupplyTemplate) throw new InvalidOperationException("facility_supply_slot_invalid");
            var ids = Ordered(state, slotZoneId);
            if (ids.Any(id => !IsFacility(state.ItemZones.Items.Single(i => i.ItemId == id)))) throw new InvalidOperationException("facility_supply_members_invalid");
            return new FacilitySupplyLayerSnapshot { SlotZoneId = slotZoneId,
                SlotIndex = int.Parse(zone.LocalZoneId.Substring("facility_supply_".Length), CultureInfo.InvariantCulture),
                ItemIds = ids, TopItemId = ids.FirstOrDefault() ?? "", OrderDigest = Digest(state, slotZoneId),
                OrderRevision = state.ItemZones.Orders.Single(o => o.ZoneId == slotZoneId).OrderRevision };
        }
        public static IReadOnlyList<FacilitySupplyLayerSnapshot> Supply(GameState state) => state.ItemZones.Zones
            .Where(z => z.TemplateId == SupplyTemplate).Select(z => ResolveSupply(state, z.ZoneId)).OrderBy(s => s.SlotIndex).ToList().AsReadOnly();
        public static bool IsSupplyTop(GameState state, string itemId)
        {
            var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == itemId);
            var zone = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == item?.ZoneId);
            return zone?.TemplateId == SupplyTemplate && ResolveSupply(state, zone.ZoneId).TopItemId == itemId;
        }
        public static IReadOnlyList<FacilityTowerAdjacency> ResolveTowerAdjacency(GameState state, int playerId)
        {
            var cells = ResolveCity(state, playerId); var result = new List<FacilityTowerAdjacency>();
            int width = BuildFacilityService.CityBoardSlotCountPerRow;
            foreach (var cell in cells)
            foreach (var id in cell.AllItemIds)
            {
                var item = state.ItemZones.Items.Single(i => i.ItemId == id);
                if (FacilityCardDatabase.Get(state, item.DefinitionId)?.EffectId != "setup_core_command_tower") continue;
                result.Add(new FacilityTowerAdjacency { TowerItemId = id, SlotIndex = cell.SlotIndex,
                    AdjacentTopItemIds = cells.Where(other => other.TopItemId.Length > 0 &&
                        Math.Abs(other.SlotIndex / width - cell.SlotIndex / width) + Math.Abs(other.SlotIndex % width - cell.SlotIndex % width) == 1)
                        .OrderBy(other => other.SlotIndex).Select(other => other.TopItemId).ToList() });
            }
            return result.AsReadOnly();
        }
        public static HashSet<string> DeclaredItemIds(GameState state, int playerId) => new HashSet<string>(
            state.FindPlayer(playerId).DeclaredCityStyles.SelectMany(d => d.UsedFacilityIds), StringComparer.Ordinal);
        public static IReadOnlyList<int> CurrentDeclaredTopSlots(GameState state, int playerId)
        {
            var used = DeclaredItemIds(state, playerId);
            return ResolveCity(state, playerId).Where(c => used.Contains(c.TopItemId)).Select(c => c.SlotIndex).ToList().AsReadOnly();
        }
    }
}

