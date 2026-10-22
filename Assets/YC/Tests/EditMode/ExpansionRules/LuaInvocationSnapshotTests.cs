using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class LuaInvocationSnapshotTests
    {
        private static readonly MethodInfo CreateContext = typeof(LuaEffectEventDispatcher).GetMethod(
            "CreateInvocationContext", BindingFlags.NonPublic | BindingFlags.Static);

        private static GameState State(string mapId = StaticMapDefinitions.FourPlayerMapId)
        {
            var map = StaticMapDefinitions.Resolve(mapId);
            var source = new GameState {
                GameId = "game.lua_snapshot", MapId = mapId, ContentPackHash = "content.lua_snapshot",
                RuleConfiguration = new GameRuleConfiguration { MapId = mapId, PlayerCount = 2, ContentPackHash = "content.lua_snapshot" },
                ContentDefinitions = new GameContentDefinitions(),
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } }
            };
            var recipe = new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId = "hand", DefinitionId = "character.snapshot", TypeId = "core.basic_character_card", PerPlayer = true, Quantity = 2, InitialZoneRole = "hand" },
                new ItemRecipeEntry { EntryId = "facility", DefinitionId = "facility.snapshot", TypeId = "core.facility_card", Quantity = 3, InitialZoneRole = "facility_deck" },
                new ItemRecipeEntry { EntryId = "city", DefinitionId = "city.snapshot", TypeId = "core.mobile_city", PerPlayer = true, Quantity = 1, InitialZoneRole = "city_reserve" },
                new ItemRecipeEntry { EntryId = "resource", DefinitionId = "resource.snapshot", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/" + map.Locations[0].LocationId },
                new ItemRecipeEntry { EntryId = "road", DefinitionId = "road.snapshot", TypeId = "core.road_token", Quantity = 1, InitialZoneRole = "reserve" },
                new ItemRecipeEntry { EntryId = "enterprise", DefinitionId = "enterprise.snapshot", TypeId = "core.board_card", Quantity = 1, InitialZoneRole = "enterprises" }
            };
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, recipe);
            var facility = state.ItemZones.Items.First(item => item.TypeId == "core.facility_card");
            Position(state, facility, Zone(state, "city_cell_0", 1));
            return state;
        }

        private static ZoneInstanceState Zone(GameState state, string local, int owner = -1) =>
            state.ItemZones.Zones.First(zone => zone.LocalZoneId == local && (owner < 0 || zone.OwnerPlayerId == owner));

        private static ZoneInstanceState Child(GameState state, string location, string template) =>
            state.ItemZones.Zones.Single(zone => zone.TemplateId == template && zone.ParentId == Zone(state, location).ZoneId);

        private static void Position(GameState state, ItemInstanceState item, ZoneInstanceState zone)
        {
            state.ItemZones.Orders.Find(order => order.ZoneId == item.ZoneId)?.ItemIds.Remove(item.ItemId);
            item.ZoneId = zone.ZoneId;
            state.ItemZones.Orders.Find(order => order.ZoneId == zone.ZoneId)?.ItemIds.Add(item.ItemId);
        }

        private static LuaScriptDefinition Script(string source = "return function(ctx) return {} end") =>
            new LuaScriptDefinition("content.snapshot", "ability.snapshot", "handler.snapshot", "1.0.0", source, LuaContentHasher.ComputeSha256(source));

        private static LuaInvocationContext Context(GameState state, int player = 1, bool allowOthers = false, string source = null)
        {
            var definition = new LuaHandlerDefinition("subscription.snapshot", "SnapshotEvent", "*", source == null ? Script() : Script(source), allowOtherPlayerReads: allowOthers);
            var ruleEvent = new RuleEvent { EventId = "event.snapshot", EventType = "SnapshotEvent", PlayerId = player,
                Payload = NormalizedValue.CreateString("public.payload"), HostOnlyPayload = NormalizedValue.CreateString("host.payload") };
            return (LuaInvocationContext)CreateContext.Invoke(null, new object[] { definition,
                new EffectEventHandlerContext(state, ruleEvent, new EffectNodeRuntimeState { PlayerId = 1 }, null) });
        }

        private static LuaSnapshotValue Location(LuaInvocationContext context, string id) =>
            context.FacadeSnapshot.GlobalMap.Get("locations").Items.Single(location => location.Get("locationId").StringValue == id);

        [Test]
        public void SameRevisionReadsCurrentPositionsRoadsAndResourceTokens()
        {
            var state = State(); var map = StaticMapDefinitions.Resolve(state.MapId);
            var old = Context(state); string frozen = JsonConvert.SerializeObject(old);
            string first = map.Locations[0].LocationId, second = map.Locations[1].LocationId;
            Position(state, state.ItemZones.Items.Single(item => item.TypeId == "core.resource_point_token"), Child(state, second, "core.zone.resource_point_markers"));
            Position(state, state.ItemZones.Items.First(item => item.TypeId == "core.mobile_city" && item.OwnerSubject.PlayerId == 1), Child(state, second, "core.zone.city_dock"));
            Position(state, state.ItemZones.Items.First(item => item.TypeId == "core.facility_card" && item.ZoneId == Zone(state, "city_cell_0", 1).ZoneId), Zone(state, "city_cell_1", 1));
            var route = map.Routes[0]; Position(state, state.ItemZones.Items.Single(item => item.TypeId == "core.road_token"), Child(state, route.RouteId, "core.zone.route_road"));
            var current = Context(state);
            Assert.That(current.StateRevision, Is.EqualTo(old.StateRevision));
            Assert.That(Location(old, first).Get("hasResourcePointIndicator").BooleanValue, Is.True);
            Assert.That(Location(current, first).Get("hasResourcePointIndicator").BooleanValue, Is.False);
            Assert.That(Location(current, second).Get("hasResourcePointIndicator").BooleanValue, Is.True);
            Assert.That(current.FacadeSnapshot.Players.Single(player => player.PlayerId == "p1").CityLocationId, Is.EqualTo(second));
            Assert.That(current.FacadeSnapshot.GlobalContent.Get("instances").Items.Single(item => item.Get("definitionId").StringValue == "facility.snapshot").Get("slotIndex").IntegerValue, Is.EqualTo(1));
            Assert.That(current.FacadeSnapshot.GlobalMap.Get("routes").Items.Single(item => item.Get("routeId").StringValue == route.RouteId).Get("hasRoad").BooleanValue, Is.True);
            Assert.That(JsonConvert.SerializeObject(old), Is.EqualTo(frozen));
        }

        [Test]
        public void SameRevisionDeckAndHandChangesDoNotReusePriorContext()
        {
            var state = State(); var old = Context(state);
            var deck = state.ItemZones.Orders.Single(order => order.ZoneId == Zone(state, "facility_deck").ZoneId);
            var moved = state.ItemZones.Items.Single(item => item.ItemId == deck.ItemIds[0]);
            deck.ItemIds.Reverse(); Position(state, moved, Zone(state, "facility_supply_0"));
            var character = state.ItemZones.Items.First(item => item.TypeId == "core.basic_character_card" && item.OwnerSubject.PlayerId == 1);
            Position(state, character, Zone(state, "discard", 1));
            var current = Context(state);
            Assert.That(current.StateRevision, Is.EqualTo(old.StateRevision));
            Assert.That(old.FacadeSnapshot.Players.Single(player => player.PlayerId == "p1").HandCardIds, Has.Count.EqualTo(2));
            Assert.That(current.FacadeSnapshot.Players.Single(player => player.PlayerId == "p1").HandCardIds, Has.Count.EqualTo(1));
            var oldZone = old.FacadeSnapshot.GlobalContent.Get("zones").Items.Single(zone => zone.Get("id").StringValue == deck.ZoneId);
            var newZone = current.FacadeSnapshot.GlobalContent.Get("zones").Items.Single(zone => zone.Get("id").StringValue == deck.ZoneId);
            Assert.That(newZone.Get("itemCount").IntegerValue, Is.EqualTo(oldZone.Get("itemCount").IntegerValue - 1));
            Assert.That(current.FacadeSnapshot.GlobalContent.Get("instances").Items.Any(item => item.Get("id").StringValue == moved.ItemId), Is.True);
        }

        [TestCase(false, 1)] [TestCase(true, 3)]
        public void PlayerPrivateReadsKeepExistingPermission(bool allowOthers, int expected)
        {
            const string source = "return function(ctx) return { Effect.GainScore({recipient=ctx.playerId,amount=1+#PlayerData.GetHand('p2')}) } end";
            var state = State(); var context = Context(state, allowOthers: allowOthers, source: source);
            var result = new MoonSharpLuaRuntimeHost().Invoke(Script(source), context);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.Effects.Single().Amount, Is.EqualTo(expected));
            var privateIds = state.ItemZones.Items.Where(item => item.TypeId == "core.basic_character_card" && item.OwnerSubject.PlayerId == 2).Select(item => item.ItemId).ToList();
            var global = context.FacadeSnapshot.GlobalContent.Get("instances").Items.Select(item => item.Get("id").StringValue).ToList();
            Assert.That(global.Intersect(privateIds), Is.Empty);
            Assert.That(context.AllowOtherPlayerReads, Is.EqualTo(allowOthers));
        }

        [Test]
        public void OwnerFallbackDoesNotGrantEventViewerPrivateIdentities()
        {
            var state = State(); var context = Context(state, -1, true);
            Assert.That(context.PlayerId, Is.EqualTo("p1"));
            var privateIds = state.ItemZones.Items.Where(item => item.TypeId == "core.basic_character_card").Select(item => item.ItemId);
            Assert.That(context.FacadeSnapshot.GlobalContent.Get("instances").Items.Select(item => item.Get("id").StringValue).Intersect(privateIds), Is.Empty);
        }

        [TestCase(StaticMapDefinitions.ThreePlayerMapId)] [TestCase(StaticMapDefinitions.FourPlayerMapId)]
        public void RepeatedCallsAndWorkingCopyProduceEquivalentDetachedSnapshots(string mapId)
        {
            var state = State(mapId); var original = Context(state); string frozen = JsonConvert.SerializeObject(original);
            var work = GameStateCloneService.DeepClone(state);
            Assert.That(JsonConvert.SerializeObject(Context(state)), Is.EqualTo(frozen));
            Assert.That(JsonConvert.SerializeObject(Context(work)), Is.EqualTo(frozen));
            Assert.That(original.FacadeSnapshot.EventPayload.StringValue, Is.EqualTo("host.payload"));
            state.Players[0].Score += 2; state.Players[0].Resources.GoldVoucher += 3;
            state.Map.OpenLocationIds.Add(StaticMapDefinitions.Resolve(mapId).Locations[0].LocationId);
            Assert.That(JsonConvert.SerializeObject(original), Is.EqualTo(frozen));
            Assert.That(JsonConvert.SerializeObject(Context(work)), Is.EqualTo(frozen));
            Assert.That(Context(state).PlayerScore, Is.EqualTo(original.PlayerScore + 2));
        }
    }
}
