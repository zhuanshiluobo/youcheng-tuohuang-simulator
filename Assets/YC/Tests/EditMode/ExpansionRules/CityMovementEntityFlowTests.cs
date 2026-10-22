using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class CityMovementEntityFlowTests
    {
        [TestCase(1, "G-03", "G-02")]
        [TestCase(2, "D-02", "D-03")]
        [TestCase(3, "C-02", "C-03")]
        public void ThreePlayersOnFourPlayerMapCanMoveAfterEightYellowEvents(int player, string source, string target)
        {
            var f = Create(player);
            Assert.That(f.Decks.RemainingCount(f.State.Decks, EventColor.Yellow), Is.EqualTo(10));
            RetireYellow(f, 8);
            var result = Move(f, target);
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Assert.That(f.State.FindPlayer(player).CityLocationId, Is.EqualTo(target));
            Assert.That(f.State.FindPlayer(player).Resources.OriginiumShard, Is.EqualTo(1));
            Assert.That(f.State.FindPlayer(player).RemainingMainActionsThisTurn, Is.EqualTo(1));
            var move = LastMove(f);
            Assert.That(move.Status, Is.EqualTo(EffectNodeStatus.Blocked));
            var commit = f.State.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == move.EffectId && n.EffectTypeId == CityMoveEffectTypeIds.CommitPosition);
            Assert.That(commit.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(commit.PositionChanges.Count, Is.EqualTo(1));
            Assert.That(commit.PositionChanges[0].ItemId, Is.EqualTo(City(f, player).ItemId));
            Assert.That(commit.PositionChanges[0].To.ZoneId, Is.EqualTo(City(f, player).ZoneId));
            Assert.That(ParentLocation(f, City(f, player)), Is.EqualTo(target));
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.SourceEffectId == move.EffectId && e.EventType == CityMoveEventTypeIds.CityMoveCompleted), Is.EqualTo(1));
            CompleteMoveResponses(f);
            Assert.That(LastMove(f).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.FindPlayer(player).RemainingMainActionsThisTurn, Is.Zero);
            int shards = f.State.FindPlayer(player).Resources.OriginiumShard;
            new EffectTreeExecutor(f.State, f.Registry).RunUntilQuiescent();
            Assert.That(f.State.FindPlayer(player).Resources.OriginiumShard, Is.EqualTo(shards));
            Assert.That(f.State.FindPlayer(player).RemainingMainActionsThisTurn, Is.Zero);
        }

        [Test]
        public void ExhaustedEventDeckRejectsUnrevealedMoveWithTheActualFailedNode()
        {
            var f = Create(3); RetireYellow(f, 10);
            var result = Move(f, "C-03");
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            var move = LastMove(f);
            Assert.That(move.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(move.FailureReason, Is.EqualTo(CommandErrorCode.InvalidTarget.ToString()));
            Assert.That(move.NormalizedResult.Kind, Is.EqualTo(NormalizedValueKind.String));
            Assert.That(result.Validation.Reason, Does.Contain(move.NormalizedResult.StringValue));
            Assert.That(f.State.FindPlayer(3).CityLocationId, Is.EqualTo("C-02"));
            Assert.That(f.State.FindPlayer(3).Resources.OriginiumShard, Is.EqualTo(4));
            Assert.That(f.State.FindPlayer(3).RemainingMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(move.ChildEffectIds, Is.Empty);
            Assert.That(f.State.EffectRuntime.RuleEvents.Any(e => e.SourceEffectId == move.EffectId && e.EventType == CityMoveEventTypeIds.CityMoveCompleted), Is.False);
        }

        [Test]
        public void RevealedTargetStillMovesWithAnEmptyDeckAndAnOpenMainlineRequest()
        {
            var f = Create(3); Reveal(f, "C-02"); Reveal(f, "C-03"); RetireYellow(f, 10);
            var result = Move(f, "C-03");
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            var move = LastMove(f);
            Assert.That(move.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.FindPlayer(3).CityLocationId, Is.EqualTo("C-03"));
            Assert.That(ParentLocation(f, City(f, 3)), Is.EqualTo("C-03"));
            Assert.That(f.State.FindPlayer(3).Resources.OriginiumShard, Is.EqualTo(1));
            Assert.That(f.State.FindPlayer(3).RemainingMainActionsThisTurn, Is.Zero);
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.ParentEffectId == move.EffectId && n.EffectTypeId == EventCardEffectTypeIds.Resolve), Is.False);
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.True);
            Assert.That(result.LogMessage, Does.Not.Contain("等待事件"));
        }

        private sealed class Fixture
        {
            public GameState State;
            public EffectRegistry Registry;
            public EventDeckService Decks;
            public MoveCityCommandHandler Handler;
            public int Player;
        }

        private static Fixture Create(int player)
        {
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            var config = new GameRuleConfiguration { PlayerCount = 3, MapId = StaticMapDefinitions.FourPlayerMapId,
                EnabledExpansionIds = new List<string> { "core", "enterprise" } };
            var catalog = ActiveContentCatalogBuilder.Build(pack, config); config.ContentPackHash = catalog.ContentPackHash;
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, player,
                Enumerable.Range(1, 3).Select(p => new PlayerSeat { PlayerId = p }).ToList(), config, 321, catalog.SetupRecipe, catalog.ContentDefinitions);
            foreach (var row in new[] { new { Player = 1, Source = "G-03" }, new { Player = 2, Source = "D-02" }, new { Player = 3, Source = "C-02" } })
            {
                var city = state.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == row.Player);
                var parent = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == row.Source);
                var dock = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.city_dock" && z.ParentId == parent.ZoneId);
                SetPosition(state, city, dock.ZoneId);
                state.FindPlayer(row.Player).Resources.OriginiumShard = 4;
                state.FindPlayer(row.Player).RemainingMainActionsThisTurn = 1;
            }
            var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)); var influence = new InfluenceService(map);
            var tokens = new ResourceTokenService(); var decks = new EventDeckService(321); var costs = new TravelCostService(map);
            var movement = new CityMovementService(map, influence, costs, decks, tokens); var registry = new EffectRegistry();
            catalog.Register(registry); ResourceEffectExecutor.Register(registry); InfluenceEffectExecutor.Register(registry, influence);
            CityMoveEffectExecutor.Register(registry, map, influence, movement, costs, tokens);
            EventCardEffectExecutor.Register(registry, map, influence, decks, tokens);
            var builder = new RoundMainChainBuilder(); var order = new List<int> { 1, 2, 3 };
            var chain = builder.Build(state.GameId, builder.CreateRoundExecutionId(state.GameId, 2, order), 2, order, 1);
            var window = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && n.ActionRound == 2 && n.PlayerId == player);
            // 载入第二行动轮检查点，后续挂载、交互、付款和移动均走正式内核。
            foreach (var node in chain.Nodes.Where(n => n.MainlineIndex < window.MainlineIndex)) node.Status = EffectNodeStatus.Completed;
            var runtime = state.EffectRuntime; runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = 2;
            runtime.CurrentRoundStartPlayerId = 1; runtime.PlayerOrderSnapshot = order; runtime.FirstMainNodeId = chain.FirstNodeId;
            runtime.MainNodes.AddRange(chain.Nodes); runtime.ActiveMainNodeId = window.NodeId; window.Status = EffectNodeStatus.Ready;
            var round = new RoundExecutionService(registry);
            var prepared = round.Advance(state); Assert.That(prepared.IsValid, Is.True, prepared.Reason);
            Assert.That(state.Round, Is.EqualTo(2)); Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound2));
            return new Fixture { State = state, Registry = registry, Decks = decks, Player = player,
                Handler = new MoveCityCommandHandler(movement, new RoundAdvanceService(round), registry) };
        }

        private static CommandResult Move(Fixture f, string target) => f.Handler.Handle(f.State,
            new GameCommand { CommandId = "movement-entity-flow", Kind = GameCommandKind.MoveCity, PlayerId = f.Player,
                TargetId = CityMoveCandidateQueryService.CandidatePrefix + target });
        private static EffectNodeRuntimeState LastMove(Fixture f) => f.State.EffectRuntime.EffectNodes.Last(n => n.EffectTypeId == CityMoveEffectTypeIds.Move);
        private static ItemInstanceState City(Fixture f, int player) => f.State.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == player);
        private static string ParentLocation(Fixture f, ItemInstanceState item)
        {
            var dock = f.State.ItemZones.Zones.Single(z => z.ZoneId == item.ZoneId);
            return f.State.ItemZones.Zones.Single(z => z.ZoneId == dock.ParentId).LocalZoneId;
        }
        private static void RetireYellow(Fixture f, int count)
        {
            var deck = f.State.ItemZones.Zones.Single(z => z.LocalZoneId == "event_yellow");
            var retired = f.State.ItemZones.Zones.Single(z => z.LocalZoneId == "retired");
            foreach (var id in f.State.ItemZones.Orders.Single(o => o.ZoneId == deck.ZoneId).ItemIds.Take(count).ToArray())
                SetPosition(f.State, f.State.ItemZones.Items.Single(i => i.ItemId == id), retired.ZoneId);
        }
        private static void Reveal(Fixture f, string location)
        {
            var parent = f.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == location);
            var markers = f.State.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.resource_point_markers");
            var source = f.State.EffectRuntime.MainNodes.Single(n => n.NodeId == f.State.EffectRuntime.ActiveMainNodeId).ExecutionEffectId;
            Assert.That(new ItemZoneStateFactory().TryCreateGrantedItem(f.State, source, location, "core.resource_point", "core.resource_point_token", markers.ZoneId,
                new ItemSubjectRef(), out var itemId, out var reason), Is.True, reason);
            f.State.ItemZones.Items.Single(i => i.ItemId == itemId).LocalState = NormalizedValue.CreateObject(new List<NormalizedValueEntry> {
                new NormalizedValueEntry { Name = "resourceType", Value = NormalizedValue.CreateInteger((int)ResourceType.Originium) },
                new NormalizedValueEntry { Name = "amount", Value = NormalizedValue.CreateInteger(1) } });
            if (!f.State.Map.OpenLocationIds.Contains(location)) f.State.Map.OpenLocationIds.Add(location);
        }
        private static void SetPosition(GameState state, ItemInstanceState item, string zone) => typeof(ItemZoneStateMigrator)
            .GetMethod("SetPosition", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { state, item, zone });
        private static void CompleteMoveResponses(Fixture f)
        {
            for (int i = 0; i < 30 && LastMove(f).Status != EffectNodeStatus.Completed; i++)
            {
                var owner = f.State.EffectRuntime.MainNodes.Single(n => n.NodeId == f.State.EffectRuntime.ActiveMainNodeId).ExecutionEffectId;
                var request = f.State.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open" && r.OwnerEffectId != owner);
                Assert.That(request, Is.Not.Null, f.State.EffectRuntime.LastFaultMessage);
                var choices = request.CandidateIds.Take(Math.Max(1, request.MinSelections)).Select(NormalizedValue.CreateString).ToList();
                var answer = choices.Count == 1 ? choices[0] : NormalizedValue.CreateArray(choices);
                var engine = new EffectTreeExecutor(f.State, f.Registry);
                Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, answer, out var reason), Is.True, reason);
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            }
        }
    }
}
