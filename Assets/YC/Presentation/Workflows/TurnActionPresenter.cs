using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public sealed class TurnActionPresenter : IInteractionWorkflow
    {
        private static readonly MainActionBudgetService MainActionBudgetService = new MainActionBudgetService();
        private readonly IWritableGameplayContext context;
        private readonly CommandGateway commandGateway;
        private readonly ITurnActionView view;
        private readonly InteractionFlowCoordinator flowCoordinator;
        private readonly ResourceCollectionPresenter resourceCollectionPresenter;
        private readonly InfluenceActionPresenter influenceActionPresenter;
        private readonly ExplorationEventPresenter explorationEventPresenter;
        private readonly LocalPlayerResolver localPlayerResolver = new LocalPlayerResolver();
        private string pendingMainActionCommandId = string.Empty;
        private string completedMainActionName = string.Empty;
        public TurnActionPresenter(
            IWritableGameplayContext context,
            IGameCommandPort commandPort,
            ITurnActionView view,
            InteractionFlowCoordinator flowCoordinator,
            IMapQueryService mapQuery,
            ResourceCollectionPresenter resourceCollectionPresenter,
            InfluenceActionPresenter influenceActionPresenter,
            ExplorationEventPresenter explorationEventPresenter)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            commandGateway = new CommandGateway(
                commandPort ?? throw new ArgumentNullException(nameof(commandPort)));
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.flowCoordinator = flowCoordinator ?? throw new ArgumentNullException(nameof(flowCoordinator));
            var validatedMapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.resourceCollectionPresenter = resourceCollectionPresenter ?? throw new ArgumentNullException(nameof(resourceCollectionPresenter));
            this.influenceActionPresenter = influenceActionPresenter ?? throw new ArgumentNullException(nameof(influenceActionPresenter));
            this.explorationEventPresenter = explorationEventPresenter ?? throw new ArgumentNullException(nameof(explorationEventPresenter));
            BuildInteraction = new BuildInteraction(
                this.context,
                commandGateway,
                this.view,
                this.flowCoordinator,
                CanStartMainAction,
                CompleteAction);
            MoveInteraction = new MoveInteraction(
                this.context,
                commandGateway,
                this.view,
                this.flowCoordinator,
                validatedMapQuery,
                () => this.flowCoordinator.Activate(this),
                CanStartMainAction,
                CompleteAction);
            ActionPanelPresenter = new TurnActionPanelPresenter(
                this.context,
                this.view,
                this.flowCoordinator,
                this.resourceCollectionPresenter,
                this.influenceActionPresenter,
                this.explorationEventPresenter,
                BuildInteraction,
                () => completedMainActionName,
                CanEndCurrentAction,
                () => this.flowCoordinator.IsActive(this) &&
                      MoveInteraction.IsSelectingMoveTarget,
                () => !string.IsNullOrEmpty(pendingMainActionCommandId));
            CityStyleInteraction = new CityStyleInteraction(
                this.context,
                commandGateway,
                this.view,
                this.flowCoordinator,
                validatedMapQuery,
                CanStartQuickAction,
                CanStartMainAction,
                GetQuickActionUnavailableReason,
                CompleteAction,
                BuildInteraction.RestoreAfterCityStylePreviewClosed,
                SubmitMainActionIntent);
        }
        public InteractionMode Mode
        {
            get { return MoveInteraction.Mode; }
        }
        public BuildInteraction BuildInteraction { get; private set; }
        public MoveInteraction MoveInteraction { get; private set; }
        public TurnActionPanelPresenter ActionPanelPresenter { get; private set; }
        public CityStyleInteraction CityStyleInteraction { get; private set; }
        public bool IsSelectingMoveTarget
        {
            get { return MoveInteraction.IsSelectingMoveTarget; }
        }

        public bool IsAwaitingInitialPlacement
        {
            get { return MoveInteraction.IsAwaitingInitialPlacement; }
        }

        public void Activate() { MoveInteraction.Activate(); }

        public void Cancel() { MoveInteraction.Cancel(); }

        public void SynchronizeFromState()
        {
            localPlayerResolver.ResolveAndApply(context);
            BuildInteraction.SynchronizeFromState();
            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            if (player == null ||
                (player.CompletedMainActionsThisTurn <= 0 && !player.ActedMainActionThisTurn))
            {
                completedMainActionName = string.Empty;
            }

            if (BuildInteraction.IsActive &&
                (player == null ||
                 !MainActionBudgetService.HasAvailableMainAction(state, context.LocalPlayerId) ||
                 state.CurrentPlayerId != context.LocalPlayerId))
            {
                BuildInteraction.Cancel();
            }
        }
        public bool IsLocalPlayersTurn() { return MoveInteraction.IsLocalPlayersTurn(); }

        public bool CanEndCurrentAction()
        {
            var state = context.CurrentState;
            if (state == null || state.HasPendingChoice() ||
                !string.IsNullOrEmpty(pendingMainActionCommandId))
            {
                return false;
            }

            var player = state.FindPlayer(context.LocalPlayerId);
            if (state.Phase == GamePhase.ResourceCollection)
            {
                return player != null && !player.HasCollectedResourcesThisRound;
            }

            if (state.Phase == GamePhase.Cleanup)
            {
                return player != null &&
                       (context.LocalPlayerId == state.StartPlayerId || context.LocalPlayerId == state.CurrentPlayerId);
            }

            return IsLocalPlayersTurn() &&
                   (state.Phase == GamePhase.ActionRound1 || state.Phase == GamePhase.ActionRound2) &&
                   player != null &&
                   (player.CompletedMainActionsThisTurn > 0 || player.ActedMainActionThisTurn);
        }

        public void EndCurrentAction()
        {
            if (!CanEndCurrentAction())
            {
                view.ShowPrompt(ActionPanelPresenter.GetUnavailableEndActionPrompt());
                return;
            }

            var state = context.CurrentState;
            if (state.Phase == GamePhase.ResourceCollection)
            {
                resourceCollectionPresenter.Submit();
                return;
            }

            commandGateway.Submit(
                new GameCommand
                {
                    Kind = GameCommandKind.EndAction,
                    PlayerId = context.LocalPlayerId
                },
                new SubmitCallbacks(
                    view.ShowPrompt,
                    CommandGateway.BuildWaitingForHostPrompt("\u7ed3\u675f\u56de\u5408\u547d\u4ee4"))
                {
                    OnAppliedLocally = result =>
                    {
                        completedMainActionName = string.Empty;
                        flowCoordinator.ResetToChooseAction();
                        view.RefreshFromState();
                        view.ShowPrompt(
                            ActionPanelPresenter.BuildEndActionPrompt(
                                context.CurrentState));
                    }
                });
        }

        public void BeginNextRound()
        {
            EndCurrentAction();
        }

        public void PlaceInitialCity(string locationId) { MoveInteraction.PlaceInitialCity(locationId); }

        public IReadOnlyList<WorkflowHighlight> BuildInitialPlacementHighlights() { return MoveInteraction.BuildInitialPlacementHighlights(); }

        public void BeginMoveAction() { BeginMainAction(GameCommandKind.MoveCity); }

        public void MoveCity(string locationId) { MoveInteraction.Move(locationId); }

        public void RestoreMovePresentation() { MoveInteraction.RestorePresentation(); }

        public void BeginExploreAction() { BeginMainAction(GameCommandKind.ExploreLocation); }

        public void BeginAdditionalExploreAction(PendingCardSessionState pending, string optionId)
        {
            explorationEventPresenter.PrepareAdditionalExplore(pending, optionId);
            flowCoordinator.Activate(explorationEventPresenter);
        }

        public void CancelAdditionalExploreAction()
        {
            if (flowCoordinator.IsActive(explorationEventPresenter))
            {
                flowCoordinator.ResetToChooseAction();
            }
        }

        public void BeginDeployAction() { BeginMainAction(GameCommandKind.DeployInfluence); }

        public void BeginDispatchAction() { BeginMainAction(GameCommandKind.DispatchInfluence); }

        public void BeginSpecialAction() { BeginMainAction(GameCommandKind.UseSpecialAction); }

        private void BeginMainAction(GameCommandKind kind)
        {
            if (!CanStartMainAction()) return;
            flowCoordinator.ResetToChooseAction();
            SubmitMainActionIntent(new GameCommand { Kind = kind, PlayerId = context.LocalPlayerId });
        }

        public void BeginResourceCollection()
        {
            localPlayerResolver.ResolveAndApply(context);
            var player = context.CurrentState == null ? null : context.CurrentState.FindPlayer(context.LocalPlayerId);
            if (player == null || player.HasCollectedResourcesThisRound)
            {
                flowCoordinator.SetMode(InteractionMode.WaitingForNextPlayer);
                return;
            }

            flowCoordinator.Activate(resourceCollectionPresenter);
        }

        public void BeginBuildAction() { BeginMainAction(GameCommandKind.BuildFacility); }

        private string draggedFacilityId = string.Empty;

        public void BeginBuildFacilityDrag(string facilityId)
        {
            draggedFacilityId = CanStartMainAction() ? facilityId : string.Empty;
        }

        public void DropBuildFacility(int cityBoardSlotIndex)
        {
            if (string.IsNullOrEmpty(draggedFacilityId) || !CanStartMainAction()) return;
            var command = new GameCommand { Kind = GameCommandKind.BuildFacility, PlayerId = context.LocalPlayerId };
            command.Parameters[BuildFacilityCommandHandler.FacilityIdParameter] = draggedFacilityId;
            command.Parameters[BuildFacilityCommandHandler.CityBoardSlotIndexParameter] = cityBoardSlotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            draggedFacilityId = string.Empty;
            flowCoordinator.ResetToChooseAction();
            SubmitMainActionIntent(command);
        }

        public void RejectBuildFacilityDrop()
        {
            draggedFacilityId = string.Empty;
        }

        public void BeginGhostBuildFacilityDrag()
        {
            BuildInteraction.BeginGhostDrag();
        }

        public bool HandleBuildFacilityEscape()
        {
            return BuildInteraction.HandleEscape();
        }

        public void SelectBuildFacilityPayment(string paymentMode)
        {
            BuildInteraction.SelectPayment(paymentMode);
        }

        public void BackToBuildFacilityPayment()
        {
            BuildInteraction.BackToPayment();
        }

        public void CancelBuildFacility()
        {
            BuildInteraction.CancelExplicitly();
        }

        public void ConfirmBuildFacility()
        {
            BuildInteraction.Confirm();
        }

        public BuildFacilityDraftViewModel BuildBuildFacilityDraftViewModel()
        {
            return BuildInteraction.BuildDraftViewModel();
        }

        public BuildFacilityAvailabilityViewModel BuildBuildFacilityAvailabilityViewModel()
        {
            return BuildInteraction.BuildAvailabilityViewModel();
        }

        public void BeginDeclareCityStyle(string initialCityStyleId = "")
        {
            CityStyleInteraction.BeginDeclare(initialCityStyleId);
        }

        public void OpenCityStylePreview(string initialCityStyleId)
        {
            CityStyleInteraction.OpenPreview(initialCityStyleId);
        }

        public void SubmitDeclareCityStyle(string cityStyleId, IReadOnlyList<int> selectedSlotIndexes)
        {
            CityStyleInteraction.SubmitDeclare(cityStyleId, selectedSlotIndexes);
        }

        public void CompleteAction(string actionName)
        {
            completedMainActionName = actionName ?? string.Empty;
            flowCoordinator.ResetToChooseAction();
            view.CompleteMainActionPresentation(completedMainActionName);
        }

        public ActionPanelViewModel BuildActionPanelViewModel() => ActionPanelPresenter.BuildViewModel();

        public void NotifyCommandSettled(string commandId)
        {
            if (string.IsNullOrEmpty(commandId) || commandId != pendingMainActionCommandId) return;
            pendingMainActionCommandId = string.Empty;
            view.RefreshActionPanel();
        }

        private bool SubmitMainActionIntent(GameCommand command)
        {
            if (!string.IsNullOrEmpty(pendingMainActionCommandId)) return false;
            pendingMainActionCommandId = command.CommandId;
            view.RefreshActionPanel();
            try
            {
                var outcome = commandGateway.Submit(command,
                    new SubmitCallbacks(view.ShowPrompt, CommandGateway.BuildWaitingForHostPrompt("主要行动"))
                    {
                        BeforeRejectedPrompt = _ => NotifyCommandSettled(command.CommandId),
                        OnAppliedLocally = _ =>
                        {
                            NotifyCommandSettled(command.CommandId);
                            view.RefreshFromState();
                        }
                    });
                return outcome.Kind == SubmitOutcomeKind.WaitingForHost ||
                       outcome.Kind == SubmitOutcomeKind.AppliedLocally;
            }
            catch
            {
                NotifyCommandSettled(command.CommandId);
                throw;
            }
        }

        private bool CanStartMainAction() => string.IsNullOrEmpty(pendingMainActionCommandId) && ActionPanelPresenter.CanStartMainAction();

        private bool CanStartQuickAction() => ActionPanelPresenter.CanStartQuickAction();

        private string GetQuickActionUnavailableReason()
        {
            return ActionPanelPresenter.GetQuickActionUnavailableReason();
        }

        private void CancelActiveMainActionSelection()
        {
            flowCoordinator.ResetToChooseAction();
            view.RefreshActionPanel();
        }

        public static string BuildCompletedMainActionMessage(string actionName)
        {
            return TurnActionPanelPresenter.BuildCompletedMainActionMessage(actionName);
        }
    }
}
