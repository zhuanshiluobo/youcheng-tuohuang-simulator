using YC.Domain.Cards;
using YC.Domain.Commands;

namespace YC.Domain.CardFlows
{
    public sealed class CardFlowStartResult
    {
        public bool Succeeded;
        public ValidationResult Validation;
        public CardFlowContext Context;
        public EventCardDefinition Card;
    }
}
