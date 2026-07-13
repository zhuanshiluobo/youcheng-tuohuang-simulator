namespace YC.Presentation.Workflows
{
    public enum InteractionMode
    {
        Hidden,
        ChooseAction,
        ResolvingMoveTarget,
        ResolvingExploreTarget,
        ResolvingDeployTarget,
        ResolvingDispatchSource,
        ResolvingDispatchTarget,
        ResolvingDispatchDecision,
        ResolvingBuildCard,
        ResolvingBuildFocus,
        ResolvingBuildConfirmation,
        ResolvingEventInfluenceTarget,
        ResolvingResourceCollection,
        PendingChoice,
        WaitingForNextPlayer
    }
}
