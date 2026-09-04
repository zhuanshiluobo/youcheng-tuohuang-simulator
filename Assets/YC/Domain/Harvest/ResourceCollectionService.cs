using System;
using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Exploration;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Domain.Harvest
{
    public sealed class ResourceCollectionService
    {
        // 采集阶段按具体航道记录支付；同一区域的其他航道仍需分别满足路费条件。
        private const RouteTollPaymentKeyMode CollectionPaymentKeyMode = RouteTollPaymentKeyMode.RouteId;
        private const RouteTollPaymentKeyMode CollectionInfluenceKeyMode = RouteTollPaymentKeyMode.RouteId;

        private readonly IMapQueryService mapQuery;
        private readonly ResourceTokenService resourceTokenService;
        private readonly RouteTollService routeTollService;

        public ResourceCollectionService(IMapQueryService mapQuery)
            : this(mapQuery, new ResourceTokenService())
        {
        }

        public ResourceCollectionService(IMapQueryService mapQuery, ResourceTokenService resourceTokenService)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.resourceTokenService = resourceTokenService ?? throw new ArgumentNullException(nameof(resourceTokenService));
            routeTollService = new RouteTollService(this.mapQuery);
        }

        public ResourceCollectionResult Collect(
            GameState state,
            int playerId,
            IReadOnlyList<string> locationIds,
            IDictionary<string, int> paymentRecipientsByRouteId)
        {
            return Collect(state, playerId, locationIds, null, paymentRecipientsByRouteId);
        }

        public ResourceCollectionResult Collect(
            GameState state,
            int playerId,
            IReadOnlyList<string> locationIds,
            IReadOnlyList<string> routeIds,
            IDictionary<string, int> paymentRecipientsByRouteId)
        {
            var validation = CanCollect(state, playerId, locationIds, routeIds, paymentRecipientsByRouteId);
            if (!validation.IsValid)
            {
                return ResourceCollectionResult.Failure(validation);
            }

            EnsureResourceCollectionGoldSnapshot(state);

            var player = state.FindPlayer(playerId);
            var uniqueLocationIds = NormalizeLocationIds(locationIds);
            var collectionRouteIds = ResolveCollectionRouteIds(player.CityLocationId, uniqueLocationIds, routeIds);
            List<ExplorationTravelPayment> payments;
            var paymentValidation = routeTollService.TryBuildPaymentPlanWithInfluenceKeyMode(
                state,
                playerId,
                collectionRouteIds,
                paymentRecipientsByRouteId,
                CollectionPaymentKeyMode,
                CollectionInfluenceKeyMode,
                out payments);
            if (!paymentValidation.IsValid)
            {
                return ResourceCollectionResult.Failure(paymentValidation);
            }

            routeTollService.ApplyPayments(state, player, payments);

            var reward = new ResourceSet();
            for (var i = 0; i < uniqueLocationIds.Count; i++)
            {
                var token = resourceTokenService.FindToken(state.Map, uniqueLocationIds[i]);
                AddTokenReward(reward, token);
            }

            player.Resources.Add(reward);
            player.HasCollectedResourcesThisRound = true;

            return ResourceCollectionResult.Success(uniqueLocationIds, payments, reward);
        }

        public ValidationResult CanCollect(
            GameState state,
            int playerId,
            IReadOnlyList<string> locationIds,
            IDictionary<string, int> paymentRecipientsByRouteId)
        {
            return CanCollect(state, playerId, locationIds, null, paymentRecipientsByRouteId);
        }

        public ValidationResult CanCollect(
            GameState state,
            int playerId,
            IReadOnlyList<string> locationIds,
            IReadOnlyList<string> routeIds,
            IDictionary<string, int> paymentRecipientsByRouteId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var phaseValidation = CommandPhasePolicy.ValidatePhase(state, GameCommandKind.CollectResource);
            if (!phaseValidation.IsValid)
            {
                return phaseValidation;
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "采集玩家不存在。");
            }

            if (state.HasPendingChoice())
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "请先处理待选择项再采集。");
            }

            if (player.HasCollectedResourcesThisRound)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "该玩家本轮采集阶段已经提交过采集。");
            }

            if (string.IsNullOrEmpty(player.CityLocationId))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidSource, "玩家城市不在场上，无法采集。");
            }

            var uniqueLocationIds = NormalizeLocationIds(locationIds);
            if (uniqueLocationIds.Count != (locationIds == null ? 0 : locationIds.Count))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "同一个资源点不能在一次采集中重复选择。");
            }

            for (var i = 0; i < uniqueLocationIds.Count; i++)
            {
                var locationId = uniqueLocationIds[i];
                try
                {
                    mapQuery.GetLocation(locationId);
                }
                catch (ArgumentException)
                {
                    return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "采集目标必须是地图上的资源点。");
                }

                if (!resourceTokenService.HasResourceToken(state.Map, locationId))
                {
                    return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "采集目标必须已经放置资源点指示物。");
                }

                if (!CanPlayerCollectLocation(state, playerId, locationId))
                {
                    return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "只能采集有自己影响力或自己移动城市停靠的资源点。");
                }
            }

            var collectionRouteIds = ResolveCollectionRouteIds(player.CityLocationId, uniqueLocationIds, routeIds);
            var routeValidation = ValidateCollectionRoutes(player.CityLocationId, uniqueLocationIds, collectionRouteIds);
            if (!routeValidation.IsValid)
            {
                return routeValidation;
            }

            List<ExplorationTravelPayment> payments;
            var paymentValidation = routeTollService.TryBuildPaymentPlanWithInfluenceKeyMode(
                state,
                playerId,
                collectionRouteIds,
                paymentRecipientsByRouteId,
                CollectionPaymentKeyMode,
                CollectionInfluenceKeyMode,
                out payments);
            if (!paymentValidation.IsValid)
            {
                return paymentValidation;
            }

            var totalCost = routeTollService.SumPayments(payments);
            var collectionStartGold = GetResourceCollectionStartGoldVoucher(player);
            var payableGold = Math.Min(player.Resources.GoldVoucher, collectionStartGold);
            if (payableGold < totalCost)
            {
                return ValidationResult.Failure(
                    CommandErrorCode.InsufficientResource,
                    "玩家阶段开始时的金币不足，不能使用本采集阶段新获得的金币支付路费。");
            }

            return ValidationResult.Success;
        }

        public void EnsureResourceCollectionGoldSnapshot(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var hasMissingSnapshot = false;
            for (var i = 0; i < state.Players.Count; i++)
            {
                if (state.Players[i].ResourceCollectionStartGoldVoucher < 0)
                {
                    hasMissingSnapshot = true;
                    break;
                }
            }

            if (!hasMissingSnapshot)
            {
                return;
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                state.Players[i].ResourceCollectionStartGoldVoucher = state.Players[i].Resources.GoldVoucher;
            }
        }

        public ResourceCollectionSelectionQuery QuerySelection(
            GameState state,
            int playerId,
            IReadOnlyCollection<string> confirmedPaidRouteIds)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var baseValidation = CanCollect(
                state,
                playerId,
                new List<string>(),
                new List<string>(),
                null);
            if (!baseValidation.IsValid)
            {
                return ResourceCollectionSelectionQuery.Failure(baseValidation);
            }

            var player = state.FindPlayer(playerId);
            var confirmedPaymentKeys = new HashSet<string>(StringComparer.Ordinal);
            if (confirmedPaidRouteIds != null)
            {
                foreach (var routeId in confirmedPaidRouteIds)
                {
                    MapRouteDefinition route;
                    try
                    {
                        route = mapQuery.GetRoute(routeId);
                    }
                    catch (ArgumentException)
                    {
                        return ResourceCollectionSelectionQuery.Failure(ValidationResult.Failure(
                            CommandErrorCode.InvalidTarget,
                            "已确认路费包含不存在的航道。"));
                    }

                    if (routeTollService.IsPaymentRequired(
                        state,
                        route.RouteId,
                        playerId,
                        CollectionInfluenceKeyMode))
                    {
                        confirmedPaymentKeys.Add(routeTollService.GetRoutePaymentKey(
                            route.RouteId,
                            CollectionPaymentKeyMode));
                    }
                }
            }

            var result = new ResourceCollectionSelectionQuery
            {
                AvailableGoldVoucher = GetResourceCollectionStartGoldVoucher(player),
                ConfirmedTollCost = confirmedPaymentKeys.Count * RouteTollService.RouteCostGoldVoucher
            };

            var pathSearch = new MapPathSearchService(mapQuery);
            var reachablePaths = pathSearch.FindReachablePaths(
                player.CityLocationId,
                route => IsCollectionRouteSatisfied(state, playerId, route.RouteId, confirmedPaymentKeys));

            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var locationId = mapQuery.Map.Locations[i].LocationId;
                if (!resourceTokenService.HasResourceToken(state.Map, locationId) ||
                    !CanPlayerCollectLocation(state, playerId, locationId))
                {
                    continue;
                }

                result.CandidateLocationIds.Add(locationId);
                MapPath path;
                if (reachablePaths.TryGetValue(locationId, out path))
                {
                    result.AddPath(locationId, path);
                }
            }

            for (var routeIndex = 0; routeIndex < mapQuery.Map.Routes.Count; routeIndex++)
            {
                var route = mapQuery.Map.Routes[routeIndex];
                if (IsCollectionRouteSatisfied(state, playerId, route.RouteId, confirmedPaymentKeys) ||
                    !RouteTouchesReachableLocation(route, reachablePaths))
                {
                    continue;
                }

                var paymentKey = routeTollService.GetRoutePaymentKey(
                    route.RouteId,
                    CollectionPaymentKeyMode);
                result.AddRouteOption(new ResourceCollectionRouteOption
                {
                    RouteId = route.RouteId,
                    PaymentKey = paymentKey,
                    Cost = RouteTollService.RouteCostGoldVoucher,
                    CanAfford = result.ConfirmedTollCost + RouteTollService.RouteCostGoldVoucher <=
                                result.AvailableGoldVoucher,
                    OpponentOwnerPlayerIds = routeTollService.GetOpponentInfluenceOwnersOnPaymentKey(
                        state,
                        route.RouteId,
                        playerId,
                        CollectionInfluenceKeyMode)
                });
            }

            return result;
        }

        private bool IsCollectionRouteSatisfied(
            GameState state,
            int playerId,
            string routeId,
            ISet<string> confirmedPaymentKeys)
        {
            if (!routeTollService.IsPaymentRequired(
                state,
                routeId,
                playerId,
                CollectionInfluenceKeyMode))
            {
                return true;
            }

            var paymentKey = routeTollService.GetRoutePaymentKey(
                routeId,
                CollectionPaymentKeyMode);
            return confirmedPaymentKeys.Contains(paymentKey);
        }

        private static bool RouteTouchesReachableLocation(
            MapRouteDefinition route,
            IReadOnlyDictionary<string, MapPath> reachablePaths)
        {
            var coveredLocationIds = GetRouteCoveredLocationIds(route);
            for (var i = 0; i < coveredLocationIds.Count; i++)
            {
                if (reachablePaths.ContainsKey(coveredLocationIds[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private List<MapPath> BuildDefaultPaths(string sourceLocationId, IReadOnlyList<string> locationIds)
        {
            var paths = new List<MapPath>();
            var pathSearch = new MapPathSearchService(mapQuery);
            for (var i = 0; i < locationIds.Count; i++)
            {
                paths.Add(pathSearch.FindShortestPath(sourceLocationId, locationIds[i]));
            }

            return paths;
        }

        private static List<string> CollectUniqueRouteIds(IReadOnlyList<MapPath> paths)
        {
            var routeIds = new List<string>();
            for (var i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                for (var routeIndex = 0; routeIndex < path.RouteIds.Count; routeIndex++)
                {
                    var routeId = path.RouteIds[routeIndex];
                    if (!routeIds.Contains(routeId))
                    {
                        routeIds.Add(routeId);
                    }
                }
            }

            return routeIds;
        }

        private IReadOnlyList<string> ResolveCollectionRouteIds(
            string sourceLocationId,
            IReadOnlyList<string> locationIds,
            IReadOnlyList<string> explicitRouteIds)
        {
            var normalizedRouteIds = NormalizeRouteIds(explicitRouteIds);
            if (normalizedRouteIds.Count > 0)
            {
                return normalizedRouteIds;
            }

            var paths = BuildDefaultPaths(sourceLocationId, locationIds);
            return CollectUniqueRouteIds(paths);
        }

        private ValidationResult ValidateCollectionRoutes(
            string sourceLocationId,
            IReadOnlyList<string> locationIds,
            IReadOnlyList<string> routeIds)
        {
            for (var i = 0; i < routeIds.Count; i++)
            {
                try
                {
                    mapQuery.GetRoute(routeIds[i]);
                }
                catch (ArgumentException)
                {
                    return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "采集路线包含不存在的航道。");
                }
            }

            if (!CanReachLocationsUsingRoutes(sourceLocationId, locationIds, routeIds))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "采集目标必须能通过已选择的航道联通。");
            }

            return ValidationResult.Success;
        }

        private bool CanReachLocationsUsingRoutes(
            string sourceLocationId,
            IReadOnlyList<string> locationIds,
            IReadOnlyList<string> routeIds)
        {
            if (locationIds.Count == 0)
            {
                return true;
            }

            var reachable = BuildReachableLocations(sourceLocationId, routeIds);
            for (var i = 0; i < locationIds.Count; i++)
            {
                if (!reachable.Contains(locationIds[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private HashSet<string> BuildReachableLocations(string sourceLocationId, IReadOnlyList<string> routeIds)
        {
            var allowedRoutes = new HashSet<string>(routeIds);
            var reachable = new HashSet<string> { sourceLocationId };
            var queue = new Queue<string>();
            queue.Enqueue(sourceLocationId);

            while (queue.Count > 0)
            {
                var currentLocationId = queue.Dequeue();
                for (var routeIndex = 0; routeIndex < mapQuery.Map.Routes.Count; routeIndex++)
                {
                    var route = mapQuery.Map.Routes[routeIndex];
                    if (!allowedRoutes.Contains(route.RouteId) ||
                        !RouteCoversLocation(route, currentLocationId))
                    {
                        continue;
                    }

                    var coveredLocationIds = GetRouteCoveredLocationIds(route);
                    for (var locationIndex = 0; locationIndex < coveredLocationIds.Count; locationIndex++)
                    {
                        var nextLocationId = coveredLocationIds[locationIndex];
                        if (reachable.Contains(nextLocationId))
                        {
                            continue;
                        }

                        reachable.Add(nextLocationId);
                        queue.Enqueue(nextLocationId);
                    }
                }
            }

            return reachable;
        }

        private static bool RouteCoversLocation(MapRouteDefinition route, string locationId)
        {
            var coveredLocationIds = GetRouteCoveredLocationIds(route);
            for (var i = 0; i < coveredLocationIds.Count; i++)
            {
                if (coveredLocationIds[i] == locationId)
                {
                    return true;
                }
            }

            return false;
        }

        private static IReadOnlyList<string> GetRouteCoveredLocationIds(MapRouteDefinition route)
        {
            if (route.CoveredLocationIds != null && route.CoveredLocationIds.Count > 0)
            {
                return route.CoveredLocationIds;
            }

            return new List<string> { route.FromLocationId, route.ToLocationId };
        }

        private bool CanPlayerCollectLocation(GameState state, int playerId, string locationId)
        {
            var player = state.FindPlayer(playerId);
            if (player != null && player.CityLocationId == locationId)
            {
                return true;
            }

            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId == playerId && IsInfluenceOnLocation(influence, locationId))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsInfluenceOnLocation(InfluencePlacement influence, string locationId)
        {
            if (influence.LocationId == locationId)
            {
                return true;
            }

            InfluenceSlotReference slot;
            string reason;
            return InfluenceSlotReference.TryParse(mapQuery, influence.SlotId, out slot, out reason) &&
                   slot.Kind == InfluenceSlotKind.Location &&
                   slot.LocationId == locationId;
        }

        private static List<string> NormalizeLocationIds(IReadOnlyList<string> locationIds)
        {
            var result = new List<string>();
            if (locationIds == null)
            {
                return result;
            }

            for (var i = 0; i < locationIds.Count; i++)
            {
                var locationId = locationIds[i];
                if (string.IsNullOrEmpty(locationId))
                {
                    continue;
                }

                locationId = locationId.Trim();
                if (!result.Contains(locationId))
                {
                    result.Add(locationId);
                }
            }

            return result;
        }

        private static List<string> NormalizeRouteIds(IReadOnlyList<string> routeIds)
        {
            var result = new List<string>();
            if (routeIds == null)
            {
                return result;
            }

            for (var i = 0; i < routeIds.Count; i++)
            {
                var routeId = routeIds[i];
                if (string.IsNullOrEmpty(routeId))
                {
                    continue;
                }

                routeId = routeId.Trim();
                if (!result.Contains(routeId))
                {
                    result.Add(routeId);
                }
            }

            return result;
        }

        private static void AddTokenReward(ResourceSet reward, ResourceTokenState token)
        {
            if (token == null)
            {
                return;
            }

            switch (token.ResourceType)
            {
                case ResourceType.Originium:
                    reward.Originium += token.Amount;
                    break;
                case ResourceType.OriginiumShard:
                    reward.OriginiumShard += token.Amount;
                    break;
                case ResourceType.Iron:
                    reward.Iron += token.Amount;
                    break;
                case ResourceType.PureOriginium:
                    reward.PureOriginium += token.Amount;
                    break;
                case ResourceType.GoldVoucher:
                    reward.GoldVoucher += token.Amount;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static int GetResourceCollectionStartGoldVoucher(PlayerState player)
        {
            return player.ResourceCollectionStartGoldVoucher >= 0
                ? player.ResourceCollectionStartGoldVoucher
                : player.Resources.GoldVoucher;
        }
    }
}
