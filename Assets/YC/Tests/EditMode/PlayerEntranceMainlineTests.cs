using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using YC.Application.Setup;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class PlayerEntranceMainlineTests
    {
        [OneTimeSetUp]
        public void LoadDefinitions()
        {
            var pack = ExternalContentPack.Load(Path.Combine(
                Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            if (!CharacterCardDatabase.IsInitialized)
                CharacterCardDatabase.Initialize(pack.CreateCharacters());
            if (!EventCardDatabase.IsInitialized)
                EventCardDatabase.InitializeExternal(pack.CreateEvents());
            if (!FacilityCardDatabase.IsInitialized)
                FacilityCardDatabase.InitializeExternal(pack.CreateFacilities());
        }

        internal static SetupCommandHandler Handler(IMapQueryService map)
        {
            var handler = new SetupCommandHandler(map);
            EventCardLuaCatalog.Register(handler.EffectRegistry);
            return handler;
        }

        private static GameState State()
        {
            var state = new GameState { GameId = "entrance-regression", MapId = StaticMapDefinitions.FourPlayerMapId,
                Phase = GamePhase.Entrance, StartPlayerId = 1, CurrentPlayerId = 1, UseSeatTurnOrder = true };
            for (int i = 1; i <= 4; i++)
            {
                var player = new PlayerState { PlayerId = i, Color = (PlayerColor)(i - 1) };
                CharacterCardDatabase.InitializePlayerHand(player);
                state.Players.Add(player);
            }
            foreach (string id in StaticMapDefinitions.FourPlayerInitialLocationIds)
                state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = id, ResourceType = ResourceType.Iron, Amount = 1 });
            return state;
        }
        private static SetupCommandHandler Handler() => Handler(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
        private static CommandResult Enter(GameState state, SetupCommandHandler handler, int player, string location)
        {
            if (string.IsNullOrEmpty(state.EffectRuntime.ActiveMainNodeId))
                Assert.That(Round(handler).StartEntrance(state).IsValid, Is.True);
            var command = new GameCommand { Kind = GameCommandKind.ChooseInitialLocation, PlayerId = player, TargetId = location };
            var request = state.EffectRuntime.InteractionRequests.Find(item => item.Status == "open" &&
                item.AnsweringPlayerId == player && item.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId);
            if (request != null)
            {
                Assert.That(request.StateRevision, Is.EqualTo(state.EffectRuntime.StateRevision));
                command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
                command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
            }
            return handler.Handle(state, command);
        }
        private static RoundExecutionService Round(SetupCommandHandler handler) => new RoundExecutionService(handler.EffectRegistry);
        private static MainlineNodeRuntimeState EntranceStage(GameState state) => state.EffectRuntime.MainNodes
            .Single(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup);
        private static EffectNodeRuntimeState[] EntranceTasks(GameState state)
        {
            var execution = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == EntranceStage(state).ExecutionEffectId);
            var group = execution.ChildSchedulingGroups.Single(item => item.StableGroupKey == "entrance_player_tasks");
            Assert.That(group.Mode, Is.EqualTo(EffectChildSchedulingMode.Independent));
            return group.ChildEffectIds.Select(id => state.EffectRuntime.EffectNodes.Single(node => node.EffectId == id)).ToArray();
        }

        [Test]
        public void FourPlayers_HaveOrderedPlacementTasksInSharedEntranceStage_AndGrantInitialGoldOnce()
        {
            var state = State(); var handler = Handler(); var round = Round(handler);
            Assert.That(round.StartEntrance(state).IsValid, Is.True);
            Assert.That(state.EffectRuntime.MainNodes.Any(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntrance), Is.False);
            Assert.That(EntranceTasks(state).Select(node => node.PlayerId), Is.EqualTo(new[] { 1, 2, 3, 4 }));
            Assert.That(EntranceTasks(state).All(node => node.EffectTypeId == RoundExecutionService.PlayerEntranceTaskTypeId), Is.True);
            Assert.That(Enter(state, handler, 2, "A-01").Succeeded, Is.False);
            Assert.That(state.FindPlayer(2).CityLocationId, Is.Empty);
            Assert.That(Enter(state, handler, 1, "B-01").Succeeded, Is.True, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            Assert.That(Enter(state, handler, 1, "B-01").Succeeded, Is.False);
            Assert.That(Enter(state, handler, 2, "A-01").Succeeded, Is.True);
            Assert.That(Enter(state, handler, 3, "A-02").Succeeded, Is.True);
            Assert.That(Enter(state, handler, 4, "G-01").Succeeded, Is.True, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(state.Players.ConvertAll(player => player.Resources.GoldVoucher), Is.EqualTo(new[] { 10, 12, 14, 18 }));
            Assert.That(state.EffectRuntime.RuleEvents.FindAll(e => e.EventType == PlayerEntranceEffectExecutor.EventType), Has.Count.EqualTo(4));
            Assert.That(EntranceStage(state).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(EntranceTasks(state), Has.All.Matches<EffectNodeRuntimeState>(node => node.Status == EffectNodeStatus.Completed));
            Assert.That(round.Advance(state).IsValid, Is.True);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(10));
            var next = new RoundMainChainBuilder().Build(state.GameId, "", 2, new[] { 1, 2, 3, 4 });
            Assert.That(next.Nodes.Exists(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntrance), Is.False);
            Assert.That(next.Nodes.Exists(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup), Is.False);
        }

        [Test]
        public void EventCard_AllowsNextPlacementButBlocksOpeningCompletion_AndRecoversWithoutRedraw()
        {
            var state = State(); var handler = Handler();
            state.Map.ResourceTokens.RemoveAll(token => token.LocationId == "B-01");
            state.Decks.EventDeckGreen.Add("event_green_01");
            var result = Enter(state, handler, 1, "B-01");
            Assert.That(result.Succeeded, Is.True, result.Validation == null ? "" : result.Validation.Reason);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.Entrance));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            Assert.That(state.PendingChoice, Is.Null);
            Assert.That(state.PendingCardSession, Is.Null);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            var request = state.EffectRuntime.InteractionRequests.Find(item => item.Status == "open" && item.InteractionTypeId == EventCardEffectExecutor.OptionInteractionTypeId);
            Assert.That(request, Is.Not.Null);
            string interactionId = request.GetStableInteractionId();
            var candidates = request.CandidateIds.ToArray();
            Assert.That(Enter(state, handler, 2, "A-01").Succeeded, Is.True);
            Assert.That(state.FindPlayer(2).CityLocationId, Is.EqualTo("A-01"));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(3));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.Entrance));
            Assert.That(EntranceStage(state).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            Assert.That(EntranceTasks(state).Single(node => node.PlayerId == 1).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.Players.TrueForAll(player => player.Resources.GoldVoucher == 0), Is.True);
            state = GameStateCloneService.DeepClone(state);
            handler = Handler();
            Assert.That(Round(handler).Advance(state).IsValid, Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.FindAll(node => node.EffectTypeId == EventCardEffectTypeIds.Resolve), Has.Count.EqualTo(1));
            request = state.EffectRuntime.InteractionRequests.Single(item => item.Status == "open" &&
                item.AnsweringPlayerId == 1 && item.GetStableInteractionId() == interactionId);
            Assert.That(request.CandidateIds, Is.EqualTo(candidates));
            Assert.That(request.StateRevision, Is.EqualTo(state.EffectRuntime.StateRevision));
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = 1 };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
            command.OptionIds.Add(request.CandidateIds[0]);
            result = new AnswerInteractionCommandHandler(handler.EffectRegistry, Round(handler)).Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.Validation == null ? "" : result.Validation.Reason);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(5));
            Assert.That(new AnswerInteractionCommandHandler(handler.EffectRegistry, Round(handler)).Handle(state, command).Succeeded, Is.False);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(5));
            Assert.That(state.EffectRuntime.RuleEvents.FindAll(e => e.EventType == PlayerEntranceEffectExecutor.EventType), Has.Count.EqualTo(2));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.Entrance));
            Assert.That(state.EffectRuntime.RuleEvents.Exists(e => e.EventType == "RoundStarted"), Is.False);
            Assert.That(state.Players.TrueForAll(player => player.Resources.GoldVoucher == 0), Is.True);
        }

        [Test]
        public void EntranceEffect_OutsideEntranceMainline_DoesNotPlaceCityOrPublishEvent()
        {
            var state = State(); var handler = Handler();
            var executor = new EffectTreeExecutor(state, handler.EffectRegistry);
            var id = executor.CreateRoot(PlayerEntranceEffectExecutor.Create(1));
            executor.RunUntilQuiescent();
            var node = state.EffectRuntime.EffectNodes.Find(item => item.EffectId == id);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(node.FailureReason, Is.EqualTo("entrance_outside_mainline"));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.Empty);
            Assert.That(state.EffectRuntime.RuleEvents.Exists(e => e.EventType == PlayerEntranceEffectExecutor.EventType), Is.False);
        }

        [Test]
        public void EmptyEventDeck_FailsAndDoesNotAdvanceOrGrantInitialGold()
        {
            var state = State(); var handler = Handler();
            state.Map.ResourceTokens.RemoveAll(token => token.LocationId == "B-01");
            var result = Enter(state, handler, 1, "B-01");
            Assert.That(result.Succeeded, Is.False);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.Entrance));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            Assert.That(EntranceStage(state).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            Assert.That(EntranceTasks(state).Single(node => node.PlayerId == 1).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(state.EffectRuntime.RuleEvents.Exists(e => e.EventType == "RoundStarted"), Is.False);
            Assert.That(Round(handler).Advance(state).IsValid, Is.False);
        }

        [Test]
        public void CityMoveEvent_DoesNotTriggerEntranceReward()
        {
            var state = State(); var handler = Handler();
            handler.EffectRegistry.Register(new EffectRegistration("test.move", context => EffectStepResult.Completed().AddEvent(
                new EffectEventRequest { EventType = CityMoveEventTypeIds.CityMoveCompleted, PlayerId = 1,
                    OwnerNodeId = context.Node.EffectId, SourceEffectId = context.Node.EffectId,
                    Payload = NormalizedValue.CreateObject(new List<NormalizedValueEntry> {
                        new NormalizedValueEntry { Name = "targetLocationId", Value = NormalizedValue.CreateString("B-01") } }) })));
            var executor = new EffectTreeExecutor(state, handler.EffectRegistry);
            executor.CreateRoot(new EffectSpec("test.move") { PlayerId = 1 });
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.Zero);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.Zero);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
            Assert.That(state.EffectRuntime.RuleEvents.Exists(e => e.EventType == PlayerEntranceEffectExecutor.EventType), Is.False);
        }
    }
}
