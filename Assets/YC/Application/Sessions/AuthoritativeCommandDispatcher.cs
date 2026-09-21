using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public sealed class AuthoritativeCommandDispatcher
    {
        private readonly GameSession session;
        private readonly Dictionary<ulong, int> playerIdsByClientId = new Dictionary<ulong, int>();
        private int nextAcceptedSequence = 1;
        private int nextExpectedConfirmedSequence = 1;
        private bool initialStateSynchronized;

        public AuthoritativeCommandDispatcher(GameSession session)
            : this(session, false)
        {
        }

        public AuthoritativeCommandDispatcher(GameSession session, bool requireInitialStateSynchronization)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            initialStateSynchronized = !requireInitialStateSynchronization;
        }

        public event Action<ConfirmedGameCommandDto> CommandAccepted;
        public event Action<ulong, RejectedGameCommandDto> CommandRejected;

        public bool IsInitialStateSynchronized
        {
            get { return initialStateSynchronized; }
        }

        public void RegisterClientPlayer(ulong clientId, int playerId)
        {
            if (playerId <= 0)
            {
                return;
            }

            playerIdsByClientId[clientId] = playerId;
        }

        public CommandResult SubmitHostCommand(GameCommandDto dto)
        {
            return SubmitAuthorityCommand(dto);
        }

        public CommandResult ReceiveClientCommand(ulong senderClientId, GameCommandDto dto)
        {
            if (dto == null)
            {
                return Reject(senderClientId, dto, CommandErrorCode.UnknownCommand, "Network command payload is empty.");
            }

            int authorizedPlayerId;
            if (!playerIdsByClientId.TryGetValue(senderClientId, out authorizedPlayerId))
            {
                return Reject(senderClientId, dto, CommandErrorCode.InvalidPlayer, "Network client is not mapped to a game player.");
            }

            if (authorizedPlayerId != dto.PlayerId)
            {
                return Reject(senderClientId, dto, CommandErrorCode.InvalidPlayer, "Network client cannot submit commands for another player.");
            }

            var result = SubmitAuthorityCommand(dto);
            if (!result.Succeeded)
            {
                RaiseRejected(senderClientId, dto, result);
            }

            return result;
        }

        public CommandResult RejectClientLocalSubmit(GameCommandDto dto)
        {
            return CommandResult.Invalid(ValidationResult.Failure(
                CommandErrorCode.UnknownCommand,
                "Client commands must be sent to the Host and cannot be submitted locally."));
        }

        public InitialGameStateDto CreateInitialStateSynchronization()
        {
            return new InitialGameStateDto
            {
                NextConfirmedSequence = nextAcceptedSequence,
                State = GameStateCloneService.DeepClone(session.State)
            };
        }

        public InitialGameStateViewDto CreateInitialStateViewSynchronization(int viewerPlayerId)
        {
            return CreateInitialStateViewSynchronization(GameStateViewer.Player(viewerPlayerId));
        }

        public InitialGameStateViewDto CreateInitialStateViewSynchronization(GameStateViewer viewer)
        {
            return new InitialGameStateViewDto
            {
                NextConfirmedSequence = nextAcceptedSequence,
                View = GameStateViewProjector.Project(session.State, viewer)
            };
        }

        public ConfirmedGameStateViewDto CreateConfirmedStateViewSynchronization(
            ConfirmedGameCommandDto confirmed,
            GameStateViewer viewer)
        {
            if (confirmed == null) throw new ArgumentNullException(nameof(confirmed));
            return new ConfirmedGameStateViewDto
            {
                Sequence = confirmed.Sequence,
                // 命令本体只回传给命令玩家和 Host；其他连接只靠 view 感知公开结果，
                // 避免通过 command kind/id/OptionIds/参数数量反推出私有交互。
                Command = IsCommandVisibleToViewer(confirmed.Command, viewer)
                    ? CloneCommandDto(confirmed.Command)
                    : null,
                View = GameStateViewProjector.Project(session.State, viewer)
            };
        }

        private static bool IsCommandVisibleToViewer(GameCommandDto command, GameStateViewer viewer)
        {
            return command != null &&
                (viewer.IsHost ||
                 (viewer.Role == GameStateViewerRole.Player && viewer.PlayerId == command.PlayerId));
        }

        private static GameCommandDto CloneCommandDto(GameCommandDto source)
        {
            var clone = new GameCommandDto
            {
                CommandId = source.CommandId ?? string.Empty,
                Kind = source.Kind,
                PlayerId = source.PlayerId,
                SourceId = source.SourceId ?? string.Empty,
                TargetId = source.TargetId ?? string.Empty,
                OptionIds = source.OptionIds == null ? new List<string>() : new List<string>(source.OptionIds)
            };
            if (source.Parameters != null)
            {
                for (int i = 0; i < source.Parameters.Count; i++)
                {
                    GameCommandParameterDto parameter = source.Parameters[i];
                    if (parameter == null) continue;
                    clone.Parameters.Add(new GameCommandParameterDto
                    {
                        Key = parameter.Key ?? string.Empty,
                        Value = parameter.Value ?? string.Empty
                    });
                }
            }
            return clone;
        }

        public CommandResult ApplyInitialStateSynchronization(InitialGameStateDto snapshot)
        {
            if (snapshot == null || snapshot.State == null)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Initial game state payload is empty."));
            }

            session.ReplaceState(snapshot.State);
            nextExpectedConfirmedSequence = Math.Max(1, snapshot.NextConfirmedSequence);
            initialStateSynchronized = true;
            return CommandResult.SuccessResult(new List<YC.Domain.Events.GameEvent>(), "Initial game state synchronized.");
        }

        public CommandResult ApplyInitialStateViewSynchronization(InitialGameStateViewDto snapshot)
        {
            if (snapshot == null || snapshot.View == null || snapshot.View.SchemaVersion <= 0 ||
                snapshot.View.SchemaVersion > GameStateView.CurrentSchemaVersion)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Initial game state view payload is empty or unsupported."));
            }

            session.ReplaceView(snapshot.View);
            nextExpectedConfirmedSequence = Math.Max(1, snapshot.NextConfirmedSequence);
            initialStateSynchronized = true;
            return CommandResult.SuccessResult(new List<YC.Domain.Events.GameEvent>(), "Initial game state view synchronized.");
        }

        public CommandResult ApplyConfirmedCommand(ConfirmedGameCommandDto confirmed)
        {
            if (confirmed == null)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Confirmed command payload is empty."));
            }

            if (!initialStateSynchronized)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Initial game state must be synchronized before applying confirmed commands."));
            }

            if (confirmed.Sequence != nextExpectedConfirmedSequence)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Confirmed command sequence is not continuous."));
            }

            if (confirmed.State == null)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Confirmed game state payload is empty."));
            }

            session.ReplaceState(confirmed.State);
            nextExpectedConfirmedSequence++;
            return CommandResult.SuccessResult(new List<YC.Domain.Events.GameEvent>(), "Confirmed game state synchronized.");
        }

        public CommandResult ApplyConfirmedStateViewSynchronization(ConfirmedGameStateViewDto confirmed)
        {
            if (confirmed == null)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Confirmed game state view payload is empty."));
            }
            if (!initialStateSynchronized)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Initial game state view must be synchronized before applying confirmed views."));
            }
            if (confirmed.Sequence != nextExpectedConfirmedSequence)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Confirmed view sequence is not continuous."));
            }
            if (confirmed.View == null || confirmed.View.SchemaVersion <= 0 ||
                confirmed.View.SchemaVersion > GameStateView.CurrentSchemaVersion)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Confirmed game state view payload is empty or unsupported."));
            }

            session.ReplaceView(confirmed.View);
            nextExpectedConfirmedSequence++;
            return CommandResult.SuccessResult(new List<YC.Domain.Events.GameEvent>(), "Confirmed game state view synchronized.");
        }

        private CommandResult SubmitAuthorityCommand(GameCommandDto dto)
        {
            if (dto == null)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Network command payload is empty."));
            }

            GameCommand command;
            try
            {
                command = dto.ToCommand();
            }
            catch (Exception ex)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Network command payload could not be restored: " + ex.Message));
            }

            var result = session.Submit(command);
            if (!result.Succeeded)
            {
                return result;
            }

            var confirmed = new ConfirmedGameCommandDto
            {
                Sequence = nextAcceptedSequence++,
                Command = GameCommandDto.FromCommand(command),
                State = GameStateCloneService.DeepClone(session.State)
            };

            var handler = CommandAccepted;
            if (handler != null)
            {
                handler(confirmed);
            }

            return result;
        }

        private CommandResult Reject(ulong senderClientId, GameCommandDto dto, CommandErrorCode errorCode, string reason)
        {
            var result = CommandResult.Invalid(ValidationResult.Failure(errorCode, reason));
            RaiseRejected(senderClientId, dto, result);
            return result;
        }

        private void RaiseRejected(ulong senderClientId, GameCommandDto dto, CommandResult result)
        {
            var handler = CommandRejected;
            if (handler != null)
            {
                handler(senderClientId, RejectedGameCommandDto.FromResult(dto, result));
            }
        }
    }
}
