using System;
using System.Collections.Generic;
using YC.Domain.Rules;

namespace YC.Domain.Commands
{
    [Serializable]
    public sealed class GameCommand
    {
        public string CommandId = Guid.NewGuid().ToString("N");
        public GameCommandKind Kind;
        public int PlayerId = -1;
        public string SourceId = string.Empty;
        public string TargetId = string.Empty;
        public List<string> OptionIds = new List<string>();
        public Dictionary<string, string> Parameters = new Dictionary<string, string>();
    }
}
