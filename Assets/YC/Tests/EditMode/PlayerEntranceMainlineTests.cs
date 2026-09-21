using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Setup;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class PlayerEntranceMainlineTests
    {
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
        private static CommandResult Enter(GameState state, SetupCommandHandler handler, int player, string location) =>
            handler.Handle(state, new GameCommand { Kind = GameCommandKind.ChooseInitialLocation, PlayerId = player, TargetId = location });
        private static RoundExecutionService Round(SetupCommandHandler handler) => new RoundExecutionService(handler.EffectRegistry);

        [Test]
        public void FourPlayers_HaveFourOrderedEntranceNodes_ThenFirstRound_AndGrantInitialGoldOnce()
        {
            var state = State(); var handler = Handler(); var round = Round(handler);
            Assert.That(round.StartEntrance(state).IsValid, Is.True);
            var entrance = state.EffectRuntime.MainNodes.FindAll(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntrance);
            Assert.That(entrance.ConvertAll(node => node.PlayerId), Is.EqualTo(new[] { 1, 2, 3, 4 }));
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
            Assert.That(entrance, Has.All.Matches<MainlineNodeRuntimeState>(node => node.Status == EffectNodeStatus.Completed));
            Assert.That(round.Advance(state).IsValid, Is.True);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(10));
            var next = new RoundMainChainBuilder().Build(state.GameId, "", 2, new[] { 1, 2, 3, 4 });
            Assert.That(next.Nodes.Exists(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntrance), Is.False);
        }

        [Test]
        public void EventCard_BlocksEntrance_RecoversWithoutRedraw_AndUsesGenericAnswer()
        {
            var state = State(); var handler = Handler();
            state.Map.ResourceTokens.RemoveAll(token => token.LocationId == "B-01");
            state.Decks.EventDeckGreen.Add("event_green_01");
            var result = Enter(state, handler, 1, "B-01");
            Assert.That(result.Succeeded, Is.True, result.Validation == null ? "" : result.Validation.Reason);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.Entrance));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(state.PendingChoice, Is.Null);
            Assert.That(state.PendingCardSession, Is.Null);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            var request = state.EffectRuntime.InteractionRequests.Find(item => item.Status == "open" && item.InteractionTypeId == EventCardEffectExecutor.OptionInteractionTypeId);
            Assert.That(request, Is.Not.Null);
            Assert.That(Enter(state, handler, 2, "A-01").Succeeded, Is.False);
            state = GameStateCloneService.DeepClone(state);
            handler = Handler();
            Assert.That(Round(handler).Advance(state).IsValid, Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.FindAll(node => node.EffectTypeId == EventCardEffectTypeIds.Resolve), Has.Count.EqualTo(1));
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = 1 };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.InteractionId;
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString();
            command.OptionIds.Add(request.CandidateIds[0]);
            result = new AnswerInteractionCommandHandler(handler.EffectRegistry, Round(handler)).Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.Validation == null ? "" : result.Validation.Reason);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(5));
            Assert.That(new AnswerInteractionCommandHandler(handler.EffectRegistry, Round(handler)).Handle(state, command).Succeeded, Is.False);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(5));
            Assert.That(state.EffectRuntime.RuleEvents.FindAll(e => e.EventType == PlayerEntranceEffectExecutor.EventType), Has.Count.EqualTo(1));
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
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            Assert.That(state.EffectRuntime.MainNodes.FindAll(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntrance),
                Has.None.Matches<MainlineNodeRuntimeState>(node => node.Status == EffectNodeStatus.Completed));
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
