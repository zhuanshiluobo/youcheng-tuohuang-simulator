using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Application.Setup;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class ThreePlayerRulesTests
    {
        [Test]
        public void ThreePlayerDecks_FilterSourceBeforeShuffling_WithoutMutatingSharedLists()
        {
            var four = CreateDecks(2468, 4);
            var three = CreateDecks(2468, 3);
            var excluded = new[] { "event_green_06", "event_red_05", "event_yellow_05", "event_yellow_08" };
            CollectionAssert.AreEquivalent(excluded,
                AllCards(four).Where(EventCardDatabase.IsFourPlayerOnly));
            Assert.That(three.EventDeckGreen, Has.Count.EqualTo(5));
            Assert.That(three.EventDeckYellow, Has.Count.EqualTo(8));
            Assert.That(three.EventDeckRed, Has.Count.EqualTo(5));
            var expected = new DeckRuntimeState();
            new EventDeckService(2468).InitializeDecks(expected,
                EventCardDatabase.GreenCardIds.Where(id => !excluded.Contains(id)).ToArray(),
                EventCardDatabase.YellowCardIds.Where(id => !excluded.Contains(id)).ToArray(),
                EventCardDatabase.RedCardIds.Where(id => !excluded.Contains(id)).ToArray());
            CollectionAssert.AreEqual(AllCards(expected), AllCards(three));
            Assert.That(EventCardDatabase.GreenCardIds, Has.Count.EqualTo(6));
            Assert.That(EventCardDatabase.YellowCardIds, Has.Count.EqualTo(10));
            Assert.That(EventCardDatabase.RedCardIds, Has.Count.EqualTo(6));
        }

        [Test]
        public void SameSeedAndPlayerCount_ReproduceDecks_AndRebuildClearsOldCards()
        {
            var first = CreateDecks(12345, 3);
            var replay = CreateDecks(12345, 3);
            CollectionAssert.AreEqual(AllCards(first), AllCards(replay));
            replay.EventDeckGreen.Add("event_green_06");
            Initialize(new EventDeckService(12345), replay, 3);
            CollectionAssert.AreEqual(AllCards(first), AllCards(replay));
        }

        [Test]
        public void DefaultPlayerCount_PreservesFourPlayerPoolAndOrder()
        {
            var decks = new DeckRuntimeState();
            new EventDeckService(2468).InitializeDecks(decks,
                EventCardDatabase.GreenCardIds, EventCardDatabase.YellowCardIds, EventCardDatabase.RedCardIds);
            CollectionAssert.AreEqual(AllCards(CreateDecks(2468, 4)), AllCards(decks));
            CollectionAssert.AreEqual(AllCards(CreateDecks(2468, 1)), AllCards(decks));
            Assert.That(AllCards(decks).Count(), Is.EqualTo(22));
            Assert.That(AllCards(decks).Count(EventCardDatabase.IsFourPlayerOnly), Is.EqualTo(4));
        }

        [TestCase(3)]
        [TestCase(4)]
        public void InitialFunds_AreAddedInTurnOrder_NotPlayerIdOrder(int playerCount)
        {
            var state = CreateState(playerCount);
            state.StartPlayerId = 2;
            state.UseSeatTurnOrder = true;
            foreach (var player in state.Players) player.Resources.GoldVoucher = 7;
            // 隔离基础资金发放，不依赖入场事件资产和 A 尚在录入的正式地图。
            var handler = new SetupCommandHandler(new MapQueryService(new GameMapDefinition()));
            var grant = typeof(SetupCommandHandler).GetMethod("GrantInitialGoldVouchers", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(grant, Is.Not.Null);
            grant.Invoke(handler, new object[] { state });
            var expected = playerCount == 3 ? new[] { 10, 12, 18 } : new[] { 10, 12, 14, 18 };
            var order = new TurnOrderService().GetTurnOrder(state);
            Assert.That(order.Count, Is.EqualTo(playerCount));
            Assert.That(order[0], Is.EqualTo(2));
            for (var i = 0; i < order.Count; i++)
                Assert.That(state.FindPlayer(order[i]).Resources.GoldVoucher, Is.EqualTo(7 + expected[i]));
        }

        [Test]
        public void ThreePlayerRedZone_OpensAtRoundFive_NotRoundFour()
        {
            var state = CreateState(3);
            var map = new GameMapDefinition { MinPlayers = 3, MaxPlayers = 3 };
            var red = new MapLocationDefinition { IsRedZone = true };
            state.Round = 4;
            Assert.That(RedZoneAccessRule.IsClosed(state, map, red), Is.True);
            Assert.That(RedZoneAccessRule.IsClosed(state, map, new MapLocationDefinition()), Is.False);
            state.Round = 5;
            Assert.That(RedZoneAccessRule.IsClosed(state, map, red), Is.False);
            Assert.That(RedZoneAccessRule.GetOpenRound(new GameMapDefinition { MinPlayers = 4, MaxPlayers = 4 }, 4), Is.EqualTo(4));
        }

        [Test]
        public void SevenRegionScoring_UsesMapValuesAndCityInfluence_TiesGiveNoPoints()
        {
            // 规则夹具，不冒充正式三人拓扑验收；生产地图由 A 的专项测试验证。
            var map = new GameMapDefinition { MinPlayers = 3, MaxPlayers = 3 };
            var ids = new[] { "A", "B", "C", "D", "E", "F", "R" };
            foreach (var id in ids)
            {
                map.Regions.Add(new MapRegionDefinition
                {
                    RegionId = id, ScoreValue = id == "E" || id == "F" ? 4 : 3,
                    LocationIds = id == "R" ? new List<string>() : new List<string> { id + "-test" }
                });
                if (id != "R") map.Locations.Add(new MapLocationDefinition { LocationId = id + "-test", RegionId = id });
            }
            var state = CreateState(3);
            state.Phase = GamePhase.FinalScoring;
            state.FindPlayer(1).CityLocationId = "A-test";
            state.FindPlayer(2).CityLocationId = "E-test";
            new FinalScoringService(new MapQueryService(map)).Resolve(state);
            Assert.That(state.FinalScoring.RegionScores, Has.Count.EqualTo(7));
            CollectionAssert.AreEqual(new[] { 3, 3, 3, 3, 4, 4, 3 }, state.FinalScoring.RegionScores.Select(r => r.ScoreValue));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(3));
            Assert.That(state.FindPlayer(2).Score, Is.EqualTo(4));
            Assert.That(state.FinalScoring.RegionScores[0].InfluenceCounts.Single().Count, Is.EqualTo(2));
            state.FindPlayer(3).CityLocationId = "A-test";
            var tied = new RegionControlService(new MapQueryService(map)).Evaluate(state);
            Assert.That(tied[0].ControllerPlayerId, Is.Null);
        }

        [Test]
        public void InitialEntranceReward_IsAddedOnce_IndependentlyOfBaseFunds()
        {
            var map = new GameMapDefinition { MapId = StaticMapDefinitions.ThreePlayerMapId, MinPlayers = 3, MaxPlayers = 3 };
            var reward = new ResourceSet { Originium = 2, Iron = 2, OriginiumShard = 2 };
            map.Locations.Add(new MapLocationDefinition
            {
                LocationId = "B-01", CanDockCity = true, EventColor = EventColor.Green,
                InitialEntranceReward = reward
            });
            map.Locations.Add(new MapLocationDefinition
            {
                LocationId = "A-03", CanDockCity = true, EventColor = EventColor.Yellow
            });
            var state = CreateState(3);
            state.Phase = GamePhase.Entrance;
            state.StartPlayerId = 1;
            state.CurrentPlayerId = 1;
            state.FindPlayer(1).Resources.GoldVoucher = 7;
            // 已有资源标记，隔离地图奖励；这里不冒充事件选项流程验收。
            var tokens = new ResourceTokenService();
            tokens.PlaceToken(state.Map, "B-01", ResourceType.Iron, 1);
            var handler = new SetupCommandHandler(new MapQueryService(map), new EventDeckService(1), tokens, new TurnOrderService());
            Assert.That(handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation, PlayerId = 1, TargetId = "A-03"
            }).Succeeded, Is.False);
            var command = new GameCommand { Kind = GameCommandKind.ChooseInitialLocation, PlayerId = 1, TargetId = "B-01" };
            Assert.That(handler.Handle(state, command).Succeeded, Is.True);
            // 回到该玩家以验证“已有城市”门禁，而非只依赖当前玩家门禁。
            state.CurrentPlayerId = 1;
            Assert.That(handler.Handle(state, command).Succeeded, Is.False);
            var resources = state.FindPlayer(1).Resources;
            Assert.That(resources.Originium, Is.EqualTo(2));
            Assert.That(resources.Iron, Is.EqualTo(2));
            Assert.That(resources.OriginiumShard, Is.EqualTo(2));
            Assert.That(resources.GoldVoucher, Is.EqualTo(7));
            Assert.That(resources.PureOriginium, Is.Zero);
            Assert.That(reward.Originium, Is.EqualTo(2), "不得消耗共享地图奖励定义");
        }
        private static GameState CreateState(int count)
        {
            var state = new GameState();
            for (var i = 1; i <= count; i++) state.Players.Add(new PlayerState { PlayerId = i });
            return state;
        }

        private static DeckRuntimeState CreateDecks(int seed, int count)
        {
            var decks = new DeckRuntimeState();
            Initialize(new EventDeckService(seed), decks, count);
            return decks;
        }

        private static void Initialize(EventDeckService service, DeckRuntimeState decks, int count)
        {
            service.InitializeDecks(decks, EventCardDatabase.GreenCardIds,
                EventCardDatabase.YellowCardIds, EventCardDatabase.RedCardIds, count);
        }

        private static IEnumerable<string> AllCards(DeckRuntimeState decks)
        {
            return decks.EventDeckGreen.Concat(decks.EventDeckYellow).Concat(decks.EventDeckRed);
        }
    }
}