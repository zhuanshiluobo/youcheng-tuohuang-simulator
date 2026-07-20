using System;
using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public sealed class GameSession
    {
        private readonly List<IGameCommandHandler> commandHandlers = new List<IGameCommandHandler>();

        public GameState State { get; private set; }

        public GameSession(GameState initialState)
        {
            State = initialState ?? throw new ArgumentNullException(nameof(initialState));
            ClearInvalidPendingChoice();
        }

        public void RegisterHandler(IGameCommandHandler handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            commandHandlers.Add(handler);
        }

        public void ReplaceState(GameState state)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            ClearInvalidPendingChoice();
        }

        private void ClearInvalidPendingChoice()
        {
            if (State.PendingChoice != null && !State.PendingChoice.IsValid())
            {
                State.PendingChoice = null;
            }

            if (State.PendingCardSession != null && !State.PendingCardSession.IsValid())
            {
                State.PendingCardSession = null;
            }
        }

        public CommandResult Submit(GameCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            if (State.HasPendingChoice() &&
                !IsPendingChoiceResolutionCommand(command) &&
                !IsConfirmedSecondCharacterEffectCommand(State, command))
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    Domain.Rules.CommandErrorCode.PendingChoiceRequired,
                    "请先处理待选择项，再提交其他行动。"));
            }

            CommandResult deferredPendingChoiceResult = null;
            for (var i = 0; i < commandHandlers.Count; i++)
            {
                if (!commandHandlers[i].CanHandle(command))
                {
                    continue;
                }

                var result = commandHandlers[i].Handle(State, command);
                if (command.Kind == Domain.Rules.GameCommandKind.ResolvePendingChoice &&
                    !result.Succeeded &&
                    result.Validation != null &&
                    result.Validation.ErrorCode == Domain.Rules.CommandErrorCode.PendingChoiceRequired)
                {
                    if (deferredPendingChoiceResult == null)
                    {
                        deferredPendingChoiceResult = result;
                    }

                    continue;
                }

                AppendLog(command, result);
                return result;
            }

            var invalid = ValidationResult.Failure(Domain.Rules.CommandErrorCode.UnknownCommand, "没有为该命令注册处理器。");
            if (deferredPendingChoiceResult != null)
            {
                return deferredPendingChoiceResult;
            }

            return CommandResult.Invalid(invalid);
        }

        private static bool IsPendingChoiceResolutionCommand(GameCommand command)
        {
            return command != null &&
                   (command.Kind == Domain.Rules.GameCommandKind.ResolvePendingChoice ||
                    command.Kind == Domain.Rules.GameCommandKind.ResolveEntranceEvent);
        }

        private static bool IsConfirmedSecondCharacterEffectCommand(GameState state, GameCommand command)
        {
            if (state == null || command == null ||
                command.Kind != Domain.Rules.GameCommandKind.UseCharacterCard ||
                (state.PendingChoice != null && state.PendingChoice.IsValid()) ||
                (state.PendingCardSession != null && state.PendingCardSession.IsValid()))
            {
                return false;
            }

            var pending = state.PendingCharacterEffect;
            if (pending == null || !pending.IsValid() ||
                pending.ChoiceType != CharacterPendingChoiceTypes.SecondEffectExecution ||
                pending.PlayerId != command.PlayerId)
            {
                return false;
            }

            string cardId;
            if (command.Parameters == null || !command.Parameters.TryGetValue("cardId", out cardId) ||
                string.IsNullOrEmpty(cardId))
            {
                cardId = command.TargetId;
            }

            string effectMode;
            if (command.Parameters == null || !command.Parameters.TryGetValue("effectMode", out effectMode))
            {
                effectMode = string.Empty;
            }

            return cardId == pending.CardId && effectMode == pending.RemainingEffectMode;
        }

        private void AppendLog(GameCommand command, CommandResult result)
        {
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.LogMessage))
            {
                return;
            }

            State.Logs.Add(new GameLogEntry
            {
                Sequence = State.Logs.Count + 1,
                CommandId = command.CommandId,
                PlayerId = command.PlayerId,
                Message = result.LogMessage
            });
        }
    }

    public interface IGameCommandHandler
    {
        bool CanHandle(GameCommand command);
        CommandResult Handle(GameState state, GameCommand command);
    }
}
