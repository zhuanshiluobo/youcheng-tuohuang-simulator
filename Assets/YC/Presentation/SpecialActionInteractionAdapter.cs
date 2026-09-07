using System;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>把特殊行动待选协调器接入统一交互路由，保留原有在途命令追踪。</summary>
    internal sealed class SpecialActionInteractionAdapter : InteractionBase
    {
        private readonly SpecialActionInteractionUiCoordinator coordinator;

        public SpecialActionInteractionAdapter(SpecialActionInteractionUiCoordinator coordinator)
        {
            this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        public override string Id => "pending.special-action";

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
            return coordinator.IsBlockingMapInteraction
                ? InteractionResult.Consumed
                : InteractionResult.Passthrough;
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

        public override void Cancel()
        {
            coordinator.Dispose();
        }

        public override void NotifyCommandSettled(string commandId)
        {
            coordinator.NotifyCommandSettled(commandId);
            // 广播可能先于下一次刷新到达，失效会话必须立即收尾。
            if (!coordinator.IsActive) coordinator.Dispose();
        }
    }
}
