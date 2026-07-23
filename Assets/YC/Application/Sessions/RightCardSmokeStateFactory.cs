using System;
using System.Collections.Generic;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public static class RightCardSmokeStateFactory
    {
        public const string DefaultCityLocationId = "G-01";
        public const string SharedCityStyleId = CityStyleDatabase.MilitaryIndustrialArea;

        private static readonly string[] SharedStyleCityLocationIds =
        {
            "G-01",
            "A-01",
            "A-02",
            "B-01"
        };

        public static GameState CreateInitialState(
            LaunchMode mode,
            int localPlayerId,
            IList<PlayerSeat> players,
            string mapId,
            int eventDeckSeed,
            bool prepareSharedCityStyle = false)
        {
            var state = GameLaunchStateFactory.CreateInitialState(
                mode,
                localPlayerId,
                players,
                mapId,
                eventDeckSeed);
            var player = state.FindPlayer(localPlayerId);
            if (player == null && state.Players.Count > 0)
            {
                player = state.Players[0];
            }

            if (player == null)
            {
                throw new InvalidOperationException("Right card smoke state requires at least one player.");
            }

            PrepareState(state, player);
            if (prepareSharedCityStyle)
            {
                PrepareSharedCityStyleState(state);
            }
            return state;
        }

        private static void PrepareState(GameState state, PlayerState player)
        {
            state.Round = Math.Max(1, state.Round);
            state.Phase = GamePhase.ActionRound1;
            state.ActionRound = 1;
            state.StartPlayerId = player.PlayerId;
            state.CurrentPlayerId = player.PlayerId;
            state.PendingChoice = null;
            state.PendingCardSession = null;

            if (string.IsNullOrEmpty(player.CityLocationId))
            {
                player.CityLocationId = DefaultCityLocationId;
            }

            player.ActedMainActionThisTurn = false;
            player.HasMovedCityThisRound = false;
            player.HasCollectedResourcesThisRound = false;
            player.Resources.Originium = Math.Max(player.Resources.Originium, 6);
            player.Resources.OriginiumShard = Math.Max(player.Resources.OriginiumShard, 6);
            player.Resources.Iron = Math.Max(player.Resources.Iron, 6);
            player.Resources.PureOriginium = Math.Max(player.Resources.PureOriginium, 2);
            player.Resources.GoldVoucher = Math.Max(player.Resources.GoldVoucher, 30);

            if (!state.Map.OpenLocationIds.Contains(player.CityLocationId))
            {
                state.Map.OpenLocationIds.Add(player.CityLocationId);
            }

            if (!state.Decks.FacilitySupply.Contains(FacilityCardDatabase.SimpleEngineeringCamp))
            {
                state.Decks.FacilitySupply.Insert(0, FacilityCardDatabase.SimpleEngineeringCamp);
            }

            if (state.Decks.CityStyleSupply.Count <= 0)
            {
                state.Decks.CityStyleSupply.AddRange(CityStyleDatabase.DefaultSupplyIds);
            }
        }

        private static void PrepareSharedCityStyleState(GameState state)
        {
            var cityStyle = CityStyleDatabase.Get(SharedCityStyleId);
            if (cityStyle == null)
            {
                throw new InvalidOperationException("Shared city style smoke state requires a known city style.");
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (player == null)
                {
                    continue;
                }

                if (i < SharedStyleCityLocationIds.Length)
                {
                    player.CityLocationId = SharedStyleCityLocationIds[i];
                    if (!state.Map.OpenLocationIds.Contains(player.CityLocationId))
                    {
                        state.Map.OpenLocationIds.Add(player.CityLocationId);
                    }
                }

                player.InfluenceSupply = Math.Max(0, player.InfluenceSupply - 1);
                player.DeclaredCityStyleIds.Add(SharedCityStyleId);
                player.DeclaredCityStyles.Add(new CityStyleDeclarationState
                {
                    InfluenceMarkerId = SharedCityStyleId + ":" + player.PlayerId + ":1",
                    CityStyleId = SharedCityStyleId,
                    MarkerArea = CityStyleMarkerAreas.Unused,
                    UnlockedSpecialActionId = cityStyle.SpecialActionId ?? string.Empty,
                    RemainingSpecialActionUses = 1
                });
            }
        }
    }
}
