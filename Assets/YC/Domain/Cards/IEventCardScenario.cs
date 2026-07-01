using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.State;

namespace YC.Domain.CardFlows
{
    public interface IEventCardScenario
    {
        string ScenarioId { get; }
        string ChoiceType { get; }

        string ResolvePoolId(GameState state, CardFlowStartRequest request);

        ValidationResult ValidateStart(GameState state, CardFlowStartRequest request, EventCardDefinition previewCard);

        ValidationResult OnCardRevealed(GameState state, CardFlowContext context, EventCardDefinition card);

        ValidationResult ValidateOption(
            GameState state,
            CardFlowContext context,
            EventCardDefinition card,
            int optionIndex,
            System.Collections.Generic.IReadOnlyList<StringKeyValuePair> arguments);

        CardFlowApplyResult ApplyOption(
            GameState state,
            CardFlowContext context,
            EventCardDefinition card,
            int optionIndex,
            System.Collections.Generic.IReadOnlyList<StringKeyValuePair> arguments);
    }
}
