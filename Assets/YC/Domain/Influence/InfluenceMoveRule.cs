using System;
using YC.Domain.State;

namespace YC.Domain.Influence
{
    public sealed class InfluenceMoveRule
    {
        private readonly InfluencePlacementRule placementRule;

        public InfluenceMoveRule(InfluencePlacementRule placementRule)
        {
            _ = placementRule ?? throw new ArgumentNullException(nameof(placementRule));
            this.placementRule = placementRule;
        }

        public InfluenceMoveRule()
            : this(new InfluencePlacementRule())
        {
        }

        public InfluenceOperationResult Validate(GameState state, int playerId, string sourceSlotId, string targetSlotId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return ValidateCore(state, playerId, sourceSlotId, targetSlotId);
        }

        private InfluenceOperationResult ValidateCore(GameState state, int playerId, string sourceSlotId, string targetSlotId)
        {
            var sourcePlacement = FindInfluenceAtSlot(state, sourceSlotId);
            if (sourcePlacement == null)
            {
                return InfluenceOperationResult.Failure(
                    InfluenceFailureCode.InfluenceNotFound,
                    "Source influence must exist before it can move.",
                    playerId, sourceSlotId, false);
            }

            if (sourcePlacement.PlayerId != playerId)
            {
                return InfluenceOperationResult.Failure(
                    InfluenceFailureCode.InfluenceOwnerMismatch,
                    "Only the owner can move this influence.",
                    playerId, sourceSlotId, false);
            }

            return placementRule.Validate(state, playerId, targetSlotId, false);
        }

        private static InfluencePlacement FindInfluenceAtSlot(GameState state, string slotId)
        {
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                if (state.Map.Influences[i].SlotId == slotId)
                {
                    return state.Map.Influences[i];
                }
            }

            return null;
        }
    }
}
