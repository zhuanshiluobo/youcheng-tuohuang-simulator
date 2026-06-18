using System;
using System.Collections.Generic;
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
        }

        public CommandResult Submit(GameCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            for (var i = 0; i < commandHandlers.Count; i++)
            {
                if (!commandHandlers[i].CanHandle(command))
                {
                    continue;
                }

                var result = commandHandlers[i].Handle(State, command);
                AppendLog(command, result);
                return result;
            }

            var invalid = ValidationResult.Failure(Domain.Rules.CommandErrorCode.UnknownCommand, "没有为该命令注册处理器。");
            return CommandResult.Invalid(invalid);
        }

        private void AppendLog(GameCommand command, CommandResult result)
        {
            if (!result.Succeeded)
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
