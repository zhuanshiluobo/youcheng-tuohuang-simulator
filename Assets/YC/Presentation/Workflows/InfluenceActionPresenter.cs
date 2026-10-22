using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public sealed class InfluenceActionPresenter : InteractionBase, IInteractionWorkflow
    {
        private enum InfluenceActionStage
        {
            Inactive,
            SelectingDeployTarget,
            SelectingDispatchSource,
            SelectingDispatchTarget
        }

        private readonly IGameplayContext context;
        private readonly CommandGateway commandGateway;
        private readonly IInfluenceActionView view;
        private readonly IMapQueryService mapQuery;
        private readonly InfluenceService influenceService;

        private string pendingConfirmationAction = string.Empty;
        private string pendingConfirmationSlotId = string.Empty;



        private InfluenceActionStage stage = InfluenceActionStage.Inactive;
        private DeployDraft deployDraft;
        private DeployDraft submittedDeployDraft;
        private string deployCommandId = string.Empty;
        private string dispatchCommandId = string.Empty;
        private DeployDraft dispatchDraft;
        private DeployDraft submittedDispatchDraft;
        public Func<int> ObservedStateRevision { private get; set; }
        public string SourceInfluenceId { get; private set; } = string.Empty;
        public string SourceSlotId { get; private set; } = string.Empty;
        public string TargetSlotId { get; private set; } = string.Empty;
        public bool HasUnsubmittedMainActionDraft => (IsSelectingDeployTarget || IsSelectingDispatchSource || IsSelectingDispatchTarget) &&
            !IsDeploySubmissionInFlight && !IsDispatchSubmissionInFlight;
        public bool IsDispatchSubmissionInFlight => !string.IsNullOrEmpty(dispatchCommandId) && IsDeploySessionCurrent(submittedDispatchDraft);

        private sealed class DeployDraft
        {
            public GameState State;
            public string GameId;
            public int PlayerId;
            public int Round;
            public int ActionRound;
            public GamePhase Phase;
            public int Revision;
        }

        public bool IsDeploySubmissionInFlight =>
            !string.IsNullOrEmpty(deployCommandId) && IsDeploySessionCurrent(submittedDeployDraft);

        public bool CanResumeDeployDraft => IsSelectingDeployTarget &&
            IsDeployDraftCurrent(deployDraft) && CanSpendDeployAction();
        public bool CanResumeDispatchDraft => (IsSelectingDispatchSource || IsSelectingDispatchTarget) &&
            IsDeployDraftCurrent(dispatchDraft) && CanSpendDeployAction();

        public InfluenceActionPresenter(
            IGameplayContext context,
            IGameCommandPort commandPort,
            IInfluenceActionView view,
            IMapQueryService mapQuery,
            InfluenceService influenceService)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            commandGateway = new CommandGateway(
                commandPort ?? throw new ArgumentNullException(nameof(commandPort)));
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
        }

        public InteractionMode Mode
        {
            get
            {
                return stage == InfluenceActionStage.Inactive
                    ? InteractionMode.ChooseAction
                    : InteractionMode.Busy;
            }
        }

        public override bool IsActive
        {
            get { return stage != InfluenceActionStage.Inactive; }
        }

        public bool IsSelectingDeployTarget
        {
            get { return stage == InfluenceActionStage.SelectingDeployTarget; }
        }

        public bool IsSelectingDispatchSource
        {
            get { return stage == InfluenceActionStage.SelectingDispatchSource; }
        }

        public bool IsSelectingDispatchTarget
        {
            get { return stage == InfluenceActionStage.SelectingDispatchTarget; }
        }

        public bool IsChoosingDispatchContinuation
        {
            get { return false; }
        }

        public string CurrentPrompt
        {
            get
            {
                switch (stage)
                {
                    case InfluenceActionStage.SelectingDeployTarget:
                        return IsDeploySubmissionInFlight
                            ? CommandGateway.BuildWaitingForHostPrompt("部署命令")
                            : "选择一个影响力空格放置影响力。";
                    default:
                        return string.Empty;
                }
            }
        }

        public string DispatchSourceSlotId
        {
            get { return SourceSlotId; }
        }

        public string FirstSourceSlotId
        {
            get { return SourceSlotId; }
        }

        public string FirstTargetSlotId
        {
            get { return TargetSlotId; }
        }

        public bool HasPendingFirstMove
        {
            get
            {
                return false;
            }
        }

        public bool HasPendingConfirmation
        {
            get { return !string.IsNullOrEmpty(pendingConfirmationSlotId); }
        }

        public void Activate()
        {
            BeginDeploy();
        }

        public void BeginDeploy()
        {
            if (IsDeploySubmissionInFlight || IsDispatchSubmissionInFlight) return;
            ClearDeploySubmission();
            ClearState();
            var state = context.CurrentState;
            if (state == null) { SetStage(InfluenceActionStage.Inactive); return; }
            deployDraft = new DeployDraft
            {
                State = state, GameId = state.GameId, PlayerId = context.LocalPlayerId,
                Round = state.Round, ActionRound = state.ActionRound, Phase = state.Phase, Revision = CurrentRevision()
            };
            SetStage(InfluenceActionStage.SelectingDeployTarget);
            PresentDeployTargets();
            view.ShowPrompt(CurrentPrompt);
        }

        public void BeginDispatch()
        {
            if (IsDeploySubmissionInFlight || IsDispatchSubmissionInFlight || !CanSpendDeployAction()) return;
            ClearState();
            var state = context.CurrentState;
            dispatchDraft = new DeployDraft { State = state, GameId = state.GameId, PlayerId = context.LocalPlayerId,
                Round = state.Round, ActionRound = state.ActionRound, Phase = state.Phase, Revision = CurrentRevision() };
            SetStage(InfluenceActionStage.SelectingDispatchSource);
            PresentDispatchTargets();
        }

        public override void Cancel()
        {
            if (IsDeploySubmissionInFlight || IsDispatchSubmissionInFlight) return;
            Clear();
        }

        public void Clear()
        {
            if (IsDeploySubmissionInFlight || IsDispatchSubmissionInFlight) return;
            var ownedPresentation = IsActive;
            ClearState(ownedPresentation);
            if (!ownedPresentation) return;
            SetStage(InfluenceActionStage.Inactive);
        }

        public void CancelPendingConfirmation(bool restorePresentation)
        {
            if (string.IsNullOrEmpty(pendingConfirmationSlotId))
            {
                return;
            }

            ClearConfirmation();
            if (restorePresentation)
            {
                RestorePresentation();
            }
        }

        public void RestorePresentation()
        {
            if (IsSelectingDispatchSource || IsSelectingDispatchTarget) { PresentDispatchTargets(); return; }
            switch (stage)
            {
                case InfluenceActionStage.SelectingDeployTarget:
                    PresentDeployTargets();
                    view.ShowPrompt(CurrentPrompt);
                    break;
            }
        }

        public void SelectLocation(string locationId)
        {
            if (IsSelectingDispatchSource || IsSelectingDispatchTarget)
            { SelectDispatchSlot(FindFirstLocationSlot(locationId, slot => IsSelectingDispatchSource ? FindDispatchSource(slot) != null : CanDispatchTo(slot))); return; }
            if (IsSelectingDeployTarget && !CanInteractWithDeploy()) return;
            switch (stage)
            {
                case InfluenceActionStage.SelectingDeployTarget:
                    SelectDeploySlot(FindFirstLocationSlot(locationId, CanDeployTo));
                    break;
            }
        }

        public void SelectSlot(string slotId)
        {
            if (IsSelectingDispatchSource || IsSelectingDispatchTarget) { SelectDispatchSlot(slotId); return; }
            switch (stage)
            {
                case InfluenceActionStage.SelectingDeployTarget:
                    SelectDeploySlot(slotId);
                    break;
            }
        }

        public void ContinueDispatch() { view.HideDispatchDecision(); }

        public void FinishDispatch() { view.HideDispatchDecision(); }

        private void SelectDeploySlot(string slotId)
        {
            if (!CanInteractWithDeploy()) return;
            if (string.IsNullOrEmpty(slotId) || !CanDeployTo(slotId))
            {
                ClearConfirmation();
                PresentDeployTargets();
                view.ShowPrompt("该位置没有可部署的影响力空格。");
                return;
            }

            var player = GetPlayer();
            var remaining = player == null ? 0 : Math.Max(0, player.InfluenceSupply - 1);
            if (!ConfirmOnSecondSelection(
                    "DeployInfluence",
                    slotId,
                    "再次点击确认放置   影响力剩余：" + remaining,
                    WorkflowHighlightSemantic.DeployTarget))
            {
                return;
            }

            SubmitDeploy(slotId);
        }

        private void SubmitDeploy(string slotId)
        {
            var draft = deployDraft;
            var command = new GameCommand
            {
                Kind = GameCommandKind.DeployInfluence,
                PlayerId = draft.PlayerId,
                TargetId = slotId
            };
            // 先上锁，覆盖Submit中同步回调再次点击的情况；等待Host时保留至对应回执。
            submittedDeployDraft = draft;
            deployCommandId = command.CommandId;
            try
            {
                commandGateway.Submit(command, new SubmitCallbacks(
                    message => { if (ReferenceEquals(deployDraft, draft) && IsDeployDraftCurrent(draft)) view.ShowPrompt(message); },
                    CommandGateway.BuildWaitingForHostPrompt("部署命令"))
                {
                    BeforeRejectedPrompt = _ => NotifyDeployCommandSettled(command.CommandId),
                    OnAppliedLocally = result => CompleteAppliedSubmission(draft.GameId, command.CommandId)
                });
            }
            catch
            {
                NotifyDeployCommandSettled(command.CommandId);
                throw;
            }
        }

        public void NotifyDeployCommandSettled(string commandId)
        {
            if (string.IsNullOrEmpty(deployCommandId) || commandId != deployCommandId) return;
            var submitted = submittedDeployDraft;
            ClearDeploySubmission();
            // 切换行动后只解除旧命令门禁，不复活旧草稿或覆盖新行动的高亮。
            if (!ReferenceEquals(deployDraft, submitted) || !IsSelectingDeployTarget) return;
            if (!CanResumeDeployDraft) { Clear(); return; }
            ClearConfirmation();
            PresentDeployTargets();
        }

        public void RefreshDeployDraft()
        {
            if (!CanResumeDeployDraft) return;
            ClearConfirmation();
            PresentDeployTargets();
        }

        private void ClearDeploySubmission()
        {
            deployCommandId = string.Empty;
            submittedDeployDraft = null;
        }

        private bool IsDeploySessionCurrent(DeployDraft draft)
        {
            var state = context.CurrentState;
            return draft != null && state != null && draft.GameId == state.GameId &&
                   draft.PlayerId == context.LocalPlayerId;
        }

        private bool IsDeployDraftCurrent(DeployDraft draft)
        {
            var state = context.CurrentState;
            return IsDeploySessionCurrent(draft) && ReferenceEquals(state, draft.State) &&
                   state.CurrentPlayerId == draft.PlayerId && state.Round == draft.Round &&
                   state.ActionRound == draft.ActionRound && state.Phase == draft.Phase && CurrentRevision() == draft.Revision;
        }
        private int CurrentRevision() => ObservedStateRevision?.Invoke() ?? context.CurrentState?.EffectRuntime?.StateRevision ?? 0;

        private bool CanSpendDeployAction()
        {
            var state = context.CurrentState;
            return state != null && (state.Phase == GamePhase.ActionRound1 || state.Phase == GamePhase.ActionRound2) &&
                   !state.HasPendingChoice() && new MainActionBudgetService().HasAvailableMainAction(state, context.LocalPlayerId);
        }

        private bool CanInteractWithDeploy()
        {
            if (!CanResumeDeployDraft) { Clear(); return false; }
            return !IsDeploySubmissionInFlight;
        }

        private void SubmitDispatch()
        {
            if (IsDispatchSubmissionInFlight) return;
            var draft = dispatchDraft;
            view.HideDispatchDecision();
            var command = new GameCommand
            {
                Kind = GameCommandKind.DispatchInfluence,
                PlayerId = draft.PlayerId,
                SourceId = SourceInfluenceId,
                TargetId = TargetSlotId
            };
            dispatchCommandId = command.CommandId;
            submittedDispatchDraft = draft;
            try
            {
            commandGateway.Submit(
                command,
                new SubmitCallbacks(
                    message => { if (ReferenceEquals(dispatchDraft, draft) && IsDeploySessionCurrent(draft)) view.ShowPrompt(message); },
                    CommandGateway.BuildWaitingForHostPrompt("调度命令"))
                {
                    BeforeRejectedPrompt = _ => NotifyDispatchCommandSettled(command.CommandId),
                    AfterRejectedPrompt = _ => view.RefreshActionPanel(),
                    OnAppliedLocally = _ => CompleteAppliedSubmission(draft.GameId, command.CommandId)
                });
            }
            catch { NotifyDispatchCommandSettled(command.CommandId); throw; }
        }

        public void NotifyDispatchCommandSettled(string commandId)
        {
            if (string.IsNullOrEmpty(dispatchCommandId) || commandId != dispatchCommandId) return;
            var submitted = submittedDispatchDraft;
            dispatchCommandId = string.Empty; submittedDispatchDraft = null;
            if (!ReferenceEquals(dispatchDraft, submitted)) return;
            TargetSlotId = string.Empty;
            if (!CanResumeDispatchDraft) { Clear(); return; }
            PresentDispatchTargets();
        }

        private string FindDispatchSource(string slot)
        {
            var state = context.CurrentState;
            return state?.Map?.Influences.FirstOrDefault(value => value.PlayerId == context.LocalPlayerId && value.SlotId == slot &&
                influenceService.GetLegalMovementSlotIds(state, value.InfluenceId).Any(target => influenceService.CanMove(state, context.LocalPlayerId, value.InfluenceId, target).IsValid))?.InfluenceId;
        }
        private bool CanDispatchTo(string slot) => !string.IsNullOrEmpty(SourceInfluenceId) &&
            influenceService.CanMove(context.CurrentState, context.LocalPlayerId, SourceInfluenceId, slot).IsValid;
        private void SelectDispatchSlot(string slot)
        {
            if (IsDispatchSubmissionInFlight) return;
            if (!CanResumeDispatchDraft) { Clear(); return; }
            if (IsSelectingDispatchSource)
            {
                var source = FindDispatchSource(slot); if (source == null) return;
                SourceInfluenceId = source; SourceSlotId = slot;
                SetStage(InfluenceActionStage.SelectingDispatchTarget); PresentDispatchTargets(); return;
            }
            if (!CanDispatchTo(slot)) return;
            TargetSlotId = slot; SubmitDispatch();
        }
        private IReadOnlyList<WorkflowHighlight> DispatchHighlights()
        {
            var result = new List<WorkflowHighlight>();
            if (!IsDeployDraftCurrent(dispatchDraft) || IsDispatchSubmissionInFlight) return result;
            if (IsSelectingDispatchSource)
            {
                foreach (var source in context.CurrentState.Map.Influences.Where(value => value.PlayerId == context.LocalPlayerId))
                    if (FindDispatchSource(source.SlotId) != null) AddSlotHighlight(result, source.SlotId, WorkflowHighlightSemantic.DispatchSource, true);
            }
            else if (IsSelectingDispatchTarget)
                foreach (var slot in influenceService.GetLegalMovementSlotIds(context.CurrentState, SourceInfluenceId))
                    if (CanDispatchTo(slot)) AddSlotHighlight(result, slot, WorkflowHighlightSemantic.DispatchTarget, true);
            return result;
        }
        private void PresentDispatchTargets()
        {
            view.SetHighlights(DispatchHighlights()); view.RefreshInfluencePreview(!string.IsNullOrEmpty(SourceInfluenceId), SourceSlotId, TargetSlotId);
            view.RefreshActionPanel();
        }
        public override string Id => "active.dispatch";
        public override InteractionPriority Priority => InteractionPriority.ActiveAction;
        public override InteractionResult OnLocationClicked(string id)
        { if (!IsSelectingDispatchSource && !IsSelectingDispatchTarget) return InteractionResult.Passthrough; SelectLocation(id); return InteractionResult.Consumed; }
        public override InteractionResult OnInfluenceSlotClicked(string id)
        { if (!IsSelectingDispatchSource && !IsSelectingDispatchTarget) return InteractionResult.Passthrough; SelectSlot(id); return InteractionResult.Consumed; }
        public override InteractionResult OnMobileCityClicked() => InteractionResult.Passthrough;
        public override InteractionResult OnEscape() => IsDispatchSubmissionInFlight ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionPresentation BuildPresentation() => IsDispatchSubmissionInFlight
            ? new InteractionPresentation(null, CommandGateway.BuildWaitingForHostPrompt("调度命令"), InteractionMode.Busy)
            : IsSelectingDispatchSource || IsSelectingDispatchTarget ? new InteractionPresentation(DispatchHighlights(), string.Empty, InteractionMode.Busy) : InteractionPresentation.Empty;
        public override void NotifyCommandSettled(string commandId) { NotifyDispatchCommandSettled(commandId); NotifyDeployCommandSettled(commandId); }
        public override void NotifyInteractionCommandResolved(string gameId, string commandId, bool accepted, CommandErrorCode errorCode)
        {
            var submitted = commandId == deployCommandId ? submittedDeployDraft : commandId == dispatchCommandId ? submittedDispatchDraft : null;
            if (submitted == null || submitted.GameId != gameId || !IsDeploySessionCurrent(submitted)) return;
            if (!accepted) { NotifyCommandSettled(commandId); return; }
            CompleteAppliedSubmission(gameId, commandId);
        }

        private void CompleteAppliedSubmission(string gameId, string commandId)
        {
            if (string.IsNullOrEmpty(commandId)) return;
            bool deploy = commandId == deployCommandId;
            var submitted = deploy ? submittedDeployDraft : commandId == dispatchCommandId ? submittedDispatchDraft : null;
            if (submitted == null || submitted.GameId != gameId) return;
            if (deploy) ClearDeploySubmission();
            else { dispatchCommandId = string.Empty; submittedDispatchDraft = null; }
            // 正式成功必然可能推进 revision；核原提交归属，不拿旧草稿 revision 拦截成功收尾。
            // 两条成功通知共用入口，先释放准确命令，重复通知不再刷新，也不覆盖新草稿。
            if (!IsDeploySessionCurrent(submitted) ||
                !ReferenceEquals(deploy ? deployDraft : dispatchDraft, submitted)) return;
            if (deploy) Complete("部署");
            else { Clear(); view.RefreshActionPanel(); }
        }

        private void Complete(string actionName)
        {
            ClearState();
            SetStage(InfluenceActionStage.Inactive);
            view.CompleteAction(actionName);
        }

        private bool ConfirmOnSecondSelection(
            string action,
            string slotId,
            string prompt,
            WorkflowHighlightSemantic semantic)
        {
            if (pendingConfirmationAction == action && pendingConfirmationSlotId == slotId)
            {
                ClearConfirmation();
                view.ClearHighlights();
                return true;
            }

            pendingConfirmationAction = action;
            pendingConfirmationSlotId = slotId;
            RestorePresentation();
            RefreshPreview();
            view.ShowPrompt(prompt);
            return false;
        }

        private void PresentDeployTargets()
        {
            if (!CanInteractWithDeploy()) return;
            var highlights = new List<WorkflowHighlight>();
            foreach (var slotId in influenceService.GetLegalPlacementSlotIds(context.CurrentState, deployDraft.PlayerId))
                AddSlotHighlight(highlights, slotId, WorkflowHighlightSemantic.DeployTarget, true);
            view.SetHighlights(highlights);
            RefreshPreview();
            view.RefreshActionPanel();
        }

        private bool CanDeployTo(string slotId)
        {
            var state = context.CurrentState;
            return state != null &&
                   influenceService.CanPlace(state, context.LocalPlayerId, slotId).IsValid;
        }

        private string FindFirstLocationSlot(string locationId, Func<string, bool> predicate)
        {
            if (string.IsNullOrEmpty(locationId))
            {
                return string.Empty;
            }

            MapLocationDefinition location;
            try
            {
                location = mapQuery.GetLocation(locationId);
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }

            for (var i = 0; i < location.InfluenceSlotCount; i++)
            {
                var slotId = InfluenceService.GetLocationSlotId(locationId, i);
                if (predicate(slotId))
                {
                    return slotId;
                }
            }

            return string.Empty;
        }

        private void AddSlotHighlight(
            ICollection<WorkflowHighlight> highlights,
            string slotId,
            WorkflowHighlightSemantic semantic,
            bool includeLocation)
        {
            highlights.Add(new WorkflowHighlight(
                WorkflowHighlightTargetKind.InfluenceSlot,
                slotId,
                semantic,
                pendingConfirmationSlotId == slotId ? WorkflowHighlightState.PendingConfirmation : WorkflowHighlightState.Available));
        }

        private PlayerState GetPlayer()
        {
            var state = context.CurrentState;
            return state == null ? null : state.FindPlayer(context.LocalPlayerId);
        }

        private void SetStage(InfluenceActionStage value)
        {
            stage = value;
            view.SetInteractionMode(Mode);
        }

        private void RefreshPreview()
        {
            view.RefreshInfluencePreview(false, string.Empty, string.Empty);
        }

        private void ClearConfirmation()
        {
            pendingConfirmationAction = string.Empty;
            pendingConfirmationSlotId = string.Empty;
        }

        private void ClearState(bool clearPresentation = true)
        {
            deployDraft = null;
            dispatchDraft = null; SourceInfluenceId = SourceSlotId = TargetSlotId = string.Empty;
            ClearConfirmation();

            if (!clearPresentation) return;
            view.HideDispatchDecision();
            view.ClearHighlights();
            RefreshPreview();
        }
    }
}
