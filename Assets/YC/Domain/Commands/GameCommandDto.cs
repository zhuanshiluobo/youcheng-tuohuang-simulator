using System;
using System.Collections.Generic;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Commands
{
    [Serializable]
    public sealed class GameCommandDto
    {
        public string CommandId = string.Empty;
        public GameCommandKind Kind;
        public int PlayerId = -1;
        public string SourceId = string.Empty;
        public string TargetId = string.Empty;
        public List<string> OptionIds = new List<string>();
        public List<GameCommandParameterDto> Parameters = new List<GameCommandParameterDto>();

        public static GameCommandDto FromCommand(GameCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            var dto = new GameCommandDto
            {
                CommandId = command.CommandId,
                Kind = command.Kind,
                PlayerId = command.PlayerId,
                SourceId = command.SourceId,
                TargetId = command.TargetId,
                OptionIds = new List<string>(command.OptionIds)
            };

            foreach (var pair in command.Parameters)
            {
                dto.Parameters.Add(new GameCommandParameterDto
                {
                    Key = pair.Key,
                    Value = pair.Value
                });
            }

            return dto;
        }

        public GameCommand ToCommand()
        {
            var command = new GameCommand
            {
                CommandId = CommandId,
                Kind = Kind,
                PlayerId = PlayerId,
                SourceId = SourceId,
                TargetId = TargetId,
                OptionIds = OptionIds == null ? new List<string>() : new List<string>(OptionIds)
            };

            if (Parameters == null)
            {
                return command;
            }

            for (var i = 0; i < Parameters.Count; i++)
            {
                var parameter = Parameters[i];
                if (parameter == null || string.IsNullOrEmpty(parameter.Key))
                {
                    continue;
                }

                command.Parameters[parameter.Key] = parameter.Value ?? string.Empty;
            }

            return command;
        }
    }

    [Serializable]
    public sealed class GameCommandParameterDto
    {
        public string Key = string.Empty;
        public string Value = string.Empty;
    }

    [Serializable]
    public sealed class ConfirmedGameCommandDto
    {
        public int Sequence;
        public GameCommandDto Command = new GameCommandDto();
        public GameState State;
    }

    [Serializable]
    public sealed class RejectedGameCommandDto
    {
        public GameCommandDto Command = new GameCommandDto();
        public CommandErrorCode ErrorCode = CommandErrorCode.UnknownCommand;
        public string Reason = string.Empty;

        public static RejectedGameCommandDto FromResult(GameCommandDto command, CommandResult result)
        {
            var validation = result == null ? null : result.Validation;
            return new RejectedGameCommandDto
            {
                Command = command ?? new GameCommandDto(),
                ErrorCode = validation == null ? CommandErrorCode.UnknownCommand : validation.ErrorCode,
                Reason = validation == null ? string.Empty : validation.Reason
            };
        }
    }
}
