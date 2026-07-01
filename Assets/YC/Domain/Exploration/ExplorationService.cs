using System;
using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.CardFlows;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Exploration
{
    public sealed class ExplorationService
    {
        public const int RouteCostGoldVoucher = 2;

        private readonly IMapQueryService mapQuery;
        private readonly InfluenceService influenceService;
        private readonly EventDeckService eventDeckService;
        private readonly ResourceTokenService resourceTokenService;
        private readonly EventEffectResolver eventEffectResolver;
        private readonly CardFlowService cardFlowService;

        public ExplorationService(IMapQueryService mapQuery)
            : this(mapQuery, new InfluenceService(mapQuery), new EventDeckService(), new ResourceTokenService())
        {
        }

        public ExplorationService(
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            EventDeckService eventDeckService,
            ResourceTokenService resourceTokenService)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
            this.eventDeckService = eventDeckService ?? throw new ArgumentNullException(nameof(eventDeckService));
            this.resourceTokenService = resourceTokenService ?? throw new ArgumentNullException(nameof(resourceTokenService));
            eventEffectResolver = new EventEffectResolver(this.mapQuery, this.influenceService);
            cardFlowService = new CardFlowService();
        }

        public ValidationResult CanExplore(
            GameState state,
            int playerId,
            string targetLocationId,
            MapPath path,
            int selectedOptionIndex,
            string influenceSlotId,
            IDictionary<string, int> paymentRecipientsByRouteId,
            IReadOnlyList<string> eventInfluenceSlotIds = null,
            bool validateEventOption = true)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "探索玩家不存在。");
            }

            var phaseValidation = CommandPhasePolicy.ValidatePhase(state, GameCommandKind.ExploreLocation);
            if (!phaseValidation.IsValid)
            {
                return phaseValidation;
            }

            if (state.CurrentPlayerId != playerId)
            {
                return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "当前不是该玩家的行动回合。");
            }

            if (state.HasPendingChoice())
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "请先处理待选择项再探索。");
            }

            if (player.ActedMainActionThisTurn)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "该玩家本行动轮已执行过主要行动。");
            }

            if (string.IsNullOrEmpty(player.CityLocationId))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidSource, "玩家城市不在场上。");
            }

            MapLocationDefinition targetLocation;
            try
            {
                targetLocation = mapQuery.GetLocation(targetLocationId);
            }
            catch (ArgumentException)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "探索目标必须是已知资源点。");
            }

            if (resourceTokenService.HasResourceToken(state.Map, targetLocationId))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "已有资源点指示物的位置不能再次探索。");
            }

            if (IsRedZoneClosed(state, targetLocation))
            {
                return ValidationResult.Failure(CommandErrorCode.ClosedLocation, "红色区域当前回合尚未开放探索。");
            }

            var pathValidation = ValidatePath(player.CityLocationId, targetLocationId, path);
            if (!pathValidation.IsValid)
            {
                return pathValidation;
            }

            var eventColor = StaticMapDefinitions.GetEventColor(targetLocationId);
            if (eventDeckService.RemainingCount(state.Decks, eventColor) <= 0)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "对应颜色事件牌堆已空。");
            }

            var card = EventCardDatabase.Get(PeekEventCardId(state.Decks, eventColor));
            if (card == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "事件牌数据不存在。");
            }

            if (card.ChoiceRewards.Count <= 0)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "探索事件没有可用选项。");
            }

            if (validateEventOption && (selectedOptionIndex < 0 || selectedOptionIndex >= card.ChoiceRewards.Count))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "探索事件选项无效。");
            }

            var slotValidation = ValidateExplorationInfluenceSlot(state, playerId, targetLocationId, influenceSlotId);
            if (!slotValidation.IsValid)
            {
                return slotValidation;
            }

            List<ExplorationTravelPayment> payments;
            var paymentValidation = TryBuildPaymentPlan(
                state,
                playerId,
                path,
                paymentRecipientsByRouteId,
                out payments);
            if (!paymentValidation.IsValid)
            {
                return paymentValidation;
            }

            var totalCost = SumPayments(payments);
            var travelCost = new ResourceSet { GoldVoucher = totalCost };
            if (!player.Resources.CanPay(travelCost))
            {
                return ValidationResult.Failure(CommandErrorCode.InsufficientResource, "玩家金券不足，无法支付探索路费。");
            }

            var resolvedSlotId = ResolveInfluenceSlotId(state, playerId, targetLocationId, influenceSlotId);
            if (validateEventOption)
            {
                var eventEffectValidation = eventEffectResolver.Validate(
                    state,
                    playerId,
                    card.ChoicePendingEffects[selectedOptionIndex],
                    targetLocationId,
                    eventInfluenceSlotIds,
                    new List<string> { resolvedSlotId }.AsReadOnly(),
                    travelCost);
                if (!eventEffectValidation.IsValid)
                {
                    return eventEffectValidation;
                }
            }

            return ValidationResult.Success;
        }


        public ExplorationResult Explore(
            GameState state,
            int playerId,
            string targetLocationId,
            MapPath path,
            int selectedOptionIndex,
            string influenceSlotId,
            IDictionary<string, int> paymentRecipientsByRouteId,
            IReadOnlyList<string> eventInfluenceSlotIds = null)
        {
            var validation = CanExplore(
                state,
                playerId,
                targetLocationId,
                path,
                selectedOptionIndex,
                influenceSlotId,
                paymentRecipientsByRouteId,
                eventInfluenceSlotIds);
            if (!validation.IsValid)
            {
                return ExplorationResult.Failure(validation);
            }
            var flowResult = cardFlowService.ExecuteImmediate(
                state,
                new CardFlowExecuteRequest
                {
                    PlayerId = playerId,
                    TargetId = targetLocationId,
                    OptionIndex = selectedOptionIndex,
                    Arguments = BuildCardFlowArguments(path, influenceSlotId, paymentRecipientsByRouteId, eventInfluenceSlotIds)
                },
                new ExploreEventCardScenario(this));
            if (!flowResult.Succeeded)
            {
                return ExplorationResult.Failure(flowResult.Validation);
            }

            return ExplorationResult.Success(
                path.LocationIds[0],
                targetLocationId,
                flowResult.Card.CardId,
                flowResult.Card.Color,
                selectedOptionIndex,
                flowResult.Reward,
                new List<ExplorationTravelPayment>().AsReadOnly(),
                flowResult.PrimaryInfluencePlacement);
        }

        public ExplorationResult BeginExploreEvent(
            GameState state,
            int playerId,
            string targetLocationId,
            MapPath path,
            string influenceSlotId,
            IDictionary<string, int> paymentRecipientsByRouteId,
            string sourceCommandId = null)
        {
            var validation = CanExplore(
                state,
                playerId,
                targetLocationId,
                path,
                -1,
                influenceSlotId,
                paymentRecipientsByRouteId,
                null,
                false);
            if (!validation.IsValid)
            {
                return ExplorationResult.Failure(validation);
            }
            var flowResult = cardFlowService.StartPendingChoice(
                state,
                new CardFlowStartRequest
                {
                    PlayerId = playerId,
                    TargetId = targetLocationId,
                    SourceCommandId = sourceCommandId,
                    Arguments = BuildCardFlowArguments(path, influenceSlotId, paymentRecipientsByRouteId, null)
                },
                new ExploreEventCardScenario(this));
            if (!flowResult.Succeeded)
            {
                return ExplorationResult.Failure(flowResult.Validation);
            }

            return ExplorationResult.Success(
                path.LocationIds[0],
                targetLocationId,
                flowResult.Card.CardId,
                flowResult.Card.Color,
                -1,
                null,
                new List<ExplorationTravelPayment>().AsReadOnly(),
                null);
        }

        public ExplorationResult ResolveExploreEvent(
            GameState state,
            int playerId,
            string targetLocationId,
            string eventCardId,
            int selectedOptionIndex,
            string influenceSlotId,
            IReadOnlyList<string> eventInfluenceSlotIds = null)
        {
            var flowResult = cardFlowService.ResolvePendingChoice(
                state,
                new CardFlowResolveRequest
                {
                    PlayerId = playerId,
                    SessionId = state.PendingCardSession == null ? string.Empty : state.PendingCardSession.SessionId,
                    OptionIndex = selectedOptionIndex,
                    Arguments = BuildCardFlowArguments(null, influenceSlotId, null, eventInfluenceSlotIds)
                },
                new ExploreEventCardScenario(this));
            if (!flowResult.Succeeded)
            {
                return ExplorationResult.Failure(flowResult.Validation);
            }

            return ExplorationResult.Success(
                string.Empty,
                targetLocationId,
                flowResult.Card.CardId,
                flowResult.Card.Color,
                selectedOptionIndex,
                flowResult.Reward,
                new List<ExplorationTravelPayment>().AsReadOnly(),
                flowResult.PrimaryInfluencePlacement);
        }

        internal ValidationResult RevealExploreCard(
            GameState state,
            int playerId,
            string targetLocationId,
            MapPath path,
            IDictionary<string, int> paymentRecipientsByRouteId,
            EventCardDefinition card)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (card == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "探索事件牌不存在。");
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "探索玩家不存在。");
            }

            List<ExplorationTravelPayment> payments;
            var paymentValidation = TryBuildPaymentPlan(state, playerId, path, paymentRecipientsByRouteId, out payments);
            if (!paymentValidation.IsValid)
            {
                return paymentValidation;
            }

            ApplyPayments(state, player, payments);
            resourceTokenService.PlaceToken(
                state.Map,
                targetLocationId,
                card.RepresentativeResourceType,
                card.RepresentativeResourceAmount);
            MarkLocationOpen(state, targetLocationId);
            return ValidationResult.Success;
        }

        internal ValidationResult ValidateResolvedExploreOption(
            GameState state,
            int playerId,
            string targetLocationId,
            EventCardDefinition card,
            int selectedOptionIndex,
            string influenceSlotId,
            IReadOnlyList<string> eventInfluenceSlotIds)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "探索玩家不存在。");
            }

            if (card == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "探索事件牌不存在。");
            }

            if (selectedOptionIndex < 0 || selectedOptionIndex >= card.ChoiceRewards.Count)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "探索事件选项无效。");
            }

            var slotValidation = ValidateExplorationInfluenceSlot(state, playerId, targetLocationId, influenceSlotId);
            if (!slotValidation.IsValid)
            {
                return slotValidation;
            }

            var resolvedSlotId = ResolveInfluenceSlotId(state, playerId, targetLocationId, influenceSlotId);
            return eventEffectResolver.Validate(
                state,
                playerId,
                card.ChoicePendingEffects[selectedOptionIndex],
                targetLocationId,
                eventInfluenceSlotIds,
                new List<string> { resolvedSlotId }.AsReadOnly());
        }

        internal ExplorationResult ApplyExploreOption(
            GameState state,
            int playerId,
            string targetLocationId,
            EventCardDefinition card,
            int selectedOptionIndex,
            string influenceSlotId,
            IReadOnlyList<string> eventInfluenceSlotIds)
        {
            var validation = ValidateResolvedExploreOption(
                state,
                playerId,
                targetLocationId,
                card,
                selectedOptionIndex,
                influenceSlotId,
                eventInfluenceSlotIds);
            if (!validation.IsValid)
            {
                return ExplorationResult.Failure(validation);
            }

            var player = state.FindPlayer(playerId);
            var reward = card.ChoiceRewards[selectedOptionIndex].Clone();
            player.Resources.Add(reward);

            var resolvedSlotId = ResolveInfluenceSlotId(state, playerId, targetLocationId, influenceSlotId);
            var influencePlacement = influenceService.Place(state, playerId, resolvedSlotId);
            var eventEffectResult = eventEffectResolver.Apply(
                state,
                playerId,
                card.ChoicePendingEffects[selectedOptionIndex],
                targetLocationId,
                eventInfluenceSlotIds);
            if (!eventEffectResult.Succeeded)
            {
                return ExplorationResult.Failure(eventEffectResult.Validation);
            }

            return ExplorationResult.Success(
                string.Empty,
                targetLocationId,
                card.CardId,
                card.Color,
                selectedOptionIndex,
                reward,
                new List<ExplorationTravelPayment>().AsReadOnly(),
                influencePlacement);
        }


        public MapPath FindDefaultPath(GameState state, int playerId, string targetLocationId)
        {
            var choices = FindDefaultPathChoices(state, playerId, targetLocationId);
            return choices.Count <= 0 ? null : choices[0];
        }

        public IReadOnlyList<MapPath> FindDefaultPathChoices(GameState state, int playerId, string targetLocationId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return new List<MapPath>().AsReadOnly();
            }

            return FindMinimumPaymentPathChoices(
                state,
                playerId,
                player.CityLocationId,
                targetLocationId).AsReadOnly();
        }

        private ValidationResult ValidatePath(string sourceLocationId, string targetLocationId, MapPath path)
        {
            if (path == null || path.LocationIds == null || path.RouteIds == null)
            {
                return ValidationResult.Failure(CommandErrorCode.NoRoute, "探索路线不能为空。");
            }

            if (path.RouteIds.Count <= 0 || path.LocationIds.Count != path.RouteIds.Count + 1)
            {
                return ValidationResult.Failure(CommandErrorCode.NoRoute, "探索路线必须包含连续地点和航道。");
            }

            if (path.LocationIds[0] != sourceLocationId || path.LocationIds[path.LocationIds.Count - 1] != targetLocationId)
            {
                return ValidationResult.Failure(CommandErrorCode.NoRoute, "探索路线必须从玩家城市出发并到达目标资源点。");
            }

            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                MapRouteDefinition route;
                try
                {
                    route = mapQuery.GetRoute(path.RouteIds[i]);
                    mapQuery.GetLocation(path.LocationIds[i]);
                    mapQuery.GetLocation(path.LocationIds[i + 1]);
                }
                catch (ArgumentException)
                {
                    return ValidationResult.Failure(CommandErrorCode.NoRoute, "探索路线包含未知地点或航道。");
                }

                if (!RouteCoversLocation(route, path.LocationIds[i]) ||
                    !RouteCoversLocation(route, path.LocationIds[i + 1]))
                {
                    return ValidationResult.Failure(CommandErrorCode.NoRoute, "探索路线中的航道与相邻地点不匹配。");
                }
            }

            return ValidationResult.Success;
        }

        private ValidationResult ValidateExplorationInfluenceSlot(
            GameState state,
            int playerId,
            string targetLocationId,
            string influenceSlotId)
        {
            var player = state.FindPlayer(playerId);
            if (player.InfluenceSupply <= 0)
            {
                return ValidationResult.Failure(CommandErrorCode.InsufficientInfluence, "玩家供应堆中没有可用标记。");
            }

            var slotId = ResolveInfluenceSlotId(state, playerId, targetLocationId, influenceSlotId);
            if (string.IsNullOrEmpty(slotId))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "目标资源点没有可放置的影响力空格。");
            }

            InfluenceSlotReference slot;
            string reason;
            if (!InfluenceSlotReference.TryParse(mapQuery, slotId, out slot, out reason) ||
                slot.Kind != InfluenceSlotKind.Location ||
                slot.LocationId != targetLocationId)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "探索影响力必须放置在目标资源点。");
            }

            if (FindInfluenceAtSlot(state, slot.SlotId) != null)
            {
                return ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "目标影响力空格已被占用。");
            }

            if (HasOpponentCityAtLocation(state, playerId, targetLocationId))
            {
                return ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "目标资源点有其他玩家的移动城市停靠。");
            }

            return ValidationResult.Success;
        }

        private string ResolveInfluenceSlotId(
            GameState state,
            int playerId,
            string targetLocationId,
            string influenceSlotId)
        {
            if (!string.IsNullOrEmpty(influenceSlotId))
            {
                return influenceSlotId;
            }

            var location = mapQuery.GetLocation(targetLocationId);
            for (var i = 0; i < location.InfluenceSlotCount; i++)
            {
                var slotId = InfluenceService.GetLocationSlotId(targetLocationId, i);
                if (FindInfluenceAtSlot(state, slotId) == null && !HasOpponentCityAtLocation(state, playerId, targetLocationId))
                {
                    return slotId;
                }
            }

            return string.Empty;
        }

        private ValidationResult TryBuildPaymentPlan(
            GameState state,
            int playerId,
            MapPath path,
            IDictionary<string, int> paymentRecipientsByRouteId,
            out List<ExplorationTravelPayment> payments)
        {
            payments = new List<ExplorationTravelPayment>();

            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                var routeId = path.RouteIds[i];
                var paymentKey = GetRoutePaymentKey(routeId);
                if (PaymentPlanContainsPaymentKey(payments, paymentKey) ||
                    HasRoutePaymentKeyInfluenceOwnedBy(state, paymentKey, playerId))
                {
                    continue;
                }

                var opponentOwners = GetOpponentInfluenceOwnersOnRoutePaymentKey(state, paymentKey, playerId);
                var receiverPlayerId = -1;
                if (opponentOwners.Count > 0)
                {
                    if (paymentRecipientsByRouteId != null && paymentRecipientsByRouteId.TryGetValue(routeId, out var requestedReceiver))
                    {
                        if (!opponentOwners.Contains(requestedReceiver))
                        {
                            return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "指定路费接收玩家在该航道上没有影响力。");
                        }

                        receiverPlayerId = requestedReceiver;
                    }
                    else
                    {
                        receiverPlayerId = opponentOwners[0];
                    }
                }
                else if (paymentRecipientsByRouteId != null && paymentRecipientsByRouteId.ContainsKey(routeId))
                {
                    return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "该航道没有对手影响力，不能指定玩家接收路费。");
                }

                payments.Add(new ExplorationTravelPayment
                {
                    RouteId = routeId,
                    Amount = RouteCostGoldVoucher,
                    ReceiverPlayerId = receiverPlayerId
                });
            }

            return ValidationResult.Success;
        }

        private void ApplyPayments(GameState state, PlayerState player, IReadOnlyList<ExplorationTravelPayment> payments)
        {
            for (var i = 0; i < payments.Count; i++)
            {
                var payment = payments[i];
                player.Resources.GoldVoucher -= payment.Amount;
                if (!payment.PaidToSupply)
                {
                    var receiver = state.FindPlayer(payment.ReceiverPlayerId);
                    if (receiver != null)
                    {
                        receiver.Resources.GoldVoucher += payment.Amount;
                    }
                }
            }
        }

        private static void MarkLocationOpen(GameState state, string locationId)
        {
            if (!state.Map.OpenLocationIds.Contains(locationId))
            {
                state.Map.OpenLocationIds.Add(locationId);
            }
        }

        private bool HasRouteInfluenceOwnedBy(GameState state, string routeId, int playerId)
        {
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId == playerId && IsInfluenceOnRoute(influence, routeId))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasRoutePaymentKeyInfluenceOwnedBy(GameState state, string paymentKey, int playerId)
        {
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId == playerId && IsInfluenceOnRoutePaymentKey(influence, paymentKey))
                {
                    return true;
                }
            }

            return false;
        }

        private DefaultPathPaymentScore GetDefaultPathPaymentScore(GameState state, int playerId, MapPath path)
        {
            var score = new DefaultPathPaymentScore();
            if (path == null)
            {
                return score;
            }

            var paidPaymentKeys = new List<string>();
            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                var routeId = path.RouteIds[i];
                var paymentKey = GetRoutePaymentKey(routeId);
                score.StepCount += 1;
                if (paidPaymentKeys.Contains(paymentKey) ||
                    HasRoutePaymentKeyInfluenceOwnedBy(state, paymentKey, playerId))
                {
                    continue;
                }

                paidPaymentKeys.Add(paymentKey);
                score.TollRouteCount += 1;
                if (GetOpponentInfluenceOwnersOnRoutePaymentKey(state, paymentKey, playerId).Count > 0)
                {
                    score.OpponentTollRouteCount += 1;
                }
            }

            return score;
        }

        private static bool IsBetterDefaultPathScore(DefaultPathPaymentScore candidate, DefaultPathPaymentScore current)
        {
            if (candidate.TollRouteCount != current.TollRouteCount)
            {
                return candidate.TollRouteCount < current.TollRouteCount;
            }

            if (candidate.OpponentTollRouteCount != current.OpponentTollRouteCount)
            {
                return candidate.OpponentTollRouteCount < current.OpponentTollRouteCount;
            }

            return candidate.StepCount < current.StepCount;
        }

        private static bool IsSameDefaultPathScore(DefaultPathPaymentScore left, DefaultPathPaymentScore right)
        {
            return left.OpponentTollRouteCount == right.OpponentTollRouteCount &&
                   left.TollRouteCount == right.TollRouteCount &&
                   left.StepCount == right.StepCount;
        }

        private List<MapPath> FindMinimumPaymentPathChoices(
            GameState state,
            int playerId,
            string fromLocationId,
            string toLocationId)
        {
            mapQuery.GetLocation(fromLocationId);
            mapQuery.GetLocation(toLocationId);

            var open = new List<DefaultPathSearchNode>
            {
                new DefaultPathSearchNode
                {
                    LocationId = fromLocationId,
                    Path = new MapPath { LocationIds = new List<string> { fromLocationId } },
                    PaidPaymentKeys = new List<string>(),
                    Score = new DefaultPathPaymentScore()
                }
            };
            var bestScoresByState = new Dictionary<string, DefaultPathPaymentScore>();
            bestScoresByState[BuildPathSearchStateKey(fromLocationId, open[0].PaidPaymentKeys)] = open[0].Score;

            var bestPaths = new List<MapPath>();
            var hasBestScore = false;
            var bestScore = new DefaultPathPaymentScore();

            while (open.Count > 0)
            {
                var nodeIndex = FindBestOpenPathNodeIndex(open);
                var node = open[nodeIndex];
                open.RemoveAt(nodeIndex);

                if (node.LocationId == toLocationId)
                {
                    if (!hasBestScore || IsBetterDefaultPathScore(node.Score, bestScore))
                    {
                        bestPaths.Clear();
                        bestPaths.Add(ClonePath(node.Path));
                        bestScore = node.Score;
                        hasBestScore = true;
                        continue;
                    }

                    if (IsSameDefaultPathScore(node.Score, bestScore))
                    {
                        bestPaths.Add(ClonePath(node.Path));
                    }

                    continue;
                }

                if (hasBestScore && !CanStillMatchBestScore(node.Score, bestScore))
                {
                    continue;
                }

                var adjacentLocations = mapQuery.GetAdjacentLocations(node.LocationId);
                for (var i = 0; i < adjacentLocations.Count; i++)
                {
                    var nextLocationId = adjacentLocations[i].LocationId;
                    if (node.Path.LocationIds.Contains(nextLocationId))
                    {
                        continue;
                    }

                    var route = mapQuery.FindRoute(node.LocationId, nextLocationId);
                    var nextNode = BuildNextPathSearchNode(state, playerId, node, nextLocationId, route.RouteId);
                    var stateKey = BuildPathSearchStateKey(nextNode.LocationId, nextNode.PaidPaymentKeys);
                    if (bestScoresByState.TryGetValue(stateKey, out var existingScore) &&
                        !IsBetterDefaultPathScore(nextNode.Score, existingScore))
                    {
                        continue;
                    }

                    bestScoresByState[stateKey] = nextNode.Score;
                    open.Add(nextNode);
                }
            }

            if (bestPaths.Count <= 0)
            {
                throw new ArgumentException("No path exists between the supplied locations.");
            }

            return bestPaths;
        }

        private DefaultPathSearchNode BuildNextPathSearchNode(
            GameState state,
            int playerId,
            DefaultPathSearchNode current,
            string nextLocationId,
            string routeId)
        {
            var nextPath = ClonePath(current.Path);
            nextPath.LocationIds.Add(nextLocationId);
            nextPath.RouteIds.Add(routeId);

            var paidPaymentKeys = new List<string>(current.PaidPaymentKeys);
            var score = current.Score;
            score.StepCount += 1;

            var paymentKey = GetRoutePaymentKey(routeId);
            if (!paidPaymentKeys.Contains(paymentKey) &&
                !HasRoutePaymentKeyInfluenceOwnedBy(state, paymentKey, playerId))
            {
                paidPaymentKeys.Add(paymentKey);
                score.TollRouteCount += 1;
                if (GetOpponentInfluenceOwnersOnRoutePaymentKey(state, paymentKey, playerId).Count > 0)
                {
                    score.OpponentTollRouteCount += 1;
                }
            }

            return new DefaultPathSearchNode
            {
                LocationId = nextLocationId,
                Path = nextPath,
                PaidPaymentKeys = paidPaymentKeys,
                Score = score
            };
        }

        private static int FindBestOpenPathNodeIndex(IReadOnlyList<DefaultPathSearchNode> nodes)
        {
            var bestIndex = 0;
            for (var i = 1; i < nodes.Count; i++)
            {
                if (IsBetterDefaultPathScore(nodes[i].Score, nodes[bestIndex].Score))
                {
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private static bool CanStillMatchBestScore(DefaultPathPaymentScore partial, DefaultPathPaymentScore best)
        {
            if (partial.TollRouteCount != best.TollRouteCount)
            {
                return partial.TollRouteCount < best.TollRouteCount;
            }

            if (partial.OpponentTollRouteCount != best.OpponentTollRouteCount)
            {
                return partial.OpponentTollRouteCount < best.OpponentTollRouteCount;
            }

            return partial.StepCount < best.StepCount;
        }

        private static string BuildPathSearchStateKey(string locationId, List<string> paidPaymentKeys)
        {
            var sortedKeys = new List<string>(paidPaymentKeys);
            sortedKeys.Sort(StringComparer.Ordinal);

            var key = locationId ?? string.Empty;
            for (var i = 0; i < sortedKeys.Count; i++)
            {
                key += "|" + sortedKeys[i];
            }

            return key;
        }

        private static MapPath ClonePath(MapPath source)
        {
            return new MapPath
            {
                LocationIds = new List<string>(source.LocationIds),
                RouteIds = new List<string>(source.RouteIds)
            };
        }

        private bool PaymentPlanContainsPaymentKey(IReadOnlyList<ExplorationTravelPayment> payments, string paymentKey)
        {
            for (var i = 0; i < payments.Count; i++)
            {
                if (GetRoutePaymentKey(payments[i].RouteId) == paymentKey)
                {
                    return true;
                }
            }

            return false;
        }

        private List<int> GetOpponentInfluenceOwnersOnRoute(GameState state, string routeId, int playerId)
        {
            var owners = new List<int>();
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId == playerId || !IsInfluenceOnRoute(influence, routeId))
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

        private List<int> GetOpponentInfluenceOwnersOnRoutePaymentKey(GameState state, string paymentKey, int playerId)
        {
            var owners = new List<int>();
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId == playerId || !IsInfluenceOnRoutePaymentKey(influence, paymentKey))
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

        private bool IsInfluenceOnRoute(InfluencePlacement influence, string routeId)
        {
            if (influence.RouteId == routeId)
            {
                return true;
            }

            InfluenceSlotReference slot;
            string reason;
            return InfluenceSlotReference.TryParse(mapQuery, influence.SlotId, out slot, out reason) &&
                   slot.Kind == InfluenceSlotKind.Route &&
                   slot.RouteId == routeId;
        }

        private bool IsInfluenceOnRoutePaymentKey(InfluencePlacement influence, string paymentKey)
        {
            if (!string.IsNullOrEmpty(influence.RouteId) &&
                GetRoutePaymentKey(influence.RouteId) == paymentKey)
            {
                return true;
            }

            InfluenceSlotReference slot;
            string reason;
            return InfluenceSlotReference.TryParse(mapQuery, influence.SlotId, out slot, out reason) &&
                   slot.Kind == InfluenceSlotKind.Route &&
                   GetRoutePaymentKey(slot.RouteId) == paymentKey;
        }

        private string GetRoutePaymentKey(string routeId)
        {
            var route = mapQuery.GetRoute(routeId);
            if (!string.IsNullOrEmpty(route.RegionId) && IsRoutePaymentRegion(route.RegionId))
            {
                return route.RegionId;
            }

            return route.RouteId;
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

        private InfluencePlacement FindInfluenceAtSlot(GameState state, string slotId)
        {
            InfluenceSlotReference requestedSlot;
            string reason;
            if (!InfluenceSlotReference.TryParse(mapQuery, slotId, out requestedSlot, out reason))
            {
                return null;
            }

            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                InfluenceSlotReference placedSlot;
                if (InfluenceSlotReference.TryParse(mapQuery, influence.SlotId, out placedSlot, out reason) &&
                    placedSlot.SlotId == requestedSlot.SlotId)
                {
                    return influence;
                }
            }

            return null;
        }

        private static int SumPayments(IReadOnlyList<ExplorationTravelPayment> payments)
        {
            var total = 0;
            for (var i = 0; i < payments.Count; i++)
            {
                total += payments[i].Amount;
            }

            return total;
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

        private static bool RouteCoversLocation(MapRouteDefinition route, string locationId)
        {
            if (route.CoveredLocationIds != null && route.CoveredLocationIds.Count > 0)
            {
                return route.CoveredLocationIds.Contains(locationId);
            }

            return route.FromLocationId == locationId || route.ToLocationId == locationId;
        }

        private bool IsRedZoneClosed(GameState state, MapLocationDefinition location)
        {
            return RedZoneAccessRule.IsClosed(state, mapQuery.Map, location);
        }

        private static string PeekEventCardId(DeckRuntimeState decks, EventColor color)
        {
            return new EventDeckService().Peek(decks, color);
        }

        private static List<StringKeyValuePair> BuildCardFlowArguments(
            MapPath path,
            string influenceSlotId,
            IDictionary<string, int> paymentRecipientsByRouteId,
            IReadOnlyList<string> eventInfluenceSlotIds)
        {
            var result = new List<StringKeyValuePair>();
            if (path != null)
            {
                CardFlowArgumentUtility.SetValue(result, ExploreEventCardScenario.PathLocationIdsArgument, JoinIds(path.LocationIds));
                CardFlowArgumentUtility.SetValue(result, ExploreEventCardScenario.RouteIdsArgument, JoinIds(path.RouteIds));
            }

            if (!string.IsNullOrEmpty(influenceSlotId))
            {
                CardFlowArgumentUtility.SetValue(result, ExploreEventCardScenario.InfluenceSlotIdArgument, influenceSlotId);
            }

            if (paymentRecipientsByRouteId != null && paymentRecipientsByRouteId.Count > 0)
            {
                var encoded = string.Empty;
                foreach (var entry in paymentRecipientsByRouteId)
                {
                    if (!string.IsNullOrEmpty(encoded))
                    {
                        encoded += ";";
                    }

                    encoded += entry.Key + "=" + entry.Value;
                }

                CardFlowArgumentUtility.SetValue(result, ExploreEventCardScenario.PaymentRecipientsArgument, encoded);
            }

            if (eventInfluenceSlotIds != null && eventInfluenceSlotIds.Count > 0)
            {
                CardFlowArgumentUtility.SetValue(result, ExploreEventCardScenario.EventInfluenceSlotIdsArgument, JoinIds(eventInfluenceSlotIds));
            }

            return result;
        }

        private static string JoinIds(IReadOnlyList<string> ids)
        {
            if (ids == null || ids.Count <= 0)
            {
                return string.Empty;
            }

            return string.Join(",", ids);
        }

        private struct DefaultPathPaymentScore
        {
            public int OpponentTollRouteCount;
            public int TollRouteCount;
            public int StepCount;
        }

        private sealed class DefaultPathSearchNode
        {
            public string LocationId;
            public MapPath Path;
            public List<string> PaidPaymentKeys;
            public DefaultPathPaymentScore Score;
        }
    }
}
