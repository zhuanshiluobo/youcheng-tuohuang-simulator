using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;

namespace YC.Domain.Items
{
    [Serializable]
    public sealed class ZoneTemplateDefinition
    {
        public string TemplateId = string.Empty;
        public List<string> AllowedParentTemplateIds = new List<string>();
        public List<string> AllowedCarrierTypeIds = new List<string>();
        public List<string> AllowedChildTemplateIds = new List<string>();
        public List<string> AcceptedTypeIds = new List<string>();
        public List<ItemPhysicalKind> AcceptedItemKinds = new List<ItemPhysicalKind>();
        public ZoneOrderingKind OrderingKind;
        public int Capacity = -1;
        public bool AllowExternalCardBinding;
    }

    [Serializable]
    public sealed class CardZoneDefinition
    {
        public string LocalZoneId = string.Empty;
        public string ZoneTemplateId = string.Empty;
        public List<ItemPhysicalKind> AcceptedItemKinds = new List<ItemPhysicalKind>();
        public List<string> AcceptedTypeIds = new List<string>();
        public string EntryPolicyId = string.Empty;
        public string ExitPolicyId = string.Empty;
        public string CapacityPolicy = string.Empty;
        public int? Capacity;
        public ZoneOrderingKind OrderingKind;
        public string VisibilityPolicyId = string.Empty;
        public string CarrierExitPolicyId = string.Empty;
        public string DisplayAnchorId = string.Empty;
        public List<string> Slots = new List<string>();
        public string EmptyExitPolicyId = string.Empty, PaymentPolicyId = string.Empty;
        public int MaxMarkersPerPayment;
    }

    [Serializable]
    public sealed class ItemCardZoneDeclaration
    {
        public string DefinitionId = string.Empty;
        public List<CardZoneDefinition> CardZones = new List<CardZoneDefinition>();
    }

    public static class ItemZoneVisibility
    {
        public const string Public = "core.public_items";
        public const string Owner = "core.owner_items";
        public const string Hidden = "core.hidden_items";
        public const string SecretRetired = "core.secret_retired_count";
        public static bool IsKnown(string id) => id == Public || id == Owner || id == Hidden || id == SecretRetired;
    }

    // 仅查询所需的不可变规则；集合包装私有注册副本，不能取得可写List。
    internal sealed class ZoneTemplateRules
    {
        internal readonly string TemplateId;
        internal readonly System.Collections.ObjectModel.ReadOnlyCollection<string> AllowedParentTemplateIds, AllowedCarrierTypeIds, AllowedChildTemplateIds, AcceptedTypeIds;
        internal readonly System.Collections.ObjectModel.ReadOnlyCollection<ItemPhysicalKind> AcceptedItemKinds;
        internal readonly ZoneOrderingKind OrderingKind;
        internal readonly int Capacity;
        internal readonly bool AllowExternalCardBinding;
        internal ZoneTemplateRules(ZoneTemplateDefinition value)
        {
            TemplateId = value.TemplateId;
            AllowedParentTemplateIds = value.AllowedParentTemplateIds.AsReadOnly();
            AllowedCarrierTypeIds = value.AllowedCarrierTypeIds.AsReadOnly();
            AllowedChildTemplateIds = value.AllowedChildTemplateIds.AsReadOnly();
            AcceptedTypeIds = value.AcceptedTypeIds.AsReadOnly();
            AcceptedItemKinds = value.AcceptedItemKinds.AsReadOnly();
            OrderingKind = value.OrderingKind; Capacity = value.Capacity; AllowExternalCardBinding = value.AllowExternalCardBinding;
        }
    }

    public sealed class ZoneTemplateRegistry
    {
        private readonly ItemTypeRegistry types;
        private readonly Dictionary<string, ZoneTemplateDefinition> definitions = new Dictionary<string, ZoneTemplateDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, ZoneTemplateRules> rules = new Dictionary<string, ZoneTemplateRules>(StringComparer.Ordinal);
        private bool frozen;
        public IReadOnlyList<ZoneTemplateDefinition> Definitions => System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(definitions.Values, value => GameStateCloneService.DeepClone(value))).AsReadOnly();
        public ZoneTemplateRegistry(ItemTypeRegistry types) { this.types = types ?? throw new ArgumentNullException(nameof(types)); }

        internal void RegisterBuiltIn(ZoneTemplateDefinition definition)
        {
            if (frozen) throw new InvalidOperationException("registry_frozen");
            if (definition == null || string.IsNullOrWhiteSpace(definition.TemplateId) || definitions.ContainsKey(definition.TemplateId))
                throw new InvalidOperationException("duplicate_template_id");
            if (definition.Capacity < -1 || !Enum.IsDefined(typeof(ZoneOrderingKind), definition.OrderingKind) ||
                definition.AllowedParentTemplateIds == null || definition.AllowedCarrierTypeIds == null || definition.AllowedChildTemplateIds == null ||
                definition.AcceptedItemKinds == null || definition.AcceptedTypeIds == null) throw new InvalidOperationException("zone_template_invalid");
            foreach (string typeId in definition.AcceptedTypeIds)
                if (types.Find(typeId) == null || !definition.AcceptedItemKinds.Contains(types.Find(typeId).PhysicalKind)) throw new InvalidOperationException("acceptedTypeIds_invalid");
            foreach (ItemPhysicalKind kind in definition.AcceptedItemKinds)
                if (!Enum.IsDefined(typeof(ItemPhysicalKind), kind)) throw new InvalidOperationException("acceptedItemKinds_invalid");
            var stored = GameStateCloneService.DeepClone(definition);
            definitions.Add(definition.TemplateId, stored);
            rules.Add(definition.TemplateId, new ZoneTemplateRules(stored));
        }

        public bool ValidateCardZoneDeclaration(string carrierTypeId, CardZoneDefinition declaration, out string reasonCode)
        {
            reasonCode = string.Empty;
            if (declaration == null) return Reject("cardZones_required", out reasonCode);
            var template = Find(declaration.ZoneTemplateId);
            if (template == null || !template.AllowExternalCardBinding) return Reject("zoneTemplateId_rejected", out reasonCode);
            if (!MatchesType(carrierTypeId, template.AllowedCarrierTypeIds)) return Reject("carrierTypeId_rejected", out reasonCode);
            if (string.IsNullOrWhiteSpace(declaration.LocalZoneId)) return Reject("localZoneId_required", out reasonCode);
            if (declaration.AcceptedItemKinds == null || declaration.AcceptedItemKinds.Count == 0) return Reject("acceptedItemKinds_required", out reasonCode);
            foreach (var kind in declaration.AcceptedItemKinds)
                if (!Enum.IsDefined(typeof(ItemPhysicalKind), kind) || !template.AcceptedItemKinds.Contains(kind)) return Reject("acceptedItemKinds_rejected", out reasonCode);
            if (declaration.AcceptedTypeIds == null) return Reject("acceptedTypeIds_required", out reasonCode);
            // 空过滤器不能扩大原模板的子类型范围。
            if (template.AcceptedTypeIds.Count > 0 && declaration.AcceptedTypeIds.Count == 0) return Reject("acceptedTypeIds_rejected", out reasonCode);
            foreach (string typeId in declaration.AcceptedTypeIds)
            {
                var type = types.Find(typeId);
                if (type == null || !declaration.AcceptedItemKinds.Contains(type.PhysicalKind) ||
                    (template.AcceptedTypeIds.Count > 0 && !MatchesType(typeId, template.AcceptedTypeIds))) return Reject("acceptedTypeIds_rejected", out reasonCode);
            }
            if (declaration.CapacityPolicy != "bounded" && declaration.CapacityPolicy != "unbounded") return Reject("capacityPolicy_rejected", out reasonCode);
            if ((declaration.CapacityPolicy == "bounded" && (!declaration.Capacity.HasValue || declaration.Capacity.Value < 0)) ||
                (declaration.CapacityPolicy == "unbounded" && declaration.Capacity.HasValue)) return Reject("capacity_rejected", out reasonCode);
            if (template.Capacity >= 0 && (declaration.CapacityPolicy == "unbounded" || declaration.Capacity > template.Capacity)) return Reject("capacity_rejected", out reasonCode);
            if (declaration.OrderingKind != template.OrderingKind) return Reject("orderingKind_rejected", out reasonCode);
            if (!ItemZoneVisibility.IsKnown(declaration.VisibilityPolicyId) || declaration.VisibilityPolicyId == ItemZoneVisibility.SecretRetired) return Reject("visibilityPolicyId_rejected", out reasonCode);
            if (!IsPolicy(declaration.EntryPolicyId, "core.explicit_transfer", "core.owned_player_marker", "core.removed_owned_influence")) return Reject("entryPolicyId_rejected", out reasonCode);
            if (!IsPolicy(declaration.ExitPolicyId, "core.explicit_transfer", "core.card_payment")) return Reject("exitPolicyId_rejected", out reasonCode);
            if (!IsPolicy(declaration.CarrierExitPolicyId, "core.preserve_contents", "core.return_contents_before_exit")) return Reject("carrierExitPolicyId_rejected", out reasonCode);
            if (declaration.Slots == null || declaration.Slots.Any(string.IsNullOrWhiteSpace) || declaration.Slots.Count != declaration.Slots.Distinct().Count() || declaration.Slots.Count > 0 && template.OrderingKind != ZoneOrderingKind.Track) return Reject("slots_invalid", out reasonCode);
            if (!ValidateCarrierPolicies(declaration, out reasonCode)) return false;
            return true;
        }
        public static bool ValidateCarrierPolicies(CardZoneDefinition declaration, out string reason)
        {
            reason = "carrier_payment_policy_invalid";
            if (declaration.EmptyExitPolicyId != "" && declaration.EmptyExitPolicyId != "core.discard_carrier_when_empty") return false;
            if (declaration.PaymentPolicyId == "") { if (declaration.MaxMarkersPerPayment != 0) return false; }
            else if (declaration.PaymentPolicyId != "core.basic_resource_substitution" || declaration.MaxMarkersPerPayment != 2 ||
                declaration.ZoneTemplateId != "core.zone.card_cube_storage" || declaration.ExitPolicyId != "core.card_payment" ||
                declaration.AcceptedTypeIds.Count != 1 || declaration.AcceptedTypeIds[0] != "core.player_marker") return false;
            reason = ""; return true;
        }

        public bool ValidateExternalFields(IEnumerable<string> fieldNames, out string reasonCode)
        {
            var allowed = new HashSet<string>(new[] { "localZoneId", "zoneTemplateId", "acceptedItemKinds", "acceptedTypeIds", "entryPolicyId", "exitPolicyId", "capacityPolicy", "capacity", "orderingKind", "visibilityPolicyId", "carrierExitPolicyId", "displayAnchorId", "emptyExitPolicyId", "paymentPolicyId", "maxMarkersPerPayment", "slots" }, StringComparer.Ordinal);
            foreach (string name in fieldNames)
                if (!allowed.Contains(name)) return Reject(name + "_unsupported", out reasonCode);
            reasonCode = string.Empty;
            return true;
        }

        internal ZoneTemplateRules FindRules(string id) => id != null && rules.TryGetValue(id, out var value) ? value : null;
        public ZoneTemplateDefinition Find(string templateId) => templateId != null && definitions.TryGetValue(templateId, out var value) ? GameStateCloneService.DeepClone(value) : null;
        public void Freeze()
        {
            foreach (var value in definitions.Values)
            {
                foreach (string parent in value.AllowedParentTemplateIds)
                    if (!definitions.ContainsKey(parent) || !definitions[parent].AllowedChildTemplateIds.Contains(value.TemplateId)) throw new InvalidOperationException("allowedParentTemplateIds_invalid");
                foreach (string child in value.AllowedChildTemplateIds)
                    if (!definitions.ContainsKey(child) || !definitions[child].AllowedParentTemplateIds.Contains(value.TemplateId)) throw new InvalidOperationException("allowedChildTemplateIds_invalid");
                foreach (string carrier in value.AllowedCarrierTypeIds)
                    if (types.Find(carrier) == null) throw new InvalidOperationException("allowedCarrierTypeIds_invalid");
            }
            frozen = true;
        }
        private bool MatchesType(string typeId, List<string> allowed) => allowed.Exists(parent => types.IsAssignableTo(typeId, parent));
        private static bool IsPolicy(string value, params string[] allowed) => Array.IndexOf(allowed, value) >= 0;
        private static bool Reject(string code, out string reasonCode) { reasonCode = code; return false; }

        public static ZoneTemplateRegistry CreateBuiltIn(ItemTypeRegistry types)
        {
            var registry = new ZoneTemplateRegistry(types);
            foreach (string root in new[] { "map", "table", "players", "game_box" }) registry.Add(root, "", null);
            registry.Add("map_region", "map", null);
            registry.Add("map_location", "map_region", null);
            registry.Add("map_route", "map_region", null);
            registry.Add("score_track", "map", new[] { ItemPhysicalKind.Cube, ItemPhysicalKind.Token }, ZoneOrderingKind.Track, -1, new[] { "core.player_marker" });
            registry.Add("influence_slots", "map_location,map_route", new[] { ItemPhysicalKind.Cube }, ZoneOrderingKind.FixedSlots);
            registry.Add("city_dock", "map_location,city_panel", new[] { ItemPhysicalKind.MobileCity }, ZoneOrderingKind.Unordered, 1);
            registry.Add("map_tokens", "map,map_location,map_route", new[] { ItemPhysicalKind.Token });
            registry.Add("resource_point_markers", "map_location", new[] { ItemPhysicalKind.Token }, ZoneOrderingKind.Unordered, -1, new[] { "core.resource_point_token" });
            registry.Add("route_road", "map_route", new[] { ItemPhysicalKind.Token }, ZoneOrderingKind.Unordered, 1, new[] { "core.road_token" });
            registry.Add("covered_cubes", "map_route", new[] { ItemPhysicalKind.Cube });
            registry.Add("public_supply", "table", null);
            foreach (string display in new[] { "enterprise_display", "department_display", "city_style_display" }) registry.Add(display, "table", new[] { ItemPhysicalKind.Card }, ZoneOrderingKind.Unordered, -1, new[] { "core.board_card" });
            registry.Add("facility_supply", "public_supply", null);
registry.Add("facility_supply_slot", "facility_supply", new[] { ItemPhysicalKind.Card }, ZoneOrderingKind.Deck, -1, new[] { "core.facility_card" });
            registry.Add("reward_pool", "public_supply", new[] { ItemPhysicalKind.Card, ItemPhysicalKind.Cube, ItemPhysicalKind.Token }, ZoneOrderingKind.Unordered, -1, new[] { "core.award_card", ItemTypeRegistry.ContractTokenTypeId, "core.generated_character_card", "infected.company_marker" });
            registry.Add("card_deck", "public_supply", new[] { ItemPhysicalKind.Card }, ZoneOrderingKind.Deck);
            registry.Add("player_panel", "players", null);
            foreach (string area in new[] { "hand", "discard", "covered", "persistent" }) registry.Add("character_" + area, "player_panel", new[] { ItemPhysicalKind.Card }, ZoneOrderingKind.Unordered, -1, new[] { "core.character_card" });
            registry.Add("player_supply", "player_panel", new[] { ItemPhysicalKind.Cube, ItemPhysicalKind.Card, ItemPhysicalKind.Token }, ZoneOrderingKind.Unordered, -1, new[] { "core.player_marker", "entrepreneur.gang_marker", "core.facility_card", "core.die" });
            registry.Add("entrepreneur_container", "player_panel", null);
            foreach (string area in new[] { "entrepreneur_pending", "entrepreneur_active" }) registry.Add(area, "entrepreneur_container", new[] { ItemPhysicalKind.Card }, ZoneOrderingKind.Unordered, -1, new[] { "entrepreneur.ability_card", "entrepreneur.round_card" });
            registry.Add("city_panel", "player_panel", null);
registry.Add("city_facility_cell", "city_panel", new[] { ItemPhysicalKind.Card }, ZoneOrderingKind.Unordered, 1, new[] { "core.facility_card" });
            registry.Add("city_facility_covered", "city_facility_cell", new[] { ItemPhysicalKind.Card }, ZoneOrderingKind.Deck, -1, new[] { "core.facility_card" });
            registry.Add("player_rewards", "player_panel", new[] { ItemPhysicalKind.Card, ItemPhysicalKind.Token }, ZoneOrderingKind.Unordered, -1, new[] { "core.award_card", ItemTypeRegistry.ContractTokenTypeId });
            foreach (string box in new[] { "box_reserve", "box_retired", "box_secret_retired" }) registry.Add(box, "game_box", new[] { ItemPhysicalKind.Cube, ItemPhysicalKind.Card, ItemPhysicalKind.MobileCity, ItemPhysicalKind.Token });
            registry.Add("effect_staging", "table", new[] { ItemPhysicalKind.Cube, ItemPhysicalKind.Card, ItemPhysicalKind.MobileCity, ItemPhysicalKind.Token });
            registry.Add("card_cube_storage", "", new[] { ItemPhysicalKind.Cube }, ZoneOrderingKind.Unordered, -1, null, new[] { "core.card" }, true);
            registry.Add("card_facility_storage", "", new[] { ItemPhysicalKind.Card }, ZoneOrderingKind.Unordered, -1, new[] { "core.facility_card" }, new[] { "core.card" }, true);
            registry.Add("board_marker_track", "", new[] { ItemPhysicalKind.Cube }, ZoneOrderingKind.Track, -1, new[] { "core.player_marker" }, new[] { "core.card" }, true);
            // 根允许同时包含物品与子区；公开指示物有明确物理资格。
            registry.definitions["core.zone.table"].AcceptedItemKinds.Add(ItemPhysicalKind.Token);
            registry.definitions["core.zone.table"].AcceptedTypeIds.Add("core.start_player_token");
            foreach (var value in registry.definitions.Values)
                foreach (string parent in value.AllowedParentTemplateIds) registry.definitions[parent].AllowedChildTemplateIds.Add(value.TemplateId);
            registry.Freeze();
            return registry;
        }

        private void Add(string name, string parents, ItemPhysicalKind[] kinds, ZoneOrderingKind ordering = ZoneOrderingKind.Unordered, int capacity = -1, string[] acceptedTypes = null, string[] carriers = null, bool external = false)
        {
            var value = new ZoneTemplateDefinition { TemplateId = "core.zone." + name, OrderingKind = ordering, Capacity = capacity, AllowExternalCardBinding = external };
            if (!string.IsNullOrEmpty(parents)) foreach (string parent in parents.Split(',')) value.AllowedParentTemplateIds.Add("core.zone." + parent);
            if (kinds != null) value.AcceptedItemKinds.AddRange(kinds);
            if (acceptedTypes != null) value.AcceptedTypeIds.AddRange(acceptedTypes);
            if (carriers != null) value.AllowedCarrierTypeIds.AddRange(carriers);
            RegisterBuiltIn(value);
        }
    }
}
