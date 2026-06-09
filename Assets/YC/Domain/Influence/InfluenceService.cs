using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Influence
{
    public sealed class InfluenceService
    {
        private readonly GameState boundState;
        private readonly IMapQueryService mapQuery;
        private readonly IInfluenceRoadCoverageQuery roadCoverageQuery;

        public InfluenceService(IMapQueryService mapQuery)
            : this(null, mapQuery, new RuntimeStateRoadCoverageQuery())
        {
        }

        public InfluenceService(GameState state, IMapQueryService mapQuery)
            : this(state, mapQuery, new RuntimeStateRoadCoverageQuery())
        {
        }

        public InfluenceService(
            GameState state,
            IMapQueryService mapQuery,
            IInfluenceRoadCoverageQuery roadCoverageQuery)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            boundState = state;
            this.roadCoverageQuery = roadCoverageQuery ?? new NoInfluenceRoadCoverageQuery();
        }

        public ValidationResult CanPlace(GameState state, int playerId, string slotId)
        {
            return ToValidationResult(CanPlaceCore(state, playerId, slotId));
        }

        public InfluenceOperationResult Place(GameState state, int playerId, string slotId)
        {
            return PlaceCore(state, playerId, slotId);
        }

        public ValidationResult CanMove(GameState state, int playerId, string sourceInfluenceId, string targetSlotId)
        {
            return ToValidationResult(CanMoveCore(state, playerId, sourceInfluenceId, targetSlotId));
        }

        public InfluenceOperationResult Move(GameState state, int playerId, string sourceInfluenceId, string targetSlotId)
        {
            return MoveCore(state, playerId, sourceInfluenceId, targetSlotId);
        }

        public InfluenceOperationResult Remove(GameState state, string influenceId)
        {
            return RemoveCore(state, influenceId);
        }

        public InfluenceOperationResult Replace(GameState state, string targetInfluenceId, int newOwnerPlayerId)
        {
            return ReplaceCore(state, targetInfluenceId, newOwnerPlayerId);
        }

        public int CountInRegion(GameState state, int playerId, string regionId, bool includeCity)
        {
            return CountInRegionCore(state, playerId, regionId, includeCity);
        }

        public InfluenceOperationResult CanPlace(int playerId, string slotId)
        {
            return CanPlaceCore(GetBoundState(), playerId, slotId);
        }

        public InfluenceOperationResult Place(int playerId, string slotId)
        {
            return PlaceCore(GetBoundState(), playerId, slotId);
        }

        public InfluenceOperationResult CanMove(string influenceId, string targetSlotId)
        {
            var state = GetBoundState();
            var placement = FindInfluence(state, influenceId);
            if (placement == null)
            {
                return Failure(InfluenceFailureCode.InfluenceNotFound, "Source influence must exist before it can move.", -1, influenceId, false);
            }

            return CanMoveCore(state, placement.PlayerId, influenceId, targetSlotId);
        }

        public InfluenceOperationResult CanMove(int playerId, string sourceInfluenceId, string targetSlotId)
        {
            return CanMoveCore(GetBoundState(), playerId, sourceInfluenceId, targetSlotId);
        }

        public InfluenceOperationResult Move(string influenceId, string targetSlotId)
        {
            var state = GetBoundState();
            var placement = FindInfluence(state, influenceId);
            if (placement == null)
            {
                return Failure(InfluenceFailureCode.InfluenceNotFound, "Source influence must exist before it can move.", -1, influenceId, false);
            }

            return MoveCore(state, placement.PlayerId, influenceId, targetSlotId);
        }

        public InfluenceOperationResult Move(int playerId, string sourceInfluenceId, string targetSlotId)
        {
            return MoveCore(GetBoundState(), playerId, sourceInfluenceId, targetSlotId);
        }

        public InfluenceOperationResult Remove(string influenceId)
        {
            return RemoveCore(GetBoundState(), influenceId);
        }

        public InfluenceOperationResult Replace(string targetInfluenceId, int newOwnerPlayerId)
        {
            return ReplaceCore(GetBoundState(), targetInfluenceId, newOwnerPlayerId);
        }

        public int CountInRegion(int playerId, string regionId, bool includeCity)
        {
            return CountInRegionCore(GetBoundState(), playerId, regionId, includeCity);
        }

        public bool HasInfluenceAt(GameState state, string slotId)
        {
            return FindInfluence(state, slotId) != null;
        }

        public bool HasInfluenceAt(string slotId)
        {
            return FindInfluence(GetBoundState(), slotId) != null;
        }

        public static string GetLocationSlotId(string locationId, int index)
        {
            return InfluenceSlotReference.ForLocation(locationId, index).SlotId;
        }

        public static string GetRouteSlotId(string routeId, int index)
        {
            return InfluenceSlotReference.ForRoute(routeId, index).SlotId;
        }

        public InfluencePlacement FindInfluence(GameState state, string influenceId)
        {
            ValidateState(state);

            if (string.IsNullOrEmpty(influenceId))
            {
                return null;
            }

            InfluenceSlotReference requestedSlot;
            string ignoredReason;
            var requestedSlotValid = InfluenceSlotReference.TryParse(
                mapQuery,
                influenceId,
                out requestedSlot,
                out ignoredReason);

            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var placement = state.Map.Influences[i];
                if (placement.SlotId == influenceId)
                {
                    return placement;
                }

                if (!requestedSlotValid)
                {
                    continue;
                }

                InfluenceSlotReference placedSlot;
                if (InfluenceSlotReference.TryParse(mapQuery, placement.SlotId, out placedSlot, out ignoredReason) &&
                    placedSlot.SlotId == requestedSlot.SlotId)
                {
                    return placement;
                }
            }

            return null;
        }

        public InfluencePlacement FindInfluence(string influenceId)
        {
            return FindInfluence(GetBoundState(), influenceId);
        }

        private InfluenceOperationResult CanPlaceCore(GameState state, int playerId, string slotId)
        {
            ValidateState(state);
            InfluenceSlotReference slot;
            var slotResult = TryResolveSlot(slotId, playerId, out slot);
            if (!slotResult.Succeeded)
            {
                return slotResult;
            }

            return ValidateDestination(state, playerId, slot, true);
        }

        private InfluenceOperationResult PlaceCore(GameState state, int playerId, string slotId)
        {
            ValidateState(state);
            InfluenceSlotReference slot;
            var slotResult = TryResolveSlot(slotId, playerId, out slot);
            if (!slotResult.Succeeded)
            {
                return slotResult;
            }

            var validation = ValidateDestination(state, playerId, slot, true);
            if (!validation.Succeeded)
            {
                return validation;
            }

            var player = state.FindPlayer(playerId);
            player.InfluenceSupply -= 1;
            state.Map.Influences.Add(CreatePlacement(playerId, slot));
            return InfluenceOperationResult.Success(playerId, slot.SlotId, true);
        }

        private InfluenceOperationResult CanMoveCore(GameState state, int playerId, string sourceInfluenceId, string targetSlotId)
        {
            ValidateState(state);
            var placement = FindInfluence(state, sourceInfluenceId);
            if (placement == null)
            {
                return Failure(InfluenceFailureCode.InfluenceNotFound, "Source influence must exist before it can move.", playerId, sourceInfluenceId, false);
            }

            if (placement.PlayerId != playerId)
            {
                return Failure(InfluenceFailureCode.InfluenceOwnerMismatch, "Only the owner can move this influence.", playerId, sourceInfluenceId, false);
            }

            InfluenceSlotReference targetSlot;
            var slotResult = TryResolveSlot(targetSlotId, playerId, out targetSlot);
            if (!slotResult.Succeeded)
            {
                return slotResult;
            }

            return ValidateDestination(state, playerId, targetSlot, false);
        }

        private InfluenceOperationResult MoveCore(GameState state, int playerId, string sourceInfluenceId, string targetSlotId)
        {
            ValidateState(state);
            var validation = CanMoveCore(state, playerId, sourceInfluenceId, targetSlotId);
            if (!validation.Succeeded)
            {
                return validation;
            }

            InfluenceSlotReference targetSlot;
            TryResolveSlot(targetSlotId, playerId, out targetSlot);

            var placement = FindInfluence(state, sourceInfluenceId);
            ApplySlot(placement, targetSlot);
            return InfluenceOperationResult.Success(playerId, targetSlot.SlotId, true);
        }

        private InfluenceOperationResult RemoveCore(GameState state, string influenceId)
        {
            ValidateState(state);
            var placement = FindInfluence(state, influenceId);
            if (placement == null)
            {
                return Failure(InfluenceFailureCode.InfluenceNotFound, "Influence must exist before it can be removed.", -1, influenceId, false);
            }

            var playerId = placement.PlayerId;
            var player = state.FindPlayer(playerId);
            var slotId = placement.SlotId;
            state.Map.Influences.Remove(placement);

            if (player != null)
            {
                player.InfluenceSupply += 1;
            }

            return InfluenceOperationResult.Success(playerId, slotId, true);
        }

        private InfluenceOperationResult ReplaceCore(GameState state, string targetInfluenceId, int newOwnerPlayerId)
        {
            ValidateState(state);
            var target = FindInfluence(state, targetInfluenceId);
            if (target == null)
            {
                return Failure(InfluenceFailureCode.InfluenceNotFound, "Target influence must exist before it can be replaced.", newOwnerPlayerId, targetInfluenceId, false);
            }

            if (state.FindPlayer(newOwnerPlayerId) == null)
            {
                return Failure(InfluenceFailureCode.InvalidPlayer, "Replacement owner must be an existing player.", newOwnerPlayerId, targetInfluenceId, false);
            }

            InfluenceSlotReference targetSlot;
            var slotResult = TryResolveSlot(target.SlotId, newOwnerPlayerId, out targetSlot);
            if (!slotResult.Succeeded)
            {
                return slotResult;
            }

            var oldOwner = state.FindPlayer(target.PlayerId);
            state.Map.Influences.Remove(target);
            if (oldOwner != null)
            {
                oldOwner.InfluenceSupply += 1;
            }

            var placementValidation = ValidateDestination(state, newOwnerPlayerId, targetSlot, true);
            if (!placementValidation.Succeeded)
            {
                return Failure(
                    placementValidation.FailureCode,
                    placementValidation.Reason,
                    newOwnerPlayerId,
                    targetSlot.SlotId,
                    true);
            }

            var newOwner = state.FindPlayer(newOwnerPlayerId);
            newOwner.InfluenceSupply -= 1;
            state.Map.Influences.Add(CreatePlacement(newOwnerPlayerId, targetSlot));
            return InfluenceOperationResult.Success(newOwnerPlayerId, targetSlot.SlotId, true);
        }

        private int CountInRegionCore(GameState state, int playerId, string regionId, bool includeCity)
        {
            ValidateState(state);
            var region = FindRegion(regionId);
            if (region == null)
            {
                throw new ArgumentException("Unknown map region id.", nameof(regionId));
            }

            var count = 0;
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var placement = state.Map.Influences[i];
                if (placement.PlayerId != playerId)
                {
                    continue;
                }

                if (IsPlacementInRegion(placement, region))
                {
                    count += 1;
                }
            }

            if (includeCity)
            {
                var player = state.FindPlayer(playerId);
                if (player != null && IsLocationInRegion(player.CityLocationId, region))
                {
                    count += 2;
                }
            }

            return count;
        }

        private InfluenceOperationResult TryResolveSlot(
            string slotId,
            int playerId,
            out InfluenceSlotReference slot)
        {
            string reason;
            if (InfluenceSlotReference.TryParse(mapQuery, slotId, out slot, out reason))
            {
                return InfluenceOperationResult.Success(playerId, slot.SlotId, false);
            }

            return InfluenceOperationResult.Failure(
                InfluenceFailureCode.InvalidSlot,
                reason,
                playerId,
                slotId,
                false);
        }

        private InfluenceOperationResult ValidateDestination(
            GameState state,
            int playerId,
            InfluenceSlotReference slot,
            bool requireAvailableSupply)
        {
            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return InfluenceOperationResult.Failure(
                    InfluenceFailureCode.InvalidPlayer,
                    "Player must exist before placing influence.",
                    playerId,
                    slot.SlotId,
                    false);
            }

            if (FindInfluence(state, slot.SlotId) != null)
            {
                return InfluenceOperationResult.Failure(
                    InfluenceFailureCode.OccupiedSlot,
                    "Influence slot is already occupied.",
                    playerId,
                    slot.SlotId,
                    false);
            }

            if (requireAvailableSupply && player.InfluenceSupply <= 0)
            {
                return InfluenceOperationResult.Failure(
                    InfluenceFailureCode.InsufficientSupply,
                    "Player has no available markers in supply.",
                    playerId,
                    slot.SlotId,
                    false);
            }

            if (slot.Kind == InfluenceSlotKind.Location)
            {
                if (!HasResourceToken(state, slot.LocationId))
                {
                    return InfluenceOperationResult.Failure(
                        InfluenceFailureCode.ResourceTokenRequired,
                        "Location influence slots require an existing resource token.",
                        playerId,
                        slot.SlotId,
                        false);
                }

                if (HasOpponentCity(state, playerId, slot.LocationId))
                {
                    return InfluenceOperationResult.Failure(
                        InfluenceFailureCode.OpponentCityPresent,
                        "Location has an opponent mobile city docked.",
                        playerId,
                        slot.SlotId,
                        false);
                }
            }
            else if (roadCoverageQuery.IsRouteCoveredByRoad(state, slot.RouteId))
            {
                return InfluenceOperationResult.Failure(
                    InfluenceFailureCode.RouteCoveredByRoad,
                    "Route influence slots cannot be used when covered by a road.",
                    playerId,
                    slot.SlotId,
                    false);
            }

            return InfluenceOperationResult.Success(playerId, slot.SlotId, false);
        }

        private bool HasResourceToken(GameState state, string locationId)
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

        private MapRegionDefinition FindRegion(string regionId)
        {
            for (var i = 0; i < mapQuery.Map.Regions.Count; i++)
            {
                if (mapQuery.Map.Regions[i].RegionId == regionId)
                {
                    return mapQuery.Map.Regions[i];
                }
            }

            return null;
        }

        private bool IsPlacementInRegion(InfluencePlacement placement, MapRegionDefinition region)
        {
            if (!string.IsNullOrEmpty(placement.LocationId))
            {
                return IsLocationInRegion(placement.LocationId, region);
            }

            if (!string.IsNullOrEmpty(placement.RouteId))
            {
                return IsRouteInRegion(placement.RouteId, region);
            }

            InfluenceSlotReference slot;
            string reason;
            if (!InfluenceSlotReference.TryParse(mapQuery, placement.SlotId, out slot, out reason))
            {
                return false;
            }

            return slot.Kind == InfluenceSlotKind.Location
                ? IsLocationInRegion(slot.LocationId, region)
                : IsRouteInRegion(slot.RouteId, region);
        }

        private bool IsLocationInRegion(string locationId, MapRegionDefinition region)
        {
            if (string.IsNullOrEmpty(locationId))
            {
                return false;
            }

            try
            {
                var location = mapQuery.GetLocation(locationId);
                if (location.RegionId == region.RegionId)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }

            return region.LocationIds.Contains(locationId);
        }

        private bool IsRouteInRegion(string routeId, MapRegionDefinition region)
        {
            MapRouteDefinition route;
            try
            {
                route = mapQuery.GetRoute(routeId);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(route.RegionId))
            {
                return route.RegionId == region.RegionId;
            }

            if (region.RouteIds.Contains(routeId))
            {
                return true;
            }

            return IsLocationInRegion(route.FromLocationId, region)
                && IsLocationInRegion(route.ToLocationId, region);
        }

        private static InfluencePlacement CreatePlacement(int playerId, InfluenceSlotReference slot)
        {
            var placement = new InfluencePlacement { PlayerId = playerId };
            ApplySlot(placement, slot);
            return placement;
        }

        private static void ApplySlot(InfluencePlacement placement, InfluenceSlotReference slot)
        {
            placement.SlotId = slot.SlotId;
            placement.LocationId = slot.Kind == InfluenceSlotKind.Location ? slot.LocationId : string.Empty;
            placement.RouteId = slot.Kind == InfluenceSlotKind.Route ? slot.RouteId : string.Empty;
        }

        private GameState GetBoundState()
        {
            if (boundState == null)
            {
                throw new InvalidOperationException("This InfluenceService method requires a GameState-bound service. Use the overload that accepts GameState.");
            }

            return boundState;
        }

        private static void ValidateState(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (state.Map == null)
            {
                throw new ArgumentException("GameState must include map runtime state.", nameof(state));
            }
        }

        private static InfluenceOperationResult Failure(
            InfluenceFailureCode failureCode,
            string reason,
            int playerId,
            string slotId,
            bool stateChanged)
        {
            return InfluenceOperationResult.Failure(failureCode, reason, playerId, slotId, stateChanged);
        }

        private static ValidationResult ToValidationResult(InfluenceOperationResult result)
        {
            if (result.Succeeded)
            {
                return ValidationResult.Success;
            }

            return ValidationResult.Failure(ToCommandErrorCode(result.FailureCode), result.Reason);
        }

        private static CommandResult ToCommandResult(
            InfluenceOperationResult result,
            GameEventKind eventKind,
            string defaultMessage)
        {
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(ToValidationResult(result));
            }

            var message = string.IsNullOrEmpty(defaultMessage) ? "Influence operation completed." : defaultMessage;
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = eventKind,
                    PlayerId = result.PlayerId,
                    SubjectId = result.SlotId,
                    Message = message
                }
            }, message);
        }

        private static CommandErrorCode ToCommandErrorCode(InfluenceFailureCode failureCode)
        {
            switch (failureCode)
            {
                case InfluenceFailureCode.None:
                    return CommandErrorCode.None;
                case InfluenceFailureCode.InvalidPlayer:
                    return CommandErrorCode.InvalidPlayer;
                case InfluenceFailureCode.InvalidSlot:
                    return CommandErrorCode.InvalidTarget;
                case InfluenceFailureCode.OccupiedSlot:
                case InfluenceFailureCode.OpponentCityPresent:
                case InfluenceFailureCode.RouteCoveredByRoad:
                    return CommandErrorCode.OccupiedSlot;
                case InfluenceFailureCode.InsufficientSupply:
                    return CommandErrorCode.InsufficientInfluence;
                case InfluenceFailureCode.ResourceTokenRequired:
                    return CommandErrorCode.ClosedLocation;
                case InfluenceFailureCode.InfluenceNotFound:
                case InfluenceFailureCode.InfluenceOwnerMismatch:
                    return CommandErrorCode.InvalidSource;
                case InfluenceFailureCode.InvalidState:
                default:
                    return CommandErrorCode.InvalidTarget;
            }
        }
    }
}
