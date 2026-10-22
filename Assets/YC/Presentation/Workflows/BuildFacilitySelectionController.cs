using System;
using System.Collections.Generic;
using System.Linq;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    public enum BuildFacilityDraftPhase
    {
        Inactive,
        Selecting,
        Dragging,
        Focused,
        Ghosted,
        Confirming,
        Placing
    }

    public sealed class BuildFacilitySelectionController
    {
        private readonly BuildFacilityOptionQueryService optionQuery;
        private readonly FacilityBuildCandidateCache candidates;
        private string selectedSourceZoneId = string.Empty;
        private readonly List<string> additionalBuildFacilityIds = new List<string>();
        private BuildFacilityDraftPhase dragOriginPhase = BuildFacilityDraftPhase.Selecting;
        private int dragOriginSlotIndex = -1;
        private bool isAdditionalBuild;
        private string effectBuildOwner = string.Empty;

        public BuildFacilitySelectionController() : this(new BuildFacilityOptionQueryService()) { }

        public BuildFacilitySelectionController(BuildFacilityOptionQueryService optionQuery)
        {
            this.optionQuery = optionQuery ?? throw new ArgumentNullException(nameof(optionQuery));
            candidates = new FacilityBuildCandidateCache(optionQuery);
        }

        public BuildFacilityDraftPhase Phase { get; private set; } = BuildFacilityDraftPhase.Inactive;
        public int PlayerId { get; private set; }
        public string FacilityId { get; private set; } = string.Empty;
        public int CityBoardSlotIndex { get; private set; } = -1;
        public string PaymentMode { get; private set; } = string.Empty;
        public string ErrorMessage { get; private set; } = string.Empty;
        public bool IsSupplySource => !isAdditionalBuild && effectBuildOwner.Length == 0 && candidates.IsDefaultSupplySet;
        public bool IsActive => Phase != BuildFacilityDraftPhase.Inactive;
        public Func<GameStateView> GetVisibleState { private get; set; }
        public bool IsEffectBuild => effectBuildOwner.Length != 0;
        public EffectBuildScope ReadEffectBuildScope(GameState state) => IsEffectBuild &&
            EffectBuildScope.TryReadEffectBuildScope(state, GetVisibleState?.Invoke(), PlayerId, effectBuildOwner, out var scope) ? scope : null;

        public void BeginEffectBuild(EffectBuildScope scope)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            Reset(); PlayerId = scope.PlayerId; effectBuildOwner = scope.OwnerEffectId;
            Phase = BuildFacilityDraftPhase.Selecting;
            if (scope.Payment != null)
            {
                FacilityId = scope.Payment.TargetId;
                scope.TryGetCandidateSourceZoneId(FacilityId, out selectedSourceZoneId);
                Phase = BuildFacilityDraftPhase.Focused;
            }
        }

        public void Begin(int playerId)
        {
            Reset();
            PlayerId = playerId;
            Phase = BuildFacilityDraftPhase.Selecting;
        }

        public void BeginAdditionalBuild(int playerId, IReadOnlyList<string> facilityIds)
        {
            Reset();
            PlayerId = playerId;
            isAdditionalBuild = true;
            if (facilityIds != null)
            {
                for (var i = 0; i < facilityIds.Count; i++)
                {
                    var facilityId = facilityIds[i];
                    if (!string.IsNullOrEmpty(facilityId) &&
                        !additionalBuildFacilityIds.Contains(facilityId))
                    {
                        additionalBuildFacilityIds.Add(facilityId);
                    }
                }
            }

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

            if (isAdditionalBuild && !additionalBuildFacilityIds.Contains(facilityId))
            {
                reason = "该设施不在本次额外建设的可选供应中。";
                return false;
            }

            var option = QueryOption(state, facilityId);
            if (option == null || !(IsEffectBuild ? option.PreparationValidation.IsValid : isAdditionalBuild ? option.CanBuild : CanPrepareOption(option)))
            {
                reason = option == null ? "设施不在公共建设牌堆中。" : option.Reason;
                return false;
            }

            FacilityId = facilityId ?? string.Empty;
            selectedSourceZoneId = option.SourceZoneId;
            CityBoardSlotIndex = -1;
            PaymentMode = string.Empty;
            ErrorMessage = string.Empty;
            dragOriginPhase = BuildFacilityDraftPhase.Selecting;
            dragOriginSlotIndex = -1;
            Phase = BuildFacilityDraftPhase.Dragging;
            return true;
        }

        public bool TrySelectFacility(GameState state, string facilityId, out string reason)
        {
            if (isAdditionalBuild && !additionalBuildFacilityIds.Contains(facilityId))
            { reason = "设施不在本次候选中。"; return false; }
            var option = QueryOption(state, facilityId);
            reason = option == null ? "设施不在本次候选中。" : option.Reason;
            if (!IsActive || option == null || !(IsEffectBuild ? option.PreparationValidation.IsValid : isAdditionalBuild ? option.CanBuild : CanPrepareOption(option))) return false;
            FacilityId = facilityId;
            selectedSourceZoneId = option.SourceZoneId;
            CityBoardSlotIndex = -1;
            PaymentMode = ErrorMessage = string.Empty;
            Phase = BuildFacilityDraftPhase.Focused;
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

            var slot = FindSlot(QueryOption(state, FacilityId), cityBoardSlotIndex);
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

        public bool TrySelectPayment(GameState state, string paymentMode, out string reason, bool hasPreparedPayment = false)
        {
            reason = string.Empty;
            if (Phase != BuildFacilityDraftPhase.Focused)
            {
                reason = "请先选择设施牌。";
                return false;
            }

            var payment = FindPayment(QueryOption(state, FacilityId), paymentMode);
            if (payment == null || !hasPreparedPayment && !payment.IsAvailable)
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

        public static bool CanPrepareOption(BuildFacilityOptionQueryResult option) => option != null &&
            (option.CanBuild || option.Validation.ErrorCode == CommandErrorCode.InsufficientResource);

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
                string.IsNullOrEmpty(PaymentMode))
                throw new InvalidOperationException("建设草稿尚未进入最终确认状态。");
            if (CityBoardSlotIndex >= 0) return CreateCommand(PlayerId, FacilityId, CityBoardSlotIndex, PaymentMode);
            var command = new GameCommand { Kind = GameCommandKind.BuildFacility, PlayerId = PlayerId, TargetId = FacilityId };
            command.Parameters[BuildFacilityCommandHandler.PaymentModeParameter] = PaymentMode;
            command.Parameters[BuildFacilityCommandHandler.ExpectedSourceZoneIdParameter] = selectedSourceZoneId;
            return command;
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
            var option = QueryOption(state, FacilityId);
            if (option == null || option.SlotOptions == null) return result.AsReadOnly();
            for (var i = 0; i < option.SlotOptions.Count; i++)
                if (option.SlotOptions[i].IsLegal) result.Add(option.SlotOptions[i].CityBoardSlotIndex);
            return result.AsReadOnly();
        }

        public BuildFacilityOptionQueryResult QuerySelectedOption(GameState state)
        {
            return string.IsNullOrEmpty(FacilityId) ? null : QueryOption(state, FacilityId);
        }

        public IReadOnlyList<BuildFacilityOptionQueryResult> QueryOptions(GameState state)
        {
            if (IsEffectBuild) return optionQuery.QueryForEffectBuild(state, PlayerId, GetVisibleState?.Invoke(), effectBuildOwner);
            if (isAdditionalBuild)
            {
                var options = new List<BuildFacilityOptionQueryResult>(additionalBuildFacilityIds.Count);
                for (var i = 0; i < additionalBuildFacilityIds.Count; i++)
                {
                    options.Add(optionQuery.QueryForAdditionalBuild(
                        state,
                        PlayerId,
                        additionalBuildFacilityIds[i]));
                }

                return options.AsReadOnly();
            }

            return candidates.Query(state, PlayerId, GetVisibleState?.Invoke());
        }

        public IReadOnlyList<BuildFacilityOptionQueryResult> QueryOptions(GameState state, int playerId)
        {
            return candidates.Query(state, playerId, GetVisibleState?.Invoke());
        }

        private BuildFacilityOptionQueryResult QueryOption(GameState state, string facilityId)
        {
            if (IsEffectBuild) return QueryOptions(state).FirstOrDefault(option => option.FacilityId == facilityId &&
                (facilityId != FacilityId || string.IsNullOrEmpty(selectedSourceZoneId) || selectedSourceZoneId == option.SourceZoneId));
            if (isAdditionalBuild) return optionQuery.QueryForAdditionalBuild(state, PlayerId, facilityId);
            foreach (var option in candidates.Query(state, PlayerId, GetVisibleState?.Invoke()))
                if (option.FacilityId == facilityId)
                    return facilityId == FacilityId && !string.IsNullOrEmpty(selectedSourceZoneId) &&
                        selectedSourceZoneId != option.SourceZoneId ? null : option;
            return null;
        }

        public BuildFacilityOptionQueryResult SelectedOption(IReadOnlyList<BuildFacilityOptionQueryResult> options)
        {
            foreach (var option in options)
                if (option.FacilityId == FacilityId)
                    return !string.IsNullOrEmpty(selectedSourceZoneId) && selectedSourceZoneId != option.SourceZoneId ? null : option;
            return null;
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
                    { BuildFacilityCommandHandler.PaymentModeParameter, paymentMode },
                    { BuildFacilityCommandHandler.ExpectedSourceZoneIdParameter, facilityId == FacilityId ? selectedSourceZoneId : string.Empty }
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

            FacilityId = string.Empty;
            CityBoardSlotIndex = -1;
            dragOriginPhase = BuildFacilityDraftPhase.Selecting;
            dragOriginSlotIndex = -1;
            Phase = BuildFacilityDraftPhase.Selecting;
        }

        private void Reset()
        {
            effectBuildOwner = string.Empty;
            candidates.Invalidate();
            selectedSourceZoneId = string.Empty;
            Phase = BuildFacilityDraftPhase.Inactive;
            PlayerId = 0;
            FacilityId = string.Empty;
            CityBoardSlotIndex = -1;
            PaymentMode = string.Empty;
            ErrorMessage = string.Empty;
            dragOriginPhase = BuildFacilityDraftPhase.Selecting;
            dragOriginSlotIndex = -1;
            isAdditionalBuild = false;
            additionalBuildFacilityIds.Clear();
        }
    }
}
