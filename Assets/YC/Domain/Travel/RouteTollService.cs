using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Exploration;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Travel
{
    public enum RouteTollPaymentKeyMode
    {
        RouteId,
        SharedRegion
    }

    public sealed class RouteTollService
    {
        public const int RouteCostGoldVoucher = 2;

        private readonly IMapQueryService mapQuery;
        private readonly IInfluenceRoadCoverageQuery roadCoverageQuery;

        public RouteTollService(IMapQueryService mapQuery)
            : this(mapQuery, new RuntimeStateRoadCoverageQuery())
        {
        }

        public RouteTollService(IMapQueryService mapQuery, IInfluenceRoadCoverageQuery roadCoverageQuery)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.roadCoverageQuery = roadCoverageQuery ?? new RuntimeStateRoadCoverageQuery();
        }

        public ValidationResult TryBuildPaymentPlan(
            GameState state,
            int playerId,
            IReadOnlyList<string> routeIds,
            IDictionary<string, int> paymentRecipientsByRouteId,
            RouteTollPaymentKeyMode paymentKeyMode,
            out List<ExplorationTravelPayment> payments)
        {
            return TryBuildPaymentPlanWithInfluenceKeyMode(
                state,
                playerId,
                routeIds,
                paymentRecipientsByRouteId,
                paymentKeyMode,
                paymentKeyMode,
                out payments);
        }

        internal ValidationResult TryBuildPaymentPlanWithInfluenceKeyMode(
            GameState state,
            int playerId,
            IReadOnlyList<string> routeIds,
            IDictionary<string, int> paymentRecipientsByRouteId,
            RouteTollPaymentKeyMode paymentKeyMode,
            RouteTollPaymentKeyMode influenceKeyMode,
            out List<ExplorationTravelPayment> payments)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            routeIds = routeIds ?? new List<string>();
            payments = new List<ExplorationTravelPayment>();
            var paidPaymentKeys = new List<string>();
            var requestedRecipientsByPaymentKey = new Dictionary<string, int>();

            var requestValidation = ValidateRequestedRecipients(
                state,
                playerId,
                routeIds,
                paymentRecipientsByRouteId,
                paymentKeyMode,
                influenceKeyMode,
                requestedRecipientsByPaymentKey);
            if (!requestValidation.IsValid)
            {
                return requestValidation;
            }

            for (var i = 0; i < routeIds.Count; i++)
            {
                var routeId = routeIds[i];
                var paymentKey = GetRoutePaymentKey(routeId, paymentKeyMode);
                var influenceKey = GetRoutePaymentKey(routeId, influenceKeyMode);
                if (paidPaymentKeys.Contains(paymentKey) ||
                    IsRouteCoveredByRoad(state, routeId) ||
                    HasPaymentKeyInfluenceOwnedBy(
                        state,
                        influenceKey,
                        playerId,
                        influenceKeyMode))
                {
                    continue;
                }

                var opponentOwners = GetOpponentInfluenceOwnersOnPaymentKey(
                    state,
                    influenceKey,
                    playerId,
                    influenceKeyMode);
                var receiverPlayerId = -1;
                if (opponentOwners.Count > 0)
                {
                    if (requestedRecipientsByPaymentKey.TryGetValue(paymentKey, out var requestedReceiver))
                    {
                        if (!opponentOwners.Contains(requestedReceiver))
                        {
                            return ValidationResult.Failure(
                                CommandErrorCode.InvalidTarget,
                                "指定路费接收玩家在该航道上没有影响力。");
                        }

                        receiverPlayerId = requestedReceiver;
                    }
                    else
                    {
                        receiverPlayerId = opponentOwners[0];
                    }
                }
                else if (requestedRecipientsByPaymentKey.ContainsKey(paymentKey))
                {
                    return ValidationResult.Failure(
                        CommandErrorCode.InvalidTarget,
                        "该航道没有对手影响力，不能指定玩家接收路费。");
                }

                paidPaymentKeys.Add(paymentKey);
                payments.Add(new ExplorationTravelPayment
                {
                    RouteId = routeId,
                    Amount = RouteCostGoldVoucher,
                    ReceiverPlayerId = receiverPlayerId
                });
            }

            return ValidationResult.Success;
        }

        public void ApplyPayments(GameState state, PlayerState player, IReadOnlyList<ExplorationTravelPayment> payments)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            if (payments == null)
            {
                return;
            }

            for (var i = 0; i < payments.Count; i++)
            {
                var payment = payments[i];
                player.Resources.GoldVoucher -= payment.Amount;
                if (payment.PaidToSupply)
                {
                    continue;
                }

                var receiver = state.FindPlayer(payment.ReceiverPlayerId);
                if (receiver != null)
                {
                    receiver.Resources.GoldVoucher += payment.Amount;
                }
            }
        }

        public int SumPayments(IReadOnlyList<ExplorationTravelPayment> payments)
        {
            var total = 0;
            if (payments == null)
            {
                return total;
            }

            for (var i = 0; i < payments.Count; i++)
            {
                total += payments[i].Amount;
            }

            return total;
        }

        public string GetRoutePaymentKey(string routeId, RouteTollPaymentKeyMode paymentKeyMode)
        {
            var route = mapQuery.GetRoute(routeId);
            if (paymentKeyMode == RouteTollPaymentKeyMode.SharedRegion &&
                !string.IsNullOrEmpty(route.RegionId) &&
                IsRoutePaymentRegion(route.RegionId))
            {
                return route.RegionId;
            }

            return route.RouteId;
        }

        public bool IsRouteCoveredByRoad(GameState state, string routeId)
        {
            return roadCoverageQuery.IsRouteCoveredByRoad(state, routeId);
        }

        public bool HasPaymentKeyInfluenceOwnedBy(
            GameState state,
            string paymentKey,
            int playerId,
            RouteTollPaymentKeyMode paymentKeyMode)
        {
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId == playerId && IsInfluenceOnPaymentKey(influence, paymentKey, paymentKeyMode))
                {
                    return true;
                }
            }

            return false;
        }

        public List<int> GetOpponentInfluenceOwnersOnPaymentKey(
            GameState state,
            string paymentKey,
            int playerId,
            RouteTollPaymentKeyMode paymentKeyMode)
        {
            var owners = new List<int>();
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId == playerId ||
                    !IsInfluenceOnPaymentKey(influence, paymentKey, paymentKeyMode))
                {
                    continue;
                }

                if (!owners.Contains(influence.PlayerId))
                {
                    owners.Add(influence.PlayerId);
                }
            }

            return owners;
        }

        public bool IsPaymentRequired(
            GameState state,
            string routeId,
            int playerId,
            RouteTollPaymentKeyMode paymentKeyMode)
        {
            if (IsRouteCoveredByRoad(state, routeId))
            {
                return false;
            }

            var paymentKey = GetRoutePaymentKey(routeId, paymentKeyMode);
            return !HasPaymentKeyInfluenceOwnedBy(state, paymentKey, playerId, paymentKeyMode);
        }

        private ValidationResult ValidateRequestedRecipients(
            GameState state,
            int playerId,
            IReadOnlyList<string> routeIds,
            IDictionary<string, int> paymentRecipientsByRouteId,
            RouteTollPaymentKeyMode paymentKeyMode,
            RouteTollPaymentKeyMode influenceKeyMode,
            Dictionary<string, int> requestedRecipientsByPaymentKey)
        {
            if (paymentRecipientsByRouteId == null || paymentRecipientsByRouteId.Count <= 0)
            {
                return ValidationResult.Success;
            }

            foreach (var entry in paymentRecipientsByRouteId)
            {
                if (!ContainsRouteId(routeIds, entry.Key))
                {
                    return ValidationResult.Failure(
                        CommandErrorCode.InvalidTarget,
                        "指定的路费接收航道不在本次路线中。");
                }

                var paymentKey = GetRoutePaymentKey(entry.Key, paymentKeyMode);
                var influenceKey = GetRoutePaymentKey(entry.Key, influenceKeyMode);
                if (IsRouteCoveredByRoad(state, entry.Key) ||
                    HasPaymentKeyInfluenceOwnedBy(
                        state,
                        influenceKey,
                        playerId,
                        influenceKeyMode))
                {
                    return ValidationResult.Failure(
                        CommandErrorCode.InvalidTarget,
                        "该航道无需支付路费，不能指定接收方。");
                }

                var opponentOwners = GetOpponentInfluenceOwnersOnPaymentKey(
                    state,
                    influenceKey,
                    playerId,
                    influenceKeyMode);
                if (opponentOwners.Count <= 0)
                {
                    return ValidationResult.Failure(
                        CommandErrorCode.InvalidTarget,
                        "该航道没有对手影响力，不能指定玩家接收路费。");
                }

                if (!opponentOwners.Contains(entry.Value))
                {
                    return ValidationResult.Failure(
                        CommandErrorCode.InvalidTarget,
                        "指定路费接收玩家在该航道上没有影响力。");
                }

                if (requestedRecipientsByPaymentKey.TryGetValue(paymentKey, out var existingReceiver) &&
                    existingReceiver != entry.Value)
                {
                    return ValidationResult.Failure(
                        CommandErrorCode.InvalidTarget,
                        "同一航道路费分组不能指定多个接收玩家。");
                }

                requestedRecipientsByPaymentKey[paymentKey] = entry.Value;
            }

            return ValidationResult.Success;
        }

        private bool IsInfluenceOnPaymentKey(
            InfluencePlacement influence,
            string paymentKey,
            RouteTollPaymentKeyMode paymentKeyMode)
        {
            if (!string.IsNullOrEmpty(influence.RouteId) &&
                GetRoutePaymentKey(influence.RouteId, paymentKeyMode) == paymentKey)
            {
                return true;
            }

            InfluenceSlotReference slot;
            string reason;
            return InfluenceSlotReference.TryParse(mapQuery, influence.SlotId, out slot, out reason) &&
                   slot.Kind == InfluenceSlotKind.Route &&
                   GetRoutePaymentKey(slot.RouteId, paymentKeyMode) == paymentKey;
        }

        private bool IsRoutePaymentRegion(string regionId)
        {
            for (var i = 0; i < mapQuery.Map.Regions.Count; i++)
            {
                var region = mapQuery.Map.Regions[i];
                if (region.RegionId == regionId)
                {
                    return region.LocationIds != null && region.LocationIds.Count > 0;
                }
            }

            return false;
        }

        private static bool ContainsRouteId(IReadOnlyList<string> routeIds, string routeId)
        {
            if (routeIds == null)
            {
                return false;
            }

            for (var i = 0; i < routeIds.Count; i++)
            {
                if (routeIds[i] == routeId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
