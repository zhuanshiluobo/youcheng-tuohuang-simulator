using System;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>把设施待选协调器接入统一交互路由，内部阶段机仍由原协调器持有。</summary>
    internal sealed class FacilityEffectInteractionAdapter : InteractionBase
    {
        private readonly FacilityEffectInteractionUiCoordinator coordinator;

        public FacilityEffectInteractionAdapter(FacilityEffectInteractionUiCoordinator coordinator)
        {
            this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        public override string Id => "pending.facility-effect";

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
            return coordinator.TryHandleAdditionalBuildEscape()
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
