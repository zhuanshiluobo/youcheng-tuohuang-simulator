using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.State;

namespace YC.Domain.CardFlows
{
    public sealed class CardFlowResolveResult
    {
        public bool Succeeded;
        public ValidationResult Validation;
        public CardFlowContext Context;
        public EventCardDefinition Card;
        public int OptionIndex = -1;
        public ResourceSet Reward;
        public InfluenceOperationResult PrimaryInfluencePlacement;
    }
}
