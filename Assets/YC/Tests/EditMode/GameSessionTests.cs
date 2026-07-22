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
        public void Submit_WithEmptySuccessfulMessage_DoesNotAppendPrematureLog()
        {
            var session = new GameSession(new GameState());
            session.RegisterHandler(new EmptyLogHandler());

            var result = session.Submit(new GameCommand
            {
                Kind = GameCommandKind.EndAction,
                PlayerId = 0
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(session.State.Logs, Is.Empty);
        }

        [Test]
        public void Submit_PublicActionWithPendingChoice_PublishesChineseLogAfterSettlement()
        {
            var state = new GameState();
            var session = new GameSession(state);
            session.RegisterHandler(new DeferredBuildHandler());
            session.RegisterHandler(new ResolvePendingChoiceHandler());

            var build = session.Submit(new GameCommand
            {
                CommandId = "build-command",
                Kind = GameCommandKind.BuildFacility,
                PlayerId = 1,
                TargetId = "building-test"
            });

            Assert.That(build.Succeeded, Is.True);
            Assert.That(state.Logs, Is.Empty, "\u5efa\u7b51\u6548\u679c\u5c1a\u672a\u7ed3\u7b97\u65f6\u4e0d\u5e94\u63d0\u524d\u53d1\u5e03\u65e5\u5fd7\u3002");

            var resolve = session.Submit(new GameCommand
            {
                CommandId = "resolve-command",
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1
            });

            Assert.That(resolve.Succeeded, Is.True);
            Assert.That(state.Logs, Has.Count.EqualTo(1));
            Assert.That(state.Logs[0].CommandId, Is.EqualTo("resolve-command"));
            Assert.That(state.Logs[0].Message, Is.EqualTo("\u5efa\u9020\u4e86\u5efa\u7b51\u201c\u6d4b\u8bd5\u5efa\u7b51\u201d\uff08\u57ce\u5e02\u9762\u677f\u7b2c 2 \u683c\uff09\u3002"));
        }

        [Test]
        public void Submit_RealEndActionEvent_DoesNotPublishVagueLog()
        {
            var session = new GameSession(new GameState());
            session.RegisterHandler(new PlayerAdvancedHandler());

            var result = session.Submit(new GameCommand
            {
                Kind = GameCommandKind.EndAction,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(session.State.Logs, Is.Empty);
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

        private sealed class EmptyLogHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.EndAction;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                return CommandResult.SuccessResult(new List<GameEvent>(), string.Empty);
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

        private sealed class DeferredBuildHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.BuildFacility;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                state.PendingChoice = new PendingChoiceState
                {
                    ChoiceType = "test_build_effect",
                    PlayerId = command.PlayerId,
                    CardId = command.TargetId,
                    OptionIds = { "confirm" }
                };
                return CommandResult.SuccessResult(new List<GameEvent>
                {
                    new GameEvent
                    {
                        Kind = GameEventKind.FacilityBuilt,
                        PlayerId = command.PlayerId,
                        SubjectId = command.TargetId,
                        Data =
                        {
                            { "facilityName", "\u6d4b\u8bd5\u5efa\u7b51" },
                            { "cityBoardSlotIndex", "1" }
                        }
                    }
                }, "Player 1 built test building.");
            }
        }

        private sealed class PlayerAdvancedHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.EndAction;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                return CommandResult.SuccessResult(new List<GameEvent>
                {
                    new GameEvent
                    {
                        Kind = GameEventKind.PlayerAdvanced,
                        PlayerId = command.PlayerId
                    }
                }, "Player 1 ended their action.");
            }
        }
    }
}
