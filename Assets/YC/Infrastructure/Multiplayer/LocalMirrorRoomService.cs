using System;
using System.Threading.Tasks;
using YC.Application.Sessions;

namespace YC.Infrastructure.Multiplayer
{
    public sealed class LocalMirrorRoomService : IOnlineRoomService
    {
        private readonly NetworkRoomService roomService = new NetworkRoomService();
        private readonly WaitingRoomConnectionAuthority waitingRoomAuthority = new WaitingRoomConnectionAuthority();
        private TaskCompletionSource<RoomState> pendingJoin;
        private Action<RoomState> pendingJoinHandler;
        private MirrorNetworkRuntime subscribedRuntime;
        private bool isHost;
        private bool acceptNetworkEvents;
        private bool disposed;

        public LocalMirrorRoomService()
        {
            roomService.RoomUpdated += OnRoomUpdated;
            roomService.GameStarted += OnGameStarted;
            roomService.RoomDisbanded += OnRoomDisbanded;
            roomService.ErrorOccurred += OnErrorOccurred;
        }

        public bool SupportsFriendInvites => false;
        public bool HasPendingLobbyJoinRequest => false;
        public event Action<RoomState> RoomUpdated;
        public event Action<RoomState> GameStarted;
        public event Action RoomDisbanded;
        public event Action<string> ErrorOccurred;
        public event Action<string> LobbyJoinRequested;

        public void Initialize() => ThrowIfDisposed();

        public Task<RoomState> CreateRoomAsync(string hostPlayerName, int playerCount)
        {
            ThrowIfDisposed();
            Shutdown();
            isHost = true;
            acceptNetworkEvents = true;
            var room = roomService.CreateRoom(hostPlayerName, playerCount);
            SynchronizeAuthorityRoster(room);
            SubscribeToRuntime(MirrorNetworkRuntime.Ensure());
            subscribedRuntime.SetLocalClientPlayerId(1);
            subscribedRuntime.StartLocalHost();
            return Task.FromResult(GetCurrentRoom());
        }

        public Task<RoomState> JoinRoomAsync(string roomId, string playerName)
        {
            ThrowIfDisposed();
            Shutdown();
            isHost = false;
            acceptNetworkEvents = true;
            pendingJoin = new TaskCompletionSource<RoomState>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingJoinHandler = room =>
            {
                if (room == null || room.LocalPlayerId <= 0 || room.PlayerCount <= 0) return;
                CompletePendingJoin(room);
            };
            roomService.RoomUpdated += pendingJoinHandler;

            try
            {
                roomService.JoinRoom(roomId, playerName);
                SubscribeToRuntime(MirrorNetworkRuntime.Ensure());
                var joinedRoom = roomService.GetCurrentRoom();
                if (joinedRoom != null && joinedRoom.LocalPlayerId > 0)
                    subscribedRuntime.SetLocalClientPlayerId(joinedRoom.LocalPlayerId);
                subscribedRuntime.StartLocalClient(LocalMirrorTestMode.GetMirrorHost(roomId));
                return pendingJoin.Task;
            }
            catch
            {
                ClearPendingJoin();
                throw;
            }
        }

        public Task StartGameAsync()
        {
            ThrowIfDisposed();
            return roomService.TryStartGame(out var reason)
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException(reason));
        }

        public void InviteFriends() => ErrorOccurred?.Invoke("本地测试模式不使用 Steam 好友邀请，请复制并发送本地房间号。");

        public bool TryConsumePendingLobbyJoinRequest(out string lobbyId)
        {
            lobbyId = string.Empty;
            return false;
        }

        public RoomState GetCurrentRoom() => roomService.GetCurrentRoom();

        public void Shutdown()
        {
            if (disposed) return;
            CancelPendingJoin();
            acceptNetworkEvents = false;
            isHost = false;
            waitingRoomAuthority.Shutdown();
            if (subscribedRuntime != null)
            {
                UnsubscribeFromRuntime(subscribedRuntime);
                subscribedRuntime = null;
            }
            roomService.Shutdown();
            MirrorNetworkRuntime.Instance?.ShutdownNetwork();
        }

        public void Dispose()
        {
            if (disposed) return;
            Shutdown();
            roomService.RoomUpdated -= OnRoomUpdated;
            roomService.GameStarted -= OnGameStarted;
            roomService.RoomDisbanded -= OnRoomDisbanded;
            roomService.ErrorOccurred -= OnErrorOccurred;
            roomService.Dispose();
            disposed = true;
        }

        private void OnRoomUpdated(RoomState room)
        {
            if (!acceptNetworkEvents || room == null) return;
            if (isHost) SynchronizeAuthorityRoster(room);
            else if (room.LocalPlayerId > 0) subscribedRuntime?.SetLocalClientPlayerId(room.LocalPlayerId);
            RoomUpdated?.Invoke(room);
        }
        private void OnGameStarted(RoomState room) => GameStarted?.Invoke(room);
        private void OnRoomDisbanded() => RoomDisbanded?.Invoke();
        private void OnErrorOccurred(string message) => ErrorOccurred?.Invoke(message);

        private void SubscribeToRuntime(MirrorNetworkRuntime runtime)
        {
            if (subscribedRuntime == runtime) return;
            if (subscribedRuntime != null) UnsubscribeFromRuntime(subscribedRuntime);
            subscribedRuntime = runtime;
            if (runtime == null) return;
            runtime.ServerClientConnected += OnServerClientConnected;
            runtime.ServerClientDisconnected += OnServerClientDisconnected;
            runtime.ServerLocalIdentityClaimed += OnServerLocalIdentityClaimed;
            runtime.LocalClientDisconnected += OnLocalClientDisconnected;
        }

        private void UnsubscribeFromRuntime(MirrorNetworkRuntime runtime)
        {
            runtime.ServerClientConnected -= OnServerClientConnected;
            runtime.ServerClientDisconnected -= OnServerClientDisconnected;
            runtime.ServerLocalIdentityClaimed -= OnServerLocalIdentityClaimed;
            runtime.LocalClientDisconnected -= OnLocalClientDisconnected;
        }

        private void SynchronizeAuthorityRoster(RoomState room)
        {
            if (!isHost || room == null) return;
            if (!waitingRoomAuthority.IsActive)
            {
                waitingRoomAuthority.Begin(room.Seats);
                return;
            }
            var removed = waitingRoomAuthority.UpdateLobbySeats(room.Seats);
            for (var i = 0; i < removed.Count; i++) subscribedRuntime?.RequestServerDisconnect(removed[i]);
        }

        private void OnServerClientConnected(MirrorServerConnectionInfo connection)
        {
            if (!acceptNetworkEvents || !isHost)
            {
                subscribedRuntime?.RequestServerDisconnect(connection.ConnectionId);
                return;
            }
            SynchronizeAuthorityRoster(roomService.GetCurrentRoom());
            waitingRoomAuthority.ObserveTransportConnection(connection.ConnectionId);
            if (!connection.IsLocal) return;
            if (connection.ConnectionId != 0 ||
                !waitingRoomAuthority.TryVerifyIdentity(
                    connection.ConnectionId,
                    LocalMirrorIdentity.ForPlayer(1),
                    1,
                    out var playerId))
            {
                RejectServerConnection(connection.ConnectionId);
                return;
            }
            roomService.SetTransportReadiness(playerId, true, true, (ulong)connection.ConnectionId);
        }

        private void OnServerLocalIdentityClaimed(int connectionId, int claimedPlayerId)
        {
            if (!acceptNetworkEvents || !isHost || claimedPlayerId < 1 || claimedPlayerId > 4)
            {
                RejectServerConnection(connectionId);
                return;
            }
            SynchronizeAuthorityRoster(roomService.GetCurrentRoom());
            if (!waitingRoomAuthority.TryVerifyIdentity(
                    connectionId,
                    LocalMirrorIdentity.ForPlayer(claimedPlayerId),
                    claimedPlayerId,
                    out var playerId))
            {
                RejectServerConnection(connectionId);
                return;
            }
            roomService.SetTransportReadiness(playerId, true, true, (ulong)connectionId);
        }

        private void OnServerClientDisconnected(int connectionId)
        {
            if (!acceptNetworkEvents || !isHost) return;
            if (waitingRoomAuthority.Disconnect(connectionId, out var playerId))
                roomService.SetTransportReadiness(playerId, false, false, 0);
        }

        private void OnLocalClientDisconnected()
        {
            if (!acceptNetworkEvents || isHost) return;
            acceptNetworkEvents = false;
            roomService.Shutdown();
            RoomDisbanded?.Invoke();
        }

        private void RejectServerConnection(int connectionId)
        {
            waitingRoomAuthority.Disconnect(connectionId, out _);
            subscribedRuntime?.RequestServerDisconnect(connectionId);
        }

        private void CompletePendingJoin(RoomState room)
        {
            var completion = pendingJoin;
            ClearPendingJoin();
            completion?.TrySetResult(room);
        }

        private void CancelPendingJoin()
        {
            var completion = pendingJoin;
            ClearPendingJoin();
            completion?.TrySetCanceled();
        }

        private void ClearPendingJoin()
        {
            if (pendingJoinHandler != null)
                roomService.RoomUpdated -= pendingJoinHandler;
            pendingJoinHandler = null;
            pendingJoin = null;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(LocalMirrorRoomService));
            if (!LocalMirrorTestMode.IsEnabled)
                throw new InvalidOperationException("Mirror 本地测试模式尚未启用。");
        }
    }
}
