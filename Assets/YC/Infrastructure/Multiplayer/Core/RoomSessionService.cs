using System;
using System.Threading.Tasks;
using YC.Application.Sessions;
using YC.Domain.Maps;

namespace YC.Infrastructure.Multiplayer
{
    /// <summary>先创建本地房间；显式打开联机后才创建网络服务，关闭时保留房主配置。</summary>
    public sealed class RoomSessionService : IOnlineRoomService, IRoomNetworkingControl, ILocalRoomPlayerControl
    {
        private readonly IRoomContentCapabilityProvider content;
        private readonly RoomConfigurationService authority;
        private readonly Func<IOnlineRoomService> createNetworkService;
        private RoomState localRoom;
        private IOnlineRoomService network;
        private int generation;
        private bool disposed;
        public bool IsOnline { get; private set; }
        public bool IsChangingNetworking { get; private set; }
        public bool SupportsLocalPlayers => !IsOnline && !IsChangingNetworking;
        public bool SupportsFriendInvites => IsOnline && network != null && network.SupportsFriendInvites;
        public bool HasPendingLobbyJoinRequest => network != null && network.HasPendingLobbyJoinRequest;
        public event Action<RoomState> RoomUpdated;
        public event Action<RoomState> GameStarted;
        public event Action RoomDisbanded;
        public event Action<string> ErrorOccurred;
        public event Action<string> LobbyJoinRequested;

        public RoomSessionService(IRoomContentCapabilityProvider content, Func<IOnlineRoomService> createNetworkService)
        {
            this.content = content ?? throw new ArgumentNullException(nameof(content));
            this.createNetworkService = createNetworkService ?? throw new ArgumentNullException(nameof(createNetworkService));
            authority = new RoomConfigurationService(content, localPlayers: true);
        }

        public void Initialize() => ThrowIfDisposed();

        public Task<RoomState> CreateRoomAsync(string hostPlayerName, int playerCount)
        {
            ThrowIfDisposed();
            if (playerCount < 2 || playerCount > 4) throw new ArgumentOutOfRangeException(nameof(playerCount));
            Shutdown();
            localRoom = new RoomState { RoomId = "local-" + Guid.NewGuid().ToString("N"), HostPlayerId = 1, LocalPlayerId = 1 };
            localRoom.Configuration.MapId = playerCount == 4 ? StaticMapDefinitions.FourPlayerMapId : StaticMapDefinitions.ThreePlayerMapId;
            if (!authority.TryOccupySeat(localRoom, LocalHost(1, hostPlayerName), out var reason))
                throw new InvalidOperationException(reason);
            RoomUpdated?.Invoke(localRoom.Clone());
            return Task.FromResult(localRoom.Clone());
        }

        public async Task<RoomState> SetOnlineAsync(bool enabled)
        {
            ThrowIfDisposed();
            if (IsChangingNetworking) throw new InvalidOperationException("room_network_busy");
            var source = GetCurrentRoom();
            if (source == null) throw new InvalidOperationException("service_unavailable");
            if (source.LocalPlayerId != source.HostPlayerId) throw new InvalidOperationException("not_room_host");
            if (source.HasStarted) throw new InvalidOperationException("configuration_stale");
            if (enabled == IsOnline) return source;
            if (enabled && source.PlayerCount > 1) throw new InvalidOperationException("hotseat_requires_offline");
            if (!enabled)
            {
                var host = RoomConfigurationService.FindSlot(source, source.HostPlayerId).Occupant;
                var restored = RoomHostSnapshot.Create(source, "local-" + Guid.NewGuid().ToString("N"),
                    LocalHost(host.PlayerId, host.PlayerName), content);
                // 先解除转发，再关闭实际Lobby/运输，远程断线通知不会把本地房间关掉。
                ReleaseNetwork();
                localRoom = restored;
                IsOnline = false;
                RoomUpdated?.Invoke(restored.Clone());
                return restored.Clone();
            }

            var operation = ++generation;
            IsChangingNetworking = true;
            try
            {
                network = CreateNetwork();
                if (!(network is IPreconfiguredRoomHost host)) throw new InvalidOperationException("service_unavailable");
                var created = await host.CreateRoomFromSnapshotAsync(source);
                EnsureCurrent(operation);
                IsOnline = true;
                localRoom = null;
                IsChangingNetworking = false;
                RoomUpdated?.Invoke(created.Clone());
                return created.Clone();
            }
            catch
            {
                if (operation == generation) ReleaseNetwork();
                throw;
            }
            finally { if (operation == generation) IsChangingNetworking = false; }
        }

        public async Task<RoomState> JoinRoomAsync(string roomId, string playerName)
        {
            ThrowIfDisposed();
            if (IsChangingNetworking) throw new InvalidOperationException("room_network_busy");
            Shutdown();
            var operation = ++generation;
            IsChangingNetworking = true;
            try
            {
                network = CreateNetwork();
                var joined = await network.JoinRoomAsync(roomId, playerName);
                EnsureCurrent(operation);
                IsOnline = true;
                IsChangingNetworking = false;
                RoomUpdated?.Invoke(joined.Clone());
                return joined.Clone();
            }
            catch
            {
                if (operation == generation) ReleaseNetwork();
                throw;
            }
            finally { if (operation == generation) IsChangingNetworking = false; }
        }

        private IOnlineRoomService CreateNetwork()
        {
            var service = createNetworkService();
            service.RoomUpdated += ForwardRoom;
            service.GameStarted += ForwardStart;
            service.RoomDisbanded += ForwardDisbanded;
            service.ErrorOccurred += ForwardError;
            service.LobbyJoinRequested += ForwardLobbyJoin;
            return service;
        }
        private void ForwardRoom(RoomState room) { if (IsOnline && !IsChangingNetworking) RoomUpdated?.Invoke(room); }
        private void ForwardStart(RoomState room) { if (IsOnline && !IsChangingNetworking) GameStarted?.Invoke(room); }
        private void ForwardDisbanded() { if (IsOnline && !IsChangingNetworking) RoomDisbanded?.Invoke(); }
        private void ForwardError(string error) { if (!IsChangingNetworking) ErrorOccurred?.Invoke(error); }
        private void ForwardLobbyJoin(string id) => LobbyJoinRequested?.Invoke(id);

        private Task<RoomConfigurationResult> Send(RoomConfigurationRequest request,
            Func<RoomConfigurationResult> local, Func<IOnlineRoomService, Task<RoomConfigurationResult>> remote)
        {
            ThrowIfDisposed();
            if (IsChangingNetworking) return Task.FromResult(RoomConfigurationService.Result(GetCurrentRoom(), "room_network_busy"));
            if (IsOnline) return remote(network);
            if (localRoom == null || request == null) return Task.FromResult(RoomConfigurationService.Result(localRoom, "service_unavailable"));
            if (request.RequestingPlayerId != localRoom.LocalPlayerId)
                return Task.FromResult(RoomConfigurationService.Result(localRoom, "not_room_host"));
            var result = local();
            if (result.Success) RoomUpdated?.Invoke(result.Snapshot.Clone());
            return Task.FromResult(result);
        }
        public Task<RoomConfigurationResult> ChangeConfigurationAsync(ChangeRoomConfigurationRequest r) => Send(r, () => authority.ChangeConfiguration(localRoom, r), s => s.ChangeConfigurationAsync(r));
        public Task<RoomConfigurationResult> SetReadyAsync(SetRoomReadyRequest r) => Send(r, () => authority.SetReady(localRoom, r), s => s.SetReadyAsync(r));
        public Task<RoomConfigurationResult> SetSeatStatusAsync(SetRoomSeatStatusRequest r) => Send(r, () => authority.SetSeatStatus(localRoom, r), s => s.SetSeatStatusAsync(r));
        public Task<RoomConfigurationResult> RequestSeatSwapAsync(RequestRoomSeatSwapRequest r) => Send(r, () => authority.RequestSeatSwap(localRoom, r), s => s.RequestSeatSwapAsync(r));
        public Task<RoomConfigurationResult> ConfirmSeatSwapAsync(ConfirmRoomSeatSwapRequest r) => Send(r, () => authority.ConfirmSeatSwap(localRoom, r), s => s.ConfirmSeatSwapAsync(r));
        public Task<RoomConfigurationResult> AddLocalPlayerAsync(AddLocalRoomPlayerRequest r) => Send(r,
            () => authority.AddLocalPlayer(localRoom, r), s => Task.FromResult(RoomConfigurationService.Result(s.GetCurrentRoom(), "hotseat_requires_offline")));
        public Task<RoomConfigurationResult> RemoveLocalPlayerAsync(RemoveLocalRoomPlayerRequest r) => Send(r,
            () => authority.RemoveLocalPlayer(localRoom, r), s => Task.FromResult(RoomConfigurationService.Result(s.GetCurrentRoom(), "hotseat_requires_offline")));
        public Task<RoomConfigurationResult> StartConfiguredGameAsync(StartConfiguredGameRequest r) => Send(r, () =>
        {
            var wasStarted = localRoom.HasStarted;
            var success = authority.TryFreezeForLaunch(localRoom, r, out _, out var reason);
            if (success && !wasStarted) GameStarted?.Invoke(localRoom.Clone());
            return RoomConfigurationService.Result(localRoom, reason);
        }, s => s.StartConfiguredGameAsync(r));

        public async Task StartGameAsync()
        {
            var room = GetCurrentRoom();
            if (room == null) throw new InvalidOperationException("service_unavailable");
            var result = await StartConfiguredGameAsync(new StartConfiguredGameRequest
            {
                CommandId = Guid.NewGuid().ToString("N"), RoomId = room.RoomId, RequestingPlayerId = room.LocalPlayerId,
                ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision
            });
            if (!result.Success) throw new InvalidOperationException(result.ReasonCode);
        }
        public void InviteFriends()
        {
            if (!SupportsFriendInvites) throw new InvalidOperationException("room_network_offline");
            network.InviteFriends();
        }
        public bool TryConsumePendingLobbyJoinRequest(out string lobbyId)
        {
            lobbyId = string.Empty;
            return network != null && network.TryConsumePendingLobbyJoinRequest(out lobbyId);
        }
        public RoomState GetCurrentRoom() => IsOnline ? network?.GetCurrentRoom() : localRoom?.Clone();
        public void Shutdown()
        {
            generation++;
            IsChangingNetworking = false;
            IsOnline = false;
            localRoom = null;
            ReleaseNetwork();
        }
        private void ReleaseNetwork()
        {
            var previous = network;
            network = null;
            if (previous == null) return;
            previous.RoomUpdated -= ForwardRoom;
            previous.GameStarted -= ForwardStart;
            previous.RoomDisbanded -= ForwardDisbanded;
            previous.ErrorOccurred -= ForwardError;
            previous.LobbyJoinRequested -= ForwardLobbyJoin;
            previous.Dispose();
        }
        public void Dispose() { if (disposed) return; Shutdown(); disposed = true; }
        private void EnsureCurrent(int operation)
        { if (disposed || operation != generation) throw new OperationCanceledException(); }
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(RoomSessionService)); }
        private static PlayerSeat LocalHost(int playerId, string name) => new PlayerSeat
        { PlayerId = playerId, PlayerName = name, LobbyMemberPresent = true };
    }
}
