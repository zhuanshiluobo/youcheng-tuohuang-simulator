using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public static class RightCardSmokeStateFactory
    {
        public const string DefaultCityLocationId = "G-01";
        public const string SharedCityStyleId = CityStyleDatabase.MilitaryIndustrialArea;

        private static readonly string[] SharedStyleCityLocationIds =
        {
            "G-01",
            "A-01",
            "A-02",
            "B-01"
        };

        public static GameState CreateInitialState(
            LaunchMode mode,
            int localPlayerId,
            IList<PlayerSeat> players,
            string mapId,
            int eventDeckSeed,
            bool prepareSharedCityStyle = false)
        {
            var state = GameLaunchStateFactory.CreateInitialState(
                mode,
                localPlayerId,
                players,
                mapId,
                eventDeckSeed);
            var player = state.FindPlayer(localPlayerId);
            if (player == null && state.Players.Count > 0)
            {
                player = state.Players[0];
            }

            if (player == null)
            {
                throw new InvalidOperationException("Right card smoke state requires at least one player.");
            }

            PrepareState(state, player);
            if (prepareSharedCityStyle)
            {
                PrepareSharedCityStyleState(state);
            }
            if (!new ItemZoneQuery().TryValidate(state, out string reason)) throw new InvalidOperationException(reason);
            return state;
        }

        private static void PrepareState(GameState state, PlayerState player)
        {
            var roundExecution = new RoundExecutionService();
            var prepare = roundExecution.PrepareActionWindowForSmoke(state, player.PlayerId);
            if (!prepare.IsValid)
            {
                throw new InvalidOperationException(prepare.Reason);
            }
            state.PendingChoice = null;
            state.PendingCardSession = null;

            if (string.IsNullOrEmpty(player.CityLocationId))
            {
                PlaceCity(state, player.PlayerId, DefaultCityLocationId);
            }

            player.ActedMainActionThisTurn = false;
            player.HasMovedCityThisRound = false;
            player.HasCollectedResourcesThisRound = false;
            player.Resources.Originium = Math.Max(player.Resources.Originium, 6);
            player.Resources.OriginiumShard = Math.Max(player.Resources.OriginiumShard, 6);
            player.Resources.Iron = Math.Max(player.Resources.Iron, 6);
            player.Resources.PureOriginium = Math.Max(player.Resources.PureOriginium, 2);
            player.Resources.GoldVoucher = Math.Max(player.Resources.GoldVoucher, 30);

            if (!state.Map.OpenLocationIds.Contains(player.CityLocationId))
            {
                state.Map.OpenLocationIds.Add(player.CityLocationId);
            }

            if (!state.ItemZones.Items.Any(item => item.DefinitionId == FacilityCardDatabase.SimpleEngineeringCamp &&
                state.ItemZones.Zones.Any(zone => zone.ZoneId == item.ZoneId && zone.TemplateId == "core.zone.facility_supply_slot")))
            {
                var facility = state.ItemZones.Items.First(item => item.DefinitionId == FacilityCardDatabase.SimpleEngineeringCamp);
                var slot = state.ItemZones.Zones.Single(zone => zone.LocalZoneId == "facility_supply_0");
                var previous = state.ItemZones.Items.SingleOrDefault(item => item.ZoneId == slot.ZoneId);
                if (previous != null) Place(state, previous, state.ItemZones.Zones.Single(zone => zone.ZoneId == facility.ZoneId));
                Place(state, facility, slot);
            }
            var styles = state.ItemZones.Zones.Single(zone => zone.LocalZoneId == "city_styles");
            foreach (var item in state.ItemZones.Items.Where(item => CityStyleDatabase.DefaultSupplyIds.Contains(item.DefinitionId)).ToList())
                Place(state, item, styles);
        }

        private static void PrepareSharedCityStyleState(GameState state)
        {
            var cityStyle = CityStyleDatabase.Get(SharedCityStyleId);
            if (cityStyle == null)
            {
                throw new InvalidOperationException("Shared city style smoke state requires a known city style.");
            }

            var ids = state.Players.Select(player => player.PlayerId).ToList();
            foreach (var id in ids)
            {
                int index = ids.IndexOf(id);
                PlaceCity(state, id, SharedStyleCityLocationIds[index]);
                var style = state.ItemZones.Items.Single(item => item.DefinitionId == SharedCityStyleId);
                var marker = state.ItemZones.Items.First(item => item.TypeId == "core.player_marker" && item.OwnerSubject.PlayerId == id &&
                    state.ItemZones.Zones.Any(zone => zone.ZoneId == item.ZoneId && zone.LocalZoneId == "supply"));
                string markerId = marker.ItemId;
                var zoneDefinition = new CardZoneDefinition {
                    LocalZoneId = "declaration_" + id + "_0", ZoneTemplateId = "core.zone.board_marker_track",
                    AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" },
                    OrderingKind = ZoneOrderingKind.Track, CapacityPolicy = "bounded", Capacity = 1,
                    VisibilityPolicyId = ItemZoneVisibility.Public, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents"
                };
                if (!new ItemZoneStateFactory().TryCreateCardZone(state, style.ItemId, zoneDefinition, out string zoneId, out string reason))
                    throw new InvalidOperationException(reason);
                var zone = state.ItemZones.Zones.Single(value => value.ZoneId == zoneId);
                zone.LegalSlotIds = new List<string> { "unused", "used" };
                Place(state, state.ItemZones.Items.Single(item => item.ItemId == markerId), zone, "unused");
                var declaration = new CityStyleDeclarationState {
                    DeclarationId = "smoke_declaration_" + id, CityStyleItemId = style.ItemId,
                    InfluenceMarkerId = markerId, CityStyleId = SharedCityStyleId,
                    UnlockedSpecialActionId = cityStyle.SpecialActionId ?? string.Empty
                };
                state.FindPlayer(id).DeclaredCityStyles.Add(declaration);
                declaration.BindItemZones(state);
            }
        }

        private static void PlaceCity(GameState state, int playerId, string preferred)
        {
            var map = StaticMapDefinitions.Resolve(state.MapId);
            string locationId = map.Locations.Any(location => location.LocationId == preferred && location.CanDockCity)
                ? preferred : map.Locations.First(location => location.CanDockCity && !location.IsRedZone).LocationId;
            var location = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.map_location" && zone.LocalZoneId == locationId);
            var cities = state.ItemZones.Zones.Single(zone => zone.ParentId == location.ZoneId && zone.LocalZoneId == "cities");
            var city = state.ItemZones.Items.Single(item => item.TypeId == "core.mobile_city" && item.OwnerSubject.PlayerId == playerId);
            Place(state, city, cities);
            if (!state.Map.OpenLocationIds.Contains(locationId)) state.Map.OpenLocationIds.Add(locationId);
        }

        // 调试输入装配只移动正式配方已有实例；发布前校验实体区域，不写旧派生位置列表。
        private static void Place(GameState state, ItemInstanceState item, ZoneInstanceState zone, string slot = "")
        {
            foreach (var order in state.ItemZones.Orders)
                if (order.ItemIds.Remove(item.ItemId)) order.OrderRevision++;
            item.ZoneId = zone.ZoneId; item.SlotId = slot;
            var destination = state.ItemZones.Orders.SingleOrDefault(order => order.ZoneId == zone.ZoneId);
            if (destination != null) { destination.ItemIds.Add(item.ItemId); destination.OrderRevision++; }
        }
    }
}
