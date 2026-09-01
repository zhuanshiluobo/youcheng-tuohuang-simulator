using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Application.Setup;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class NetworkCommandDispatcherTests
    {
        [Test]
        public void GameCommandDto_RoundTrip_PreservesCommandFields()
        {
            var command = new GameCommand
            {
                CommandId = "cmd-1",
                Kind = GameCommandKind.MoveCity,
                PlayerId = 2,
                SourceId = "A-01",
                TargetId = "A-02",
                OptionIds = { "opt-a", "opt-b" },
                Parameters =
                {
                    { "cost", "3" },
                    { "route", "A-01>A-02" }
                }
            };

            var dto = GameCommandDto.FromCommand(command);
            var restored = dto.ToCommand();

            Assert.That(restored.CommandId, Is.EqualTo("cmd-1"));
            Assert.That(restored.Kind, Is.EqualTo(GameCommandKind.MoveCity));
            Assert.That(restored.PlayerId, Is.EqualTo(2));
            Assert.That(restored.SourceId, Is.EqualTo("A-01"));
            Assert.That(restored.TargetId, Is.EqualTo("A-02"));
            Assert.That(restored.OptionIds, Is.EqualTo(new[] { "opt-a", "opt-b" }));
            Assert.That(restored.Parameters["cost"], Is.EqualTo("3"));
            Assert.That(restored.Parameters["route"], Is.EqualTo("A-01>A-02"));
        }

        [Test]
        public void GameCommandDto_ToCommand_ToleratesMissingListsFromNetworkPayload()
        {
            var dto = new GameCommandDto
            {
                CommandId = "cmd-minimal",
                Kind = GameCommandKind.EndAction,
                PlayerId = 1,
                OptionIds = null,
                Parameters = null
            };

            var restored = dto.ToCommand();

            Assert.That(restored.CommandId, Is.EqualTo("cmd-minimal"));
            Assert.That(restored.OptionIds, Is.Empty);
            Assert.That(restored.Parameters, Is.Empty);
        }

        [Test]
        public void ReceiveClientCommand_WhenAuthorizedAndValid_SubmitsAndBroadcastsConfirmedCommand()
        {
            var session = CreateSession();
            var dispatcher = new AuthoritativeCommandDispatcher(session);
            var accepted = new List<ConfirmedGameCommandDto>();
            dispatcher.CommandAccepted += accepted.Add;
            dispatcher.RegisterClientPlayer(42, 2);

            var result = dispatcher.ReceiveClientCommand(42, new GameCommandDto
            {
                CommandId = "cmd-2",
                Kind = GameCommandKind.EndAction,
                PlayerId = 2
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(session.State.Logs, Has.Count.EqualTo(1));
            Assert.That(session.State.Logs[0].CommandId, Is.EqualTo("cmd-2"));
            Assert.That(accepted, Has.Count.EqualTo(1));
            Assert.That(accepted[0].Sequence, Is.EqualTo(1));
            Assert.That(accepted[0].Command.CommandId, Is.EqualTo("cmd-2"));
            Assert.That(accepted[0].State.Logs[0].CommandId, Is.EqualTo("cmd-2"));
        }

        [Test]
        public void ReceiveClientCommand_WhenClientSpoofsAnotherPlayer_RejectsWithoutBroadcast()
        {
            var session = CreateSession();
            var dispatcher = new AuthoritativeCommandDispatcher(session);
            var accepted = new List<ConfirmedGameCommandDto>();
            var rejected = new List<System.Tuple<ulong, RejectedGameCommandDto>>();
            dispatcher.CommandAccepted += accepted.Add;
            dispatcher.CommandRejected += (clientId, dto) => rejected.Add(System.Tuple.Create(clientId, dto));
            dispatcher.RegisterClientPlayer(42, 2);

            var result = dispatcher.ReceiveClientCommand(42, new GameCommandDto
            {
                CommandId = "cmd-spoof",
                Kind = GameCommandKind.EndAction,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidPlayer));
            Assert.That(session.State.Logs, Is.Empty);
            Assert.That(accepted, Is.Empty);
            Assert.That(rejected, Has.Count.EqualTo(1));
            Assert.That(rejected[0].Item1, Is.EqualTo(42));
            Assert.That(rejected[0].Item2.Command.CommandId, Is.EqualTo("cmd-spoof"));
            Assert.That(rejected[0].Item2.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidPlayer));
            Assert.That(rejected[0].Item2.Reason, Is.EqualTo("Network client cannot submit commands for another player."));
        }

        [Test]
        public void ReceiveClientCommand_WhenHostValidationRejects_ReturnsReasonToOriginatingClient()
        {
            var session = new GameSession(new GameState());
            session.RegisterHandler(new RejectingHandler());
            var dispatcher = new AuthoritativeCommandDispatcher(session);
            var accepted = new List<ConfirmedGameCommandDto>();
            var rejected = new List<System.Tuple<ulong, RejectedGameCommandDto>>();
            dispatcher.CommandAccepted += accepted.Add;
            dispatcher.CommandRejected += (clientId, dto) => rejected.Add(System.Tuple.Create(clientId, dto));
            dispatcher.RegisterClientPlayer(42, 2);

            var result = dispatcher.ReceiveClientCommand(42, new GameCommandDto
            {
                CommandId = "cmd-rejected-by-rules",
                Kind = GameCommandKind.EndAction,
                PlayerId = 2
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(result.Validation.Reason, Is.EqualTo(RejectingHandler.Reason));
            Assert.That(session.State.Logs, Is.Empty);
            Assert.That(accepted, Is.Empty);
            Assert.That(rejected, Has.Count.EqualTo(1));
            Assert.That(rejected[0].Item1, Is.EqualTo(42));
            Assert.That(rejected[0].Item2.Command.CommandId, Is.EqualTo("cmd-rejected-by-rules"));
            Assert.That(rejected[0].Item2.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(rejected[0].Item2.Reason, Is.EqualTo(RejectingHandler.Reason));
        }

        [Test]
        public void BuildRejectedCommandPrompt_IncludesHostValidationReason()
        {
            var prompt = NetworkCommandPromptFormatter.BuildRejectedCommandPrompt(new RejectedGameCommandDto
            {
                ErrorCode = CommandErrorCode.InvalidTarget,
                Reason = RejectingHandler.Reason
            });

            Assert.That(prompt, Is.EqualTo("Host rejected the command: " + RejectingHandler.Reason));
        }

        [Test]
        public void RejectClientLocalSubmit_DoesNotSubmitIntoLocalSession()
        {
            var session = CreateSession();
            var dispatcher = new AuthoritativeCommandDispatcher(session);

            var result = dispatcher.RejectClientLocalSubmit(new GameCommandDto
            {
                CommandId = "cmd-local-client",
                Kind = GameCommandKind.EndAction,
                PlayerId = 2
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.UnknownCommand));
            Assert.That(session.State.Logs, Is.Empty);
        }

        [Test]
        public void ApplyConfirmedCommand_WhenSequenceSkips_RejectsWithoutSubmitting()
        {
            var session = CreateSession();
            var dispatcher = new AuthoritativeCommandDispatcher(session);

            var result = dispatcher.ApplyConfirmedCommand(new ConfirmedGameCommandDto
            {
                Sequence = 2,
                Command = new GameCommandDto
                {
                    CommandId = "cmd-out-of-order",
                    Kind = GameCommandKind.EndAction,
                    PlayerId = 2
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.UnknownCommand));
            Assert.That(session.State.Logs, Is.Empty);
        }

        [Test]
        public void ApplyConfirmedCommand_WhenSequenceMatches_ReplacesStateSnapshotWithoutSubmitting()
        {
            var session = new GameSession(new GameState());
            var dispatcher = new AuthoritativeCommandDispatcher(session);

            var result = dispatcher.ApplyConfirmedCommand(new ConfirmedGameCommandDto
            {
                Sequence = 1,
                Command = new GameCommandDto
                {
                    CommandId = "cmd-snapshot-only",
                    Kind = GameCommandKind.EndAction,
                    PlayerId = 2
                },
                State = new GameState
                {
                    Round = 3,
                    Logs =
                    {
                        new GameLogEntry
                        {
                            Sequence = 1,
                            CommandId = "cmd-host-applied",
                            PlayerId = 2,
                            Message = "Host applied the command."
                        }
                    }
                }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(session.State.Round, Is.EqualTo(3));
            Assert.That(session.State.Logs, Has.Count.EqualTo(1));
            Assert.That(session.State.Logs[0].CommandId, Is.EqualTo("cmd-host-applied"));
        }

        [Test]
        public void ApplyConfirmedCommand_WhenStateSnapshotIsMissing_Rejects()
        {
            var session = CreateSession();
            var dispatcher = new AuthoritativeCommandDispatcher(session);

            var result = dispatcher.ApplyConfirmedCommand(new ConfirmedGameCommandDto
            {
                Sequence = 1,
                Command = new GameCommandDto
                {
                    CommandId = "cmd-without-state",
                    Kind = GameCommandKind.EndAction,
                    PlayerId = 2
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.UnknownCommand));
            Assert.That(result.Validation.Reason, Is.EqualTo("Confirmed game state payload is empty."));
            Assert.That(session.State.Logs, Is.Empty);
        }

        [Test]
        public void ApplyConfirmedCommand_WhenSnapshotHasDefaultPendingChoice_ClearsInvalidPendingChoice()
        {
            var session = new GameSession(new GameState());
            var dispatcher = new AuthoritativeCommandDispatcher(session);

            var result = dispatcher.ApplyConfirmedCommand(new ConfirmedGameCommandDto
            {
                Sequence = 1,
                Command = new GameCommandDto
                {
                    CommandId = "cmd-clears-invalid-pending-choice",
                    Kind = GameCommandKind.EndAction,
                    PlayerId = 2
                },
                State = new GameState
                {
                    Phase = GamePhase.ActionRound1,
                    Round = 1,
                    ActionRound = 1,
                    CurrentPlayerId = 2,
                    PendingChoice = new PendingChoiceState()
                }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(session.State.PendingChoice, Is.Null);
            Assert.That(session.State.HasPendingChoice(), Is.False);
            Assert.That(session.State.CurrentPlayerId, Is.EqualTo(2));
        }

        [Test]
        public void EventDeckService_WithSameSeed_InitializesSameDeckOrder()
        {
            var hostDecks = new DeckRuntimeState();
            var clientDecks = new DeckRuntimeState();
            var greenIds = new[] { "event_green_01", "event_green_02", "event_green_03", "event_green_04" };
            var yellowIds = new[] { "event_yellow_01", "event_yellow_02", "event_yellow_03", "event_yellow_04" };
            var redIds = new[] { "event_red_01", "event_red_02", "event_red_03", "event_red_04" };

            new EventDeckService(12345).InitializeDecks(hostDecks, greenIds, yellowIds, redIds);
            new EventDeckService(12345).InitializeDecks(clientDecks, greenIds, yellowIds, redIds);

            Assert.That(clientDecks.EventDeckGreen, Is.EqualTo(hostDecks.EventDeckGreen));
            Assert.That(clientDecks.EventDeckYellow, Is.EqualTo(hostDecks.EventDeckYellow));
            Assert.That(clientDecks.EventDeckRed, Is.EqualTo(hostDecks.EventDeckRed));
        }

        [Test]
        public void StateDtos_JsonRoundTrip_PreservesCompleteCityStyleDeclarationState()
        {
            var state = new GameState
            {
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        DeclaredCityStyles =
                        {
                            new CityStyleDeclarationState
                            {
                                InfluenceMarkerId = "style-marker-1",
                                CityStyleId = CityStyleDatabase.CompositePowerSystem,
                                MarkerArea = CityStyleMarkerAreas.UsesOne,
                                UnlockedSpecialActionId = "special-action-test",
                                RemainingSpecialActionUses = 1,
                                UsedFacilityIds = { "building-a", "building-b" },
                                UsedCityBoardSlotIndexes = { 1, 4 }
                            }
                        }
                    }
                }
            };

            var initialState = CloneJson(new InitialGameStateDto
            {
                NextConfirmedSequence = 7,
                State = state
            });
            var confirmedState = CloneJson(new ConfirmedGameCommandDto
            {
                Sequence = 6,
                State = state
            });

            Assert.That(initialState.NextConfirmedSequence, Is.EqualTo(7));
            AssertCompleteCityStyleDeclaration(initialState.State);
            Assert.That(confirmedState.Sequence, Is.EqualTo(6));
            AssertCompleteCityStyleDeclaration(confirmedState.State);
        }

        [Test]
        public void ApplyConfirmedCommand_WhenInitialStateIsNotSynchronized_RejectsWithoutSubmitting()
        {
            var session = CreateSession();
            var dispatcher = new AuthoritativeCommandDispatcher(session, true);

            var result = dispatcher.ApplyConfirmedCommand(new ConfirmedGameCommandDto
            {
                Sequence = 1,
                Command = new GameCommandDto
                {
                    CommandId = "cmd-before-initial-state",
                    Kind = GameCommandKind.EndAction,
                    PlayerId = 2
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.UnknownCommand));
            Assert.That(session.State.Logs, Is.Empty);
        }

        [Test]
        public void ApplyInitialStateSynchronization_ThenConfirmedEntranceDraw_UsesHostDeckState()
        {
            var hostSession = CreateFourPlayerEntranceSession("event_green_01");
            var clientSession = CreateFourPlayerEntranceSession("event_green_02");
            var hostDispatcher = new AuthoritativeCommandDispatcher(hostSession);
            var clientDispatcher = new AuthoritativeCommandDispatcher(clientSession, true);
            var accepted = new List<ConfirmedGameCommandDto>();
            hostDispatcher.CommandAccepted += accepted.Add;

            var snapshot = CloneJson(hostDispatcher.CreateInitialStateSynchronization());
            var syncResult = clientDispatcher.ApplyInitialStateSynchronization(snapshot);
            Assert.That(syncResult.Succeeded, Is.True);
            Assert.That(clientSession.State.Decks.EventDeckGreen, Is.EqualTo(new[] { "event_green_01" }));

            var hostResult = hostDispatcher.SubmitHostCommand(new GameCommandDto
            {
                CommandId = "cmd-place-host",
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = 1,
                TargetId = "G-01"
            });
            var clientResult = clientDispatcher.ApplyConfirmedCommand(CloneJson(accepted[0]));

            Assert.That(hostResult.Succeeded, Is.True);
            Assert.That(clientResult.Succeeded, Is.True);
            Assert.That(hostSession.State.PendingChoice.CardId, Is.EqualTo("event_green_01"));
            Assert.That(clientSession.State.PendingChoice.CardId, Is.EqualTo(hostSession.State.PendingChoice.CardId));
            Assert.That(clientSession.State.Map.ResourceTokens[0].ResourceType, Is.EqualTo(hostSession.State.Map.ResourceTokens[0].ResourceType));
            Assert.That(clientSession.State.Logs[0].CommandId, Is.EqualTo("cmd-place-host"));
        }

        private static T CloneJson<T>(T source)
        {
            return JsonUtility.FromJson<T>(JsonUtility.ToJson(source));
        }

        private static void AssertCompleteCityStyleDeclaration(GameState state)
        {
            var player = state.FindPlayer(1);
            Assert.That(player, Is.Not.Null);
            Assert.That(player.DeclaredCityStyles, Has.Count.EqualTo(1));
            var declaration = player.DeclaredCityStyles[0];
            Assert.That(declaration.InfluenceMarkerId, Is.EqualTo("style-marker-1"));
            Assert.That(declaration.CityStyleId, Is.EqualTo(CityStyleDatabase.CompositePowerSystem));
            Assert.That(declaration.MarkerArea, Is.EqualTo(CityStyleMarkerAreas.UsesOne));
            Assert.That(declaration.UnlockedSpecialActionId, Is.EqualTo("special-action-test"));
            Assert.That(declaration.RemainingSpecialActionUses, Is.EqualTo(1));
            Assert.That(declaration.UsedFacilityIds, Is.EqualTo(new[] { "building-a", "building-b" }));
            Assert.That(declaration.UsedCityBoardSlotIndexes, Is.EqualTo(new[] { 1, 4 }));
        }

        private static GameSession CreateSession()
        {
            var session = new GameSession(new GameState());
            session.RegisterHandler(new TestHandler());
            return session;
        }

        private static GameSession CreateFourPlayerEntranceSession(string greenCardId)
        {
            var state = new GameState
            {
                Phase = GamePhase.Entrance,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                MapId = StaticMapDefinitions.FourPlayerMapId,
                Players =
                {
                    new PlayerState { PlayerId = 1, Name = "Player 1", Color = PlayerColor.Red },
                    new PlayerState { PlayerId = 2, Name = "Player 2", Color = PlayerColor.Blue },
                    new PlayerState { PlayerId = 3, Name = "Player 3", Color = PlayerColor.Green },
                    new PlayerState { PlayerId = 4, Name = "Player 4", Color = PlayerColor.Yellow }
                },
                Decks =
                {
                    EventDeckGreen = { greenCardId }
                }
            };

            var session = new GameSession(state);
            session.RegisterHandler(new SetupCommandHandler(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap())));
            return session;
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

        private sealed class RejectingHandler : IGameCommandHandler
        {
            public const string Reason = "The target cannot be used by this player.";

            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.EndAction;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, Reason));
            }
        }
    }
}
