namespace YC.Presentation.Workflows
{
    public abstract class InteractionBase : IInteraction
    {
        public abstract string Id { get; }

        public abstract InteractionPriority Priority { get; }

        public abstract bool IsActive { get; }

        public abstract InteractionResult OnLocationClicked(string locationId);

        public abstract InteractionResult OnInfluenceSlotClicked(string slotId);

        public abstract InteractionResult OnMobileCityClicked();

        public abstract InteractionResult OnEscape();

        public abstract InteractionPresentation BuildPresentation();

        public abstract void Cancel();

        public virtual void NotifyCommandSettled(string commandId)
        {
        }
    }
}
