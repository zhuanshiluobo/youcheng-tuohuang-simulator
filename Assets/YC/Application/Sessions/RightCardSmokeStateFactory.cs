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

        public static GameState CreateInitialState(
            LaunchMode mode,
            int localPlayerId,
            IList<PlayerSeat> players,
            string mapId,
            int eventDeckSeed)
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
    }
}
