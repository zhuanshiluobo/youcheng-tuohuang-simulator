using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Exploration;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    /// <summary>经过真实移动/探索 Effect、抽牌和外部图片映射，防止地图颜色与事件牌池串用。</summary>
    public sealed class EventPointColorFlowTests
    {
        private List<EventCardDefinition> previousEvents;
        private ExternalContentPack pack;

        [SetUp]
        public void LoadExternalEvents()
        {
            previousEvents = Colors.SelectMany(EventCardDatabase.GetCardIds).Select(EventCardDatabase.Get).ToList();
            pack = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            ResetEvents();
            EventCardDatabase.InitializeExternal(pack.CreateEvents());
        }

        [TearDown]
        public void RestoreEvents()
        {
            ResetEvents();
            EventCardDatabase.InitializeExternal(previousEvents);
        }

        private static readonly EventColor[] Colors = { EventColor.Green, EventColor.Yellow, EventColor.Red };
        private static void ResetEvents() => typeof(EventCardDatabase)
            .GetMethod("ResetForTests", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);

        [TestCase(3, EventColor.Green, false)]
        [TestCase(3, EventColor.Yellow, false)]
        [TestCase(3, EventColor.Red, false)]
        [TestCase(4, EventColor.Green, false)]
        [TestCase(4, EventColor.Yellow, false)]
        [TestCase(4, EventColor.Red, false)]
        [TestCase(3, EventColor.Green, true)]
        [TestCase(3, EventColor.Yellow, true)]
        [TestCase(3, EventColor.Red, true)]
        [TestCase(4, EventColor.Green, true)]
        [TestCase(4, EventColor.Yellow, true)]
        [TestCase(4, EventColor.Red, true)]
        public void MovementAndExploration_RevealOnlyTheDestinationColor(int players, EventColor expectedColor, bool explore)
        {
            var map = StaticMapDefinitions.ForPlayerCount(players);
            var target = expectedColor == EventColor.Green ? "A-01" : expectedColor == EventColor.Yellow ? "A-03" : "F-01";
            VerifyReveal(map, target, expectedColor, explore, players);
        }

        [TestCase("A-01", EventColor.Yellow)]
        [TestCase("A-03", EventColor.Green)]
        public void Exploration_UsesInjectedMapColorInsteadOfFourPlayerLocationName(string target, EventColor expectedColor)
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            map.Locations.Single(location => location.LocationId == target).EventColor = expectedColor;
            VerifyReveal(map, target, expectedColor, true, 4);
        }

        [Test]
        public void EveryShuffledEventPool_KeepsDefinitionAndSpriteColorTogether()
        {
            foreach (var players in new[] { 3, 4 })
            {
                var decks = new DeckRuntimeState();
                var service = new EventDeckService(20260930);
                InitializeDecks(service, decks, players);
                foreach (var color in Colors)
                    while (service.RemainingCount(decks, color) > 0)
                    {
                        var cardId = service.Draw(decks, color);
                        AssertVisualColor(cardId, color);
                        if (players == 3) Assert.That(EventCardDatabase.IsFourPlayerOnly(cardId), Is.False);
                    }
            }
        }

        private void VerifyReveal(GameMapDefinition map, string target, EventColor expectedColor, bool explore, int players)
        {
            var source = target == "F-01" ? "D-02" : "A-02";
            var routeId = target == "F-01" ? "F1" : target == "A-01" ? "A1" : "A2";
            var query = new MapQueryService(map);
            var state = new GameState
            {
                GameId = "event-color-regression", MapId = map.MapId, Round = 6,
                Phase = GamePhase.ActionRound1, CurrentPlayerId = 1,
                Players = new List<PlayerState>
                {
                    new PlayerState { PlayerId = 1, CityLocationId = source, InfluenceSupply = 30,
                        Resources = new ResourceSet { Originium = 30, OriginiumShard = 30, Iron = 30,
                            PureOriginium = 30, GoldVoucher = 30 } },
                    new PlayerState { PlayerId = 2, CityLocationId = "B-01", InfluenceSupply = 30 }
                }
            };
            state.Players.Add(new PlayerState { PlayerId = 3, CityLocationId = "B-02", InfluenceSupply = 30 });
            if (players == 4)
                state.Players.Add(new PlayerState { PlayerId = 4, CityLocationId = "C-01", InfluenceSupply = 30 });
            var decks = new EventDeckService(20260930);
            InitializeDecks(decks, state.Decks, players);
            var expectedCard = decks.Peek(state.Decks, expectedColor);
            var counts = Colors.ToDictionary(color => color, color => decks.RemainingCount(state.Decks, color));
            var influence = new InfluenceService(query);
            var tokens = new ResourceTokenService();
            var travel = new TravelCostService(query);
            var movement = new CityMovementService(query, influence, travel, decks, tokens);
            var exploration = new ExplorationService(query, influence, decks, tokens);
            var registry = new EffectRegistry();
            ResourceEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, influence);
            CityMoveEffectExecutor.Register(registry, query, influence, movement, travel, tokens);
            ExplorationEffectExecutor.Register(registry, query, exploration, decks, tokens);
            EventCardEffectExecutor.Register(registry, query, influence, decks, tokens);
            EventCardLuaCatalog.Register(registry);
            var executor = new EffectTreeExecutor(state, registry);
            var effect = explore
                ? ExplorationEffectSpecFactory.Explore(1, target, new MapPath
                    { LocationIds = new List<string> { source, target }, RouteIds = new List<string> { routeId } },
                    InfluenceService.GetLocationSlotId(target, 0), new Dictionary<string, int>())
                : CityMoveEffectSpecFactory.Move(1, target);
            var root = executor.CreateRoot(effect);
            var report = executor.RunUntilQuiescent();
            Assert.That(report.Faulted, Is.False, report.FaultCode + ": " + state.EffectRuntime.LastFaultMessage);
            var revealed = state.EffectRuntime.RuleEvents.SingleOrDefault(item => item.EventType == EventCardEffectEventTypeIds.Revealed);
            Assert.That(revealed, Is.Not.Null, executor.GetNode(root).FailureReason);
            Assert.That(revealed.TargetEntityId, Is.EqualTo(target));
            Assert.That(revealed.RouteKey, Is.EqualTo(expectedCard), "目标地点的颜色必须决定抽牌池。");
            Assert.That(report.WaitingForInput, Is.True);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(request => request.Status == "open" &&
                request.InteractionTypeId == EventCardEffectExecutor.OptionInteractionTypeId &&
                request.CandidateIds.All(id => id.StartsWith(expectedCard + ":option:", StringComparison.Ordinal))), Is.True);
            foreach (var color in Colors)
                Assert.That(decks.RemainingCount(state.Decks, color),
                    Is.EqualTo(counts[color] - (color == expectedColor ? 1 : 0)), "只消耗对应颜色的一张事件牌。");
            AssertVisualColor(expectedCard, expectedColor);
        }

        private void AssertVisualColor(string cardId, EventColor color)
        {
            Assert.That(EventCardDatabase.Get(cardId).Color, Is.EqualTo(color));
            var slice = pack.FindArtworkSlice("event", cardId);
            var colorId = color.ToString().ToLowerInvariant();
            Assert.That(slice, Is.Not.Null);
            Assert.That(slice.Artwork, Is.EqualTo("artwork/sheets/event_" + colorId + "_faces.jpg"));
            Assert.That(slice.Id, Does.StartWith("event_" + colorId + "_"));
            var runtime = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            var sprite = (Sprite)runtime.GetMethod("GetArtworkSprite").Invoke(null, new object[] { "event", cardId });
            Assert.That(sprite.name, Is.EqualTo(slice.Id));
            Assert.That(sprite.texture.name, Is.EqualTo(slice.Artwork));
            Assert.That(sprite.rect, Is.EqualTo(new Rect(slice.X, slice.Y, slice.Width, slice.Height)));
        }

        private static void InitializeDecks(EventDeckService service, DeckRuntimeState decks, int players) =>
            service.InitializeDecks(decks, EventCardDatabase.GetCardIds(EventColor.Green),
                EventCardDatabase.GetCardIds(EventColor.Yellow), EventCardDatabase.GetCardIds(EventColor.Red), players);
    }
}
