using System;
using System.Collections.Generic;
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
        private readonly BuildFacilitySelectionController selection =
            new BuildFacilitySelectionController();
        private string inFlightCommandId = string.Empty;

        public BuildInteraction(
            IWritableGameplayContext context,
            IGameCommandPort commandPort,
            ITurnActionView view,
            InteractionFlowCoordinator flowCoordinator,
            Func<bool> canStartMainAction,
            Action<string> completeAction)
            : this(
                context,
                new CommandGateway(
                    commandPort ?? throw new ArgumentNullException(nameof(commandPort))),
                view,
                flowCoordinator,
                canStartMainAction,
                completeAction)
        {
        }

        internal BuildInteraction(
            IWritableGameplayContext context,
            CommandGateway commandGateway,
            ITurnActionView view,
            InteractionFlowCoordinator flowCoordinator,
            Func<bool> canStartMainAction,
            Action<string> completeAction)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.commandGateway = commandGateway ??
                                  throw new ArgumentNullException(nameof(commandGateway));
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.flowCoordinator = flowCoordinator ??
                                   throw new ArgumentNullException(nameof(flowCoordinator));
            this.canStartMainAction = canStartMainAction ??
                                      throw new ArgumentNullException(nameof(canStartMainAction));
            this.completeAction = completeAction ??
                                  throw new ArgumentNullException(nameof(completeAction));
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

        public bool IsSubmissionInFlight
        {
            get { return !string.IsNullOrEmpty(inFlightCommandId); }
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
            view.ShowPrompt("建设：拖动公共建设牌到自己面板的合法槽位。");
        }

        public void BeginDrag(string facilityId)
        {
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
            if (RejectMutationWhileSubmitting())
            {
                return;
            }

            string reason;
            if (!selection.TrySelectPayment(context.CurrentState, paymentMode, out reason))
            {
                view.ShowPrompt(reason);
                PresentDraft();
                return;
            }

            flowCoordinator.SetMode(InteractionMode.Busy);
            PresentDraft();
        }

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
            var outcome = commandGateway.Submit(
                command,
                new SubmitCallbacks(
                    view.ShowPrompt,
                    CommandGateway.BuildWaitingForHostPrompt("建设命令"))
                {
                    BeforeRejectedPrompt = result =>
                        selection.MarkSubmissionFailed(result.Validation.Reason),
                    AfterRejectedPrompt = result => PresentDraft(),
                    OnAppliedLocally = result =>
                    {
                        selection.MarkSubmissionSucceeded();
                        view.HideBuildFacilityDraft();
                        view.RefreshInformation();
                        completeAction("建设");
                    }
                });
            if (outcome.Kind == SubmitOutcomeKind.WaitingForHost)
            {
                inFlightCommandId = command.CommandId;
            }
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

        public void SynchronizeFromState()
        {
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
            var selectedOption = selection.QuerySelectedOption(state);
            var facility = string.IsNullOrEmpty(selection.FacilityId)
                ? null
                : FacilityCardDatabase.Get(selection.FacilityId);
            return new BuildFacilityDraftViewModel(
                selection.Phase,
                selection.QueryOptions(state),
                selectedOption,
                facility,
                selection.CityBoardSlotIndex,
                selection.PaymentMode,
                selection.ErrorMessage,
                selection.QueryLegalSlotIndexes(state),
                Dispatch);
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

            var pending = state.PendingCardSession;
            var hasFacilitySpecialBuild = pending != null &&
                                          pending.IsValid() &&
                                          pending.PlayerId == context.LocalPlayerId &&
                                          string.Equals(
                                              pending.ScenarioId,
                                              FacilityPendingChoiceTypes.ScenarioId,
                                              StringComparison.Ordinal) &&
                                          (pending.ChoiceType ==
                                           FacilityPendingChoiceTypes.BuildAdditionalFacility ||
                                           pending.ChoiceType ==
                                           FacilityPendingChoiceTypes.BuildExtensionHub);
            var usesAdditionalBuild = hasFacilitySpecialBuild &&
                                      pending.ChoiceType ==
                                      FacilityPendingChoiceTypes.BuildAdditionalFacility;
            if (hasFacilitySpecialBuild)
            {
                if (usesAdditionalBuild)
                {
                    for (var i = 0; i < pending.OptionIds.Count; i++)
                    {
                        if (state.Decks.FacilitySupply.Contains(pending.OptionIds[i]))
                        {
                            draggableIds.Add(pending.OptionIds[i]);
                        }
                    }
                }

                AddEmptyCityBoardSlots(state, context.LocalPlayerId, legalSlotIndexes);
                return new BuildFacilityAvailabilityViewModel(
                    draggableIds.AsReadOnly(),
                    legalSlotIndexes.AsReadOnly(),
                    true,
                    string.Empty);
            }

            var isActionPhase = state.Phase == GamePhase.ActionRound1 ||
                                state.Phase == GamePhase.ActionRound2;
            var canUseMainBuild = isActionPhase &&
                                  state.CurrentPlayerId == context.LocalPlayerId &&
                                  !state.HasPendingChoice() &&
                                  MainActionBudgetService.HasAvailableMainAction(
                                      state,
                                      context.LocalPlayerId);
            if (canUseMainBuild)
            {
                var options = selection.QueryOptions(state, context.LocalPlayerId);
                for (var i = 0; i < options.Count; i++)
                {
                    if (options[i].CanBuild)
                    {
                        draggableIds.Add(options[i].FacilityId);
                    }
                }
            }

            var exhaustedMessage = isActionPhase &&
                                   state.CurrentPlayerId == context.LocalPlayerId &&
                                   !MainActionBudgetService.HasAvailableMainAction(
                                       state,
                                       context.LocalPlayerId) &&
                                   !hasFacilitySpecialBuild
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

            if (state.Decks.FacilitySupply.Count == 0)
            {
                view.ShowPrompt("设施供应区为空。");
                return false;
            }

            selection.Begin(context.LocalPlayerId);
            flowCoordinator.SetMode(InteractionMode.Busy);
            view.ClearHighlights();
            view.RefreshActionPanel();
            return true;
        }

        private void CancelCore(bool showUserPrompt)
        {
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

            for (var i = 0; i < state.Map.Facilities.Count; i++)
            {
                var facility = state.Map.Facilities[i];
                if (facility != null &&
                    facility.PlayerId == selection.PlayerId &&
                    facility.CityBoardSlotIndex == selection.CityBoardSlotIndex &&
                    string.Equals(
                        facility.FacilityCardId,
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
