using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
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
        public void Submit_ExploreLocation_LogsRewardScoreAndInfluenceAfterSettlement()
        {
            var state = CreatePublicLogState();
            var session = new GameSession(state);
            session.RegisterHandler(new DetailedExploreHandler());

            var result = session.Submit(new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "D-01"
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Logs, Has.Count.EqualTo(1));
            Assert.That(
                state.Logs[0].Message,
                Is.EqualTo(
                    "\u63a2\u7d22\u4e86\u5730\u70b9 D-01\uff0c\u5e76\u7ed3\u7b97\u4e86\u4e8b\u4ef6\u201c\u5f02\u94c1\u5f00\u91c7\u201d\uff1a" +
                    "\u83b7\u5f97\u4e86\u94c1\u00d71\uff1b\u83b7\u5f97\u4e86 2 \u5206\uff1b" +
                    "\u5728\u5730\u70b9 D-01\u653e\u7f6e\u4e86\u5f71\u54cd\u529b\u3002"));
        }

        [Test]
        public void Submit_ResolveEntranceEvent_LogsExactReward()
        {
            var state = CreatePublicLogState();
            var session = new GameSession(state);
            session.RegisterHandler(new DetailedEntranceHandler());

            var result = session.Submit(new GameCommand
            {
                Kind = GameCommandKind.ResolveEntranceEvent,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Logs, Has.Count.EqualTo(1));
            Assert.That(
                state.Logs[0].Message,
                Is.EqualTo(
                    "\u7ed3\u7b97\u4e86\u5165\u573a\u4e8b\u4ef6\u201c\u77ff\u4e1a\u805a\u843d\u201d\uff1a" +
                    "\u83b7\u5f97\u4e86\u6e90\u77f3\u788e\u7247\u00d71\u3002"));
        }

        [Test]
        public void Submit_LiskarmBothEffects_LogsStrategyAndTacticPublicOutcomes()
        {
            var state = CreatePublicLogState();
            state.FindPlayer(1).Resources.GoldVoucher = 10;
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = "location:C-01:0",
                LocationId = "C-01"
            });
            var session = new GameSession(state);
            session.RegisterHandler(new DeferredLiskarmHandler());
            session.RegisterHandler(new ResolveLiskarmHandler());

            var use = session.Submit(new GameCommand
            {
                CommandId = "liskarm-use",
                Kind = GameCommandKind.UseCharacterCard,
                PlayerId = 1,
                TargetId = "character.blue.p1.liskarm",
                Parameters =
                {
                    { "cardId", "character.blue.p1.liskarm" },
                    { "effectMode", CharacterEffectModes.Both }
                }
            });

            Assert.That(use.Succeeded, Is.True);
            Assert.That(state.Logs, Is.Empty);

            var resolve = session.Submit(new GameCommand
            {
                CommandId = "liskarm-resolve",
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1
            });

            Assert.That(resolve.Succeeded, Is.True);
            Assert.That(state.Logs, Has.Count.EqualTo(1));
            Assert.That(
                state.Logs[0].Message,
                Is.EqualTo(
                    "\u53d1\u52a8\u4e86\u89d2\u8272\u724c\u201c\u96f7\u86c7\u201d\u7684\u7b56\u7565\u6548\u679c\uff1a" +
                    "\u5728\u5730\u70b9 A-01 \u548c\u5730\u70b9 B-01\u653e\u7f6e\u4e86\u5f71\u54cd\u529b\uff1b" +
                    "\u53d1\u52a8\u4e86\u89d2\u8272\u724c\u201c\u96f7\u86c7\u201d\u7684\u8ba1\u8c0b\u6548\u679c\uff1a" +
                    "\u66ff\u6362\u4e86\u5730\u70b9 C-01\u7684 Player 2 \u5f71\u54cd\u529b\uff0c\u5e76" +
                    "\u652f\u4ed8\u4e86\u91d1\u5238\u00d73\u3002"));
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

        private sealed class DetailedExploreHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.ExploreLocation;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                state.FindPlayer(command.PlayerId).Resources.Iron += 1;
                state.FindPlayer(command.PlayerId).Score += 2;
                state.Map.Influences.Add(new InfluencePlacement
                {
                    PlayerId = command.PlayerId,
                    SlotId = "location:D-01:0",
                    LocationId = "D-01"
                });
                return CommandResult.SuccessResult(new List<GameEvent>
                {
                    new GameEvent
                    {
                        Kind = GameEventKind.CardMoved,
                        PlayerId = command.PlayerId,
                        SubjectId = "event.test",
                        Data =
                        {
                            { "cardName", "\u5f02\u94c1\u5f00\u91c7" },
                            { "targetLocationId", "D-01" }
                        }
                    },
                    new GameEvent
                    {
                        Kind = GameEventKind.ResourceChanged,
                        PlayerId = command.PlayerId,
                        Data =
                        {
                            { "rewardOriginium", "0" },
                            { "rewardOriginiumShard", "0" },
                            { "rewardIron", "1" },
                            { "rewardPureOriginium", "0" },
                            { "rewardGoldVoucher", "0" }
                        }
                    }
                }, "explored");
            }
        }

        private sealed class DetailedEntranceHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.ResolveEntranceEvent;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                state.FindPlayer(command.PlayerId).Resources.OriginiumShard += 1;
                return CommandResult.SuccessResult(new List<GameEvent>
                {
                    new GameEvent
                    {
                        Kind = GameEventKind.ChoiceResolved,
                        PlayerId = command.PlayerId,
                        SubjectId = "event.entrance",
                        Data =
                        {
                            { "cardName", "\u77ff\u4e1a\u805a\u843d" },
                            { "rewardOriginium", "0" },
                            { "rewardOriginiumShard", "1" },
                            { "rewardIron", "0" },
                            { "rewardPureOriginium", "0" },
                            { "rewardGoldVoucher", "0" }
                        }
                    }
                }, "resolved entrance");
            }
        }

        private sealed class DeferredLiskarmHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.UseCharacterCard;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                state.Map.Influences.Add(new InfluencePlacement
                {
                    PlayerId = command.PlayerId,
                    SlotId = "location:A-01:0",
                    LocationId = "A-01"
                });
                state.Map.Influences.Add(new InfluencePlacement
                {
                    PlayerId = command.PlayerId,
                    SlotId = "location:B-01:0",
                    LocationId = "B-01"
                });
                state.PendingCharacterEffect = new PendingCharacterEffectState
                {
                    ChoiceType = "test.liskarm.tactic",
                    PlayerId = command.PlayerId,
                    CardId = command.TargetId,
                    OptionIds = { "confirm" }
                };
                return CommandResult.SuccessResult(new List<GameEvent>(), string.Empty);
            }
        }

        private sealed class ResolveLiskarmHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.ResolvePendingChoice;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                for (var i = 0; i < state.Map.Influences.Count; i++)
                {
                    if (state.Map.Influences[i].SlotId == "location:C-01:0")
                    {
                        state.Map.Influences[i].PlayerId = command.PlayerId;
                        break;
                    }
                }

                state.FindPlayer(command.PlayerId).Resources.GoldVoucher -= 3;
                state.PendingCharacterEffect = null;
                return CommandResult.SuccessResult(new List<GameEvent>
                {
                    new GameEvent
                    {
                        Kind = GameEventKind.CardMoved,
                        PlayerId = command.PlayerId,
                        SubjectId = "character.blue.p1.liskarm"
                    }
                }, "resolved liskarm");
            }
        }

        private static GameState CreatePublicLogState()
        {
            return new GameState
            {
                Players = new List<PlayerState>
                {
                    new PlayerState { PlayerId = 1, Name = "Player 1", Color = PlayerColor.Blue },
                    new PlayerState { PlayerId = 2, Name = "Player 2", Color = PlayerColor.Red }
                }
            };
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
