namespace YC.Presentation.Workflows
{
    public interface IInteraction
    {
        string Id { get; }

        InteractionPriority Priority { get; }

        bool IsActive { get; }

        InteractionResult OnLocationClicked(string locationId);

        InteractionResult OnInfluenceSlotClicked(string slotId);

        InteractionResult OnMobileCityClicked();

        InteractionResult OnEscape();

        InteractionPresentation BuildPresentation();

        void Cancel();

        void NotifyCommandSettled(string commandId);
    }
}
