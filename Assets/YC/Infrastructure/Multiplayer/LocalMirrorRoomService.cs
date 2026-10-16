using System;
using System.Threading.Tasks;
using YC.Application.Sessions;

namespace YC.Infrastructure.Multiplayer
{
    public sealed class LocalMirrorRoomService : IResumableOnlineRoomService
    {
        private readonly NetworkRoomService roomService;
        private readonly WaitingRoomConnectionAuthority waitingRoomAuthority = new WaitingRoomConnectionAuthority();
        private TaskCompletionSource<RoomState> pendingJoin;
        private Action<RoomState> pendingJoinHandler;
        private MirrorNetworkRuntime subscribedRuntime;
        private bool isHost;
        private bool acceptNetworkEvents;
        private bool disposed;
        private string joinedRoomId;
        private bool clientStarted;
        private int eventGeneration;
        private readonly System.Threading.SynchronizationContext mainContext = System.Threading.SynchronizationContext.Current;
        private static string OperatorIdentity(string name)
        {
            var key = "YC.LocalTestIdentity." + name;
            var value = UnityEngine.PlayerPrefs.GetString(key, "");
            if (string.IsNullOrEmpty(value)) { value = Guid.NewGuid().ToString("N"); UnityEngine.PlayerPrefs.SetString(key, value); UnityEngine.PlayerPrefs.Save(); }
            return value;
        }
        public Task<RoomState> CreateResumeRoomAsync(ResumeRoomOptions options)
        {
            options.Validate();
            var host = options.Seats.Find(s => s.PlayerId == options.HostPlayerId);
            if (host.OperatorId != OperatorIdentity(host.PlayerName)) throw new InvalidOperationException("本地联机续局需使用原房主配置。");
            return CreateLocalRoom(host.PlayerName, options.Seats.Count, options);
        }
        public void AssignResumeSeat(string memberId, int playerId) => roomService.AssignResumeSeat(memberId, playerId);

        public LocalMirrorRoomService()
        {
            roomService = new NetworkRoomService(OnlineRoomServiceProvider.ContentCapabilities);
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
            var joinCompletion = new TaskCompletionSource<RoomState>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingJoin = joinCompletion;
            pendingJoinHandler = room =>
            {
                if (room == null || room.LocalPlayerId < 0 || room.LocalPlayerId == 0 && room.Resume == null || room.PlayerCount <= 0) return;
                CompletePendingJoin(room);
            };
            roomService.RoomUpdated += pendingJoinHandler;

            try
            {
                joinedRoomId = roomId;
                roomService.JoinRoom(roomId, playerName, OperatorIdentity(playerName));
                SubscribeToRuntime(MirrorNetworkRuntime.Ensure());
                var joinedRoom = roomService.GetCurrentRoom();
                TryStartAssignedClient(joinedRoom);
                // TCP 接收线程可能已完成加入并清空字段；始终返回本次操作的任务。
                return joinCompletion.Task;
            }
            catch
            {
                ClearPendingJoin();
                throw;
            }
        }

        public async Task StartGameAsync()
        {
            ThrowIfDisposed();
            var room = GetCurrentRoom();
            if (room == null) throw new InvalidOperationException("service_unavailable");
            var result = await StartConfiguredGameAsync(new StartConfiguredGameRequest
            {
                CommandId = Guid.NewGuid().ToString("N"), RoomId = room.RoomId,
                RequestingPlayerId = room.LocalPlayerId, ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision
            });
            if (!result.Success) throw new InvalidOperationException(result.ReasonCode);
        }

        public Task<RoomConfigurationResult> ChangeConfigurationAsync(ChangeRoomConfigurationRequest request) => roomService.SendConfigurationAsync(request);
        public Task<RoomConfigurationResult> SetReadyAsync(SetRoomReadyRequest request) => roomService.SendConfigurationAsync(request);
        public Task<RoomConfigurationResult> SetSeatStatusAsync(SetRoomSeatStatusRequest request) => roomService.SendConfigurationAsync(request);
        public Task<RoomConfigurationResult> RequestSeatSwapAsync(RequestRoomSeatSwapRequest request) => roomService.SendConfigurationAsync(request);
        public Task<RoomConfigurationResult> ConfirmSeatSwapAsync(ConfirmRoomSeatSwapRequest request) => roomService.SendConfigurationAsync(request);
        public Task<RoomConfigurationResult> StartConfiguredGameAsync(StartConfiguredGameRequest request) => roomService.SendConfigurationAsync(request);

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
            acceptNetworkEvents = false;
            isHost = false;
            clientStarted = false; joinedRoomId = null;
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
            if (mainContext != null && System.Threading.SynchronizationContext.Current != mainContext)
            { var captured = eventGeneration; mainContext.Post(_ => { if (captured == eventGeneration) OnRoomUpdated(room); }, null); return; }
            if (!acceptNetworkEvents || room == null) return;
            if (isHost) SynchronizeAuthorityRoster(room);
            else TryStartAssignedClient(room);
            RoomUpdated?.Invoke(room);
        }
        private void TryStartAssignedClient(RoomState room)
        {
            if (isHost || room == null || room.LocalPlayerId <= 0 || subscribedRuntime == null || string.IsNullOrEmpty(joinedRoomId)) return;
            subscribedRuntime.SetLocalClientIdentityTicket(roomService.LocalIdentityTicket);
            if (!clientStarted) { clientStarted = true; subscribedRuntime.StartLocalClient(LocalMirrorTestMode.GetMirrorHost(joinedRoomId)); }
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
            runtime.ClientDisconnected += OnTerminalClientDisconnected;
        }

        private void UnsubscribeFromRuntime(MirrorNetworkRuntime runtime)
        {
            runtime.ServerClientConnected -= OnServerClientConnected;
            runtime.ServerClientDisconnected -= OnServerClientDisconnected;
            runtime.ServerLocalIdentityPresented -= OnServerLocalIdentityPresented;
            runtime.LocalClientDisconnected -= OnLocalClientDisconnected;
            runtime.ClientDisconnected -= OnTerminalClientDisconnected;
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
            UnityEngine.Debug.Log("[LocalMirrorLifecycle] 原房间票据已认证，连接=" + connectionId + "，玩家=" + playerId);
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
            // 房间 TCP 仍持有原身份票据；局内只重连 Mirror，不删除席位或重建对局。
            if (roomService.GetCurrentRoom()?.HasStarted == true && subscribedRuntime != null &&
                subscribedRuntime.TryBeginLocalClientReconnect()) return;
            acceptNetworkEvents = false;
            roomService.Shutdown();
            RoomDisbanded?.Invoke();
        }

        private void OnTerminalClientDisconnected()
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
