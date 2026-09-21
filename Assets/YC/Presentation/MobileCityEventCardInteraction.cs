using YC.Presentation.Workflows;
namespace YC.Presentation
{
    public sealed partial class MobileCityInteractionController
    {
        private InteractionRequestRouter interactionRequestRouter;
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
            interactionRequestRouter = new InteractionRequestRouter();
            interactionRequestRouter.Register(eventCardInteraction);
            interactionRequestRouter.Register(characterAbilityInteraction);
            interactionRequestRouter.Register(facilityInteraction);
            influenceEffectInteraction = new MapEffectInteractionUiCoordinator(
                () => session == null ? null : session.State, () => localPlayerId,
                characterCardEffectChoiceDialog, highlights => workflowView.SetHighlights(highlights),
                () => workflowView.ClearHighlights(), SubmitPendingEffectCommand, SetPrompt);
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

            var state = session == null ? null : session.State;
            if (state == null || state.EffectRuntime == null ||
                state.EffectRuntime.InteractionRequests == null)
            {
                interactionRequestRouter.Clear();
                return false;
            }

            return interactionRequestRouter.RouteOpen(state.EffectRuntime.InteractionRequests, localPlayerId);
        }
    }
}
