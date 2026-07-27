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
        private enum InfluenceActionStage
        {
            Inactive,
            SelectingDeployTarget,
            SelectingDispatchSource,
            SelectingDispatchTarget,
            ChoosingDispatchContinuation
        }

        private const string SecondSourceParameter = "source2";
        private const string SecondTargetParameter = "target2";

        private readonly IGameplayContext context;
        private readonly CommandGateway commandGateway;
        private readonly IInfluenceActionView view;
        private readonly IMapQueryService mapQuery;
        private readonly InfluenceService influenceService;

        private string pendingConfirmationAction = string.Empty;
        private string pendingConfirmationSlotId = string.Empty;
        private string dispatchSourceSlotId = string.Empty;
        private string firstSourceSlotId = string.Empty;
        private string firstTargetSlotId = string.Empty;
        private InfluenceActionStage stage = InfluenceActionStage.Inactive;

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

        public bool IsActive
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
            get { return stage == InfluenceActionStage.ChoosingDispatchContinuation; }
        }

        public string CurrentPrompt
        {
            get
            {
                switch (stage)
                {
                    case InfluenceActionStage.SelectingDeployTarget:
                        return "选择一个影响力空格放置影响力。";
                    case InfluenceActionStage.SelectingDispatchSource:
                        return HasPendingFirstMove
                            ? "调度：请选择第二个影响力。"
                            : "调度：先选择一个自己的影响力。";
                    case InfluenceActionStage.SelectingDispatchTarget:
                        return "请选择调度目标槽位。";
                    case InfluenceActionStage.ChoosingDispatchContinuation:
                        return "已预览本次调度。请选择再次调度，或取消以结束调度。";
                    default:
                        return string.Empty;
                }
            }
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
            SetStage(InfluenceActionStage.SelectingDeployTarget);
            PresentDeployTargets();
            view.ShowPrompt(CurrentPrompt);
        }

        public void BeginDispatch()
        {
            if (HasPendingFirstMove)
            {
                SetStage(InfluenceActionStage.ChoosingDispatchContinuation);
                ShowDispatchDecision();
                return;
            }

            ClearState();
            SetStage(InfluenceActionStage.SelectingDispatchSource);
            PresentDispatchSources();
            view.ShowPrompt(CurrentPrompt);
        }

        public void Cancel()
        {
            Clear();
        }

        public void Clear()
        {
            ClearState();
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
            switch (stage)
            {
                case InfluenceActionStage.SelectingDeployTarget:
                    PresentDeployTargets();
                    view.ShowPrompt(CurrentPrompt);
                    break;
                case InfluenceActionStage.SelectingDispatchSource:
                    PresentDispatchSources();
                    view.ShowPrompt(CurrentPrompt);
                    break;
                case InfluenceActionStage.SelectingDispatchTarget:
                    PresentDispatchTargets(dispatchSourceSlotId);
                    view.ShowPrompt(CurrentPrompt);
                    break;
                case InfluenceActionStage.ChoosingDispatchContinuation:
                    view.ClearHighlights();
                    RefreshPreview();
                    ShowDispatchDecision();
                    break;
            }
        }

        public void SelectLocation(string locationId)
        {
            switch (stage)
            {
                case InfluenceActionStage.SelectingDeployTarget:
                    SelectDeploySlot(FindFirstLocationSlot(locationId, CanDeployTo));
                    break;
                case InfluenceActionStage.SelectingDispatchSource:
                    SelectDispatchSourceSlot(FindFirstSourceAtLocation(locationId));
                    break;
                case InfluenceActionStage.SelectingDispatchTarget:
                    SelectDispatchTargetSlot(FindFirstLocationSlot(locationId, CanDispatchTo));
                    break;
            }
        }

        public void SelectSlot(string slotId)
        {
            switch (stage)
            {
                case InfluenceActionStage.SelectingDeployTarget:
                    SelectDeploySlot(slotId);
                    break;
                case InfluenceActionStage.SelectingDispatchSource:
                    SelectDispatchSourceSlot(slotId);
                    break;
                case InfluenceActionStage.SelectingDispatchTarget:
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
            SetStage(InfluenceActionStage.SelectingDispatchSource);
            PresentDispatchSources();
            view.ShowPrompt(CurrentPrompt);
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
            commandGateway.Submit(
                new GameCommand
                {
                    Kind = GameCommandKind.DeployInfluence,
                    PlayerId = context.LocalPlayerId,
                    TargetId = slotId
                },
                new SubmitCallbacks(
                    view.ShowPrompt,
                    CommandGateway.BuildWaitingForHostPrompt("部署命令"))
                {
                    OnAppliedLocally = result => Complete("部署")
                });
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
            SetStage(InfluenceActionStage.SelectingDispatchTarget);
            PresentDispatchTargets(dispatchSourceSlotId);
            view.ShowPrompt(CurrentPrompt);
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
            SetStage(InfluenceActionStage.ChoosingDispatchContinuation);
            view.ClearHighlights();
            RefreshPreview();
            ShowDispatchDecision();
            view.ShowPrompt(CurrentPrompt);
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

            commandGateway.Submit(
                command,
                new SubmitCallbacks(
                    view.ShowPrompt,
                    CommandGateway.BuildWaitingForHostPrompt("调度命令"))
                {
                    OnAppliedLocally = result => Complete("调度")
                });
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

        private void SetStage(InfluenceActionStage value)
        {
            stage = value;
            view.SetInteractionMode(Mode);
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
