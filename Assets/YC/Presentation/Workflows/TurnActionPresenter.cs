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
            DeployInteraction = new DeployInteraction(this.influenceActionPresenter);
            DispatchInteraction = new DispatchInteraction(this.influenceActionPresenter);
            ExploreInteraction = new ExploreInteraction(this.explorationEventPresenter);
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
                      MoveInteraction.IsSelectingMoveTarget);
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
                BuildInteraction.RestoreAfterCityStylePreviewClosed);
        }
        public InteractionMode Mode
        {
            get { return MoveInteraction.Mode; }
        }
        public BuildInteraction BuildInteraction { get; private set; }
        public MoveInteraction MoveInteraction { get; private set; }
        public DeployInteraction DeployInteraction { get; private set; }
        public DispatchInteraction DispatchInteraction { get; private set; }
        public ExploreInteraction ExploreInteraction { get; private set; }
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
            if (state == null || state.HasPendingChoice())
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

        public void BeginMoveAction() { MoveInteraction.Begin(); }

        public void MoveCity(string locationId) { MoveInteraction.Move(locationId); }

        public void RestoreMovePresentation() { MoveInteraction.RestorePresentation(); }

        public void BeginExploreAction()
        {
            if (flowCoordinator.IsActive(explorationEventPresenter))
            {
                CancelActiveMainActionSelection();
                return;
            }

            if (CanStartMainAction())
            {
                explorationEventPresenter.PrepareNormalExplore();
                flowCoordinator.Activate(explorationEventPresenter);
            }
        }

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

        public void BeginDeployAction()
        {
            if (flowCoordinator.IsActive(influenceActionPresenter) &&
                influenceActionPresenter.IsSelectingDeployTarget)
            {
                CancelActiveMainActionSelection();
                return;
            }

            if (CanStartMainAction())
            {
                flowCoordinator.Activate(influenceActionPresenter);
            }
        }

        public void BeginDispatchAction()
        {
            if (flowCoordinator.IsActive(influenceActionPresenter) &&
                (influenceActionPresenter.IsSelectingDispatchSource ||
                 influenceActionPresenter.IsSelectingDispatchTarget ||
                 influenceActionPresenter.IsChoosingDispatchContinuation))
            {
                CancelActiveMainActionSelection();
                return;
            }

            if (!influenceActionPresenter.HasPendingFirstMove && !CanStartMainAction())
            {
                return;
            }

            if (!flowCoordinator.IsActive(influenceActionPresenter))
            {
                flowCoordinator.Activate(influenceActionPresenter);
            }

            influenceActionPresenter.BeginDispatch();
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

        public void BeginBuildAction() { BuildInteraction.Begin(); }

        public void BeginBuildFacilityDrag(string facilityId) { BuildInteraction.BeginDrag(facilityId); }

        public void DropBuildFacility(int cityBoardSlotIndex)
        {
            BuildInteraction.Drop(cityBoardSlotIndex);
        }

        public void RejectBuildFacilityDrop()
        {
            BuildInteraction.RejectDrop();
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

        public ActionPanelViewModel BuildActionPanelViewModel()
        {
            return ActionPanelPresenter.BuildViewModel();
        }

        private bool CanStartMainAction()
        {
            return ActionPanelPresenter.CanStartMainAction();
        }

        private bool CanStartQuickAction()
        {
            return ActionPanelPresenter.CanStartQuickAction();
        }

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
