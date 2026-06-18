using System;
using System.Collections.Generic;
using YC.Domain.Cards;
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
        }

        public ValidationResult CanExplore(
            GameState state,
            int playerId,
            string targetLocationId,
            MapPath path,
            int selectedOptionIndex,
            string influenceSlotId,
            IDictionary<string, int> paymentRecipientsByRouteId)
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

            if (selectedOptionIndex < 0 || selectedOptionIndex >= card.ChoiceRewards.Count)
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
            if (player.Resources.GoldVoucher < totalCost)
            {
                return ValidationResult.Failure(CommandErrorCode.InsufficientResource, "玩家金券不足，无法支付探索路费。");
            }

            return ValidationResult.Success;
        }

#if false
        public ValidationResult CanExplore(
            GameState state,
            int playerId,
            string targetLocationId,
            MapPath path,
            int selectedOptionIndex,
            string influenceSlotId,
            IDictionary<string, int> paymentRecipientsByRouteId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ExplorationResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "探索玩家不存在。"));
            }

            var resolvedCard = EventCardDatabase.Get(eventCardId);
            if (resolvedCard == null)
            {
                return ExplorationResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "探索事件牌不存在。"));
            }

            if (selectedOptionIndex < 0 || selectedOptionIndex >= resolvedCard.ChoiceRewards.Count)
            {
                return ExplorationResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "探索事件选项无效。"));
            }

            /*
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
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "本行动轮已执行过主要行动。");
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

            if (!state.Map.OpenLocationIds.Contains(targetLocationId))
            {
                return ValidationResult.Failure(CommandErrorCode.ClosedLocation, "目标资源点尚未开放探索。");
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

            if (selectedOptionIndex < 0 || selectedOptionIndex >= card.ChoiceRewards.Count)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "探索事件选项无效。");
            }

            */

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
            if (player.Resources.GoldVoucher < totalCost)
            {
                return ValidationResult.Failure(CommandErrorCode.InsufficientResource, "玩家金券不足，无法支付探索路费。");
            }

            return ValidationResult.Success;
        }

#endif

        public ExplorationResult Explore(
            GameState state,
            int playerId,
            string targetLocationId,
            MapPath path,
            int selectedOptionIndex,
            string influenceSlotId,
            IDictionary<string, int> paymentRecipientsByRouteId)
        {
            var validation = CanExplore(
                state,
                playerId,
                targetLocationId,
                path,
                selectedOptionIndex,
                influenceSlotId,
                paymentRecipientsByRouteId);
            if (!validation.IsValid)
            {
                return ExplorationResult.Failure(validation);
            }

            var player = state.FindPlayer(playerId);
            List<ExplorationTravelPayment> payments;
            TryBuildPaymentPlan(state, playerId, path, paymentRecipientsByRouteId, out payments);

            ApplyPayments(state, player, payments);

            var eventColor = StaticMapDefinitions.GetEventColor(targetLocationId);
            var cardId = eventDeckService.Draw(state.Decks, eventColor);
            var card = EventCardDatabase.Get(cardId);

            resourceTokenService.PlaceToken(
                state.Map,
                targetLocationId,
                card.RepresentativeResourceType,
                card.RepresentativeResourceAmount);
            MarkLocationOpen(state, targetLocationId);

            var reward = card.ChoiceRewards[selectedOptionIndex].Clone();
            player.Resources.Add(reward);
            ApplyPendingEffectText(state, playerId, card.ChoicePendingEffects[selectedOptionIndex]);

            var resolvedSlotId = ResolveInfluenceSlotId(state, playerId, targetLocationId, influenceSlotId);
            var influencePlacement = influenceService.Place(state, playerId, resolvedSlotId);

            return ExplorationResult.Success(
                path.LocationIds[0],
                targetLocationId,
                cardId,
                eventColor,
                selectedOptionIndex,
                reward,
                payments.AsReadOnly(),
                influencePlacement);
        }

        public ExplorationResult BeginExploreEvent(
            GameState state,
            int playerId,
            string targetLocationId,
            MapPath path,
            string influenceSlotId,
            IDictionary<string, int> paymentRecipientsByRouteId)
        {
            var validation = CanExplore(
                state,
                playerId,
                targetLocationId,
                path,
                0,
                influenceSlotId,
                paymentRecipientsByRouteId);
            if (!validation.IsValid)
            {
                return ExplorationResult.Failure(validation);
            }

            var player = state.FindPlayer(playerId);
            List<ExplorationTravelPayment> payments;
            TryBuildPaymentPlan(state, playerId, path, paymentRecipientsByRouteId, out payments);

            ApplyPayments(state, player, payments);

            var eventColor = StaticMapDefinitions.GetEventColor(targetLocationId);
            var cardId = eventDeckService.Draw(state.Decks, eventColor);
            var card = EventCardDatabase.Get(cardId);

            resourceTokenService.PlaceToken(
                state.Map,
                targetLocationId,
                card.RepresentativeResourceType,
                card.RepresentativeResourceAmount);
            MarkLocationOpen(state, targetLocationId);

            return ExplorationResult.Success(
                path.LocationIds[0],
                targetLocationId,
                cardId,
                eventColor,
                -1,
                null,
                payments.AsReadOnly(),
                null);
        }

        public ExplorationResult ResolveExploreEvent(
            GameState state,
            int playerId,
            string targetLocationId,
            string eventCardId,
            int selectedOptionIndex,
            string influenceSlotId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ExplorationResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "探索玩家不存在。"));
            }

            var card = EventCardDatabase.Get(eventCardId);
            if (card == null)
            {
                return ExplorationResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "探索事件牌不存在。"));
            }

            if (selectedOptionIndex < 0 || selectedOptionIndex >= card.ChoiceRewards.Count)
            {
                return ExplorationResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "探索事件选项无效。"));
            }

            var slotValidation = ValidateExplorationInfluenceSlot(state, playerId, targetLocationId, influenceSlotId);
            if (!slotValidation.IsValid)
            {
                return ExplorationResult.Failure(slotValidation);
            }

            var reward = card.ChoiceRewards[selectedOptionIndex].Clone();
            player.Resources.Add(reward);
            ApplyPendingEffectText(state, playerId, card.ChoicePendingEffects[selectedOptionIndex]);

            var resolvedSlotId = ResolveInfluenceSlotId(state, playerId, targetLocationId, influenceSlotId);
            var influencePlacement = influenceService.Place(state, playerId, resolvedSlotId);

            return ExplorationResult.Success(
                string.Empty,
                targetLocationId,
                eventCardId,
                card.Color,
                selectedOptionIndex,
                reward,
                new List<ExplorationTravelPayment>().AsReadOnly(),
                influencePlacement);
        }

#if false
        public ExplorationResult ResolveExploreEvent(
            GameState state,
            int playerId,
            string targetLocationId,
            string eventCardId,
            int selectedOptionIndex,
            string influenceSlotId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ExplorationResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "鎺㈢储鐜╁涓嶅瓨鍦ㄣ€?));
            }

            var card = EventCardDatabase.Get(eventCardId);
            if (card == null)
            {
                return ExplorationResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "浜嬩欢鐗屾暟鎹笉瀛樺湪銆?));
            }

            if (selectedOptionIndex < 0 || selectedOptionIndex >= card.ChoiceRewards.Count)
            {
                return ExplorationResult.Failure(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "鎺㈢储浜嬩欢閫夐」鏃犳晥銆?));
            }

            var slotValidation = ValidateExplorationInfluenceSlot(state, playerId, targetLocationId, influenceSlotId);
            if (!slotValidation.IsValid)
            {
                return ExplorationResult.Failure(slotValidation);
            }

            var reward = card.ChoiceRewards[selectedOptionIndex].Clone();
            player.Resources.Add(reward);
            ApplyPendingEffectText(state, playerId, card.ChoicePendingEffects[selectedOptionIndex]);

            var resolvedSlotId = ResolveInfluenceSlotId(state, playerId, targetLocationId, influenceSlotId);
            var influencePlacement = influenceService.Place(state, playerId, resolvedSlotId);

            return ExplorationResult.Success(
                string.Empty,
                targetLocationId,
                eventCardId,
                resolvedCard.Color,
                selectedOptionIndex,
                reward,
                new List<ExplorationTravelPayment>().AsReadOnly(),
                influencePlacement);
        }

#endif

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

            var pathSearch = new MapPathSearchService(mapQuery);
            var shortestPath = pathSearch.FindShortestPath(player.CityLocationId, targetLocationId);
            var shortestPaths = pathSearch.EnumerateSimplePaths(
                player.CityLocationId,
                targetLocationId,
                shortestPath.StepCount);

            var bestPaths = new List<MapPath>();
            var hasBestScore = false;
            var bestScore = new DefaultPathPaymentScore();
            for (var i = 0; i < shortestPaths.Count; i++)
            {
                var candidate = shortestPaths[i];
                if (candidate.StepCount != shortestPath.StepCount)
                {
                    continue;
                }

                var candidateScore = GetDefaultPathPaymentScore(state, playerId, candidate);
                if (IsBetterDefaultPathScore(candidateScore, bestScore))
                {
                    bestPaths.Clear();
                    bestPaths.Add(candidate);
                    bestScore = candidateScore;
                    hasBestScore = true;
                    continue;
                }

                if (!hasBestScore || IsSameDefaultPathScore(candidateScore, bestScore))
                {
                    bestPaths.Add(candidate);
                    bestScore = candidateScore;
                    hasBestScore = true;
                }
            }

            if (bestPaths.Count <= 0)
            {
                bestPaths.Add(shortestPath);
            }

            return bestPaths.AsReadOnly();
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
                if (HasRouteInfluenceOwnedBy(state, routeId, playerId))
                {
                    continue;
                }

                var opponentOwners = GetOpponentInfluenceOwnersOnRoute(state, routeId, playerId);
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

        private static void ApplyPendingEffectText(GameState state, int playerId, string pendingEffect)
        {
            if (string.IsNullOrEmpty(pendingEffect))
            {
                return;
            }

            var player = state.FindPlayer(playerId);
            if (pendingEffect.Contains("获得 1 分数"))
            {
                player.Score += 1;
            }

            if (pendingEffect.Contains("所有对手获得 3 金券"))
            {
                GrantOpponents(state, playerId, ResourceType.GoldVoucher, 3);
            }

            if (pendingEffect.Contains("所有对手获得 1 异铁"))
            {
                GrantOpponents(state, playerId, ResourceType.Iron, 1);
            }
        }

        private static void MarkLocationOpen(GameState state, string locationId)
        {
            if (!state.Map.OpenLocationIds.Contains(locationId))
            {
                state.Map.OpenLocationIds.Add(locationId);
            }
        }

        private static void GrantOpponents(GameState state, int playerId, ResourceType type, int amount)
        {
            for (var i = 0; i < state.Players.Count; i++)
            {
                var opponent = state.Players[i];
                if (opponent.PlayerId == playerId)
                {
                    continue;
                }

                opponent.Resources.Set(type, opponent.Resources.Get(type) + amount);
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

        private DefaultPathPaymentScore GetDefaultPathPaymentScore(GameState state, int playerId, MapPath path)
        {
            var score = new DefaultPathPaymentScore();
            if (path == null)
            {
                return score;
            }

            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                var routeId = path.RouteIds[i];
                if (HasRouteInfluenceOwnedBy(state, routeId, playerId))
                {
                    continue;
                }

                score.TollRouteCount += 1;
                if (GetOpponentInfluenceOwnersOnRoute(state, routeId, playerId).Count > 0)
                {
                    score.OpponentTollRouteCount += 1;
                }
            }

            return score;
        }

        private static bool IsBetterDefaultPathScore(DefaultPathPaymentScore candidate, DefaultPathPaymentScore current)
        {
            if (candidate.OpponentTollRouteCount != current.OpponentTollRouteCount)
            {
                return candidate.OpponentTollRouteCount < current.OpponentTollRouteCount;
            }

            return candidate.TollRouteCount < current.TollRouteCount;
        }

        private static bool IsSameDefaultPathScore(DefaultPathPaymentScore left, DefaultPathPaymentScore right)
        {
            return left.OpponentTollRouteCount == right.OpponentTollRouteCount &&
                   left.TollRouteCount == right.TollRouteCount;
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

        private static bool IsRedZoneClosed(GameState state, MapLocationDefinition location)
        {
            if (!location.IsRedZone)
            {
                return false;
            }

            return state.Round < GetRedZoneOpenRound(state.Players.Count);
        }

        private static int GetRedZoneOpenRound(int playerCount)
        {
            if (playerCount <= 2)
            {
                return 6;
            }

            return playerCount == 3 ? 5 : 4;
        }

        private static string PeekEventCardId(DeckRuntimeState decks, EventColor color)
        {
            List<string> targetDeck;
            switch (color)
            {
                case EventColor.Green:
                    targetDeck = decks.EventDeckGreen;
                    break;
                case EventColor.Yellow:
                    targetDeck = decks.EventDeckYellow;
                    break;
                case EventColor.Red:
                    targetDeck = decks.EventDeckRed;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(color), color, null);
            }

            return targetDeck[targetDeck.Count - 1];
        }

        private struct DefaultPathPaymentScore
        {
            public int OpponentTollRouteCount;
            public int TollRouteCount;
        }
    }
}
