using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>Keeps Unity view adaptation out of workflow presenters and the scene controller.</summary>
    internal sealed class MobileCityWorkflowViewAdapter :
        IResourceCollectionView,
        IInfluenceActionView,
        IExplorationEventView,
        ITurnActionView
    {
        private readonly Func<GameState> getState;
        private readonly Func<int> getLocalPlayerId;
        private readonly Func<RectTransform> getCanvas;
        private readonly Func<MapViewPresenter> getMapView;
        private readonly IMapQueryService mapQuery;
        private readonly EventChoiceDialog eventChoiceDialog;
        private readonly CityStyleDeclarationPreviewDialog cityStyleDeclarationDialog;
        private readonly DispatchDecisionView dispatchDecisionView;
        private readonly Action<string> showPrompt;
        private readonly Action refreshAll;
        private readonly Action refreshInformation;
        private readonly Action refreshActionPanel;
        private readonly Action refreshResource;
        private readonly Action refreshInfluence;
        private readonly Action<string> completeActionPresentation;
        private readonly Func<int, string> getPlayerDisplayName;
        private InteractionFlowCoordinator flowCoordinator;
        private ResourceCollectionPresenter resourceCollectionPresenter;
        private InfluenceActionPresenter influenceActionPresenter;
        private ExplorationEventPresenter explorationEventPresenter;
        private TurnActionPresenter turnActionPresenter;

        public MobileCityWorkflowViewAdapter(
            Func<GameState> getState,
            Func<int> getLocalPlayerId,
            Func<RectTransform> getCanvas,
            GameplayDialogRegistry dialogRegistry,
            Func<MapViewPresenter> getMapView,
            IMapQueryService mapQuery,
            EventChoiceDialog eventChoiceDialog,
            Action<string> showPrompt,
            Action refreshAll,
            Action refreshInformation,
            Action refreshActionPanel,
            Action refreshResource,
            Action refreshInfluence,
            Action<string> completeActionPresentation,
            Func<int, string> getPlayerDisplayName)
        {
            this.getState = getState;
            this.getLocalPlayerId = getLocalPlayerId;
            this.getCanvas = getCanvas;
            cityStyleDeclarationDialog = new CityStyleDeclarationPreviewDialog(
                dialogRegistry ?? throw new ArgumentNullException(nameof(dialogRegistry)),
                getCanvas ?? throw new ArgumentNullException(nameof(getCanvas)));
            this.getMapView = getMapView;
            this.mapQuery = mapQuery;
            this.eventChoiceDialog = eventChoiceDialog;
            dispatchDecisionView = new DispatchDecisionView(dialogRegistry, getCanvas);
            this.showPrompt = showPrompt;
            this.refreshAll = refreshAll;
            this.refreshInformation = refreshInformation;
            this.refreshActionPanel = refreshActionPanel;
            this.refreshResource = refreshResource;
            this.refreshInfluence = refreshInfluence;
            this.completeActionPresentation = completeActionPresentation;
            this.getPlayerDisplayName = getPlayerDisplayName;
        }

        public void Bind(
            InteractionFlowCoordinator coordinator,
            ResourceCollectionPresenter resourceCollection,
            InfluenceActionPresenter influenceAction,
            ExplorationEventPresenter explorationEvent,
            TurnActionPresenter turnAction)
        {
            flowCoordinator = coordinator;
            resourceCollectionPresenter = resourceCollection;
            influenceActionPresenter = influenceAction;
            explorationEventPresenter = explorationEvent;
            turnActionPresenter = turnAction;
        }

        public void ShowPrompt(string message) => showPrompt(message);

        public void SetHighlights(IReadOnlyList<WorkflowHighlight> highlights)
        {
            ClearHighlights();
            var mapView = getMapView();
            if (highlights == null || mapView == null) return;

            for (var i = 0; i < highlights.Count; i++)
            {
                var highlight = highlights[i];
                if (highlight == null) continue;
                mapView.ApplyHighlight(highlight);
            }
            refreshInfluence();
        }

        public void ClearHighlights()
        {
            var mapView = getMapView();
            if (mapView != null) mapView.ClearHighlights();
        }

        public void ShowRoutePaymentOptions(string routeId, int cost, IReadOnlyList<int> recipientPlayerIds)
        {
            eventChoiceDialog.ShowResourceCollectionPaymentOptions(
                routeId, cost, recipientPlayerIds, getPlayerDisplayName,
                receiver => resourceCollectionPresenter.ConfirmRoutePayment(routeId, receiver),
                () => resourceCollectionPresenter.ConfirmRoutePayment(routeId, -1),
                resourceCollectionPresenter.CancelRoutePayment,
                resourceCollectionPresenter.GetSelectedPaymentRecipient(routeId),
                () => resourceCollectionPresenter.RemoveRoutePayment(routeId));
        }

        public string GetPlayerDisplayName(int playerId) => getPlayerDisplayName(playerId);
        public void RefreshSelectionView() { refreshInfluence(); refreshActionPanel(); }
        public void RefreshFromState() => refreshAll();

        public void SetInteractionMode(InteractionMode mode)
        {
            if (flowCoordinator == null) return;
            var active = flowCoordinator.IsActive(influenceActionPresenter) ||
                         flowCoordinator.IsActive(explorationEventPresenter);
            if (!active) flowCoordinator.SetMode(mode);
        }

        public void ShowDispatchDecision(DispatchDecisionViewModel viewModel) => dispatchDecisionView.Show(viewModel);
        public void HideDispatchDecision() => dispatchDecisionView.Hide();
        public void RefreshInfluencePreview(bool pending, string sourceSlotId, string targetSlotId)
        {
            var mapView = getMapView();
            var state = getState();
            if (mapView != null && state != null) mapView.RefreshInfluenceDisplay(state, pending, sourceSlotId, targetSlotId);
        }
        public void RefreshActionPanel() => refreshActionPanel();
        public void CompleteAction(string actionName) => turnActionPresenter.CompleteAction(actionName);

        public void ShowExplorePathOptions(ExplorePathOptionsViewModel model)
        {
            if (model != null) eventChoiceDialog.ShowExplorePathOptions(model.Choices, model.SelectPath);
        }
        public void ShowExplorePaymentOptions(ExplorePaymentOptionsViewModel model)
        {
            if (model != null) eventChoiceDialog.ShowExplorePaymentOptions(
                model.Choices, model.RecipientsByRouteId, getPlayerDisplayName, model.SelectRecipient, model.Confirm);
        }
        public void ShowEventCardOptions(EventCardOptionsViewModel model)
        {
            if (model == null)
            {
                return;
            }

            eventChoiceDialog.SetChoiceHighlightColor(ResolveEventChoiceHighlightColor());
            eventChoiceDialog.ShowEventCardOptions(
                model.Card,
                model.MetadataLabel,
                model.PaymentChoices,
                model.RecipientsByRouteId,
                getPlayerDisplayName,
                model.SelectChoice,
                model.SelectRecipient);
        }

        private Color ResolveEventChoiceHighlightColor()
        {
            var state = getState();
            if (state == null)
            {
                return UiTheme.CyanAccent;
            }

            var playerId = getLocalPlayerId();
            if (playerId <= 0)
            {
                playerId = state.CurrentPlayerId;
            }

            var player = state.FindPlayer(playerId);
            return player == null
                ? UiTheme.CyanAccent
                : UiTheme.GetPlayerColor(player.Color, 1f);
        }
        public void CollapseEventOptions() => eventChoiceDialog.SuspendForMapInteraction();
        public void HideEventOptions() => eventChoiceDialog.Hide();
        public void RefreshResourceAndInfluence() { refreshResource(); refreshInfluence(); }

        void ITurnActionView.RefreshInformation() => refreshInformation();
        void ITurnActionView.ShowPendingChoice() => explorationEventPresenter.ShowPendingChoice();
        void ITurnActionView.CompleteMainActionPresentation(string actionName) => completeActionPresentation(actionName);
        public void ShowBuildFacilityDraft(BuildFacilityDraftViewModel model)
        {
            if (model == null)
            {
                eventChoiceDialog.Hide();
                return;
            }

            if (model.Phase == BuildFacilityDraftPhase.Focused)
            {
                eventChoiceDialog.ShowBuildFacilityFocus(model);
            }
            else if (model.Phase == BuildFacilityDraftPhase.Confirming)
            {
                eventChoiceDialog.ShowBuildFacilityConfirmation(model);
            }
            else
            {
                eventChoiceDialog.Hide();
            }
        }
        public void HideBuildFacilityDraft() => eventChoiceDialog.Hide();
        public void RefreshCityStylePreview() => cityStyleDeclarationDialog.RefreshProjection();

        public void ShowCityStyleOptions(CityStyleOptionsViewModel model)
        {
            if (model == null)
            {
                cityStyleDeclarationDialog.Hide();
                return;
            }

            if (model.DeclareMode) eventChoiceDialog.Hide();
            cityStyleDeclarationDialog.Show(model);
        }

    }
}
