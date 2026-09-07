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
            // 广播可能先于下一次刷新到达，失效会话必须立即收尾。
            if (!coordinator.IsActive) coordinator.Dispose();
        }

        public override void Cancel()
        {
            coordinator.Dispose();
        }
    }
}
