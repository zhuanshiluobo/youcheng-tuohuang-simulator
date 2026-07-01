using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    public sealed class EventEffectResolutionResult
    {
        private EventEffectResolutionResult(
            bool succeeded,
            ValidationResult validation,
            IReadOnlyList<InfluenceOperationResult> influencePlacements)
        {
            Succeeded = succeeded;
            Validation = validation;
            InfluencePlacements = influencePlacements ?? new List<InfluenceOperationResult>().AsReadOnly();
        }

        public bool Succeeded { get; private set; }
        public ValidationResult Validation { get; private set; }
        public IReadOnlyList<InfluenceOperationResult> InfluencePlacements { get; private set; }

        public static EventEffectResolutionResult Success(IReadOnlyList<InfluenceOperationResult> influencePlacements)
        {
            return new EventEffectResolutionResult(true, ValidationResult.Success, influencePlacements);
        }

        public static EventEffectResolutionResult Failure(ValidationResult validation)
        {
            return new EventEffectResolutionResult(false, validation, new List<InfluenceOperationResult>().AsReadOnly());
        }
    }

    public sealed class EventEffectResolver
    {
        private readonly IMapQueryService mapQuery;
        private readonly InfluenceService influenceService;
        private readonly IInfluenceRoadCoverageQuery roadCoverageQuery;

        public EventEffectResolver(IMapQueryService mapQuery, InfluenceService influenceService)
            : this(mapQuery, influenceService, new RuntimeStateRoadCoverageQuery())
        {
        }

        public EventEffectResolver(
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            IInfluenceRoadCoverageQuery roadCoverageQuery)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
            this.roadCoverageQuery = roadCoverageQuery ?? new RuntimeStateRoadCoverageQuery();
        }

        public ValidationResult Validate(
            GameState state,
            int playerId,
            IReadOnlyList<EventEffect> effects,
            string originLocationId,
            IReadOnlyList<string> influenceSlotIds,
            IReadOnlyList<string> reservedSlotIds = null,
            ResourceSet priorCost = null)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "事件效果玩家不存在。");
            }

            if (effects == null || effects.Count <= 0)
            {
                return ValidationResult.Success;
            }

            var requiredInfluenceCount = CountRequiredInfluence(effects);
            if (requiredInfluenceCount <= 0)
            {
                return ValidationResult.Success;
            }

            if (influenceSlotIds == null || influenceSlotIds.Count < requiredInfluenceCount)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "事件影响力效果缺少目标槽位。");
            }

            if (player.InfluenceSupply < requiredInfluenceCount)
            {
                return ValidationResult.Failure(CommandErrorCode.InsufficientInfluence, "玩家供应堆中没有足够影响力标识。");
            }

            var totalCost = BuildTotalCost(effects);
            if (priorCost != null)
            {
                totalCost.Add(priorCost);
            }

            if (!player.Resources.CanPay(totalCost))
            {
                return ValidationResult.Failure(CommandErrorCode.InsufficientResource, "玩家无法支付事件效果费用。");
            }

            var slotCursor = 0;
            var reservedSlots = BuildReservedSlots(reservedSlotIds);
            for (var effectIndex = 0; effectIndex < effects.Count; effectIndex++)
            {
                var effect = effects[effectIndex];
                if (!IsInfluenceEffect(effect))
                {
                    continue;
                }

                for (var amountIndex = 0; amountIndex < effect.Amount; amountIndex++)
                {
                    var slotValidation = ValidateInfluenceSlot(
                        state,
                        playerId,
                        effect,
                        originLocationId,
                        influenceSlotIds[slotCursor],
                        reservedSlots);
                    if (!slotValidation.IsValid)
                    {
                        return slotValidation;
                    }

                    slotCursor += 1;
                }
            }

            return ValidationResult.Success;
        }

        public EventEffectResolutionResult Apply(
            GameState state,
            int playerId,
            IReadOnlyList<EventEffect> effects,
            string originLocationId,
            IReadOnlyList<string> influenceSlotIds,
            IReadOnlyList<string> reservedSlotIds = null)
        {
            var validation = Validate(state, playerId, effects, originLocationId, influenceSlotIds, reservedSlotIds);
            if (!validation.IsValid)
            {
                return EventEffectResolutionResult.Failure(validation);
            }

            if (effects == null || effects.Count <= 0)
            {
                return EventEffectResolutionResult.Success(new List<InfluenceOperationResult>().AsReadOnly());
            }

            var player = state.FindPlayer(playerId);
            var totalCost = BuildTotalCost(effects);
            player.Resources.TryPay(totalCost);
            EventEffectUtility.Apply(state, playerId, effects);

            var placements = new List<InfluenceOperationResult>();
            var slotCursor = 0;
            for (var effectIndex = 0; effectIndex < effects.Count; effectIndex++)
            {
                var effect = effects[effectIndex];
                if (!IsInfluenceEffect(effect))
                {
                    continue;
                }

                for (var amountIndex = 0; amountIndex < effect.Amount; amountIndex++)
                {
                    var placement = influenceService.Place(state, playerId, influenceSlotIds[slotCursor]);
                    if (!placement.Succeeded)
                    {
                        return EventEffectResolutionResult.Failure(placement.Validation);
                    }

                    placements.Add(placement);
                    slotCursor += 1;
                }
            }

            return EventEffectResolutionResult.Success(placements.AsReadOnly());
        }

        private ValidationResult ValidateInfluenceSlot(
            GameState state,
            int playerId,
            EventEffect effect,
            string originLocationId,
            string slotId,
            HashSet<string> reservedSlots)
        {
            InfluenceSlotReference slot;
            string reason;
            if (!InfluenceSlotReference.TryParse(mapQuery, slotId, out slot, out reason))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, reason);
            }

            if (!IsSlotInScope(effect.TargetScope, originLocationId, slot))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "事件影响力目标不在允许范围内。");
            }

            if (reservedSlots.Contains(slot.SlotId))
            {
                return ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "事件影响力目标槽位重复。");
            }

            if (FindInfluenceAtSlot(state, slot.SlotId) != null)
            {
                return ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "事件影响力目标槽位已被占用。");
            }

            if (slot.Kind == InfluenceSlotKind.Location)
            {
                if (!HasResourceToken(state, slot.LocationId) && slot.LocationId != originLocationId)
                {
                    return ValidationResult.Failure(CommandErrorCode.ClosedLocation, "事件影响力目标地块尚未成为资源点。");
                }

                if (HasOpponentCityAtLocation(state, playerId, slot.LocationId))
                {
                    return ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "事件影响力目标地块有对手移动城市。");
                }
            }
            else if (roadCoverageQuery.IsRouteCoveredByRoad(state, slot.RouteId))
            {
                return ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "事件影响力目标航道已被道路覆盖。");
            }

            var placementValidation = influenceService.CanPlace(state, playerId, slot.SlotId);
            if (!placementValidation.IsValid &&
                !(slot.Kind == InfluenceSlotKind.Location &&
                  slot.LocationId == originLocationId &&
                  placementValidation.ErrorCode == CommandErrorCode.ClosedLocation))
            {
                return placementValidation;
            }

            reservedSlots.Add(slot.SlotId);
            return ValidationResult.Success;
        }

        private bool IsSlotInScope(EventEffectTargetScope targetScope, string originLocationId, InfluenceSlotReference slot)
        {
            switch (targetScope)
            {
                case EventEffectTargetScope.None:
                    return true;
                case EventEffectTargetScope.CurrentLocationOrAdjacentRoute:
                    return (slot.Kind == InfluenceSlotKind.Location && slot.LocationId == originLocationId) ||
                           (slot.Kind == InfluenceSlotKind.Route && RouteCoversLocation(slot.RouteId, originLocationId));
                case EventEffectTargetScope.AdjacentRoute:
                    return slot.Kind == InfluenceSlotKind.Route && RouteCoversLocation(slot.RouteId, originLocationId);
                default:
                    return false;
            }
        }

        private bool RouteCoversLocation(string routeId, string locationId)
        {
            var route = mapQuery.GetRoute(routeId);
            if (route.CoveredLocationIds != null && route.CoveredLocationIds.Count > 0)
            {
                return route.CoveredLocationIds.Contains(locationId);
            }

            return route.FromLocationId == locationId || route.ToLocationId == locationId;
        }

        private static ResourceSet BuildTotalCost(IReadOnlyList<EventEffect> effects)
        {
            var total = new ResourceSet();
            if (effects == null)
            {
                return total;
            }

            for (var i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                if (!IsInfluenceEffect(effect) || effect.CostAmount <= 0)
                {
                    continue;
                }

                total.Set(effect.CostResourceType, total.Get(effect.CostResourceType) + effect.CostAmount);
            }

            return total;
        }

        private static int CountRequiredInfluence(IReadOnlyList<EventEffect> effects)
        {
            var total = 0;
            if (effects == null)
            {
                return total;
            }

            for (var i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                if (IsInfluenceEffect(effect) && effect.Amount > 0)
                {
                    total += effect.Amount;
                }
            }

            return total;
        }

        private static bool IsInfluenceEffect(EventEffect effect)
        {
            return effect != null && effect.Kind == EventEffectKind.PlaceInfluence && effect.Amount > 0;
        }

        private HashSet<string> BuildReservedSlots(IReadOnlyList<string> reservedSlotIds)
        {
            var reservedSlots = new HashSet<string>();
            if (reservedSlotIds == null)
            {
                return reservedSlots;
            }

            for (var i = 0; i < reservedSlotIds.Count; i++)
            {
                InfluenceSlotReference slot;
                string reason;
                if (InfluenceSlotReference.TryParse(mapQuery, reservedSlotIds[i], out slot, out reason))
                {
                    reservedSlots.Add(slot.SlotId);
                }
            }

            return reservedSlots;
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
    }
}
