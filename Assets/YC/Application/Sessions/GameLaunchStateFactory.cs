using System;
using System.Collections.Generic;
using YC.Domain.CityStyles;
using YC.Domain.Cards;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public static class GameLaunchStateFactory
    {
        public static bool IsNetworkLaunch(LaunchMode mode)
        {
            return mode != LaunchMode.Local;
        }

        public static bool ContainsPlayer(IList<PlayerSeat> players, int playerId)
        {
            if (players == null || playerId <= 0)
            {
                return false;
            }

            for (var i = 0; i < players.Count; i++)
            {
                if (players[i].PlayerId == playerId)
                {
                    return true;
                }
            }

            return false;
        }

        public static int ResolveHostLocalPlayerId(
            int localPlayerId,
            int hostPlayerId,
            bool isHost,
            IList<PlayerSeat> players)
        {
            if (ContainsPlayer(players, localPlayerId))
            {
                return localPlayerId;
            }

            if (isHost && ContainsPlayer(players, hostPlayerId))
            {
                return hostPlayerId;
            }

            return localPlayerId;
        }

        public static GameState CreateInitialState(
            LaunchMode mode,
            int localPlayerId,
            IList<PlayerSeat> players,
            string mapId,
            int eventDeckSeed)
        {
            var startPlayerId = GetStartPlayerId(mode, players);
            var state = new GameState
            {
                Phase = GamePhase.Entrance,
                StartPlayerId = startPlayerId,
                CurrentPlayerId = startPlayerId,
                UseSeatTurnOrder = IsNetworkLaunch(mode),
                MapId = mapId ?? string.Empty,
                EventDeckSeed = eventDeckSeed
            };

            AddPlayers(state, localPlayerId, players);
            ScoreTrackService.InitializePlayerMarkers(state);
            for (var i = 0; i < state.Players.Count; i++)
            {
                CharacterCardDatabase.InitializePlayerHand(state.Players[i]);
            }
            BuildFacilityService.EnsureInitialCoreCommandTowers(state);
            InitializeFacilityMarket(state, eventDeckSeed);
            state.Decks.CityStyleSupply.AddRange(CityStyleDatabase.DefaultSupplyIds);
            return state;
        }

        private static void InitializeFacilityMarket(GameState state, int seed)
        {
            FacilitySupplyService.Initialize(
                state.Decks,
                FacilityCardDatabase.DefaultSupplyIds,
                seed);
        }

        private static int GetStartPlayerId(LaunchMode mode, IList<PlayerSeat> players)
        {
            if (IsNetworkLaunch(mode))
            {
                return 1;
            }

            if (players != null && players.Count > 0)
            {
                return players[0].PlayerId;
            }

            return 1;
        }

        private static void AddPlayers(GameState state, int localPlayerId, IList<PlayerSeat> players)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (players != null && players.Count > 0)
            {
                for (var i = 0; i < players.Count; i++)
                {
                    var seat = players[i];
                    state.Players.Add(new PlayerState
                    {
                        PlayerId = seat.PlayerId,
                        Name = string.IsNullOrEmpty(seat.PlayerName) ? "Player " + seat.PlayerId : seat.PlayerName,
                        Color = seat.Color
                    });
                }

                return;
            }

            state.Players.Add(new PlayerState
            {
                PlayerId = localPlayerId,
                Name = "Player " + localPlayerId,
                Color = YC.Domain.Rules.PlayerColor.Blue
            });
        }
    }
}
