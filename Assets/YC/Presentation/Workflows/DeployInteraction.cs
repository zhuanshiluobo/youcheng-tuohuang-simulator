using System;

namespace YC.Presentation.Workflows
{
    /// <summary>把影响力 Presenter 的部署阶段包装为独立交互。</summary>
    public sealed class DeployInteraction : InteractionBase
    {
        private readonly InfluenceActionPresenter presenter;

        public DeployInteraction(InfluenceActionPresenter presenter)
        {
            this.presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        }

        public override string Id => "active.deploy";

        public override InteractionPriority Priority => InteractionPriority.ActiveAction;

        public override bool IsActive => presenter.IsSelectingDeployTarget;

        public override InteractionResult OnLocationClicked(string locationId)
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionResult OnMobileCityClicked()
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionResult OnEscape()
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionPresentation BuildPresentation()
        {
            return IsActive
                ? new InteractionPresentation(
                    null,
                    presenter.CurrentPrompt,
                    InteractionMode.Busy,
                    false)
                : InteractionPresentation.Empty;
        }

        public override void Cancel()
        {
            if (IsActive)
            {
                presenter.Cancel();
            }
        }
    }
}
