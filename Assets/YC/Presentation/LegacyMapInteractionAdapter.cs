using System;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>把旧地图路由作为统一注册表的默认末级路由。</summary>
    internal sealed class LegacyMapInteractionAdapter : InteractionBase
    {
        private readonly MapInteractionRouter router;

        public LegacyMapInteractionAdapter(MapInteractionRouter router)
        {
            this.router = router ?? throw new ArgumentNullException(nameof(router));
        }

        public override string Id => "default.map-route";

        public override InteractionPriority Priority => InteractionPriority.DefaultRoute;

        public override bool IsActive => true;

        public override InteractionResult OnLocationClicked(string locationId)
        {
            router.OnLocationClicked(locationId);
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            router.OnInfluenceSlotClicked(slotId);
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnMobileCityClicked()
        {
            router.OnMobileCityClicked();
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnEscape()
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionPresentation BuildPresentation()
        {
            return InteractionPresentation.Empty;
        }

        public override void Cancel()
        {
            router.ClearConfirmation(false);
        }
    }
}
