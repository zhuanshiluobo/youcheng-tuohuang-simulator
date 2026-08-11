using System;
using System.Collections.Generic;
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

        public event Action ClientDisconnected;
        public event Action<MirrorServerConnectionInfo> ServerClientConnected;
        public event Action<int> ServerClientDisconnected;
        public event Action LocalClientConnected;
        public event Action LocalClientDisconnected;
        public event Action<int, string> ServerLocalIdentityPresented;
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
            if (!SteamBootstrap.IsInitialized) throw new InvalidOperationException("Steam 尚未初始化。");
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

        public void ShutdownNetwork()
        {
            if (NetworkServer.active && IsLocalTestMode)
                NetworkServer.UnregisterHandler<WaitingRoomLocalIdentityMessage>();
            UnsubscribeMirrorCallbacks();
            if (NetworkServer.active && NetworkClient.active) Manager.StopHost();
            else if (NetworkClient.active) Manager.StopClient();
            else if (NetworkServer.active) Manager.StopServer();
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
            mirrorCallbacksSubscribed = true;
        }

        private void UnsubscribeMirrorCallbacks()
        {
            if (!mirrorCallbacksSubscribed) return;
            NetworkServer.OnConnectedEvent -= OnServerConnected;
            NetworkServer.OnDisconnectedEvent -= OnServerDisconnected;
            NetworkClient.OnConnectedEvent -= OnLocalNetworkConnected;
            NetworkClient.OnDisconnectedEvent -= OnLocalNetworkDisconnected;
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
            if (localClientConnectedPublished) return;
            localClientConnectedPublished = true;
            localClientDisconnectedPublished = false;
            LocalClientConnected?.Invoke();
            lock (localIdentityLock)
                localIdentitySendPending = !string.IsNullOrEmpty(localClientIdentityTicket);
            SendWaitingRoomLocalIdentity();
        }

        private void OnLocalNetworkDisconnected()
        {
            if (localClientDisconnectedPublished) return;
            localClientDisconnectedPublished = true;
            localClientConnectedPublished = false;
            LocalClientDisconnected?.Invoke();
            ClientDisconnected?.Invoke();
        }

        private void PrepareForNetworkStart()
        {
            publishedServerConnections.Clear();
            disconnectingServerConnections.Clear();
            localClientConnectedPublished = false;
            localClientDisconnectedPublished = false;
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
