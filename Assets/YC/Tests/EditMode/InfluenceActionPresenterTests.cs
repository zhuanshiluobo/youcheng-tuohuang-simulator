using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class InfluenceActionPresenterTests
    {
        [Test]
        public void DeployAndDispatchInteractions_ExposeMutuallyExclusivePresenterStages()
        {
            var fixture = CreateFixture();
            var deploy = new DeployInteraction(fixture.Presenter);
            var dispatch = new DispatchInteraction(fixture.Presenter);

            Assert.That(deploy.Id, Is.EqualTo("active.deploy"));
            Assert.That(dispatch.Id, Is.EqualTo("active.dispatch"));
            Assert.That(deploy.Priority, Is.EqualTo(InteractionPriority.ActiveAction));
            Assert.That(dispatch.Priority, Is.EqualTo(InteractionPriority.ActiveAction));
            Assert.That(deploy.IsActive, Is.False);
            Assert.That(dispatch.IsActive, Is.False);

            fixture.Presenter.BeginDeploy();

            Assert.That(deploy.IsActive, Is.True);
            Assert.That(dispatch.IsActive, Is.False);
            Assert.That(deploy.BuildPresentation().PanelMode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(
                deploy.OnInfluenceSlotClicked(fixture.DeployTarget),
                Is.SameAs(InteractionResult.Passthrough));
            deploy.Cancel();
            Assert.That(fixture.Presenter.IsActive, Is.False);

            fixture.Presenter.BeginDispatch();

            Assert.That(deploy.IsActive, Is.False);
            Assert.That(dispatch.IsActive, Is.True);
            Assert.That(dispatch.BuildPresentation().PromptText, Does.Contain("调度"));

            fixture.Presenter.SelectSlot(fixture.FirstSource);
            Assert.That(fixture.Presenter.IsSelectingDispatchTarget, Is.True);
            Assert.That(dispatch.IsActive, Is.True);
            Assert.That(deploy.IsActive, Is.False);

            fixture.Presenter.SelectSlot(fixture.FirstTarget);
            Assert.That(fixture.Presenter.IsSelectingDispatchTarget, Is.True);
            Assert.That(dispatch.IsActive, Is.True);

            fixture.Presenter.SelectSlot(fixture.FirstTarget);
            Assert.That(fixture.Presenter.IsChoosingDispatchContinuation, Is.True);
            Assert.That(dispatch.IsActive, Is.True);
            Assert.That(deploy.IsActive, Is.False);

            var router = new InteractionRouter(message => { });
            router.Register(deploy);
            router.Register(dispatch);
            Assert.That(() => router.CancelAll(), Throws.Nothing);
            Assert.That(fixture.Presenter.IsActive, Is.False);
        }

        [Test]
        public void Deploy_SecondSelectionSubmitsExpectedCommand()
        {
            var fixture = CreateFixture();
            fixture.Presenter.BeginDeploy();

            fixture.Presenter.SelectSlot(fixture.DeployTarget);
            Assert.That(fixture.CommandPort.LastCommand, Is.Null);

            fixture.Presenter.SelectSlot(fixture.DeployTarget);

            Assert.That(fixture.CommandPort.LastCommand.Kind, Is.EqualTo(GameCommandKind.DeployInfluence));
            Assert.That(fixture.CommandPort.LastCommand.PlayerId, Is.EqualTo(1));
            Assert.That(fixture.CommandPort.LastCommand.TargetId, Is.EqualTo(fixture.DeployTarget));
            Assert.That(fixture.View.CompletedAction, Is.EqualTo("部署"));
            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.ChooseAction));
            Assert.That(fixture.Presenter.IsSelectingDeployTarget, Is.False);
        }

        [Test]
        public void Dispatch_SelectSourceMovesToTargetModeAndHighlightsLegalTargets()
        {
            var fixture = CreateFixture();
            fixture.Presenter.BeginDispatch();
            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Presenter.IsSelectingDispatchSource, Is.True);

            fixture.Presenter.SelectSlot(fixture.FirstSource);

            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Presenter.IsSelectingDispatchSource, Is.False);
            Assert.That(fixture.Presenter.IsSelectingDispatchTarget, Is.True);
            Assert.That(fixture.Presenter.CurrentPrompt, Is.EqualTo("请选择调度目标槽位。"));
            Assert.That(fixture.Presenter.DispatchSourceSlotId, Is.EqualTo(fixture.FirstSource));
            Assert.That(fixture.View.HasHighlight(
                fixture.FirstTarget,
                WorkflowHighlightSemantic.DispatchTarget), Is.True);
        }

        [Test]
        public void Dispatch_InvalidTargetIsRejectedWithoutSubmission()
        {
            var fixture = CreateFixture();
            fixture.Presenter.BeginDispatch();
            fixture.Presenter.SelectSlot(fixture.FirstSource);

            fixture.Presenter.SelectSlot(fixture.SecondSource);

            Assert.That(fixture.CommandPort.LastCommand, Is.Null);
            Assert.That(fixture.View.Prompt, Does.Contain("没有可调度"));
        }

        [Test]
        public void Dispatch_FirstMoveShowsDecisionAndContinueRequestsSecondSource()
        {
            var fixture = CreateFixture();
            SelectFirstMove(fixture);

            Assert.That(fixture.Presenter.HasPendingFirstMove, Is.True);
            Assert.That(fixture.Presenter.IsChoosingDispatchContinuation, Is.True);
            Assert.That(fixture.View.Decision, Is.Not.Null);
            Assert.That(fixture.View.PreviewEnabled, Is.True);

            fixture.View.Decision.ContinueAction();

            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Presenter.IsChoosingDispatchContinuation, Is.False);
            Assert.That(fixture.Presenter.IsSelectingDispatchSource, Is.True);
            Assert.That(fixture.Context.State.Map.Influences[0].SlotId, Is.EqualTo(fixture.FirstSource),
                "合法性预览不得移动实时状态中的影响力。");
            Assert.That(fixture.View.Decision, Is.Null);
            Assert.That(fixture.View.HasHighlight(
                fixture.SecondSource,
                WorkflowHighlightSemantic.DispatchSource), Is.True);
        }

        [Test]
        public void Dispatch_FinishSubmitsSingleMoveCommand()
        {
            var fixture = CreateFixture();
            SelectFirstMove(fixture);

            fixture.View.Decision.FinishAction();

            var command = fixture.CommandPort.LastCommand;
            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.DispatchInfluence));
            Assert.That(command.SourceId, Is.EqualTo(fixture.FirstSource));
            Assert.That(command.TargetId, Is.EqualTo(fixture.FirstTarget));
            Assert.That(command.Parameters.ContainsKey("source2"), Is.False);
            Assert.That(fixture.View.CompletedAction, Is.EqualTo("调度"));
            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.ChooseAction));
            Assert.That(fixture.Presenter.IsChoosingDispatchContinuation, Is.False);
        }

        [Test]
        public void Dispatch_SecondMoveSubmitsAtomicCommandParameters()
        {
            var fixture = CreateFixture();
            SelectFirstMove(fixture);
            fixture.View.Decision.ContinueAction();
            fixture.Presenter.SelectSlot(fixture.SecondSource);
            fixture.Presenter.SelectSlot(fixture.SecondTarget);
            fixture.Presenter.SelectSlot(fixture.SecondTarget);

            var command = fixture.CommandPort.LastCommand;
            Assert.That(command.SourceId, Is.EqualTo(fixture.FirstSource));
            Assert.That(command.TargetId, Is.EqualTo(fixture.FirstTarget));
            Assert.That(command.Parameters["source2"], Is.EqualTo(fixture.SecondSource));
            Assert.That(command.Parameters["target2"], Is.EqualTo(fixture.SecondTarget));
        }

        [Test]
        public void Dispatch_RemoteWaitingKeepsPreviewAndShowsWaitingPrompt()
        {
            var fixture = CreateFixture();
            fixture.CommandPort.NextResult = Success(false);
            SelectFirstMove(fixture);

            fixture.View.Decision.FinishAction();

            Assert.That(fixture.Presenter.HasPendingFirstMove, Is.True);
            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Presenter.IsChoosingDispatchContinuation, Is.True);
            Assert.That(fixture.View.PreviewEnabled, Is.True);
            Assert.That(fixture.View.CompletedAction, Is.Empty);
            Assert.That(fixture.View.Prompt, Does.Contain("等待确认"));
        }

        [Test]
        public void Deploy_RejectedSubmissionShowsReasonAndKeepsWorkflow()
        {
            var fixture = CreateFixture();
            fixture.CommandPort.NextResult = new WorkflowSubmissionResult(
                CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "rejected")),
                false);
            fixture.Presenter.BeginDeploy();
            fixture.Presenter.SelectSlot(fixture.DeployTarget);

            fixture.Presenter.SelectSlot(fixture.DeployTarget);

            Assert.That(fixture.View.Prompt, Is.EqualTo("rejected"));
            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Presenter.IsSelectingDeployTarget, Is.True);
            Assert.That(fixture.View.CompletedAction, Is.Empty);
        }

        [Test]
        public void CancelClearsConfirmationDispatchPreviewDecisionAndHighlights()
        {
            var fixture = CreateFixture();
            SelectFirstMove(fixture);

            fixture.Presenter.Cancel();

            Assert.That(fixture.Presenter.HasPendingFirstMove, Is.False);
            Assert.That(fixture.Presenter.HasPendingConfirmation, Is.False);
            Assert.That(fixture.Presenter.DispatchSourceSlotId, Is.Empty);
            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.ChooseAction));
            Assert.That(fixture.Presenter.IsSelectingDeployTarget, Is.False);
            Assert.That(fixture.Presenter.IsSelectingDispatchSource, Is.False);
            Assert.That(fixture.Presenter.IsSelectingDispatchTarget, Is.False);
            Assert.That(fixture.Presenter.IsChoosingDispatchContinuation, Is.False);
            Assert.That(fixture.Presenter.CurrentPrompt, Is.Empty);
            Assert.That(fixture.View.Decision, Is.Null);
            Assert.That(fixture.View.PreviewEnabled, Is.False);
            Assert.That(fixture.View.Highlights, Is.Empty);
        }

        private static void SelectFirstMove(Fixture fixture)
        {
            fixture.Presenter.BeginDispatch();
            fixture.Presenter.SelectSlot(fixture.FirstSource);
            fixture.Presenter.SelectSlot(fixture.FirstTarget);
            fixture.Presenter.SelectSlot(fixture.FirstTarget);
        }

        private static Fixture CreateFixture()
        {
            var map = new GameMapDefinition
            {
                MapId = "influence-presenter",
                Locations =
                {
                    new MapLocationDefinition { LocationId = "A", InfluenceSlotCount = 2 },
                    new MapLocationDefinition { LocationId = "B", InfluenceSlotCount = 2 },
                    new MapLocationDefinition { LocationId = "C", InfluenceSlotCount = 2 },
                    new MapLocationDefinition { LocationId = "D", InfluenceSlotCount = 2 }
                }
            };
            var state = new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState { PlayerId = 1, InfluenceSupply = 5 },
                    new PlayerState { PlayerId = 2, InfluenceSupply = 5 }
                }
            };
            for (var i = 0; i < map.Locations.Count; i++)
            {
                state.Map.ResourceTokens.Add(new ResourceTokenState
                {
                    LocationId = map.Locations[i].LocationId,
                    ResourceType = ResourceType.Iron
                });
            }

            var firstSource = InfluenceService.GetLocationSlotId("A", 0);
            var secondSource = InfluenceService.GetLocationSlotId("B", 0);
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = firstSource,
                LocationId = "A"
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = secondSource,
                LocationId = "B"
            });

            var context = new FakeContext { State = state };
            var commandPort = new FakeCommandPort { NextResult = Success(true) };
            var view = new FakeView();
            var mapQuery = new MapQueryService(map);
            return new Fixture
            {
                Context = context,
                CommandPort = commandPort,
                View = view,
                FirstSource = firstSource,
                FirstTarget = InfluenceService.GetLocationSlotId("C", 0),
                SecondSource = secondSource,
                SecondTarget = InfluenceService.GetLocationSlotId("D", 0),
                DeployTarget = InfluenceService.GetLocationSlotId("C", 1),
                Presenter = new InfluenceActionPresenter(
                    context,
                    commandPort,
                    view,
                    mapQuery,
                    new InfluenceService(mapQuery))
            };
        }

        private static WorkflowSubmissionResult Success(bool appliedLocally)
        {
            return new WorkflowSubmissionResult(
                CommandResult.SuccessResult(new List<GameEvent>(), "ok"),
                appliedLocally);
        }

        private sealed class Fixture
        {
            public FakeContext Context;
            public FakeCommandPort CommandPort;
            public FakeView View;
            public InfluenceActionPresenter Presenter;
            public string FirstSource;
            public string FirstTarget;
            public string SecondSource;
            public string SecondTarget;
            public string DeployTarget;
        }

        private sealed class FakeContext : IGameplayContext
        {
            public GameState State;

            public GameState CurrentState
            {
                get { return State; }
            }

            public int LocalPlayerId
            {
                get { return 1; }
            }
        }

        private sealed class FakeCommandPort : IGameCommandPort
        {
            public GameCommand LastCommand;
            public WorkflowSubmissionResult NextResult;

            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                LastCommand = command;
                return NextResult;
            }
        }

        private sealed class FakeView : IInfluenceActionView
        {
            public readonly List<WorkflowHighlight> Highlights = new List<WorkflowHighlight>();
            public string Prompt = string.Empty;
            public string CompletedAction = string.Empty;
            public DispatchDecisionViewModel Decision;
            public bool PreviewEnabled;
            public InteractionMode Mode;

            public void SetInteractionMode(InteractionMode mode)
            {
                Mode = mode;
            }

            public void ShowDispatchDecision(DispatchDecisionViewModel viewModel)
            {
                Decision = viewModel;
            }

            public void HideDispatchDecision()
            {
                Decision = null;
            }

            public void RefreshInfluencePreview(
                bool hasPendingFirstMove,
                string firstSourceSlotId,
                string firstTargetSlotId)
            {
                PreviewEnabled = hasPendingFirstMove;
            }

            public void RefreshActionPanel()
            {
            }

            public void CompleteAction(string actionName)
            {
                CompletedAction = actionName;
            }

            public void ShowPrompt(string message)
            {
                Prompt = message;
            }

            public void SetHighlights(IReadOnlyList<WorkflowHighlight> highlights)
            {
                Highlights.Clear();
                if (highlights != null)
                {
                    Highlights.AddRange(highlights);
                }
            }

            public void ClearHighlights()
            {
                Highlights.Clear();
            }

            public bool HasHighlight(string targetId, WorkflowHighlightSemantic semantic)
            {
                for (var i = 0; i < Highlights.Count; i++)
                {
                    if (Highlights[i].TargetId == targetId && Highlights[i].Semantic == semantic)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
