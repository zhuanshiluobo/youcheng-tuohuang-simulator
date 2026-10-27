using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Items
{
    /// <summary>每次查询从持久 DTO 构造索引；不缓存局状态，不创建或修改实例。</summary>
    public sealed class ItemZoneQuery
    {
        // 可选整次校验计时；默认路径没有时钟、采样分配或规则状态写入。
        [ThreadStatic] public static Action<long> ValidationTimingSample;
        private readonly struct ValidationSample : IDisposable
        {
            private readonly Action<long> record;
            private readonly long started;
            internal ValidationSample(Action<long> record)
            { this.record = record; started = record == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp(); }
            public void Dispose()
            {
                if (record == null) return;
                try { record(System.Diagnostics.Stopwatch.GetTimestamp() - started); }
                catch (Exception) { }
            }
        }
        // 内建Schema从不依赖对局。仅复用冻结注册表，任何局状态/玩家可见性都不放入静态缓存。
        private sealed class BuiltInSchema
        {
            internal readonly ItemTypeRegistry Types;
            internal readonly ZoneTemplateRegistry Templates;
            internal BuiltInSchema()
            {
                Types = ItemTypeRegistry.CreateBuiltIn();
                Types.Freeze();
                Templates = ZoneTemplateRegistry.CreateBuiltIn(Types);
                Templates.Freeze();
            }
        }
        private static readonly Lazy<BuiltInSchema> DefaultSchema = new Lazy<BuiltInSchema>(() => new BuiltInSchema());
        private readonly ItemTypeRegistry types;
        private readonly ZoneTemplateRegistry templates;
        private enum CharacterBackKind { Unknown, Basic, Operator, Generated }
        public ItemZoneQuery(ItemTypeRegistry types = null, ZoneTemplateRegistry templates = null)
        {
            this.types = types ?? DefaultSchema.Value.Types;
            this.templates = templates ?? (types == null ? DefaultSchema.Value.Templates : ZoneTemplateRegistry.CreateBuiltIn(this.types));
        }
        public ItemInstanceState FindItem(GameState state, string itemId) => GameStateCloneService.DeepClone(state?.ItemZones?.Items?.Find(item => item != null && item.ItemId == itemId));
        public ZoneInstanceState FindZone(GameState state, string zoneId) => GameStateCloneService.DeepClone(state?.ItemZones?.Zones?.Find(zone => zone != null && zone.ZoneId == zoneId));
        public IReadOnlyList<string> GetItemIds(GameState state, string zoneId)
        {
            if (state?.ItemZones?.Zones?.Find(zone => zone != null && zone.ZoneId == zoneId) == null) throw new InvalidOperationException("unknown_zone: ZoneId");
            var order = state.ItemZones.Orders.Find(value => value.ZoneId == zoneId);
            return (order == null ? state.ItemZones.Items.Where(item => item.ZoneId == zoneId).Select(item => item.ItemId).ToList() : new List<string>(order.ItemIds)).AsReadOnly();
        }
        public IReadOnlyList<string> GetTopItemIds(GameState state, string zoneId, int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            var zone = state?.ItemZones?.Zones?.Find(value => value != null && value.ZoneId == zoneId);
            if (zone == null) throw new InvalidOperationException("unknown_zone: ZoneId");
            if (templates.FindRules(zone.TemplateId).OrderingKind != ZoneOrderingKind.Deck) throw new InvalidOperationException("orderingKind_not_deck");
            return GetItemIds(state, zoneId).Take(count).ToList().AsReadOnly();
        }
        public bool IsPlayerMarker(GameState state, string itemId)
        {
            var item = state?.ItemZones?.Items?.Find(value => value != null && value.ItemId == itemId);
            return item != null && item.PhysicalKind == ItemPhysicalKind.Cube && types.IsAssignableTo(item.TypeId, "core.player_marker");
        }
        public bool IsInfluence(GameState state, string itemId)
        {
            var item = state?.ItemZones?.Items?.Find(value => value != null && value.ItemId == itemId);
            var zone = item == null ? null : state.ItemZones.Zones.Find(value => value != null && value.ZoneId == item.ZoneId);
            return item != null && zone != null && zone.TemplateId == "core.zone.influence_slots" &&
                item.PhysicalKind == ItemPhysicalKind.Cube && types.HasOperationProfile(item.TypeId, "core.influence_cube") &&
                zone.LegalSlotIds.Contains(item.SlotId);
        }

        public bool TryValidate(GameState state, out string reasonCode)
        {
            using var sample = new ValidationSample(ValidationTimingSample);
            reasonCode = string.Empty;
            if (state == null || state.EntitySchemaVersion != GameProtocolVersions.Entity || state.ItemZones == null) return Reject("EntitySchemaVersion_unsupported", out reasonCode);
            var runtime = state.ItemZones;
            if (runtime.NamingVersion != PlayerAreaPolicy.NamingVersion) return Reject("item_zone_naming_version_unsupported", out reasonCode);
            if (!PlayerAreaPolicy.Validate(state, out reasonCode)) return false;
            if (runtime.Items == null || runtime.Zones == null || runtime.Orders == null || runtime.Relations == null) return Reject("ItemZones_required", out reasonCode);
            // 仅限这一次完整校验。下一次调用重建，工作副本即使revision尚未提交也不会读旧位置。
            var templateDefinitions = new Dictionary<string, ZoneTemplateRules>(StringComparer.Ordinal);
            var typeDefinitions = new Dictionary<string, ItemPhysicalKind?>(StringComparer.Ordinal);
            ZoneTemplateRules Template(string id)
            {
                if (id == null) return null;
                if (!templateDefinitions.TryGetValue(id, out var definition)) templateDefinitions.Add(id, definition = templates.FindRules(id));
                return definition;
            }
            ItemPhysicalKind? Type(string id)
            {
                if (id == null) return null;
                if (!typeDefinitions.TryGetValue(id, out var definition)) typeDefinitions.Add(id, definition = types.FindPhysicalKind(id));
                return definition;
            }
            if (runtime.Zones.Count > 0 && (state.Players.Any(player => player.HandCardIds.Values.Count > 0 || player.DiscardCardIds.Values.Count > 0 || player.BuiltFacilityIds.Values.Count > 0 ||
                player.LegacyInfluenceSupply != 0 || player.LegacyHasScoreTrackMarker || !string.IsNullOrEmpty(player.LegacyCityLocationId) || !string.IsNullOrEmpty(player.LegacyCoveredCharacterCardId)) ||
                new[] { state.Decks.CharacterDeck, state.Decks.CharacterDiscard, state.Decks.FacilityDeck, state.Decks.FacilitySupply, state.Decks.CityStyleSupply, state.Decks.EventDeckGreen, state.Decks.EventDeckYellow, state.Decks.EventDeckRed, state.Map.RoadRouteIds, state.Map.RemovedFromGameCardIds }.Any(list => list.Values.Count > 0) ||
                state.Decks.CardPools.Any(pool => pool.RemainingCardIds.Values.Count > 0) || state.Map.Influences.Values.Count > 0 || state.Map.Facilities.Values.Count > 0 || state.Map.ResourceTokens.Values.Count > 0 ||
                state.GameBox.Cards.Count > 0 || state.GameBox.Markers.Count > 0 || state.GameBox.Tokens.Count > 0 || state.GameBox.Enterprises.Count > 0)) return Reject("legacy_position_write_rejected", out reasonCode);
            var items = new Dictionary<string, ItemInstanceState>(StringComparer.Ordinal);
            var zones = new Dictionary<string, ZoneInstanceState>(StringComparer.Ordinal);
            foreach (var item in runtime.Items)
                if (item == null || string.IsNullOrWhiteSpace(item.ItemId) || items.ContainsKey(item.ItemId)) return Reject("duplicate_item_id", out reasonCode);
                else items.Add(item.ItemId, item);
            foreach (var zone in runtime.Zones)
                if (zone == null || string.IsNullOrWhiteSpace(zone.ZoneId) || zones.ContainsKey(zone.ZoneId)) return Reject("duplicate_zone_id", out reasonCode);
                else zones.Add(zone.ZoneId, zone);
            var membersByZone = new Dictionary<string, List<ItemInstanceState>>(StringComparer.Ordinal);
            foreach (var item in items.Values)
            {
                if (item.ZoneId == null) return Reject("ZoneId_unknown", out reasonCode);
                if (!membersByZone.TryGetValue(item.ZoneId, out var members)) membersByZone.Add(item.ZoneId, members = new List<ItemInstanceState>());
                members.Add(item);
            }
            var roots = new HashSet<string>(StringComparer.Ordinal);
            var anchorNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var zone in zones.Values)
            {
                var template = Template(zone.TemplateId);
                if (template == null) return Reject("TemplateId_unknown", out reasonCode);
                if (string.IsNullOrWhiteSpace(zone.LocalZoneId) || !anchorNames.Add(zone.ParentKind + ":" + zone.ParentId + ":" + zone.LocalZoneId + ":" + zone.OwnerEffectId)) return Reject("LocalZoneId_duplicate", out reasonCode);
                if (!ItemZoneVisibility.IsKnown(zone.VisibilityPolicyId)) return Reject("VisibilityPolicyId_rejected", out reasonCode);
                if (zone.TemplateId == "core.zone.box_secret_retired" && zone.VisibilityPolicyId != ItemZoneVisibility.SecretRetired) return Reject("VisibilityPolicyId_secret_retired", out reasonCode);
                if (zone.TemplateId == "core.zone.card_deck" && zone.VisibilityPolicyId != ItemZoneVisibility.Hidden) return Reject("VisibilityPolicyId_deck", out reasonCode);
                if ((zone.TemplateId == "core.zone.card_deck" || zone.TemplateId == "core.zone.reward_pool" || zone.TemplateId == "core.zone.effect_staging") && zone.Configuration == null) return Reject("Configuration_required", out reasonCode);
                if (zone.ParentKind == ZoneParentKind.Root && zone.VisibilityPolicyId != ItemZoneVisibility.Public) return Reject("VisibilityPolicyId_root", out reasonCode);
                if (zone.TemplateId == "core.zone.effect_staging" && (string.IsNullOrWhiteSpace(zone.OwnerEffectId) ||
                    state.EffectRuntime == null || !state.EffectRuntime.EffectNodes.Exists(node => node.EffectId == zone.OwnerEffectId))) return Reject("OwnerEffectId_required", out reasonCode);
                if (zone.TemplateId != "core.zone.effect_staging" && !string.IsNullOrEmpty(zone.OwnerEffectId)) return Reject("OwnerEffectId_rejected", out reasonCode);
                if (zone.Configuration != null && (zone.Configuration.ZoneTemplateId != zone.TemplateId || zone.Configuration.LocalZoneId != zone.LocalZoneId || zone.Configuration.VisibilityPolicyId != zone.VisibilityPolicyId)) return Reject("Configuration_zone_mismatch", out reasonCode);
                switch (zone.ParentKind)
                {
                    case ZoneParentKind.Root:
                        if (!string.IsNullOrEmpty(zone.ParentId) || !IsRoot(zone.TemplateId) || !roots.Add(zone.TemplateId)) return Reject("ParentId_root_invalid", out reasonCode);
                        if (zone.LocalZoneId != zone.TemplateId.Substring("core.zone.".Length)) return Reject("LocalZoneId_root_invalid", out reasonCode);
                        break;
                    case ZoneParentKind.Zone:
                        if (!zones.TryGetValue(zone.ParentId, out var parent) || !template.AllowedParentTemplateIds.Contains(parent.TemplateId) ||
                            !Template(parent.TemplateId).AllowedChildTemplateIds.Contains(zone.TemplateId)) return Reject("ParentId_rejected", out reasonCode);
                        if (zone.Configuration != null && !ValidateHostConfiguration(template, zone.Configuration, Type, out reasonCode)) return false;
                        break;
                    case ZoneParentKind.Item:
                        if (!items.TryGetValue(zone.ParentId, out var carrier) || !template.AllowedCarrierTypeIds.Any(id => types.IsAssignableTo(carrier.TypeId, id))) return Reject("ParentId_carrier_rejected", out reasonCode);
                        if (zone.Configuration == null) return Reject("Configuration_required", out reasonCode);
                        if (!templates.ValidateCardZoneDeclaration(carrier.TypeId, zone.Configuration, out reasonCode)) return false;
                        break;
                    default: return Reject("ParentKind_rejected", out reasonCode);
                }
                if (zone.LegalSlotIds == null || zone.LegalSlotIds.Count != zone.LegalSlotIds.Distinct(StringComparer.Ordinal).Count()) return Reject("LegalSlotIds_invalid", out reasonCode);
            }
            if (roots.Count != 4) return Reject("root_zones_required", out reasonCode);
            var occupiedSlots = new HashSet<string>(StringComparer.Ordinal);
            var localFields = new HashSet<string>(new[] { "faceUp", "orientation", "resourceType", "amount", "backColor", "backVisualKey",
                "activeFaceId", "activationAuthorizationRef", "sourceSelectionReceipt", "grantingSourceRefs" }, StringComparer.Ordinal);
            foreach (var item in items.Values)
            {
                if (item.DefinitionId == YC.Domain.Influence.CompanyInfluenceRules.ContractDefinitionId && item.TypeId != ItemTypeRegistry.ContractTokenTypeId)
                    return Reject("legacy_lifetime_security_card_requires_original_version", out reasonCode);
                var type = Type(item.TypeId);
                if (type == null || type.Value != item.PhysicalKind) return Reject("PhysicalKind_type_mismatch", out reasonCode);
                if (string.IsNullOrWhiteSpace(item.DefinitionId) || !zones.TryGetValue(item.ZoneId, out var zone)) return Reject("ZoneId_unknown", out reasonCode);
                if (!ValidSubject(item.OwnerSubject) || !ValidSubject(item.ControllerSubject)) return Reject("OwnerSubject_ControllerSubject_invalid", out reasonCode);
                if (item.OwnerSubject.Kind == ItemSubjectKind.Player && state.FindPlayer(item.OwnerSubject.PlayerId) == null ||
                    item.ControllerSubject.Kind == ItemSubjectKind.Player && state.FindPlayer(item.ControllerSubject.PlayerId) == null) return Reject("OwnerSubject_ControllerSubject_unknown_player", out reasonCode);
                if (types.IsAssignableTo(item.TypeId, "infected.company_marker") && (item.OwnerSubject.Kind != ItemSubjectKind.Company || item.OwnerSubject.CompanyId != "stonehoove_security")) return Reject("OwnerSubject_company_required", out reasonCode);
                if (types.IsAssignableTo(item.TypeId, "entrepreneur.gang_marker") && item.OwnerSubject.Kind != ItemSubjectKind.Player) return Reject("OwnerSubject_gang_player_required", out reasonCode);
                if (item.LocalState == null || item.LocalState.Kind != NormalizedValueKind.Object || !item.LocalState.IsValid()) return Reject("LocalState_invalid", out reasonCode);
                if (item.LocalState.Properties.Any(field => !localFields.Contains(field.Name))) return Reject("LocalState_field_rejected", out reasonCode);
                if (!YC.Domain.Effects.AbilitySourceSelection.ValidateLocalState(state, item, out reasonCode)) return false;
                if (!Accepts(zone, item, Template(zone.TemplateId))) return Reject("acceptedItemKinds_acceptedTypeIds_rejected", out reasonCode);
                if (zone.TemplateId == "core.zone.reward_pool" && state.ContentDefinitions?.Awards.Any(a => a.DefinitionId == item.DefinitionId && !a.RandomAwardPool) == true)
                    return Reject("content_granted_award_not_random_pool", out reasonCode);
                if (item.OriginalSupplyZoneId == null) return Reject("OriginalSupplyZoneId_invalid", out reasonCode);
                if (item.OriginalSupplyZoneId.Length > 0 && ((!ItemTransferService.IsTokenKind(item.PhysicalKind) && !types.IsAssignableTo(item.TypeId, "core.facility_card")) || !zones.TryGetValue(item.OriginalSupplyZoneId, out var supply) ||
                    !new[] { "core.zone.player_supply", "core.zone.reward_pool", "core.zone.box_reserve" }.Contains(supply.TemplateId) || !Accepts(supply, item, Template(supply.TemplateId)) ||
                    item.PhysicalKind == ItemPhysicalKind.Token && supply.TemplateId != "core.zone.box_reserve" &&
                        !(types.IsAssignableTo(item.TypeId, ItemTypeRegistry.ContractTokenTypeId) && supply.TemplateId == "core.zone.reward_pool" && supply.LocalZoneId == "contracts") ||
                    supply.TemplateId == "core.zone.player_supply" && item.OwnerSubject.PlayerId != supply.OwnerPlayerId)) return Reject("OriginalSupplyZoneId_invalid", out reasonCode);
                if (types.IsAssignableTo(item.TypeId, ItemTypeRegistry.ContractTokenTypeId) &&
                    (!zones.TryGetValue(item.OriginalSupplyZoneId, out var contractPool) ||
                     contractPool.TemplateId != "core.zone.box_reserve" && (contractPool.TemplateId != "core.zone.reward_pool" || contractPool.LocalZoneId != "contracts") ||
                     zone.TemplateId == "core.zone.player_rewards" && (item.ControllerSubject.Kind != ItemSubjectKind.Player || item.ControllerSubject.PlayerId != zone.OwnerPlayerId)))
                    return Reject("contract_token_origin_or_holder_invalid", out reasonCode);
                var template = Template(zone.TemplateId);
                if (template.OrderingKind == ZoneOrderingKind.FixedSlots || template.OrderingKind == ZoneOrderingKind.Track)
                {
                    if (zone.TemplateId == "core.zone.score_track")
                    {
                        var player = state.FindPlayer(item.OwnerSubject.PlayerId);
                        if (player == null || item.SlotId != "score_" + player.Score.ToString(CultureInfo.InvariantCulture)) return Reject("SlotId_score_not_derived", out reasonCode);
                    }
                    else if (!zone.LegalSlotIds.Contains(item.SlotId) || !occupiedSlots.Add(item.ZoneId + ":" + item.SlotId)) return Reject("SlotId_rejected_or_occupied", out reasonCode);
                }
                else if (!string.IsNullOrEmpty(item.SlotId) && !zone.LegalSlotIds.Contains(item.SlotId)) return Reject("SlotId_rejected", out reasonCode);
                if (zone.TemplateId == "core.zone.influence_slots" && !types.HasOperationProfile(item.TypeId, "core.influence_cube")) return Reject("operation_profile_rejected", out reasonCode);
                if (zone.TemplateId == "core.zone.player_supply" && (item.OwnerSubject.Kind != ItemSubjectKind.Player || item.OwnerSubject.PlayerId != zone.OwnerPlayerId)) return Reject("OwnerSubject_supply_mismatch", out reasonCode);
                if (zone.TemplateId.StartsWith("core.zone.character_", StringComparison.Ordinal) && (item.ControllerSubject.Kind != ItemSubjectKind.Player || item.ControllerSubject.PlayerId != zone.OwnerPlayerId)) return Reject("ControllerSubject_zone_mismatch", out reasonCode);
                if (zone.TemplateId == "core.zone.board_marker_track" && zone.OwnerPlayerId > 0 && item.OwnerSubject.PlayerId != zone.OwnerPlayerId) return Reject("OwnerSubject_track_mismatch", out reasonCode);
            }
            foreach (var zone in zones.Values)
            {
                membersByZone.TryGetValue(zone.ZoneId, out var members);
                if (zone.TemplateId == "core.zone.player_rewards" && members != null && members.Count(item => types.IsAssignableTo(item.TypeId, ItemTypeRegistry.ContractTokenTypeId)) > 1) return Reject("contract_limit_reached", out reasonCode);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var current = zone;
                while (current.ParentKind != ZoneParentKind.Root)
                {
                    if (!visited.Add(current.ZoneId)) return Reject("zone_cycle", out reasonCode);
                    string parentId = current.ParentKind == ZoneParentKind.Zone ? current.ParentId : items[current.ParentId].ZoneId;
                    if (!zones.TryGetValue(parentId, out current)) return Reject("ParentId_unreachable", out reasonCode);
                }
                int capacity = zone.Configuration?.CapacityPolicy == "bounded" ? zone.Configuration.Capacity.Value : Template(zone.TemplateId).Capacity;
                if (capacity >= 0 && (members?.Count ?? 0) > capacity) return Reject("capacity_exceeded", out reasonCode);
                if (zone.TemplateId == "core.zone.effect_staging" && members?.Count > 0)
                {
                    var node = state.EffectRuntime.EffectNodes.Find(value => value.EffectId == zone.OwnerEffectId);
                    if (node.Status == EffectNodeStatus.Completed || node.Status == EffectNodeStatus.Failed || node.Status == EffectNodeStatus.Faulted) return Reject("staging_not_cleared", out reasonCode);
                }
            }
            var ordered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var order in runtime.Orders)
            {
                if (order == null || !zones.ContainsKey(order.ZoneId) || !ordered.Add(order.ZoneId) || order.ItemIds == null || order.OrderRevision < 0) return Reject("Orders_invalid", out reasonCode);
                if (Template(zones[order.ZoneId].TemplateId).OrderingKind == ZoneOrderingKind.Unordered) return Reject("Orders_unordered_zone", out reasonCode);
                membersByZone.TryGetValue(order.ZoneId, out var orderedMembers);
                var members = new HashSet<string>(orderedMembers == null ? Enumerable.Empty<string>() : orderedMembers.Select(item => item.ItemId), StringComparer.Ordinal);
                if (order.ItemIds.Count != members.Count || !members.SetEquals(order.ItemIds) || order.ItemIds.Distinct(StringComparer.Ordinal).Count() != order.ItemIds.Count) return Reject("Orders_membership_mismatch", out reasonCode);
            }
            foreach (var zone in zones.Values)
                if (Template(zone.TemplateId).OrderingKind != ZoneOrderingKind.Unordered && !ordered.Contains(zone.ZoneId)) return Reject("Orders_required", out reasonCode);
            foreach (var cityCell in runtime.Zones.Where(z => z.TemplateId == YC.Domain.Facilities.FacilityLayerQuery.CellTemplate))
            {
                try { YC.Domain.Facilities.FacilityLayerQuery.ResolveCell(state, cityCell.OwnerPlayerId, YC.Domain.Facilities.FacilityLayerQuery.Slot(cityCell)); }
                catch (InvalidOperationException) { return Reject("facility_layer_structure_invalid", out reasonCode); }
            }
            var facilityIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var facility in state.Map.FacilityInstances)
                if (facility == null || !facilityIds.Add(facility.ContentInstanceId) || !items.TryGetValue(facility.ContentInstanceId, out var facilityItem) ||
                    !types.IsAssignableTo(facilityItem.TypeId, "core.facility_card") || facility.FacilityCardId != facilityItem.DefinitionId)
                    return Reject("FacilityInstances_item_mismatch", out reasonCode);
                else if (facility.LegacyOwnerPlayerId != -1 || facility.LegacyCityBoardSlotIndex != -1) return Reject("legacy_position_write_rejected", out reasonCode);
            var relationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var relation in runtime.Relations)
                if (relation == null || string.IsNullOrWhiteSpace(relation.RelationId) || string.IsNullOrWhiteSpace(relation.RelationTypeId) || !relationIds.Add(relation.RelationId) ||
                    !items.ContainsKey(relation.SourceItemId) || !items.ContainsKey(relation.TargetItemId)) return Reject("Relations_invalid", out reasonCode);
            return true;
        }

        public bool Accepts(ZoneInstanceState zone, ItemInstanceState item)
        {
            return Accepts(zone, item, templates.FindRules(zone.TemplateId));
        }
        private bool Accepts(ZoneInstanceState zone, ItemInstanceState item, ZoneTemplateRules template)
        {
            if (template == null || !template.AcceptedItemKinds.Contains(item.PhysicalKind) ||
                (template.AcceptedTypeIds.Count > 0 && !template.AcceptedTypeIds.Any(id => types.IsAssignableTo(item.TypeId, id)))) return false;
            var config = zone.Configuration;
            return config == null || (config.AcceptedItemKinds.Contains(item.PhysicalKind) && (config.AcceptedTypeIds.Count == 0 || config.AcceptedTypeIds.Exists(id => types.IsAssignableTo(item.TypeId, id))));
        }

        public ItemZoneStateView Project(GameState state, GameStateViewer viewer)
        {
            var result = new ItemZoneStateView();
            if (state?.ItemZones == null) return result;
            // 可见性索引仅属于这次投影和这一个viewer；不绑定revision缓存，避免未提交副本的旧候选。
            var zones = new Dictionary<string, ZoneInstanceState>(StringComparer.Ordinal);
            var items = new Dictionary<string, ItemInstanceState>(StringComparer.Ordinal);
            var members = new Dictionary<string, List<ItemInstanceState>>(StringComparer.Ordinal);
            foreach (var zone in state.ItemZones.Zones) if (!zones.ContainsKey(zone.ZoneId)) zones.Add(zone.ZoneId, zone);
            foreach (var item in state.ItemZones.Items)
            {
                if (!items.ContainsKey(item.ItemId)) items.Add(item.ItemId, item);
                if (!members.TryGetValue(item.ZoneId, out var values)) members.Add(item.ZoneId, values = new List<ItemInstanceState>());
                values.Add(item);
            }
            var visibility = new Dictionary<string, bool>(StringComparer.Ordinal);
            var ownerBacks = new Dictionary<int, string>();
            foreach (var player in state.Players)
                if (player != null && Enum.IsDefined(typeof(PlayerColor), player.Color))
                    ownerBacks[player.PlayerId] = "character_back_" + ((int)player.Color).ToString(CultureInfo.InvariantCulture);
            var backKinds = new Dictionary<string, CharacterBackKind>(StringComparer.Ordinal);
            var definitionBacks = BuildCharacterDefinitionBacks(state);
            bool CanSeeParent(ZoneInstanceState zone, HashSet<string> path) =>
                zone.ParentKind == ZoneParentKind.Zone ? CanSeeZone(zone.ParentId, path) :
                zone.ParentKind != ZoneParentKind.Item || CanSeeItem(zone.ParentId, path);
            bool CanSeeZone(string id, HashSet<string> path)
            {
                if (id == null || !path.Add("zone:" + id)) return false;
                if (visibility.TryGetValue(id, out var cached)) return cached;
                bool visible = false;
                if (zones.TryGetValue(id, out var zone))
                {
                    bool permitted = true;
                    if (!viewer.IsHost)
                    {
                        permitted = zone.VisibilityPolicyId != ItemZoneVisibility.Hidden && zone.VisibilityPolicyId != ItemZoneVisibility.SecretRetired;
                        int controller = zone.OwnerPlayerId;
                        if (zone.ParentKind == ZoneParentKind.Item) controller = items.TryGetValue(zone.ParentId, out var owner) ? owner.ControllerSubject.PlayerId : -1;
                        if (zone.VisibilityPolicyId == ItemZoneVisibility.Owner && (viewer.Role != GameStateViewerRole.Player || viewer.PlayerId != controller)) permitted = false;
                    }
                    if (permitted) visible = CanSeeParent(zone, path);
                }
                visibility[id] = visible;
                return visible;
            }
            bool CanSeeItem(string id, HashSet<string> path)
            {
                if (id == null || !path.Add("item:" + id) || !items.TryGetValue(id, out var item)) return false;
                if (viewer.Role == GameStateViewerRole.Player && viewer.PlayerId > 0 && item.PhysicalKind == ItemPhysicalKind.Card &&
                    item.ControllerSubject.Kind == ItemSubjectKind.Player && item.ControllerSubject.PlayerId == viewer.PlayerId &&
                    zones.TryGetValue(item.ZoneId, out var zone) && zone.TemplateId == "core.zone.box_secret_retired" &&
                    zone.VisibilityPolicyId == ItemZoneVisibility.SecretRetired)
                    return path.Add("zone:" + zone.ZoneId) && CanSeeParent(zone, path);
                return CanSeeZone(item.ZoneId, path);
            }
            var visibleZones = new HashSet<string>(StringComparer.Ordinal);
            var visibleItems = new HashSet<string>(StringComparer.Ordinal);
            foreach (var zone in state.ItemZones.Zones)
            {
                // 卡背不能通过附属区的 ParentId 暴露隐藏载体的身份。
                if (zone.ParentKind == ZoneParentKind.Item && !CanSeeItem(zone.ParentId, new HashSet<string>(StringComparer.Ordinal))) continue;
                bool identities = CanSeeZone(zone.ZoneId, new HashSet<string>(StringComparer.Ordinal));
                var copy = GameStateCloneService.DeepClone(zone);
                copy.Configuration = null;
                copy.OwnerEffectId = string.Empty;
                members.TryGetValue(zone.ZoneId, out var contents);
                var projected = new ZoneStateView { Zone = copy, ItemCount = contents?.Count ?? 0, IdentitiesVisible = identities };
                result.Zones.Add(projected);
                bool showCardBacks = zone.TemplateId == "core.zone.character_hand" || zone.TemplateId == "core.zone.character_covered";
                var backCounts = showCardBacks ? new Dictionary<string, int>(StringComparer.Ordinal) : null;
                visibleZones.Add(zone.ZoneId);
                foreach (var item in contents ?? Enumerable.Empty<ItemInstanceState>())
                {
                    // 背面数量不携带实例映射，也不沿隐藏区域顺序输出。
                    if (showCardBacks)
                    {
                        string key = ResolveCharacterBackVisualKey(item, ownerBacks, backKinds, definitionBacks);
                        // 未解析背面以空 key 聚合数量；显示缺图不能中断授权视图。
                        backCounts.TryGetValue(key, out int count);
                        backCounts[key] = count + 1;
                    }
                    // 共享秘密区仍不整体公开，仅透出本人持有的真实牌；区域总数和整区顺序不变。
                    if (!identities && !CanSeeItem(item.ItemId, new HashSet<string>(StringComparer.Ordinal))) continue;
                    result.Items.Add(GameStateCloneService.DeepClone(item));
                    visibleItems.Add(item.ItemId);
                }
                if (showCardBacks)
                    foreach (var group in backCounts.OrderBy(value => value.Key, StringComparer.Ordinal))
                        projected.CardBackGroups.Add(new CardBackGroupView { BackVisualKey = group.Key, Count = group.Value });
            }
            foreach (var order in state.ItemZones.Orders)
                if (visibleZones.Contains(order.ZoneId) && order.ItemIds.All(visibleItems.Contains) && CanSeeZone(order.ZoneId, new HashSet<string>(StringComparer.Ordinal))) result.Orders.Add(GameStateCloneService.DeepClone(order));
            foreach (var relation in state.ItemZones.Relations)
                if (visibleItems.Contains(relation.SourceItemId) && visibleItems.Contains(relation.TargetItemId)) result.Relations.Add(GameStateCloneService.DeepClone(relation));
            return result;
        }
        private string ResolveCharacterBackVisualKey(ItemInstanceState item, IDictionary<int, string> ownerBacks,
            IDictionary<string, CharacterBackKind> backKinds, IDictionary<string, string> definitionBacks)
        {
            if (!backKinds.TryGetValue(item.TypeId, out var kind))
            {
                kind = types.IsAssignableTo(item.TypeId, "core.operator_card") ? CharacterBackKind.Operator :
                    types.IsAssignableTo(item.TypeId, "core.basic_character_card") ? CharacterBackKind.Basic :
                    types.IsAssignableTo(item.TypeId, "core.generated_character_card") ? CharacterBackKind.Generated : CharacterBackKind.Unknown;
                backKinds[item.TypeId] = kind;
            }
            if (kind == CharacterBackKind.Operator) return "operator_back";
            if (kind == CharacterBackKind.Generated)
            {
                string definedBack = ResolveGeneratedDefinitionBack(item.DefinitionId, definitionBacks);
                if (!string.IsNullOrEmpty(definedBack)) return definedBack;
                var configured = item.LocalState?.Properties?.Find(entry => entry.Name == "backVisualKey")?.Value;
                if (configured?.Kind == NormalizedValueKind.String && IsPublicCharacterBack(configured.StringValue)) return configured.StringValue;
                var color = item.LocalState?.Properties?.Find(entry => entry.Name == "backColor")?.Value;
                if (color?.Kind == NormalizedValueKind.Integer && color.IntegerValue >= 0 && color.IntegerValue <= 3)
                    return "character_back_" + color.IntegerValue.ToString(CultureInfo.InvariantCulture);
            }
            if ((kind == CharacterBackKind.Basic || kind == CharacterBackKind.Generated) &&
                item.OwnerSubject.Kind == ItemSubjectKind.Player && ownerBacks.TryGetValue(item.OwnerSubject.PlayerId, out string original))
                return original;
            return string.Empty;
        }
        internal static bool IsPublicCharacterBack(string key) => key == "operator_back" || key == "flowing_shape_back" || key == "character_back_0" ||
            key == "character_back_1" || key == "character_back_2" || key == "character_back_3";
        internal static Dictionary<string, string> BuildCharacterDefinitionBacks(GameState state)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (state.ContentDefinitions?.Characters != null)
                foreach (var definition in state.ContentDefinitions.Characters)
                    if (definition != null && !string.IsNullOrEmpty(definition.TemplateId) && IsPublicCharacterBack(definition.BackVisualKey))
                        result[definition.TemplateId] = definition.BackVisualKey;
            return result;
        }
        internal static string ResolveGeneratedDefinitionBack(string definitionId, IDictionary<string, string> definitionBacks)
        {
            if (definitionId != null && definitionBacks.TryGetValue(definitionId, out string configured)) return configured;
            // 仅补旧存档中这三张已确定实体背面的流形；不把未来生成角色当作流形。
            return definitionId == "infected.character.flowing_shape_alpha" || definitionId == "infected.character.flowing_shape_beta" ||
                definitionId == "infected.character.flowing_shape_gamma" ? "flowing_shape_back" : string.Empty;
        }
        private bool ValidateHostConfiguration(ZoneTemplateRules template, CardZoneDefinition config, Func<string, ItemPhysicalKind?> findType, out string reason)
        {
            reason = string.Empty;
            if (config.AcceptedItemKinds == null || config.AcceptedItemKinds.Count == 0 || config.AcceptedTypeIds == null || config.OrderingKind != template.OrderingKind ||
                config.AcceptedItemKinds.Any(kind => !template.AcceptedItemKinds.Contains(kind)) || config.AcceptedTypeIds.Any(id => findType(id) == null ||
                    !config.AcceptedItemKinds.Contains(findType(id).Value) || (template.AcceptedTypeIds.Count > 0 && !template.AcceptedTypeIds.Any(parent => types.IsAssignableTo(id, parent)))) ||
                (template.AcceptedTypeIds.Count > 0 && config.AcceptedTypeIds.Count == 0)) return Reject("Configuration_acceptedTypeIds_rejected", out reason);
            if (config.CapacityPolicy != "bounded" && config.CapacityPolicy != "unbounded" ||
                config.CapacityPolicy == "bounded" && (!config.Capacity.HasValue || config.Capacity < 0) || config.CapacityPolicy == "unbounded" && config.Capacity.HasValue ||
                template.Capacity >= 0 && (config.CapacityPolicy == "unbounded" || config.Capacity > template.Capacity)) return Reject("Configuration_capacity_rejected", out reason);
            if (!new[] { "core.explicit_transfer", "core.owned_player_marker", "core.removed_owned_influence" }.Contains(config.EntryPolicyId)) return Reject("entryPolicyId_rejected", out reason);
            if (!new[] { "core.explicit_transfer", "core.card_payment" }.Contains(config.ExitPolicyId)) return Reject("exitPolicyId_rejected", out reason);
            if (!new[] { "core.preserve_contents", "core.return_contents_before_exit" }.Contains(config.CarrierExitPolicyId)) return Reject("carrierExitPolicyId_rejected", out reason);
            return ZoneTemplateRegistry.ValidateCarrierPolicies(config, out reason);
        }
        private static bool ValidSubject(ItemSubjectRef value) => value != null && (value.Kind == ItemSubjectKind.None && value.PlayerId == -1 && string.IsNullOrEmpty(value.CompanyId) || value.Kind == ItemSubjectKind.Player && value.PlayerId > 0 && string.IsNullOrEmpty(value.CompanyId) || value.Kind == ItemSubjectKind.Company && value.PlayerId == -1 && !string.IsNullOrWhiteSpace(value.CompanyId));
        private static bool IsRoot(string id) => id == "core.zone.map" || id == "core.zone.table" || id == "core.zone.players" || id == "core.zone.game_box";
        private static bool Reject(string code, out string reason) { reason = code; return false; }
    }
}
