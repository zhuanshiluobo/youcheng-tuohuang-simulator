using System;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>把设施通用 InteractionRequest 接入统一交互路由。</summary>
    internal sealed class FacilityInteractionAdapter : InteractionBase
    {
        private readonly FacilityInteractionUiCoordinator coordinator;

        public FacilityInteractionAdapter(FacilityInteractionUiCoordinator coordinator)
        {
            this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        public override string Id => "interaction.facility";

        public override InteractionPriority Priority => InteractionPriority.PendingResolution;

        public override bool IsActive => coordinator.IsActive;

        public override InteractionResult OnLocationClicked(string locationId)
        {
            return coordinator.TryHandleLocationClicked(locationId)
                ? InteractionResult.Consumed
                : InteractionResult.Passthrough;
        }

        public override InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            return coordinator.TryHandleInfluenceSlotClicked(slotId)
                ? InteractionResult.Consumed
                : InteractionResult.Passthrough;
        }

        public override InteractionResult OnMobileCityClicked()
        {
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnEscape()
        {
            return coordinator.TryHandleEscape()
                ? InteractionResult.Consumed
                : InteractionResult.Passthrough;
        }

        public override InteractionPresentation BuildPresentation()
        {
            return coordinator.Synchronize()
                ? InteractionPresentation.Busy
                : InteractionPresentation.Empty;
        }

        public override void NotifyCommandSettled(string commandId)
        {
            coordinator.NotifyCommandSettled(commandId);
        }

        public override void Cancel()
        {
            coordinator.Dispose();
        }
    }
}
