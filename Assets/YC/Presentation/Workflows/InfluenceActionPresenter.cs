using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public sealed class InfluenceActionPresenter : IInteractionWorkflow
    {
        private const string SecondSourceParameter = "source2";
        private const string SecondTargetParameter = "target2";

        private readonly IGameplayContext context;
        private readonly IGameCommandPort commandPort;
        private readonly IInfluenceActionView view;
        private readonly IMapQueryService mapQuery;
        private readonly InfluenceService influenceService;

        private string pendingConfirmationAction = string.Empty;
        private string pendingConfirmationSlotId = string.Empty;
        private string dispatchSourceSlotId = string.Empty;
        private string firstSourceSlotId = string.Empty;
        private string firstTargetSlotId = string.Empty;
        private InteractionMode currentMode = InteractionMode.ChooseAction;

        public InfluenceActionPresenter(
            IGameplayContext context,
            IGameCommandPort commandPort,
            IInfluenceActionView view,
            IMapQueryService mapQuery,
            InfluenceService influenceService)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
        }

        public InteractionMode Mode
        {
            get { return currentMode; }
        }

        public string DispatchSourceSlotId
        {
            get { return dispatchSourceSlotId; }
        }

        public string FirstSourceSlotId
        {
            get { return firstSourceSlotId; }
        }

        public string FirstTargetSlotId
        {
            get { return firstTargetSlotId; }
        }

        public bool HasPendingFirstMove
        {
            get
            {
                return !string.IsNullOrEmpty(firstSourceSlotId) &&
                       !string.IsNullOrEmpty(firstTargetSlotId);
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
            ClearState();
            SetMode(InteractionMode.ResolvingDeployTarget);
            PresentDeployTargets();
            view.ShowPrompt("选择一个影响力空格放置影响力。");
        }

        public void BeginDispatch()
        {
            if (HasPendingFirstMove)
            {
                SetMode(InteractionMode.ResolvingDispatchDecision);
                ShowDispatchDecision();
                return;
            }

            ClearState();
            SetMode(InteractionMode.ResolvingDispatchSource);
            PresentDispatchSources();
            view.ShowPrompt("调度：先选择一个自己的影响力。");
        }

        public void Cancel()
        {
            Clear();
            currentMode = InteractionMode.ChooseAction;
            view.SetInteractionMode(currentMode);
        }

        public void Clear()
        {
            ClearState();
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
            switch (currentMode)
            {
                case InteractionMode.ResolvingDeployTarget:
                    PresentDeployTargets();
                    view.ShowPrompt("选择一个影响力空格放置影响力。");
                    break;
                case InteractionMode.ResolvingDispatchSource:
                    PresentDispatchSources();
                    view.ShowPrompt(HasPendingFirstMove
                        ? "调度：请选择第二个影响力。"
                        : "调度：先选择一个自己的影响力。");
                    break;
                case InteractionMode.ResolvingDispatchTarget:
                    PresentDispatchTargets(dispatchSourceSlotId);
                    view.ShowPrompt("请选择调度目标槽位。");
                    break;
                case InteractionMode.ResolvingDispatchDecision:
                    view.ClearHighlights();
                    RefreshPreview();
                    ShowDispatchDecision();
                    break;
            }
        }

        public void SelectLocation(string locationId)
        {
            switch (currentMode)
            {
                case InteractionMode.ResolvingDeployTarget:
                    SelectDeploySlot(FindFirstLocationSlot(locationId, CanDeployTo));
                    break;
                case InteractionMode.ResolvingDispatchSource:
                    SelectDispatchSourceSlot(FindFirstSourceAtLocation(locationId));
                    break;
                case InteractionMode.ResolvingDispatchTarget:
                    SelectDispatchTargetSlot(FindFirstLocationSlot(locationId, CanDispatchTo));
                    break;
            }
        }

        public void SelectSlot(string slotId)
        {
            switch (currentMode)
            {
                case InteractionMode.ResolvingDeployTarget:
                    SelectDeploySlot(slotId);
                    break;
                case InteractionMode.ResolvingDispatchSource:
                    SelectDispatchSourceSlot(slotId);
                    break;
                case InteractionMode.ResolvingDispatchTarget:
                    SelectDispatchTargetSlot(slotId);
                    break;
            }
        }

        public void ContinueDispatch()
        {
            if (!HasPendingFirstMove)
            {
                view.HideDispatchDecision();
                return;
            }

            view.HideDispatchDecision();
            ClearConfirmation();
            dispatchSourceSlotId = string.Empty;
            SetMode(InteractionMode.ResolvingDispatchSource);
            PresentDispatchSources();
            view.ShowPrompt("调度：请选择第二个影响力。");
        }

        public void FinishDispatch()
        {
            if (!HasPendingFirstMove)
            {
                view.HideDispatchDecision();
                return;
            }

            SubmitDispatch(string.Empty, string.Empty);
        }

        private void SelectDeploySlot(string slotId)
        {
            if (string.IsNullOrEmpty(slotId) || !CanDeployTo(slotId))
            {
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
            var submission = commandPort.Submit(new GameCommand
            {
                Kind = GameCommandKind.DeployInfluence,
                PlayerId = context.LocalPlayerId,
                TargetId = slotId
            });

            if (!TryHandleSubmission(submission, "部署命令已发送给主机，等待确认。"))
            {
                return;
            }

            Complete("部署");
        }

        private void SelectDispatchSourceSlot(string slotId)
        {
            ClearConfirmation();
            var state = context.CurrentState;
            var placement = state == null ? null : influenceService.FindInfluence(state, slotId);
            if (placement == null ||
                placement.PlayerId != context.LocalPlayerId ||
                placement.SlotId == firstSourceSlotId)
            {
                view.ShowPrompt("请选择一个自己的影响力作为调度来源。");
                return;
            }

            if (!CanMoveFrom(placement.SlotId))
            {
                view.ShowPrompt("该影响力当前没有可调度的目标。");
                return;
            }

            dispatchSourceSlotId = placement.SlotId;
            SetMode(InteractionMode.ResolvingDispatchTarget);
            PresentDispatchTargets(dispatchSourceSlotId);
            view.ShowPrompt("请选择调度目标槽位。");
        }

        private void SelectDispatchTargetSlot(string targetSlotId)
        {
            if (string.IsNullOrEmpty(targetSlotId) || !CanDispatchTo(targetSlotId))
            {
                view.ShowPrompt("该位置没有可调度进入的影响力空格。");
                return;
            }

            if (!ConfirmOnSecondSelection(
                    "DispatchInfluence",
                    targetSlotId,
                    "确认移动：再次点击目标槽位。",
                    WorkflowHighlightSemantic.DispatchTarget))
            {
                return;
            }

            if (HasPendingFirstMove)
            {
                SubmitDispatch(dispatchSourceSlotId, targetSlotId);
                return;
            }

            firstSourceSlotId = dispatchSourceSlotId;
            firstTargetSlotId = targetSlotId;
            dispatchSourceSlotId = string.Empty;
            SetMode(InteractionMode.ResolvingDispatchDecision);
            view.ClearHighlights();
            RefreshPreview();
            ShowDispatchDecision();
            view.ShowPrompt("已预览本次调度。请选择再次调度，或取消以结束调度。");
        }

        private void SubmitDispatch(string secondSourceSlotId, string secondTargetSlotId)
        {
            view.HideDispatchDecision();
            var command = new GameCommand
            {
                Kind = GameCommandKind.DispatchInfluence,
                PlayerId = context.LocalPlayerId,
                SourceId = firstSourceSlotId,
                TargetId = firstTargetSlotId
            };
            if (!string.IsNullOrEmpty(secondSourceSlotId) && !string.IsNullOrEmpty(secondTargetSlotId))
            {
                command.Parameters[SecondSourceParameter] = secondSourceSlotId;
                command.Parameters[SecondTargetParameter] = secondTargetSlotId;
            }

            var submission = commandPort.Submit(command);
            if (!TryHandleSubmission(submission, "调度命令已发送给主机，等待确认。"))
            {
                return;
            }

            Complete("调度");
        }

        private bool TryHandleSubmission(WorkflowSubmissionResult submission, string waitingPrompt)
        {
            if (submission == null || submission.CommandResult == null)
            {
                view.ShowPrompt("命令未返回结果。");
                return false;
            }

            if (!submission.CommandResult.Succeeded)
            {
                view.ShowPrompt(submission.CommandResult.Validation.Reason);
                return false;
            }

            if (!submission.AppliedLocally)
            {
                view.ShowPrompt(waitingPrompt);
                return false;
            }

            return true;
        }

        private void Complete(string actionName)
        {
            ClearState();
            currentMode = InteractionMode.ChooseAction;
            view.SetInteractionMode(currentMode);
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
            var highlights = new List<WorkflowHighlight>();
            AddSlotHighlight(highlights, slotId, semantic, true);
            view.SetHighlights(highlights);
            RefreshPreview();
            view.ShowPrompt(prompt);
            return false;
        }

        private void PresentDeployTargets()
        {
            var highlights = new List<WorkflowHighlight>();
            VisitAllSlots(slotId =>
            {
                if (CanDeployTo(slotId))
                {
                    AddSlotHighlight(highlights, slotId, WorkflowHighlightSemantic.DeployTarget, true);
                }
            });
            view.SetHighlights(highlights);
            RefreshPreview();
            view.RefreshActionPanel();
        }

        private void PresentDispatchSources()
        {
            var highlights = new List<WorkflowHighlight>();
            var state = context.CurrentState;
            if (state != null && state.Map != null)
            {
                for (var i = 0; i < state.Map.Influences.Count; i++)
                {
                    var placement = state.Map.Influences[i];
                    if (placement.PlayerId == context.LocalPlayerId &&
                        placement.SlotId != firstSourceSlotId &&
                        CanMoveFrom(placement.SlotId))
                    {
                        AddSlotHighlight(
                            highlights,
                            placement.SlotId,
                            WorkflowHighlightSemantic.DispatchSource,
                            true);
                    }
                }
            }

            view.SetHighlights(highlights);
            RefreshPreview();
            view.RefreshActionPanel();
        }

        private void PresentDispatchTargets(string sourceSlotId)
        {
            var highlights = new List<WorkflowHighlight>();
            if (!string.IsNullOrEmpty(sourceSlotId))
            {
                VisitAllSlots(slotId =>
                {
                    if (CanMoveFromTo(sourceSlotId, slotId))
                    {
                        AddSlotHighlight(
                            highlights,
                            slotId,
                            WorkflowHighlightSemantic.DispatchTarget,
                            true);
                    }
                });
            }

            view.SetHighlights(highlights);
            RefreshPreview();
            view.RefreshActionPanel();
        }

        private void ShowDispatchDecision()
        {
            view.ShowDispatchDecision(new DispatchDecisionViewModel(
                "调度已移动到目标槽位",
                "可以再调度一个影响力，或取消并结束本次调度。",
                "再次调度",
                "取消",
                ContinueDispatch,
                FinishDispatch));
            RefreshPreview();
            view.RefreshActionPanel();
        }

        private bool CanDeployTo(string slotId)
        {
            var state = context.CurrentState;
            return state != null &&
                   influenceService.CanPlace(state, context.LocalPlayerId, slotId).IsValid;
        }

        private bool CanDispatchTo(string slotId)
        {
            return CanMoveFromTo(dispatchSourceSlotId, slotId);
        }

        private bool CanMoveFrom(string sourceSlotId)
        {
            if (string.IsNullOrEmpty(sourceSlotId) || sourceSlotId == firstSourceSlotId)
            {
                return false;
            }

            var found = false;
            VisitAllSlots(slotId =>
            {
                if (!found && CanMoveFromTo(sourceSlotId, slotId))
                {
                    found = true;
                }
            });
            return found;
        }

        private bool CanMoveFromTo(string sourceSlotId, string targetSlotId)
        {
            var state = context.CurrentState;
            if (state == null || string.IsNullOrEmpty(sourceSlotId) || string.IsNullOrEmpty(targetSlotId))
            {
                return false;
            }

            if (!HasPendingFirstMove)
            {
                return influenceService.CanMove(
                    state,
                    context.LocalPlayerId,
                    sourceSlotId,
                    targetSlotId).IsValid;
            }

            return influenceService.CanMoveAtomically(
                state,
                context.LocalPlayerId,
                new List<InfluenceMoveRequest>
                {
                    new InfluenceMoveRequest(firstSourceSlotId, firstTargetSlotId),
                    new InfluenceMoveRequest(sourceSlotId, targetSlotId)
                }).IsValid;
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

        private string FindFirstSourceAtLocation(string locationId)
        {
            var state = context.CurrentState;
            if (state == null || state.Map == null)
            {
                return string.Empty;
            }

            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var placement = state.Map.Influences[i];
                if (placement.PlayerId == context.LocalPlayerId &&
                    placement.LocationId == locationId &&
                    placement.SlotId != firstSourceSlotId &&
                    CanMoveFrom(placement.SlotId))
                {
                    return placement.SlotId;
                }
            }

            return string.Empty;
        }

        private void VisitAllSlots(Action<string> visitor)
        {
            for (var locationIndex = 0; locationIndex < mapQuery.Map.Locations.Count; locationIndex++)
            {
                var location = mapQuery.Map.Locations[locationIndex];
                for (var slotIndex = 0; slotIndex < location.InfluenceSlotCount; slotIndex++)
                {
                    visitor(InfluenceService.GetLocationSlotId(location.LocationId, slotIndex));
                }
            }

            for (var routeIndex = 0; routeIndex < mapQuery.Map.Routes.Count; routeIndex++)
            {
                var route = mapQuery.Map.Routes[routeIndex];
                for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    visitor(InfluenceService.GetRouteSlotId(route.RouteId, slotIndex));
                }
            }
        }

        private static void AddSlotHighlight(
            ICollection<WorkflowHighlight> highlights,
            string slotId,
            WorkflowHighlightSemantic semantic,
            bool includeLocation)
        {
            highlights.Add(new WorkflowHighlight(
                WorkflowHighlightTargetKind.InfluenceSlot,
                slotId,
                semantic));

            if (!includeLocation ||
                !slotId.StartsWith(InfluenceSlotReference.LocationPrefix, StringComparison.Ordinal))
            {
                return;
            }

            var rest = slotId.Substring(InfluenceSlotReference.LocationPrefix.Length);
            var separator = rest.LastIndexOf(':');
            var locationId = separator < 0 ? rest : rest.Substring(0, separator);
            highlights.Add(new WorkflowHighlight(
                WorkflowHighlightTargetKind.Location,
                locationId,
                semantic));
        }

        private PlayerState GetPlayer()
        {
            var state = context.CurrentState;
            return state == null ? null : state.FindPlayer(context.LocalPlayerId);
        }

        private void SetMode(InteractionMode mode)
        {
            currentMode = mode;
            view.SetInteractionMode(mode);
        }

        private void RefreshPreview()
        {
            view.RefreshInfluencePreview(HasPendingFirstMove, firstSourceSlotId, firstTargetSlotId);
        }

        private void ClearConfirmation()
        {
            pendingConfirmationAction = string.Empty;
            pendingConfirmationSlotId = string.Empty;
        }

        private void ClearState()
        {
            ClearConfirmation();
            dispatchSourceSlotId = string.Empty;
            firstSourceSlotId = string.Empty;
            firstTargetSlotId = string.Empty;
            view.HideDispatchDecision();
            view.ClearHighlights();
            RefreshPreview();
        }
    }
}
