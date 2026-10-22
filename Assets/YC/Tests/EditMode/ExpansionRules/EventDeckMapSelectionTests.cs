using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Exploration;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class EventDeckMapSelectionTests
    {
        private static ExternalContentPack installed;
        private static ExternalContentPack Installed() => installed ?? (installed = ExternalContentPack.Load(
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "StreamingAssets", "Content", "core")));

        private static GameState Create(int players, string mapId, bool enterprise)
        {
            var config = new GameRuleConfiguration { PlayerCount = players, MapId = mapId };
            if (enterprise) config.EnabledExpansionIds.Add("enterprise");
            var catalog = ActiveContentCatalogBuilder.Build(Installed(), config);
            config.ContentPackHash = catalog.ContentPackHash;
            var seats = Enumerable.Range(1, players).Select(id => new PlayerSeat {
                PlayerId = id, PlayerName = "玩家" + id, Color = (PlayerColor)(id - 1) }).ToList();
            return GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, seats, config,
                321, catalog.SetupRecipe, catalog.ContentDefinitions);
        }

        [TestCase(1, StaticMapDefinitions.FourPlayerMapId, true)]
        [TestCase(2, StaticMapDefinitions.FourPlayerMapId, true)]
        [TestCase(3, StaticMapDefinitions.FourPlayerMapId, true)]
        [TestCase(4, StaticMapDefinitions.FourPlayerMapId, true)]
        [TestCase(2, StaticMapDefinitions.ThreePlayerMapId, true)]
        [TestCase(3, StaticMapDefinitions.ThreePlayerMapId, true)]
        [TestCase(3, StaticMapDefinitions.FourPlayerMapId, false)]
        [TestCase(3, StaticMapDefinitions.ThreePlayerMapId, false)]
        public void FormalAssemblyUsesChosenMapAndPreservesPlayerRules(int players, string mapId, bool enterprise)
        {
            var state = Create(players, mapId, enterprise);
            Assert.That(state.MapId, Is.EqualTo(mapId));
            Assert.That(state.RuleConfiguration.PlayerCount, Is.EqualTo(players));
            Assert.That(state.Players.Count, Is.EqualTo(players));
            bool full = mapId == StaticMapDefinitions.FourPlayerMapId;
            Assert.That(state.Decks.EventDeckGreen.Count, Is.EqualTo(full ? 6 : 5));
            Assert.That(state.Decks.EventDeckYellow.Count, Is.EqualTo(full ? 10 : 8));
            Assert.That(state.Decks.EventDeckRed.Count, Is.EqualTo(full ? 6 : 5));
            Assert.That(state.SetupRecipe.OperatorDrawCount, Is.EqualTo(players == 2 ? 3 : 2));
            Assert.That(state.Players.All(p => p.InfluenceSupply == 29), Is.True);
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
            if (enterprise)
            {
                Assert.That(state.Decks.EventDeckYellow, Does.Contain("enterprise.event.cave_ruins"));
                Assert.That(state.Decks.EventDeckRed, Does.Contain("enterprise.event.collection_platform"));
            }
        }

        [TestCase(StaticMapDefinitions.ThreePlayerMapId)]
        [TestCase(StaticMapDefinitions.FourPlayerMapId)]
        public void RegisteredRecipeAndLegacyDeckInitializationUseSameMap(string mapId)
        {
            var recipe = GameSetupRecipe.CreateRegisteredStandard(mapId);
            var green = EventCardDatabase.GreenCardIds.ToArray();
            var yellow = EventCardDatabase.YellowCardIds.ToArray();
            var red = EventCardDatabase.RedCardIds.ToArray();
            var decks = new DeckRuntimeState();
            new EventDeckService(321).InitializeDecks(decks, green, yellow, red, mapId);
            bool full = mapId == StaticMapDefinitions.FourPlayerMapId;
            Assert.That(decks.EventDeckGreen.Count, Is.EqualTo(full ? 6 : 5));
            Assert.That(decks.EventDeckYellow.Count, Is.EqualTo(full ? 10 : 8));
            Assert.That(decks.EventDeckRed.Count, Is.EqualTo(full ? 6 : 5));
            foreach (string role in new[] { "event_green", "event_yellow", "event_red" })
            {
                var ids = recipe.Entries.Where(e => e.InitialZoneRole == role).Select(e => e.DefinitionId);
                Assert.That(ids.Any(EventCardDatabase.IsFourPlayerOnly), Is.EqualTo(full));
            }
            Assert.That(green.Length, Is.EqualTo(6));
            Assert.That(yellow.Length, Is.EqualTo(10));
            Assert.That(red.Length, Is.EqualTo(6));
            Assert.That(green, Is.EqualTo(EventCardDatabase.GreenCardIds));
            Assert.That(yellow, Is.EqualTo(EventCardDatabase.YellowCardIds));
            Assert.That(red, Is.EqualTo(EventCardDatabase.RedCardIds));
        }

        [TestCase(StaticMapDefinitions.FourPlayerMapId, 2, true)]
        [TestCase(StaticMapDefinitions.ThreePlayerMapId, 0, false)]
        public void D01EligibilityAfterEightYellowCardsFollowsMapPool(string mapId, int remaining, bool eligible)
        {
            var state = Create(3, mapId, true);
            var yellow = state.ItemZones.Zones.Single(z => z.LocalZoneId == "event_yellow");
            var retired = state.ItemZones.Zones.Single(z => z.LocalZoneId == "retired");
            var order = state.ItemZones.Orders.Single(o => o.ZoneId == yellow.ZoneId);
            // 预置历史边界夹具，使用原实例和牌序；不将其冒充完整主链回放。
            foreach (string id in order.ItemIds.Take(8).ToArray())
                SetPosition(state, state.ItemZones.Items.Single(i => i.ItemId == id), retired.ZoneId);
            PlaceCity(state, 1, "D-02");
            PlaceCity(state, 2, mapId == StaticMapDefinitions.FourPlayerMapId ? "G-03" : "B-01");
            PlaceCity(state, 3, "C-02");
            state.Phase = GamePhase.ActionRound2;
            state.Round = 2;
            state.CurrentPlayerId = 1;
            state.FindPlayer(1).Resources.GoldVoucher = 30;
            var exploration = new ExplorationService(new MapQueryService(StaticMapDefinitions.Resolve(mapId)));
            Assert.That(state.Decks.EventDeckYellow.Count, Is.EqualTo(remaining));
            Assert.That(exploration.GetSelectableLocationIds(state, 1).Contains("D-01"), Is.EqualTo(eligible));
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(29));
            Assert.That(state.ItemZones.Items.Count(i => i.TypeId == "core.event_card"),
                Is.EqualTo(mapId == StaticMapDefinitions.FourPlayerMapId ? 22 : 18));
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
        }

        private static void PlaceCity(GameState state, int playerId, string locationId)
        {
            var location = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == locationId);
            var dock = state.ItemZones.Zones.Single(z => z.ParentId == location.ZoneId && z.TemplateId == "core.zone.city_dock");
            var city = state.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == playerId);
            SetPosition(state, city, dock.ZoneId);
        }

        private static void SetPosition(GameState state, ItemInstanceState item, string destination)
            => typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { state, item, destination });
    }
}
