using YC.Domain.Cards;
using YC.Domain.CardFlows;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Setup
{
    internal sealed class EntranceEventCardScenario : IEventCardScenario
    {
        private readonly ResourceTokenService resourceTokenService;
        private readonly IMapQueryService mapQuery;

        public EntranceEventCardScenario(ResourceTokenService resourceTokenService, IMapQueryService mapQuery)
        {
            this.resourceTokenService = resourceTokenService;
            this.mapQuery = mapQuery ?? throw new System.ArgumentNullException(nameof(mapQuery));
        }

        public string ScenarioId
        {
            get { return CardFlowScenarioIds.EntranceEvent; }
        }

        public string ChoiceType
        {
            get { return CardFlowChoiceTypes.EntranceEvent; }
        }

        public string ResolvePoolId(GameState state, CardFlowStartRequest request)
        {
            var eventColor = StaticMapDefinitions.GetEventColor(mapQuery.Map, request.TargetId);
            return EventCardPoolIds.FromColor(eventColor);
        }

        public ValidationResult ValidateStart(GameState state, CardFlowStartRequest request, EventCardDefinition previewCard)
        {
            return ValidationResult.Success;
        }

        public ValidationResult OnCardRevealed(GameState state, CardFlowContext context, EventCardDefinition card)
        {
            resourceTokenService.PlaceToken(
                state.Map,
                context.TargetId,
                card.RepresentativeResourceType,
                card.RepresentativeResourceAmount);
            return ValidationResult.Success;
        }

        public ValidationResult ValidateOption(
            GameState state,
            CardFlowContext context,
            EventCardDefinition card,
            int optionIndex,
            System.Collections.Generic.IReadOnlyList<StringKeyValuePair> arguments)
        {
            return ValidationResult.Success;
        }

        public CardFlowApplyResult ApplyOption(
            GameState state,
            CardFlowContext context,
            EventCardDefinition card,
            int optionIndex,
            System.Collections.Generic.IReadOnlyList<StringKeyValuePair> arguments)
        {
            var player = state.FindPlayer(context.PlayerId);
            if (player == null)
            {
                return new CardFlowApplyResult
                {
                    Validation = ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "处理入场事件前，玩家必须存在。")
                };
            }

            var reward = card.ChoiceRewards[optionIndex].Clone();
            player.Resources.Add(reward);
            return new CardFlowApplyResult
            {
                Validation = ValidationResult.Success,
                Reward = reward
            };
        }
    }
}
