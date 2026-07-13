using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using YC.Application.Sessions;
using YC.Domain.Rules;

namespace YC.Infrastructure.Multiplayer
{
    public sealed class NetworkRoomService : IDisposable
    {
        private const int DefaultPort = 7780;
        private const int MaxPort = 7799;
        private const int ConnectTimeoutMilliseconds = 5000;

        private static readonly PlayerColor[] SeatColors =
        {
            PlayerColor.Blue,
            PlayerColor.Red,
            PlayerColor.Green,
            PlayerColor.Yellow
        };

        private readonly object syncRoot = new object();
        private readonly List<ClientConnection> hostClients = new List<ClientConnection>();
        private readonly Dictionary<int, ClientConnection> seatOwners = new Dictionary<int, ClientConnection>();
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

        public event Action<RoomState> RoomUpdated;
        public event Action<RoomState> GameStarted;
        public event Action RoomDisbanded;
        public event Action<string> ErrorOccurred;

        public NetworkRoomService()
            : this(true)
        {
        }

        internal NetworkRoomService(bool enableNetworking)
        {
            this.enableNetworking = enableNetworking;
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

        public RoomState CreateRoom(string hostPlayerName, int playerCount)
        {
            ThrowIfDisposed();
            Shutdown();

            playerCount = Math.Max(3, Math.Min(4, playerCount));
            var port = enableNetworking ? StartListener() : DefaultPort;
            var roomId = (enableNetworking ? GetLocalAddress() : IPAddress.Loopback.ToString()) + ":" + port;
            TcpListener activeListener;
            lock (syncRoot)
            {
                suppressDisconnectNotice = false;
                isHost = true;
                roomGeneration++;
                currentRoom = CreateDefaultRoom(roomId, playerCount);
                currentRoom.LocalPlayerId = currentRoom.HostPlayerId;
                currentRoom.Seats[0].PlayerName = string.IsNullOrEmpty(hostPlayerName) ? "Player 1" : hostPlayerName;
                currentRoom.Seats[0].LobbyMemberPresent = true;
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
            activeWriter.WriteLine("JOIN|" + Escape(playerName));

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
                    LocalPlayerId = -1,
                    PlayerCount = 0
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
                if (!isHost || currentRoom == null || currentRoom.HasStarted) return GetCurrentRoom();
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
                    generation = roomGeneration;
                    break;
                }
            }

            if (generation != 0) PublishRoomUpdate(generation);
            return GetCurrentRoom();
        }

        public void StartGame() => TryStartGame(out _);

        public bool TryStartGame(out string error)
        {
            error = null;
            RoomState roomToStart = null;
            long generation = 0;
            lock (syncRoot)
            {
                if (!isHost)
                {
                    error = "Only the room host can start the game.";
                }
                else if (currentRoom == null)
                {
                    error = "No room has been created.";
                }
                else if (!RoomReadinessPolicy.TryValidateStart(currentRoom, out error))
                {
                }
                else
                {
                    currentRoom.HasStarted = true;
                    roomToStart = currentRoom.Clone();
                    generation = roomGeneration;
                }
            }

            if (error != null)
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
                hostClientThreads.RemoveAll(thread => !thread.IsAlive);
                currentRoom = null;
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

        private void HostClientLoop(ClientConnection connection)
        {
            try
            {
                var line = connection.ReadLine();
                if (line == null || !line.StartsWith("JOIN|", StringComparison.Ordinal))
                {
                    return;
                }

                var playerName = Unescape(line.Substring("JOIN|".Length));
                var assignedPlayerId = AssignSeat(playerName, connection);
                if (assignedPlayerId < 0)
                {
                    connection.WriteLine("ERROR|Room is full.");
                    return;
                }

                connection.WriteLine("WELCOME|" + assignedPlayerId);
                connection.WriteLine("ROOM|" + SerializeRoom(GetCurrentRoom()));
                Broadcast("ROOM|" + SerializeRoom(GetCurrentRoom()));
                if (IsSeatOwner(connection)) RaiseRoomUpdated();

                while (true)
                {
                    if (connection.ReadLine() == null)
                    {
                        return;
                    }
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
                    if (currentRoom != null && !currentRoom.HasStarted)
                    {
                        for (var i = 0; i < currentRoom.Seats.Count; i++)
                        {
                            var seat = currentRoom.Seats[i];
                            if (seat.PlayerId != connection.PlayerId) continue;
                            var defaultName = "Player " + seat.PlayerId;
                            roomChanged = seat.LobbyMemberPresent || seat.IsReady ||
                                          !string.Equals(seat.PlayerName, defaultName, StringComparison.Ordinal);
                            seat.PlayerName = defaultName;
                            seat.LobbyMemberPresent = false;
                            seat.TransportConnected = false;
                            seat.IdentityVerified = false;
                            seat.GameStateSynchronized = false;
                            seat.NetworkClientId = 0;
                            seat.IsReady = false;
                            break;
                        }
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

                for (var i = 0; i < currentRoom.Seats.Count; i++)
                {
                    var seat = currentRoom.Seats[i];
                    if (seat.LobbyMemberPresent || seatOwners.ContainsKey(seat.PlayerId))
                    {
                        continue;
                    }

                    seat.PlayerName = string.IsNullOrEmpty(playerName) ? "Player " + seat.PlayerId : playerName;
                    seat.LobbyMemberPresent = true;
                    seat.TransportConnected = false;
                    seat.IdentityVerified = false;
                    seat.GameStateSynchronized = false;
                    seat.IsReady = false;
                    connection.PlayerId = seat.PlayerId;
                    seatOwners[seat.PlayerId] = connection;
                    return seat.PlayerId;
                }
            }

            return -1;
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
            if (line.StartsWith("WELCOME|", StringComparison.Ordinal))
            {
                int playerId;
                if (int.TryParse(line.Substring("WELCOME|".Length), out playerId))
                {
                    lock (syncRoot)
                    {
                        if (currentRoom != null)
                        {
                            currentRoom.LocalPlayerId = playerId;
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
            var localPlayerId = -1;
            lock (syncRoot)
            {
                if (currentRoom != null)
                {
                    localPlayerId = currentRoom.LocalPlayerId;
                }

                currentRoom = DeserializeRoom(payload);
                currentRoom.LocalPlayerId = localPlayerId;
                if (started)
                {
                    currentRoom.HasStarted = true;
                }
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
                RoomId = roomId,
                HostPlayerId = 1,
                LocalPlayerId = 1,
                PlayerCount = playerCount
            };

            for (var i = 0; i < playerCount; i++)
            {
                var playerId = i + 1;
                room.Seats.Add(new PlayerSeat
                {
                    PlayerId = playerId,
                    SteamId = LocalMirrorIdentity.ForPlayer(playerId),
                    NetworkClientId = 0,
                    PlayerName = "Player " + playerId,
                    Color = SeatColors[i],
                    LobbyMemberPresent = false,
                    TransportConnected = false,
                    IdentityVerified = false,
                    GameStateSynchronized = false,
                    IsReady = false
                });
            }

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

        internal static string SerializeRoom(RoomState room)
        {
            if (room == null)
            {
                return string.Empty;
            }

            var seats = new List<string>();
            for (var i = 0; i < room.Seats.Count; i++)
            {
                var seat = room.Seats[i];
                seats.Add(seat.PlayerId + "," +
                          Escape(seat.PlayerName) + "," +
                          (int)seat.Color + "," +
                          (seat.LobbyMemberPresent ? "1" : "0") + "," +
                          (seat.TransportConnected ? "1" : "0") + "," +
                          (seat.IdentityVerified ? "1" : "0") + "," +
                          seat.NetworkClientId + "," +
                          (seat.IsReady ? "1" : "0") + "," +
                          (seat.GameStateSynchronized ? "1" : "0"));
            }

            return Escape(room.RoomId) + "|" +
                   room.HostPlayerId + "|" +
                   room.PlayerCount + "|" +
                   (room.HasStarted ? "1" : "0") + "|" +
                   string.Join(";", seats.ToArray());
        }

        internal static RoomState DeserializeRoom(string payload)
        {
            var parts = payload.Split('|');
            var room = new RoomState
            {
                RoomId = parts.Length > 0 ? Unescape(parts[0]) : string.Empty,
                HostPlayerId = parts.Length > 1 ? ParseInt(parts[1], -1) : -1,
                PlayerCount = parts.Length > 2 ? ParseInt(parts[2], 0) : 0,
                HasStarted = parts.Length > 3 && parts[3] == "1"
            };

            if (parts.Length > 4 && !string.IsNullOrEmpty(parts[4]))
            {
                var seatPayloads = parts[4].Split(';');
                for (var i = 0; i < seatPayloads.Length; i++)
                {
                    var seatParts = seatPayloads[i].Split(',');
                    if (seatParts.Length < 4)
                    {
                        continue;
                    }

                    var playerId = ParseInt(seatParts[0], -1);
                    if (playerId < 1 || playerId > 4)
                    {
                        continue;
                    }

                    var legacyReady = seatParts[3] == "1";
                    var lobbyMemberPresent = legacyReady;
                    var transportConnected = seatParts.Length >= 9 && seatParts[4] == "1";
                    var identityVerified = seatParts.Length >= 9 && seatParts[5] == "1";
                    var networkClientId = seatParts.Length >= 9 ? ParseUlong(seatParts[6], 0UL) : 0UL;
                    var isReady = seatParts.Length >= 9 ? seatParts[7] == "1" : false;
                    var synchronized = seatParts.Length >= 9 && seatParts[8] == "1";
                    room.Seats.Add(new PlayerSeat
                    {
                        PlayerId = playerId,
                        SteamId = LocalMirrorIdentity.ForPlayer(playerId),
                        NetworkClientId = networkClientId,
                        PlayerName = Unescape(seatParts[1]),
                        Color = (PlayerColor)ParseInt(seatParts[2], 0),
                        LobbyMemberPresent = lobbyMemberPresent,
                        TransportConnected = transportConnected,
                        IdentityVerified = identityVerified,
                        GameStateSynchronized = synchronized,
                        IsReady = isReady
                    });
                }
            }

            return room;
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, out parsed) ? parsed : fallback;
        }

        private static ulong ParseUlong(string value, ulong fallback)
        {
            return ulong.TryParse(value, out var parsed) ? parsed : fallback;
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
