namespace YC.Presentation.Workflows
{
    public interface IInteractionWorkflow
    {
        InteractionMode Mode { get; }

        void Activate();

        void Cancel();
    }
}
