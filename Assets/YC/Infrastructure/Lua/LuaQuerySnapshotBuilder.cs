using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    // 只在当前Dispatcher调用内物化；不保留GameState或跨revision缓存。
    internal static partial class LuaQuerySnapshotBuilder
    {
        internal static void Stamp(GameState state, LuaFacadeSnapshot snapshot, int revision)
        {
            snapshot.StateRevision = revision;
            var enabled = state.RuleConfiguration?.EnabledExpansionIds;
            snapshot.EnabledDlcIdsAvailable = enabled != null;
            if (enabled != null) snapshot.EnabledDlcIds.AddRange(enabled);
        }

        internal static LuaSnapshotValue Map(GameState state, int? revision = null)
        {
            GameMapDefinition map;
            try { map = StaticMapDefinitions.Resolve(state.MapId); }
            catch (ArgumentException) { return LuaSnapshotValue.Null; }
            var query = new MapQueryService(map);
            var tokens = state.Map.ResourceTokens.ToArray().Where(t => t != null).ToDictionary(t => t.LocationId, StringComparer.Ordinal);
            var roads = new HashSet<string>(state.Map.RoadRouteIds.ToArray(), StringComparer.Ordinal);
            var open = new HashSet<string>(state.Map.OpenLocationIds.ToArray(), StringComparer.Ordinal);
            var influences = state.Map.Influences.ToArray().Where(i => i != null).OrderBy(i => i.InfluenceId, StringComparer.Ordinal).ToArray();
            var influenceValues = new List<LuaSnapshotValue>();
            foreach (var value in influences)
            {
                InfluenceSlotReference.TryParse(query, value.SlotId, out var slot, out _);
                influenceValues.Add(O(E("influenceId", S(InfluenceIdentity.GetStableId(state, value))), E("ownerPlayerId", S(Player(value.PlayerId))),
                    E("owner", S(value.OwnerSubject?.SubjectType == "company" ? "company:" +
                        (string.IsNullOrEmpty(value.OwnerSubject.DefinitionId) ? value.OwnerSubject.InstanceId : value.OwnerSubject.DefinitionId) : Player(value.PlayerId))),
                    E("slotId", S(value.SlotId)), E("locationId", S(slot?.LocationId ?? "")), E("routeId", S(slot?.RouteId ?? ""))));
            }
            var locations = new List<LuaSnapshotValue>(); var resources = new List<LuaSnapshotValue>();
            var slots = new List<LuaSnapshotValue>(); var connected = new List<LuaSnapshotEntry>(); var cities = new List<LuaSnapshotEntry>();
            foreach (var location in map.Locations.OrderBy(l => l.LocationId, StringComparer.Ordinal))
            {
                var ids = Enumerable.Range(0, location.InfluenceSlotCount).Select(i => InfluenceService.GetLocationSlotId(location.LocationId, i)).ToArray();
                locations.Add(O(E("locationId", S(location.LocationId)), E("regionId", S(location.RegionId)), E("isOpen", B(open.Contains(location.LocationId))),
                    E("isRedZone", B(location.IsRedZone)), E("hasResourcePointIndicator", B(tokens.ContainsKey(location.LocationId))),
                    E("resourceType", S(location.ResourceType.ToString())), E("eventColor", S(location.EventColor.ToString())),
                    E("canDockCity", B(location.CanDockCity)), E("influenceSlotIds", Strings(ids))));
                if (tokens.TryGetValue(location.LocationId, out var token))
                    resources.Add(O(E("locationId", S(location.LocationId)), E("resourceType", S(token.ResourceType.ToString())), E("amount", N(token.Amount))));
                AddSlots(slots, ids, location.LocationId, "", influences, state);
                connected.Add(E(location.LocationId, A(query.GetAdjacentLocations(location.LocationId).OrderBy(l => l.LocationId, StringComparer.Ordinal)
                    .Select(l => O(E("locationId", S(l.LocationId)), E("id", S(l.LocationId)))))));
                cities.Add(E(location.LocationId, A(state.Players.Where(p => p.CityLocationId == location.LocationId).OrderBy(p => p.PlayerId)
                    .Select(p => O(E("playerId", S(Player(p.PlayerId))))))));
            }
            var routeValues = new List<LuaSnapshotValue>(); var between = new List<LuaSnapshotEntry>();
            var routeLocations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var route in map.Routes.OrderBy(r => r.RouteId, StringComparer.Ordinal))
            {
                var covered = (route.CoveredLocationIds ?? new List<string>()).Concat(new[] { route.FromLocationId, route.ToLocationId })
                    .Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
                routeLocations.Add(route.RouteId, new HashSet<string>(covered, StringComparer.Ordinal));
                var ids = Enumerable.Range(0, route.InfluenceSlotCount).Select(i => InfluenceService.GetRouteSlotId(route.RouteId, i)).ToArray();
                routeValues.Add(O(E("id", S(route.RouteId)), E("routeId", S(route.RouteId)), E("regionId", S(route.RegionId)),
                    E("fromLocationId", S(route.FromLocationId)), E("toLocationId", S(route.ToLocationId)), E("coveredLocationIds", Strings(covered)),
                    E("baseCost", N(route.BaseCost)), E("hasRoad", B(roads.Contains(route.RouteId))), E("influenceSlotIds", Strings(ids))));
                AddSlots(slots, ids, "", route.RouteId, influences, state);
            }
            foreach (var from in map.Locations.OrderBy(l => l.LocationId, StringComparer.Ordinal))
                between.Add(E(from.LocationId, O(map.Locations.Where(to => from.LocationId != to.LocationId &&
                    routeLocations.Values.Any(ids => ids.Contains(from.LocationId) && ids.Contains(to.LocationId)))
                    .OrderBy(l => l.LocationId, StringComparer.Ordinal).Select(to =>
                    E(to.LocationId, A(
                        routeValues.Where(r => routeLocations[r.Get("routeId").StringValue].Contains(from.LocationId) &&
                            routeLocations[r.Get("routeId").StringValue].Contains(to.LocationId))
                        .Select(r => O(E("id", r.Get("id")), E("routeId", r.Get("routeId"))))))).ToArray())));
            return O(E("mapId", S(map.MapId)), E("stateRevision", N(revision ?? state.EffectRuntime?.StateRevision ?? 0)), E("locations", A(locations)),
                E("routes", A(routeValues)), E("regions", A(map.Regions.OrderBy(r => r.RegionId, StringComparer.Ordinal).Select(r => O(
                    E("regionId", S(r.RegionId)), E("locationIds", Strings(r.LocationIds)), E("routeIds", Strings(r.RouteIds)), E("scoreValue", N(r.ScoreValue)))))),
                E("influenceSlots", A(slots)), E("influences", A(influenceValues)), E("resourcePoints", A(resources)), E("connectedLocations", O(connected.ToArray())),
                E("routesBetween", O(between.ToArray())), E("citiesAt", O(cities.ToArray())), E("roads", O(map.Routes.OrderBy(r => r.RouteId, StringComparer.Ordinal)
                    .Select(r => E(r.RouteId, B(roads.Contains(r.RouteId)))).ToArray())), E("openLocationIds", Strings(open.OrderBy(id => id, StringComparer.Ordinal))),
                E("redZoneLocationIds", Strings(map.Locations.Where(l => l.IsRedZone).Select(l => l.LocationId).OrderBy(id => id, StringComparer.Ordinal))));
        }

        private static void AddSlots(List<LuaSnapshotValue> result, IEnumerable<string> ids, string location, string route,
            InfluencePlacement[] influences, GameState state)
        {
            foreach (var id in ids)
            {
                var occupant = influences.FirstOrDefault(i => i.SlotId == id);
                result.Add(O(E("slotId", S(id)), E("locationId", S(location)), E("routeId", S(route)), E("isOccupied", B(occupant != null)),
                    E("influenceId", occupant == null ? LuaSnapshotValue.Null : S(InfluenceIdentity.GetStableId(state, occupant)))));
            }
        }

        internal static LuaSnapshotValue Definitions(GameState state)
        {
            var defs = state.ContentDefinitions;
            if (defs == null) return LuaSnapshotValue.Null;
            var values = new List<LuaSnapshotValue>();
            foreach (var d in defs.Characters) values.Add(Definition(d.CardId, "character", d.Name, "", null));
            foreach (var d in defs.Facilities) values.Add(O(E("id", S(d.FacilityId)), E("definitionId", S(d.FacilityId)), E("contentType", S("facility")), E("name", S(d.Name)), E("description", S(d.Description)), E("goldVoucherCost", N(d.GoldVoucherCost)), E("tags", Strings(d.Keywords))));
            foreach (var d in defs.Events) values.Add(Definition(d.CardId, "event", d.Name, d.Description, null));
            foreach (var d in defs.CityStyles) values.Add(O(E("id", S(d.CityStyleId)), E("definitionId", S(d.CityStyleId)), E("contentType", S("city_style")), E("name", S(d.Name)), E("description", S(d.Description)), E("level", N(d.Level)), E("specialActionId", S(d.SpecialActionId)), E("tags", Strings(null))));
            foreach (var d in defs.SpecialActions) values.Add(Definition(d.SpecialActionId, "special_action", d.Name, d.Description, null));
            foreach (var d in defs.Components) values.Add(Definition(d.DefinitionId, "component", "", "", null));
            foreach (var d in defs.Awards) values.Add(O(E("id", S(d.DefinitionId)), E("definitionId", S(d.DefinitionId)), E("contentType", S("award")),
                E("titleKey", S(d.TitleKey)), E("descriptionKey", S(d.DescriptionKey)), E("lifecycle", S(d.Lifecycle)), E("tags", Strings(null))));
            foreach (var id in defs.EnterpriseDefinitionIds) values.Add(Definition(id, "enterprise", "", "", null));
            if (values.GroupBy(v => v.Get("id").StringValue, StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw new InvalidOperationException("lua_query_definition_id_duplicate");
            return A(values.OrderBy(v => v.Get("id").StringValue, StringComparer.Ordinal));
        }

        internal static LuaSnapshotValue Content(GameState state, ItemZoneStateView visible, List<LuaSnapshotValue> instances)
        {
            var definitions = Definitions(state);
            var byDefinition = definitions.Items.ToDictionary(d => d.Get("id").StringValue, StringComparer.Ordinal);
            var byItem = visible.Items.ToDictionary(i => i.ItemId, StringComparer.Ordinal);
            var enriched = new List<LuaSnapshotValue>();
            foreach (var value in instances)
            {
                string id = value.Get("id").StringValue;
                byItem.TryGetValue(id, out var item);
                if (ItemZoneLegacyProjection.IsActive(state) && item == null) continue;
                var fields = value.Properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                if (item != null)
                {
                    fields["zoneId"] = S(item.ZoneId); fields["typeId"] = S(item.TypeId); fields["slotId"] = S(item.SlotId);
                    // owner保留既有实例投影的玩家控制者语义；不能因补查询悄悄改写旧Lua的筛选条件。
                    if (!fields.ContainsKey("owner")) fields["owner"] = S(Player(item.ControllerSubject.PlayerId));
                    fields["ownerSubject"] = S(Subject(item.OwnerSubject)); fields["controller"] = S(Subject(item.ControllerSubject));
                    AddInstanceContinuationFacts(state, item.ItemId, fields, new HashSet<string>(byItem.Keys, StringComparer.Ordinal));
                }
                if (byDefinition.TryGetValue(value.Get("definitionId").StringValue, out var definition))
                { fields["contentType"] = definition.Get("contentType"); fields["tags"] = definition.Get("tags"); }
                enriched.Add(O(fields.Select(p => E(p.Key, p.Value)).ToArray()));
            }
            var zones = visible.Zones.Where(z => z.IdentitiesVisible || z.Zone.TemplateId == "core.zone.box_retired" || z.Zone.LocalZoneId == "facility_deck")
                .Select(z => O(E("id", S(z.Zone.ZoneId)), E("templateId", S(z.Zone.TemplateId)), E("localZoneId", S(z.Zone.LocalZoneId)),
                    E("parentId", S(z.Zone.ParentId)), E("parentKind", S(z.Zone.ParentKind.ToString())), E("owner", S(Player(z.Zone.OwnerPlayerId))), E("itemCount", N(z.ItemCount))));
            return O(E("instances", A(enriched)), E("zones", A(zones)), E("definitions", definitions),
                E("stateRevision", N(state.EffectRuntime?.StateRevision ?? 0)), E("contentPackHash", S(state.ContentPackHash)));
        }

        internal static LuaSnapshotValue CandidateContent(GameState state, CandidateSetDraft draft, int revision)
        {
            // 从本地当前状态物化本次来源池；这些只读查询资料不进入持久化Context。
            var definitions = Definitions(state);
            var byDefinition = definitions.Items.ToDictionary(d => d.Get("id").StringValue, StringComparer.Ordinal);
            var items = state.ItemZones.Items.ToDictionary(i => i.ItemId, StringComparer.Ordinal);
            var zones = state.ItemZones.Zones.ToDictionary(z => z.ZoneId, StringComparer.Ordinal);
            var sourceItems = new List<ItemInstanceState>();
            foreach (string id in draft.CandidatePoolIds)
            {
                if (!items.TryGetValue(id, out var item)) throw new InvalidOperationException("lua_query_candidate_instance_missing");
                sourceItems.Add(item);
            }
            var sourceZones = sourceItems.Select(item => zones.TryGetValue(item.ZoneId, out var zone) ? zone :
                throw new InvalidOperationException("lua_query_candidate_zone_missing")).Distinct().ToList();
            foreach (var zone in sourceZones)
                if (zone.ParentKind == ZoneParentKind.Item && items.TryGetValue(zone.ParentId, out var carrier)) sourceItems.Add(carrier);
            var values = new List<LuaSnapshotValue>();
            foreach (var item in sourceItems.Distinct())
            {
                var fields = new Dictionary<string, LuaSnapshotValue>(StringComparer.Ordinal) {
                    ["id"] = S(item.ItemId), ["definitionId"] = S(item.DefinitionId), ["zoneId"] = S(item.ZoneId),
                    ["typeId"] = S(item.TypeId), ["owner"] = S(Player(item.ControllerSubject.PlayerId)),
                    ["controller"] = S(Subject(item.ControllerSubject)), ["ownerSubject"] = S(Subject(item.OwnerSubject)), ["slotId"] = S(item.SlotId) };
                if (byDefinition.TryGetValue(item.DefinitionId, out var definition))
                { fields["contentType"] = definition.Get("contentType"); fields["tags"] = definition.Get("tags"); }
                values.Add(O(fields.Select(p => E(p.Key, p.Value)).ToArray()));
            }
            var zoneValues = new List<LuaSnapshotValue>();
            foreach (var zone in sourceZones)
            {
                zoneValues.Add(O(E("id", S(zone.ZoneId)), E("templateId", S(zone.TemplateId)), E("localZoneId", S(zone.LocalZoneId)),
                    E("parentId", S(zone.ParentId)), E("owner", S(Player(zone.OwnerPlayerId))), E("parentKind", S(zone.ParentKind.ToString())),
                    E("itemCount", N(sourceItems.Count(item => item.ZoneId == zone.ZoneId)))));
            }
            return O(E("instances", A(values)), E("zones", A(zoneValues)), E("definitions", definitions),
                E("stateRevision", N(revision)), E("contentPackHash", S(state.ContentPackHash)));
        }

        internal static string Subject(ItemSubjectRef subject) => subject?.Kind == ItemSubjectKind.Player ? Player(subject.PlayerId) :
            subject?.Kind == ItemSubjectKind.Company ? "company:" + subject.CompanyId : "none";
        private static string Player(int id) => id < 0 ? "none" : "p" + id.ToString(CultureInfo.InvariantCulture);
        private static LuaSnapshotValue Definition(string id, string type, string name, string description, IEnumerable<string> tags) =>
            O(E("id", S(id)), E("definitionId", S(id)), E("contentType", S(type)), E("name", S(name)), E("description", S(description)), E("tags", Strings(tags)));
        private static LuaSnapshotEntry E(string name, LuaSnapshotValue value) => new LuaSnapshotEntry(name, value);
        private static LuaSnapshotValue O(params LuaSnapshotEntry[] entries) => LuaSnapshotValue.Object(entries);
        private static LuaSnapshotValue A(IEnumerable<LuaSnapshotValue> values) => LuaSnapshotValue.Array(values.ToList());
        private static LuaSnapshotValue S(string value) => LuaSnapshotValue.String(value);
        private static LuaSnapshotValue B(bool value) => LuaSnapshotValue.Boolean(value);
        private static LuaSnapshotValue N(int value) => LuaSnapshotValue.Integer(value);
        private static LuaSnapshotValue Strings(IEnumerable<string> values) => A((values ?? Enumerable.Empty<string>()).Select(S));
    }
}
