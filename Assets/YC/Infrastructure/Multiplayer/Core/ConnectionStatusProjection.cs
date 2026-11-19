using System.Collections.Generic;
using YC.Application.Sessions;

namespace YC.Infrastructure.Multiplayer
{
    public enum PlayerConnectionStatus { Unknown, Connected, Disconnected, Reconnecting, Verifying, Synchronizing, LeftRoom }

    public sealed class PlayerConnectionStatusEntry
    {
        public int PlayerId;
        public string PlayerName;
        public PlayerConnectionStatus Status;
    }

    /// <summary>只投影当前已知的连接事实；本机离线时不把最后一份房间快照当成实时状态。</summary>
    public static class ConnectionStatusProjection
    {
        public static IReadOnlyList<PlayerConnectionStatusEntry> Read(LaunchMode mode, int localPlayerId, string gameId,
            IReadOnlyList<PlayerSeat> originalPlayers, RoomState room, bool localConnected, bool localReconnecting, bool recovering)
        {
            var result = new List<PlayerConnectionStatusEntry>();
            if (mode == LaunchMode.Local || originalPlayers == null) return result;
            bool hasCurrentRoom = localConnected && room != null && room.HasStarted && room.GameId == gameId &&
                room.LocalPlayerId == localPlayerId;
            var seats = hasCurrentRoom ? room.Seats : null;
            foreach (var original in originalPlayers)
            {
                if (original == null || original.PlayerId <= 0) continue;
                var status = PlayerConnectionStatus.Unknown;
                if (original.PlayerId == localPlayerId)
                {
                    if (localReconnecting) status = PlayerConnectionStatus.Reconnecting;
                    else if (!localConnected) status = PlayerConnectionStatus.Disconnected;
                    else status = recovering ? PlayerConnectionStatus.Synchronizing : PlayerConnectionStatus.Connected;
                }
                else if (seats != null)
                {
                    var current = seats.Find(seat => seat.PlayerId == original.PlayerId && seat.SteamId == original.SteamId);
                    if (current != null)
                    {
                        if (!current.LobbyMemberPresent) status = PlayerConnectionStatus.LeftRoom;
                        else if (!current.TransportConnected) status = PlayerConnectionStatus.Disconnected;
                        else if (!current.IdentityVerified) status = PlayerConnectionStatus.Verifying;
                        else status = PlayerConnectionStatus.Connected;
                    }
                }
                result.Add(new PlayerConnectionStatusEntry { PlayerId = original.PlayerId, PlayerName = original.PlayerName, Status = status });
            }
            return result;
        }
    }
}
