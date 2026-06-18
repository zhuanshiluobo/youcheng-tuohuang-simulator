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
        private const int DefaultPort = 7777;
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

        private TcpListener listener;
        private TcpClient client;
        private StreamWriter clientWriter;
        private Thread acceptThread;
        private Thread clientReadThread;
        private bool isDisposed;
        private bool isHost;
        private bool suppressDisconnectNotice;
        private RoomState currentRoom;

        public event Action<RoomState> RoomUpdated;
        public event Action<RoomState> GameStarted;
        public event Action RoomDisbanded;
        public event Action<string> ErrorOccurred;

        public RoomState CreateRoom(string hostPlayerName, int playerCount)
        {
            Shutdown();
            suppressDisconnectNotice = false;
            isHost = true;

            playerCount = Math.Max(3, Math.Min(4, playerCount));
            var port = StartListener();
            var roomId = GetLocalAddress() + ":" + port;
            currentRoom = CreateDefaultRoom(roomId, playerCount);
            currentRoom.LocalPlayerId = currentRoom.HostPlayerId;
            currentRoom.Seats[0].PlayerName = string.IsNullOrEmpty(hostPlayerName) ? "Player 1" : hostPlayerName;
            currentRoom.Seats[0].IsReady = true;

            acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            acceptThread.Start();

            RaiseRoomUpdated();
            return currentRoom.Clone();
        }

        public RoomState JoinRoom(string roomId, string playerName)
        {
            Shutdown();
            suppressDisconnectNotice = false;
            isHost = false;

            var endpoint = ParseRoomEndpoint(roomId);
            client = new TcpClient();
            ConnectWithTimeout(client, endpoint);

            var stream = client.GetStream();
            clientWriter = new StreamWriter(stream) { AutoFlush = true };
            clientWriter.WriteLine("JOIN|" + Escape(playerName));

            currentRoom = new RoomState
            {
                RoomId = roomId,
                LocalPlayerId = -1,
                PlayerCount = 0
            };

            clientReadThread = new Thread(() => ClientReadLoop(client)) { IsBackground = true };
            clientReadThread.Start();

            RaiseRoomUpdated();
            return currentRoom.Clone();
        }

        public RoomState GetCurrentRoom()
        {
            lock (syncRoot)
            {
                return currentRoom == null ? null : currentRoom.Clone();
            }
        }

        public void StartGame()
        {
            if (!isHost)
            {
                RaiseError("Only the room host can start the game.");
                return;
            }

            lock (syncRoot)
            {
                if (currentRoom == null)
                {
                    RaiseError("No room has been created.");
                    return;
                }

                if (CountJoinedSeats(currentRoom) < currentRoom.PlayerCount)
                {
                    RaiseError("等待所有席位加入后才能开始。");
                    return;
                }

                currentRoom.HasStarted = true;
            }

            Broadcast("START|" + SerializeRoom(GetCurrentRoom()));
            RaiseGameStarted();
        }

        public void Shutdown()
        {
            suppressDisconnectNotice = true;
            var shouldNotifyClients = isHost && currentRoom != null && !currentRoom.HasStarted;
            if (shouldNotifyClients)
            {
                Broadcast("DISBAND|房间已解散。");
            }

            lock (syncRoot)
            {
                for (var i = 0; i < hostClients.Count; i++)
                {
                    hostClients[i].Dispose();
                }

                hostClients.Clear();
            }

            if (clientWriter != null)
            {
                clientWriter.Dispose();
                clientWriter = null;
            }

            if (client != null)
            {
                client.Close();
                client = null;
            }

            if (listener != null)
            {
                listener.Stop();
                listener = null;
            }

            acceptThread = null;
            clientReadThread = null;
            currentRoom = null;
            isHost = false;
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

        private void AcceptLoop()
        {
            while (listener != null)
            {
                try
                {
                    var acceptedClient = listener.AcceptTcpClient();
                    var connection = new ClientConnection(acceptedClient);
                    var thread = new Thread(() => HostClientLoop(connection)) { IsBackground = true };
                    thread.Start();
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
                var line = connection.Reader.ReadLine();
                if (line == null || !line.StartsWith("JOIN|", StringComparison.Ordinal))
                {
                    connection.Dispose();
                    return;
                }

                var playerName = Unescape(line.Substring("JOIN|".Length));
                var assignedPlayerId = AssignSeat(playerName, connection);
                if (assignedPlayerId < 0)
                {
                    connection.Writer.WriteLine("ERROR|Room is full.");
                    connection.Dispose();
                    return;
                }

                connection.Writer.WriteLine("WELCOME|" + assignedPlayerId);
                connection.Writer.WriteLine("ROOM|" + SerializeRoom(GetCurrentRoom()));
                Broadcast("ROOM|" + SerializeRoom(GetCurrentRoom()));
                RaiseRoomUpdated();

                while (connection.Client.Connected)
                {
                    if (connection.Reader.ReadLine() == null)
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
        }

        private int AssignSeat(string playerName, ClientConnection connection)
        {
            lock (syncRoot)
            {
                if (currentRoom == null)
                {
                    return -1;
                }

                for (var i = 0; i < currentRoom.Seats.Count; i++)
                {
                    var seat = currentRoom.Seats[i];
                    if (seat.IsReady)
                    {
                        continue;
                    }

                    seat.PlayerName = string.IsNullOrEmpty(playerName) ? "Player " + seat.PlayerId : playerName;
                    seat.IsReady = true;
                    connection.PlayerId = seat.PlayerId;
                    hostClients.Add(connection);
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

                if (currentRoom != null && currentRoom.LocalPlayerId != currentRoom.HostPlayerId)
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

        private void Broadcast(string message)
        {
            lock (syncRoot)
            {
                for (var i = hostClients.Count - 1; i >= 0; i--)
                {
                    var clientConnection = hostClients[i];
                    try
                    {
                        clientConnection.Writer.WriteLine(message);
                    }
                    catch (IOException)
                    {
                        hostClients.RemoveAt(i);
                        clientConnection.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        hostClients.RemoveAt(i);
                    }
                }
            }
        }

        private void RaiseRoomUpdated()
        {
            var handler = RoomUpdated;
            if (handler != null)
            {
                handler(GetCurrentRoom());
            }
        }

        private void RaiseGameStarted()
        {
            var handler = GameStarted;
            if (handler != null)
            {
                handler(GetCurrentRoom());
            }
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
                    NetcodeClientId = 0,
                    PlayerName = "Player " + playerId,
                    Color = SeatColors[i],
                    IsReady = false
                });
            }

            return room;
        }

        private static int CountJoinedSeats(RoomState room)
        {
            var joinedCount = 0;
            for (var i = 0; i < room.Seats.Count; i++)
            {
                if (room.Seats[i].IsReady)
                {
                    joinedCount++;
                }
            }

            return joinedCount;
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

        private static string SerializeRoom(RoomState room)
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
                          (seat.IsReady ? "1" : "0"));
            }

            return Escape(room.RoomId) + "|" +
                   room.HostPlayerId + "|" +
                   room.PlayerCount + "|" +
                   (room.HasStarted ? "1" : "0") + "|" +
                   string.Join(";", seats.ToArray());
        }

        private static RoomState DeserializeRoom(string payload)
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

                    room.Seats.Add(new PlayerSeat
                    {
                        PlayerId = ParseInt(seatParts[0], -1),
                        NetcodeClientId = 0,
                        PlayerName = Unescape(seatParts[1]),
                        Color = (PlayerColor)ParseInt(seatParts[2], 0),
                        IsReady = seatParts[3] == "1"
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

        private sealed class ClientConnection : IDisposable
        {
            public readonly TcpClient Client;
            public readonly StreamReader Reader;
            public readonly StreamWriter Writer;
            public int PlayerId = -1;

            public ClientConnection(TcpClient client)
            {
                Client = client;
                var stream = client.GetStream();
                Reader = new StreamReader(stream);
                Writer = new StreamWriter(stream) { AutoFlush = true };
            }

            public void Dispose()
            {
                Reader.Dispose();
                Writer.Dispose();
                Client.Close();
            }
        }
    }
}
