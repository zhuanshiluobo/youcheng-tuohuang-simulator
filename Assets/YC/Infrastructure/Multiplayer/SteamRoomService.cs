using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Steamworks;
using YC.Application.Sessions;
using YC.Domain.Rules;
using UnityEngine;
namespace YC.Infrastructure.Multiplayer
{
    public sealed class SteamRoomService : IOnlineRoomService
    {
        public bool SupportsFriendInvites => true;
        private static readonly PlayerColor[] SeatColors = { PlayerColor.Blue, PlayerColor.Red, PlayerColor.Green, PlayerColor.Yellow };
        private Callback<LobbyCreated_t> lobbyCreated;
        private Callback<LobbyEnter_t> lobbyEntered;
        private Callback<LobbyDataUpdate_t> lobbyDataUpdated;
        private Callback<LobbyChatUpdate_t> lobbyChatUpdated;
        private Callback<GameLobbyJoinRequested_t> lobbyJoinRequested;
        private readonly LobbyJoinRequestInbox lobbyJoinRequests = new LobbyJoinRequestInbox();
        private readonly WaitingRoomConnectionAuthority waitingRoomAuthority = new WaitingRoomConnectionAuthority();
        private TaskCompletionSource<RoomState> pendingCreate;
        private TaskCompletionSource<RoomState> pendingJoin;
        private CSteamID pendingJoinLobbyId;
        private CSteamID lobbyId;
        private ulong originalHostSteamId;
        private int requestedPlayerCount;
        private bool isHost;
        private bool disposed;
        private bool callbacksInitialized;
        private bool gameStartedRaised;
        private bool acceptNetworkEvents;
        private MirrorNetworkRuntime subscribedRuntime;
        private RoomState currentRoom;
        public bool HasPendingLobbyJoinRequest => lobbyJoinRequests.HasPending;
        public event Action<RoomState> RoomUpdated;
        public event Action<RoomState> GameStarted;
        public event Action RoomDisbanded;
        public event Action<string> ErrorOccurred;
        public event Action<string> LobbyJoinRequested
        {
            add => lobbyJoinRequests.LobbyJoinRequested += value;
            remove => lobbyJoinRequests.LobbyJoinRequested -= value;
        }
        public SteamRoomService() { }
        public void Initialize()
        {
            ThrowIfDisposed();
            EnsureSteam();
            EnsureCallbacks();
        }
        public Task<RoomState> CreateRoomAsync(string hostPlayerName, int playerCount)
        {
            ThrowIfDisposed();
            Initialize();
            if (playerCount != 2 && playerCount != 3 && playerCount != 4)
                throw new ArgumentOutOfRangeException(
                    nameof(playerCount),
                    "Steam 房间仅支持 3 人、4 人，或明确标记的 2 人联机验证房。");
            ShutdownNetworkAndLobby();
            requestedPlayerCount = playerCount;
            isHost = true;
            pendingCreate = new TaskCompletionSource<RoomState>(TaskCreationOptions.RunContinuationsAsynchronously);
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, playerCount);
            return pendingCreate.Task;
        }
        public Task<RoomState> JoinRoomAsync(string roomCode, string playerName)
        {
            ThrowIfDisposed();
            Initialize();
            if (!SteamLobbyPolicy.TryParseLobbyId(roomCode, out var parsed)) throw new ArgumentException("请输入有效的 64 位 Steam Lobby ID。", nameof(roomCode));
            ShutdownNetworkAndLobby();
            isHost = false;
            pendingJoinLobbyId = new CSteamID(parsed);
            pendingJoin = new TaskCompletionSource<RoomState>(TaskCreationOptions.RunContinuationsAsynchronously);
            SteamMatchmaking.JoinLobby(pendingJoinLobbyId);
            return pendingJoin.Task;
        }
        public Task StartGameAsync()
        {
            ThrowIfDisposed();
            if (!isHost || !lobbyId.IsValid()) throw new InvalidOperationException("只有房主可以开始游戏。");
            if (SteamUser.GetSteamID().m_SteamID != originalHostSteamId ||
                SteamMatchmaking.GetLobbyOwner(lobbyId).m_SteamID != originalHostSteamId)
                throw new InvalidOperationException("只有原始房主可以开始游戏。");
            if (!string.Equals(GetData("roomStatus"), SteamLobbyPolicy.WaitingStatus, StringComparison.Ordinal))
                throw new InvalidOperationException("房间不再处于等待状态。");
            RefreshRoom();
            var room = GetCurrentRoom();
            var reason = string.Empty;
            if (subscribedRuntime == null ||
                !waitingRoomAuthority.TryValidateStart(room, subscribedRuntime.IsServerClientActive, out reason))
                throw new InvalidOperationException(string.IsNullOrEmpty(reason) ? "等待所有玩家完成网络连接和身份校验。" : reason);
            if (!LobbyStartCommitPolicy.TryCommit(
                    joinable => SteamMatchmaking.SetLobbyJoinable(lobbyId, joinable),
                    status => SteamMatchmaking.SetLobbyData(lobbyId, "roomStatus", status),
                    out reason))
                throw new InvalidOperationException(reason);
            RefreshRoom();
            return Task.CompletedTask;
        }
        public void StartGame() => _ = StartGameSafelyAsync();
        public void InviteFriends()
        {
            ThrowIfDisposed();
            if (!SteamBootstrap.IsInitialized || !lobbyId.IsValid())
                throw new InvalidOperationException("请先进入 Steam Lobby，再邀请好友。");
            SteamFriends.ActivateGameOverlayInviteDialog(lobbyId);
        }
        public RoomState GetCurrentRoom() => currentRoom == null ? null : currentRoom.Clone();
        public bool TryConsumePendingLobbyJoinRequest(out string invitedLobbyId) =>
            lobbyJoinRequests.TryConsume(out invitedLobbyId);
        public void Shutdown()
        {
            if (disposed) return;
            ShutdownNetworkAndLobby();
        }
        public void Dispose()
        {
            if (disposed) return;
            ShutdownNetworkAndLobby();
            if (callbacksInitialized)
            {
                lobbyCreated.Dispose();
                lobbyEntered.Dispose();
                lobbyDataUpdated.Dispose();
                lobbyChatUpdated.Dispose();
                lobbyJoinRequested.Dispose();
                callbacksInitialized = false;
            }
            lobbyJoinRequests.Clear();
            disposed = true;
        }
        private void OnLobbyCreated(LobbyCreated_t value)
        {
            if (pendingCreate == null)
            {
                if (value.m_eResult == EResult.k_EResultOK)
                    SteamMatchmaking.LeaveLobby(new CSteamID(value.m_ulSteamIDLobby));
                return;
            }
            if (value.m_eResult != EResult.k_EResultOK)
            {
                FailPending(new InvalidOperationException("创建 Steam Lobby 失败：" + value.m_eResult));
                return;
            }
            try
            {
                lobbyId = new CSteamID(value.m_ulSteamIDLobby);
                originalHostSteamId = SteamUser.GetSteamID().m_SteamID;
                SetData("gameKey", SteamLobbyPolicy.GameKey);
                SetData("protocolVersion", SteamLobbyPolicy.ProtocolVersion);
                SetData("roomStatus", SteamLobbyPolicy.WaitingStatus);
                SetData("playerCount", requestedPlayerCount.ToString());
                SetData("hostSteamId", originalHostSteamId.ToString());
                SetData(
                    SteamLobbyPolicy.SessionKindKey,
                    requestedPlayerCount == 2
                        ? SteamLobbyPolicy.TwoPlayerValidationSessionKind
                        : string.Empty);
                for (var playerId = 1; playerId <= requestedPlayerCount; playerId++)
                {
                    SetData(SteamLobbyPolicy.SeatKey(playerId), playerId == 1 ? originalHostSteamId.ToString() : string.Empty);
                    SetData(SteamLobbyPolicy.TransportConnectedKey(playerId), "0");
                    SetData(SteamLobbyPolicy.IdentityVerifiedKey(playerId), "0");
                }
                SteamMatchmaking.SetLobbyJoinable(lobbyId, true);
                waitingRoomAuthority.Begin(BuildPresenceRoom().Seats);
                acceptNetworkEvents = true;
                SubscribeToRuntime(MirrorNetworkRuntime.Ensure());
                subscribedRuntime.StartHost();
            }
            catch (Exception ex)
            {
                FailPending(new InvalidOperationException("初始化 Steam 房主网络失败：" + ex.Message, ex));
            }
        }
        private void OnLobbyEntered(LobbyEnter_t value)
        {
            var enteredLobbyId = new CSteamID(value.m_ulSteamIDLobby);
            if (pendingCreate == null && pendingJoin == null)
            {
                if (enteredLobbyId.IsValid()) SteamMatchmaking.LeaveLobby(enteredLobbyId);
                if (lobbyId == enteredLobbyId) lobbyId = CSteamID.Nil;
                return;
            }
            if (pendingJoin != null && enteredLobbyId != pendingJoinLobbyId)
            {
                if (enteredLobbyId.IsValid()) SteamMatchmaking.LeaveLobby(enteredLobbyId);
                return;
            }
            if (pendingCreate != null && lobbyId.IsValid() && enteredLobbyId != lobbyId)
            {
                if (enteredLobbyId.IsValid()) SteamMatchmaking.LeaveLobby(enteredLobbyId);
                return;
            }
            if ((EChatRoomEnterResponse)value.m_EChatRoomEnterResponse != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                FailPending(new InvalidOperationException("加入 Steam Lobby 失败：" + (EChatRoomEnterResponse)value.m_EChatRoomEnterResponse));
                return;
            }
            try
            {
                lobbyId = enteredLobbyId;
                var data = ReadCompatibilityData();
                if (!SteamLobbyPolicy.IsCompatible(data, requestedPlayerCount: 0, out var reason))
                {
                    FailPending(new InvalidOperationException(reason));
                    return;
                }
                if (!ulong.TryParse(GetData("hostSteamId"), out originalHostSteamId) || originalHostSteamId == 0)
                {
                    FailPending(new InvalidOperationException("房间缺少有效的房主 SteamID。"));
                    return;
                }
                requestedPlayerCount = int.Parse(data["playerCount"]);
                if (isHost)
                {
                    AssignStableSeats();
                }
                else
                {
                    acceptNetworkEvents = true;
                    SubscribeToRuntime(MirrorNetworkRuntime.Ensure());
                    subscribedRuntime.StartClient(originalHostSteamId);
                }
                RefreshRoom();
                var snapshot = GetCurrentRoom();
                pendingCreate?.TrySetResult(snapshot); pendingCreate = null;
                pendingJoin?.TrySetResult(snapshot); pendingJoin = null;
                pendingJoinLobbyId = CSteamID.Nil;
            }
            catch (Exception ex)
            {
                FailPending(new InvalidOperationException("初始化 Steam 房间失败：" + ex.Message, ex));
            }
        }
        private void OnLobbyJoinRequested(GameLobbyJoinRequested_t value)
        {
            var invitedLobbyId = value.m_steamIDLobby.m_SteamID.ToString();
            if (!SteamLobbyPolicy.TryParseLobbyId(invitedLobbyId, out _))
            {
                ErrorOccurred?.Invoke("收到的 Steam Lobby 邀请无效，已安全忽略。");
                return;
            }
            lobbyJoinRequests.Record(invitedLobbyId);
        }
        private void RefreshRoom()
        {
            if (!lobbyId.IsValid()) return;
            var owner = SteamMatchmaking.GetLobbyOwner(lobbyId).m_SteamID;
            if (originalHostSteamId != 0 && owner != originalHostSteamId)
            {
                ShutdownNetworkAndLobby();
                RoomDisbanded?.Invoke();
                return;
            }
            if (isHost)
            {
                AssignStableSeats();
                SynchronizeAuthorityRoster();
            }
            var room = BuildRoomState();
            if (isHost) PublishAuthorityReadiness(room);
            currentRoom = room;
            if (room.HasStarted && !gameStartedRaised)
            {
                gameStartedRaised = true;
                GameStarted?.Invoke(room.Clone());
            }
            else RoomUpdated?.Invoke(room.Clone());
        }
        private void AssignStableSeats()
        {
            var members = new List<ulong>();
            var count = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
            for (var i = 0; i < count; i++) members.Add(SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i).m_SteamID);
            var existing = new Dictionary<int, ulong>();
            for (var seat = 1; seat <= requestedPlayerCount; seat++)
            {
                ulong.TryParse(GetData(SteamLobbyPolicy.SeatKey(seat)), out var id);
                existing[seat] = id;
            }
            var assigned = SteamSeatAssignment.Assign(originalHostSteamId, requestedPlayerCount, members, existing);
            for (var seat = 1; seat <= requestedPlayerCount; seat++)
            {
                var value = assigned[seat] == 0 ? string.Empty : assigned[seat].ToString();
                if (!string.Equals(GetData(SteamLobbyPolicy.SeatKey(seat)), value, StringComparison.Ordinal))
                    SetData(SteamLobbyPolicy.SeatKey(seat), value);
            }
        }
        private RoomState BuildRoomState()
        {
            var room = BuildPresenceRoom();
            if (isHost)
            {
                waitingRoomAuthority.ApplyTo(room);
            }
            else
            {
                for (var i = 0; i < room.Seats.Count; i++)
                {
                    var seat = room.Seats[i];
                    seat.TransportConnected = seat.LobbyMemberPresent &&
                                                GetData(SteamLobbyPolicy.TransportConnectedKey(seat.PlayerId)) == "1";
                    seat.IdentityVerified = seat.TransportConnected &&
                                            GetData(SteamLobbyPolicy.IdentityVerifiedKey(seat.PlayerId)) == "1";
                    seat.IsReady = seat.LobbyMemberPresent && seat.TransportConnected && seat.IdentityVerified;
                }
            }
            return room;
        }
        private RoomState BuildPresenceRoom()
        {
            var localSteamId = SteamUser.GetSteamID().m_SteamID;
            var members = new HashSet<ulong>();
            var memberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
            for (var i = 0; i < memberCount; i++)
                members.Add(SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i).m_SteamID);
            var room = new RoomState
            {
                RoomId = lobbyId.m_SteamID.ToString(),
                HostPlayerId = 1,
                LocalPlayerId = -1,
                PlayerCount = requestedPlayerCount,
                HasStarted = GetData("roomStatus") == SteamLobbyPolicy.StartedStatus
            };
            for (var playerId = 1; playerId <= requestedPlayerCount; playerId++)
            {
                ulong.TryParse(GetData(SteamLobbyPolicy.SeatKey(playerId)), out var steamId);
                var present = steamId != 0 && members.Contains(steamId);
                var name = !present ? "等待玩家" : SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
                room.Seats.Add(new PlayerSeat
                {
                    PlayerId = playerId,
                    SteamId = steamId,
                    NetworkClientId = 0,
                    PlayerName = string.IsNullOrEmpty(name) ? "Player " + playerId : name,
                    Color = SeatColors[playerId - 1],
                    LobbyMemberPresent = present,
                    TransportConnected = false,
                    IdentityVerified = false,
                    GameStateSynchronized = false,
                    IsReady = false
                });
                if (steamId == localSteamId) room.LocalPlayerId = playerId;
            }
            return room;
        }
        private void SynchronizeAuthorityRoster()
        {
            var presenceRoom = BuildPresenceRoom();
            if (!waitingRoomAuthority.IsActive)
            {
                waitingRoomAuthority.Begin(presenceRoom.Seats);
                return;
            }
            var removedConnections = waitingRoomAuthority.UpdateLobbySeats(presenceRoom.Seats);
            for (var i = 0; i < removedConnections.Count; i++)
                subscribedRuntime?.RequestServerDisconnect(removedConnections[i]);
        }
        private void PublishAuthorityReadiness(RoomState room)
        {
            if (room == null || !isHost) return;
            for (var i = 0; i < room.Seats.Count; i++)
            {
                var seat = room.Seats[i];
                SetDataIfChanged(
                    SteamLobbyPolicy.TransportConnectedKey(seat.PlayerId),
                    seat.TransportConnected ? "1" : "0");
                SetDataIfChanged(
                    SteamLobbyPolicy.IdentityVerifiedKey(seat.PlayerId),
                    seat.IdentityVerified ? "1" : "0");
            }
        }
        private Dictionary<string, string> ReadCompatibilityData() => new Dictionary<string, string>
        {
            ["gameKey"] = GetData("gameKey"), ["protocolVersion"] = GetData("protocolVersion"),
            ["roomStatus"] = GetData("roomStatus"), ["playerCount"] = GetData("playerCount"),
            ["hostSteamId"] = GetData("hostSteamId"),
            [SteamLobbyPolicy.SessionKindKey] = GetData(SteamLobbyPolicy.SessionKindKey)
        };
        private string GetData(string key) => SteamMatchmaking.GetLobbyData(lobbyId, key) ?? string.Empty;
        private void SetData(string key, string value) => SteamMatchmaking.SetLobbyData(lobbyId, key, value ?? string.Empty);
        private void SetDataIfChanged(string key, string value)
        {
            if (!string.Equals(GetData(key), value, StringComparison.Ordinal)) SetData(key, value);
        }
        private static void EnsureSteam() { if (!UnityEngine.Application.isPlaying || !SteamBootstrap.Ensure().Initialize()) throw new InvalidOperationException("Steam 未运行或初始化失败。"); }
        private void EnsureCallbacks()
        {
            if (callbacksInitialized) return;
            if (!SteamBootstrap.IsInitialized) throw new InvalidOperationException("Steam 尚未初始化，无法注册回调。");
            lobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            lobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            lobbyDataUpdated = Callback<LobbyDataUpdate_t>.Create(_ => RefreshRoom());
            lobbyChatUpdated = Callback<LobbyChatUpdate_t>.Create(_ => RefreshRoom());
            lobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
            callbacksInitialized = true;
        }
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(SteamRoomService)); }
        private async Task StartGameSafelyAsync() { try { await StartGameAsync(); } catch (Exception ex) { ErrorOccurred?.Invoke(ex.Message); } }
        private void FailPending(Exception ex)
        {
            var createCompletion = pendingCreate;
            var joinCompletion = pendingJoin;
            pendingCreate = null;
            pendingJoin = null;
            pendingJoinLobbyId = CSteamID.Nil;
            ShutdownNetworkAndLobby();
            createCompletion?.TrySetException(ex);
            joinCompletion?.TrySetException(ex);
            ErrorOccurred?.Invoke(ex.Message);
        }
        private void SubscribeToRuntime(MirrorNetworkRuntime runtime)
        {
            if (subscribedRuntime == runtime) return;
            if (subscribedRuntime != null)
                UnsubscribeFromRuntime(subscribedRuntime);
            subscribedRuntime = runtime;
            if (subscribedRuntime == null) return;
            subscribedRuntime.ClientDisconnected -= OnNetworkDisconnected;
            subscribedRuntime.ClientDisconnected += OnNetworkDisconnected;
            subscribedRuntime.ServerClientConnected -= OnServerClientConnected;
            subscribedRuntime.ServerClientConnected += OnServerClientConnected;
            subscribedRuntime.ServerClientDisconnected -= OnServerClientDisconnected;
            subscribedRuntime.ServerClientDisconnected += OnServerClientDisconnected;
        }
        private void UnsubscribeFromRuntime(MirrorNetworkRuntime runtime)
        {
            runtime.ClientDisconnected -= OnNetworkDisconnected;
            runtime.ServerClientConnected -= OnServerClientConnected;
            runtime.ServerClientDisconnected -= OnServerClientDisconnected;
        }
        private void ShutdownNetworkAndLobby()
        {
            CancelPending();
            acceptNetworkEvents = false;
            waitingRoomAuthority.Shutdown();
            if (subscribedRuntime != null)
            {
                UnsubscribeFromRuntime(subscribedRuntime);
                subscribedRuntime = null;
            }
            MirrorNetworkRuntime.Instance?.ShutdownNetwork();
            if (lobbyId.IsValid()) SteamMatchmaking.LeaveLobby(lobbyId);
            lobbyId = CSteamID.Nil; currentRoom = null; originalHostSteamId = 0; requestedPlayerCount = 0;
            isHost = false; gameStartedRaised = false;
        }
        private void CancelPending()
        {
            pendingCreate?.TrySetCanceled();
            pendingJoin?.TrySetCanceled();
            pendingCreate = null;
            pendingJoin = null;
            pendingJoinLobbyId = CSteamID.Nil;
        }
        private void OnNetworkDisconnected()
        {
            if (disposed || !acceptNetworkEvents || isHost) return;
            ShutdownNetworkAndLobby();
            RoomDisbanded?.Invoke();
        }
        private void OnServerClientConnected(MirrorServerConnectionInfo connection)
        {
            if (disposed || !acceptNetworkEvents || !isHost || !lobbyId.IsValid())
            {
                subscribedRuntime?.RequestServerDisconnect(connection.ConnectionId);
                return;
            }
            AssignStableSeats();
            SynchronizeAuthorityRoster();
            waitingRoomAuthority.ObserveTransportConnection(connection.ConnectionId);
            ulong steamId;
            var claimedPlayerId = -1;
            if (connection.IsLocal)
            {
                if (connection.ConnectionId != 0)
                {
                    RejectServerConnection(connection.ConnectionId, "拒绝异常的房主本地连接。");
                    return;
                }
                steamId = SteamUser.GetSteamID().m_SteamID;
                claimedPlayerId = 1;
            }
            else if (!SteamIdentityAddress.TryParse(connection.Address, out steamId))
            {
                RejectServerConnection(connection.ConnectionId, "拒绝无法解析 SteamID 的连接：" + connection.Address);
                return;
            }
            if (!waitingRoomAuthority.TryVerifyIdentity(
                    connection.ConnectionId,
                    steamId,
                    claimedPlayerId,
                    out _))
            {
                RejectServerConnection(connection.ConnectionId, "拒绝非 Lobby 成员、重复或席位不匹配的 Steam 连接。");
                return;
            }
            RefreshRoom();
        }
        private void OnServerClientDisconnected(int connectionId)
        {
            if (disposed || !acceptNetworkEvents || !isHost) return;
            if (waitingRoomAuthority.Disconnect(connectionId, out _)) RefreshRoom();
        }
        private void RejectServerConnection(int connectionId, string reason)
        {
            waitingRoomAuthority.Disconnect(connectionId, out _);
            Debug.LogWarning(reason);
            subscribedRuntime?.RequestServerDisconnect(connectionId);
            RefreshRoom();
        }
    }
}