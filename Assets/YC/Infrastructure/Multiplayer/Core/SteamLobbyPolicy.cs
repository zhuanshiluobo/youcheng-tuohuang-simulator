using System;
using System.Collections.Generic;

namespace YC.Infrastructure.Multiplayer
{
    public static class SteamLobbyPolicy
    {
        public const uint AppId = 480;
        public const string GameKey = "yc_nomad_city";
        public const string ProtocolVersion = "2";
        public const string WaitingStatus = "waiting";
        public const string StartedStatus = "started";
        public const string SessionKindKey = "sessionKind";
        public const string TwoPlayerValidationSessionKind = "steam_two_player_validation";

        public static bool TryParseLobbyId(string value, out ulong lobbyId)
        {
            return ulong.TryParse(value == null ? string.Empty : value.Trim(), out lobbyId) && lobbyId > 0;
        }

        public static bool IsCompatible(IReadOnlyDictionary<string, string> data, int requestedPlayerCount, out string reason)
        {
            reason = string.Empty;
            if (data == null || !Has(data, "gameKey", GameKey))
            {
                reason = "该 Lobby 不属于游城拓荒。";
                return false;
            }

            if (!Has(data, "protocolVersion", ProtocolVersion))
            {
                reason = "房间协议版本不一致。";
                return false;
            }

            if (Has(data, "roomStatus", StartedStatus))
            {
                reason = "房间已经开始游戏。";
                return false;
            }

            if (!data.TryGetValue("playerCount", out var value) ||
                !int.TryParse(value, out var actualPlayerCount) ||
                !IsSupportedRoomConfiguration(data, actualPlayerCount) ||
                (requestedPlayerCount > 0 && requestedPlayerCount != actualPlayerCount))
            {
                reason = "房间人数配置不匹配。";
                return false;
            }

            if (!data.TryGetValue("hostSteamId", out var hostValue) ||
                !ulong.TryParse(hostValue, out var hostSteamId) ||
                hostSteamId == 0)
            {
                reason = "房间缺少有效的房主 SteamID。";
                return false;
            }

            return true;
        }

        public static bool IsTwoPlayerValidationRoom(
            IReadOnlyDictionary<string, string> data,
            int playerCount)
        {
            return playerCount == 2 &&
                   data != null &&
                   Has(data, SessionKindKey, TwoPlayerValidationSessionKind);
        }

        private static bool IsSupportedRoomConfiguration(
            IReadOnlyDictionary<string, string> data,
            int playerCount)
        {
            return playerCount == 3 ||
                   playerCount == 4 ||
                   IsTwoPlayerValidationRoom(data, playerCount);
        }

        public static string SeatKey(int playerId)
        {
            if (playerId < 1 || playerId > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(playerId));
            }

            return "seat." + playerId;
        }

        public static string TransportConnectedKey(int playerId) => ReadyKey("transportConnected", playerId);
        public static string IdentityVerifiedKey(int playerId) => ReadyKey("identityVerified", playerId);

        private static string ReadyKey(string prefix, int playerId)
        {
            if (playerId < 1 || playerId > 4) throw new ArgumentOutOfRangeException(nameof(playerId));
            return prefix + "." + playerId;
        }

        private static bool Has(IReadOnlyDictionary<string, string> data, string key, string expected)
        {
            return data.TryGetValue(key, out var value) && string.Equals(value, expected, StringComparison.Ordinal);
        }
    }
}
