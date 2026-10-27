using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Exploration;
using YC.Domain.Effects;
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
        public Func<GameState, int, bool> HasCompanyContract { get; set; } = CompanyInfluenceRules.HasContract;
        public EffectRegistry RuleRegistry { get; set; }

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

        // 资格查询不寻路，也不改变阶段预算；实体状态与旧状态都沿原采集权规则。
        public IReadOnlyList<string> GetSelectableLocationIds(GameState state, int playerId, bool extra = false)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var player = state.FindPlayer(playerId);
            if (player == null || (!extra && player.HasCollectedResourcesThisRound)) return Array.AsReadOnly(new string[0]);
            HashSet<string> allowed;
            if (YC.Domain.Items.ItemZoneLegacyProjection.IsActive(state))
                allowed = new HashSet<string>(new InfluenceSubjectQuery().GetCollectionLocationIds(state, playerId, extra ? "extra" : "normal",
                    !extra && HasCompanyContract?.Invoke(state, playerId) == true), StringComparer.Ordinal);
            else
            {
                allowed = new HashSet<string>(StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(player.CityLocationId)) allowed.Add(player.CityLocationId);
                foreach (var influence in state.Map.Influences.ToArray())
                {
                    if (influence == null || influence.PlayerId != playerId) continue;
                    if (!string.IsNullOrEmpty(influence.LocationId)) allowed.Add(influence.LocationId);
                    else if (InfluenceSlotReference.TryParse(mapQuery, influence.SlotId, out var slot, out _) && slot.Kind == InfluenceSlotKind.Location)
                        allowed.Add(slot.LocationId);
                }
            }
            var tokens = new HashSet<string>(state.Map.ResourceTokens.ToArray().Select(t => t.LocationId), StringComparer.Ordinal);
            return Array.AsReadOnly(mapQuery.Map.Locations.Where(l => allowed.Contains(l.LocationId) && tokens.Contains(l.LocationId)).Select(l => l.LocationId).ToArray());
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
            => CollectCore(state, playerId, locationIds, routeIds, paymentRecipientsByRouteId, "normal", -1);

        public ResourceCollectionResult CollectExtra(GameState state, int playerId, IReadOnlyList<string> locationIds, IReadOnlyList<string> routeIds,
            IDictionary<string, int> paymentRecipientsByRouteId, int startGoldVoucherAmount)
            => CollectCore(state, playerId, locationIds, routeIds, paymentRecipientsByRouteId, "extra", startGoldVoucherAmount);

        private ResourceCollectionResult CollectCore(GameState state, int playerId, IReadOnlyList<string> locationIds, IReadOnlyList<string> routeIds,
            IDictionary<string, int> paymentRecipientsByRouteId, string scope, int startGold, bool allowCollectionSelections = false,
            CollectionFreeLocationResult preparedFree = null, bool legacyPlan = false)
        {
            CollectionFreeLocationResult free = preparedFree;
            if (!legacyPlan && free == null && state?.FindPlayer(playerId) != null)
            {
                try { free = QueryFree(state, playerId, scope); }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
                { return ResourceCollectionResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, error.Message)); }
            }
            var validation = CanCollectCore(state, playerId, locationIds, routeIds, paymentRecipientsByRouteId, scope, startGold, allowCollectionSelections, free, legacyPlan);
            if (!validation.IsValid)
            {
                return ResourceCollectionResult.Failure(validation);
            }

            if (scope == "normal") EnsureResourceCollectionGoldSnapshot(state);

            var player = state.FindPlayer(playerId);
            var uniqueLocationIds = NormalizeLocationIds(locationIds);
            List<ExplorationTravelPayment> payments;
            var paymentValidation = BuildCanonicalPayments(state, playerId, uniqueLocationIds, routeIds, paymentRecipientsByRouteId, out payments, scope, free, legacyPlan);
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
            if (scope == "normal") player.HasCollectedResourcesThisRound = true;

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
            => CanCollectCore(state, playerId, locationIds, routeIds, paymentRecipientsByRouteId, "normal", -1);

        public ValidationResult CanCollectExtra(GameState state, int playerId, IReadOnlyList<string> locationIds, IReadOnlyList<string> routeIds,
            IDictionary<string, int> recipients, int startGold) => CanCollectCore(state, playerId, locationIds, routeIds, recipients, "extra", startGold);

        // 仅权威选择Effect使用；并行普通采集请求不阻塞另一已复验的玩家结算。
        internal ResourceCollectionResult CollectSelection(GameState state, int playerId, IReadOnlyList<string> locations, IReadOnlyList<string> routes,
            IDictionary<string, int> recipients, bool extra, int startGold)
            => CollectCore(state, playerId, locations, routes, recipients, extra ? "extra" : "normal", startGold, true);

        private CollectionFreeLocationResult QueryFree(GameState state, int playerId, string scope)
        {
            var query = new CollectionQueryContext(playerId, scope, "", "direct.collection",
                state.FindPlayer(playerId).CityLocationId, state.ContentPackHash,
                GetSelectableLocationIds(state, playerId, scope == "extra"));
            return new CollectionFreeLocationQuery(RuleRegistry, mapQuery).Query(state, query);
        }

        internal ResourceCollectionResult CollectPlan(GameState state, RouteSelectionSession session, RouteSelectionCommitment commitment)
        {
            if (state == null || session == null || commitment?.AcceptedPreview == null || commitment.SessionId != session.SessionId ||
                commitment.OwnerEffectId != session.OwnerEffectId || commitment.PlayerId != session.PlayerId ||
                commitment.BudgetBindingId != session.BudgetBindingId)
                return ResourceCollectionResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "collection_plan_context_invalid"));
            var owner = state.EffectRuntime?.EffectNodes.Find(n => n.EffectId == session.OwnerEffectId);
            if (owner == null || owner.PlayerId != session.PlayerId)
                return ResourceCollectionResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "collection_plan_owner_missing"));
            if (RouteSelectionSessionAuthority.Value(owner.ContinuationData, "collectionCommitmentId") != null)
                return ResourceCollectionResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "collection_already_committed"));
            var preview = commitment.AcceptedPreview;
            if (RouteResourceSelectionService.PlanDigest(session, preview) != preview.PlanDigest)
                return ResourceCollectionResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "collection_plan_digest_invalid"));
            CollectionFreeLocationResult free = null;
            if (session.SchemaVersion == CollectionPlanService.ProtocolVersion)
            {
                var saved = CollectionPlanService.ValidateSaved(session, preview);
                if (!saved.IsValid) return ResourceCollectionResult.Failure(saved);
                var legal = GetSelectableLocationIds(state, session.PlayerId, session.Purpose == RouteSelectionPurpose.ExtraHarvest)
                    .Where(id => session.SelectableLocationUpperBound.Contains(id)).ToArray();
                var current = CollectionPlanService.ValidateCurrent(state, mapQuery, session, legal, preview.Collection.FreeLocations);
                if (!current.IsValid) return ResourceCollectionResult.Failure(current);
                try
                {
                    free = new CollectionFreeLocationQuery(RuleRegistry, mapQuery).Query(state, CollectionPlanService.Context(state, session, legal));
                    if (free.ResultDigest != preview.Collection.FreeLocations.ResultDigest)
                        return ResourceCollectionResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "collection_free_facts_changed"));
                }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
                { return ResourceCollectionResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, error.Message)); }
            }
            var recipients = preview.PaymentPlan.Lines.ToDictionary(l => l.RouteId,
                l => l.ReceiverSubjectKey == "bank" ? -1 : int.Parse(l.ReceiverSubjectKey.Substring(7), System.Globalization.CultureInfo.InvariantCulture));
            string scope = session.Purpose == RouteSelectionPurpose.ExtraHarvest ? "extra" : "normal";
            var checkedPayments = BuildCanonicalPayments(state, session.PlayerId, preview.SelectedLocationIds,
                preview.OriginalConfirmedRouteIds, recipients, out var payments, scope, free, session.SchemaVersion == 1);
            if (!checkedPayments.IsValid || payments.Count != preview.PaymentPlan.Lines.Count ||
                payments.Any(p => !preview.PaymentPlan.Lines.Any(l => l.RouteId == p.RouteId && l.GoldVoucherAmount == p.Amount &&
                    l.ReceiverSubjectKey == (p.ReceiverPlayerId < 0 ? "bank" : "player:" + p.ReceiverPlayerId))))
                return ResourceCollectionResult.Failure(checkedPayments.IsValid ?
                    ValidationResult.Failure(CommandErrorCode.InvalidTarget, "collection_payment_plan_changed") : checkedPayments);
            // 防重字段先在克隆值上验证容量，所有付费、收益和收据仍在调用Effect的同一步工作提交。
            var receipt = owner.ContinuationData.Clone();
            RouteSelectionSessionAuthority.Set(receipt, "collectionCommitmentId", NormalizedValue.CreateString(commitment.CommitmentId));
            if (!receipt.IsValid()) return ResourceCollectionResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "collection_receipt_too_large"));
            var result = CollectCore(state, session.PlayerId, preview.SelectedLocationIds, preview.OriginalConfirmedRouteIds,
                recipients, scope, session.StartGoldVoucherAmount, true, free, session.SchemaVersion == 1);
            if (result.Succeeded) owner.ContinuationData = receipt;
            return result;
        }

        private ValidationResult CanCollectCore(GameState state, int playerId, IReadOnlyList<string> locationIds, IReadOnlyList<string> routeIds,
            IDictionary<string, int> paymentRecipientsByRouteId, string scope, int startGold, bool allowCollectionSelections = false,
            CollectionFreeLocationResult free = null, bool legacyPlan = false)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var phaseValidation = scope == "normal" ? CommandPhasePolicy.ValidatePhase(state, GameCommandKind.CollectResource) : ValidationResult.Success;
            if (!phaseValidation.IsValid)
            {
                return phaseValidation;
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "采集玩家不存在。");
            }

            if (scope == "normal" && state.HasPendingChoice() && (!allowCollectionSelections || HasUnrelatedChoice(state)))
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "请先处理待选择项再采集。");
            }

            if (scope == "normal" && player.HasCollectedResourcesThisRound)
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

                if (!CanPlayerCollectLocation(state, playerId, locationId, scope))
                {
                    return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "只能采集有自己影响力或自己移动城市停靠的资源点。");
                }
            }

            List<ExplorationTravelPayment> payments;
            var paymentValidation = BuildCanonicalPayments(state, playerId, uniqueLocationIds, routeIds, paymentRecipientsByRouteId, out payments, scope, free, legacyPlan);
            if (!paymentValidation.IsValid)
            {
                return paymentValidation;
            }

            var totalCost = routeTollService.SumPayments(payments);
            var collectionStartGold = scope == "extra" ? startGold : GetResourceCollectionStartGoldVoucher(player);
            var payableGold = Math.Min(player.Resources.GoldVoucher, collectionStartGold);
            if (payableGold < totalCost)
            {
                return ValidationResult.Failure(
                    CommandErrorCode.InsufficientResource,
                    "玩家阶段开始时的金币不足，不能使用本采集阶段新获得的金币支付路费。");
            }

            return ValidationResult.Success;
        }

        private ValidationResult BuildCanonicalPayments(GameState state, int playerId, IReadOnlyList<string> selectedLocations, IReadOnlyList<string> confirmedRoutes,
            IDictionary<string, int> requestedRecipients, out List<ExplorationTravelPayment> payments, string scope = "normal",
            CollectionFreeLocationResult free = null, bool legacyPlan = false)
        {
            payments = new List<ExplorationTravelPayment>();
            if (!legacyPlan && free == null)
            {
                try { free = QueryFree(state, playerId, scope); }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
                { return ValidationResult.Failure(CommandErrorCode.InvalidTarget, error.Message); }
            }
            var routeTargets = legacyPlan ? selectedLocations : selectedLocations.Where(id => !free.FreeLocationIds.Contains(id)).ToArray();
            var routeEligible = legacyPlan ? selectedLocations : free.LegalLocationIds.Where(id => !free.FreeLocationIds.Contains(id)).ToArray();
            var facts = routeTollService.ReadRouteFacts(state, playerId);
            var graph = RouteResourceSelectionService.QueryGraph(mapQuery, facts, state.FindPlayer(playerId).CityLocationId,
                confirmedRoutes ?? Array.AsReadOnly(new string[0]), routeEligible, routeTargets, selectedLocations.Count == 0);
            if (!graph.IsValid) return ValidationResult.Failure(CommandErrorCode.InvalidTarget, graph.Diagnostic);
            var byId = facts.ToDictionary(f => f.RouteId, StringComparer.Ordinal);
            if (requestedRecipients != null) foreach (var pair in requestedRecipients)
            {
                string subject = pair.Value < 0 ? "bank" : "player:" + pair.Value;
                if (!graph.OriginalConfirmedRouteIds.Contains(pair.Key) || !byId[pair.Key].EligibleRecipients.Any(r => r.SubjectKey == subject))
                    return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "路费接收者不是本次确认航道的合法主体。");
            }
            foreach (string route in graph.ChargeRouteIds)
            {
                var fact = byId[route]; string subject = null;
                int receiver;
                if (requestedRecipients != null && requestedRecipients.TryGetValue(route, out receiver)) subject = receiver < 0 ? "bank" : "player:" + receiver;
                else if (fact.EligibleRecipients.Count == 1) subject = fact.EligibleRecipients[0].SubjectKey;
                if (subject == null) return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "该航道需要明确选择路费接收者。");
                payments.Add(new ExplorationTravelPayment { RouteId = route, Amount = fact.TollGoldVoucherAmount, ReceiverPlayerId = subject == "bank" ? -1 : int.Parse(subject.Substring(7)) });
            }
            return ValidationResult.Success;
        }

        private static bool HasUnrelatedChoice(GameState state)
        {
            if (state.PendingChoice?.IsValid() == true || state.PendingCardSession?.IsValid() == true || state.PendingCharacterEffect?.IsValid() == true || state.PendingSpecialAction?.IsValid(state) == true) return true;
            return state.EffectRuntime != null && state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && !r.IsInternalMainlineInteraction()
                && (r.InteractionTypeId != "route_resource.selection" || RouteSelectionSessionAuthority.Integer(r.PromptParameters, "purpose", -1) != (int)RouteSelectionPurpose.NormalHarvest));
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

            try { result.FreeLocations = QueryFree(state, playerId, "normal"); }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
            { return ResourceCollectionSelectionQuery.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, error.Message)); }
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
                if (!result.IsFreeLocation(locationId) && reachablePaths.TryGetValue(locationId, out path))
                {
                    result.AddPath(locationId, path);
                }
            }

            // 只为通向合法采集目标的航道提供支付选项，不能从城市周围任意扩张。
            // 排除已可达范围再从待采集目标反向查找，避免无关支路绕回城市也被当作有效路线。
            var pendingTargetLocations = new HashSet<string>(StringComparer.Ordinal);
            var remainingRouteIds = new List<string>();
            for (var i = 0; i < mapQuery.Map.Routes.Count; i++)
            {
                var route = mapQuery.Map.Routes[i];
                if (!RouteTouchesReachableLocation(route, reachablePaths))
                {
                    remainingRouteIds.Add(route.RouteId);
                }
            }

            foreach (var locationId in result.CandidateLocationIds)
            {
                if (!result.IsFreeLocation(locationId) && !reachablePaths.ContainsKey(locationId))
                {
                    pendingTargetLocations.UnionWith(BuildReachableLocations(locationId, remainingRouteIds));
                }
            }

            for (var routeIndex = 0; routeIndex < mapQuery.Map.Routes.Count; routeIndex++)
            {
                var route = mapQuery.Map.Routes[routeIndex];
                if (IsCollectionRouteSatisfied(state, playerId, route.RouteId, confirmedPaymentKeys) ||
                    !RouteTouchesReachableLocation(route, reachablePaths) ||
                    !RouteTouchesPendingTarget(route, pendingTargetLocations))
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

        private static bool RouteTouchesPendingTarget(MapRouteDefinition route, ISet<string> pendingTargetLocations)
        {
            var coveredLocationIds = GetRouteCoveredLocationIds(route);
            for (var i = 0; i < coveredLocationIds.Count; i++)
            {
                if (pendingTargetLocations.Contains(coveredLocationIds[i]))
                {
                    return true;
                }
            }

            return false;
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

        public IReadOnlyList<string> GetExtraRouteIds(GameState state, int playerId, IReadOnlyList<string> locationIds) => ResolveCollectionRouteIds(state.FindPlayer(playerId).CityLocationId, locationIds, null);

        private bool CanPlayerCollectLocation(GameState state, int playerId, string locationId, string scope = "normal")
        {
            if (YC.Domain.Items.ItemZoneLegacyProjection.IsActive(state))
                return new InfluenceSubjectQuery().GetCollectionLocationIds(state, playerId, scope, scope == "normal" && HasCompanyContract?.Invoke(state, playerId) == true).Contains(locationId);
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
