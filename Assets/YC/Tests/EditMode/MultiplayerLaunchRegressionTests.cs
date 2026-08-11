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
            Assert.That(GameLaunchStateFactory.ContainsPlayer(seats, -1), Is.False);

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
            Assert.That(GameLaunchStateFactory.ContainsPlayer(seats, localPlayerId), Is.True);
            Assert.That(state.UseSeatTurnOrder, Is.True);
            Assert.That(state.StartPlayerId, Is.EqualTo(1));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(GetPlayerIds(state), Is.EqualTo(new[] { 1, 2, 3, 4 }));
            Assert.That(state.Players, Has.All.Matches<PlayerState>(player => player.HasScoreTrackMarker));
            Assert.That(state.Players, Has.All.Matches<PlayerState>(player => player.InfluenceSupply == 29));

            AssertInitialPlacement(setupHandler, state, 1, "G-01", 2, GamePhase.Entrance);
            AssertInitialPlacement(setupHandler, state, 2, "A-01", 3, GamePhase.Entrance);
            AssertInitialPlacement(setupHandler, state, 3, "A-02", 4, GamePhase.Entrance);
            AssertInitialPlacement(setupHandler, state, 4, "B-01", 1, GamePhase.CharacterCover);
            CoverAllPlayers(state);

            Assert.That(state.ActionRound, Is.EqualTo(1));
            AssertActionRoundOrder(state, GamePhase.ActionRound1, GamePhase.ActionRound2, 2);
            AssertActionRoundOrder(state, GamePhase.ActionRound2, GamePhase.ResourceCollection, 0);
            EndResourceCollectionAndCleanup(state);
            CoverAllPlayers(state);
            Assert.That(state.Round, Is.EqualTo(2));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
            Assert.That(state.ActionRound, Is.EqualTo(1));
        }

        [Test]
        public void HostLaunch_FourPlayers_AdvancesToRound4AndSynchronizesRedZoneAndRoundTrack()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var state = CreatePlacedFourPlayerState(map);

            Assert.That(RedZoneAccessRule.GetOpenRound(map, state.Players.Count), Is.EqualTo(4));
            Assert.That(RoundTrackRule.GetRoundIndex(state), Is.EqualTo(1));

            AdvanceFullRound(state);
            Assert.That(state.Round, Is.EqualTo(2));
            Assert.That(RoundTrackRule.GetRoundIndex(state), Is.EqualTo(2));

            AdvanceFullRound(state);
            Assert.That(state.Round, Is.EqualTo(3));
            Assert.That(RoundTrackRule.GetRoundIndex(state), Is.EqualTo(3));
            AssertRedZoneClosed(state, map, true);

            AdvanceFullRound(state);
            Assert.That(state.Round, Is.EqualTo(4));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
            Assert.That(state.ActionRound, Is.EqualTo(1));
            Assert.That(state.StartPlayerId, Is.EqualTo(4));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(4));
            Assert.That(RoundTrackRule.GetRoundIndex(state), Is.EqualTo(4));
            AssertRedZoneClosed(state, map, false);
        }

        [Test]
        public void HostLaunch_FourPlayers_EndsOnlyAfterRound8Cleanup()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var state = CreatePlacedFourPlayerState(map);
            var roundAdvanceService = new RoundAdvanceService();

            for (var round = 1; round < state.MaxRounds; round++)
            {
                AdvanceFullRound(state);
            }

            Assert.That(state.Round, Is.EqualTo(8));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
            Assert.That(state.ActionRound, Is.EqualTo(1));
            Assert.That(RoundTrackRule.GetRoundIndex(state), Is.EqualTo(8));

            roundAdvanceService.CompleteMainAction(state, 4);
            roundAdvanceService.CompleteMainAction(state, 1);
            roundAdvanceService.CompleteMainAction(state, 2);
            roundAdvanceService.CompleteMainAction(state, 3);

            Assert.That(state.Round, Is.EqualTo(8));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound2));
            Assert.That(state.ActionRound, Is.EqualTo(2));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(4));
            Assert.That(RoundTrackRule.GetRoundIndex(state), Is.EqualTo(8));

            roundAdvanceService.CompleteMainAction(state, 4);
            roundAdvanceService.CompleteMainAction(state, 1);
            roundAdvanceService.CompleteMainAction(state, 2);

            Assert.That(state.Round, Is.EqualTo(8));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound2));
            Assert.That(state.ActionRound, Is.EqualTo(2));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(3));
            Assert.That(RoundTrackRule.GetRoundIndex(state), Is.EqualTo(8));

            roundAdvanceService.CompleteMainAction(state, 3);

            Assert.That(state.Round, Is.EqualTo(8));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            Assert.That(state.ActionRound, Is.EqualTo(0));
            Assert.That(RoundTrackRule.GetRoundIndex(state), Is.EqualTo(8));

            EndResourceCollectionAndCleanup(state);

            Assert.That(state.Round, Is.EqualTo(8));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.FinalScoring));
            Assert.That(state.ActionRound, Is.EqualTo(0));
            Assert.That(RoundTrackRule.GetRoundIndex(state), Is.EqualTo(RoundTrackRule.FinalIndex));
        }

        [Test]
        public void HostLaunch_TwoPlayerSteamValidation_UsesTwoSeatsAndAdvancesBothActionRounds()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var seats = new List<PlayerSeat>
            {
                CreateSeat(1, PlayerColor.Blue),
                CreateSeat(2, PlayerColor.Red)
            };
            var state = GameLaunchStateFactory.CreateInitialState(
                LaunchMode.Host,
                1,
                seats,
                map.MapId,
                EventDeckService.DefaultSeed);
            var setupHandler = new SetupCommandHandler(new MapQueryService(map));

            Assert.That(state.MapId, Is.EqualTo(StaticMapDefinitions.FourPlayerMapId));
            Assert.That(GetPlayerIds(state), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(state.UseSeatTurnOrder, Is.True);

            AssertInitialPlacement(setupHandler, state, 1, "G-01", 2, GamePhase.Entrance);
            AssertInitialPlacement(setupHandler, state, 2, "A-01", 1, GamePhase.CharacterCover);
            CoverAllPlayers(state);
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

        private static GameState CreatePlacedFourPlayerState(GameMapDefinition map)
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
            var setupHandler = new SetupCommandHandler(new MapQueryService(map));

            AssertInitialPlacement(setupHandler, state, 1, "G-01", 2, GamePhase.Entrance);
            AssertInitialPlacement(setupHandler, state, 2, "A-01", 3, GamePhase.Entrance);
            AssertInitialPlacement(setupHandler, state, 3, "A-02", 4, GamePhase.Entrance);
            AssertInitialPlacement(setupHandler, state, 4, "B-01", 1, GamePhase.CharacterCover);
            CoverAllPlayers(state);
            return state;
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
            var order = new List<int>(new TurnOrderService().GetTurnOrder(state));

            Assert.That(state.Phase, Is.EqualTo(expectedStartingPhase));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(order[0]));

            for (var i = 0; i < order.Count; i++)
            {
                roundAdvanceService.CompleteMainAction(state, order[i]);
                if (i < order.Count - 1)
                {
                    Assert.That(state.CurrentPlayerId, Is.EqualTo(order[i + 1]));
                }
            }

            Assert.That(state.Phase, Is.EqualTo(expectedEndingPhase));
            Assert.That(state.ActionRound, Is.EqualTo(expectedEndingActionRound));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(order[0]));
        }

        private static void AdvanceFullRound(GameState state)
        {
            AssertActionRoundOrder(state, GamePhase.ActionRound1, GamePhase.ActionRound2, 2);
            AssertActionRoundOrder(state, GamePhase.ActionRound2, GamePhase.ResourceCollection, 0);
            EndResourceCollectionAndCleanup(state);
            if (state.Phase == GamePhase.CharacterCover)
            {
                CoverAllPlayers(state);
            }
        }

        private static void CoverAllPlayers(GameState state)
        {
            var service = new CharacterCardService();
            var submitted = 0;
            while (state.Phase == GamePhase.CharacterCover && submitted < state.Players.Count)
            {
                var player = state.FindPlayer(state.CurrentPlayerId);
                Assert.That(player, Is.Not.Null);
                var cardId = player.HandCardIds.Count > 0
                    ? player.HandCardIds[0]
                    : player.DiscardCardIds[0];
                var result = service.Cover(state, player.PlayerId, cardId);
                Assert.That(result.IsValid, Is.True, result.Reason);
                submitted++;
            }

            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
        }

        private static void EndResourceCollectionAndCleanup(GameState state)
        {
            var roundAdvanceService = new RoundAdvanceService();
            for (var i = 0; i < state.Players.Count; i++)
            {
                state.Players[i].HasCollectedResourcesThisRound = true;
            }

            roundAdvanceService.AdvanceResourceCollectionToCleanup(state);
            var result = roundAdvanceService.EndCompletedAction(state, state.CurrentPlayerId);
            Assert.That(result.IsValid, Is.True, result.Reason);
        }

        private static void AssertRedZoneClosed(GameState state, GameMapDefinition map, bool expectedClosed)
        {
            var mapQuery = new MapQueryService(map);
            foreach (var locationId in StaticMapDefinitions.FourPlayerRedZoneLocationIds)
            {
                var location = mapQuery.GetLocation(locationId);
                Assert.That(location, Is.Not.Null, locationId);
                Assert.That(
                    RedZoneAccessRule.IsClosed(state, map, location),
                    Is.EqualTo(expectedClosed),
                    locationId);
            }
        }
    }
}
