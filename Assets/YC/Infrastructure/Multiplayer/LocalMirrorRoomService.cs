using System;
using System.Threading.Tasks;
using YC.Application.Sessions;

namespace YC.Infrastructure.Multiplayer
{
    public sealed class LocalMirrorRoomService : IResumableOnlineRoomService
    {
        private readonly NetworkRoomService roomService = new NetworkRoomService();
        private readonly WaitingRoomConnectionAuthority waitingRoomAuthority = new WaitingRoomConnectionAuthority();
        private TaskCompletionSource<RoomState> pendingJoin;
        private Action<RoomState> pendingJoinHandler;
        private MirrorNetworkRuntime subscribedRuntime;
        private bool isHost;
        private bool acceptNetworkEvents;
        private bool disposed;
        private bool clientConnectionStarted;
        private string joinRoomId;
        private int eventGeneration;
        private readonly System.Threading.SynchronizationContext mainContext = System.Threading.SynchronizationContext.Current;
        private static string OperatorIdentity(string name)
        {
            var key = "YC.LocalTestIdentity." + name;
            var id = UnityEngine.PlayerPrefs.GetString(key, "");
            if (string.IsNullOrEmpty(id)) { id = Guid.NewGuid().ToString("N"); UnityEngine.PlayerPrefs.SetString(key, id); UnityEngine.PlayerPrefs.Save(); }
            return id;
        }
        public Task<RoomState> CreateResumeRoomAsync(ResumeRoomOptions options)
        {
            options.Validate();
            var host = options.Seats.Find(s => s.PlayerId == 1);
            if (host.OperatorId != OperatorIdentity(host.PlayerName))
                throw new InvalidOperationException("本地测试续局只能由原房主配置创建。");
            return CreateLocalRoom(host.PlayerName, options.Seats.Count, options);
        }
        public void AssignResumeSeat(string memberId, int playerId) => roomService.AssignResumeSeat(memberId, playerId);

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
        public event Action<string> LobbyJoinRequested
        {
            add { }
            remove { }
        }

        public void Initialize() => ThrowIfDisposed();

        public Task<RoomState> CreateRoomAsync(string hostPlayerName, int playerCount)
            => CreateLocalRoom(hostPlayerName, playerCount, null);
        private Task<RoomState> CreateLocalRoom(string hostPlayerName, int playerCount, ResumeRoomOptions resume)
        {
            ThrowIfDisposed();
            Shutdown();
            isHost = true;
            acceptNetworkEvents = true;
            var room = roomService.CreateRoom(hostPlayerName, playerCount, resume, OperatorIdentity(hostPlayerName));
            SynchronizeAuthorityRoster(room);
            SubscribeToRuntime(MirrorNetworkRuntime.Ensure());
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
                if (room == null || (room.LocalPlayerId <= 0 && room.Resume == null) || room.PlayerCount <= 0) return;
                CompletePendingJoin(room);
            };
            roomService.RoomUpdated += pendingJoinHandler;
            var joinTask = pendingJoin.Task;

            try
            {
                joinRoomId = roomId;
                roomService.JoinRoom(roomId, playerName, OperatorIdentity(playerName));
                SubscribeToRuntime(MirrorNetworkRuntime.Ensure());
                TryConnectAssignedClient(roomService.GetCurrentRoom());
                return joinTask;
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
            eventGeneration++;
            clientConnectionStarted = false;
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

        private void TryConnectAssignedClient(RoomState room)
        {
            if (isHost || clientConnectionStarted || room == null || room.LocalPlayerId <= 0 || subscribedRuntime == null) return;
            if (string.IsNullOrEmpty(roomService.LocalIdentityTicket)) return;
            subscribedRuntime.SetLocalClientIdentityTicket(roomService.LocalIdentityTicket);
            clientConnectionStarted = true;
            subscribedRuntime.StartLocalClient(LocalMirrorTestMode.GetMirrorHost(joinRoomId));
        }
        private void OnRoomUpdated(RoomState room)
        {
            if (mainContext != null && System.Threading.SynchronizationContext.Current != mainContext)
            {
                var generation = eventGeneration;
                mainContext.Post(_ => { if (generation == eventGeneration) OnRoomUpdated(room); }, null);
                return;
            }
            if (!acceptNetworkEvents || room == null) return;
            if (isHost) SynchronizeAuthorityRoster(room);
            else TryConnectAssignedClient(room);
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
            runtime.ServerLocalIdentityPresented += OnServerLocalIdentityPresented;
            runtime.LocalClientDisconnected += OnLocalClientDisconnected;
        }

        private void UnsubscribeFromRuntime(MirrorNetworkRuntime runtime)
        {
            runtime.ServerClientConnected -= OnServerClientConnected;
            runtime.ServerClientDisconnected -= OnServerClientDisconnected;
            runtime.ServerLocalIdentityPresented -= OnServerLocalIdentityPresented;
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

        private void OnServerLocalIdentityPresented(int connectionId, string ticket)
        {
            if (!acceptNetworkEvents || !isHost ||
                !roomService.TryResolveIdentityTicket(ticket, out var claimedPlayerId))
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
