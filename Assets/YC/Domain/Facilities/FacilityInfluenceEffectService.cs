using System;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    public sealed class FacilityInfluenceEffectResult
    {
        private FacilityInfluenceEffectResult(
            bool succeeded,
            ValidationResult validation,
            string targetSlotId,
            bool targetRemoved,
            bool replacementPlaced,
            InfluenceFailureCode placementFailureCode,
            string placementFailureReason)
        {
            Succeeded = succeeded;
            Validation = validation;
            TargetSlotId = targetSlotId ?? string.Empty;
            TargetRemoved = targetRemoved;
            ReplacementPlaced = replacementPlaced;
            PlacementFailureCode = placementFailureCode;
            PlacementFailureReason = placementFailureReason ?? string.Empty;
        }

        public bool Succeeded { get; private set; }
        public ValidationResult Validation { get; private set; }
        public string TargetSlotId { get; private set; }
        public bool TargetRemoved { get; private set; }
        public bool ReplacementPlaced { get; private set; }
        public InfluenceFailureCode PlacementFailureCode { get; private set; }
        public string PlacementFailureReason { get; private set; }

        public static FacilityInfluenceEffectResult Success(
            string targetSlotId,
            bool replacementPlaced,
            InfluenceFailureCode placementFailureCode = InfluenceFailureCode.None,
            string placementFailureReason = "")
        {
            return new FacilityInfluenceEffectResult(
                true,
                ValidationResult.Success,
                targetSlotId,
                true,
                replacementPlaced,
                placementFailureCode,
                placementFailureReason);
        }

        public static FacilityInfluenceEffectResult Failure(ValidationResult validation, string targetSlotId)
        {
            return new FacilityInfluenceEffectResult(
                false,
                validation,
                targetSlotId,
                false,
                false,
                InfluenceFailureCode.None,
                string.Empty);
        }
    }

    /// <summary>
    /// 建设牌上的“替换一次影响力”：先移除目标，再尽量在原槽位放置自己的影响力。
    /// 放置受对手城市或供应不足等限制时，已完成的移除不会回滚。
    /// </summary>
    public sealed class FacilityInfluenceEffectService
    {
        private readonly InfluenceService influenceService;

        public FacilityInfluenceEffectService(InfluenceService influenceService)
        {
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
        }

        public FacilityInfluenceEffectResult ReplaceOnce(
            GameState state,
            int playerId,
            string targetInfluenceSlotId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (state.FindPlayer(playerId) == null)
            {
                return Failure(CommandErrorCode.InvalidPlayer, "替换影响力的玩家不存在。", targetInfluenceSlotId);
            }

            var target = influenceService.FindInfluence(state, targetInfluenceSlotId);
            if (target == null)
            {
                return Failure(CommandErrorCode.InvalidSource, "要替换的影响力不存在。", targetInfluenceSlotId);
            }

            if (target.PlayerId == playerId)
            {
                return Failure(CommandErrorCode.InvalidTarget, "不能用自己的影响力替换自己的影响力。", targetInfluenceSlotId);
            }

            var canonicalSlotId = target.SlotId;
            var removal = influenceService.Remove(state, canonicalSlotId);
            if (!removal.Succeeded)
            {
                return FacilityInfluenceEffectResult.Failure(removal.Validation, canonicalSlotId);
            }

            var placement = influenceService.Place(state, playerId, canonicalSlotId);
            return placement.Succeeded
                ? FacilityInfluenceEffectResult.Success(canonicalSlotId, true)
                : FacilityInfluenceEffectResult.Success(
                    canonicalSlotId,
                    false,
                    placement.FailureCode,
                    placement.Reason);
        }

        private static FacilityInfluenceEffectResult Failure(
            CommandErrorCode code,
            string reason,
            string targetSlotId)
        {
            return FacilityInfluenceEffectResult.Failure(
                ValidationResult.Failure(code, reason),
                targetSlotId);
        }
    }
}
