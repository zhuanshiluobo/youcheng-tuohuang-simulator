using System;
using System.Collections.Generic;
using YC.Application.Sessions;

namespace YC.Infrastructure.Multiplayer
{
    public sealed class WaitingRoomConnectionAuthority
    {
        private readonly object syncRoot = new object();
        private readonly SteamIdentityBindingRegistry bindings = new SteamIdentityBindingRegistry();
        private readonly HashSet<int> transportConnections = new HashSet<int>();
        private bool active;

        public bool IsActive
        {
            get { lock (syncRoot) return active; }
        }

        public void Begin(IEnumerable<PlayerSeat> seats)
        {
            lock (syncRoot)
            {
                bindings.Clear();
                transportConnections.Clear();
                active = true;
                bindings.ReplaceLobbySeats(BuildExpectedSeats(seats));
            }
        }

        public IReadOnlyList<int> UpdateLobbySeats(IEnumerable<PlayerSeat> seats)
        {
            lock (syncRoot)
            {
                if (!active) return Array.Empty<int>();
                var removed = bindings.ReplaceLobbySeats(BuildExpectedSeats(seats));
                for (var i = 0; i < removed.Count; i++) transportConnections.Remove(removed[i]);
                return removed;
            }
        }

        public bool ObserveTransportConnection(int connectionId)
        {
            lock (syncRoot)
                return active && connectionId >= 0 && transportConnections.Add(connectionId);
        }

        public bool TryVerifyIdentity(
            int connectionId,
            ulong identity,
            int claimedPlayerId,
            out int playerId)
        {
            lock (syncRoot)
            {
                playerId = -1;
                if (!active || !transportConnections.Contains(connectionId) || identity == 0 ||
                    !bindings.TryBindConnection(connectionId, identity, out playerId))
                    return false;

                if (claimedPlayerId > 0 && claimedPlayerId != playerId)
                {
                    bindings.UnbindConnection(connectionId);
                    playerId = -1;
                    return false;
                }

                return true;
            }
        }

        public bool Disconnect(int connectionId, out int playerId)
        {
            lock (syncRoot)
            {
                playerId = -1;
                if (!active) return false;
                transportConnections.Remove(connectionId);
                bindings.TryGetPlayer(connectionId, out playerId);
                return bindings.UnbindConnection(connectionId);
            }
        }

        public bool TryGetPlayer(int connectionId, out int playerId)
        {
            lock (syncRoot)
            {
                playerId = -1;
                return active && bindings.TryGetPlayer(connectionId, out playerId);
            }
        }

        public void ApplyTo(RoomState room)
        {
            if (room == null || room.Seats == null) return;
            lock (syncRoot)
            {
                for (var i = 0; i < room.Seats.Count; i++)
                {
                    var seat = room.Seats[i];
                    var connectionId = -1;
                    var hasConnection = active && bindings.TryGetConnection(seat.PlayerId, out connectionId);
                    seat.TransportConnected = hasConnection && transportConnections.Contains(connectionId);
                    seat.IdentityVerified = seat.TransportConnected &&
                                            bindings.IsPlayerIdentityVerified(seat.PlayerId, seat.SteamId);
                    seat.NetworkClientId = seat.IdentityVerified ? (ulong)connectionId : 0UL;
                    seat.IsReady = seat.LobbyMemberPresent && seat.TransportConnected && seat.IdentityVerified;
                }
            }
        }

        public bool TryValidateStart(RoomState room, Func<int, bool> isConnectionActive, out string reason)
        {
            ApplyTo(room);
            if (!RoomReadinessPolicy.TryValidateStart(room, out reason)) return false;
            if (isConnectionActive == null) return true;
            for (var i = 0; i < room.Seats.Count; i++)
            {
                var connectionId = (int)room.Seats[i].NetworkClientId;
                if (isConnectionActive(connectionId)) continue;
                reason = "存在正在断开或已经失效的 Mirror 连接。";
                return false;
            }
            return true;
        }

        public void Shutdown()
        {
            lock (syncRoot)
            {
                active = false;
                transportConnections.Clear();
                bindings.Clear();
            }
        }

        private static IEnumerable<KeyValuePair<int, ulong>> BuildExpectedSeats(IEnumerable<PlayerSeat> seats)
        {
            var expected = new List<KeyValuePair<int, ulong>>();
            if (seats == null) return expected;
            foreach (var seat in seats)
            {
                if (seat != null && seat.LobbyMemberPresent && seat.PlayerId > 0 && seat.SteamId > 0)
                    expected.Add(new KeyValuePair<int, ulong>(seat.PlayerId, seat.SteamId));
            }
            return expected;
        }
    }

    public static class RoomReadinessPolicy
    {
        public static bool TryValidateStart(RoomState room, out string reason)
        {
            reason = string.Empty;
            if (room == null || room.Seats == null)
            {
                reason = "房间状态不可用。";
                return false;
            }
            if (room.HasStarted)
            {
                reason = "房间已经开始游戏。";
                return false;
            }
            if (room.PlayerCount < 1 || room.Seats.Count != room.PlayerCount)
            {
                reason = "房间席位数量不完整。";
                return false;
            }

            var playerIds = new HashSet<int>();
            var identities = new HashSet<ulong>();
            var connectionIds = new HashSet<ulong>();
            for (var i = 0; i < room.Seats.Count; i++)
            {
                var seat = room.Seats[i];
                if (seat == null || seat.PlayerId <= 0 || !playerIds.Add(seat.PlayerId))
                {
                    reason = "房间包含无效或重复的席位。";
                    return false;
                }
                if (!seat.LobbyMemberPresent || seat.SteamId == 0 || !identities.Add(seat.SteamId))
                {
                    reason = "等待所有目标席位进入 Lobby。";
                    return false;
                }
                if (!seat.TransportConnected)
                {
                    reason = "等待所有玩家建立 Mirror 传输连接。";
                    return false;
                }
                if (!seat.IdentityVerified || !seat.IsReady)
                {
                    reason = "等待房主完成所有连接身份校验。";
                    return false;
                }
                if (!connectionIds.Add(seat.NetworkClientId))
                {
                    reason = "检测到重复的 Mirror 连接映射。";
                    return false;
                }
                if (seat.NetworkClientId > int.MaxValue)
                {
                    reason = "检测到无效的 Mirror 连接编号。";
                    return false;
                }
            }

            var host = FindSeat(room.Seats, room.HostPlayerId);
            if (host == null || host.NetworkClientId != 0)
            {
                reason = "房主本地 Mirror 连接映射无效。";
                return false;
            }
            return true;
        }

        public static bool TryResolveExpectedBinding(
            IList<PlayerSeat> seats,
            int connectionId,
            ulong identity,
            out int playerId)
        {
            playerId = -1;
            if (seats == null || connectionId < 0 || identity == 0) return false;
            for (var i = 0; i < seats.Count; i++)
            {
                var seat = seats[i];
                if (seat == null || !seat.LobbyMemberPresent || !seat.TransportConnected ||
                    !seat.IdentityVerified || !seat.IsReady || seat.SteamId != identity ||
                    seat.NetworkClientId != (ulong)connectionId)
                    continue;
                playerId = seat.PlayerId;
                return playerId > 0;
            }
            return false;
        }

        private static PlayerSeat FindSeat(IList<PlayerSeat> seats, int playerId)
        {
            for (var i = 0; i < seats.Count; i++)
                if (seats[i] != null && seats[i].PlayerId == playerId) return seats[i];
            return null;
        }
    }

    public static class LobbyStartCommitPolicy
    {
        public static bool TryCommit(
            Func<bool, bool> setJoinable,
            Func<string, bool> setRoomStatus,
            out string reason)
        {
            if (setJoinable == null) throw new ArgumentNullException(nameof(setJoinable));
            if (setRoomStatus == null) throw new ArgumentNullException(nameof(setRoomStatus));
            reason = string.Empty;
            if (!setJoinable(false))
            {
                reason = "无法关闭 Lobby 的继续加入状态。";
                return false;
            }
            if (setRoomStatus(SteamLobbyPolicy.StartedStatus)) return true;
            setJoinable(true);
            reason = "无法更新 Steam Lobby 的开局状态。";
            return false;
        }
    }
}
