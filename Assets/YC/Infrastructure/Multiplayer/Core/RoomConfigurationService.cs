using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using YC.Application.Sessions;
using YC.Domain.Maps;
using YC.Domain.Rules;

namespace YC.Infrastructure.Multiplayer
{
    /// <summary>只由适配器的已验证 Host 调用。所有成员请求在运输层绑定真实连接身份。</summary>
    public sealed class RoomConfigurationService
    {
        private readonly IRoomContentCapabilityProvider content;
        // 由可信的本地适配器选择，绝不从客户端请求或房间快照读取。
        private readonly bool localPlayers;
        private readonly Func<int, int> nextRandomIndex;
        private readonly object randomGate = new object();
        private readonly Func<int> seedSource;
        private readonly ConditionalWeakTable<RoomState, List<Receipt>> histories = new ConditionalWeakTable<RoomState, List<Receipt>>();
        private sealed class Receipt { public string Key; public Type RequestType; public bool Success; public string Reason; }
        public RoomConfigurationService(IRoomContentCapabilityProvider content = null, bool localPlayers = false,
            Func<int, int> nextRandomIndex = null, Func<int> seedSource = null)
        {
            this.content = content; this.localPlayers = localPlayers;
            this.nextRandomIndex = nextRandomIndex;
            this.seedSource = seedSource ?? (() => GameplayRandomSeed.CreateMatchSeed());
        }
        public static PlayerColor GetSeatColor(int index)
        {
            switch (index)
            {
                case 0: return PlayerColor.Green;
                case 1: return PlayerColor.Yellow;
                case 2: return PlayerColor.Blue;
                case 3: return PlayerColor.Red;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public RoomConfigurationResult ChangeConfiguration(RoomState room, ChangeRoomConfigurationRequest request) =>
            Execute(room, request, true, () =>
            {
                var next = room.Configuration.Clone();
                next.MapId = request.MapId;
                next.RuleModeId = request.RuleModeId;
                next.EnabledExpansionIds = request.EnabledExpansionIds == null ? null : new List<string>(request.EnabledExpansionIds);
                next.PlayerCount = room.PlayerCount;
                if (!next.TryValidate(out var reason)) return reason;
                next.EnabledExpansionIds.Sort(StringComparer.Ordinal);
                var previous = new List<string>(room.Configuration.EnabledExpansionIds);
                previous.Sort(StringComparer.Ordinal);
                if (next.MapId == room.Configuration.MapId && next.RuleModeId == room.Configuration.RuleModeId &&
                    string.Join("|", previous) == string.Join("|", next.EnabledExpansionIds)) return string.Empty;
                next.ContentPackHash = content?.GetContentPackHash(next) ?? string.Empty;
                room.Configuration = next;
                var host = FindSlot(room, room.HostPlayerId)?.Occupant;
                if (host != null) { host.ContentPackHash = next.ContentPackHash; host.RuleContractVersion = next.RuleContractVersion; }
                InvalidatePreparation(room);
                return string.Empty;
            });

        public RoomConfigurationResult SetReady(RoomState room, SetRoomReadyRequest request) =>
            Execute(room, request, false, () =>
            {
                var slot = FindSlot(room, request.RequestingPlayerId);
                if (slot == null) return "service_unavailable";
                if (request.IsUserReady)
                {
                    if (!slot.Occupant.IsReady || !slot.Occupant.TransportConnected || !slot.Occupant.IdentityVerified)
                        return "player_not_ready";
                    if (!VersionsMatch(room.Configuration, slot.Occupant)) return "content_version_mismatch";
                }
                if (slot.IsUserReady != request.IsUserReady)
                {
                    slot.IsUserReady = request.IsUserReady;
                    slot.ReadyConfigurationRevision = request.IsUserReady ? room.Configuration.ConfigurationRevision : -1;
                    room.StateRevision++;
                }
                return string.Empty;
            });

        public RoomConfigurationResult SetSeatStatus(RoomState room, SetRoomSeatStatusRequest request) =>
            Execute(room, request, true, () =>
            {
                if (request.SeatIndex < 0 || request.SeatIndex >= 4 || (request.Status != "open" && request.Status != "closed"))
                    return "room_protocol_mismatch";
                var slot = room.SeatSlots[request.SeatIndex];
                if (slot.Occupant != null) return "seat_occupied";
                if (slot.Status != request.Status) { slot.Status = request.Status; InvalidatePreparation(room); }
                return string.Empty;
            });

        public RoomConfigurationResult RequestSeatSwap(RoomState room, RequestRoomSeatSwapRequest request) =>
            Execute(room, request, false, () =>
            {
                var source = FindSlot(room, request.RequestingPlayerId);
                if (source == null || request.TargetSeatIndex < 0 || request.TargetSeatIndex >= 4) return "seat_swap_stale";
                var target = room.SeatSlots[request.TargetSeatIndex];
                if (target.Status == "closed") return "seat_occupied";
                if (source == target) return string.Empty;
                if (target.Occupant == null || localPlayers && request.RequestingPlayerId == room.HostPlayerId)
                { Swap(room, source, target); return string.Empty; }
                room.PendingSeatSwaps.RemoveAll(s => s.RequestingPlayerId == request.RequestingPlayerId);
                room.PendingSeatSwaps.Add(new RoomSeatSwapState
                {
                    SwapRequestId = request.CommandId, RequestingPlayerId = request.RequestingPlayerId,
                    TargetPlayerId = target.Occupant.PlayerId, SourceSeatIndex = source.SeatIndex,
                    TargetSeatIndex = target.SeatIndex, ConfigurationRevision = room.Configuration.ConfigurationRevision
                });
                room.StateRevision++;
                return string.Empty;
            });

        public RoomConfigurationResult ConfirmSeatSwap(RoomState room, ConfirmRoomSeatSwapRequest request) =>
            Execute(room, request, false, () =>
            {
                var swap = room.PendingSeatSwaps.Find(s => s.SwapRequestId == request.SwapRequestId);
                if (swap == null || swap.TargetPlayerId != request.RequestingPlayerId ||
                    swap.ConfigurationRevision != room.Configuration.ConfigurationRevision) return "seat_swap_stale";
                var source = room.SeatSlots[swap.SourceSeatIndex];
                var target = room.SeatSlots[swap.TargetSeatIndex];
                if (source.Occupant?.PlayerId != swap.RequestingPlayerId || target.Occupant?.PlayerId != swap.TargetPlayerId)
                    return "seat_swap_stale";
                if (request.Accepted) Swap(room, source, target);
                else { room.PendingSeatSwaps.Remove(swap); room.StateRevision++; }
                return string.Empty;
            });

        public RoomConfigurationResult ValidateLaunchRequest(RoomState room, StartConfiguredGameRequest request) =>
            Execute(room, request, true, () => TryValidateLaunch(room, out var reason) ? string.Empty : reason, false);

        public bool TryFreezeForLaunch(RoomState room, StartConfiguredGameRequest request,
            out GameRuleConfiguration configuration, out string reasonCode)
        {
            var result = Execute(room, request, true, () =>
            {
                if (!TryValidateLaunch(room, out var reason)) return reason;
                var gameId = Guid.NewGuid().ToString("N");
                var seed = seedSource();
                var runtime = GameplayRandomRuntime.Create(gameId);
                var order = room.Seats.ConvertAll(player => player.PlayerId);
                lock (randomGate)
                    for (var i = order.Count - 1; i > 0; i--)
                    {
                        var other = nextRandomIndex == null
                            ? GameplayRandomRuntime.Next(runtime, gameId, seed, GameplayRandomRuntime.LaunchOrder, i + 1)
                            : nextRandomIndex(i + 1);
                        if (other < 0 || other > i) throw new InvalidOperationException("launch_player_order_random_invalid");
                        var player = order[i]; order[i] = order[other]; order[other] = player;
                    }
                // 从此所有配置和座位命令均被拒绝；适配器广播同一冻结副本。
                room.Configuration = room.Configuration.Clone();
                room.GameId = gameId;
                room.EventDeckSeed = seed;
                room.RandomRuntime = runtime;
                room.RandomAlgorithmVersion = GameplayRandomRuntime.AlgorithmVersion;
                room.RandomReady = true;
                room.LaunchPlayerOrder = order;
                room.HasStarted = true;
                room.StateRevision++;
                return string.Empty;
            });
            reasonCode = result.ReasonCode;
            configuration = result.Success ? result.Snapshot.Configuration.Clone() : null;
            return result.Success;
        }

        public bool TryValidateLaunch(RoomState room, out string reasonCode)
        {
            reasonCode = "room_protocol_mismatch";
            if (!RoomStateSerializer.TryValidate(room, out reasonCode)) return false;
            if (room.HasStarted) { reasonCode = "configuration_stale"; return false; }
            if (room.PlayerCount < 2 || room.PlayerCount > 4) { reasonCode = "unsupported_player_count"; return false; }
            if (content == null) { reasonCode = "content_not_ready"; return false; }
            if (!content.TryValidate(room.Configuration.Clone(), out reasonCode))
            { if (string.IsNullOrEmpty(reasonCode)) reasonCode = "content_not_ready"; return false; }
            if (string.IsNullOrEmpty(room.Configuration.ContentPackHash) ||
                room.Configuration.ContentPackHash != content.GetContentPackHash(room.Configuration))
            { reasonCode = "content_version_mismatch"; return false; }
            if (localPlayers)
            {
                if (room.LocalPlayerId != room.HostPlayerId || room.Seats.Exists(p => !p.LobbyMemberPresent || p.SteamId != 0))
                { reasonCode = "player_not_ready"; return false; }
            }
            else if (!RoomReadinessPolicy.TryValidateTransport(room, out _)) { reasonCode = "player_not_ready"; return false; }
            foreach (var slot in room.SeatSlots)
            {
                if (slot.Occupant == null) continue;
                if (!VersionsMatch(room.Configuration, slot.Occupant)) { reasonCode = "content_version_mismatch"; return false; }
                if (!localPlayers && slot.Occupant.PlayerId != room.HostPlayerId &&
                    (!slot.IsUserReady || slot.ReadyConfigurationRevision != room.Configuration.ConfigurationRevision))
                { reasonCode = "player_not_ready"; return false; }
            }
            reasonCode = string.Empty;
            return true;
        }

        public bool TryOccupySeat(RoomState room, PlayerSeat player, out string reasonCode)
        {
            reasonCode = "seat_occupied";
            if (room == null || player == null || player.PlayerId <= 0 || room.HasStarted) return false;
            if (!player.LobbyMemberPresent) { reasonCode = "service_unavailable"; return false; }
            lock (room)
            {
                if (FindSlot(room, player.PlayerId) != null) return false;
                var target = room.SeatSlots.Find(s => s.Status == "open" && s.Occupant == null);
                if (target == null) return false;
                target.Occupant = player.Clone(); target.Status = "occupied";
                target.Occupant.Color = GetSeatColor(target.SeatIndex);
                InvalidatePreparation(room);
                reasonCode = string.Empty;
                return true;
            }
        }

        public RoomConfigurationResult AddLocalPlayer(RoomState room, AddLocalRoomPlayerRequest request) =>
            Execute(room, request, true, () =>
            {
                if (!localPlayers) return "service_unavailable";
                if (request.SeatIndex < 0 || request.SeatIndex >= 4 || string.IsNullOrWhiteSpace(request.PlayerName))
                    return "room_protocol_mismatch";
                var slot = room.SeatSlots[request.SeatIndex];
                if (slot.Status != "open" || slot.Occupant != null) return "seat_occupied";
                var playerId = 1;
                foreach (var player in room.Seats) playerId = Math.Max(playerId, player.PlayerId + 1);
                slot.Occupant = new PlayerSeat
                {
                    PlayerId = playerId, PlayerName = request.PlayerName.Trim(),
                    Color = GetSeatColor(slot.SeatIndex), LobbyMemberPresent = true
                };
                slot.Status = "occupied";
                InvalidatePreparation(room);
                return string.Empty;
            });

        public RoomConfigurationResult RemoveLocalPlayer(RoomState room, RemoveLocalRoomPlayerRequest request) =>
            Execute(room, request, true, () =>
            {
                if (!localPlayers) return "service_unavailable";
                if (request.SeatIndex < 0 || request.SeatIndex >= 4) return "room_protocol_mismatch";
                var slot = room.SeatSlots[request.SeatIndex];
                if (slot.Occupant == null || slot.Occupant.PlayerId == room.HostPlayerId) return "seat_occupied";
                slot.Occupant = null;
                slot.Status = "open";
                InvalidatePreparation(room);
                return string.Empty;
            });

        public bool VacateSeat(RoomState room, int playerId)
        {
            if (room == null || room.HasStarted || playerId == room.HostPlayerId) return false;
            lock (room)
            {
                var slot = FindSlot(room, playerId);
                if (slot == null) return false;
                slot.Occupant = null; slot.Status = "open";
                InvalidatePreparation(room);
                return true;
            }
        }

        public static RoomSeatSlotState FindSlot(RoomState room, int playerId) =>
            room?.SeatSlots?.Find(s => s?.Status == "occupied" && s.Occupant?.PlayerId == playerId);
        private static bool VersionsMatch(GameRuleConfiguration configuration, PlayerSeat player) =>
            !string.IsNullOrEmpty(configuration.ContentPackHash) && configuration.ContentPackHash == player.ContentPackHash &&
            configuration.RuleContractVersion == player.RuleContractVersion;
        private void Swap(RoomState room, RoomSeatSlotState source, RoomSeatSlotState target)
        {
            var occupant = target.Occupant; target.Occupant = source.Occupant; source.Occupant = occupant;
            source.Status = source.Occupant == null ? "open" : "occupied";
            target.Status = "occupied";
            if (source.Occupant != null) source.Occupant.Color = GetSeatColor(source.SeatIndex);
            target.Occupant.Color = GetSeatColor(target.SeatIndex);
            InvalidatePreparation(room);
        }
        private void InvalidatePreparation(RoomState room)
        {
            room.Configuration.PlayerCount = room.PlayerCount;
            if (room.PlayerCount == 4) room.Configuration.MapId = StaticMapDefinitions.FourPlayerMapId;
            if (content != null)
            {
                room.Configuration.ContentPackHash = content.GetContentPackHash(room.Configuration);
                foreach (var player in room.Seats)
                    if (localPlayers || player.PlayerId == room.HostPlayerId)
                    {
                        player.ContentPackHash = room.Configuration.ContentPackHash;
                        player.RuleContractVersion = room.Configuration.RuleContractVersion;
                    }
            }
            room.Configuration.ConfigurationRevision++;
            room.StateRevision++;
            room.PendingSeatSwaps.Clear();
            foreach (var slot in room.SeatSlots) { slot.IsUserReady = false; slot.ReadyConfigurationRevision = -1; }
        }
        private RoomConfigurationResult Execute(RoomState room, RoomConfigurationRequest request, bool hostOnly, Func<string> action,
            bool recordReceipt = true)
        {
            if (room == null || request == null) return Result(room, "service_unavailable");
            lock (room)
            {
                if (request.RoomId != room.RoomId || string.IsNullOrEmpty(request.CommandId) || FindSlot(room, request.RequestingPlayerId) == null)
                    return Result(room, "room_protocol_mismatch");
                if (hostOnly && request.RequestingPlayerId != room.HostPlayerId) return Result(room, "not_room_host");
                var key = request.RequestingPlayerId + ":" + request.CommandId;
                histories.TryGetValue(room, out var history);
                var receipt = history?.Find(r => r.Key == key);
                if (receipt != null) return Result(room, receipt.RequestType != request.GetType() ? "room_protocol_mismatch" : receipt.Success ? string.Empty : receipt.Reason);
                string reason;
                if (room.ProtocolVersion != RoomState.CurrentProtocolVersion || room.Configuration == null) reason = "room_protocol_mismatch";
                else if (room.HasStarted || request.ExpectedConfigurationRevision != room.Configuration.ConfigurationRevision) reason = "configuration_stale";
                else reason = action();
                if (recordReceipt)
                {
                    history = histories.GetOrCreateValue(room);
                    history.Add(new Receipt { Key = key, RequestType = request.GetType(), Success = string.IsNullOrEmpty(reason), Reason = reason });
                    if (history.Count > 256) history.RemoveAt(0);
                }
                return Result(room, reason);
            }
        }
        public static RoomConfigurationResult Result(RoomState room, string reasonCode) => new RoomConfigurationResult
        { Success = string.IsNullOrEmpty(reasonCode), ReasonCode = reasonCode ?? string.Empty, Snapshot = room?.Clone() };
    }
}
