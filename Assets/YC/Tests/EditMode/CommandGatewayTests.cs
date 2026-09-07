using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class CommandGatewayTests
    {
        [Test]
        public void Submit_MissingResult_RunsRecoveryCallbacks()
        {
            var steps = new List<string>();
            var gateway = new CommandGateway(new FakeCommandPort());
            var outcome = gateway.Submit(
                new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = 1 },
                new SubmitCallbacks(_ => steps.Add("prompt"), "waiting")
                {
                    BeforeRejectedPrompt = result => { Assert.That(result.Succeeded, Is.False); steps.Add("before"); },
                    AfterRejectedPrompt = _ => steps.Add("after")
                });
            Assert.That(outcome.Kind, Is.EqualTo(SubmitOutcomeKind.NoResult));
            Assert.That(steps, Is.EqualTo(new[] { "before", "prompt", "after" }));
        }

        [Test]
        public void Submit_WhenAppliedLocally_InvokesSuccessCallbackAndReturnsOutcome()
        {
            var commandPort = new FakeCommandPort
            {
                NextResult = Success(true)
            };
            var gateway = new CommandGateway(commandPort);
            var prompt = string.Empty;
            var appliedCount = 0;
            var command = new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = 1 };

            var outcome = gateway.Submit(
                command,
                new SubmitCallbacks(message => prompt = message, "waiting")
                {
                    OnAppliedLocally = result => appliedCount++
                });

            Assert.That(commandPort.LastCommand, Is.SameAs(command));
            Assert.That(outcome.Kind, Is.EqualTo(SubmitOutcomeKind.AppliedLocally));
            Assert.That(outcome.CommandResult, Is.SameAs(commandPort.NextResult.CommandResult));
            Assert.That(appliedCount, Is.EqualTo(1));
            Assert.That(prompt, Is.Empty);
        }

        [Test]
        public void Submit_WhenRejected_RunsCallbacksAroundValidationPrompt()
        {
            var commandPort = new FakeCommandPort
            {
                NextResult = new WorkflowSubmissionResult(
                    CommandResult.Invalid(
                        ValidationResult.Failure(CommandErrorCode.InvalidTarget, "rejected")),
                    false)
            };
            var gateway = new CommandGateway(commandPort);
            var steps = new List<string>();

            var outcome = gateway.Submit(
                new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = 1 },
                new SubmitCallbacks(message => steps.Add("prompt:" + message), "waiting")
                {
                    BeforeRejectedPrompt = result => steps.Add("before"),
                    AfterRejectedPrompt = result => steps.Add("after"),
                    OnAppliedLocally = result => steps.Add("applied")
                });

            Assert.That(outcome.Kind, Is.EqualTo(SubmitOutcomeKind.Rejected));
            Assert.That(steps, Is.EqualTo(new[] { "before", "prompt:rejected", "after" }));
        }

        [Test]
        public void Submit_WhenWaitingForHost_ShowsCentralizedPromptWithoutLocalCallback()
        {
            var commandPort = new FakeCommandPort
            {
                NextResult = Success(false)
            };
            var gateway = new CommandGateway(commandPort);
            var prompt = string.Empty;
            var appliedCount = 0;

            var outcome = gateway.Submit(
                new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = 1 },
                new SubmitCallbacks(
                    message => prompt = message,
                    CommandGateway.BuildWaitingForHostPrompt("结束回合命令"))
                {
                    OnAppliedLocally = result => appliedCount++
                });

            Assert.That(outcome.Kind, Is.EqualTo(SubmitOutcomeKind.WaitingForHost));
            Assert.That(prompt, Is.EqualTo("结束回合命令已发送给主机，等待确认。"));
            Assert.That(appliedCount, Is.Zero);
        }

        [Test]
        public void Submit_WhenPortReturnsNoResult_ShowsConfiguredMissingResultPrompt()
        {
            var gateway = new CommandGateway(new FakeCommandPort());
            var prompt = string.Empty;

            var outcome = gateway.Submit(
                new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = 1 },
                new SubmitCallbacks(message => prompt = message, "waiting")
                {
                    MissingResultPrompt = CommandGateway.BuildMissingResultPrompt("采集命令")
                });

            Assert.That(outcome.Kind, Is.EqualTo(SubmitOutcomeKind.NoResult));
            Assert.That(prompt, Is.EqualTo("采集命令未返回结果。"));
        }

        private static WorkflowSubmissionResult Success(bool appliedLocally)
        {
            return new WorkflowSubmissionResult(
                CommandResult.SuccessResult(new List<GameEvent>(), "ok"),
                appliedLocally);
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
    }
}
