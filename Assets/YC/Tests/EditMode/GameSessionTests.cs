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

        [Test]
        public void Constructor_WithInvalidPendingChoice_ClearsPendingChoice()
        {
            var state = new GameState
            {
                PendingChoice = new PendingChoiceState
                {
                    ChoiceType = "test_choice",
                    PlayerId = 1
                }
            };

            var session = new GameSession(state);

            Assert.That(session.State.PendingChoice, Is.Null);
        }

        [Test]
        public void Submit_WithPendingChoice_BlocksNonResolutionCommandBeforeHandler()
        {
            var session = new GameSession(new GameState
            {
                PendingChoice = new PendingChoiceState
                {
                    ChoiceType = "test_choice",
                    PlayerId = 1,
                    CardId = "test_card",
                    OptionIds = { "option_1" }
                }
            });
            var handler = new TestHandler();
            session.RegisterHandler(handler);

            var result = session.Submit(new GameCommand
            {
                Kind = GameCommandKind.EndAction,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.PendingChoiceRequired));
            Assert.That(handler.HandleCount, Is.EqualTo(0));
            Assert.That(session.State.Logs, Is.Empty);
        }

        [Test]
        public void Submit_WithPendingChoice_AllowsResolvePendingChoiceAndContinuesPastMismatchedHandler()
        {
            var session = new GameSession(new GameState
            {
                PendingChoice = new PendingChoiceState
                {
                    ChoiceType = "test_choice",
                    PlayerId = 1,
                    CardId = "test_card",
                    OptionIds = { "option_1" }
                }
            });
            session.RegisterHandler(new MismatchedPendingChoiceHandler());
            session.RegisterHandler(new ResolvePendingChoiceHandler());

            var result = session.Submit(new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(session.State.Logs, Has.Count.EqualTo(1));
            Assert.That(session.State.Logs[0].Message, Is.EqualTo("resolved"));
        }

        private sealed class TestHandler : IGameCommandHandler
        {
            public int HandleCount { get; private set; }

            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.EndAction;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                HandleCount += 1;
                return CommandResult.SuccessResult(new List<GameEvent>(), "handled");
            }
        }

        private sealed class MismatchedPendingChoiceHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.ResolvePendingChoice;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.PendingChoiceRequired,
                    "not this pending choice"));
            }
        }

        private sealed class ResolvePendingChoiceHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.ResolvePendingChoice;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                state.PendingChoice = null;
                return CommandResult.SuccessResult(new List<GameEvent>(), "resolved");
            }
        }
    }
}
