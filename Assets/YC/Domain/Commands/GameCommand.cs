using System;
using System.Collections.Generic;
using YC.Domain.Rules;

namespace YC.Domain.Commands
{
    public interface IGameCommand
    {
        string CommandId { get; }
        GameCommandKind Kind { get; }
        int PlayerId { get; }
    }

    [Serializable]
    public sealed class GameCommand : IGameCommand
    {
        public string CommandId = Guid.NewGuid().ToString("N");
        public GameCommandKind Kind;
        public int PlayerId = -1;
        public string SourceId = string.Empty;
        public string TargetId = string.Empty;
        public List<string> OptionIds = new List<string>();
        public Dictionary<string, string> Parameters = new Dictionary<string, string>();

        string IGameCommand.CommandId
        {
            get { return CommandId; }
        }

        GameCommandKind IGameCommand.Kind
        {
            get { return Kind; }
        }

        int IGameCommand.PlayerId
        {
            get { return PlayerId; }
        }
    }
}
