using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YC.Application.Sessions;
using YC.Domain.Rules;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace YC.Infrastructure.Multiplayer
{
    public sealed class UnityOfficialRoomService : IDisposable
    {
        private const string RelayJoinCodeKey = "relayJoinCode";
        private const string RoomStatusKey = "roomStatus";
        private const string PlayerCountKey = "playerCount";
        private const string PlayerNameKey = "playerName";
        private const string ReadyKey = "ready";
        private const string WaitingStatus = "waiting";
        private const string StartedStatus = "started";
        private const float LobbyPollSeconds = 2f;
        private const float LobbyHeartbeatSeconds = 15f;

        private static readonly PlayerColor[] SeatColors =
        {
            PlayerColor.Blue,
            PlayerColor.Red,
            PlayerColor.Green,
            PlayerColor.Yellow
        };

        private readonly object syncRoot = new object();

        private CancellationTokenSource cancellation;
        private Lobby currentLobby;
        private RoomState currentRoom;
        private bool isDisposed;
        private bool isHost;
        private bool gameStartRaised;

        public event Action<RoomState> RoomUpdated;
        public event Action<RoomState> GameStarted;
        public event Action RoomDisbanded;
        public event Action<string> ErrorOccurred;

        public async Task<RoomState> CreateRoomAsync(string hostPlayerName, int playerCount)
        {
            Shutdown();
            ResetSessionState();
            EnsurePlayModeNetworking();
            isHost = true;
            playerCount = Mathf.Clamp(playerCount, 3, 4);

            try
            {
                await EnsureUnityServicesAsync();
                var manager = EnsureNetworkManager();
                var relayJoinCode = await ConfigureHostRelayAsync(playerCount);

                var options = new CreateLobbyOptions
                {
                    IsPrivate = false,
                    Player = CreateLobbyPlayer(hostPlayerName),
                    Data = new Dictionary<string, DataObject>
                    {
                        { RelayJoinCodeKey, new DataObject(DataObject.VisibilityOptions.Member, relayJoinCode) },
                        { RoomStatusKey, new DataObject(DataObject.VisibilityOptions.Member, WaitingStatus) },
                        { PlayerCountKey, new DataObject(DataObject.VisibilityOptions.Public, playerCount.ToString()) }
                    }
                };

                currentLobby = await LobbyService.Instance.CreateLobbyAsync("游城拓荒房间", playerCount, options);

                if (!manager.StartHost())
                {
                    throw new InvalidOperationException("NetworkManager.StartHost() failed.");
                }

                ApplyLobby(currentLobby);
                StartLobbyLoops();
                RaiseRoomUpdated();
                return GetCurrentRoom();
            }
            catch
            {
                Shutdown();
                throw;
            }
        }

        public async Task<RoomState> JoinRoomAsync(string roomCode, string playerName)
        {
            if (string.IsNullOrWhiteSpace(roomCode))
            {
                throw new ArgumentException("房间码不能为空。", nameof(roomCode));
            }

            Shutdown();
            ResetSessionState();
            EnsurePlayModeNetworking();
            isHost = false;

            try
            {
                await EnsureUnityServicesAsync();
                var manager = EnsureNetworkManager();

                var options = new JoinLobbyByCodeOptions
                {
                    Player = CreateLobbyPlayer(playerName)
                };

                currentLobby = await LobbyService.Instance.JoinLobbyByCodeAsync(roomCode.Trim(), options);
                var relayJoinCode = GetLobbyData(currentLobby, RelayJoinCodeKey);
                if (string.IsNullOrEmpty(relayJoinCode))
                {
                    throw new InvalidOperationException("Lobby 未提供 Relay join code。");
                }

                await ConfigureClientRelayAsync(relayJoinCode);

                if (!manager.StartClient())
                {
                    throw new InvalidOperationException("NetworkManager.StartClient() failed.");
                }

                ApplyLobby(currentLobby);
                StartLobbyLoops();
                RaiseRoomUpdated();
                return GetCurrentRoom();
            }
            catch
            {
                Shutdown();
                throw;
            }
        }

        public async Task StartGameAsync()
        {
            if (!isHost)
            {
                RaiseError("只有房主可以开始游戏。");
                return;
            }

            var lobby = currentLobby;
            if (lobby == null)
            {
                RaiseError("还没有创建房间。");
                return;
            }

            var room = GetCurrentRoom();
            if (room == null)
            {
                RaiseError("房间状态不可用。");
                return;
            }

            if (CountJoinedSeats(room) < room.PlayerCount)
            {
                RaiseError("等待所有席位加入后才能开始。");
                return;
            }

            var options = new UpdateLobbyOptions
            {
                IsLocked = true,
                Data = new Dictionary<string, DataObject>
                {
                    { RoomStatusKey, new DataObject(DataObject.VisibilityOptions.Member, StartedStatus) }
                }
            };

            currentLobby = await LobbyService.Instance.UpdateLobbyAsync(lobby.Id, options);
            ApplyLobby(currentLobby);
            RaiseGameStartedOnce();
        }

        public void StartGame()
        {
            _ = StartGameSafelyAsync();
        }

        public RoomState GetCurrentRoom()
        {
            lock (syncRoot)
            {
                return currentRoom == null ? null : currentRoom.Clone();
            }
        }

        public void Shutdown()
        {
            if (cancellation != null)
            {
                cancellation.Cancel();
                cancellation.Dispose();
                cancellation = null;
            }

            var lobby = currentLobby;
            currentLobby = null;
            currentRoom = null;
            gameStartRaised = false;

            if (lobby != null && UnityServices.State == ServicesInitializationState.Initialized)
            {
                if (isHost)
                {
                    _ = DeleteLobbySafelyAsync(lobby.Id);
                }
                else if (AuthenticationService.Instance.IsSignedIn)
                {
                    _ = LeaveLobbySafelyAsync(lobby.Id, AuthenticationService.Instance.PlayerId);
                }
            }

            var manager = NetworkManager.Singleton;
            if (manager != null && manager.IsListening)
            {
                manager.Shutdown();
            }

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

        private void ResetSessionState()
        {
            cancellation = new CancellationTokenSource();
            currentLobby = null;
            currentRoom = null;
            gameStartRaised = false;
        }

        private async Task StartGameSafelyAsync()
        {
            try
            {
                await StartGameAsync();
            }
            catch (Exception ex)
            {
                RaiseError(ex.Message);
            }
        }

        private static async Task EnsureUnityServicesAsync()
        {
            try
            {
                if (UnityServices.State == ServicesInitializationState.Uninitialized)
                {
                    await UnityServices.InitializeAsync();
                }

                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    AuthenticationService.Instance.SwitchProfile(CreateLocalProfileName());
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                }
            }
            catch (RequestFailedException ex) when (IsSslOrNetworkFailure(ex))
            {
                throw new InvalidOperationException(
                    "无法连接 Unity Authentication。请检查 Unity Services Project ID、系统代理/防火墙、系统时间和 HTTPS 证书；当前网络可能无法完成 SSL 握手。原始错误：" + ex.Message,
                    ex);
            }
        }

        private static bool IsSslOrNetworkFailure(Exception ex)
        {
            var message = ex.Message ?? string.Empty;
            return message.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0
                   || message.IndexOf("Network Error", StringComparison.OrdinalIgnoreCase) >= 0
                   || message.IndexOf("Unable to complete", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string CreateLocalProfileName()
        {
            if (UnityEngine.Application.isEditor)
            {
                return "editor";
            }

            try
            {
                return "local-" + System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch
            {
                return "local-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            }
        }

        private static NetworkManager EnsureNetworkManager()
        {
            var manager = NetworkManager.Singleton;
            if (manager != null)
            {
                EnsureUnityTransport(manager);
                return manager;
            }

            var managerObject = new GameObject("NetworkManager", typeof(UnityTransport), typeof(NetworkManager));
            if (UnityEngine.Application.isPlaying)
            {
                UnityEngine.Object.DontDestroyOnLoad(managerObject);
            }

            manager = managerObject.GetComponent<NetworkManager>();
            EnsureUnityTransport(manager);
            return manager;
        }

        private static void EnsurePlayModeNetworking()
        {
            if (!UnityEngine.Application.isPlaying)
            {
                throw new InvalidOperationException("Unity 官方联机只能在 Play Mode 或构建后的运行程序中启动。请先点击 Unity 顶部 Play 按钮，再创建或加入房间。");
            }
        }

        private static UnityTransport EnsureUnityTransport(NetworkManager manager)
        {
            if (manager.NetworkConfig == null)
            {
                manager.NetworkConfig = new NetworkConfig();
            }

            var transport = manager.GetComponent<UnityTransport>();
            if (transport == null)
            {
                transport = manager.gameObject.AddComponent<UnityTransport>();
            }

            manager.NetworkConfig.NetworkTransport = transport;
            return transport;
        }

        private static async Task<string> ConfigureHostRelayAsync(int playerCount)
        {
            var allocation = await RelayService.Instance.CreateAllocationAsync(Math.Max(1, playerCount - 1));
            var joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            SetRelayServerData(allocation);
            return joinCode;
        }

        private static async Task ConfigureClientRelayAsync(string relayJoinCode)
        {
            var allocation = await RelayService.Instance.JoinAllocationAsync(relayJoinCode);
            SetRelayServerData(allocation);
        }

        private static void SetRelayServerData(Allocation allocation)
        {
            var transport = EnsureUnityTransport(EnsureNetworkManager());
            transport.SetRelayServerData(new RelayServerData(allocation, "dtls"));
        }

        private static void SetRelayServerData(JoinAllocation allocation)
        {
            var transport = EnsureUnityTransport(EnsureNetworkManager());
            transport.SetRelayServerData(new RelayServerData(allocation, "dtls"));
        }

        private static Player CreateLobbyPlayer(string playerName)
        {
            return new Player(
                AuthenticationService.Instance.PlayerId,
                data: new Dictionary<string, PlayerDataObject>
                {
                    { PlayerNameKey, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, string.IsNullOrEmpty(playerName) ? "Player" : playerName) },
                    { ReadyKey, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, "1") }
                });
        }

        private void StartLobbyLoops()
        {
            var token = cancellation.Token;
            if (isHost)
            {
                _ = HeartbeatLoopAsync(token);
            }

            _ = PollLobbyLoopAsync(token);
        }

        private async Task HeartbeatLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && currentLobby != null)
            {
                try
                {
                    await LobbyService.Instance.SendHeartbeatPingAsync(currentLobby.Id);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogException(ex);
                    RaiseError(ex.Message);
                }

                await DelaySeconds(LobbyHeartbeatSeconds, token);
            }
        }

        private async Task PollLobbyLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && currentLobby != null)
            {
                await DelaySeconds(LobbyPollSeconds, token);
                if (token.IsCancellationRequested || currentLobby == null)
                {
                    return;
                }

                try
                {
                    currentLobby = await LobbyService.Instance.GetLobbyAsync(currentLobby.Id);
                    ApplyLobby(currentLobby);

                    if (currentRoom != null && currentRoom.HasStarted)
                    {
                        RaiseGameStartedOnce();
                    }
                    else
                    {
                        RaiseRoomUpdated();
                    }
                }
                catch (LobbyServiceException)
                {
                    RaiseRoomDisbanded();
                    return;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogException(ex);
                    RaiseError(ex.Message);
                }
            }
        }

        private static async Task DelaySeconds(float seconds, CancellationToken token)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), token);
            }
            catch (TaskCanceledException)
            {
            }
        }

        private void ApplyLobby(Lobby lobby)
        {
            if (lobby == null)
            {
                return;
            }

            lock (syncRoot)
            {
                currentRoom = BuildRoomState(lobby);
                currentRoom.LocalPlayerId = GameLaunchStateFactory.ResolveHostLocalPlayerId(
                    currentRoom.LocalPlayerId,
                    currentRoom.HostPlayerId,
                    isHost,
                    currentRoom.Seats);
            }
        }

        private static RoomState BuildRoomState(Lobby lobby)
        {
            var playerCount = ParseInt(GetLobbyData(lobby, PlayerCountKey), lobby.MaxPlayers);
            var room = new RoomState
            {
                RoomId = string.IsNullOrEmpty(lobby.LobbyCode) ? lobby.Id : lobby.LobbyCode,
                HostPlayerId = 1,
                LocalPlayerId = -1,
                PlayerCount = playerCount,
                HasStarted = string.Equals(GetLobbyData(lobby, RoomStatusKey), StartedStatus, StringComparison.Ordinal)
            };

            for (var i = 0; i < playerCount; i++)
            {
                var playerId = i + 1;
                room.Seats.Add(new PlayerSeat
                {
                    PlayerId = playerId,
                    NetcodeClientId = 0,
                    PlayerName = "Player " + playerId,
                    Color = SeatColors[Math.Min(i, SeatColors.Length - 1)],
                    IsReady = false
                });
            }

            var orderedPlayers = OrderLobbyPlayers(lobby);
            for (var i = 0; i < orderedPlayers.Count && i < room.Seats.Count; i++)
            {
                var lobbyPlayer = orderedPlayers[i];
                var seat = room.Seats[i];
                var playerName = GetPlayerData(lobbyPlayer, PlayerNameKey, string.Empty);
                seat.PlayerName = string.IsNullOrEmpty(playerName) || playerName.StartsWith("Player ", StringComparison.Ordinal)
                    ? "Player " + seat.PlayerId
                    : playerName;
                seat.IsReady = GetPlayerData(lobbyPlayer, ReadyKey, "1") == "1";

                if (AuthenticationService.Instance.IsSignedIn && lobbyPlayer.Id == AuthenticationService.Instance.PlayerId)
                {
                    room.LocalPlayerId = seat.PlayerId;
                    var manager = NetworkManager.Singleton;
                    if (manager != null)
                    {
                        seat.NetcodeClientId = manager.LocalClientId;
                    }
                }
            }

            return room;
        }

        private static List<Player> OrderLobbyPlayers(Lobby lobby)
        {
            var players = new List<Player>();
            if (lobby.Players == null)
            {
                return players;
            }

            for (var i = 0; i < lobby.Players.Count; i++)
            {
                if (lobby.Players[i].Id == lobby.HostId)
                {
                    players.Add(lobby.Players[i]);
                    break;
                }
            }

            var nonHostPlayers = new List<Player>();
            for (var i = 0; i < lobby.Players.Count; i++)
            {
                if (lobby.Players[i].Id != lobby.HostId)
                {
                    nonHostPlayers.Add(lobby.Players[i]);
                }
            }

            nonHostPlayers.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
            players.AddRange(nonHostPlayers);

            return players;
        }

        private static string GetLobbyData(Lobby lobby, string key)
        {
            if (lobby == null || lobby.Data == null || !lobby.Data.TryGetValue(key, out var data) || data == null)
            {
                return string.Empty;
            }

            return data.Value;
        }

        private static string GetPlayerData(Player player, string key, string fallback)
        {
            if (player == null || player.Data == null || !player.Data.TryGetValue(key, out var data) || data == null)
            {
                return fallback;
            }

            return data.Value;
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, out parsed) ? parsed : fallback;
        }

        private static int CountJoinedSeats(RoomState room)
        {
            var count = 0;
            for (var i = 0; i < room.Seats.Count; i++)
            {
                if (room.Seats[i].IsReady)
                {
                    count++;
                }
            }

            return count;
        }

        private void RaiseRoomUpdated()
        {
            var handler = RoomUpdated;
            if (handler != null)
            {
                handler(GetCurrentRoom());
            }
        }

        private void RaiseGameStartedOnce()
        {
            if (gameStartRaised)
            {
                return;
            }

            gameStartRaised = true;
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

        private static async Task DeleteLobbySafelyAsync(string lobbyId)
        {
            try
            {
                await LobbyService.Instance.DeleteLobbyAsync(lobbyId);
            }
            catch
            {
            }
        }

        private static async Task LeaveLobbySafelyAsync(string lobbyId, string playerId)
        {
            try
            {
                await LobbyService.Instance.RemovePlayerAsync(lobbyId, playerId);
            }
            catch
            {
            }
        }
    }
}
