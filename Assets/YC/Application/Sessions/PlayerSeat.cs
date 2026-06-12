using System;
using YC.Domain.Rules;

namespace YC.Application.Sessions
{
    public enum LaunchMode
    {
        Local,
        Host,
        Client
    }

    [Serializable]
    public sealed class PlayerSeat
    {
        public int PlayerId;
        public ulong NetcodeClientId;
        public string PlayerName = string.Empty;
        public PlayerColor Color;
        public bool IsReady;
    }
}
