using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Mirror;
using Mirror.FizzySteam;
using UnityEngine;

namespace YC.Infrastructure.Multiplayer
{
    public readonly struct MirrorServerConnectionInfo
    {
        public MirrorServerConnectionInfo(int connectionId, string address, bool isLocal)
        {
            ConnectionId = connectionId;
            Address = address ?? string.Empty;
            IsLocal = isLocal;
        }

        public int ConnectionId { get; }
        public string Address { get; }
        public bool IsLocal { get; }
    }

    public struct WaitingRoomLocalIdentityMessage : NetworkMessage
    {
        public string Ticket;
    }

    public struct WaitingRoomStateMessage : NetworkMessage
    {
        public string Json;
    }
    public struct WaitingRoomCommandMessage : NetworkMessage { public string Json; }
    public struct WaitingRoomCommandReplyMessage : NetworkMessage { public string Json; }

    [DefaultExecutionOrder(-24000)]
    public sealed class MirrorNetworkRuntime : MonoBehaviour
    {
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private FizzySteamworks steamTransport;
        [SerializeField] private TelepathyTransport localTransport;

        private readonly object disconnectQueueLock = new object();
        private readonly object localIdentityLock = new object();
        private readonly Queue<int> pendingServerDisconnects = new Queue<int>();
        private readonly HashSet<int> publishedServerConnections = new HashSet<int>();
        private readonly HashSet<int> disconnectingServerConnections = new HashSet<int>();
        private bool mirrorCallbacksSubscribed;
        private bool localClientConnectedPublished;
        private bool localClientDisconnectedPublished;
        private string localClientIdentityTicket = string.Empty;
        private bool localIdentitySendPending;
        private bool reconnectLocalClient;
        private float nextLocalReconnectTime;
        private float localReconnectDeadline;
        private int localReconnectAttempts;
        private bool networkShutdownRequested;
        private bool serverIdentityRetryPending;
        private float nextServerIdentityRetryTime;
        public event Action ServerIdentityRetryRequested;
        private bool steamReconnectCancelled;
        private ulong steamReconnectLobbyId, steamReconnectHostId, steamReconnectLocalId;
        private string steamReconnectGameId;
        // 默认直接读取SDK；隔离检查只替换平台读取，不替换运行时或重试逻辑。
        private Func<bool> steamReadyProbe;
        private Func<ulong> steamAccountProbe;
        private Func<ulong, ulong> steamOwnerProbe;
        private Func<ulong, int> steamMemberCountProbe;
        private Func<ulong, int, ulong> steamMemberProbe;
        public bool IsReconnectingClient => reconnectLocalClient;
        public bool IsReconnectingLocalClient => IsReconnectingClient;
        public bool HasLocalClientIdentityTicket { get { lock (localIdentityLock) return !string.IsNullOrEmpty(localClientIdentityTicket); } }
        public bool HasClientReconnectIdentity => !networkShutdownRequested && (IsLocalTestMode ? HasLocalClientIdentityTicket :
            steamReconnectLobbyId != 0 && steamReconnectHostId != 0 && steamReconnectLocalId != 0 && !string.IsNullOrEmpty(steamReconnectGameId) &&
            SteamReady() && ReadSteamAccount() == steamReconnectLocalId);
        public string ClientReconnectGameId => IsLocalTestMode ? string.Empty : steamReconnectGameId ?? string.Empty;
        public event Action LocalClientRestarted;
        public event Action<string> LocalClientReconnectFailed;
        private readonly Dictionary<string, TaskCompletionSource<RoomConfigurationResult>> pendingRoomCommands =
            new Dictionary<string, TaskCompletionSource<RoomConfigurationResult>>(StringComparer.Ordinal);

        public event Action ClientDisconnected;
        public event Action<MirrorServerConnectionInfo> ServerClientConnected;
        public event Action<int> ServerClientDisconnected;
        public event Action LocalClientConnected;
        public event Action LocalClientDisconnected;
        public event Action<int, string> ServerLocalIdentityPresented;
        public event Action<RoomState> WaitingRoomStateReceived;
        public event Action<int, RoomConfigurationMessage> WaitingRoomCommandReceived;
        public static MirrorNetworkRuntime Instance { get; private set; }
        public NetworkManager Manager => networkManager;
        public FizzySteamworks Transport => steamTransport;
        public TelepathyTransport LocalTransport => localTransport;
        public Mirror.Transport ActiveTransport { get; private set; }
        public bool IsLocalTestMode { get; private set; }
        public bool IsHost => NetworkServer.active && NetworkClient.active;

        public static MirrorNetworkRuntime Ensure()
        {
            if (Instance == null)
            {
                throw new InvalidOperationException(
                    "缺少预接线的 NetworkRuntimeRoot。请重建网络运行时 Prefab 并确认 StartScene 接线完整。");
            }

            return Instance;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                gameObject.SetActive(false);
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            if (!TryValidatePersistentConfiguration(out var reason))
            {
                throw new InvalidOperationException("NetworkRuntimeRoot 接线无效：" + reason);
            }

            IsLocalTestMode = LocalMirrorTestMode.IsEnabled;
            LocalTransport.enabled = false;
            Transport.enabled = false;
            if (IsLocalTestMode)
            {
                LocalTransport.port = LocalMirrorTestMode.MirrorPort;
                ActiveTransport = LocalTransport;
            }
            else
            {
                Transport.AllowSteamRelay = true;
                Transport.UseNextGenSteamNetworking = true;
                ActiveTransport = Transport;
            }
            Manager.transport = ActiveTransport;
            Manager.maxConnections = 4;
            Manager.autoCreatePlayer = false;
            Mirror.Transport.active = ActiveTransport;
        }

        public bool TryValidatePersistentConfiguration(out string reason)
        {
            if (networkManager == null || steamTransport == null || localTransport == null)
            {
                reason = "NetworkManager、FizzySteamworks 或 TelepathyTransport 引用为空。";
                return false;
            }

            if (networkManager.gameObject != gameObject ||
                steamTransport.gameObject != gameObject ||
                localTransport.gameObject != gameObject)
            {
                reason = "核心网络组件必须与 MirrorNetworkRuntime 位于同一持久化根对象。";
                return false;
            }

            if (networkManager.maxConnections != 4 || networkManager.autoCreatePlayer)
            {
                reason = "NetworkManager 必须保持 maxConnections=4 且 autoCreatePlayer=false。";
                return false;
            }

            if (localTransport.port != LocalMirrorTestMode.MirrorPort)
            {
                reason = "TelepathyTransport 端口必须与 LocalMirrorTestMode.MirrorPort 一致。";
                return false;
            }

            if (!steamTransport.AllowSteamRelay || !steamTransport.UseNextGenSteamNetworking)
            {
                reason = "FizzySteamworks 必须启用 Steam Relay 与 NextGen Steam Networking。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            ShutdownNetwork();
            Instance = null;
        }

        private void Update()
        {
            while (true)
            {
                int connectionId;
                lock (disconnectQueueLock)
                {
                    if (pendingServerDisconnects.Count == 0) break;
                    connectionId = pendingServerDisconnects.Dequeue();
                }
                DisconnectServerClientNow(connectionId);
            }
            SendWaitingRoomLocalIdentity();
            if (serverIdentityRetryPending && NetworkServer.active && Time.unscaledTime >= nextServerIdentityRetryTime)
            {
                serverIdentityRetryPending = false;
                ServerIdentityRetryRequested?.Invoke();
            }
            // Mirror 回调可能晚于这一帧；已恢复传输时不能再按旧期限关闭房间。
            if (reconnectLocalClient && NetworkClient.isConnected)
            {
                if (TryValidateReconnectIdentity(out var connectedFailure)) OnLocalNetworkConnected();
                else FailClientReconnect(connectedFailure);
            }
            if (reconnectLocalClient && Time.unscaledTime >= nextLocalReconnectTime)
            {
                if (Time.unscaledTime >= localReconnectDeadline)
                {
                    FailClientReconnect(IsLocalTestMode ? "simulation_local_reconnect_timeout" : "simulation_steam_reconnect_timeout");
                }
                else if (!NetworkClient.isConnected && !NetworkClient.isConnecting)
                {
                    if (!TryValidateReconnectIdentity(out var failure)) { FailClientReconnect(failure); return; }
                    nextLocalReconnectTime = Time.unscaledTime + 3f;
                    localReconnectAttempts++;
                    Debug.Log("[LocalMirrorLifecycle] 本地回连尝试=" + localReconnectAttempts + "，地址=" + Manager.networkAddress);
                    UnsubscribeMirrorCallbacks();
                    if (NetworkClient.active) Manager.StopClient();
                    if (IsLocalTestMode) StartLocalClient(Manager.networkAddress);
                    else StartClient(steamReconnectHostId);
                    LocalClientRestarted?.Invoke();
                }
            }
        }

        public bool TryBeginLocalClientReconnect() => IsLocalTestMode && TryBeginClientReconnect();

        public bool TryBeginClientReconnect()
        {
            if (networkShutdownRequested || NetworkServer.active || !HasClientReconnectIdentity || !TryValidateReconnectIdentity(out _)) return false;
            if (reconnectLocalClient) return true;
            reconnectLocalClient = true;
            localReconnectAttempts = 0;
            localReconnectDeadline = Time.unscaledTime + 60f;
            nextLocalReconnectTime = Time.unscaledTime + 1f;
            Debug.Log("[ClientReconnectLifecycle] 已接纳原身份回连，模式=" + (IsLocalTestMode ? "Local" : "Steam") + "，期限秒=60。");
            return true;
        }

        public void ConfigureSteamClientReconnect(ulong lobbyId, ulong hostSteamId, ulong localSteamId, string gameId)
        {
            if (networkShutdownRequested || IsLocalTestMode || NetworkServer.active || lobbyId == 0 || hostSteamId == 0 || localSteamId == 0 ||
                hostSteamId == localSteamId || string.IsNullOrWhiteSpace(gameId) || !SteamReady() || ReadSteamAccount() != localSteamId)
                throw new InvalidOperationException("steam_reconnect_identity_invalid");
            if (steamReconnectLobbyId != 0 && (steamReconnectLobbyId != lobbyId || steamReconnectHostId != hostSteamId ||
                steamReconnectLocalId != localSteamId || steamReconnectGameId != gameId)) throw new InvalidOperationException("steam_reconnect_identity_conflict");
            steamReconnectLobbyId = lobbyId; steamReconnectHostId = hostSteamId; steamReconnectLocalId = localSteamId; steamReconnectGameId = gameId;
            steamReconnectCancelled = false;
        }

        public void ClearSteamClientReconnect()
        {
            if (!IsLocalTestMode) { reconnectLocalClient = false; steamReconnectCancelled = true; }
            steamReconnectLobbyId = steamReconnectHostId = steamReconnectLocalId = 0; steamReconnectGameId = null;
        }

        public void RequestServerIdentityRetry()
        {
            if (networkShutdownRequested || !NetworkServer.active || serverIdentityRetryPending) return;
            serverIdentityRetryPending = true; nextServerIdentityRetryTime = Time.unscaledTime + 3f;
        }

        private bool SteamReady() => steamReadyProbe?.Invoke() ?? SteamBootstrap.IsInitialized;
        private ulong ReadSteamAccount() => steamAccountProbe?.Invoke() ?? Steamworks.SteamUser.GetSteamID().m_SteamID;
        private bool TryValidateReconnectIdentity(out string failure)
        {
            failure = IsLocalTestMode ? "simulation_local_reconnect_identity_lost" : "simulation_steam_reconnect_identity_lost";
            if (!HasClientReconnectIdentity) return false;
            if (IsLocalTestMode) return true;
            var lobby = new Steamworks.CSteamID(steamReconnectLobbyId);
            ulong owner = steamOwnerProbe?.Invoke(steamReconnectLobbyId) ?? Steamworks.SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID;
            if (owner != 0 && owner != steamReconnectHostId) { failure = "simulation_steam_host_changed"; return false; }
            int count = steamMemberCountProbe?.Invoke(steamReconnectLobbyId) ?? Steamworks.SteamMatchmaking.GetNumLobbyMembers(lobby);
            // 0表示平台此刻没有可验证读数；不能以此猜换主或踢掉原成员。
            if (count <= 0) return true;
            bool local = false, host = false;
            for (int i = 0; i < count; i++)
            {
                ulong member = steamMemberProbe?.Invoke(steamReconnectLobbyId, i) ?? Steamworks.SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID;
                local |= member == steamReconnectLocalId; host |= member == steamReconnectHostId;
            }
            if (!local || !host) { failure = !host ? "simulation_steam_host_left" : "simulation_steam_lobby_left"; return false; }
            return true;
        }

        private void FailClientReconnect(string code)
        {
            if (!reconnectLocalClient) return;
            reconnectLocalClient = false;
            if (!IsLocalTestMode) ClearSteamClientReconnect();
            Debug.LogWarning("[ClientReconnectLifecycle] 原身份回连终止，原因=" + code);
            LocalClientReconnectFailed?.Invoke(code); ClientDisconnected?.Invoke();
        }

        public void StartHost()
        {
            if (IsLocalTestMode) throw new InvalidOperationException("本地测试模式必须使用 StartLocalHost。");
            if (!SteamBootstrap.IsInitialized) throw new InvalidOperationException("Steam 尚未初始化。");
            if (!NetworkServer.active && !NetworkClient.active)
            {
                ActivateConfiguredTransport();
                PrepareForNetworkStart();
                Manager.StartHost();
                SubscribeMirrorCallbacks();
                PublishExistingConnections();
            }
        }

        public void StartLocalHost()
        {
            if (!IsLocalTestMode) throw new InvalidOperationException("当前未启用 Mirror 本地测试模式。");
            if (!NetworkServer.active && !NetworkClient.active)
            {
                ActivateConfiguredTransport();
                PrepareForNetworkStart();
                Manager.StartHost();
                SubscribeMirrorCallbacks();
                NetworkServer.RegisterHandler<WaitingRoomLocalIdentityMessage>(OnWaitingRoomLocalIdentity, false);
                PublishExistingConnections();
            }
        }

        public void StartClient(ulong hostSteamId)
        {
            if (IsLocalTestMode) throw new InvalidOperationException("本地测试模式必须使用 StartLocalClient。");
            if (!SteamReady()) throw new InvalidOperationException("Steam 尚未初始化。");
            if (hostSteamId == 0) throw new ArgumentOutOfRangeException(nameof(hostSteamId));
            Manager.networkAddress = hostSteamId.ToString();
            if (!NetworkClient.active && !NetworkServer.active)
            {
                ActivateConfiguredTransport();
                PrepareForNetworkStart();
                Manager.StartClient();
                SubscribeMirrorCallbacks();
                PublishCurrentClientState();
            }
        }

        public void StartLocalClient(string host)
        {
            if (!IsLocalTestMode) throw new InvalidOperationException("当前未启用 Mirror 本地测试模式。");
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("本地 Mirror 主机地址不能为空。", nameof(host));
            Manager.networkAddress = host.Trim();
            if (!NetworkClient.active && !NetworkServer.active)
            {
                ActivateConfiguredTransport();
                PrepareForNetworkStart();
                Manager.StartClient();
                SubscribeMirrorCallbacks();
                PublishCurrentClientState();
            }
        }

        public void SetLocalClientIdentityTicket(string ticket)
        {
            lock (localIdentityLock)
            {
                localClientIdentityTicket = ticket ?? string.Empty;
                localIdentitySendPending = !string.IsNullOrEmpty(localClientIdentityTicket);
            }
        }

        public void RequestServerDisconnect(int connectionId)
        {
            if (connectionId < 0) return;
            lock (disconnectQueueLock)
                if (!pendingServerDisconnects.Contains(connectionId)) pendingServerDisconnects.Enqueue(connectionId);
        }

        public bool IsServerClientActive(int connectionId)
        {
            return NetworkServer.active && NetworkServer.connections.ContainsKey(connectionId) &&
                   !disconnectingServerConnections.Contains(connectionId);
        }

        public void BroadcastWaitingRoomState(RoomState room)
        {
            if (!NetworkServer.active || room == null) return;
            var message = new WaitingRoomStateMessage { Json = RoomStateSerializer.Serialize(room) };
            foreach (var connection in NetworkServer.connections.Values)
            {
                if (connection == null || connection is LocalConnectionToClient) continue;
                connection.Send(message, Channels.Reliable);
            }
        }

        public void ShutdownNetwork()
        {
            networkShutdownRequested = true;
            serverIdentityRetryPending = false;
            ClearSteamClientReconnect();
            reconnectLocalClient = false;
            localReconnectAttempts = 0;
            NetworkServer.UnregisterHandler<WaitingRoomCommandMessage>();
            NetworkClient.UnregisterHandler<WaitingRoomCommandReplyMessage>();
            foreach (var completion in pendingRoomCommands.Values)
                completion.TrySetResult(RoomConfigurationService.Result(null, "service_unavailable"));
            pendingRoomCommands.Clear();
            if (NetworkServer.active && IsLocalTestMode)
                NetworkServer.UnregisterHandler<WaitingRoomLocalIdentityMessage>();
            NetworkClient.UnregisterHandler<WaitingRoomStateMessage>();
            UnsubscribeMirrorCallbacks();
            if (NetworkServer.active && NetworkClient.active) Manager.StopHost();
            else if (NetworkClient.active) Manager.StopClient();
            else if (NetworkServer.active) Manager.StopServer();
            if (ActiveTransport != null)
            {
                // 必须在 Steam API 释放前关闭传输层，并取消尚未执行的 Relay 初始化。
                ActiveTransport.CancelInvoke();
                ActiveTransport.Shutdown();
                ActiveTransport.enabled = false;
            }
            publishedServerConnections.Clear();
            disconnectingServerConnections.Clear();
            localClientConnectedPublished = false;
            localClientDisconnectedPublished = false;
            lock (localIdentityLock)
            {
                localClientIdentityTicket = string.Empty;
                localIdentitySendPending = false;
            }
            lock (disconnectQueueLock) pendingServerDisconnects.Clear();
        }

        private void SubscribeMirrorCallbacks()
        {
            if (mirrorCallbacksSubscribed) return;
            NetworkServer.OnConnectedEvent += OnServerConnected;
            NetworkServer.OnDisconnectedEvent += OnServerDisconnected;
            NetworkClient.OnConnectedEvent += OnLocalNetworkConnected;
            NetworkClient.OnDisconnectedEvent -= OnLocalNetworkDisconnected;
            NetworkClient.OnDisconnectedEvent += OnLocalNetworkDisconnected;
            if (ActiveTransport != null)
            {
                ActiveTransport.OnClientError += OnTransportClientError;
                ActiveTransport.OnServerError += OnTransportServerError;
            }
            mirrorCallbacksSubscribed = true;
        }

        private void UnsubscribeMirrorCallbacks()
        {
            if (!mirrorCallbacksSubscribed) return;
            NetworkServer.OnConnectedEvent -= OnServerConnected;
            NetworkServer.OnDisconnectedEvent -= OnServerDisconnected;
            NetworkClient.OnConnectedEvent -= OnLocalNetworkConnected;
            NetworkClient.OnDisconnectedEvent -= OnLocalNetworkDisconnected;
            if (ActiveTransport != null)
            {
                ActiveTransport.OnClientError -= OnTransportClientError;
                ActiveTransport.OnServerError -= OnTransportServerError;
            }
            mirrorCallbacksSubscribed = false;
        }

        private void PublishExistingConnections()
        {
            foreach (var connection in NetworkServer.connections.Values) OnServerConnected(connection);
            PublishCurrentClientState();
        }

        private void PublishCurrentClientState()
        {
            if (NetworkClient.isConnected) OnLocalNetworkConnected();
            else if (!NetworkClient.active) OnLocalNetworkDisconnected();
        }

        private void OnServerConnected(NetworkConnectionToClient connection)
        {
            if (connection == null || !publishedServerConnections.Add(connection.connectionId)) return;
            ServerClientConnected?.Invoke(new MirrorServerConnectionInfo(
                connection.connectionId,
                connection.address,
                connection is LocalConnectionToClient));
        }

        private void OnServerDisconnected(NetworkConnectionToClient connection)
        {
            if (connection == null) return;
            publishedServerConnections.Remove(connection.connectionId);
            disconnectingServerConnections.Remove(connection.connectionId);
            ServerClientDisconnected?.Invoke(connection.connectionId);
        }

        private void OnLocalNetworkConnected()
        {
            if (networkShutdownRequested || !IsLocalTestMode && steamReconnectCancelled || localClientConnectedPublished) return;
            if (reconnectLocalClient && !TryValidateReconnectIdentity(out var failure)) { FailClientReconnect(failure); return; }
            localClientConnectedPublished = true;
            localClientDisconnectedPublished = false;
            bool wasReconnecting = reconnectLocalClient;
            reconnectLocalClient = false;
            if (wasReconnecting) Debug.Log("[LocalMirrorLifecycle] 本地传输已回连，等待原身份认证与会话接续。");
            LocalClientConnected?.Invoke();
            lock (localIdentityLock)
                localIdentitySendPending = !string.IsNullOrEmpty(localClientIdentityTicket);
            SendWaitingRoomLocalIdentity();
        }

        private void OnLocalNetworkDisconnected()
        {
            if (networkShutdownRequested || localClientDisconnectedPublished) return;
            localClientDisconnectedPublished = true;
            localClientConnectedPublished = false;
            LocalClientDisconnected?.Invoke();
            if (networkShutdownRequested) return;
            Debug.LogWarning("[LocalMirrorLifecycle] 本地连接断开，模式=" + IsLocalTestMode + "，回连接纳=" + reconnectLocalClient + "，原票据存在=" + HasLocalClientIdentityTicket);
            // 已接纳的物理断线不是房间终结；保留原断线事件，期限耗尽才发终结。
            if (!reconnectLocalClient) ClientDisconnected?.Invoke();
        }

        private void OnTransportClientError(TransportError error, string reason)
            => Debug.LogWarning("[LocalMirrorLifecycle] 传输客户端错误；utc=" + DateTime.UtcNow.ToString("O") + "，错误=" + error + "，原因=" + reason);

        private void OnTransportServerError(int connectionId, TransportError error, string reason)
            => Debug.LogWarning("[LocalMirrorLifecycle] 传输服务端错误；utc=" + DateTime.UtcNow.ToString("O") + "，连接=" + connectionId + "，错误=" + error + "，原因=" + reason);

        private void PrepareForNetworkStart()
        {
            networkShutdownRequested = false;
            steamReconnectCancelled = false;
            publishedServerConnections.Clear();
            disconnectingServerConnections.Clear();
            localClientConnectedPublished = false;
            localClientDisconnectedPublished = false;
            NetworkClient.RegisterHandler<WaitingRoomStateMessage>(OnWaitingRoomState, false);
            NetworkClient.RegisterHandler<WaitingRoomCommandReplyMessage>(OnWaitingRoomCommandReply, false);
            NetworkServer.RegisterHandler<WaitingRoomCommandMessage>(OnWaitingRoomCommand, false);
            lock (disconnectQueueLock) pendingServerDisconnects.Clear();
        }

        private void ActivateConfiguredTransport()
        {
            if (ActiveTransport == null)
            {
                throw new InvalidOperationException("NetworkRuntimeRoot 尚未选择活动 Transport。");
            }

            LocalTransport.enabled = ActiveTransport == LocalTransport;
            Transport.enabled = ActiveTransport == Transport;
            Manager.transport = ActiveTransport;
            Mirror.Transport.active = ActiveTransport;
        }

        private void OnWaitingRoomLocalIdentity(
            NetworkConnectionToClient connection,
            WaitingRoomLocalIdentityMessage message)
        {
            if (!IsLocalTestMode || connection == null) return;
            ServerLocalIdentityPresented?.Invoke(connection.connectionId, message.Ticket);
        }

        private void OnWaitingRoomState(WaitingRoomStateMessage message)
        {
            if (NetworkServer.active || string.IsNullOrEmpty(message.Json)) return;
            if (!RoomStateSerializer.TryDeserialize(message.Json, out var room, out var reason))
            { Debug.LogError(reason); NetworkClient.Disconnect(); return; }
            WaitingRoomStateReceived?.Invoke(room);
        }

        public async Task<RoomConfigurationResult> SendWaitingRoomCommandAsync(RoomConfigurationMessage message)
        {
            if (!NetworkClient.isConnected || pendingRoomCommands.Count >= 32)
                return RoomConfigurationService.Result(null, "service_unavailable");
            var commandId = message.ReadRequest().CommandId;
            if (!pendingRoomCommands.TryGetValue(commandId, out var completion))
            {
                completion = new TaskCompletionSource<RoomConfigurationResult>();
                pendingRoomCommands.Add(commandId, completion);
                NetworkClient.Send(new WaitingRoomCommandMessage { Json = JsonConvert.SerializeObject(message) }, Channels.Reliable);
            }
            if (await Task.WhenAny(completion.Task, Task.Delay(10000)) != completion.Task)
            { pendingRoomCommands.Remove(commandId); return RoomConfigurationService.Result(null, "service_unavailable"); }
            return await completion.Task;
        }

        public void SendWaitingRoomCommandReply(int connectionId, RoomConfigurationReply reply)
        {
            if (NetworkServer.connections.TryGetValue(connectionId, out var connection))
                connection.Send(new WaitingRoomCommandReplyMessage { Json = JsonConvert.SerializeObject(reply) }, Channels.Reliable);
        }

        private void OnWaitingRoomCommand(NetworkConnectionToClient connection, WaitingRoomCommandMessage message)
        {
            if (connection == null) return;
            try
            {
                if (!RoomConfigurationMessage.TryDeserialize(message.Json, out var command)) { connection.Disconnect(); return; }
                WaitingRoomCommandReceived?.Invoke(connection.connectionId, command);
            }
            catch (JsonException) { connection.Disconnect(); }
        }

        private void OnWaitingRoomCommandReply(WaitingRoomCommandReplyMessage message)
        {
            if (NetworkServer.active) return;
            try
            {
                if (!RoomConfigurationReply.TryDeserialize(message.Json, out var reply))
                { Debug.LogError("room_protocol_mismatch"); NetworkClient.Disconnect(); return; }
                if (!pendingRoomCommands.TryGetValue(reply.CommandId, out var completion)) return;
                pendingRoomCommands.Remove(reply.CommandId);
                completion.TrySetResult(reply.Result);
            }
            catch (JsonException) { Debug.LogError("room_protocol_mismatch"); NetworkClient.Disconnect(); }
        }

        private void SendWaitingRoomLocalIdentity()
        {
            if (!IsLocalTestMode || !NetworkClient.isConnected) return;
            string ticket;
            lock (localIdentityLock)
            {
                if (!localIdentitySendPending || string.IsNullOrEmpty(localClientIdentityTicket)) return;
                ticket = localClientIdentityTicket;
                localIdentitySendPending = false;
            }
            NetworkClient.Send(new WaitingRoomLocalIdentityMessage { Ticket = ticket });
        }

        private void DisconnectServerClientNow(int connectionId)
        {
            if (!NetworkServer.connections.TryGetValue(connectionId, out var connection)) return;
            disconnectingServerConnections.Add(connectionId);
            connection.Disconnect();
        }
    }
}
