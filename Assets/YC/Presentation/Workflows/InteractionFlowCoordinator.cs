using System;

namespace YC.Presentation.Workflows
{
    public sealed class InteractionFlowCoordinator
    {
        private IInteractionWorkflow activeWorkflow;
        private InteractionMode restingMode = InteractionMode.Hidden;

        public InteractionMode CurrentMode
        {
            get { return activeWorkflow == null ? restingMode : activeWorkflow.Mode; }
        }

        public IInteractionWorkflow ActiveWorkflow
        {
            get { return activeWorkflow; }
        }

        public bool IsActive(InteractionMode mode)
        {
            return CurrentMode == mode;
        }

        public bool IsActive(IInteractionWorkflow workflow)
        {
            return workflow != null && ReferenceEquals(activeWorkflow, workflow);
        }

        public void Activate(IInteractionWorkflow workflow)
        {
            if (workflow == null)
            {
                throw new ArgumentNullException(nameof(workflow));
            }

            if (ReferenceEquals(activeWorkflow, workflow))
            {
                return;
            }

            CancelActiveWorkflow();
            activeWorkflow = workflow;
            try
            {
                workflow.Activate();
            }
            catch
            {
                activeWorkflow = null;
                throw;
            }
        }

        public void SetMode(InteractionMode mode)
        {
            CancelActiveWorkflow();
            restingMode = mode;
        }

        public void ResetToHidden()
        {
            SetMode(InteractionMode.Hidden);
        }

        public void ResetToChooseAction()
        {
            SetMode(InteractionMode.ChooseAction);
        }

        private void CancelActiveWorkflow()
        {
            if (activeWorkflow == null)
            {
                return;
            }

            activeWorkflow.Cancel();
            activeWorkflow = null;
        }
    }
}
