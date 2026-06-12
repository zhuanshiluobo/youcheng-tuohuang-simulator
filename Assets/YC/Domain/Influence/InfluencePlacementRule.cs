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
            return ValidateCore(state, playerId, slotId, requireAvailableSupply);
        }

        public bool CanPlace(GameState state, int playerId, string slotId)
        {
            return Validate(state, playerId, slotId, true).Succeeded;
        }

        private InfluenceOperationResult ValidateCore(GameState state, int playerId, string slotId, bool requireAvailableSupply)
        {
            var player = state.FindPlayer(playerId);
            if (player == null)
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

            if (FindInfluenceAtSlot(state, slot.SlotId) != null)
            {
                return InfluenceOperationResult.Failure(
                    InfluenceFailureCode.OccupiedSlot,
                    "Influence slot is already occupied.",
                    playerId, slot.SlotId, false);
            }

            if (requireAvailableSupply && player.InfluenceSupply <= 0)
            {
                return InfluenceOperationResult.Failure(
                    InfluenceFailureCode.InsufficientSupply,
                    "Player has no available markers in supply.",
                    playerId, slot.SlotId, false);
            }

            if (slot.Kind == InfluenceSlotKind.Location)
            {
                if (!HasResourceToken(state, slot.LocationId))
                {
                    return InfluenceOperationResult.Failure(
                        InfluenceFailureCode.ResourceTokenRequired,
                        "Location influence slots require an existing resource token.",
                        playerId, slot.SlotId, false);
                }

                if (HasOpponentCity(state, playerId, slot.LocationId))
                {
                    return InfluenceOperationResult.Failure(
                        InfluenceFailureCode.OpponentCityPresent,
                        "Location has an opponent mobile city docked.",
                        playerId, slot.SlotId, false);
                }
            }
            else if (roadQuery.IsRouteCoveredByRoad(state, slot.RouteId))
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
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                if (state.Map.Influences[i].SlotId == slotId)
                {
                    return state.Map.Influences[i];
                }
            }

            return null;
        }

        private static bool HasResourceToken(GameState state, string locationId)
        {
            for (var i = 0; i < state.Map.ResourceTokens.Count; i++)
            {
                if (state.Map.ResourceTokens[i].LocationId == locationId)
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
