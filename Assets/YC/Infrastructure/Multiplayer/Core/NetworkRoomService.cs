using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using YC.Application.Sessions;
using YC.Domain.Rules;

namespace YC.Infrastructure.Multiplayer
{
    public sealed class NetworkRoomService : IDisposable
    {
        private const int DefaultPort = 7780;
        private const int MaxPort = 7799;
        private const int ConnectTimeoutMilliseconds = 5000;

        private readonly IRoomContentCapabilityProvider content;
        private readonly RoomConfigurationService configurationService;
        private readonly Dictionary<string, TaskCompletionSource<RoomConfigurationResult>> pendingCommands =
            new Dictionary<string, TaskCompletionSource<RoomConfigurationResult>>(StringComparer.Ordinal);
        private int nextPlayerId = 2;

        private readonly object syncRoot = new object();
        private readonly List<ClientConnection> hostClients = new List<ClientConnection>();
        private readonly Dictionary<int, ClientConnection> seatOwners = new Dictionary<int, ClientConnection>();
        private readonly Dictionary<string, int> playerByIdentityTicket =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<Thread> hostClientThreads = new List<Thread>();
        private readonly bool enableNetworking;

        private TcpListener listener;
        private TcpClient client;
        private StreamWriter clientWriter;
        private Thread acceptThread;
        private Thread clientReadThread;
        private bool isDisposed;
        private bool isHost;
        private volatile bool suppressDisconnectNotice;
        private long roomGeneration;
        private RoomState currentRoom;
        private string localIdentityTicket = string.Empty;

        public event Action<RoomState> RoomUpdated;
        public event Action<RoomState> GameStarted;
        public event Action RoomDisbanded;
        public event Action<string> ErrorOccurred;

        public NetworkRoomService()
            : this(true, null)
        {
        }

        public NetworkRoomService(IRoomContentCapabilityProvider content) : this(true, content) { }

        internal NetworkRoomService(bool enableNetworking, IRoomContentCapabilityProvider content = null)
        {
            this.enableNetworking = enableNetworking;
            this.content = content;
            configurationService = new RoomConfigurationService(content);
        }

        internal int HostClientCount
        {
            get
            {
                lock (syncRoot)
                {
                    return hostClients.Count;
                }
            }
        }

        internal bool HasActiveBackgroundThreads
        {
            get
            {
                lock (syncRoot)
                {
                    if (acceptThread != null && acceptThread.IsAlive) return true;
                    if (clientReadThread != null && clientReadThread.IsAlive) return true;
                    for (var i = 0; i < hostClientThreads.Count; i++)
                    {
                        if (hostClientThreads[i].IsAlive) return true;
                    }

                    return false;
                }
            }
        }

        public string LocalIdentityTicket
        {
            get
            {
                lock (syncRoot) return localIdentityTicket;
            }
        }

        public RoomState CreateRoom(string hostPlayerName, int playerCount)
        {
            ThrowIfDisposed();
            Shutdown();

            if (playerCount < 2 || playerCount > 4) throw new ArgumentOutOfRangeException(nameof(playerCount));
            var port = enableNetworking ? StartListener() : DefaultPort;
            var roomId = (enableNetworking ? GetLocalAddress() : IPAddress.Loopback.ToString()) + ":" + port;
            TcpListener activeListener;
            lock (syncRoot)
            {
                suppressDisconnectNotice = false;
                isHost = true;
                roomGeneration++;
                currentRoom = CreateDefaultRoom(roomId, playerCount);
                nextPlayerId = 2;
                currentRoom.LocalPlayerId = currentRoom.HostPlayerId;
                currentRoom.Seats[0].PlayerName = string.IsNullOrEmpty(hostPlayerName) ? "Player 1" : hostPlayerName;
                currentRoom.Seats[0].LobbyMemberPresent = true;
                currentRoom.Configuration.ContentPackHash = content?.GetContentPackHash(currentRoom.Configuration) ?? string.Empty;
                currentRoom.Seats[0].ContentPackHash = currentRoom.Configuration.ContentPackHash;
                currentRoom.Seats[0].RuleContractVersion = currentRoom.Configuration.RuleContractVersion;
                activeListener = listener;
            }

            if (activeListener != null)
            {
                acceptThread = new Thread(() => AcceptLoop(activeListener)) { IsBackground = true };
                acceptThread.Start();
            }

            RaiseRoomUpdated();
            return GetCurrentRoom();
        }

        public RoomState JoinRoom(string roomId, string playerName)
        {
            ThrowIfDisposed();
            if (!enableNetworking) throw new InvalidOperationException("Networking is disabled for this room service instance.");
            Shutdown();

            var endpoint = ParseRoomEndpoint(roomId);
            var activeClient = new TcpClient();
            ConnectWithTimeout(activeClient, endpoint);

            var stream = activeClient.GetStream();
            var activeWriter = new StreamWriter(stream) { AutoFlush = true };
            var localConfiguration = new GameRuleConfiguration();
            activeWriter.WriteLine("JOIN|2|" + Escape(playerName) + "|" +
                (content?.GetContentPackHash(localConfiguration) ?? string.Empty) + "|" + localConfiguration.RuleContractVersion);

            lock (syncRoot)
            {
                suppressDisconnectNotice = false;
                isHost = false;
                roomGeneration++;
                client = activeClient;
                clientWriter = activeWriter;
                currentRoom = new RoomState
                {
                    RoomId = roomId,
                    LocalPlayerId = -1
                };
            }

            clientReadThread = new Thread(() => ClientReadLoop(activeClient)) { IsBackground = true };
            clientReadThread.Start();

            RaiseRoomUpdated();
            return GetCurrentRoom();
        }

        public RoomState GetCurrentRoom()
        {
            lock (syncRoot)
            {
                return currentRoom == null ? null : currentRoom.Clone();
            }
        }

        public RoomState SetTransportReadiness(
            int playerId,
            bool transportConnected,
            bool identityVerified,
            ulong networkClientId)
        {
            long generation = 0;
            lock (syncRoot)
            {
                if (!isHost || currentRoom == null) return GetCurrentRoom();
                for (var i = 0; i < currentRoom.Seats.Count; i++)
                {
                    var seat = currentRoom.Seats[i];
                    if (seat.PlayerId != playerId) continue;
                    var connected = seat.LobbyMemberPresent && transportConnected;
                    var verified = connected && identityVerified;
                    var ready = verified;
                    if (seat.TransportConnected == connected && seat.IdentityVerified == verified &&
                        seat.IsReady == ready && seat.NetworkClientId == (verified ? networkClientId : 0UL))
                        return currentRoom.Clone();
                    seat.TransportConnected = connected;
                    seat.IdentityVerified = verified;
                    seat.NetworkClientId = verified ? networkClientId : 0UL;
                    seat.GameStateSynchronized = false;
                    seat.IsReady = ready;
                    if (!ready)
                    {
                        var slot = RoomConfigurationService.FindSlot(currentRoom, playerId);
                        slot.IsUserReady = false; slot.ReadyConfigurationRevision = -1;
                    }
                    currentRoom.StateRevision++;
                    generation = roomGeneration;
                    break;
                }
            }

            if (generation != 0) PublishRoomUpdate(generation);
            return GetCurrentRoom();
        }

        public bool TryResolveIdentityTicket(string ticket, out int playerId)
        {
            lock (syncRoot)
            {
                playerId = -1;
                if (!isHost || suppressDisconnectNotice || string.IsNullOrEmpty(ticket) ||
                    !playerByIdentityTicket.TryGetValue(ticket, out var resolvedPlayerId) ||
                    !seatOwners.TryGetValue(resolvedPlayerId, out var owner) || owner.IsDisposed ||
                    !string.Equals(owner.IdentityTicket, ticket, StringComparison.Ordinal))
                    return false;
                playerId = resolvedPlayerId;
                return true;
            }
        }

        public void StartGame() => TryStartGame(out _);

        public async Task<RoomConfigurationResult> SendConfigurationAsync(RoomConfigurationRequest request)
        {
            ThrowIfDisposed();
            RoomConfigurationMessage message;
            TaskCompletionSource<RoomConfigurationResult> completion;
            lock (syncRoot)
            {
                if (currentRoom == null) return RoomConfigurationService.Result(null, "service_unavailable");
                if (request == null || request.RequestingPlayerId != currentRoom.LocalPlayerId)
                    return RoomConfigurationService.Result(currentRoom, "room_protocol_mismatch");
                message = RoomConfigurationMessage.Create(request, content, currentRoom.Configuration);
                if (!isHost)
                {
                    if (clientWriter == null || pendingCommands.Count >= 32) return RoomConfigurationService.Result(currentRoom, "service_unavailable");
                    if (!pendingCommands.TryGetValue(request.CommandId, out completion))
                    {
                        completion = new TaskCompletionSource<RoomConfigurationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                        pendingCommands.Add(request.CommandId, completion);
                        try { clientWriter.WriteLine("COMMAND|" + JsonConvert.SerializeObject(message)); }
                        catch (IOException) { pendingCommands.Remove(request.CommandId); return RoomConfigurationService.Result(currentRoom, "service_unavailable"); }
                    }
                }
                else completion = null;
            }
            if (completion == null) return ExecuteAuthenticatedConfiguration(message, request.RequestingPlayerId);
            if (await Task.WhenAny(completion.Task, Task.Delay(10000)).ConfigureAwait(false) != completion.Task)
            {
                lock (syncRoot) pendingCommands.Remove(request.CommandId);
                return RoomConfigurationService.Result(GetCurrentRoom(), "service_unavailable");
            }
            return await completion.Task.ConfigureAwait(false);
        }

        internal RoomConfigurationResult ExecuteAuthenticatedConfiguration(RoomConfigurationMessage message, int authenticatedPlayerId)
        {
            RoomConfigurationResult result;
            bool started;
            long generation;
            lock (syncRoot)
            {
                if (!isHost || currentRoom == null) return RoomConfigurationService.Result(currentRoom, "service_unavailable");
                var wasStarted = currentRoom.HasStarted;
                result = message.Execute(currentRoom, authenticatedPlayerId, configurationService);
                started = result.Success && !wasStarted && currentRoom.HasStarted;
                generation = roomGeneration;
            }
            if (result.Success)
            {
                PublishRoomUpdate(generation);
                if (started) { Broadcast("START|" + SerializeRoom(result.Snapshot)); RaiseGameStarted(); }
            }
            return result;
        }

        public bool TryStartGame(out string error)
        {
            error = null;
            RoomState roomToStart = null;
            long generation = 0;
            lock (syncRoot)
            {
                if (!isHost)
                {
                    error = "not_room_host";
                }
                else if (currentRoom == null)
                {
                    error = "service_unavailable";
                }
                else if (!configurationService.TryFreezeForLaunch(currentRoom, new StartConfiguredGameRequest
                {
                    CommandId = Guid.NewGuid().ToString("N"), RoomId = currentRoom.RoomId,
                    RequestingPlayerId = currentRoom.HostPlayerId,
                    ExpectedConfigurationRevision = currentRoom.Configuration.ConfigurationRevision
                }, out _, out error))
                {
                }
                else
                {
                    currentRoom.HasStarted = true;
                    roomToStart = currentRoom.Clone();
                    generation = roomGeneration;
                }
            }

            if (!string.IsNullOrEmpty(error))
            {
                RaiseError(error);
                return false;
            }

            Broadcast("START|" + SerializeRoom(roomToStart));
            if (IsCurrentGeneration(generation)) RaiseGameStarted();
            return true;
        }

        public void Shutdown()
        {
            bool shouldNotifyClients;
            lock (syncRoot)
            {
                suppressDisconnectNotice = true;
                roomGeneration++;
                shouldNotifyClients = isHost && currentRoom != null && !currentRoom.HasStarted;
            }

            if (shouldNotifyClients)
            {
                Broadcast("DISBAND|房间已解散。");
            }

            ClientConnection[] connections;
            Thread[] connectionThreads;
            TcpListener listenerToStop;
            TcpClient clientToClose;
            StreamWriter writerToDispose;
            Thread acceptThreadToJoin;
            Thread clientThreadToJoin;
            lock (syncRoot)
            {
                connections = hostClients.ToArray();
                connectionThreads = hostClientThreads.ToArray();
                listenerToStop = listener;
                clientToClose = client;
                writerToDispose = clientWriter;
                acceptThreadToJoin = acceptThread;
                clientThreadToJoin = clientReadThread;
                listener = null;
                client = null;
                clientWriter = null;
                acceptThread = null;
                clientReadThread = null;
            }

            if (listenerToStop != null)
            {
                try { listenerToStop.Stop(); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                catch (Exception) { }
            }

            for (var i = 0; i < connections.Length; i++)
            {
                RemoveClientConnection(connections[i], false);
            }

            if (clientToClose != null)
            {
                try { clientToClose.Close(); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                catch (Exception) { }
            }

            if (writerToDispose != null)
            {
                try { writerToDispose.Dispose(); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (Exception) { }
            }

            JoinThread(acceptThreadToJoin);
            JoinThread(clientThreadToJoin);
            for (var i = 0; i < connectionThreads.Length; i++) JoinThread(connectionThreads[i]);

            lock (syncRoot)
            {
                hostClients.Clear();
                seatOwners.Clear();
                playerByIdentityTicket.Clear();
                hostClientThreads.RemoveAll(thread => !thread.IsAlive);
                currentRoom = null;
                foreach (var pending in pendingCommands.Values) pending.TrySetResult(RoomConfigurationService.Result(null, "service_unavailable"));
                pendingCommands.Clear();
                localIdentityTicket = string.Empty;
                isHost = false;
            }
        }

        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            Shutdown();
        }

        private int StartListener()
        {
            for (var port = DefaultPort; port <= MaxPort; port++)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Any, port);
                    listener.Start();
                    return port;
                }
                catch (SocketException)
                {
                    listener = null;
                }
            }

            throw new InvalidOperationException("No available room port found.");
        }

        private void AcceptLoop(TcpListener activeListener)
        {
            while (true)
            {
                try
                {
                    var acceptedClient = activeListener.AcceptTcpClient();
                    var connection = new ClientConnection(acceptedClient);
                    var thread = new Thread(() => HostClientLoop(connection)) { IsBackground = true };
                    connection.ReadThread = thread;
                    var registered = false;
                    lock (syncRoot)
                    {
                        if (ReferenceEquals(listener, activeListener) &&
                            isHost &&
                            !suppressDisconnectNotice &&
                            currentRoom != null)
                        {
                            hostClients.Add(connection);
                            hostClientThreads.Add(thread);
                            thread.Start();
                            registered = true;
                        }
                    }

                    if (!registered)
                    {
                        connection.Dispose();
                        return;
                    }
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        internal void HostClientLoop(ClientConnection connection)
        {
            try
            {
                var line = connection.ReadLine();
                if (line == null || !line.StartsWith("JOIN|", StringComparison.Ordinal))
                {
                    return;
                }

                var joinParts = line.Split('|');
                if (joinParts.Length != 5 || joinParts[1] != "2" || joinParts[4] != GameRuleConfiguration.CurrentRuleContractVersion)
                { connection.WriteLine("ERROR|room_protocol_mismatch"); return; }
                var playerName = Unescape(joinParts[2]);
                var assignedPlayerId = AssignSeat(playerName, connection);
                if (assignedPlayerId < 0)
                {
                    connection.WriteLine("ERROR|Room is full.");
                    return;
                }

                connection.WriteLine("WELCOME|" + assignedPlayerId + "|" + connection.IdentityTicket);
                lock (syncRoot)
                {
                    var player = RoomConfigurationService.FindSlot(currentRoom, assignedPlayerId).Occupant;
                    player.ContentPackHash = joinParts[3]; player.RuleContractVersion = joinParts[4];
                }
                connection.WriteLine("ROOM|" + SerializeRoom(GetCurrentRoom()));
                Broadcast("ROOM|" + SerializeRoom(GetCurrentRoom()));
                if (IsSeatOwner(connection)) RaiseRoomUpdated();

                while (true)
                {
                    var commandLine = connection.ReadLine();
                    if (commandLine == null) return;
                    if (!commandLine.StartsWith("COMMAND|", StringComparison.Ordinal)) continue;
                    try
                    {
                        if (!RoomConfigurationMessage.TryDeserialize(commandLine.Substring(8), out var message))
                        { connection.WriteLine("ERROR|room_protocol_mismatch"); continue; }
                        var request = message.ReadRequest();
                        var result = IsSeatOwner(connection) ? ExecuteAuthenticatedConfiguration(message, connection.PlayerId) :
                            RoomConfigurationService.Result(GetCurrentRoom(), "room_protocol_mismatch");
                        connection.WriteLine("RESULT|" + JsonConvert.SerializeObject(new RoomConfigurationReply { CommandId = request.CommandId, Result = result }));
                    }
                    catch (JsonException) { connection.WriteLine("ERROR|room_protocol_mismatch"); }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                RemoveClientConnection(connection);
            }
        }

        internal void RemoveClientConnection(ClientConnection connection, bool publishRoomChange = true)
        {
            var roomChanged = false;
            long generation = 0;
            lock (syncRoot)
            {
                hostClients.Remove(connection);
                ClientConnection owner;
                if (connection.PlayerId > 0 &&
                    seatOwners.TryGetValue(connection.PlayerId, out owner) &&
                    ReferenceEquals(owner, connection))
                {
                    seatOwners.Remove(connection.PlayerId);
                    if (!string.IsNullOrEmpty(connection.IdentityTicket))
                        playerByIdentityTicket.Remove(connection.IdentityTicket);
                    if (currentRoom != null && !currentRoom.HasStarted)
                    {
                        roomChanged = configurationService.VacateSeat(currentRoom, connection.PlayerId);
                    }
                }

                if (roomChanged && publishRoomChange && !suppressDisconnectNotice)
                {
                    generation = roomGeneration;
                }
            }

            connection.Dispose();
            if (generation != 0) PublishRoomUpdate(generation);
        }

        internal int AssignSeat(string playerName, ClientConnection connection)
        {
            lock (syncRoot)
            {
                if (currentRoom == null || currentRoom.HasStarted || suppressDisconnectNotice)
                {
                    return -1;
                }

                if (!hostClients.Contains(connection)) hostClients.Add(connection);
                ClientConnection existingOwner;
                if (connection.PlayerId > 0 &&
                    seatOwners.TryGetValue(connection.PlayerId, out existingOwner) &&
                    ReferenceEquals(existingOwner, connection))
                {
                    return connection.PlayerId;
                }

                var playerId = nextPlayerId;
                var player = new PlayerSeat
                {
                    PlayerId = playerId, SteamId = LocalMirrorIdentity.ForPlayer(playerId),
                    PlayerName = string.IsNullOrEmpty(playerName) ? "玩家 " + playerId : playerName,
                    LobbyMemberPresent = true
                };
                if (!configurationService.TryOccupySeat(currentRoom, player, out _)) return -1;
                nextPlayerId++;
                connection.PlayerId = playerId;
                connection.IdentityTicket = Guid.NewGuid().ToString("N");
                seatOwners[playerId] = connection;
                playerByIdentityTicket[connection.IdentityTicket] = playerId;
                return playerId;
            }

        }

        private void ClientReadLoop(TcpClient tcpClient)
        {
            try
            {
                var reader = new StreamReader(tcpClient.GetStream());
                while (tcpClient.Connected)
                {
                    var line = reader.ReadLine();
                    if (line == null)
                    {
                        if (!suppressDisconnectNotice)
                        {
                            RaiseRoomDisbanded();
                        }

                        return;
                    }

                    HandleServerMessage(line);
                }
            }
            catch (IOException ex)
            {
                if (suppressDisconnectNotice)
                {
                    return;
                }

                var room = GetCurrentRoom();
                if (room != null && room.LocalPlayerId != room.HostPlayerId)
                {
                    RaiseRoomDisbanded();
                    return;
                }

                RaiseError(ex.Message);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void HandleServerMessage(string line)
        {
            if (line.StartsWith("RESULT|", StringComparison.Ordinal))
            {
                try
                {
                    if (!RoomConfigurationReply.TryDeserialize(line.Substring(7), out var reply))
                    { RaiseError("room_protocol_mismatch"); return; }
                    TaskCompletionSource<RoomConfigurationResult> pending;
                    lock (syncRoot)
                    {
                        if (reply.Result.Snapshot != null && currentRoom != null && reply.Result.Snapshot.RoomId == currentRoom.RoomId)
                            reply.Result.Snapshot.LocalPlayerId = currentRoom.LocalPlayerId;
                        if (reply.Result.Snapshot != null && currentRoom != null && reply.Result.Snapshot.RoomId == currentRoom.RoomId &&
                            reply.Result.Snapshot.StateRevision >= currentRoom.StateRevision)
                        {
                            reply.Result.Snapshot.LocalPlayerId = currentRoom.LocalPlayerId;
                            currentRoom = reply.Result.Snapshot.Clone();
                        }
                        if (reply.Result.Snapshot != null && currentRoom != null && reply.Result.Snapshot.RoomId == currentRoom.RoomId)
                            reply.Result.Snapshot = currentRoom.Clone();
                        if (!pendingCommands.TryGetValue(reply.CommandId, out pending)) return;
                        pendingCommands.Remove(reply.CommandId);
                    }
                    pending.TrySetResult(reply.Result);
                    RaiseRoomUpdated();
                }
                catch (JsonException) { RaiseError("room_protocol_mismatch"); }
                return;
            }
            if (line.StartsWith("WELCOME|", StringComparison.Ordinal))
            {
                var welcomeParts = line.Substring("WELCOME|".Length).Split('|');
                if (welcomeParts.Length == 2 && int.TryParse(welcomeParts[0], out var playerId) &&
                    !string.IsNullOrEmpty(welcomeParts[1]))
                {
                    lock (syncRoot)
                    {
                        if (currentRoom != null)
                        {
                            currentRoom.LocalPlayerId = playerId;
                            localIdentityTicket = welcomeParts[1];
                        }
                    }
                }

                RaiseRoomUpdated();
                return;
            }

            if (line.StartsWith("ROOM|", StringComparison.Ordinal))
            {
                ApplyRemoteRoom(line.Substring("ROOM|".Length), false);
                return;
            }

            if (line.StartsWith("START|", StringComparison.Ordinal))
            {
                ApplyRemoteRoom(line.Substring("START|".Length), true);
                return;
            }

            if (line.StartsWith("ERROR|", StringComparison.Ordinal))
            {
                RaiseError(line.Substring("ERROR|".Length));
                return;
            }

            if (line.StartsWith("DISBAND|", StringComparison.Ordinal))
            {
                suppressDisconnectNotice = true;
                RaiseRoomDisbanded();
            }
        }

        private void ApplyRemoteRoom(string payload, bool started)
        {
            if (!RoomStateSerializer.TryDeserialize(payload, out var incomingRoom, out var reason))
            { RaiseError(reason); return; }
            var localPlayerId = -1;
            lock (syncRoot)
            {
                if (currentRoom != null)
                {
                    localPlayerId = currentRoom.LocalPlayerId;
                }

                if (currentRoom != null && ((currentRoom.PlayerCount > 0 && incomingRoom.RoomId != currentRoom.RoomId) || incomingRoom.StateRevision < currentRoom.StateRevision)) return;
                currentRoom = incomingRoom;
                currentRoom.LocalPlayerId = localPlayerId;
                if (started && !currentRoom.HasStarted) { RaiseError("room_protocol_mismatch"); return; }
            }

            if (started)
            {
                RaiseGameStarted();
            }
            else
            {
                RaiseRoomUpdated();
            }
        }

        internal void Broadcast(string message)
        {
            ClientConnection[] recipients;
            lock (syncRoot)
            {
                var activeRecipients = new List<ClientConnection>();
                for (var i = 0; i < hostClients.Count; i++)
                {
                    var connection = hostClients[i];
                    ClientConnection owner;
                    if (connection.PlayerId > 0 &&
                        seatOwners.TryGetValue(connection.PlayerId, out owner) &&
                        ReferenceEquals(owner, connection))
                    {
                        activeRecipients.Add(connection);
                    }
                }

                recipients = activeRecipients.ToArray();
            }

            List<ClientConnection> failedConnections = null;
            for (var i = 0; i < recipients.Length; i++)
            {
                try
                {
                    recipients[i].WriteLine(message);
                }
                catch (IOException)
                {
                    if (failedConnections == null) failedConnections = new List<ClientConnection>();
                    failedConnections.Add(recipients[i]);
                }
                catch (ObjectDisposedException)
                {
                    if (failedConnections == null) failedConnections = new List<ClientConnection>();
                    failedConnections.Add(recipients[i]);
                }
            }

            if (failedConnections == null) return;
            for (var i = 0; i < failedConnections.Count; i++)
            {
                RemoveClientConnection(failedConnections[i]);
            }
        }

        private void RaiseRoomUpdated()
        {
            var handler = RoomUpdated;
            var room = GetCurrentRoom();
            if (handler != null && room != null) handler(room);
        }

        private void RaiseGameStarted()
        {
            var handler = GameStarted;
            var room = GetCurrentRoom();
            if (handler != null && room != null) handler(room);
        }

        private void RaiseRoomDisbanded()
        {
            var handler = RoomDisbanded;
            if (handler != null)
            {
                handler();
            }
        }

        private void RaiseError(string message)
        {
            var handler = ErrorOccurred;
            if (handler != null)
            {
                handler(message);
            }
        }

        private void PublishRoomUpdate(long generation)
        {
            RoomState room;
            lock (syncRoot)
            {
                if (generation != roomGeneration || suppressDisconnectNotice || currentRoom == null) return;
                room = currentRoom.Clone();
            }

            Broadcast("ROOM|" + SerializeRoom(room));
            if (IsCurrentGeneration(generation)) RaiseRoomUpdated();
        }

        private bool IsCurrentGeneration(long generation)
        {
            lock (syncRoot)
            {
                return generation == roomGeneration && !suppressDisconnectNotice && currentRoom != null;
            }
        }

        private bool IsSeatOwner(ClientConnection connection)
        {
            lock (syncRoot)
            {
                ClientConnection owner;
                return connection.PlayerId > 0 &&
                       seatOwners.TryGetValue(connection.PlayerId, out owner) &&
                       ReferenceEquals(owner, connection);
            }
        }

        private static void JoinThread(Thread thread)
        {
            if (thread == null || thread == Thread.CurrentThread || !thread.IsAlive) return;
            try { thread.Join(1000); }
            catch (ThreadStateException) { }
            catch (Exception) { }
        }

        private void ThrowIfDisposed()
        {
            if (isDisposed) throw new ObjectDisposedException(nameof(NetworkRoomService));
        }

        private static RoomState CreateDefaultRoom(string roomId, int playerCount)
        {
            var room = new RoomState
            {
                RoomId = roomId, HostPlayerId = 1, LocalPlayerId = 1,
                Configuration = new GameRuleConfiguration { PlayerCount = 1 }
            };
            room.SeatSlots[0].Status = "occupied";
            room.SeatSlots[0].Occupant = new PlayerSeat
            {
                PlayerId = 1, SteamId = LocalMirrorIdentity.ForPlayer(1),
                PlayerName = "玩家 1", Color = RoomConfigurationService.GetSeatColor(0), LobbyMemberPresent = true
            };
            return room;
        }

        private static IPEndPoint ParseRoomEndpoint(string roomId)
        {
            if (string.IsNullOrEmpty(roomId))
            {
                throw new ArgumentException("Room code cannot be empty.", nameof(roomId));
            }

            var parts = roomId.Trim().Split(':');
            if (parts.Length != 2)
            {
                throw new FormatException("Room code must use host:port format.");
            }

            IPAddress address;
            if (!IPAddress.TryParse(parts[0], out address))
            {
                address = Dns.GetHostAddresses(parts[0])[0];
            }

            int port;
            if (!int.TryParse(parts[1], out port))
            {
                throw new FormatException("Room code port is invalid.");
            }

            return new IPEndPoint(address, port);
        }

        private static void ConnectWithTimeout(TcpClient tcpClient, IPEndPoint endpoint)
        {
            var asyncResult = tcpClient.BeginConnect(endpoint.Address, endpoint.Port, null, null);
            if (!asyncResult.AsyncWaitHandle.WaitOne(ConnectTimeoutMilliseconds))
            {
                tcpClient.Close();
                throw new TimeoutException("连接房间超时，请确认房间号、端口和防火墙设置。");
            }

            tcpClient.EndConnect(asyncResult);
        }

        private static string GetLocalAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            for (var i = 0; i < host.AddressList.Length; i++)
            {
                var address = host.AddressList[i];
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                {
                    return address.ToString();
                }
            }

            return IPAddress.Loopback.ToString();
        }

        internal static string SerializeRoom(RoomState room) => RoomStateSerializer.Serialize(room);
        internal static RoomState DeserializeRoom(string payload)
        {
            if (!RoomStateSerializer.TryDeserialize(payload, out var room, out var reason))
                throw new InvalidDataException(reason);
            return room;
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty)
                .Replace("%", "%25")
                .Replace("|", "%7C")
                .Replace(";", "%3B")
                .Replace(",", "%2C");
        }

        private static string Unescape(string value)
        {
            return (value ?? string.Empty)
                .Replace("%2C", ",")
                .Replace("%3B", ";")
                .Replace("%7C", "|")
                .Replace("%25", "%");
        }

        internal sealed class ClientConnection : IDisposable
        {
            private readonly object writerRoot = new object();
            private readonly TcpClient client;
            private readonly TextReader reader;
            private readonly TextWriter writer;
            private readonly Action disposeAction;
            private int disposeState;

            internal int PlayerId = -1;
            internal string IdentityTicket = string.Empty;
            internal Thread ReadThread;
            internal bool IsDisposed => Volatile.Read(ref disposeState) != 0;

            internal ClientConnection(TcpClient client)
            {
                this.client = client;
                var stream = client.GetStream();
                reader = new StreamReader(stream);
                writer = new StreamWriter(stream) { AutoFlush = true };
            }

            internal ClientConnection(TextReader reader, TextWriter writer, Action disposeAction = null)
            {
                this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
                this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
                this.disposeAction = disposeAction;
            }

            internal string ReadLine()
            {
                if (IsDisposed) throw new ObjectDisposedException(nameof(ClientConnection));
                return reader.ReadLine();
            }

            internal void WriteLine(string message)
            {
                lock (writerRoot)
                {
                    if (IsDisposed) throw new ObjectDisposedException(nameof(ClientConnection));
                    writer.WriteLine(message);
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposeState, 1) != 0) return;

                if (client != null)
                {
                    try { client.Close(); }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                    catch (Exception) { }
                }

                try { reader.Dispose(); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (Exception) { }

                try
                {
                    lock (writerRoot) writer.Dispose();
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (Exception) { }

                if (disposeAction != null)
                {
                    try { disposeAction(); }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                    catch (Exception) { }
                }
            }
        }
    }
}
