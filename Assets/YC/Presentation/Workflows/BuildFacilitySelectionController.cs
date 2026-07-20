using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation
{
    public enum BuildFacilityDraftPhase
    {
        Inactive,
        Selecting,
        Dragging,
        Focused,
        Ghosted,
        Confirming
    }

    public sealed class BuildFacilitySelectionController
    {
        private readonly BuildFacilityOptionQueryService optionQuery;
        private BuildFacilityDraftPhase dragOriginPhase = BuildFacilityDraftPhase.Selecting;
        private int dragOriginSlotIndex = -1;

        public BuildFacilitySelectionController() : this(new BuildFacilityOptionQueryService()) { }

        public BuildFacilitySelectionController(BuildFacilityOptionQueryService optionQuery)
        {
            this.optionQuery = optionQuery ?? throw new ArgumentNullException(nameof(optionQuery));
        }

        public BuildFacilityDraftPhase Phase { get; private set; } = BuildFacilityDraftPhase.Inactive;
        public int PlayerId { get; private set; }
        public string FacilityId { get; private set; } = string.Empty;
        public int CityBoardSlotIndex { get; private set; } = -1;
        public string PaymentMode { get; private set; } = string.Empty;
        public string ErrorMessage { get; private set; } = string.Empty;
        public bool IsActive => Phase != BuildFacilityDraftPhase.Inactive;

        public void Begin(int playerId)
        {
            Reset();
            PlayerId = playerId;
            Phase = BuildFacilityDraftPhase.Selecting;
        }

        public bool TryBeginDrag(GameState state, string facilityId, out string reason)
        {
            reason = string.Empty;
            if (Phase == BuildFacilityDraftPhase.Inactive || Phase == BuildFacilityDraftPhase.Dragging)
            {
                reason = "当前不能从公共建设牌堆开始拖动。";
                return false;
            }

            var option = optionQuery.Query(state, PlayerId, facilityId);
            if (option == null || !option.CanBuild)
            {
                reason = option == null ? "设施不在公共建设牌堆中。" : option.Reason;
                return false;
            }

            FacilityId = facilityId ?? string.Empty;
            CityBoardSlotIndex = -1;
            PaymentMode = string.Empty;
            ErrorMessage = string.Empty;
            dragOriginPhase = BuildFacilityDraftPhase.Selecting;
            dragOriginSlotIndex = -1;
            Phase = BuildFacilityDraftPhase.Dragging;
            return true;
        }

        public bool TryBeginGhostDrag(out string reason)
        {
            reason = string.Empty;
            if (Phase != BuildFacilityDraftPhase.Ghosted || string.IsNullOrEmpty(FacilityId) || CityBoardSlotIndex < 0)
            {
                reason = "当前没有可重新拖动的建设虚影。";
                return false;
            }

            dragOriginPhase = BuildFacilityDraftPhase.Ghosted;
            dragOriginSlotIndex = CityBoardSlotIndex;
            PaymentMode = string.Empty;
            ErrorMessage = string.Empty;
            Phase = BuildFacilityDraftPhase.Dragging;
            return true;
        }

        public bool TryDrop(GameState state, int cityBoardSlotIndex, out string reason)
        {
            reason = string.Empty;
            if (Phase != BuildFacilityDraftPhase.Dragging)
            {
                reason = "当前没有正在拖动的建设卡。";
                return false;
            }

            var slot = FindSlot(optionQuery.Query(state, PlayerId, FacilityId), cityBoardSlotIndex);
            if (slot == null || !slot.IsLegal)
            {
                reason = slot == null ? "城市面板槽位无效。" : slot.Reason;
                RestoreAfterRejectedDrop();
                return false;
            }

            CityBoardSlotIndex = cityBoardSlotIndex;
            PaymentMode = string.Empty;
            ErrorMessage = string.Empty;
            Phase = BuildFacilityDraftPhase.Focused;
            return true;
        }

        public void RejectDrop()
        {
            if (Phase == BuildFacilityDraftPhase.Dragging) RestoreAfterRejectedDrop();
        }

        public bool CollapseFocusToGhost()
        {
            if (Phase != BuildFacilityDraftPhase.Focused) return false;
            ErrorMessage = string.Empty;
            Phase = BuildFacilityDraftPhase.Ghosted;
            return true;
        }

        public bool TrySelectPayment(GameState state, string paymentMode, out string reason)
        {
            reason = string.Empty;
            if (Phase != BuildFacilityDraftPhase.Focused)
            {
                reason = "请先把建设卡放到合法槽位。";
                return false;
            }

            var payment = FindPayment(optionQuery.Query(state, PlayerId, FacilityId), paymentMode);
            if (payment == null || !payment.IsAvailable)
            {
                reason = payment == null ? "未知的支付方式。" : payment.Reason;
                ErrorMessage = reason;
                return false;
            }

            PaymentMode = paymentMode ?? string.Empty;
            ErrorMessage = string.Empty;
            Phase = BuildFacilityDraftPhase.Confirming;
            return true;
        }

        public bool BackToPayment()
        {
            if (Phase != BuildFacilityDraftPhase.Confirming) return false;
            PaymentMode = string.Empty;
            ErrorMessage = string.Empty;
            Phase = BuildFacilityDraftPhase.Focused;
            return true;
        }

        public GameCommand CreateConfirmationCommand()
        {
            if (Phase != BuildFacilityDraftPhase.Confirming || string.IsNullOrEmpty(FacilityId) ||
                CityBoardSlotIndex < 0 || string.IsNullOrEmpty(PaymentMode))
                throw new InvalidOperationException("建设草稿尚未进入最终确认状态。");
            return CreateCommand(PlayerId, FacilityId, CityBoardSlotIndex, PaymentMode);
        }

        public void MarkSubmissionFailed(string reason)
        {
            if (Phase == BuildFacilityDraftPhase.Confirming) ErrorMessage = reason ?? string.Empty;
        }

        public void MarkSubmissionSucceeded() { Reset(); }
        public void Cancel() { Reset(); }

        public IReadOnlyList<int> QueryLegalSlotIndexes(GameState state)
        {
            var result = new List<int>();
            var option = optionQuery.Query(state, PlayerId, FacilityId);
            if (option == null || option.SlotOptions == null) return result.AsReadOnly();
            for (var i = 0; i < option.SlotOptions.Count; i++)
                if (option.SlotOptions[i].IsLegal) result.Add(option.SlotOptions[i].CityBoardSlotIndex);
            return result.AsReadOnly();
        }

        public BuildFacilityOptionQueryResult QuerySelectedOption(GameState state)
        {
            return string.IsNullOrEmpty(FacilityId) ? null : optionQuery.Query(state, PlayerId, FacilityId);
        }

        public IReadOnlyList<BuildFacilityOptionQueryResult> QueryOptions(GameState state)
        {
            return optionQuery.Query(state, PlayerId);
        }

        public IReadOnlyList<BuildFacilityOptionQueryResult> QueryOptions(GameState state, int playerId)
        {
            return optionQuery.Query(state, playerId);
        }

        public GameCommand CreateCommand(int playerId, string facilityId, int cityBoardSlotIndex, string paymentMode)
        {
            if (string.IsNullOrEmpty(facilityId))
                throw new ArgumentException("正式建设命令必须指定设施。", nameof(facilityId));
            if (cityBoardSlotIndex < 0 || cityBoardSlotIndex >= BuildFacilityService.CityBoardSlotCount)
                throw new ArgumentOutOfRangeException(nameof(cityBoardSlotIndex));
            if (paymentMode != BuildFacilityService.PaymentModeResources &&
                paymentMode != BuildFacilityService.PaymentModeGold)
                throw new ArgumentException("正式建设命令必须指定资源或金券支付方式。", nameof(paymentMode));

            return new GameCommand
            {
                Kind = GameCommandKind.BuildFacility,
                PlayerId = playerId,
                TargetId = facilityId ?? string.Empty,
                Parameters =
                {
                    { BuildFacilityCommandHandler.CityBoardSlotIndexParameter, cityBoardSlotIndex.ToString() },
                    { BuildFacilityCommandHandler.PaymentModeParameter, paymentMode }
                }
            };
        }

        private static BuildFacilitySlotOptionResult FindSlot(BuildFacilityOptionQueryResult option, int slotIndex)
        {
            if (option == null || option.SlotOptions == null) return null;
            for (var i = 0; i < option.SlotOptions.Count; i++)
                if (option.SlotOptions[i].CityBoardSlotIndex == slotIndex) return option.SlotOptions[i];
            return null;
        }

        private static BuildFacilityPaymentOptionResult FindPayment(BuildFacilityOptionQueryResult option, string paymentMode)
        {
            if (option == null || option.PaymentOptions == null) return null;
            for (var i = 0; i < option.PaymentOptions.Count; i++)
                if (string.Equals(option.PaymentOptions[i].PaymentMode, paymentMode, StringComparison.OrdinalIgnoreCase)) return option.PaymentOptions[i];
            return null;
        }

        private void RestoreAfterRejectedDrop()
        {
            PaymentMode = string.Empty;
            ErrorMessage = string.Empty;
            if (dragOriginPhase == BuildFacilityDraftPhase.Ghosted && dragOriginSlotIndex >= 0)
            {
                CityBoardSlotIndex = dragOriginSlotIndex;
                Phase = BuildFacilityDraftPhase.Ghosted;
                return;
            }
            Reset();
        }

        private void Reset()
        {
            Phase = BuildFacilityDraftPhase.Inactive;
            PlayerId = 0;
            FacilityId = string.Empty;
            CityBoardSlotIndex = -1;
            PaymentMode = string.Empty;
            ErrorMessage = string.Empty;
            dragOriginPhase = BuildFacilityDraftPhase.Selecting;
            dragOriginSlotIndex = -1;
        }
    }
}
