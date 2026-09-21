using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Movement
{
    [Serializable]
    public sealed class CityMoveCandidate
    {
        public string CandidateId = string.Empty;
        public string TargetLocationId = string.Empty;
        public string RouteId = string.Empty;
        public int BaseCostOriginiumShard;
        public bool IsLegal;
        public string FailureReason = string.Empty;
    }

    /// <summary>
    /// 移动候选的唯一来源。候选只暴露稳定目标 ID；提交时仍由移动 Effect 再次校验。
    /// </summary>
    public sealed class CityMoveCandidateQueryService
    {
        public const string CandidatePrefix = "city.move.target:";

        private readonly IMapQueryService mapQuery;
        private readonly CityMovementService movementService;
        private readonly TravelCostService travelCostService;

        public CityMoveCandidateQueryService(
            IMapQueryService mapQuery,
            CityMovementService movementService,
            TravelCostService travelCostService)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.movementService = movementService ?? throw new ArgumentNullException(nameof(movementService));
            this.travelCostService = travelCostService ?? throw new ArgumentNullException(nameof(travelCostService));
        }

        public IReadOnlyList<CityMoveCandidate> Query(GameState state, int playerId, bool waiveBaseCost = false)
        {
            var result = new List<CityMoveCandidate>();
            PlayerState player = state == null ? null : state.FindPlayer(playerId);
            if (player == null || string.IsNullOrEmpty(player.CityLocationId)) return result.AsReadOnly();

            IReadOnlyList<MapLocationDefinition> adjacent = mapQuery.GetAdjacentLocations(player.CityLocationId);
            for (int i = 0; i < adjacent.Count; i++)
            {
                MapLocationDefinition target = adjacent[i];
                MapRouteDefinition route;
                try { route = mapQuery.FindRoute(player.CityLocationId, target.LocationId); }
                catch (ArgumentException) { continue; }

                GameState validationState = state;
                if (waiveBaseCost)
                {
                    validationState = GameStateCloneService.DeepClone(state);
                    validationState.FindPlayer(playerId).Resources.OriginiumShard += travelCostService.GetCityMoveBaseCost().OriginiumShard;
                }
                ValidationResult validation = movementService.CanMoveCity(
                    validationState,
                    playerId,
                    target.LocationId,
                    -1,
                    null);

                result.Add(new CityMoveCandidate
                {
                    CandidateId = CandidatePrefix + target.LocationId,
                    TargetLocationId = target.LocationId,
                    RouteId = route.RouteId,
                    BaseCostOriginiumShard = waiveBaseCost ? 0 : travelCostService.GetCityMoveBaseCost().OriginiumShard,
                    IsLegal = validation.IsValid,
                    FailureReason = validation.IsValid ? string.Empty : validation.Reason
                });
            }

            result.Sort((left, right) => StringComparer.Ordinal.Compare(left.CandidateId, right.CandidateId));
            return result.AsReadOnly();
        }

        public IReadOnlyList<CityMoveCandidate> QueryLegal(GameState state, int playerId, bool waiveBaseCost = false)
        {
            var result = new List<CityMoveCandidate>();
            IReadOnlyList<CityMoveCandidate> candidates = Query(state, playerId, waiveBaseCost);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].IsLegal) result.Add(candidates[i]);
            }
            return result.AsReadOnly();
        }

        public static string GetTargetLocationId(string candidateId)
        {
            if (string.IsNullOrEmpty(candidateId) || !candidateId.StartsWith(CandidatePrefix, StringComparison.Ordinal)) return string.Empty;
            return candidateId.Substring(CandidatePrefix.Length);
        }
    }
}
