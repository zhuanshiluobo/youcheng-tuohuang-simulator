using System;

namespace YC.Presentation.Workflows
{
    /// <summary>把探索目标、路径、付费与事件续接包装为独立交互。</summary>
    public sealed class ExploreInteraction : InteractionBase
    {
        private readonly ExplorationEventPresenter presenter;

        public ExploreInteraction(ExplorationEventPresenter presenter)
        {
            this.presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        }

        public override string Id => "active.explore";

        public override InteractionPriority Priority => InteractionPriority.ActiveAction;

        public override bool IsActive => presenter.IsActive;

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
