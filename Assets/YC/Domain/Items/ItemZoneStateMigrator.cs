using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Domain.Rules;

namespace YC.Domain.Items
{
    [Serializable]
    public sealed class ItemRecipeEntry
    {
        public string EntryId = string.Empty;
        public string DefinitionId = string.Empty;
        public string TypeId = string.Empty;
        public int Quantity;
        public bool PerPlayer;
        public string InitialZoneRole = string.Empty;
    }

    [Serializable]
    public sealed class LegacyDeckEvidence
    {
        public string LocalZoneId = string.Empty;
        public List<string> BottomToTopDefinitionIds = new List<string>();
    }

    /// <summary>来自原局的确切配方/顺序证据；不是拿当前目录补猜旧档。</summary>
    [Serializable]
    public sealed class ItemZoneMigrationEvidence
    {
        public string GameId = string.Empty;
        public string MapId = string.Empty;
        public string ContentPackHash = string.Empty;
        public string RuleContractVersion = string.Empty;
        public List<ItemRecipeEntry> Recipe = new List<ItemRecipeEntry>();
        public List<LegacyDeckEvidence> Decks = new List<LegacyDeckEvidence>();
        public List<int> PlayerIds = new List<int>();
        public GameRuleConfiguration RuleConfiguration;
    }

    /// <summary>Host 的合法实例装配入口。只在准备时执行；读取/恢复不会调用本工厂。</summary>
    public sealed class ItemZoneStateFactory
    {
        private readonly ItemTypeRegistry types;
        private readonly ZoneTemplateRegistry templates;
        public ItemZoneStateFactory(ItemTypeRegistry types = null, ZoneTemplateRegistry templates = null)
        {
            this.types = types ?? ItemTypeRegistry.CreateBuiltIn();
            this.templates = templates ?? ZoneTemplateRegistry.CreateBuiltIn(this.types);
        }

        public GameState CreateForSetup(GameState source, GameMapDefinition map, IReadOnlyList<ItemRecipeEntry> recipe, IReadOnlyList<ItemCardZoneDeclaration> cardZones = null)
        {
            if (source == null || map == null || string.IsNullOrWhiteSpace(source.GameId) || source.MapId != map.MapId || recipe == null)
                throw new InvalidOperationException("setup_recipe_context_invalid");
            if (source.RuleConfiguration == null || source.RuleConfiguration.SchemaVersion != GameRuleConfiguration.CurrentSchemaVersion || source.RuleConfiguration.MapId != source.MapId ||
                source.RuleConfiguration.PlayerCount != source.Players.Count || source.RuleConfiguration.ContentPackHash != source.ContentPackHash ||
                source.RuleConfiguration.RuleContractVersion != GameRuleConfiguration.CurrentRuleContractVersion)
                throw new InvalidOperationException("RuleConfiguration_setup_mismatch");
            if (source.RuleConfiguration.RuleModeId != "standard" && source.RuleConfiguration.RuleModeId != "entrepreneur") throw new InvalidOperationException("rule_mode_not_implemented");
            if (source.RuleConfiguration.RuleModeId == "entrepreneur" && (!source.RuleConfiguration.EnabledExpansionIds.Contains("enterprise") || !source.RuleConfiguration.EnabledExpansionIds.Contains("infected")))
                throw new InvalidOperationException("missing_entrepreneur_dependency");
            RuleModeReadiness.ValidateReady(source);
            if (source.ItemZones != null && (source.ItemZones.Items.Count > 0 || source.ItemZones.Zones.Count > 0)) throw new InvalidOperationException("setup_recipe_already_created");
            var state = GameStateCloneService.DeepClone(source);
            state.ItemZones = new ItemZoneRuntimeState();
            BuildZones(state, map);
            PlayerAreaPolicy.ConfigureSupplies(state, recipe);
            var entryIds = new HashSet<string>(StringComparer.Ordinal);
            var playerIds = state.Players.Select(player => player.PlayerId).OrderBy(id => id).ToList();
            if (playerIds.Any(id => id <= 0) || playerIds.Count != playerIds.Distinct().Count()) throw new InvalidOperationException("PlayerIds_invalid");
            foreach (var entry in recipe)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.EntryId) || string.IsNullOrWhiteSpace(entry.DefinitionId) || entry.Quantity < 0 || !entryIds.Add(entry.EntryId) || types.Find(entry.TypeId) == null)
                    throw new InvalidOperationException("setup_recipe_entry_invalid");
                var owners = entry.PerPlayer ? playerIds : new List<int> { -1 };
                for (int playerIndex = 0; playerIndex < owners.Count; playerIndex++)
                    for (int copy = 0; copy < entry.Quantity; copy++)
                    {
                        int owner = owners[playerIndex];
                        string zoneId = FindRoleZone(state, entry.InitialZoneRole, owner);
                        var subject = entry.TypeId == "infected.company_marker" ? ItemSubjectRef.Company("stonehoove_security") : owner > 0 ? ItemSubjectRef.Player(owner) : new ItemSubjectRef();
                        AddItem(state, entry.DefinitionId, entry.TypeId, entry.EntryId, playerIndex * entry.Quantity + copy, zoneId, subject);
                    }
            }
            state.EntitySchemaVersion = GameProtocolVersions.Entity;
            BindDerivedScoreMarkers(state);
            ItemZoneLegacyProjection.Bind(state, true);
            if (cardZones != null)
            {
                var declaredDefinitions = new HashSet<string>(StringComparer.Ordinal);
                foreach (var binding in cardZones)
                {
                    if (binding == null || !declaredDefinitions.Add(binding.DefinitionId) || binding.CardZones == null) throw new InvalidOperationException("cardZones_definition_duplicate");
                    foreach (var item in state.ItemZones.Items.Where(value => value.DefinitionId == binding.DefinitionId).ToList())
                        foreach (var declaration in binding.CardZones)
                        {
                            if (state.ContentDefinitions?.UsageTracks.Any(t => t.DefinitionId == item.DefinitionId && t.CardZoneLocalId == declaration.LocalZoneId && t.InitializationPolicy == "public_declaration") == true) continue;
                            if (!TryCreateCardZone(state, item.ItemId, declaration, out _, out var declarationReason)) throw new InvalidOperationException(declarationReason);
                        }
                }
            }
            string reason;
            if (!new ItemZoneQuery(types, templates).TryValidate(state, out reason)) throw new InvalidOperationException(reason);
            return state;
        }

        public bool TryCreateCardZone(GameState state, string carrierItemId, CardZoneDefinition declaration, out string zoneId, out string reasonCode)
        {
            zoneId = string.Empty;
            reasonCode = string.Empty;
            var carrier = state?.ItemZones?.Items.Find(item => item.ItemId == carrierItemId);
            if (carrier == null) { reasonCode = "unknown_item"; return false; }
            if (!templates.ValidateCardZoneDeclaration(carrier.TypeId, declaration, out reasonCode)) return false;
            var copy = GameStateCloneService.DeepClone(state);
            string id = StableIdFactory.Create("zone", state.GameId, declaration.ZoneTemplateId, carrier.ItemId, declaration.LocalZoneId);
            if (copy.ItemZones.Zones.Exists(zone => zone.ZoneId == id)) { reasonCode = "LocalZoneId_duplicate"; return false; }
            var zone = new ZoneInstanceState { ZoneId = id, TemplateId = declaration.ZoneTemplateId, LocalZoneId = declaration.LocalZoneId,
                ParentKind = ZoneParentKind.Item, ParentId = carrier.ItemId, VisibilityPolicyId = declaration.VisibilityPolicyId,
                OwnerPlayerId = carrier.OwnerSubject.PlayerId, Configuration = GameStateCloneService.DeepClone(declaration) };
            if (zone.TemplateId == "core.zone.board_marker_track" && declaration.LocalZoneId.StartsWith("cooperation_", StringComparison.Ordinal))
            {
                if (!int.TryParse(declaration.LocalZoneId.Substring("cooperation_".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var playerId) || state.FindPlayer(playerId) == null)
                { reasonCode = "LocalZoneId_cooperation_player_invalid"; return false; }
                zone.OwnerPlayerId = playerId;
            }
            zone.LegalSlotIds.AddRange(declaration.Slots);
            if (zone.TemplateId == "core.zone.board_marker_track" && declaration.Slots.Count == 0 && declaration.LocalZoneId.StartsWith("cooperation_", StringComparison.Ordinal)) for (int level = 0; level <= 5; level++) zone.LegalSlotIds.Add("level_" + level);
            copy.ItemZones.Zones.Add(zone);
            AddOrder(copy, zone);
            if (!new ItemZoneQuery(types, templates).TryValidate(copy, out reasonCode)) return false;
            GameStateCloneService.CopyTo(state, copy);
            zoneId = id;
            return true;
        }

        public bool TryCreateEffectStaging(GameState state, string ownerEffectId, CardZoneDefinition declaration, out string zoneId, out string reasonCode)
        {
            zoneId = string.Empty;
            reasonCode = string.Empty;
            var owner = state?.EffectRuntime?.EffectNodes.Find(node => node.EffectId == ownerEffectId);
            if (owner == null || owner.Status == EffectNodeStatus.Completed || owner.Status == EffectNodeStatus.Failed || owner.Status == EffectNodeStatus.Faulted) { reasonCode = "OwnerEffectId_required"; return false; }
            if (declaration == null || declaration.ZoneTemplateId != "core.zone.effect_staging" || string.IsNullOrWhiteSpace(declaration.LocalZoneId) || !ItemZoneVisibility.IsKnown(declaration.VisibilityPolicyId))
            { reasonCode = "zoneTemplateId_rejected"; return false; }
            var copy = GameStateCloneService.DeepClone(state);
            string id = StableIdFactory.Create("zone", state.GameId, declaration.ZoneTemplateId, ownerEffectId, declaration.LocalZoneId);
            if (copy.ItemZones.Zones.Exists(zone => zone.ZoneId == id)) { reasonCode = "LocalZoneId_duplicate"; return false; }
            copy.ItemZones.Zones.Add(new ZoneInstanceState { ZoneId = id, TemplateId = declaration.ZoneTemplateId, LocalZoneId = declaration.LocalZoneId, ParentKind = ZoneParentKind.Zone,
                ParentId = Zone(copy, "table").ZoneId, OwnerEffectId = ownerEffectId, OwnerPlayerId = state.EffectRuntime.EffectNodes.Find(node => node.EffectId == ownerEffectId).PlayerId,
                VisibilityPolicyId = declaration.VisibilityPolicyId, Configuration = GameStateCloneService.DeepClone(declaration) });
            if (!new ItemZoneQuery(types, templates).TryValidate(copy, out reasonCode)) return false;
            GameStateCloneService.CopyTo(state, copy);
            zoneId = id;
            return true;
        }

        public bool TryCreateGrantedItem(GameState state, string sourceEffectId, string businessKey, string definitionId, string typeId, string zoneId, ItemSubjectRef owner, out string itemId, out string reasonCode)
        {
            itemId = string.Empty;
            reasonCode = string.Empty;
            var node = state?.EffectRuntime?.EffectNodes.Find(value => value.EffectId == sourceEffectId);
            if (node == null || node.Status == EffectNodeStatus.Completed || node.Status == EffectNodeStatus.Failed || node.Status == EffectNodeStatus.Faulted)
            { reasonCode = "sourceEffectId_rejected"; return false; }
            if (string.IsNullOrWhiteSpace(businessKey) || string.IsNullOrWhiteSpace(definitionId) || types.Find(typeId) == null) { reasonCode = "setup_recipe_entry_invalid"; return false; }
            var copy = GameStateCloneService.DeepClone(state);
            string entry = sourceEffectId + "/" + businessKey;
            string id = StableIdFactory.Create("item", state.GameId, definitionId, entry, "0");
            if (copy.ItemZones.Items.Exists(item => item.ItemId == id)) { reasonCode = "duplicate_item_id"; return false; }
            AddItem(copy, definitionId, typeId, entry, 0, zoneId, owner ?? new ItemSubjectRef());
            if (!new ItemZoneQuery(types, templates).TryValidate(copy, out reasonCode)) return false;
            GameStateCloneService.CopyTo(state, copy);
            itemId = id;
            return true;
        }

        internal void BuildZones(GameState state, GameMapDefinition map)
        {
            foreach (string root in new[] { "map", "table", "players", "game_box" }) AddZone(state, root, root, null, state.GameId);
            var mapRoot = Zone(state, "map");
            AddZone(state, "score_track", "score_track", mapRoot, state.GameId);
            AddZone(state, "map_tokens", "map_tokens", mapRoot, state.GameId);
            var regions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var region in map.Regions)
            {
                if (string.IsNullOrEmpty(region.RegionId) || !regions.Add(region.RegionId)) throw new InvalidOperationException("RegionId_invalid");
                AddZone(state, "map_region", region.RegionId, mapRoot, state.GameId);
            }
            foreach (var location in map.Locations)
            {
                if (!regions.Contains(location.RegionId)) throw new InvalidOperationException("RegionId_location_invalid");
                var parent = AddZone(state, "map_location", location.LocationId, Zone(state, location.RegionId, "core.zone.map_region"), state.GameId);
                var influence = AddZone(state, "influence_slots", "influence", parent, parent.ZoneId);
                for (int slot = 0; slot < location.InfluenceSlotCount; slot++) influence.LegalSlotIds.Add(InfluenceService.GetLocationSlotId(location.LocationId, slot));
                if (location.CanDockCity) AddZone(state, "city_dock", "cities", parent, parent.ZoneId);
                AddZone(state, "map_tokens", "tokens", parent, parent.ZoneId);
                AddZone(state, "resource_point_markers", "resource_point_markers", parent, parent.ZoneId);
            }
            foreach (var route in map.Routes)
            {
                if (!regions.Contains(route.RegionId)) throw new InvalidOperationException("RegionId_route_invalid");
                var parent = AddZone(state, "map_route", route.RouteId, Zone(state, route.RegionId, "core.zone.map_region"), state.GameId);
                var influence = AddZone(state, "influence_slots", "influence", parent, parent.ZoneId);
                for (int slot = 0; slot < route.InfluenceSlotCount; slot++) influence.LegalSlotIds.Add(InfluenceService.GetRouteSlotId(route.RouteId, slot));
                AddZone(state, "map_tokens", "tokens", parent, parent.ZoneId);
                AddZone(state, "route_road", "road", parent, parent.ZoneId);
                AddZone(state, "covered_cubes", "covered", parent, parent.ZoneId);
            }
            var table = Zone(state, "table");
            var supply = AddZone(state, "public_supply", "public_supply", table, state.GameId);
            foreach (string display in new[] { "enterprise_display", "department_display", "city_style_display" })
                AddZone(state, display, display == "enterprise_display" ? "enterprises" : display == "department_display" ? "departments" : "city_styles", table, state.GameId);
            var facilities = AddZone(state, "facility_supply", "facility_supply", supply, state.GameId);
            for (int slot = 0; slot < 6; slot++) AddZone(state, "facility_supply_slot", "facility_supply_" + slot, facilities, state.GameId);
            foreach (string deck in new[] { "facility_deck", "operator_deck", "character_deck", "event_green", "event_yellow", "event_red" })
            {
                var zone = AddZone(state, "card_deck", deck, supply, state.GameId, -1, ItemZoneVisibility.Hidden);
                string type = deck.StartsWith("event_", StringComparison.Ordinal) ? "core.event_card" : deck == "facility_deck" ? "core.facility_card" : deck == "operator_deck" ? "core.operator_card" : "core.character_card";
                zone.Configuration = Configuration(zone, ItemPhysicalKind.Card, type, ZoneOrderingKind.Deck);
            }
            foreach (string pool in new[] { "awards", "contracts", "company_markers", "flowing_shapes" })
            {
                var zone = AddZone(state, "reward_pool", pool, supply, state.GameId);
                zone.Configuration = Configuration(zone, pool == "company_markers" ? ItemPhysicalKind.Cube : pool == "contracts" ? ItemPhysicalKind.Token : ItemPhysicalKind.Card,
                    pool == "awards" ? "core.award_card" : pool == "contracts" ? ItemTypeRegistry.ContractTokenTypeId : pool == "company_markers" ? "infected.company_marker" : "core.generated_character_card");
            }
            foreach (var player in state.Players)
            {
                string anchor = player.PlayerId.ToString(CultureInfo.InvariantCulture);
                var panel = AddZone(state, "player_panel", "player_" + anchor, Zone(state, "players"), anchor, player.PlayerId);
                foreach (string cards in new[] { "hand", "discard", "covered", "persistent" }) AddZone(state, "character_" + cards, cards, panel, anchor, player.PlayerId, cards == "hand" || cards == "covered" ? ItemZoneVisibility.Owner : ItemZoneVisibility.Public);
                AddZone(state, "player_supply", "supply", panel, anchor, player.PlayerId);
                AddZone(state, "player_rewards", "rewards", panel, anchor, player.PlayerId);
                var entrepreneur = AddZone(state, "entrepreneur_container", "entrepreneur", panel, anchor, player.PlayerId);
                AddZone(state, "entrepreneur_pending", "entrepreneur_pending", entrepreneur, anchor, player.PlayerId);
                AddZone(state, "entrepreneur_active", "entrepreneur_active", entrepreneur, anchor, player.PlayerId);
                var city = AddZone(state, "city_panel", "city_panel", panel, anchor, player.PlayerId);
                AddZone(state, "city_dock", "city_reserve", city, anchor, player.PlayerId);
for (int cell = 0; cell < BuildFacilityService.CityBoardSlotCount; cell++)
                {
                    var main = AddZone(state, "city_facility_cell", "city_cell_" + cell, city, anchor, player.PlayerId);
                    AddZone(state, "city_facility_covered", "covered", main, main.ZoneId, player.PlayerId);
                }
            }
            foreach (string box in new[] { "reserve", "retired", "secret_retired" }) AddZone(state, "box_" + box, box, Zone(state, "game_box"), state.GameId, -1, box == "secret_retired" ? ItemZoneVisibility.SecretRetired : box == "reserve" ? ItemZoneVisibility.Hidden : ItemZoneVisibility.Public);
        }

        internal ZoneInstanceState AddZone(GameState state, string name, string local, ZoneInstanceState parent, string anchor, int playerId = -1, string visibility = ItemZoneVisibility.Public)
        {
            string template = "core.zone." + name;
            var definition = templates.Find(template);
            if (definition == null || parent != null && !definition.AllowedParentTemplateIds.Contains(parent.TemplateId)) throw new InvalidOperationException("ParentId_rejected");
            var zone = new ZoneInstanceState { TemplateId = template, LocalZoneId = local, ZoneId = StableIdFactory.Create("zone", state.GameId, template, anchor, local),
                ParentKind = parent == null ? ZoneParentKind.Root : ZoneParentKind.Zone, ParentId = parent?.ZoneId ?? string.Empty, OwnerPlayerId = playerId, VisibilityPolicyId = visibility };
            if (state.ItemZones.Zones.Exists(value => value.ZoneId == zone.ZoneId)) throw new InvalidOperationException("duplicate_zone_id");
            state.ItemZones.Zones.Add(zone);
            AddOrder(state, zone);
            return zone;
        }
        internal ItemInstanceState AddItem(GameState state, string definition, string typeId, string entry, int copy, string zoneId, ItemSubjectRef owner)
        {
            var type = types.Find(typeId);
            if (type == null) throw new InvalidOperationException("TypeId_unknown");
            var item = new ItemInstanceState { ItemId = StableIdFactory.Create("item", state.GameId, definition, entry, copy.ToString(CultureInfo.InvariantCulture)),
                DefinitionId = definition, TypeId = typeId, PhysicalKind = type.PhysicalKind, ZoneId = zoneId,
                OwnerSubject = GameStateCloneService.DeepClone(owner), ControllerSubject = GameStateCloneService.DeepClone(owner) };
            if (state.ItemZones.Items.Exists(value => value.ItemId == item.ItemId)) throw new InvalidOperationException("duplicate_item_id");
            var initial = state.ItemZones.Zones.Single(zone => zone.ZoneId == zoneId);
            if ((item.PhysicalKind == ItemPhysicalKind.Cube || types.IsAssignableTo(item.TypeId, "core.facility_card") && initial.TemplateId == "core.zone.player_supply") && new[] { "core.zone.player_supply", "core.zone.reward_pool", "core.zone.box_reserve" }.Contains(initial.TemplateId)) item.OriginalSupplyZoneId = zoneId;
            if (item.PhysicalKind == ItemPhysicalKind.Token && initial.TemplateId == "core.zone.box_reserve") item.OriginalSupplyZoneId = zoneId;
            if (types.IsAssignableTo(item.TypeId, ItemTypeRegistry.ContractTokenTypeId) && initial.TemplateId == "core.zone.reward_pool" && initial.LocalZoneId == "contracts") item.OriginalSupplyZoneId = zoneId;
            state.ItemZones.Items.Add(item);
            state.ItemZones.Orders.Find(value => value.ZoneId == zoneId)?.ItemIds.Add(item.ItemId);
            return item;
        }
        private void AddOrder(GameState state, ZoneInstanceState zone)
        {
            if (templates.Find(zone.TemplateId).OrderingKind != ZoneOrderingKind.Unordered) state.ItemZones.Orders.Add(new ZoneOrderState { ZoneId = zone.ZoneId });
        }
        internal static ZoneInstanceState Zone(GameState state, string local, string template = null, int player = -1) => state.ItemZones.Zones.Find(zone => zone.LocalZoneId == local && (template == null || zone.TemplateId == template) && (player < 0 || zone.OwnerPlayerId == player)) ?? throw new InvalidOperationException("initialZoneRole_unknown: " + local);
        private static string FindRoleZone(GameState state, string role, int player)
        {
            string[] parts = role.Split('/');
            if (parts.Length == 2 && parts[0] == "location")
            {
                var location = Zone(state, parts[1], "core.zone.map_location");
                return state.ItemZones.Zones.Single(zone => zone.ParentId == location.ZoneId && zone.TemplateId == "core.zone.resource_point_markers").ZoneId;
            }
            var target = state.ItemZones.Zones.Find(zone => zone.LocalZoneId == role && zone.OwnerPlayerId == player)
                ?? state.ItemZones.Zones.Find(zone => zone.LocalZoneId == role && zone.OwnerPlayerId < 0);
            return target?.ZoneId ?? throw new InvalidOperationException("initialZoneRole_unknown: " + role);
        }
        internal static CardZoneDefinition Configuration(ZoneInstanceState zone, ItemPhysicalKind kind, string type, ZoneOrderingKind ordering = ZoneOrderingKind.Unordered) => new CardZoneDefinition
        {
            LocalZoneId = zone.LocalZoneId, ZoneTemplateId = zone.TemplateId, AcceptedItemKinds = new List<ItemPhysicalKind> { kind }, AcceptedTypeIds = new List<string> { type },
            CapacityPolicy = "unbounded", OrderingKind = ordering, VisibilityPolicyId = zone.VisibilityPolicyId,
            EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents"
        };
        internal static void BindDerivedScoreMarkers(GameState state)
        {
            var items = state.ItemZones.Items;
            var zones = state.ItemZones.Zones;
            // 畸形对象图沿用原入口和错误行为；合法图索引保留Find的首个匹配语义。
            if (items == null || zones == null || items.Any(item => item == null || item.ZoneId == null) || zones.Any(zone => zone == null || zone.ZoneId == null))
            {
                foreach (var item in items.Where(item => zones.Find(zone => zone.ZoneId == item.ZoneId)?.TemplateId == "core.zone.score_track"))
                    item.SlotId = "score_" + (state.FindPlayer(item.OwnerSubject.PlayerId)?.Score ?? throw new InvalidOperationException("score_owner_invalid")).ToString(CultureInfo.InvariantCulture);
                return;
            }
            var byId = new Dictionary<string, ZoneInstanceState>(StringComparer.Ordinal);
            foreach (var zone in zones)
                if (!byId.ContainsKey(zone.ZoneId)) byId.Add(zone.ZoneId, zone);
            foreach (var item in items.Where(item => byId.TryGetValue(item.ZoneId, out var zone) && zone.TemplateId == "core.zone.score_track"))
                item.SlotId = "score_" + (state.FindPlayer(item.OwnerSubject.PlayerId)?.Score ?? throw new InvalidOperationException("score_owner_invalid")).ToString(CultureInfo.InvariantCulture);
        }
    }

    public static class ItemZoneStateMigrator
    {
        public const string Unsupported = "legacy_entity_state_unsupported";
        public static bool TryMigrate(GameState legacy, GameMapDefinition map, ItemZoneMigrationEvidence evidence, out GameState migrated, out string reasonCode)
        {
            migrated = null;
            reasonCode = Unsupported;
            if (legacy?.ItemZones?.Items?.Any(item => item.DefinitionId == YC.Domain.Influence.CompanyInfluenceRules.ContractDefinitionId && item.TypeId == "core.contract_card") == true ||
                evidence?.Recipe?.Any(entry => entry.DefinitionId == YC.Domain.Influence.CompanyInfluenceRules.ContractDefinitionId && entry.TypeId == "core.contract_card") == true)
            { reasonCode = "legacy_lifetime_security_card_requires_original_version"; return false; }
            if (legacy == null || map == null || evidence == null || string.IsNullOrWhiteSpace(evidence.RuleContractVersion) || string.IsNullOrWhiteSpace(evidence.ContentPackHash) ||
                evidence.GameId != legacy.GameId || evidence.MapId != legacy.MapId || legacy.MapId != map.MapId || legacy.ContentPackHash != evidence.ContentPackHash ||
                legacy.ItemZones?.Zones.Count > 0 || legacy.Round != 0 || legacy.ActionRound != 0 || legacy.Phase != YC.Domain.Rules.GamePhase.Setup || legacy.HasPendingChoice() ||
                legacy.EffectRuntime?.EffectNodes.Count > 0 || legacy.Map.Influences.Count > 0 || legacy.Map.RoadRouteIds.Count > 0 || legacy.Map.ResourceTokens.Count > 0 ||
                legacy.Players.Any(player => !string.IsNullOrEmpty(player.CityLocationId) || player.DeclaredCityStyles.Count > 0) || legacy.GameBox.Cards.Count > 0 || legacy.Decks.CardPools.Count > 0 ||
                !evidence.PlayerIds.OrderBy(id => id).SequenceEqual(legacy.Players.Select(player => player.PlayerId).OrderBy(id => id))) return false;
            try
            {
                var factory = new ItemZoneStateFactory();
                var initial = GameStateCloneService.DeepClone(legacy);
                initial.Map.FacilityInstances.Clear();
                initial.RuleConfiguration = evidence.RuleConfiguration?.Clone();
                if (initial.RuleConfiguration == null || initial.RuleConfiguration.RuleContractVersion != evidence.RuleContractVersion) throw new InvalidOperationException(Unsupported);
                migrated = factory.CreateForSetup(initial, map, evidence.Recipe);
                // 配方给出确切总数；列表只用来定位这些已创建实例，不凭位置再造一份。
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var player in legacy.Players)
                {
                    Assign(migrated, used, player.HandCardIds, "hand", player.PlayerId);
                    Assign(migrated, used, player.DiscardCardIds, "discard", player.PlayerId);
                    if (!string.IsNullOrEmpty(player.CoveredCharacterCardId)) Assign(migrated, used, new[] { player.CoveredCharacterCardId }, "covered", player.PlayerId);
                    var cubes = migrated.ItemZones.Items.Where(item => item.TypeId == "core.player_marker" && item.OwnerSubject.PlayerId == player.PlayerId).ToList();
                    if (cubes.Count != player.InfluenceSupply + (player.HasScoreTrackMarker ? 1 : 0)) throw new InvalidOperationException(Unsupported);
                    foreach (var cube in cubes) { SetPosition(migrated, cube, ItemZoneStateFactory.Zone(migrated, "supply", null, player.PlayerId).ZoneId); used.Add(cube.ItemId); }
                    if (player.HasScoreTrackMarker) { SetPosition(migrated, cubes.Last(), ItemZoneStateFactory.Zone(migrated, "score_track").ZoneId); cubes.Last().SlotId = "score_" + player.Score.ToString(CultureInfo.InvariantCulture); }
                    foreach (var placement in legacy.Map.Facilities.Where(value => value.PlayerId == player.PlayerId))
                    {
                        Assign(migrated, used, new[] { placement.FacilityCardId }, "city_cell_" + placement.CityBoardSlotIndex, player.PlayerId);
                        var behavior = legacy.Map.FacilityInstances.Find(value => value.ContentInstanceId == placement.ContentInstanceId);
                        if (behavior != null)
                        {
                            if (behavior.OwnerPlayerId != player.PlayerId || behavior.CityBoardSlotIndex != placement.CityBoardSlotIndex || behavior.FacilityCardId != placement.FacilityCardId) throw new InvalidOperationException(Unsupported);
                            var record = GameStateCloneService.DeepClone(behavior);
                            string cellZoneId = ItemZoneStateFactory.Zone(migrated, "city_cell_" + placement.CityBoardSlotIndex, null, player.PlayerId).ZoneId;
                            record.ContentInstanceId = migrated.ItemZones.Items.Single(item => item.ZoneId == cellZoneId).ItemId;
                            record.BindItemZones(migrated, true);
                            migrated.Map.FacilityInstances.Add(record);
                        }
                    }
                    foreach (var city in migrated.ItemZones.Items.Where(item => item.PhysicalKind == ItemPhysicalKind.MobileCity && item.OwnerSubject.PlayerId == player.PlayerId)) used.Add(city.ItemId);
                }
                foreach (var token in migrated.ItemZones.Items.Where(item => item.TypeId == "core.start_player_token")) used.Add(token.ItemId);
                Assign(migrated, used, legacy.Decks.FacilitySupply, "facility_supply", -1, true);
                Assign(migrated, used, legacy.Decks.CityStyleSupply, "city_styles", -1);
                var legacyDecks = new Dictionary<string, LegacyPositionList<string>> { { "facility_deck", legacy.Decks.FacilityDeck }, { "character_deck", legacy.Decks.CharacterDeck },
                    { "event_green", legacy.Decks.EventDeckGreen }, { "event_yellow", legacy.Decks.EventDeckYellow }, { "event_red", legacy.Decks.EventDeckRed } };
                foreach (var pair in legacyDecks)
                {
                    var proof = evidence.Decks.Find(deck => deck.LocalZoneId == pair.Key);
                    if (proof == null || !proof.BottomToTopDefinitionIds.SequenceEqual(pair.Value)) throw new InvalidOperationException(Unsupported);
                    Assign(migrated, used, pair.Value.AsEnumerable().Reverse(), pair.Key, -1);
                }
                if (used.Count != migrated.ItemZones.Items.Count) throw new InvalidOperationException(Unsupported);
                if (migrated.Map.FacilityInstances.Count != legacy.Map.FacilityInstances.Count) throw new InvalidOperationException(Unsupported);
                ItemZoneStateFactory.BindDerivedScoreMarkers(migrated);
                if (!new ItemZoneQuery().TryValidate(migrated, out reasonCode)) { migrated = null; reasonCode = Unsupported; return false; }
                ItemZoneLegacyProjection.Bind(migrated);
                reasonCode = string.Empty;
                return true;
            }
            catch (InvalidOperationException) { migrated = null; reasonCode = Unsupported; return false; }
        }
        private static void Assign(GameState state, HashSet<string> used, IEnumerable<string> definitions, string local, int player, bool supply = false)
        {
            var zone = supply ? null : ItemZoneStateFactory.Zone(state, local, null, player);
            if (zone != null) state.ItemZones.Orders.Find(order => order.ZoneId == zone.ZoneId)?.ItemIds.Clear();
            int index = 0;
            foreach (string definition in definitions)
            {
                if (string.IsNullOrEmpty(definition)) { index++; continue; }
                bool cityCell = local.StartsWith("city_cell_", StringComparison.Ordinal);
                var item = state.ItemZones.Items.Find(value => !used.Contains(value.ItemId) && value.DefinitionId == definition && (player < 0 || value.OwnerSubject.PlayerId == player || cityCell && value.OwnerSubject.Kind == ItemSubjectKind.None));
                if (item == null) throw new InvalidOperationException(Unsupported);
                string target = supply ? ItemZoneStateFactory.Zone(state, "facility_supply_" + index).ZoneId : zone.ZoneId;
                SetPosition(state, item, target);
                if (cityCell) item.ControllerSubject = ItemSubjectRef.Player(player);
                used.Add(item.ItemId);
                index++;
            }
        }
        internal static void SetPosition(GameState state, ItemInstanceState item, string zoneId)
        {
            state.ItemZones.Orders.Find(order => order.ZoneId == item.ZoneId)?.ItemIds.Remove(item.ItemId);
            item.ZoneId = zoneId;
            item.SlotId = string.Empty;
            var target = state.ItemZones.Orders.Find(order => order.ZoneId == zoneId);
            if (target != null && !target.ItemIds.Contains(item.ItemId)) target.ItemIds.Add(item.ItemId);
        }
    }
}
