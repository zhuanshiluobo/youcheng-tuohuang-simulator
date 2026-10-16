using System;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Influence
{
    public sealed class InfluencePlacementRule
    {
        private readonly IMapQueryService mapQuery;
        private readonly IInfluenceRoadCoverageQuery roadQuery;

        public InfluencePlacementRule(IMapQueryService mapQuery, IInfluenceRoadCoverageQuery roadQuery)
        {
            _ = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.mapQuery = mapQuery;
            this.roadQuery = roadQuery ?? new NoInfluenceRoadCoverageQuery();
        }

        public InfluencePlacementRule()
            : this(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()), new NoInfluenceRoadCoverageQuery())
        {
        }

        public InfluenceOperationResult Validate(GameState state, int playerId, string slotId, bool requireAvailableSupply)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return ValidateCore(state, playerId, slotId, requireAvailableSupply, null);
        }

        internal InfluencePlacementQuerySnapshot CreateQuerySnapshot(GameState state, int playerId) =>
            new InfluencePlacementQuerySnapshot(state, playerId, mapQuery, roadQuery);

        internal InfluenceOperationResult Validate(GameState state, int playerId, string slotId,
            bool requireAvailableSupply, InfluencePlacementQuerySnapshot snapshot) =>
            ValidateCore(state, playerId, slotId, requireAvailableSupply, snapshot);

        public bool CanPlace(GameState state, int playerId, string slotId)
        {
            return Validate(state, playerId, slotId, true).Succeeded;
        }

        private InfluenceOperationResult ValidateCore(GameState state, int playerId, string slotId,
            bool requireAvailableSupply, InfluencePlacementQuerySnapshot snapshot)
        {
            var player = snapshot == null ? state.FindPlayer(playerId) : null;
            if (snapshot == null ? player == null : !snapshot.PlayerExists)
            {
                return InfluenceOperationResult.Failure(
                    InfluenceFailureCode.InvalidPlayer,
                    "Player must exist before placing influence.",
                    playerId, slotId, false);
            }

            InfluenceSlotReference slot;
            string reason;
            if (!InfluenceSlotReference.TryParse(mapQuery, slotId, out slot, out reason))
            {
                return InfluenceOperationResult.Failure(
                    InfluenceFailureCode.InvalidSlot,
                    reason,
                    playerId, slotId, false);
            }

            if (snapshot == null ? FindInfluenceAtSlot(state, slot.SlotId) != null : snapshot.IsOccupied(slot.SlotId))
            {
                return InfluenceOperationResult.Failure(
                    InfluenceFailureCode.OccupiedSlot,
                    "Influence slot is already occupied.",
                    playerId, slot.SlotId, false);
            }

            if (requireAvailableSupply && (snapshot == null ? player.InfluenceSupply : snapshot.InfluenceSupply) <= 0)
            {
                return InfluenceOperationResult.Failure(
                    InfluenceFailureCode.InsufficientSupply,
                    "Player has no available markers in supply.",
                    playerId, slot.SlotId, false);
            }

            if (slot.Kind == InfluenceSlotKind.Location)
            {
                if (!(snapshot == null ? HasResourceToken(state, slot.LocationId) : snapshot.HasResourceToken(slot.LocationId)))
                {
                    return InfluenceOperationResult.Failure(
                        InfluenceFailureCode.ResourceTokenRequired,
                        "Location influence slots require an existing resource token.",
                        playerId, slot.SlotId, false);
                }

                if (snapshot == null ? HasOpponentCity(state, playerId, slot.LocationId) : snapshot.HasOpponentCity(slot.LocationId))
                {
                    return InfluenceOperationResult.Failure(
                        InfluenceFailureCode.OpponentCityPresent,
                        "Location has an opponent mobile city docked.",
                        playerId, slot.SlotId, false);
                }
            }
            else if (snapshot == null ? roadQuery.IsRouteCoveredByRoad(state, slot.RouteId) : snapshot.IsRouteCovered(slot.RouteId))
            {
                return InfluenceOperationResult.Failure(
                    InfluenceFailureCode.RouteCoveredByRoad,
                    "Route influence slots cannot be used when covered by a road.",
                    playerId, slot.SlotId, false);
            }

            return InfluenceOperationResult.Success(playerId, slot.SlotId, false);
        }

        private static InfluencePlacement FindInfluenceAtSlot(GameState state, string slotId)
        {
            var influences = state.Map.Influences.ToArray();
            for (var i = 0; i < influences.Length; i++)
            {
                if (influences[i].SlotId == slotId)
                {
                    return influences[i];
                }
            }

            return null;
        }

        private static bool HasResourceToken(GameState state, string locationId)
        {
            var tokens = state.Map.ResourceTokens.ToArray();
            for (var i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].LocationId == locationId)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasOpponentCity(GameState state, int playerId, string locationId)
        {
            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (player.PlayerId != playerId && player.CityLocationId == locationId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
