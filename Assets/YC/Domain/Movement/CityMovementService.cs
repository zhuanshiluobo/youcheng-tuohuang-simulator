using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Movement
{
    public sealed class CityMovementService
    {
        private static readonly HashSet<string> RedZoneLocationIds = new HashSet<string>
        {
            "G-04", "F-01", "F-02", "F-03", "E-02", "E-03"
        };

        private readonly IMapQueryService mapQuery;
        private readonly InfluenceService influenceService;
        private readonly TravelCostService travelCostService;

        public CityMovementService(
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            TravelCostService travelCostService)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
            this.travelCostService = travelCostService ?? throw new ArgumentNullException(nameof(travelCostService));
        }

        public ValidationResult CanMoveCity(GameState state, int playerId, string targetLocationId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "未知玩家。");
            }

            if (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2)
            {
                return ValidationResult.Failure(CommandErrorCode.WrongPhase, "城市移动只能在行动轮阶段进行。");
            }

            if (state.CurrentPlayerId != playerId)
            {
                return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "当前不是该玩家的行动回合。");
            }

            if (state.HasPendingChoice())
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "请先处理待选择项再移动城市。");
            }

            if (player.ActedMainActionThisTurn)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "本行动轮已执行过主要行动。");
            }

            if (string.IsNullOrEmpty(player.CityLocationId))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidSource, "玩家城市不在场上。");
            }

            mapQuery.GetLocation(targetLocationId);
            var route = FindAdjacentRoute(player.CityLocationId, targetLocationId);
            if (route == null)
            {
                return ValidationResult.Failure(CommandErrorCode.NoRoute, "目标地点与当前城市位置不相邻。");
            }

            if (HasOpponentCityAtLocation(state, playerId, targetLocationId))
            {
                return ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "目标地点有其他玩家的城市。");
            }

            if (IsRedZoneClosed(state, targetLocationId))
            {
                return ValidationResult.Failure(CommandErrorCode.ClosedLocation, "红色区域在第4回合前不可进入。");
            }

            var cost = travelCostService.GetCityMoveBaseCost();
            if (!player.Resources.CanPay(cost))
            {
                return ValidationResult.Failure(CommandErrorCode.InsufficientResource, "玩家无法支付城市移动费用。");
            }

            return ValidationResult.Success;
        }

        public CityMovementResult MoveCity(GameState state, int playerId, string targetLocationId)
        {
            var validation = CanMoveCity(state, playerId, targetLocationId);
            if (!validation.IsValid)
            {
                return CityMovementResult.Failure(validation);
            }

            var player = state.FindPlayer(playerId);
            var sourceLocationId = player.CityLocationId;
            var route = mapQuery.FindRoute(sourceLocationId, targetLocationId);
            var cost = travelCostService.GetCityMoveBaseCost();

            player.Resources.TryPay(cost);
            player.CityLocationId = targetLocationId;
            player.HasMovedCityThisRound = true;
            player.ActedMainActionThisTurn = true;

            var removedInfluenceCount = RemoveOpponentInfluenceOnRouteAndTarget(state, playerId, route.RouteId, targetLocationId);
            var sourceSlotId = FindFirstAvailableLocationSlot(state, playerId, sourceLocationId);
            var placementResult = string.IsNullOrEmpty(sourceSlotId)
                ? InfluenceOperationResult.Failure(
                    InfluenceFailureCode.InvalidSlot,
                    "Source location has no available influence slots.",
                    playerId,
                    sourceLocationId,
                    false)
                : influenceService.Place(state, playerId, sourceSlotId);

            return CityMovementResult.Success(sourceLocationId, targetLocationId, route.RouteId, removedInfluenceCount, placementResult);
        }

        private string FindFirstAvailableLocationSlot(GameState state, int playerId, string locationId)
        {
            var location = mapQuery.GetLocation(locationId);
            for (var i = 0; i < location.InfluenceSlotCount; i++)
            {
                var slotId = InfluenceService.GetLocationSlotId(locationId, i);
                if (influenceService.CanPlace(state, playerId, slotId).IsValid)
                {
                    return slotId;
                }
            }

            return string.Empty;
        }

        private int RemoveOpponentInfluenceOnRouteAndTarget(GameState state, int playerId, string routeId, string targetLocationId)
        {
            var removed = 0;
            for (var i = state.Map.Influences.Count - 1; i >= 0; i--)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId == playerId)
                {
                    continue;
                }

                if (influence.RouteId == routeId || influence.LocationId == targetLocationId)
                {
                    state.Map.Influences.RemoveAt(i);
                    var owner = state.FindPlayer(influence.PlayerId);
                    if (owner != null)
                    {
                        owner.InfluenceSupply += 1;
                    }

                    removed += 1;
                }
            }

            return removed;
        }

        private MapRouteDefinition FindAdjacentRoute(string sourceLocationId, string targetLocationId)
        {
            try
            {
                return mapQuery.FindRoute(sourceLocationId, targetLocationId);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static bool HasOpponentCityAtLocation(GameState state, int playerId, string locationId)
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

        private static bool IsRedZoneClosed(GameState state, string locationId)
        {
            if (!RedZoneLocationIds.Contains(locationId))
            {
                return false;
            }

            return state.Round < 4;
        }
    }
}
