using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class GameSessionTests
    {
        [Test]
        public void Submit_WithRegisteredHandler_AppendsSuccessfulCommandLog()
        {
            var session = new GameSession(new GameState());
            session.RegisterHandler(new TestHandler());

            var result = session.Submit(new GameCommand
            {
                Kind = GameCommandKind.EndAction,
                PlayerId = 0
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(session.State.Logs, Has.Count.EqualTo(1));
            Assert.That(session.State.Logs[0].Message, Is.EqualTo("handled"));
        }

        [Test]
        public void Submit_WithoutHandler_ReturnsUnknownCommandWithoutMutatingLog()
        {
            var session = new GameSession(new GameState());

            var result = session.Submit(new GameCommand
            {
                Kind = GameCommandKind.EndAction,
                PlayerId = 0
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.UnknownCommand));
            Assert.That(session.State.Logs, Is.Empty);
        }

        private sealed class TestHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.EndAction;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                return CommandResult.SuccessResult(new List<GameEvent>(), "handled");
            }
        }
    }
}
