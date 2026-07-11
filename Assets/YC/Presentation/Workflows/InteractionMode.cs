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
        ResolvingEventInfluenceTarget,
        ResolvingResourceCollection,
        PendingChoice,
        WaitingForNextPlayer
    }
}
