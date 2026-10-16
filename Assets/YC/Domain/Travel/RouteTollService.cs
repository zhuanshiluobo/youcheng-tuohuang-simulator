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

        // 一次查询物化公路/影响力；新共用选路不在每个Route循环重复读区域投影。
        public IReadOnlyList<RouteSelectionRouteOption> ReadRouteFacts(GameState state, int playerId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var ownRoutes = new HashSet<string>(StringComparer.Ordinal);
            var recipients = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var influences = state.Map.Influences.ToArray();
            foreach (var influence in influences)
            {
                if (influence == null) continue;
                string routeId = influence.RouteId;
                if (string.IsNullOrEmpty(routeId))
                {
                    InfluenceSlotReference slot;
                    string reason;
                    if (!InfluenceSlotReference.TryParse(mapQuery, influence.SlotId, out slot, out reason)
                        || slot.Kind != InfluenceSlotKind.Route) continue;
                    routeId = slot.RouteId;
                }
                bool company = influence.OwnerSubject?.SubjectType == "company";
                if (!company && influence.PlayerId == playerId) { ownRoutes.Add(routeId); continue; }
                string subject = company || influence.PlayerId <= 0 ? "bank" : "player:" + influence.PlayerId;
                HashSet<string> owners;
                if (!recipients.TryGetValue(routeId, out owners)) recipients.Add(routeId, owners = new HashSet<string>(StringComparer.Ordinal));
                owners.Add(subject);
            }
            var roads = new HashSet<string>(state.Map.RoadRouteIds.ToArray(), StringComparer.Ordinal);
            var result = new List<RouteSelectionRouteOption>();
            foreach (var route in mapQuery.Map.Routes)
            {
                bool road = roadCoverageQuery is RuntimeStateRoadCoverageQuery
                    ? roads.Contains(route.RouteId) : roadCoverageQuery.IsRouteCoveredByRoad(state, route.RouteId);
                bool own = ownRoutes.Contains(route.RouteId);
                var receivers = new List<RouteRecipient>();
                if (!road && !own)
                {
                    HashSet<string> owners;
                    if (!recipients.TryGetValue(route.RouteId, out owners)) owners = new HashSet<string>(StringComparer.Ordinal) { "bank" };
                    var keys = new List<string>(owners);
                    keys.Sort(StringComparer.Ordinal);
                    foreach (string subject in keys)
                    {
                        int receiver;
                        if (subject != "bank" && (!int.TryParse(subject.Substring(7), out receiver) || state.FindPlayer(receiver) == null)) continue;
                        receivers.Add(new RouteRecipient { SubjectKey = subject, Kind = subject == "bank" ? RouteRecipientKind.Bank : RouteRecipientKind.Player,
                            PlayerId = subject == "bank" ? (int?)null : int.Parse(subject.Substring(7)) });
                    }
                }
                var covered = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (route.CoveredLocationIds != null) foreach (string location in route.CoveredLocationIds)
                    if (!string.IsNullOrEmpty(location) && seen.Add(location)) covered.Add(location);
                foreach (string endpoint in new[] { route.FromLocationId, route.ToLocationId })
                    if (!string.IsNullOrEmpty(endpoint) && seen.Add(endpoint)) covered.Add(endpoint);
                result.Add(new RouteSelectionRouteOption { RouteId = route.RouteId, CoveredLocationIds = covered.AsReadOnly(),
                    FreeReason = road ? (own ? RouteFreeReason.OwnInfluenceAndPublicRoad : RouteFreeReason.PublicRoad) : (own ? RouteFreeReason.OwnInfluence : RouteFreeReason.None),
                    TollGoldVoucherAmount = road || own ? 0 : RouteCostGoldVoucher, EligibleRecipients = receivers.AsReadOnly() });
            }
            return result.AsReadOnly();
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
            var influences = state.Map.Influences.ToArray();
            for (var i = 0; i < influences.Length; i++)
            {
                var influence = influences[i];
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
            var influences = state.Map.Influences.ToArray();
            for (var i = 0; i < influences.Length; i++)
            {
                var influence = influences[i];
                var ownerPlayerId = influence.OwnerSubject?.SubjectType == "company" ? -1 : influence.PlayerId;
                if (ownerPlayerId == playerId ||
                    !IsInfluenceOnPaymentKey(influence, paymentKey, paymentKeyMode))
                {
                    continue;
                }

                if (!owners.Contains(ownerPlayerId))
                {
                    owners.Add(ownerPlayerId);
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
