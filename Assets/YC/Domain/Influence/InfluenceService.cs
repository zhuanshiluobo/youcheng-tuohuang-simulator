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
        private readonly InfluencePlacementRule placementRule;
        private readonly InfluenceMoveRule moveRule;
        private readonly InfluenceRemovalService removalService;
        private readonly InfluenceQueryService queryService;

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
            _ = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.mapQuery = mapQuery;
            boundState = state;
            this.roadCoverageQuery = roadCoverageQuery ?? new NoInfluenceRoadCoverageQuery();
            placementRule = new InfluencePlacementRule(mapQuery, roadCoverageQuery);
            moveRule = new InfluenceMoveRule(placementRule);
            removalService = new InfluenceRemovalService();
            queryService = new InfluenceQueryService(mapQuery);
        }

        public InfluenceService(
            GameState state,
            InfluencePlacementRule placementRule,
            InfluenceMoveRule moveRule,
            InfluenceRemovalService removalService,
            InfluenceQueryService queryService)
        {
            _ = placementRule ?? throw new ArgumentNullException(nameof(placementRule));
            _ = moveRule ?? throw new ArgumentNullException(nameof(moveRule));
            _ = removalService ?? throw new ArgumentNullException(nameof(removalService));
            _ = queryService ?? throw new ArgumentNullException(nameof(queryService));
            this.placementRule = placementRule;
            this.moveRule = moveRule;
            this.removalService = removalService;
            this.queryService = queryService;
            boundState = state;
            mapQuery = null;
            roadCoverageQuery = null;
        }

        public InfluenceService(
            InfluencePlacementRule placementRule,
            InfluenceMoveRule moveRule,
            InfluenceRemovalService removalService,
            InfluenceQueryService queryService)
            : this(null, placementRule, moveRule, removalService, queryService)
        {
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
            ValidateState(state);
            return queryService.HasInfluenceAt(state, slotId);
        }

        public bool HasInfluenceAt(string slotId)
        {
            return HasInfluenceAt(GetBoundState(), slotId);
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

            return queryService.FindInfluence(state, influenceId);
        }

        public InfluencePlacement FindInfluence(string influenceId)
        {
            return FindInfluence(GetBoundState(), influenceId);
        }

        private InfluenceOperationResult CanPlaceCore(GameState state, int playerId, string slotId)
        {
            ValidateState(state);
            return placementRule.Validate(state, playerId, slotId, true);
        }

        private InfluenceOperationResult PlaceCore(GameState state, int playerId, string slotId)
        {
            ValidateState(state);
            var validation = placementRule.Validate(state, playerId, slotId, true);
            if (!validation.Succeeded)
            {
                return validation;
            }

            var player = state.FindPlayer(playerId);
            var canonicalSlotId = validation.SlotId;
            player.InfluenceSupply -= 1;
            state.Map.Influences.Add(CreatePlacementFromSlotId(playerId, canonicalSlotId));
            return InfluenceOperationResult.Success(playerId, canonicalSlotId, true);
        }

        private InfluenceOperationResult CanMoveCore(GameState state, int playerId, string sourceInfluenceId, string targetSlotId)
        {
            ValidateState(state);
            return moveRule.Validate(state, playerId, sourceInfluenceId, targetSlotId);
        }

        private InfluenceOperationResult MoveCore(GameState state, int playerId, string sourceInfluenceId, string targetSlotId)
        {
            ValidateState(state);
            var validation = moveRule.Validate(state, playerId, sourceInfluenceId, targetSlotId);
            if (!validation.Succeeded)
            {
                return validation;
            }

            var placement = FindInfluence(state, sourceInfluenceId);
            var canonicalTargetSlotId = validation.SlotId;
            ApplySlotFromId(placement, canonicalTargetSlotId);
            return InfluenceOperationResult.Success(playerId, canonicalTargetSlotId, true);
        }

        private InfluenceOperationResult RemoveCore(GameState state, string influenceId)
        {
            ValidateState(state);
            return removalService.Remove(state, influenceId);
        }

        private InfluenceOperationResult ReplaceCore(GameState state, string targetInfluenceId, int newOwnerPlayerId)
        {
            ValidateState(state);
            var target = FindInfluence(state, targetInfluenceId);
            if (target == null)
            {
                return Failure(InfluenceFailureCode.InfluenceNotFound, "Target influence must exist before it can be replaced.", newOwnerPlayerId, targetInfluenceId, false);
            }

            var newOwner = state.FindPlayer(newOwnerPlayerId);
            if (newOwner == null)
            {
                return Failure(InfluenceFailureCode.InvalidPlayer, "Replacement owner must be an existing player.", newOwnerPlayerId, targetInfluenceId, false);
            }

            var targetSlotId = target.SlotId;
            if (newOwner.InfluenceSupply <= 0)
            {
                return Failure(
                    InfluenceFailureCode.InsufficientSupply,
                    "Replacement owner must have an available marker in supply.",
                    newOwnerPlayerId,
                    targetSlotId,
                    false);
            }

            var targetIndex = state.Map.Influences.IndexOf(target);
            state.Map.Influences.RemoveAt(targetIndex);

            var placementValidation = placementRule.Validate(state, newOwnerPlayerId, targetSlotId, false);
            if (!placementValidation.Succeeded)
            {
                state.Map.Influences.Insert(targetIndex, target);
                return Failure(
                    placementValidation.FailureCode,
                    placementValidation.Reason,
                    newOwnerPlayerId,
                    targetSlotId,
                    false);
            }

            var oldOwner = state.FindPlayer(target.PlayerId);
            if (oldOwner != null)
            {
                oldOwner.InfluenceSupply += 1;
            }

            newOwner.InfluenceSupply -= 1;
            state.Map.Influences.Insert(targetIndex, CreatePlacementFromSlotId(newOwnerPlayerId, targetSlotId));
            return InfluenceOperationResult.Success(newOwnerPlayerId, targetSlotId, true);
        }

        private int CountInRegionCore(GameState state, int playerId, string regionId, bool includeCity)
        {
            ValidateState(state);
            return queryService.CountInRegion(state, playerId, regionId, includeCity);
        }

        private static InfluencePlacement CreatePlacementFromSlotId(int playerId, string slotId)
        {
            var placement = new InfluencePlacement { PlayerId = playerId };
            ApplySlotFromId(placement, slotId);
            return placement;
        }

        private static void ApplySlotFromId(InfluencePlacement placement, string slotId)
        {
            placement.SlotId = slotId;
            if (slotId.StartsWith(InfluenceSlotReference.LocationPrefix, StringComparison.Ordinal))
            {
                var rest = slotId.Substring(InfluenceSlotReference.LocationPrefix.Length);
                var lastColon = rest.LastIndexOf(':');
                placement.LocationId = lastColon >= 0 ? rest.Substring(0, lastColon) : rest;
                placement.RouteId = string.Empty;
            }
            else if (slotId.StartsWith(InfluenceSlotReference.RoutePrefix, StringComparison.Ordinal))
            {
                var rest = slotId.Substring(InfluenceSlotReference.RoutePrefix.Length);
                var lastColon = rest.LastIndexOf(':');
                placement.RouteId = lastColon >= 0 ? rest.Substring(0, lastColon) : rest;
                placement.LocationId = string.Empty;
            }
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
