using System;
using System.Collections.Generic;
using YC.Domain.Rules;

namespace YC.Domain.Events
{
    [Serializable]
    public sealed class GameEvent
    {
        public string EventId = Guid.NewGuid().ToString("N");
        public GameEventKind Kind;
        public int PlayerId = -1;
        public string SubjectId = string.Empty;
        public string Message = string.Empty;
        public Dictionary<string, string> Data = new Dictionary<string, string>();

        public static GameEvent Log(string message)
        {
            return new GameEvent
            {
                Kind = GameEventKind.LogOnly,
                Message = message
            };
        }
    }
}
