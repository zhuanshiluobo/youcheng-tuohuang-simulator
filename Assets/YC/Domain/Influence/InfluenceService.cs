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

        public InfluenceOperationResult MoveAtomically(
            GameState state,
            int playerId,
            IReadOnlyList<InfluenceMoveRequest> moves)
        {
            ValidateState(state);
            if (moves == null || moves.Count == 0)
            {
                return Failure(
                    InfluenceFailureCode.InvalidState,
                    "至少需要提供一次影响力移动。",
                    playerId,
                    string.Empty,
                    false);
            }

            var projectedState = CreateMoveProjection(state);
            var movedInfluences = new HashSet<InfluencePlacement>();
            var canonicalTargets = new List<string>(moves.Count);
            for (var i = 0; i < moves.Count; i++)
            {
                var move = moves[i];
                if (move == null)
                {
                    return Failure(
                        InfluenceFailureCode.InvalidState,
                        "影响力移动参数不能为空。",
                        playerId,
                        string.Empty,
                        false);
                }

                var projectedInfluence = FindInfluence(projectedState, move.SourceSlotId);
                if (projectedInfluence != null && movedInfluences.Contains(projectedInfluence))
                {
                    return Failure(
                        InfluenceFailureCode.InfluenceOwnerMismatch,
                        "一次行动中不能重复移动同一个影响力。",
                        playerId,
                        move.SourceSlotId,
                        false);
                }

                var validation = moveRule.Validate(
                    projectedState,
                    playerId,
                    move.SourceSlotId,
                    move.TargetSlotId);
                if (!validation.Succeeded)
                {
                    return validation;
                }

                movedInfluences.Add(projectedInfluence);
                canonicalTargets.Add(validation.SlotId);
                ApplySlotFromId(projectedInfluence, validation.SlotId);
            }

            for (var i = 0; i < moves.Count; i++)
            {
                var influence = FindInfluence(state, moves[i].SourceSlotId);
                ApplySlotFromId(influence, canonicalTargets[i]);
            }

            return InfluenceOperationResult.Success(
                playerId,
                canonicalTargets[canonicalTargets.Count - 1],
                true);
        }

        public ValidationResult CanRemoveThenMoveAtomically(
            GameState state,
            int playerId,
            string removeSlotId,
            InfluenceMoveRequest move)
        {
            return CanRemoveThenMoveAtomically(
                state,
                playerId,
                removeSlotId,
                new[] { move });
        }

        public ValidationResult CanRemoveThenMoveAtomically(
            GameState state,
            int playerId,
            string removeSlotId,
            IReadOnlyList<InfluenceMoveRequest> moves)
        {
            List<string> canonicalTargetSlotIds;
            return ToValidationResult(ValidateRemoveThenMove(
                state,
                playerId,
                removeSlotId,
                moves,
                out canonicalTargetSlotIds));
        }

        public InfluenceOperationResult RemoveThenMoveAtomically(
            GameState state,
            int playerId,
            string removeSlotId,
            InfluenceMoveRequest move)
        {
            return RemoveThenMoveAtomically(
                state,
                playerId,
                removeSlotId,
                new[] { move });
        }

        public InfluenceOperationResult RemoveThenMoveAtomically(
            GameState state,
            int playerId,
            string removeSlotId,
            IReadOnlyList<InfluenceMoveRequest> moves)
        {
            List<string> canonicalTargetSlotIds;
            var validation = ValidateRemoveThenMove(
                state,
                playerId,
                removeSlotId,
                moves,
                out canonicalTargetSlotIds);
            if (!validation.Succeeded)
            {
                return validation;
            }

            var removal = removalService.Remove(state, removeSlotId);
            if (!removal.Succeeded)
            {
                return removal;
            }

            for (var i = 0; i < moves.Count; i++)
            {
                var source = FindInfluence(state, moves[i].SourceSlotId);
                ApplySlotFromId(source, canonicalTargetSlotIds[i]);
            }

            return InfluenceOperationResult.Success(
                playerId,
                canonicalTargetSlotIds[canonicalTargetSlotIds.Count - 1],
                true);
        }

        public InfluenceOperationResult PlaceAtomically(
            GameState state,
            int playerId,
            IReadOnlyList<string> slotIds)
        {
            ValidateState(state);
            if (slotIds == null || slotIds.Count == 0)
            {
                return Failure(InfluenceFailureCode.InvalidState, "At least one influence placement is required.", playerId, string.Empty, false);
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return Failure(InfluenceFailureCode.InvalidPlayer, "Player must exist.", playerId, string.Empty, false);
            }

            if (player.InfluenceSupply < slotIds.Count)
            {
                return Failure(InfluenceFailureCode.InsufficientSupply, "Not enough influence markers in supply.", playerId, string.Empty, false);
            }

            var uniqueSlots = new HashSet<string>();
            for (var i = 0; i < slotIds.Count; i++)
            {
                if (string.IsNullOrEmpty(slotIds[i]) || !uniqueSlots.Add(slotIds[i]))
                {
                    return Failure(InfluenceFailureCode.InvalidSlot, "Influence placement slots must be non-empty and distinct.", playerId, slotIds[i], false);
                }

                var validation = placementRule.Validate(state, playerId, slotIds[i], true);
                if (!validation.Succeeded)
                {
                    return validation;
                }
            }

            for (var i = 0; i < slotIds.Count; i++)
            {
                var placement = PlaceCore(state, playerId, slotIds[i]);
                if (!placement.Succeeded)
                {
                    return placement;
                }
            }

            return InfluenceOperationResult.Success(playerId, slotIds[slotIds.Count - 1], true);
        }

        public ValidationResult CanMoveAtomically(
            GameState state,
            int playerId,
            IReadOnlyList<InfluenceMoveRequest> moves)
        {
            ValidateState(state);
            if (moves == null || moves.Count == 0)
            {
                return ValidationResult.Failure(
                    CommandErrorCode.InvalidTarget,
                    "At least one influence move is required.");
            }

            var projectedState = CreateMoveProjection(state);
            var movedInfluences = new HashSet<InfluencePlacement>();
            for (var i = 0; i < moves.Count; i++)
            {
                var move = moves[i];
                if (move == null)
                {
                    return ValidationResult.Failure(
                        CommandErrorCode.InvalidTarget,
                        "Influence move cannot be null.");
                }

                var projectedInfluence = FindInfluence(projectedState, move.SourceSlotId);
                if (projectedInfluence != null && movedInfluences.Contains(projectedInfluence))
                {
                    return ValidationResult.Failure(
                        CommandErrorCode.InvalidSource,
                        "The same influence cannot move twice in one action.");
                }

                var validation = moveRule.Validate(
                    projectedState,
                    playerId,
                    move.SourceSlotId,
                    move.TargetSlotId);
                if (!validation.Succeeded)
                {
                    return ToValidationResult(validation);
                }

                movedInfluences.Add(projectedInfluence);
                ApplySlotFromId(projectedInfluence, validation.SlotId);
            }

            return ValidationResult.Success;
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

        private InfluenceOperationResult ValidateRemoveThenMove(
            GameState state,
            int playerId,
            string removeSlotId,
            IReadOnlyList<InfluenceMoveRequest> moves,
            out List<string> canonicalTargetSlotIds)
        {
            ValidateState(state);
            canonicalTargetSlotIds = new List<string>();
            if (string.IsNullOrEmpty(removeSlotId) || moves == null || moves.Count == 0)
            {
                return Failure(
                    InfluenceFailureCode.InvalidState,
                    "移除和调度参数都不能为空。",
                    playerId,
                    removeSlotId,
                    false);
            }

            var projectedState = CreateMoveProjection(state);
            var removalTarget = FindInfluence(projectedState, removeSlotId);
            if (removalTarget == null)
            {
                return Failure(
                    InfluenceFailureCode.InfluenceNotFound,
                    "要移除的影响力不存在。",
                    playerId,
                    removeSlotId,
                    false);
            }

            projectedState.Map.Influences.Remove(removalTarget);
            var movedInfluences = new HashSet<InfluencePlacement>();
            for (var i = 0; i < moves.Count; i++)
            {
                var move = moves[i];
                if (move == null)
                {
                    return Failure(
                        InfluenceFailureCode.InvalidState,
                        "影响力调度参数不能为空。",
                        playerId,
                        string.Empty,
                        false);
                }

                var projectedInfluence = FindInfluence(projectedState, move.SourceSlotId);
                if (projectedInfluence != null && movedInfluences.Contains(projectedInfluence))
                {
                    return Failure(
                        InfluenceFailureCode.InfluenceOwnerMismatch,
                        "一次效果中不能重复移动同一个影响力。",
                        playerId,
                        move.SourceSlotId,
                        false);
                }

                var moveValidation = moveRule.Validate(
                    projectedState,
                    playerId,
                    move.SourceSlotId,
                    move.TargetSlotId);
                if (!moveValidation.Succeeded)
                {
                    return moveValidation;
                }

                movedInfluences.Add(projectedInfluence);
                canonicalTargetSlotIds.Add(moveValidation.SlotId);
                ApplySlotFromId(projectedInfluence, moveValidation.SlotId);
            }

            return InfluenceOperationResult.Success(
                playerId,
                canonicalTargetSlotIds[canonicalTargetSlotIds.Count - 1],
                false);
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

        private static GameState CreateMoveProjection(GameState state)
        {
            var projection = new GameState
            {
                GameId = state.GameId,
                Phase = state.Phase,
                Round = state.Round,
                MaxRounds = state.MaxRounds,
                StartPlayerId = state.StartPlayerId,
                CurrentPlayerId = state.CurrentPlayerId,
                ActionRound = state.ActionRound,
                UseSeatTurnOrder = state.UseSeatTurnOrder,
                MapId = state.MapId,
                EventDeckSeed = state.EventDeckSeed,
                Players = state.Players,
                Decks = state.Decks,
                PendingChoice = state.PendingChoice,
                PendingCardSession = state.PendingCardSession,
                FinalScoring = state.FinalScoring,
                Logs = state.Logs,
                Map = new MapRuntimeState
                {
                    OpenLocationIds = state.Map.OpenLocationIds,
                    ResourceTokens = state.Map.ResourceTokens,
                    RoadRouteIds = state.Map.RoadRouteIds,
                    RemovedFromGameCardIds = state.Map.RemovedFromGameCardIds,
                    Facilities = state.Map.Facilities
                }
            };

            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                projection.Map.Influences.Add(new InfluencePlacement
                {
                    PlayerId = influence.PlayerId,
                    SlotId = influence.SlotId,
                    LocationId = influence.LocationId,
                    RouteId = influence.RouteId
                });
            }

            return projection;
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
