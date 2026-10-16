using System;
using System.Collections.Generic;
using System.Linq;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public sealed class BuildInteraction : InteractionBase
    {
        private static readonly MainActionBudgetService MainActionBudgetService =
            new MainActionBudgetService();

        private readonly IWritableGameplayContext context;
        private readonly CommandGateway commandGateway;
        private readonly ITurnActionView view;
        private readonly InteractionFlowCoordinator flowCoordinator;
        private readonly Func<bool> canStartMainAction;
        private readonly Action<string> completeAction;
        private readonly BuildFacilitySelectionController selection;
        private readonly ActionPaymentPreparation paymentPreparation;
        private string finalMode, finalFacility, finalGame;
        private int finalPlayer, finalRound, finalActionRound;
        private GamePhase finalPhase;
        private PreparedActionPaymentView finalPreparation;
        public Func<IReadOnlyList<InteractionRequest>> GetVisibleRequests { private get; set; }
        public Func<GameStateView> GetVisibleState { set => paymentPreparation.GetVisibleState = value; }
        public bool HasPreparedPayment => paymentPreparation.HasPreparation;
        public bool CanResumePreparedDraft => selection.IsActive && selection.PlayerId == context.LocalPlayerId &&
            (paymentPreparation.IsInFlight || paymentPreparation.Current?.TargetId == selection.FacilityId);
        public bool CanCancelPreparedPayment => paymentPreparation.CanCancel;
        public bool TryCancelPreparedPayment(Action afterApplied)
        {
            ClearFinalPaymentIntent();
            return paymentPreparation.Cancel(() => { CancelCore(false); afterApplied?.Invoke(); });
        }
        private string inFlightCommandId = string.Empty;

        public BuildInteraction(
            IWritableGameplayContext context,
            IGameCommandPort commandPort,
            ITurnActionView view,
            InteractionFlowCoordinator flowCoordinator,
            Func<bool> canStartMainAction,
            Action<string> completeAction,
            BuildFacilityOptionQueryService buildOptions = null)
            : this(
                context,
                new CommandGateway(
                    commandPort ?? throw new ArgumentNullException(nameof(commandPort))),
                view,
                flowCoordinator,
                canStartMainAction,
                completeAction, buildOptions)
        {
        }

        internal BuildInteraction(
            IWritableGameplayContext context,
            CommandGateway commandGateway,
            ITurnActionView view,
            InteractionFlowCoordinator flowCoordinator,
            Func<bool> canStartMainAction,
            Action<string> completeAction,
            BuildFacilityOptionQueryService buildOptions = null)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            selection = new BuildFacilitySelectionController(buildOptions ?? new BuildFacilityOptionQueryService());
            this.commandGateway = commandGateway ??
                                  throw new ArgumentNullException(nameof(commandGateway));
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.flowCoordinator = flowCoordinator ??
                                   throw new ArgumentNullException(nameof(flowCoordinator));
            this.canStartMainAction = canStartMainAction ??
                                      throw new ArgumentNullException(nameof(canStartMainAction));
            this.completeAction = completeAction ??
                                  throw new ArgumentNullException(nameof(completeAction));
            paymentPreparation = new ActionPaymentPreparation(context, commandGateway, "build", OnPaymentPreparationChanged, view.ShowPrompt);
        }

        public override string Id
        {
            get { return "build-facility"; }
        }

        public override InteractionPriority Priority
        {
            get { return InteractionPriority.ActiveAction; }
        }

        public override bool IsActive
        {
            get { return selection.IsActive; }
        }

        public BuildFacilityDraftPhase Phase
        {
            get { return selection.Phase; }
        }

        public string FacilityId => selection.FacilityId;
        public int CityBoardSlotIndex => selection.CityBoardSlotIndex;

        public bool IsSubmissionInFlight
        {
            get { return !string.IsNullOrEmpty(inFlightCommandId) || paymentPreparation.IsInFlight; }
        }

        public override InteractionResult OnLocationClicked(string locationId)
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionResult OnMobileCityClicked()
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionResult OnEscape()
        {
            if (!HandleEscape())
            {
                return InteractionResult.Passthrough;
            }

            return InteractionResult.Consumed;
        }

        public override InteractionPresentation BuildPresentation()
        {
            if (!IsActive)
            {
                return InteractionPresentation.Empty;
            }

            return new InteractionPresentation(
                null,
                BuildPresentationPrompt(),
                InteractionMode.Busy);
        }

        public override void Cancel()
        {
            if (paymentPreparation.IsInFlight || !string.IsNullOrEmpty(inFlightCommandId)) return;
            if (paymentPreparation.HasPreparation) { paymentPreparation.Cancel(() => CancelCore(false)); return; }
            inFlightCommandId = string.Empty;
            CancelCore(false);
        }

        public void Begin()
        {
            if (RejectMutationWhileSubmitting())
            {
                return;
            }

            if (!TryOpenDraft())
            {
                return;
            }

            PresentDraft();
        }

        public void SelectFacility(string facilityId)
        {
            if (RejectMutationWhileSubmitting()) return;
            ClearFinalPaymentIntent();
            if (paymentPreparation.Current != null && paymentPreparation.Current.TargetId != facilityId)
            {
                paymentPreparation.Cancel(() => SelectFacility(facilityId));
                return;
            }
            if (!selection.IsActive && !TryOpenDraft()) return;
            if (!selection.TrySelectFacility(context.CurrentState, facilityId, out var reason)) view.ShowPrompt(reason);
            else
            {
                var option = selection.QuerySelectedOption(context.CurrentState);
                if (paymentPreparation.Current == null)
                    paymentPreparation.Prepare(facilityId, BuildFacilityService.PaymentModeResources, option.SourceZoneId);
            }
            PresentDraft();
        }

        public void BeginDrag(string facilityId)
        {
            if (paymentPreparation.Current != null && paymentPreparation.Current.TargetId != facilityId)
            {
                ClearFinalPaymentIntent();
                paymentPreparation.Cancel(() => BeginDrag(facilityId));
                return;
            }
            if (RejectMutationWhileSubmitting())
            {
                return;
            }

            if (!selection.IsActive && !TryOpenDraft())
            {
                return;
            }

            string reason;
            if (!selection.TryBeginDrag(context.CurrentState, facilityId, out reason))
            {
                view.ShowPrompt(reason);
                PresentDraft();
                return;
            }

            flowCoordinator.SetMode(InteractionMode.Busy);
            PresentDraft();
            if (paymentPreparation.Current == null)
            {
                var option = selection.QuerySelectedOption(context.CurrentState);
                paymentPreparation.Prepare(facilityId, BuildFacilityService.PaymentModeResources, option.SourceZoneId);
            }
        }

        public void Drop(int cityBoardSlotIndex)
        {
            if (RejectMutationWhileSubmitting())
            {
                return;
            }

            string reason;
            if (!selection.TryDrop(context.CurrentState, cityBoardSlotIndex, out reason))
            {
                view.ShowPrompt(reason);
                SynchronizeInteractionMode();
                PresentDraft();
                return;
            }

            flowCoordinator.SetMode(InteractionMode.Busy);
            PresentDraft();
        }

        public void RejectDrop()
        {
            if (RejectMutationWhileSubmitting())
            {
                return;
            }

            selection.RejectDrop();
            SynchronizeInteractionMode();
            PresentDraft();
        }

        public void BeginGhostDrag()
        {
            if (RejectMutationWhileSubmitting())
            {
                return;
            }

            string reason;
            if (!selection.TryBeginGhostDrag(out reason))
            {
                view.ShowPrompt(reason);
                return;
            }

            flowCoordinator.SetMode(InteractionMode.Busy);
            PresentDraft();
        }

        public bool HandleEscape()
        {
            if (!selection.IsActive)
            {
                return false;
            }

            if (RejectMutationWhileSubmitting())
            {
                return true;
            }

            if (selection.CityBoardSlotIndex < 0)
            {
                CancelExplicitly();
                return true;
            }
            if (selection.Phase == BuildFacilityDraftPhase.Confirming)
            {
                selection.BackToPayment();
            }

            if (selection.Phase == BuildFacilityDraftPhase.Focused &&
                selection.CollapseFocusToGhost())
            {
                SynchronizeInteractionMode();
                PresentDraft();
                view.ShowPrompt("建设草稿已缩回本地虚影；拖动虚影可继续，点击 × 可取消建设。");
            }

            return true;
        }

        public void SelectPayment(string paymentMode)
        {
            if (TryPreparePayment(paymentMode)) PresentDraft();
        }

        // 支付按钮使用此入口准备正式命令，成功后不刷新中间“已选支付方式”页面。
        public bool TryPreparePayment(string paymentMode)
        {
            if (RejectMutationWhileSubmitting()) return false;
            if (!selection.IsActive || string.IsNullOrEmpty(selection.FacilityId) ||
                (paymentMode != BuildFacilityService.PaymentModeResources && paymentMode != BuildFacilityService.PaymentModeGold)) return false;
            if (paymentPreparation.Current == null || paymentPreparation.Current.PaymentMode != paymentMode)
            {
                finalMode = paymentMode; finalFacility = selection.FacilityId; finalGame = context.CurrentState.GameId;
                finalPlayer = context.LocalPlayerId; finalRound = context.CurrentState.Round;
                finalActionRound = context.CurrentState.ActionRound; finalPhase = context.CurrentState.Phase;
                finalPreparation = null;
                Action prepare = () =>
                {
                    var option = selection.QuerySelectedOption(context.CurrentState);
                    if (option == null) return;
                    paymentPreparation.Prepare(selection.FacilityId, paymentMode, option.SourceZoneId);
                };
                if (paymentPreparation.Current != null)
                {
                    if (!paymentPreparation.Cancel(prepare)) ClearFinalPaymentIntent();
                }
                else prepare();
                return false;
            }
            if (!paymentPreparation.Current.CanConfirm) { PresentDraft(); return false; }
            if (selection.Phase == BuildFacilityDraftPhase.Confirming) selection.BackToPayment();
            if (!selection.TrySelectPayment(context.CurrentState, paymentMode, out var reason, true))
            {
                view.ShowPrompt(reason);
                PresentDraft();
                return false;
            }
            flowCoordinator.SetMode(InteractionMode.Busy);
            return true;
        }

        private void OnPaymentPreparationChanged()
        {
            if (!paymentPreparation.LastResolvedApplied) { ClearFinalPaymentIntent(); view.RefreshFromState(); PresentDraft(); return; }
            if (paymentPreparation.LastResolvedOperation == "prepare_action" && !string.IsNullOrEmpty(finalMode))
                finalPreparation = paymentPreparation.Current;
            view.RefreshFromState(); PresentDraft();
            ContinueFinalPaymentIntent();
        }
        private void ContinueFinalPaymentIntent()
        {
            if (string.IsNullOrEmpty(finalMode) || finalPreparation == null || IsSubmissionInFlight) return;
            var current = paymentPreparation.Current; var state = context.CurrentState;
            var mode = finalMode;
            var valid = ActionPaymentPreparation.SameIdentity(finalPreparation, current) && current.PaymentMode == mode && current.TargetId == finalFacility &&
                state.GameId == finalGame && context.LocalPlayerId == finalPlayer && state.Round == finalRound &&
                state.ActionRound == finalActionRound && state.Phase == finalPhase && selection.FacilityId == finalFacility;
            if (!valid) { ClearFinalPaymentIntent(); return; }
            // 必要响应等待保留这次真实支付点击；计划保存后的可付变化不产生新点击意图。
            if (!current.IsReady) return;
            ClearFinalPaymentIntent();
            if (current.CanConfirm)
            {
                if (selection.Phase == BuildFacilityDraftPhase.Confirming) selection.BackToPayment();
                if (selection.TrySelectPayment(state, mode, out _, true)) Confirm();
            }
        }
        private void ClearFinalPaymentIntent() { finalMode = finalFacility = finalGame = null; finalPreparation = null; }

        public void BackToPayment()
        {
            if (RejectMutationWhileSubmitting())
            {
                return;
            }

            if (!selection.BackToPayment())
            {
                return;
            }

            flowCoordinator.SetMode(InteractionMode.Busy);
            PresentDraft();
        }

        public void CancelExplicitly()
        {
            if (RejectMutationWhileSubmitting())
            {
                return;
            }

            if (paymentPreparation.HasPreparation) { paymentPreparation.Cancel(() => CancelCore(true)); return; }
            CancelCore(true);
        }

        public void Confirm()
        {
            if (RejectMutationWhileSubmitting())
            {
                return;
            }

            if (selection.Phase != BuildFacilityDraftPhase.Confirming)
            {
                return;
            }

            var command = selection.CreateConfirmationCommand();
            if (!paymentPreparation.BindFinalCommand(command)) return;
            inFlightCommandId = command.CommandId;
            var gameId = context.CurrentState.GameId;
            try { commandGateway.Submit(
                command,
                new SubmitCallbacks(
                    view.ShowPrompt,
                    CommandGateway.BuildWaitingForHostPrompt("建设命令"))
                {
                    BeforeRejectedPrompt = result =>
                    { selection.MarkSubmissionFailed(result.Validation.Reason); NotifyInteractionCommandResolved(gameId, command.CommandId, false, result.Validation.ErrorCode); },
                    AfterRejectedPrompt = result => PresentDraft(),
                    OnAppliedLocally = result =>
                    {
                        NotifyInteractionCommandResolved(gameId, command.CommandId, true, default);
                    }
                }); }
            catch { inFlightCommandId = string.Empty; ClearFinalPaymentIntent(); throw; }
        }

        public override void NotifyCommandSettled(string commandId)
        {
            if (!IsSubmissionInFlight ||
                (!string.IsNullOrEmpty(commandId) &&
                 !string.Equals(commandId, inFlightCommandId, StringComparison.Ordinal)))
            {
                return;
            }

            inFlightCommandId = string.Empty;
            SynchronizeFromState();
        }

        public override void NotifyInteractionCommandResolved(string gameId, string commandId, bool accepted, CommandErrorCode errorCode)
        {
            if (paymentPreparation.NotifyResolved(gameId, commandId, accepted)) return;
            if (string.IsNullOrEmpty(inFlightCommandId) || commandId != inFlightCommandId || gameId != context.CurrentState?.GameId) return;
            inFlightCommandId = string.Empty;
            if (accepted)
            {
                ClearFinalPaymentIntent(); paymentPreparation.ResetAfterCommit(); selection.MarkSubmissionSucceeded();
                view.HideBuildFacilityDraft(); view.RefreshInformation();
                if (context.CurrentState.HasPendingChoice()) { flowCoordinator.SetMode(InteractionMode.Busy); view.RefreshActionPanel(); }
                else completeAction("建设");
            }
            else PresentDraft();
        }

        public void SynchronizeFromState()
        {
            ContinueFinalPaymentIntent();
            if (IsSubmissionInFlight ||
                selection.Phase != BuildFacilityDraftPhase.Confirming ||
                !HasConfirmedBuildInState())
            {
                return;
            }

            selection.MarkSubmissionSucceeded();
            view.HideBuildFacilityDraft();
        }

        public BuildFacilityDraftViewModel BuildDraftViewModel()
        {
            if (!selection.IsActive)
            {
                return null;
            }

            var state = context.CurrentState;
            var options = selection.QueryOptions(state);
            var selectedOption = selection.SelectedOption(options);
            var facility = string.IsNullOrEmpty(selection.FacilityId)
                ? null
                : FacilityCardDatabase.Get(state, selectedOption?.DefinitionId ?? selection.FacilityId);
            var model = new BuildFacilityDraftViewModel(
                selection.Phase,
                options,
                selectedOption,
                facility,
                selection.CityBoardSlotIndex,
                selection.PaymentMode,
                selection.ErrorMessage,
                selectedOption == null ? Array.Empty<int>() : selectedOption.SlotOptions
                    .Where(slot => slot.IsLegal).Select(slot => slot.CityBoardSlotIndex).ToArray(),
                Dispatch, selection.IsSupplySource);
            model.PaymentPreparation = paymentPreparation.Current;
            return model;
        }

        public BuildFacilityAvailabilityViewModel BuildAvailabilityViewModel()
        {
            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            var draggableIds = new List<string>();
            var legalSlotIndexes = new List<int>();
            if (state == null || player == null)
            {
                return new BuildFacilityAvailabilityViewModel(
                    draggableIds.AsReadOnly(),
                    legalSlotIndexes.AsReadOnly(),
                    false,
                    string.Empty);
            }

            var isActionPhase = state.Phase == GamePhase.ActionRound1 ||
                                state.Phase == GamePhase.ActionRound2;
            var pending = state.PendingCardSession;
            if (pending != null && pending.IsValid() && pending.PlayerId == context.LocalPlayerId &&
                pending.ScenarioId == FacilityPendingChoiceTypes.ScenarioId &&
                (pending.ChoiceType == FacilityPendingChoiceTypes.BuildAdditionalFacility ||
                 pending.ChoiceType == FacilityPendingChoiceTypes.BuildExtensionHub))
            {
                var query = new BuildFacilityOptionQueryService();
                foreach (var facilityId in pending.OptionIds)
                {
                    if (pending.ChoiceType == FacilityPendingChoiceTypes.BuildExtensionHub)
                    {
                        foreach (var slot in query.GetLegalReserveBuildSlots(state, context.LocalPlayerId, facilityId))
                            if (!legalSlotIndexes.Contains(slot)) legalSlotIndexes.Add(slot);
                    }
                    else
                    {
                        var option = query.QueryForAdditionalBuild(state, context.LocalPlayerId, facilityId);
                        if (!option.CanBuild) continue;
                        draggableIds.Add(facilityId);
                        foreach (var slot in option.SlotOptions)
                            if (slot.IsLegal && !legalSlotIndexes.Contains(slot.CityBoardSlotIndex))
                                legalSlotIndexes.Add(slot.CityBoardSlotIndex);
                    }
                }
                return new BuildFacilityAvailabilityViewModel(draggableIds.AsReadOnly(),
                    legalSlotIndexes.AsReadOnly(), true, string.Empty);
            }
            var canUseMainBuild = isActionPhase &&
                                  state.CurrentPlayerId == context.LocalPlayerId &&
                                  !ActionPaymentPreparation.HasBlockingChoice(state.EffectRuntime.InteractionRequests, context.LocalPlayerId) &&
                                  MainActionBudgetService.HasAvailableMainAction(
                                      state,
                                      context.LocalPlayerId);
            if (canUseMainBuild)
            {
                var options = selection.QueryOptions(state, context.LocalPlayerId);
                for (var i = 0; i < options.Count; i++)
                {
                    if (BuildFacilitySelectionController.CanPrepareOption(options[i]))
                    {
                        draggableIds.Add(options[i].FacilityId);
                    }
                }
            }

            var exhaustedMessage = isActionPhase &&
                                   state.CurrentPlayerId == context.LocalPlayerId &&
                                   !MainActionBudgetService.HasAvailableMainAction(
                                       state,
                                       context.LocalPlayerId)
                ? "本行动轮行动次数已用尽。"
                : string.Empty;
            return new BuildFacilityAvailabilityViewModel(
                draggableIds.AsReadOnly(),
                legalSlotIndexes.AsReadOnly(),
                false,
                exhaustedMessage);
        }

        public void Dispatch(BuildFacilityIntent intent)
        {
            if (intent == null)
            {
                throw new ArgumentNullException(nameof(intent));
            }

            var beginDrag = intent as BuildFacilityIntent.BeginDrag;
            if (beginDrag != null)
            {
                BeginDrag(beginDrag.FacilityId);
                return;
            }

            if (intent is BuildFacilityIntent.BeginGhostDrag)
            {
                BeginGhostDrag();
                return;
            }

            var drop = intent as BuildFacilityIntent.Drop;
            if (drop != null)
            {
                Drop(drop.CityBoardSlotIndex);
                return;
            }

            if (intent is BuildFacilityIntent.RejectDrop)
            {
                RejectDrop();
                return;
            }

            if (intent is BuildFacilityIntent.Escape)
            {
                HandleEscape();
                return;
            }

            var selectPayment = intent as BuildFacilityIntent.SelectPayment;
            if (selectPayment != null)
            {
                SelectPayment(selectPayment.PaymentMode);
                return;
            }

            if (intent is BuildFacilityIntent.Back)
            {
                BackToPayment();
                return;
            }

            if (intent is BuildFacilityIntent.Confirm)
            {
                Confirm();
                return;
            }

            if (intent is BuildFacilityIntent.Cancel)
            {
                CancelExplicitly();
                return;
            }

            throw new ArgumentException("不支持的建设交互意图。", nameof(intent));
        }

        public void RestoreAfterCityStylePreviewClosed()
        {
            if (!selection.IsActive)
            {
                return;
            }

            PresentDraft();
            view.ShowPrompt("已关闭样式卡预览，返回当前建设选择。");
        }

        private bool TryOpenDraft()
        {
            if (!canStartMainAction())
            {
                return false;
            }

            var state = context.CurrentState;
            var player = state.FindPlayer(context.LocalPlayerId);
            if (player == null)
            {
                view.ShowPrompt("当前玩家不存在。");
                return false;
            }

            selection.Begin(context.LocalPlayerId);
            if (selection.QueryOptions(state).Count == 0)
            {
                selection.Cancel();
                view.ShowPrompt("当前没有可选设施牌。");
                return false;
            }

            flowCoordinator.SetMode(InteractionMode.Busy);
            view.ClearHighlights();
            view.RefreshActionPanel();
            return true;
        }

        private void CancelCore(bool showUserPrompt)
        {
            ClearFinalPaymentIntent();
            if (!selection.IsActive)
            {
                return;
            }

            inFlightCommandId = string.Empty;
            selection.Cancel();
            flowCoordinator.ResetToChooseAction();
            view.HideBuildFacilityDraft();
            view.RefreshActionPanel();
            if (showUserPrompt)
            {
                view.ShowPrompt("已取消建设。");
            }
        }

        private void PresentDraft()
        {
            var model = BuildDraftViewModel();
            if (model == null)
            {
                view.HideBuildFacilityDraft();
                return;
            }

            view.ShowBuildFacilityDraft(model);
        }

        private void SynchronizeInteractionMode()
        {
            if (selection.Phase == BuildFacilityDraftPhase.Inactive)
            {
                flowCoordinator.ResetToChooseAction();
                view.RefreshActionPanel();
                return;
            }

            flowCoordinator.SetMode(InteractionMode.Busy);
        }

        private string BuildPresentationPrompt()
        {
            if (IsSubmissionInFlight)
            {
                return CommandGateway.BuildWaitingForHostPrompt("建设命令");
            }

            // 普通建设页面的提示来自其序列化配置；旧拖放提示只服务已有槽位的兼容路径。
            if (selection.CityBoardSlotIndex < 0) return string.Empty;
            switch (selection.Phase)
            {
                case BuildFacilityDraftPhase.Focused:
                    return "建设：选择支付方式，Esc 可缩回虚影";
                case BuildFacilityDraftPhase.Confirming:
                    return "建设：确认摘要或返回修改";
                case BuildFacilityDraftPhase.Ghosted:
                    return "建设：拖动本地虚影可重新选择槽位";
                default:
                    return "建设：拖动公共建设牌到合法槽位";
            }
        }

        private bool RejectMutationWhileSubmitting()
        {
            if (!IsSubmissionInFlight)
            {
                return false;
            }

            view.ShowPrompt(CommandGateway.BuildWaitingForHostPrompt("建设命令"));
            return true;
        }

        private bool HasConfirmedBuildInState()
        {
            var state = context.CurrentState;
            if (state == null || state.Map == null || state.Map.Facilities == null)
            {
                return false;
            }

            var entities = YC.Domain.Items.ItemZoneLegacyProjection.IsActive(state);
            var selectedItem = entities ? state.ItemZones.Items.Find(item => item.ItemId == selection.FacilityId) : null;
            var selectedZone = selectedItem == null ? null : state.ItemZones.Zones.Find(zone => zone.ZoneId == selectedItem.ZoneId);
            foreach (var request in GetVisibleRequests == null ? VisibleInteractionRequestSource.Read(state, null, context.LocalPlayerId) : GetVisibleRequests())
                if (request.InteractionTypeId == YC.Domain.Effects.FacilityBuildEffectExecutor.PlacementInteractionType &&
                    request.AnsweringPlayerId == selection.PlayerId && request.PromptParameters?.Properties != null)
                {
                    if (entities)
                    {
                        if (selectedZone?.LocalZoneId == "build_pending" && selectedZone.ParentId == request.OwnerEffectId)
                            return true;
                        continue;
                    }
                    foreach (var parameter in request.PromptParameters.Properties)
                        if (parameter.Name == "facilityId" && parameter.Value?.StringValue == selection.FacilityId) return true;
                }
            for (var i = 0; i < state.Map.Facilities.Count; i++)
            {
                var facility = state.Map.Facilities[i];
                if (facility != null &&
                    facility.PlayerId == selection.PlayerId &&
                    (selection.CityBoardSlotIndex < 0 || facility.CityBoardSlotIndex == selection.CityBoardSlotIndex) &&
                    string.Equals(
                        entities ? facility.ContentInstanceId : facility.FacilityCardId,
                        selection.FacilityId,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddEmptyCityBoardSlots(
            GameState state,
            int playerId,
            List<int> result)
        {
            for (var slotIndex = 0;
                 slotIndex < BuildFacilityService.CityBoardSlotCount;
                 slotIndex++)
            {
                var occupied = state.Map.Facilities.Exists(placement =>
                    placement.PlayerId == playerId &&
                    placement.CityBoardSlotIndex == slotIndex);
                if (!occupied)
                {
                    result.Add(slotIndex);
                }
            }
        }
    }
}
