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

    public sealed class MirrorNetworkRuntime : MonoBehaviour
    {
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
        public NetworkManager Manager { get; private set; }
        public FizzySteamworks Transport { get; private set; }
        public TelepathyTransport LocalTransport { get; private set; }
        public Mirror.Transport ActiveTransport { get; private set; }
        public bool IsLocalTestMode { get; private set; }
        public bool IsHost => NetworkServer.active && NetworkClient.active;

        public static MirrorNetworkRuntime Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("MirrorNetworkRuntime");
            DontDestroyOnLoad(go);
            return go.AddComponent<MirrorNetworkRuntime>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            IsLocalTestMode = LocalMirrorTestMode.IsEnabled;
            if (IsLocalTestMode)
            {
                LocalTransport = GetComponent<TelepathyTransport>() ?? gameObject.AddComponent<TelepathyTransport>();
                LocalTransport.port = LocalMirrorTestMode.MirrorPort;
                ActiveTransport = LocalTransport;
            }
            else
            {
                Transport = GetComponent<FizzySteamworks>() ?? gameObject.AddComponent<FizzySteamworks>();
                Transport.AllowSteamRelay = true;
                Transport.UseNextGenSteamNetworking = true;
                ActiveTransport = Transport;
            }
            Manager = GetComponent<NetworkManager>() ?? gameObject.AddComponent<NetworkManager>();
            Manager.transport = ActiveTransport;
            Manager.maxConnections = 4;
            Manager.autoCreatePlayer = false;
            Mirror.Transport.active = ActiveTransport;
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
