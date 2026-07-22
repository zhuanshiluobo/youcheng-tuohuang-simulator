using System;
using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.CardFlows;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Movement
{
    public sealed class CityMovementService
    {
        internal const string WaiveBaseCostArgument = "waiveBaseCost";
        public const string ConsumeMainActionArgument = "consumeMainAction";
        private static readonly MainActionBudgetService MainActionBudgetService = new MainActionBudgetService();
        private readonly IMapQueryService mapQuery;
        private readonly InfluenceService influenceService;
        private readonly TravelCostService travelCostService;
        private readonly EventDeckService eventDeckService;
        private readonly ResourceTokenService resourceTokenService;
        private readonly EventEffectResolver eventEffectResolver;
        private readonly CardFlowService cardFlowService;

        public CityMovementService(
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            TravelCostService travelCostService)
            : this(mapQuery, influenceService, travelCostService, new EventDeckService(), new ResourceTokenService())
        {
        }

        public CityMovementService(
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            TravelCostService travelCostService,
            EventDeckService eventDeckService,
            ResourceTokenService resourceTokenService)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
            this.travelCostService = travelCostService ?? throw new ArgumentNullException(nameof(travelCostService));
            this.eventDeckService = eventDeckService ?? throw new ArgumentNullException(nameof(eventDeckService));
            this.resourceTokenService = resourceTokenService ?? throw new ArgumentNullException(nameof(resourceTokenService));
            eventEffectResolver = new EventEffectResolver(this.mapQuery, this.influenceService);
            cardFlowService = new CardFlowService();
        }

        public ValidationResult CanMoveCity(
            GameState state,
            int playerId,
            string targetLocationId,
            int selectedOptionIndex = -1,
            IReadOnlyList<string> eventInfluenceSlotIds = null)
        {
            return CanMoveCityCore(
                state,
                playerId,
                targetLocationId,
                selectedOptionIndex,
                eventInfluenceSlotIds,
                false,
                false);
        }

        public CityMovementResult MoveCity(
            GameState state,
            int playerId,
            string targetLocationId,
            int selectedOptionIndex = -1,
            IReadOnlyList<string> eventInfluenceSlotIds = null,
            string sourceCommandId = null)
        {
            return MoveCityCore(
                state,
                playerId,
                targetLocationId,
                selectedOptionIndex,
                eventInfluenceSlotIds,
                sourceCommandId,
                false,
                false);
        }

        public ValidationResult CanMoveCityForFacility(
            GameState state,
            int playerId,
            string targetLocationId,
            int selectedOptionIndex = -1,
            IReadOnlyList<string> eventInfluenceSlotIds = null)
        {
            return CanMoveCityCore(
                state,
                playerId,
                targetLocationId,
                selectedOptionIndex,
                eventInfluenceSlotIds,
                true,
                true);
        }

        public CityMovementResult MoveCityForFacility(
            GameState state,
            int playerId,
            string targetLocationId,
            int selectedOptionIndex = -1,
            IReadOnlyList<string> eventInfluenceSlotIds = null,
            string sourceCommandId = null)
        {
            return MoveCityCore(
                state,
                playerId,
                targetLocationId,
                selectedOptionIndex,
                eventInfluenceSlotIds,
                sourceCommandId,
                true,
                true);
        }

        private ValidationResult CanMoveCityCore(
            GameState state,
            int playerId,
            string targetLocationId,
            int selectedOptionIndex,
            IReadOnlyList<string> eventInfluenceSlotIds,
            bool allowPendingEffect,
            bool waiveBaseCost)
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

            if (state.HasPendingChoice() && !allowPendingEffect)
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "请先处理待选择项再移动城市。");
            }

            if (!allowPendingEffect)
            {
                var budgetValidation = MainActionBudgetService.ValidateCanSpend(state, playerId);
                if (!budgetValidation.IsValid)
                {
                    return budgetValidation;
                }
            }

            if (string.IsNullOrEmpty(player.CityLocationId))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidSource, "玩家城市不在场上。");
            }

            try
            {
                mapQuery.GetLocation(targetLocationId);
            }
            catch (ArgumentException)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "城市移动目标不存在。");
            }

            if (FindAdjacentRoute(mapQuery, player.CityLocationId, targetLocationId) == null)
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

            var cost = waiveBaseCost ? new ResourceSet() : travelCostService.GetCityMoveBaseCost();
            if (!waiveBaseCost && !player.Resources.CanPay(cost))
            {
                return ValidationResult.Failure(CommandErrorCode.InsufficientResource, "玩家无法支付城市移动费用。");
            }

            return ValidateUnrevealedTargetEvent(
                state,
                playerId,
                targetLocationId,
                selectedOptionIndex,
                eventInfluenceSlotIds,
                FindFirstAvailableLocationSlot(state, playerId, player.CityLocationId),
                cost);
        }

        private CityMovementResult MoveCityCore(
            GameState state,
            int playerId,
            string targetLocationId,
            int selectedOptionIndex,
            IReadOnlyList<string> eventInfluenceSlotIds,
            string sourceCommandId,
            bool allowPendingEffect,
            bool waiveBaseCost)
        {
            var validation = CanMoveCityCore(
                state,
                playerId,
                targetLocationId,
                selectedOptionIndex,
                eventInfluenceSlotIds,
                allowPendingEffect,
                waiveBaseCost);
            if (!validation.IsValid)
            {
                return CityMovementResult.Failure(validation);
            }

            if (resourceTokenService.HasResourceToken(state.Map, targetLocationId))
            {
                var baseMove = PerformBaseMove(state, playerId, targetLocationId, !waiveBaseCost);
                return CityMovementResult.Success(
                    baseMove.SourceLocationId,
                    targetLocationId,
                    baseMove.RouteId,
                    baseMove.RemovedInfluenceCount,
                    baseMove.SourceInfluencePlacement);
            }

            var arguments = BuildCardFlowArguments(eventInfluenceSlotIds);
            CardFlowArgumentUtility.SetValue(
                arguments,
                ConsumeMainActionArgument,
                (!allowPendingEffect).ToString());
            if (waiveBaseCost)
            {
                CardFlowArgumentUtility.SetValue(arguments, WaiveBaseCostArgument, bool.TrueString);
            }

            if (selectedOptionIndex >= 0)
            {
                var flowResult = cardFlowService.ExecuteImmediate(
                    state,
                    new CardFlowExecuteRequest
                    {
                        PlayerId = playerId,
                        TargetId = targetLocationId,
                        SourceCommandId = sourceCommandId,
                        OptionIndex = selectedOptionIndex,
                        Arguments = arguments
                    },
                    new MoveCityEventCardScenario(this));
                if (!flowResult.Succeeded)
                {
                    return CityMovementResult.Failure(flowResult.Validation);
                }

                return CityMovementResult.Success(
                    GetContextValue(flowResult.Context, "sourceLocationId"),
                    targetLocationId,
                    GetContextValue(flowResult.Context, "routeId"),
                    GetContextIntValue(flowResult.Context, "removedInfluenceCount"),
                    null,
                    true,
                    flowResult.Card.CardId,
                    flowResult.Card.Color,
                    selectedOptionIndex,
                    flowResult.Reward);
            }

            var pendingResult = cardFlowService.StartPendingChoice(
                state,
                new CardFlowStartRequest
                {
                    PlayerId = playerId,
                    TargetId = targetLocationId,
                    SourceCommandId = sourceCommandId,
                    Arguments = arguments
                },
                new MoveCityEventCardScenario(this));
            if (!pendingResult.Succeeded)
            {
                return CityMovementResult.Failure(pendingResult.Validation);
            }

            return CityMovementResult.Success(
                GetContextValue(pendingResult.Context, "sourceLocationId"),
                targetLocationId,
                GetContextValue(pendingResult.Context, "routeId"),
                GetContextIntValue(pendingResult.Context, "removedInfluenceCount"),
                null,
                true,
                pendingResult.Card.CardId,
                pendingResult.Card.Color,
                -1,
                null);
        }

        public static bool PendingMoveConsumesMainAction(GameState state)
        {
            var pending = state == null ? null : state.PendingCardSession;
            if (pending == null)
            {
                return true;
            }

            var encoded = CardFlowArgumentUtility.GetValue(
                pending.ContextData,
                ConsumeMainActionArgument);
            return !string.Equals(encoded, bool.FalseString, StringComparison.OrdinalIgnoreCase);
        }

        public ValidationResult CanRaidCityForCharacter(GameState state, int playerId, string targetLocationId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "玩家不存在。");
            }

            if (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2)
            {
                return ValidationResult.Failure(CommandErrorCode.WrongPhase, "免费突袭只能在行动轮发动。");
            }

            if (state.CurrentPlayerId != playerId)
            {
                return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "只能在自己的行动窗口免费突袭。");
            }

            if (string.IsNullOrEmpty(player.CityLocationId))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidSource, "玩家的移动城市不在场上。");
            }

            mapQuery.GetLocation(targetLocationId);
            if (FindAdjacentRoute(mapQuery, player.CityLocationId, targetLocationId) == null)
            {
                return ValidationResult.Failure(CommandErrorCode.NoRoute, "免费突袭目标必须与移动城市相邻。");
            }

            if (HasOpponentCityAtLocation(state, playerId, targetLocationId))
            {
                return ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "目标资源点有其他玩家的移动城市。");
            }

            if (IsRedZoneClosed(state, targetLocationId))
            {
                return ValidationResult.Failure(CommandErrorCode.ClosedLocation, "红色区域尚未开放。");
            }

            if (!resourceTokenService.HasResourceToken(state.Map, targetLocationId))
            {
                return ValidationResult.Failure(CommandErrorCode.ClosedLocation, "免费突袭目标必须是已有资源的资源点。");
            }

            if (!HasPlayerInfluenceAtLocation(state, playerId, targetLocationId))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "免费突袭目标必须已有至少一个己方影响力。");
            }

            return ValidationResult.Success;
        }

        public CityMovementResult RaidCityForCharacter(GameState state, int playerId, string targetLocationId)
        {
            var validation = CanRaidCityForCharacter(state, playerId, targetLocationId);
            if (!validation.IsValid)
            {
                return CityMovementResult.Failure(validation);
            }

            var move = PerformBaseMove(state, playerId, targetLocationId, false);
            return CityMovementResult.Success(
                move.SourceLocationId,
                targetLocationId,
                move.RouteId,
                move.RemovedInfluenceCount,
                move.SourceInfluencePlacement);
        }

        public CityMovementResult ResolveMoveCityEvent(
            GameState state,
            int playerId,
            string targetLocationId,
            string eventCardId,
            int selectedOptionIndex,
            IReadOnlyList<string> eventInfluenceSlotIds = null)
        {
            var flowResult = cardFlowService.ResolvePendingChoice(
                state,
                new CardFlowResolveRequest
                {
                    PlayerId = playerId,
                    SessionId = state.PendingCardSession == null ? string.Empty : state.PendingCardSession.SessionId,
                    OptionIndex = selectedOptionIndex,
                    Arguments = BuildCardFlowArguments(eventInfluenceSlotIds)
                },
                new MoveCityEventCardScenario(this));
            if (!flowResult.Succeeded)
            {
                return CityMovementResult.Failure(flowResult.Validation);
            }

            return CityMovementResult.Success(
                string.Empty,
                targetLocationId,
                string.Empty,
                0,
                null,
                true,
                flowResult.Card.CardId,
                flowResult.Card.Color,
                selectedOptionIndex,
                flowResult.Reward);
        }

        internal ValidationResult RevealMoveCityCard(GameState state, CardFlowContext context, EventCardDefinition card)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (card == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Move city event card does not exist.");
            }

            var waiveBaseCost = string.Equals(
                CardFlowArgumentUtility.GetValue(context.ContextData, WaiveBaseCostArgument),
                bool.TrueString,
                StringComparison.OrdinalIgnoreCase);
            var moveResult = PerformBaseMove(state, context.PlayerId, context.TargetId, !waiveBaseCost);
            CardFlowArgumentUtility.SetValue(context.ContextData, "sourceLocationId", moveResult.SourceLocationId);
            CardFlowArgumentUtility.SetValue(context.ContextData, "routeId", moveResult.RouteId);
            CardFlowArgumentUtility.SetValue(context.ContextData, "removedInfluenceCount", moveResult.RemovedInfluenceCount.ToString());

            resourceTokenService.PlaceToken(
                state.Map,
                context.TargetId,
                card.RepresentativeResourceType,
                card.RepresentativeResourceAmount);
            MarkLocationOpen(state, context.TargetId);
            return ValidationResult.Success;
        }

        internal ValidationResult ValidateMoveCityEventOption(
            GameState state,
            int playerId,
            string targetLocationId,
            EventCardDefinition card,
            int selectedOptionIndex,
            IReadOnlyList<string> eventInfluenceSlotIds)
        {
            if (card == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Move city event card does not exist.");
            }

            if (selectedOptionIndex < 0 || selectedOptionIndex >= card.ChoiceRewards.Count)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Move city event option is invalid.");
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "Player must exist before resolving a move city event.");
            }

            return eventEffectResolver.Validate(
                state,
                playerId,
                card.ChoicePendingEffects[selectedOptionIndex],
                targetLocationId,
                eventInfluenceSlotIds);
        }

        internal ValidationResult ApplyMoveCityEventOption(
            GameState state,
            int playerId,
            string originLocationId,
            EventCardDefinition card,
            int selectedOptionIndex,
            IReadOnlyList<string> eventInfluenceSlotIds,
            out ResourceSet reward)
        {
            var validation = ValidateMoveCityEventOption(
                state,
                playerId,
                originLocationId,
                card,
                selectedOptionIndex,
                eventInfluenceSlotIds);
            if (!validation.IsValid)
            {
                reward = null;
                return validation;
            }

            return ApplyEventOption(state, playerId, originLocationId, card, selectedOptionIndex, eventInfluenceSlotIds, out reward);
        }

        private BaseMoveResult PerformBaseMove(GameState state, int playerId, string targetLocationId, bool payBaseCost = true)
        {
            var player = state.FindPlayer(playerId);
            var sourceLocationId = player.CityLocationId;
            var route = mapQuery.FindRoute(sourceLocationId, targetLocationId);
            var cost = travelCostService.GetCityMoveBaseCost();

            if (payBaseCost)
            {
                player.Resources.TryPay(cost);
            }
            player.CityLocationId = targetLocationId;
            player.HasMovedCityThisRound = true;

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

            return new BaseMoveResult
            {
                SourceLocationId = sourceLocationId,
                RouteId = route.RouteId,
                RemovedInfluenceCount = removedInfluenceCount,
                SourceInfluencePlacement = placementResult
            };
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

        private static MapRouteDefinition FindAdjacentRoute(IMapQueryService mapQuery, string sourceLocationId, string targetLocationId)
        {
            var map = mapQuery.Map;
            for (var i = 0; i < map.Routes.Count; i++)
            {
                var route = map.Routes[i];
                if (RouteCoversBothLocations(route, sourceLocationId, targetLocationId))
                {
                    return route;
                }
            }

            return null;
        }

        private static bool RouteCoversBothLocations(System.Collections.Generic.IReadOnlyList<string> coveredIds, string idA, string idB)
        {
            if (coveredIds == null || coveredIds.Count == 0)
            {
                return false;
            }

            var hasA = false;
            var hasB = false;
            for (var i = 0; i < coveredIds.Count; i++)
            {
                if (coveredIds[i] == idA) hasA = true;
                if (coveredIds[i] == idB) hasB = true;
            }

            return hasA && hasB;
        }

        private static bool RouteCoversBothLocations(MapRouteDefinition route, string idA, string idB)
        {
            if (RouteCoversBothLocations(route.CoveredLocationIds, idA, idB))
            {
                return true;
            }

            return (route.FromLocationId == idA && route.ToLocationId == idB) ||
                   (route.FromLocationId == idB && route.ToLocationId == idA);
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

        private static bool HasPlayerInfluenceAtLocation(GameState state, int playerId, string locationId)
        {
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId == playerId && influence.LocationId == locationId)
                {
                    return true;
                }
            }

            return false;
        }

        private ValidationResult ValidateUnrevealedTargetEvent(
            GameState state,
            int playerId,
            string targetLocationId,
            int selectedOptionIndex,
            IReadOnlyList<string> eventInfluenceSlotIds,
            string reservedSourceSlotId,
            ResourceSet priorCost)
        {
            if (selectedOptionIndex < -1)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Move city event option is invalid.");
            }

            if (resourceTokenService.HasResourceToken(state.Map, targetLocationId))
            {
                return ValidationResult.Success;
            }

            var eventColor = StaticMapDefinitions.GetEventColor(targetLocationId);
            if (eventDeckService.RemainingCount(state.Decks, eventColor) <= 0)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "No cards remain in the matching event deck.");
            }

            var card = EventCardDatabase.Get(PeekEventCardId(state.Decks, eventColor));
            if (card == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Matching event card data does not exist.");
            }

            if (card.ChoiceRewards.Count <= 0)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Move city event card has no options.");
            }

            if (selectedOptionIndex >= card.ChoiceRewards.Count)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "Move city event option is invalid.");
            }

            if (selectedOptionIndex >= 0)
            {
                var reservedSlots = string.IsNullOrEmpty(reservedSourceSlotId)
                    ? null
                    : new List<string> { reservedSourceSlotId }.AsReadOnly();
                var effectValidation = eventEffectResolver.Validate(
                    state,
                    playerId,
                    card.ChoicePendingEffects[selectedOptionIndex],
                    targetLocationId,
                    eventInfluenceSlotIds,
                    reservedSlots,
                    priorCost);
                if (!effectValidation.IsValid)
                {
                    return effectValidation;
                }
            }

            return ValidationResult.Success;
        }

        private ValidationResult ApplyEventOption(
            GameState state,
            int playerId,
            string originLocationId,
            EventCardDefinition card,
            int selectedOptionIndex,
            IReadOnlyList<string> eventInfluenceSlotIds,
            out ResourceSet reward)
        {
            var player = state.FindPlayer(playerId);
            var eventEffectValidation = eventEffectResolver.Validate(
                state,
                playerId,
                card.ChoicePendingEffects[selectedOptionIndex],
                originLocationId,
                eventInfluenceSlotIds);
            if (!eventEffectValidation.IsValid)
            {
                reward = null;
                return eventEffectValidation;
            }

            reward = card.ChoiceRewards[selectedOptionIndex].Clone();
            player.Resources.Add(reward);
            var eventEffectResult = eventEffectResolver.Apply(
                state,
                playerId,
                card.ChoicePendingEffects[selectedOptionIndex],
                originLocationId,
                eventInfluenceSlotIds);
            return eventEffectResult.Validation;
        }

        private static void MarkLocationOpen(GameState state, string locationId)
        {
            if (!state.Map.OpenLocationIds.Contains(locationId))
            {
                state.Map.OpenLocationIds.Add(locationId);
            }
        }

        private static string PeekEventCardId(DeckRuntimeState decks, EventColor color)
        {
            return new EventDeckService().Peek(decks, color);
        }

        private bool IsRedZoneClosed(GameState state, string locationId)
        {
            var location = mapQuery.GetLocation(locationId);
            return RedZoneAccessRule.IsClosed(state, mapQuery.Map, location);
        }

        private static List<StringKeyValuePair> BuildCardFlowArguments(IReadOnlyList<string> eventInfluenceSlotIds)
        {
            var result = new List<StringKeyValuePair>();
            if (eventInfluenceSlotIds != null && eventInfluenceSlotIds.Count > 0)
            {
                CardFlowArgumentUtility.SetValue(
                    result,
                    MoveCityEventCardScenario.EventInfluenceSlotIdsArgument,
                    string.Join(",", eventInfluenceSlotIds));
            }

            return result;
        }

        private static string GetContextValue(CardFlowContext context, string key)
        {
            return context == null ? string.Empty : CardFlowArgumentUtility.GetValue(context.ContextData, key);
        }

        private static int GetContextIntValue(CardFlowContext context, string key)
        {
            var encoded = GetContextValue(context, key);
            int value;
            return int.TryParse(encoded, out value) ? value : 0;
        }

        private sealed class BaseMoveResult
        {
            public string SourceLocationId = string.Empty;
            public string RouteId = string.Empty;
            public int RemovedInfluenceCount;
            public InfluenceOperationResult SourceInfluencePlacement;
        }
    }
}
