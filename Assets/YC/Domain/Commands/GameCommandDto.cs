using System;
using System.Collections.Generic;
using YC.Domain.Rules;

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
                OptionIds = new List<string>(OptionIds)
            };

            for (var i = 0; i < Parameters.Count; i++)
            {
                command.Parameters[Parameters[i].Key] = Parameters[i].Value;
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
}
