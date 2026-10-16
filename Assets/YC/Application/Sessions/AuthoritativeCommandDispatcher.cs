using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public enum CommandAuthorityOutcomeKind { Unknown, Accepted, Rejected }

    /// <summary>准确命令结果的有界记录；不保存投影、选项或规则状态，不用于重新执行命令。</summary>
    [Serializable]
    public sealed class CommandAuthorityOutcome
    {
        public const int CurrentProtocolVersion = 1;
        public int ProtocolVersion = CurrentProtocolVersion;
        public string GameId = string.Empty;
        public int PlayerId;
        public string CommandId = string.Empty;
        public CommandAuthorityOutcomeKind Outcome;
        public int AcceptedSequence;
        public int StateRevision;
        public CommandErrorCode ErrorCode;
        public string Reason = string.Empty;
    }

    public sealed class AuthoritativeCommandDispatcher
    {
        private readonly GameSession session;
        private readonly Dictionary<ulong, int> playerIdsByClientId = new Dictionary<ulong, int>();
        private int nextAcceptedSequence = 1;
        private int nextExpectedConfirmedSequence = 1;
        private bool initialStateSynchronized;
        private const int CommandOutcomeCapacity = 512;
        private readonly Dictionary<string, CommandAuthorityOutcome> commandOutcomes = new Dictionary<string, CommandAuthorityOutcome>(StringComparer.Ordinal);
        private readonly Queue<string> commandOutcomeOrder = new Queue<string>();
        private string outcomeGameId;

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

        public int NextExpectedConfirmedSequence => nextExpectedConfirmedSequence;

        public void RegisterClientPlayer(ulong clientId, int playerId)
        {
            if (playerId <= 0)
            {
                return;
            }

            playerIdsByClientId[clientId] = playerId;
            // 重新绑定后，旧连接不能继续查询该玩家的命令结果。
            foreach (var prior in new List<KeyValuePair<ulong, int>>(playerIdsByClientId))
                if (prior.Key != clientId && prior.Value == playerId) playerIdsByClientId.Remove(prior.Key);
        }

        public void UnregisterClientPlayer(ulong clientId) => playerIdsByClientId.Remove(clientId);

        public bool TryGetCommandOutcome(ulong senderClientId, string gameId, int playerId,
            string commandId, out CommandAuthorityOutcome outcome)
        {
            outcome = null;
            ResetCommandOutcomesForCurrentGame();
            if (!playerIdsByClientId.TryGetValue(senderClientId, out var owner) || owner != playerId ||
                playerId <= 0 || string.IsNullOrEmpty(commandId) || string.IsNullOrEmpty(gameId) ||
                !string.Equals(gameId, outcomeGameId, StringComparison.Ordinal) ||
                !commandOutcomes.TryGetValue(CommandOutcomeKey(playerId, commandId), out var recorded)) return false;
            // 查询者不能修改权威记录，也不会触发Submit、费用或Effect。
            outcome = new CommandAuthorityOutcome { GameId = recorded.GameId, PlayerId = recorded.PlayerId,
                CommandId = recorded.CommandId, Outcome = recorded.Outcome, AcceptedSequence = recorded.AcceptedSequence,
                StateRevision = recorded.StateRevision, ErrorCode = recorded.ErrorCode, Reason = recorded.Reason };
            return true;
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
                View = session.ProjectFor(viewer)
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
                View = session.ProjectFor(viewer)
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
            if (snapshot == null || snapshot.ProtocolVersion != GameStateView.CurrentSchemaVersion || snapshot.View == null ||
                snapshot.View.SchemaVersion != GameStateView.CurrentSchemaVersion || !HasMatchingPlayerOrder(snapshot.View))
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Initial game state view payload is empty or unsupported."));
            }

            if (initialStateSynchronized && session.View != null &&
                (snapshot.View.GameId != session.View.GameId ||
                 snapshot.View.StateRevision < session.View.StateRevision ||
                 snapshot.NextConfirmedSequence < nextExpectedConfirmedSequence))
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand, "过期或不同对局的同步快照不能覆盖当前投影。"));
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
            if (confirmed.ProtocolVersion != GameStateView.CurrentSchemaVersion || confirmed.View == null ||
                confirmed.View.SchemaVersion != GameStateView.CurrentSchemaVersion || !HasMatchingPlayerOrder(confirmed.View))
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Confirmed game state view payload is empty or unsupported."));
            }

            if (session.View != null && (confirmed.View.GameId != session.View.GameId ||
                confirmed.View.StateRevision < session.View.StateRevision))
                return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.UnknownCommand,
                    "过期或不同对局的确认不能覆盖当前投影。"));

            session.ReplaceView(confirmed.View);
            nextExpectedConfirmedSequence++;
            return CommandResult.SuccessResult(new List<YC.Domain.Events.GameEvent>(), "Confirmed game state view synchronized.");
        }

        private bool HasMatchingPlayerOrder(GameStateView view)
        {
            if (!view.HasValidPlayerOrderAndSlots()) return false;
            if (session.State.Players.Count == 0) return true;
            var expected = new YC.Domain.Rules.TurnOrderService().GetLaunchPlayerOrder(session.State);
            if (expected.Count != view.LaunchPlayerOrder.Count) return false;
            for (var i = 0; i < expected.Count; i++) if (expected[i] != view.LaunchPlayerOrder[i]) return false;
            return true;
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
                var invalid = CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Network command payload could not be restored: " + ex.Message));
                RecordCommandOutcome(dto, invalid, 0);
                return invalid;
            }

            var result = session.Submit(command);
            if (!result.Succeeded)
            {
                RecordCommandOutcome(dto, result, 0);
                return result;
            }

            int acceptedSequence = nextAcceptedSequence++;
            var acceptedCommand = GameCommandDto.FromCommand(command);
            RecordCommandOutcome(acceptedCommand, result, acceptedSequence);
            var confirmed = new ConfirmedGameCommandDto
            {
                Sequence = acceptedSequence,
                Command = acceptedCommand,
                State = GameStateCloneService.DeepClone(session.State)
            };

            var handler = CommandAccepted;
            if (handler != null)
            {
                handler(confirmed);
            }

            return result;
        }

        private void ResetCommandOutcomesForCurrentGame()
        {
            string gameId = session.State.GameId ?? string.Empty;
            if (string.Equals(outcomeGameId, gameId, StringComparison.Ordinal)) return;
            commandOutcomes.Clear();
            commandOutcomeOrder.Clear();
            outcomeGameId = gameId;
        }

        private static string CommandOutcomeKey(int playerId, string commandId) =>
            playerId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + commandId;

        private void RecordCommandOutcome(GameCommandDto command, CommandResult result, int acceptedSequence)
        {
            ResetCommandOutcomesForCurrentGame();
            if (command == null || command.PlayerId <= 0 || string.IsNullOrEmpty(command.CommandId) ||
                string.IsNullOrEmpty(outcomeGameId)) return;
            string key = CommandOutcomeKey(command.PlayerId, command.CommandId);
            if (commandOutcomes.ContainsKey(key)) return;
            if (commandOutcomes.Count >= CommandOutcomeCapacity) commandOutcomes.Remove(commandOutcomeOrder.Dequeue());
            commandOutcomes.Add(key, new CommandAuthorityOutcome { GameId = outcomeGameId, PlayerId = command.PlayerId,
                CommandId = command.CommandId, Outcome = result.Succeeded ? CommandAuthorityOutcomeKind.Accepted : CommandAuthorityOutcomeKind.Rejected,
                AcceptedSequence = acceptedSequence, StateRevision = session.State.EffectRuntime?.StateRevision ?? 0,
                ErrorCode = result.Validation?.ErrorCode ?? CommandErrorCode.None, Reason = result.Validation?.Reason ?? string.Empty });
            commandOutcomeOrder.Enqueue(key);
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
