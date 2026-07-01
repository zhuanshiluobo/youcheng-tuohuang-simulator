using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.CardFlows;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Movement
{
    internal sealed class MoveCityEventCardScenario : IEventCardScenario
    {
        public const string EventInfluenceSlotIdsArgument = "eventInfluenceSlotIds";

        private readonly CityMovementService cityMovementService;

        public MoveCityEventCardScenario(CityMovementService cityMovementService)
        {
            this.cityMovementService = cityMovementService;
        }

        public string ScenarioId
        {
            get { return CardFlowScenarioIds.MoveCityEvent; }
        }

        public string ChoiceType
        {
            get { return CardFlowChoiceTypes.MoveCityEvent; }
        }

        public string ResolvePoolId(GameState state, CardFlowStartRequest request)
        {
            var eventColor = StaticMapDefinitions.GetEventColor(request.TargetId);
            return EventCardPoolIds.FromColor(eventColor);
        }

        public ValidationResult ValidateStart(GameState state, CardFlowStartRequest request, EventCardDefinition previewCard)
        {
            return cityMovementService.CanMoveCity(state, request.PlayerId, request.TargetId, -1, null);
        }

        public ValidationResult OnCardRevealed(GameState state, CardFlowContext context, EventCardDefinition card)
        {
            return cityMovementService.RevealMoveCityCard(state, context, card);
        }

        public ValidationResult ValidateOption(
            GameState state,
            CardFlowContext context,
            EventCardDefinition card,
            int optionIndex,
            IReadOnlyList<StringKeyValuePair> arguments)
        {
            var eventInfluenceSlotIds = SplitIds(CardFlowArgumentUtility.GetValue(arguments, EventInfluenceSlotIdsArgument));
            if (state.PendingCardSession != null)
            {
                return cityMovementService.ValidateMoveCityEventOption(
                    state,
                    context.PlayerId,
                    context.TargetId,
                    card,
                    optionIndex,
                    eventInfluenceSlotIds);
            }

            return cityMovementService.CanMoveCity(
                state,
                context.PlayerId,
                context.TargetId,
                optionIndex,
                eventInfluenceSlotIds);
        }

        public CardFlowApplyResult ApplyOption(
            GameState state,
            CardFlowContext context,
            EventCardDefinition card,
            int optionIndex,
            IReadOnlyList<StringKeyValuePair> arguments)
        {
            var eventInfluenceSlotIds = SplitIds(CardFlowArgumentUtility.GetValue(arguments, EventInfluenceSlotIdsArgument));
            ResourceSet reward;
            var validation = cityMovementService.ApplyMoveCityEventOption(
                state,
                context.PlayerId,
                context.TargetId,
                card,
                optionIndex,
                eventInfluenceSlotIds,
                out reward);

            return new CardFlowApplyResult
            {
                Validation = validation,
                Reward = reward
            };
        }

        private static List<string> SplitIds(string encoded)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(encoded))
            {
                return result;
            }

            var parts = encoded.Split(new[] { ',', ';', '|' }, System.StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                result.Add(parts[i].Trim());
            }

            return result;
        }
    }
}
