using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using YC.Domain.State;

namespace YC.Domain.Items
{
    public enum ItemPhysicalKind { Cube, Card, MobileCity, Token }
    public enum ItemSubjectKind { None, Player, Company }
    public enum ZoneParentKind { Root, Zone, Item }
    public enum ZoneOrderingKind { Unordered, Deck, FixedSlots, Track }

    [Serializable]
    public sealed class ItemZoneRuntimeState
    {
        public int NamingVersion = PlayerAreaPolicy.NamingVersion;
        public int FacilityLayerVersion = 1;
        public string FacilityLayerMigrationId = "";
        public List<ItemInstanceState> Items = new List<ItemInstanceState>();
        public List<ZoneInstanceState> Zones = new List<ZoneInstanceState>();
        public List<ZoneOrderState> Orders = new List<ZoneOrderState>();
        public List<ItemRelationState> Relations = new List<ItemRelationState>();
    }

    [Serializable]
    public sealed class ItemSubjectRef
    {
        public ItemSubjectKind Kind;
        public int PlayerId = -1;
        public string CompanyId = string.Empty;
        public static ItemSubjectRef Player(int playerId) => new ItemSubjectRef { Kind = ItemSubjectKind.Player, PlayerId = playerId };
        public static ItemSubjectRef Company(string companyId) => new ItemSubjectRef { Kind = ItemSubjectKind.Company, CompanyId = companyId };
    }

    [Serializable]
    public sealed class ItemInstanceState
    {
        public string ItemId = string.Empty;
        public string DefinitionId = string.Empty;
        public string TypeId = string.Empty;
        public string OriginalSupplyZoneId = string.Empty;
        public string ZoneId = string.Empty;
        public string SlotId = string.Empty;
        public ItemPhysicalKind PhysicalKind;
        public ItemSubjectRef OwnerSubject = new ItemSubjectRef();
        public ItemSubjectRef ControllerSubject = new ItemSubjectRef();
        public NormalizedValue LocalState = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
    }

    [Serializable]
    public sealed class ZoneInstanceState
    {
        public string ZoneId = string.Empty;
        public string TemplateId = string.Empty;
        public string LocalZoneId = string.Empty;
        public string ParentId = string.Empty;
        public ZoneParentKind ParentKind;
        public int OwnerPlayerId = -1;
        public string VisibilityPolicyId = string.Empty;
        public string OwnerEffectId = string.Empty;
        // Host 保存已复验的收窄配置。结构许可始终来自注册表，不来自本字段。
        public CardZoneDefinition Configuration;
        public List<string> LegalSlotIds = new List<string>();
    }

    [Serializable]
    public sealed class ZoneOrderState
    {
        public string ZoneId = string.Empty;
        public List<string> ItemIds = new List<string>();
        public int OrderRevision;
    }

    [Serializable]
    public sealed class ItemRelationState
    {
        public string RelationId = string.Empty;
        public string RelationTypeId = string.Empty;
        public string SourceItemId = string.Empty;
        public string TargetItemId = string.Empty;
    }

    [Serializable] public sealed class ZoneRef { public string ZoneId = string.Empty; }
    [Serializable] public sealed class ItemRef { public string ItemId = string.Empty; }
    [Serializable] public sealed class ItemPositionRef { public string ZoneId = string.Empty; public string SlotId = string.Empty; }

    // 投影只含可见实例与区域数量。隐藏身份不会以占位 ItemId 或载体引用泄露。
    [Serializable]
    public sealed class ItemZoneStateView
    {
        public List<ItemInstanceState> Items = new List<ItemInstanceState>();
        public List<ZoneStateView> Zones = new List<ZoneStateView>();
        public List<ZoneOrderState> Orders = new List<ZoneOrderState>();
        public List<ItemRelationState> Relations = new List<ItemRelationState>();
    }

    [Serializable]
    public sealed class CardBackGroupView
    {
        public string BackVisualKey = string.Empty;
        public int Count;
    }

    [Serializable]
    public sealed class ZoneStateView
    {
        public ZoneInstanceState Zone = new ZoneInstanceState();
        public int ItemCount;
        public bool IdentitiesVisible;
        public List<CardBackGroupView> CardBackGroups = new List<CardBackGroupView>();
    }

    /// <summary>旧调用的过渡适配器。实体模式只读；任何旧写入口明确失败。</summary>
    [Serializable]
    public sealed class LegacyPositionList<T> : IList<T>, IReadOnlyList<T>
    {
        public List<T> Values = new List<T>();
        [NonSerialized] private Func<List<T>> projection;
        private List<T> Current => projection == null ? Values : projection();
        public bool IsReadOnly => projection != null;
        public int Count => Current.Count;
        public T this[int index] { get => Current[index]; set { Write(); Values[index] = value; } }
        public void Bind(Func<List<T>> query)
        {
            if (Values.Count > 0) throw new InvalidOperationException("legacy_position_write_rejected: Values");
            projection = query ?? throw new ArgumentNullException(nameof(query));
        }
        public void Add(T value) { Write(); Values.Add(value); }
        public void AddRange(IEnumerable<T> values) { Write(); Values.AddRange(values); }
        public void Clear() { Write(); Values.Clear(); }
        public bool Contains(T value) => Current.Contains(value);
        public int IndexOf(T value) => Current.IndexOf(value);
        public void CopyTo(T[] array, int index) => Current.CopyTo(array, index);
        public void Insert(int index, T value) { Write(); Values.Insert(index, value); }
        public bool Remove(T value) { Write(); return Values.Remove(value); }
        public void RemoveAt(int index) { Write(); Values.RemoveAt(index); }
        public int RemoveAll(Predicate<T> predicate) { Write(); return Values.RemoveAll(predicate); }
        public void RemoveRange(int index, int count) { Write(); Values.RemoveRange(index, count); }
        public void Reverse() { Write(); Values.Reverse(); }
        public void Sort(Comparison<T> comparison) { Write(); Values.Sort(comparison); }
        public T Find(Predicate<T> predicate) => Current.Find(predicate);
        public List<T> FindAll(Predicate<T> predicate) => Current.FindAll(predicate);
        public int FindIndex(Predicate<T> predicate) => Current.FindIndex(predicate);
        public bool Exists(Predicate<T> predicate) => Current.Exists(predicate);
        public T[] ToArray() => Current.ToArray();
        public List<T> GetRange(int index, int count) => Current.GetRange(index, count);
        public List<TResult> ConvertAll<TResult>(Converter<T, TResult> converter) => Current.ConvertAll(converter);
        public IEnumerator<T> GetEnumerator() => Current.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public static implicit operator LegacyPositionList<T>(List<T> values) => new LegacyPositionList<T> { Values = values ?? new List<T>() };
        private void Write() { if (IsReadOnly) throw new InvalidOperationException("legacy_position_write_rejected: ItemZones"); }
    }

    public static class ItemZoneLegacyProjection
    {
        public static bool IsActive(GameState state) => state?.EntitySchemaVersion == GameProtocolVersions.Entity && state.ItemZones?.Zones.Count > 0;
        public static void Bind(GameState state, bool clearLegacy = false)
        {
            if (!IsActive(state)) return;
            if (clearLegacy)
            {
                foreach (var player in state.Players) { player.HandCardIds.Values.Clear(); player.DiscardCardIds.Values.Clear(); player.BuiltFacilityIds.Values.Clear(); }
                foreach (var list in new[] { state.Decks.CharacterDeck, state.Decks.CharacterDiscard, state.Decks.FacilityDeck, state.Decks.FacilitySupply, state.Decks.CityStyleSupply, state.Decks.EventDeckGreen, state.Decks.EventDeckYellow, state.Decks.EventDeckRed, state.Map.RoadRouteIds, state.Map.RemovedFromGameCardIds }) list.Values.Clear();
                foreach (var pool in state.Decks.CardPools) pool.RemainingCardIds.Values.Clear();
                state.Map.Influences.Values.Clear(); state.Map.Facilities.Values.Clear(); state.Map.ResourceTokens.Values.Clear();
                state.GameBox.Cards.Clear(); state.GameBox.Markers.Clear(); state.GameBox.Tokens.Clear(); state.GameBox.Enterprises.Clear();
            }
            foreach (var player in state.Players)
            {
                player.HandCardIds.Bind(() => Cards(state, "hand", player.PlayerId));
                player.DiscardCardIds.Bind(() => Cards(state, "discard", player.PlayerId));
                player.BuiltFacilityIds.Bind(() => state.ItemZones.Items.Where(item => Zone(state, item)?.TemplateId == "core.zone.city_facility_cell" && Zone(state, item).OwnerPlayerId == player.PlayerId).Select(item => item.DefinitionId).ToList());
                player.BindItemZones(state, clearLegacy);
                foreach (var declaration in player.DeclaredCityStyles) declaration.BindItemZones(state);
            }
            foreach (var facility in state.Map.FacilityInstances) facility.BindItemZones(state, clearLegacy);
            state.Decks.CharacterDeck.Bind(() => Cards(state, "character_deck", -1, true));
            state.Decks.CharacterDiscard.Bind(() => Cards(state, "character_discard", -1));
            state.Decks.FacilityDeck.Bind(() => Cards(state, "facility_deck", -1, true));
state.Decks.FacilitySupply.Bind(() => Enumerable.Range(0, 6).Select(index => Cards(state, "facility_supply_" + index, -1, true).FirstOrDefault() ?? string.Empty).ToList());
            state.Decks.CityStyleSupply.Bind(() => Cards(state, "city_styles", -1));
            state.Decks.EventDeckGreen.Bind(() => Cards(state, "event_green", -1, true));
            state.Decks.EventDeckYellow.Bind(() => Cards(state, "event_yellow", -1, true));
            state.Decks.EventDeckRed.Bind(() => Cards(state, "event_red", -1, true));
            foreach (var pool in state.Decks.CardPools) pool.RemainingCardIds.Bind(() => Cards(state, pool.PoolId, -1, true));
            state.Map.RoadRouteIds.Bind(() => state.ItemZones.Items.Where(item => Zone(state, item)?.TemplateId == "core.zone.route_road").Select(item => Parent(state, Zone(state, item)).LocalZoneId).ToList());
            state.Map.Influences.Bind(() => state.ItemZones.Items.Where(item => Zone(state, item)?.TemplateId == "core.zone.influence_slots").Select(item =>
            {
                var parent = Parent(state, Zone(state, item));
                return new InfluencePlacement { InfluenceId = item.ItemId, PlayerId = item.OwnerSubject.PlayerId, SlotId = item.SlotId,
                    LocationId = parent.TemplateId == "core.zone.map_location" ? parent.LocalZoneId : string.Empty,
                    RouteId = parent.TemplateId == "core.zone.map_route" ? parent.LocalZoneId : string.Empty,
                    OwnerSubject = new RuleSubjectReference { SubjectType = item.OwnerSubject.Kind == ItemSubjectKind.Company ? "company" : "player", PlayerId = item.OwnerSubject.PlayerId, InstanceId = item.OwnerSubject.Kind == ItemSubjectKind.Company ? item.OwnerSubject.CompanyId : item.OwnerSubject.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
            }).ToList());
            state.Map.Facilities.Bind(() => state.ItemZones.Items.Where(item => Zone(state, item)?.TemplateId == "core.zone.city_facility_cell").Select(item => new FacilityPlacement
            {
                ContentInstanceId = item.ItemId, FacilityCardId = item.DefinitionId, PlayerId = Zone(state, item).OwnerPlayerId,
                CityBoardSlotIndex = int.Parse(Zone(state, item).LocalZoneId.Substring("city_cell_".Length), System.Globalization.CultureInfo.InvariantCulture)
            }).ToList());
            state.Map.ResourceTokens.Bind(() => state.ItemZones.Items.Where(item => item.TypeId == "core.resource_point_token").Select(item => new ResourceTokenState
            { LocationId = Parent(state, Zone(state, item)).LocalZoneId, ResourceType = (YC.Domain.Rules.ResourceType)GetInteger(item, "resourceType"), Amount = (int)GetInteger(item, "amount") }).ToList());
            state.Map.RemovedFromGameCardIds.Bind(() => Cards(state, "retired", -1));
        }
        private static long GetInteger(ItemInstanceState item, string name) => item.LocalState.Properties.Find(value => value.Name == name)?.Value.IntegerValue ?? 0;
        internal static ZoneInstanceState Zone(GameState state, ItemInstanceState item) => state.ItemZones.Zones.Find(zone => zone.ZoneId == item.ZoneId);
        internal static ZoneInstanceState Parent(GameState state, ZoneInstanceState zone) => state.ItemZones.Zones.Find(parent => parent.ZoneId == zone.ParentId);
        internal static List<string> Cards(GameState state, string local, int playerId, bool oldDeckDirection = false)
        {
            var zone = state.ItemZones.Zones.Find(value => value.LocalZoneId == local && (playerId < 0 || value.OwnerPlayerId == playerId));
            if (zone == null) return new List<string>();
            var ids = new ItemZoneQuery().GetItemIds(state, zone.ZoneId);
            var result = ids.Select(id => zone.TemplateId.StartsWith("core.zone.character_", StringComparison.Ordinal) ? id : state.ItemZones.Items.Find(item => item.ItemId == id).DefinitionId).ToList();
            if (oldDeckDirection) result.Reverse();
            return result;
        }
        public static void RejectLegacyWrite(GameState state)
        {
            if (IsActive(state)) throw new InvalidOperationException("legacy_position_write_rejected: ItemZones");
        }
    }
}
