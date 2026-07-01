using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Events;
using YC.Domain.State;

namespace YC.Domain.CardFlows
{
    public sealed class CardFlowApplyResult
    {
        public ValidationResult Validation = ValidationResult.Success;
        public ResourceSet Reward;
        public InfluenceOperationResult PrimaryInfluencePlacement;
        public List<GameEvent> Events = new List<GameEvent>();
    }
}
