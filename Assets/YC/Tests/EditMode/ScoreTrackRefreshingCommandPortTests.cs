using NUnit.Framework;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class ScoreTrackRefreshingCommandPortTests
    {
        [TestCase(GameCommandKind.ExploreLocation)]
        [TestCase(GameCommandKind.BuildFacility)]
        [TestCase(GameCommandKind.UseCharacterCard)]
        public void Submit_LocalSuccessfulCardScoreChange_RefreshesScoreTrackImmediately(
            GameCommandKind commandKind)
        {
            var context = CreateContext();
            var refreshCount = 0;
            var inner = new FakeCommandPort(command =>
            {
                context.State.FindPlayer(1).Score += 1;
                return SucceededLocally();
            });
            var port = new ScoreTrackRefreshingCommandPort(
                context,
                inner,
                () => refreshCount += 1);

            var result = port.Submit(new GameCommand
            {
                Kind = commandKind,
                PlayerId = 1
            });

            Assert.That(result.CommandResult.Succeeded, Is.True);
            Assert.That(context.State.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(refreshCount, Is.EqualTo(1));
        }

        [Test]
        public void Submit_LocalSuccessfulCommandWithoutScoreChange_DoesNotRefreshScoreTrack()
        {
            var context = CreateContext();
            var refreshCount = 0;
            var port = new ScoreTrackRefreshingCommandPort(
                context,
                new FakeCommandPort(command => SucceededLocally()),
                () => refreshCount += 1);

            port.Submit(new GameCommand
            {
                Kind = GameCommandKind.BuildFacility,
                PlayerId = 1
            });

            Assert.That(refreshCount, Is.Zero);
        }

        [Test]
        public void Submit_RemoteCommandWaitsForAuthoritativeStateSynchronization()
        {
            var context = CreateContext();
            var refreshCount = 0;
            var port = new ScoreTrackRefreshingCommandPort(
                context,
                new FakeCommandPort(command => new WorkflowSubmissionResult(
                    CommandResult.SuccessResult(null, string.Empty),
                    false)),
                () => refreshCount += 1);

            port.Submit(new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1
            });

            Assert.That(refreshCount, Is.Zero);
        }

        private static FakeContext CreateContext()
        {
            var state = new GameState();
            state.Players.Add(new PlayerState
            {
                PlayerId = 1,
                Score = 0
            });
            return new FakeContext { State = state };
        }

        private static WorkflowSubmissionResult SucceededLocally()
        {
            return new WorkflowSubmissionResult(
                CommandResult.SuccessResult(null, string.Empty),
                true);
        }

        private sealed class FakeContext : IGameplayContext
        {
            public GameState State;
            public GameState CurrentState { get { return State; } }
            public int LocalPlayerId { get { return 1; } }
        }

        private sealed class FakeCommandPort : IGameCommandPort
        {
            private readonly System.Func<GameCommand, WorkflowSubmissionResult> submit;

            public FakeCommandPort(System.Func<GameCommand, WorkflowSubmissionResult> submit)
            {
                this.submit = submit;
            }

            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                return submit(command);
            }
        }
    }
}
