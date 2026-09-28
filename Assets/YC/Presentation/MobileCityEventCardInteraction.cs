using YC.Presentation.Workflows;
namespace YC.Presentation
{
    public sealed partial class MobileCityInteractionController
    {
        private InteractionRequestRouter interactionRequestRouter;
        private FacilityBuildPlacementUiCoordinator facilityBuildPlacement;
        private EventCardInteractionUiCoordinator eventCardInteraction;
        private MapEffectInteractionUiCoordinator influenceEffectInteraction;

        private void BuildEventCardInteraction()
        {
            eventCardInteraction = new EventCardInteractionUiCoordinator(
                () => session == null ? null : session.State,
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
            facilityBuildPlacement = new FacilityBuildPlacementUiCoordinator(
                () => session?.State, () => localPlayerId, gameplayInteractionHud.DialogRegistry,
                uiCanvas.transform as UnityEngine.RectTransform, SubmitPendingEffectCommand);
            facilityBuildPlacement.GetVisibleRequests = GetVisibleInteractionRequests;
            turnActionPresenter.BuildInteraction.GetVisibleRequests = GetVisibleInteractionRequests;
            interactionRequestRouter.Register(facilityBuildPlacement);
            interactionRouter.Register(facilityBuildPlacement);
            interactionRequestRouter.Register(eventCardInteraction);
            interactionRequestRouter.Register(characterAbilityInteraction);
            interactionRequestRouter.Register(facilityInteraction);
            specialActionInteraction.GetVisibleRequests = GetVisibleInteractionRequests;
            interactionRequestRouter.Register(specialActionInteraction);
            influenceEffectInteraction = new MapEffectInteractionUiCoordinator(
                () => session == null ? null : session.State, () => localPlayerId,
                characterCardEffectChoiceDialog, highlights => workflowView.SetHighlights(highlights),
                () => workflowView.ClearHighlights(), SubmitPendingEffectCommand, SetPrompt);
            influenceEffectInteraction.GetVisibleRequests = GetVisibleInteractionRequests;
            interactionRequestRouter.Register(influenceEffectInteraction);
            interactionRouter.Register(influenceEffectInteraction);
            interactionRouter.Register(eventCardInteraction);
            interactionRouter.Register(characterAbilityInteraction);
        }

        private void ClearEventCardInteraction()
        {
            interactionRequestRouter?.Clear();
        }

        private bool SynchronizeEventCardInteraction()
        {
            if (interactionRequestRouter == null)
            {
                return false;
            }

            return interactionRequestRouter.RouteOpen(GetVisibleInteractionRequests(), localPlayerId);
        }

        private System.Collections.Generic.IReadOnlyList<YC.Domain.State.InteractionRequest> GetVisibleInteractionRequests() =>
            VisibleInteractionRequestSource.Read(session?.State, session?.View, localPlayerId);
    }
}
