using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Application.Setup;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class MultiplayerLaunchRegressionTests
    {
        [Test]
        public void HostLaunch_WithAllFourSeats_UsesOneTwoThreeFourForEntranceAndBothActionRounds()
        {
            var seats = CreateJoinedSeats();
            var localPlayerId = GameLaunchStateFactory.ResolveHostLocalPlayerId(
                -1,
                1,
                true,
                seats);

            var state = GameLaunchStateFactory.CreateInitialState(
                LaunchMode.Host,
                localPlayerId,
                seats,
                StaticMapDefinitions.FourPlayerMapId,
                EventDeckService.DefaultSeed);
            var setupHandler = new SetupCommandHandler(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));

            Assert.That(localPlayerId, Is.EqualTo(1));
            Assert.That(state.UseSeatTurnOrder, Is.True);
            Assert.That(state.StartPlayerId, Is.EqualTo(1));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(GetPlayerIds(state), Is.EqualTo(new[] { 1, 2, 3, 4 }));

            AssertInitialPlacement(setupHandler, state, 1, "G-01", 2, GamePhase.Entrance);
            AssertInitialPlacement(setupHandler, state, 2, "A-01", 3, GamePhase.Entrance);
            AssertInitialPlacement(setupHandler, state, 3, "A-02", 4, GamePhase.Entrance);
            AssertInitialPlacement(setupHandler, state, 4, "B-01", 1, GamePhase.ActionRound1);

            Assert.That(state.ActionRound, Is.EqualTo(1));
            AssertActionRoundOrder(state, GamePhase.ActionRound1, GamePhase.ActionRound2, 2);
            AssertActionRoundOrder(state, GamePhase.ActionRound2, GamePhase.ResourceCollection, 0);
        }

        private static List<PlayerSeat> CreateJoinedSeats()
        {
            return new List<PlayerSeat>
            {
                CreateSeat(1, PlayerColor.Blue),
                CreateSeat(2, PlayerColor.Red),
                CreateSeat(3, PlayerColor.Green),
                CreateSeat(4, PlayerColor.Yellow)
            };
        }

        private static PlayerSeat CreateSeat(int playerId, PlayerColor color)
        {
            return new PlayerSeat
            {
                PlayerId = playerId,
                PlayerName = "Player " + playerId,
                Color = color,
                IsReady = true
            };
        }

        private static int[] GetPlayerIds(GameState state)
        {
            var playerIds = new int[state.Players.Count];
            for (var i = 0; i < state.Players.Count; i++)
            {
                playerIds[i] = state.Players[i].PlayerId;
            }

            return playerIds;
        }

        private static void AssertInitialPlacement(
            SetupCommandHandler setupHandler,
            GameState state,
            int playerId,
            string locationId,
            int expectedCurrentPlayerId,
            GamePhase expectedPhase)
        {
            var result = setupHandler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = playerId,
                TargetId = locationId
            });

            Assert.That(result.Succeeded, Is.True, result.Validation == null ? string.Empty : result.Validation.Reason);
            Assert.That(state.FindPlayer(playerId).CityLocationId, Is.EqualTo(locationId));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(expectedCurrentPlayerId));
            Assert.That(state.Phase, Is.EqualTo(expectedPhase));
        }

        private static void AssertActionRoundOrder(
            GameState state,
            GamePhase expectedStartingPhase,
            GamePhase expectedEndingPhase,
            int expectedEndingActionRound)
        {
            var roundAdvanceService = new RoundAdvanceService();

            Assert.That(state.Phase, Is.EqualTo(expectedStartingPhase));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));

            roundAdvanceService.CompleteMainAction(state, 1);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            roundAdvanceService.CompleteMainAction(state, 2);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(3));
            roundAdvanceService.CompleteMainAction(state, 3);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(4));
            roundAdvanceService.CompleteMainAction(state, 4);

            Assert.That(state.Phase, Is.EqualTo(expectedEndingPhase));
            Assert.That(state.ActionRound, Is.EqualTo(expectedEndingActionRound));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
        }
    }
}
