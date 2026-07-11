using NUnit.Framework;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class InteractionFlowCoordinatorTests
    {
        [Test]
        public void NewCoordinator_StartsHiddenWithoutActiveWorkflow()
        {
            var coordinator = new InteractionFlowCoordinator();

            Assert.That(coordinator.CurrentMode, Is.EqualTo(InteractionMode.Hidden));
            Assert.That(coordinator.ActiveWorkflow, Is.Null);
            Assert.That(coordinator.IsActive(InteractionMode.Hidden), Is.True);
        }

        [Test]
        public void Activate_ActivatesWorkflowAndExposesItsMode()
        {
            var coordinator = new InteractionFlowCoordinator();
            var workflow = new RecordingWorkflow(InteractionMode.ResolvingExploreTarget);

            coordinator.Activate(workflow);

            Assert.That(workflow.ActivationCount, Is.EqualTo(1));
            Assert.That(workflow.CancellationCount, Is.EqualTo(0));
            Assert.That(coordinator.CurrentMode, Is.EqualTo(InteractionMode.ResolvingExploreTarget));
            Assert.That(coordinator.IsActive(workflow), Is.True);
        }

        [Test]
        public void Activate_DifferentWorkflowCancelsPreviousWorkflowFirst()
        {
            var coordinator = new InteractionFlowCoordinator();
            var first = new RecordingWorkflow(InteractionMode.ResolvingMoveTarget);
            var second = new RecordingWorkflow(InteractionMode.ResolvingDeployTarget);

            coordinator.Activate(first);
            coordinator.Activate(second);

            Assert.That(first.CancellationCount, Is.EqualTo(1));
            Assert.That(second.ActivationCount, Is.EqualTo(1));
            Assert.That(coordinator.ActiveWorkflow, Is.SameAs(second));
        }

        [Test]
        public void Activate_SameWorkflowAgainDoesNotCancelOrReactivateIt()
        {
            var coordinator = new InteractionFlowCoordinator();
            var workflow = new RecordingWorkflow(InteractionMode.ResolvingResourceCollection);

            coordinator.Activate(workflow);
            coordinator.Activate(workflow);

            Assert.That(workflow.ActivationCount, Is.EqualTo(1));
            Assert.That(workflow.CancellationCount, Is.EqualTo(0));
            Assert.That(coordinator.IsActive(workflow), Is.True);
        }

        [Test]
        public void CurrentMode_TracksActiveWorkflowModeChanges()
        {
            var coordinator = new InteractionFlowCoordinator();
            var workflow = new RecordingWorkflow(InteractionMode.ResolvingDispatchSource);

            coordinator.Activate(workflow);
            workflow.ChangeMode(InteractionMode.ResolvingDispatchTarget);

            Assert.That(coordinator.CurrentMode, Is.EqualTo(InteractionMode.ResolvingDispatchTarget));
            Assert.That(coordinator.IsActive(InteractionMode.ResolvingDispatchTarget), Is.True);
        }

        [Test]
        public void Reset_CancelsActiveWorkflowAndSelectsRequestedRestingMode()
        {
            var coordinator = new InteractionFlowCoordinator();
            var workflow = new RecordingWorkflow(InteractionMode.ResolvingDispatchTarget);

            coordinator.Activate(workflow);
            coordinator.ResetToChooseAction();

            Assert.That(workflow.CancellationCount, Is.EqualTo(1));
            Assert.That(coordinator.ActiveWorkflow, Is.Null);
            Assert.That(coordinator.CurrentMode, Is.EqualTo(InteractionMode.ChooseAction));

            coordinator.ResetToHidden();

            Assert.That(coordinator.CurrentMode, Is.EqualTo(InteractionMode.Hidden));
        }

        private sealed class RecordingWorkflow : IInteractionWorkflow
        {
            public RecordingWorkflow(InteractionMode mode)
            {
                Mode = mode;
            }

            public InteractionMode Mode { get; private set; }

            public int ActivationCount { get; private set; }

            public int CancellationCount { get; private set; }

            public void ChangeMode(InteractionMode mode)
            {
                Mode = mode;
            }

            public void Activate()
            {
                ActivationCount += 1;
            }

            public void Cancel()
            {
                CancellationCount += 1;
            }
        }
    }
}
