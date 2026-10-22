using YC.Presentation.Workflows;
namespace YC.Presentation
{
    public sealed partial class MobileCityInteractionController
    {
        private InteractionRequestRouter interactionRequestRouter;
        private FacilityBuildPlacementUiCoordinator facilityBuildPlacement;
        private FacilityBuildRequestUiCoordinator facilityBuildRequest;
        private EventCardInteractionUiCoordinator eventCardInteraction;
        private MapEffectInteractionUiCoordinator influenceEffectInteraction;
        private EnterpriseInteractionUiCoordinator enterpriseInteraction;

        private void BuildEventCardInteraction()
        {
            eventCardInteraction = new EventCardInteractionUiCoordinator(
                () => UiReadState(),
                () => localPlayerId,
                GetPlayerDisplayName,
                eventChoiceDialog,
                highlights => workflowView.SetHighlights(highlights),
                () => workflowView.ClearHighlights(),
                SubmitPendingEffectCommand,
                SetPrompt);
        }

        private void RegisterEventCardInteraction()
        {
            eventCardInteraction.GetVisibleRequests = GetVisibleInteractionRequests;
            characterAbilityInteraction.GetVisibleRequests = GetVisibleInteractionRequests;
            facilityInteraction.GetVisibleRequests = GetVisibleInteractionRequests;
            interactionRequestRouter = new InteractionRequestRouter();
            enterpriseInteraction = new EnterpriseInteractionUiCoordinator(gameplayInteractionHud.ExpansionUi,
                gameplayInteractionHud, GetVisibleInteractionRequests, () => localPlayerId, SubmitPendingEffectCommand);
            interactionRequestRouter.Register(enterpriseInteraction);
            interactionRouter.Register(enterpriseInteraction);
            characterAbilityInteraction.ExpansionUi = gameplayInteractionHud.ExpansionUi;
            facilityBuildPlacement = new FacilityBuildPlacementUiCoordinator(
                () => UiReadState(), () => localPlayerId, gameplayInteractionHud.DialogRegistry,
                uiCanvas.transform as UnityEngine.RectTransform, SubmitPendingEffectCommand);
            facilityBuildPlacement.GetVisibleRequests = GetVisibleInteractionRequests;
            turnActionPresenter.BuildInteraction.GetVisibleRequests = GetVisibleInteractionRequests;
            interactionRequestRouter.Register(facilityBuildPlacement);
            interactionRouter.Register(facilityBuildPlacement);
            facilityBuildRequest?.Dispose();
            facilityBuildRequest = new FacilityBuildRequestUiCoordinator(
                () => UiReadState(), VisibleSaleState, () => localPlayerId, gameplayInteractionHud.DialogRegistry,
                uiCanvas.transform as UnityEngine.RectTransform, gameplayAdapter,
                () => CanSubmitGameplayCommand && string.IsNullOrEmpty(pendingEffectCommandId), SynchronizeInteractionFromState, SetPrompt);
            interactionRequestRouter.Register(facilityBuildRequest);
            interactionRouter.Register(facilityBuildRequest);
            interactionRequestRouter.Register(eventCardInteraction);
            interactionRequestRouter.Register(characterAbilityInteraction);
            interactionRequestRouter.Register(facilityInteraction);
            specialActionInteraction.GetVisibleRequests = GetVisibleInteractionRequests;
            interactionRequestRouter.Register(specialActionInteraction);
            influenceEffectInteraction = new MapEffectInteractionUiCoordinator(
                () => UiReadState(), () => localPlayerId,
                characterCardEffectChoiceDialog, highlights => workflowView.SetHighlights(highlights),
                () => workflowView.ClearHighlights(), SubmitPendingEffectCommand, SetPrompt);
            influenceEffectInteraction.GetVisibleRequests = GetVisibleInteractionRequests;
            influenceEffectInteraction.Copy = gameplayInteractionHud.ExpansionUi.Copy;
            influenceEffectInteraction.ExpansionUi = gameplayInteractionHud.ExpansionUi;
            interactionRequestRouter.Register(influenceEffectInteraction);
            interactionRouter.Register(influenceEffectInteraction);
            interactionRouter.Register(eventCardInteraction);
            interactionRouter.Register(characterAbilityInteraction);
        }

        private void ClearEventCardInteraction()
        {
            interactionRequestRouter?.Clear();
        }

        private bool CanPreserveRequestDraftForRefresh() =>
            commandSubmission?.UsesDeterministicSimulation == true && interactionRequestRouter != null &&
            (enterpriseInteraction?.CanPreserveUnsubmittedDraft(interactionRequestRouter.CurrentRequest) == true ||
             enterpriseInteraction?.CanPreservePendingDraft(interactionRequestRouter.CurrentRequest) == true ||
             routeResourceSelection?.CanPreserveNormalHarvestDraft(interactionRequestRouter.CurrentRequest) == true ||
             characterAbilityInteraction?.CanPreserveUnsubmittedInitialCharacterDraft(interactionRequestRouter.CurrentRequest) == true);

        private bool SynchronizeEventCardInteraction()
        {
            if (interactionRequestRouter == null)
            {
                return false;
            }

            return interactionRequestRouter.RouteOpen(GetVisibleInteractionRequests(), localPlayerId);
        }

        private System.Collections.Generic.IReadOnlyList<YC.Domain.State.InteractionRequest> GetVisibleInteractionRequests() =>
            VisibleInteractionRequestSource.Read(null, VisibleSaleState(), localPlayerId);
    }
}
