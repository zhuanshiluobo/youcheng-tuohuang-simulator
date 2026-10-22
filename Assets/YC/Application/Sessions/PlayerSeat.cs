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
        public ulong SteamId;
        public ulong NetworkClientId;
        public string OperatorId = string.Empty; // 本地测试后端的稳定用户标识，不是连接票据。
        public string PlayerName = string.Empty;
        public PlayerColor Color;
        public bool LobbyMemberPresent;
        public bool TransportConnected;
        public bool IdentityVerified;
        public bool GameStateSynchronized;
        public bool IsReady;
        public string ContentPackHash = string.Empty;
        public string RuleContractVersion = string.Empty;

        public PlayerSeat Clone() => (PlayerSeat)MemberwiseClone();
    }
}
