using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Steamworks;
using YC.Application.Sessions;
using YC.Domain.Rules;
using UnityEngine;
namespace YC.Infrastructure.Multiplayer
{
    public sealed class SteamRoomService : IResumableOnlineRoomService, IPreconfiguredRoomHost
    {
        public bool SupportsFriendInvites => true;
        private IRoomContentCapabilityProvider content;
        private RoomConfigurationService configurationService;
        private readonly Dictionary<ulong, int> playerIdsBySteamId = new Dictionary<ulong, int>();
        private int nextPlayerId = 2;
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
        private bool authoritySnapshotReceived;
        private MirrorNetworkRuntime subscribedRuntime;
        private RoomState currentRoom;
        private RoomState pendingHostSnapshot;
        private ResumeRoomOptions pendingResume;
        private bool resumeClientStarted;
        private readonly Dictionary<int, MirrorServerConnectionInfo> pendingSteamConnections = new Dictionary<int, MirrorServerConnectionInfo>();
        // 仅Steam SDK边界可在检查中注入；房间、认证和重连方法仍是实际生产对象。
        private Func<bool> steamReadyProbe;
        private Func<ulong> steamAccountProbe;
        private Func<ulong, ulong> steamOwnerProbe;
        private Func<ulong, int> steamMemberCountProbe;
        private Func<ulong, int, ulong> steamMemberProbe;
        private Action<ulong> steamLeaveLobbyProbe;
        private Func<string, string> steamDataProbe;
        private Action<string, string> steamWriteDataProbe;
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
            content ??= OnlineRoomServiceProvider.ContentCapabilities;
            configurationService ??= new RoomConfigurationService(content);
            EnsureSteam();
            EnsureCallbacks();
        }
        public Task<RoomState> CreateRoomAsync(string hostPlayerName, int playerCount)
            => BeginCreateRoom(hostPlayerName, playerCount, null);

        public Task<RoomState> CreateResumeRoomAsync(ResumeRoomOptions options)
        {
            Initialize(); options.Validate();
            var host = options.Seats.Find(s => s.PlayerId == options.HostPlayerId);
            if (host.SteamId != SteamUser.GetSteamID().m_SteamID) throw new InvalidOperationException("只有存档的原房主可以创建续局房间。");
            return BeginCreateRoom(host.PlayerName, options.Seats.Count, null, options);
        }
        public void AssignResumeSeat(string memberId, int playerId)
        {
            if (!isHost || currentRoom?.Resume == null || currentRoom.HasStarted || !ulong.TryParse(memberId, out var steamId) ||
                ReadLobbyMembership(steamId) <= 0 || !currentRoom.WaitingMembers.Exists(m => m.MemberId == memberId))
                throw new InvalidOperationException("等待成员已经离开或房间不可分配。");
            var slot = RoomConfigurationService.FindSlot(currentRoom, playerId);
            if (slot == null || playerId == currentRoom.HostPlayerId || slot.Occupant.LobbyMemberPresent || currentRoom.Seats.Exists(s => s.SteamId == steamId))
                throw new InvalidOperationException("此席位不可替补。");
            var seat = slot.Occupant;
            seat.SteamId = steamId; seat.PlayerName = SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
            seat.LobbyMemberPresent = true; seat.TransportConnected = seat.IdentityVerified = seat.IsReady = seat.GameStateSynchronized = false;
            seat.ContentPackHash = string.Empty; seat.RuleContractVersion = string.Empty;
            slot.IsUserReady = false; slot.ReadyConfigurationRevision = -1;
            playerIdsBySteamId[steamId] = playerId; currentRoom.WaitingMembers.RemoveAll(m => m.MemberId == memberId);
            currentRoom.StateRevision++; RefreshRoom();
        }

        public Task<RoomState> CreateRoomFromSnapshotAsync(RoomState localRoom)
        {
            if (!RoomStateSerializer.TryValidate(localRoom, out var reason)) throw new InvalidOperationException(reason);
            if (localRoom.HasStarted || localRoom.PlayerCount != 1 || localRoom.LocalPlayerId != localRoom.HostPlayerId)
                throw new InvalidOperationException("configuration_stale");
            return BeginCreateRoom(RoomConfigurationService.FindSlot(localRoom, localRoom.HostPlayerId).Occupant.PlayerName,
                localRoom.Configuration.MapId == "map-four-players" ? 4 : 3, localRoom);
        }

        private Task<RoomState> BeginCreateRoom(string hostPlayerName, int playerCount, RoomState initialRoom, ResumeRoomOptions resume = null)
        {
            ThrowIfDisposed();
            Initialize();
            if (playerCount != 2 && playerCount != 3 && playerCount != 4)
                throw new ArgumentOutOfRangeException(
                    nameof(playerCount),
                    "Steam 房间仅支持 3 人、4 人，或明确标记的 2 人联机验证房。");
            ShutdownNetworkAndLobby();
            pendingHostSnapshot = initialRoom?.Clone();
            pendingResume = resume?.Clone(); resumeClientStarted = false;
            requestedPlayerCount = playerCount;
            isHost = true;
            pendingCreate = new TaskCompletionSource<RoomState>(TaskCreationOptions.RunContinuationsAsynchronously);
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, 4);
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
        public async Task StartGameAsync()
        {
            var room = GetCurrentRoom();
            if (room == null) throw new InvalidOperationException("service_unavailable");
            var result = await StartConfiguredGameAsync(new StartConfiguredGameRequest
            {
                CommandId = Guid.NewGuid().ToString("N"), RoomId = room.RoomId,
                RequestingPlayerId = room.LocalPlayerId, ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision
            });
            if (!result.Success) throw new InvalidOperationException(result.ReasonCode);
        }
        public Task<RoomConfigurationResult> ChangeConfigurationAsync(ChangeRoomConfigurationRequest request) => SendConfigurationAsync(request);
        public Task<RoomConfigurationResult> SetReadyAsync(SetRoomReadyRequest request) => SendConfigurationAsync(request);
        public Task<RoomConfigurationResult> SetSeatStatusAsync(SetRoomSeatStatusRequest request) => SendConfigurationAsync(request);
        public Task<RoomConfigurationResult> RequestSeatSwapAsync(RequestRoomSeatSwapRequest request) => SendConfigurationAsync(request);
        public Task<RoomConfigurationResult> ConfirmSeatSwapAsync(ConfirmRoomSeatSwapRequest request) => SendConfigurationAsync(request);
        public Task<RoomConfigurationResult> StartConfiguredGameAsync(StartConfiguredGameRequest request) => SendConfigurationAsync(request);
        private async Task<RoomConfigurationResult> SendConfigurationAsync(RoomConfigurationRequest request)
        {
            ThrowIfDisposed();
            if (currentRoom == null || request == null || subscribedRuntime == null)
                return RoomConfigurationService.Result(currentRoom, "service_unavailable");
            if (request.RequestingPlayerId != currentRoom.LocalPlayerId)
                return RoomConfigurationService.Result(currentRoom, "room_protocol_mismatch");
            var message = RoomConfigurationMessage.Create(request, content, currentRoom.Configuration);
            if (isHost) return ExecuteConfiguration(message, currentRoom.LocalPlayerId);
            var result = await subscribedRuntime.SendWaitingRoomCommandAsync(message);
            if (result.Snapshot != null) { OnWaitingRoomStateReceived(result.Snapshot); result.Snapshot = GetCurrentRoom(); }
            return result;
        }
        private RoomConfigurationResult ExecuteConfiguration(RoomConfigurationMessage message, int playerId)
        {
            if (!isHost || currentRoom == null || SteamMatchmaking.GetLobbyOwner(lobbyId).m_SteamID != originalHostSteamId)
                return RoomConfigurationService.Result(currentRoom, "not_room_host");
            RefreshRoom();
            lock (currentRoom)
            {
                var wasStarted = currentRoom.HasStarted;
                var lobbyCommitted = false;
                var request = message.ReadRequest();
                if (request is StartConfiguredGameRequest && !wasStarted)
                {
                    // 预检不冻结也不抽签；外部提交成功后只在正式房间生成一次顺序。
                    var validation = message.ValidateStart(currentRoom, playerId, configurationService);
                    if (!validation.Success) return validation;
                    foreach (var player in currentRoom.Seats)
                        if (!subscribedRuntime.IsServerClientActive((int)player.NetworkClientId))
                            return RoomConfigurationService.Result(currentRoom, "player_not_ready");
                    if (!LobbyStartCommitPolicy.TryCommit(
                        joinable => SteamMatchmaking.SetLobbyJoinable(lobbyId, joinable),
                        status => SteamMatchmaking.SetLobbyData(lobbyId, "roomStatus", status), out _))
                        return RoomConfigurationService.Result(currentRoom, "service_unavailable");
                    lobbyCommitted = true;
                }
                var result = message.Execute(currentRoom, playerId, configurationService);
                if (!result.Success && lobbyCommitted)
                {
                    SteamMatchmaking.SetLobbyData(lobbyId, "roomStatus", SteamLobbyPolicy.WaitingStatus);
                    SteamMatchmaking.SetLobbyJoinable(lobbyId, true);
                }
                if (result.Success)
                {
                    SetDataIfChanged("roomState", RoomStateSerializer.Serialize(currentRoom));
                    subscribedRuntime.BroadcastWaitingRoomState(currentRoom);
                    PublishRoom(currentRoom);
                }
                return result;
            }
        }
        private void OnWaitingRoomCommandReceived(int connectionId, RoomConfigurationMessage message)
        {
            if (!acceptNetworkEvents || !isHost || !waitingRoomAuthority.TryGetPlayer(connectionId, out var playerId))
            { subscribedRuntime?.RequestServerDisconnect(connectionId); return; }
            try
            {
                var request = message.ReadRequest();
                var result = ExecuteConfiguration(message, playerId);
                subscribedRuntime.SendWaitingRoomCommandReply(connectionId, new RoomConfigurationReply { CommandId = request.CommandId, Result = result });
            }
            catch (Newtonsoft.Json.JsonException) { subscribedRuntime.RequestServerDisconnect(connectionId); }
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
            // 正式退出联机才释放 Steam；创建/加入前的内部清理不能关闭 API。
            ReleaseCallbacks();
            SteamBootstrap.Instance?.Shutdown();
        }
        public void Dispose()
        {
            if (disposed) return;
            Shutdown();
            lobbyJoinRequests.Clear();
            disposed = true;
        }
        private void ReleaseCallbacks()
        {
            if (callbacksInitialized)
            {
                lobbyCreated.Dispose();
                lobbyEntered.Dispose();
                lobbyDataUpdated.Dispose();
                lobbyChatUpdated.Dispose();
                lobbyJoinRequested.Dispose();
                callbacksInitialized = false;
            }
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
                SetData("playerCount", "4");
                SetData("hostSteamId", originalHostSteamId.ToString());
                SetData(
                    SteamLobbyPolicy.SessionKindKey,
                    requestedPlayerCount == 2
                        ? SteamLobbyPolicy.TwoPlayerValidationSessionKind
                        : string.Empty);
                currentRoom = new RoomState
                {
                    RoomId = lobbyId.m_SteamID.ToString(), HostPlayerId = 1, LocalPlayerId = 1,
                    Configuration = new GameRuleConfiguration()
                };
                currentRoom.Configuration.ContentPackHash = content.GetContentPackHash(currentRoom.Configuration);
                configurationService.TryOccupySeat(currentRoom, new PlayerSeat
                {
                    PlayerId = 1, SteamId = originalHostSteamId, LobbyMemberPresent = true,
                    PlayerName = SteamFriends.GetPersonaName(), ContentPackHash = currentRoom.Configuration.ContentPackHash,
                    RuleContractVersion = currentRoom.Configuration.RuleContractVersion
                }, out _);
                if (pendingHostSnapshot != null)
                {
                    currentRoom = RoomHostSnapshot.Create(pendingHostSnapshot, currentRoom.RoomId,
                        currentRoom.Seats[0], content);
                    pendingHostSnapshot = null;
                }
                playerIdsBySteamId[originalHostSteamId] = 1;
                if (pendingResume != null)
                {
                    pendingResume.ApplyTo(currentRoom, currentRoom.Seats[0], content);
                    foreach (var saved in pendingResume.Seats) playerIdsBySteamId[saved.SteamId] = saved.PlayerId;
                    pendingResume = null;
                }
                SetData("roomState", RoomStateSerializer.Serialize(currentRoom));
                SteamMatchmaking.SetLobbyJoinable(lobbyId, true);
                waitingRoomAuthority.Begin(currentRoom.Seats);
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
                if (acceptNetworkEvents && lobbyId == enteredLobbyId && currentRoom != null) return;
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
                    resumeClientStarted = false;
                }
                RefreshRoom();
                var snapshot = GetCurrentRoom();
                if (snapshot != null)
                {
                    pendingCreate?.TrySetResult(snapshot); pendingCreate = null;
                    pendingJoin?.TrySetResult(snapshot); pendingJoin = null;
                    pendingJoinLobbyId = CSteamID.Nil;
                }
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
            var owner = ReadLobbyOwner();
            if (originalHostSteamId != 0 && owner != 0 && owner != originalHostSteamId)
            {
                ShutdownNetworkAndLobby();
                RoomDisbanded?.Invoke();
                return;
            }
            if (originalHostSteamId != 0 && owner == 0) return;
            if (!isHost && currentRoom?.HasStarted == true &&
                (ReadLobbyMembership(originalHostSteamId) < 0 || ReadLobbyMembership(ReadSteamAccount()) < 0))
            { ShutdownNetworkAndLobby(); RoomDisbanded?.Invoke(); return; }
            if (isHost)
            {
                AssignStableSeats();
                SynchronizeAuthorityRoster();
                foreach (var pair in new List<KeyValuePair<int, MirrorServerConnectionInfo>>(pendingSteamConnections))
                {
                    if (subscribedRuntime == null || !subscribedRuntime.IsServerClientActive(pair.Key)) { pendingSteamConnections.Remove(pair.Key); continue; }
                    TryAuthenticateServerConnection(pair.Value);
                }
            }
            else if (authoritySnapshotReceived)
            {
                return;
            }
            var room = BuildRoomState();
            if (room == null) return;
            if (isHost) SetDataIfChanged("roomState", RoomStateSerializer.Serialize(room));
            if (isHost) subscribedRuntime?.BroadcastWaitingRoomState(room);
            PublishRoom(room);
        }
        private void PublishRoom(RoomState room)
        {
            currentRoom = room;
            if (!isHost && !room.HasStarted && room.LocalPlayerId > 0 && subscribedRuntime != null && !resumeClientStarted)
            { resumeClientStarted = true; subscribedRuntime.StartClient(originalHostSteamId); }
            if (!isHost && room.Resume != null && room.LocalPlayerId == 0 && pendingJoin != null)
            { pendingJoin.TrySetResult(room.Clone()); pendingJoin = null; pendingJoinLobbyId = CSteamID.Nil; }
            ConfigureStartedClientReconnect(room);
            if (room.HasStarted && !gameStartedRaised)
            {
                gameStartedRaised = true;
                GameStarted?.Invoke(room.Clone());
            }
            else RoomUpdated?.Invoke(room.Clone());
        }
        private void AssignStableSeats()
        {
            if (!isHost || currentRoom == null || currentRoom.HasStarted) return;
            var members = new HashSet<ulong>();
            var count = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
            for (var i = 0; i < count; i++) members.Add(SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i).m_SteamID);
            foreach (var player in currentRoom.Seats)
                if (player.LobbyMemberPresent && !members.Contains(player.SteamId)) configurationService.VacateSeat(currentRoom, player.PlayerId);
            if (currentRoom.Resume != null)
            {
                if (currentRoom.WaitingMembers.RemoveAll(m => !ulong.TryParse(m.MemberId, out var id) || !members.Contains(id)) > 0) currentRoom.StateRevision++;
                foreach (var steamId in members)
                {
                    var saved = currentRoom.Seats.Find(s => s.SteamId == steamId);
                    if (saved != null)
                    {
                        if (!saved.LobbyMemberPresent) { saved.LobbyMemberPresent = true; saved.PlayerName = SteamFriends.GetFriendPersonaName(new CSteamID(steamId)); currentRoom.StateRevision++; }
                    }
                    else if (!currentRoom.WaitingMembers.Exists(m => m.MemberId == steamId.ToString()))
                    { currentRoom.WaitingMembers.Add(new RoomMember { MemberId = steamId.ToString(), PlayerName = SteamFriends.GetFriendPersonaName(new CSteamID(steamId)) }); currentRoom.StateRevision++; }
                }
                return;
            }
            var sorted = new List<ulong>(members); sorted.Sort();
            foreach (var steamId in sorted)
            {
                if (currentRoom.Seats.Exists(p => p.SteamId == steamId)) continue;
                if (!playerIdsBySteamId.TryGetValue(steamId, out var playerId)) playerId = nextPlayerId++;
                if (configurationService.TryOccupySeat(currentRoom, new PlayerSeat
                {
                    PlayerId = playerId, SteamId = steamId, LobbyMemberPresent = true,
                    PlayerName = SteamFriends.GetFriendPersonaName(new CSteamID(steamId))
                }, out _)) playerIdsBySteamId[steamId] = playerId;
            }
        }
        private RoomState BuildRoomState()
        {
            var room = BuildPresenceRoom();
            if (room == null) return null;
            if (isHost)
            {
                var before = Newtonsoft.Json.JsonConvert.SerializeObject(room.Seats);
                waitingRoomAuthority.ApplyTo(room);
                if (before != Newtonsoft.Json.JsonConvert.SerializeObject(room.Seats)) room.StateRevision++;
                foreach (var slot in room.SeatSlots)
                    if (slot.Occupant != null && !slot.Occupant.IsReady)
                    { slot.IsUserReady = false; slot.ReadyConfigurationRevision = -1; }
            }
            return room;
        }
        private RoomState BuildPresenceRoom()
        {
            if (isHost) return currentRoom;
            if (!RoomStateSerializer.TryDeserialize(GetData("roomState"), out var room, out _)) return null;
            return RoomState.TryLocalizeAuthoritativeSnapshot(room, lobbyId.m_SteamID.ToString(), SteamUser.GetSteamID().m_SteamID, out var localized)
                ? localized : null;
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
        private Dictionary<string, string> ReadCompatibilityData() => new Dictionary<string, string>
        {
            ["gameKey"] = GetData("gameKey"), ["protocolVersion"] = GetData("protocolVersion"),
            ["roomStatus"] = GetData("roomStatus"), ["playerCount"] = GetData("playerCount"),
            ["hostSteamId"] = GetData("hostSteamId"),
            [SteamLobbyPolicy.SessionKindKey] = GetData(SteamLobbyPolicy.SessionKindKey)
        };
        private string GetData(string key) => (steamDataProbe != null ? steamDataProbe(key) : SteamMatchmaking.GetLobbyData(lobbyId, key)) ?? string.Empty;
        private void SetData(string key, string value)
        {
            if (steamWriteDataProbe != null) steamWriteDataProbe(key, value ?? string.Empty);
            else SteamMatchmaking.SetLobbyData(lobbyId, key, value ?? string.Empty);
        }
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
            lobbyChatUpdated = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdated);
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
            subscribedRuntime.LocalClientDisconnected -= OnClientTransportDisconnected;
            subscribedRuntime.LocalClientDisconnected += OnClientTransportDisconnected;
            subscribedRuntime.ServerIdentityRetryRequested -= OnPendingSteamIdentityRetry;
            subscribedRuntime.ServerIdentityRetryRequested += OnPendingSteamIdentityRetry;
            subscribedRuntime.ServerClientConnected -= OnServerClientConnected;
            subscribedRuntime.ServerClientConnected += OnServerClientConnected;
            subscribedRuntime.ServerClientDisconnected -= OnServerClientDisconnected;
            subscribedRuntime.ServerClientDisconnected += OnServerClientDisconnected;
            subscribedRuntime.WaitingRoomCommandReceived -= OnWaitingRoomCommandReceived;
            subscribedRuntime.WaitingRoomCommandReceived += OnWaitingRoomCommandReceived;
            subscribedRuntime.WaitingRoomStateReceived -= OnWaitingRoomStateReceived;
            subscribedRuntime.WaitingRoomStateReceived += OnWaitingRoomStateReceived;
        }
        private void UnsubscribeFromRuntime(MirrorNetworkRuntime runtime)
        {
            runtime.ClientDisconnected -= OnNetworkDisconnected;
            runtime.LocalClientDisconnected -= OnClientTransportDisconnected;
            runtime.ServerIdentityRetryRequested -= OnPendingSteamIdentityRetry;
            runtime.ServerClientConnected -= OnServerClientConnected;
            runtime.ServerClientDisconnected -= OnServerClientDisconnected;
            runtime.WaitingRoomCommandReceived -= OnWaitingRoomCommandReceived;
            runtime.WaitingRoomStateReceived -= OnWaitingRoomStateReceived;
        }
        private void ShutdownNetworkAndLobby()
        {
            CancelPending();
            acceptNetworkEvents = false;
            pendingResume = null; resumeClientStarted = false;
            pendingSteamConnections.Clear();
            waitingRoomAuthority.Shutdown();
            if (subscribedRuntime != null)
            {
                subscribedRuntime.ClearSteamClientReconnect();
                UnsubscribeFromRuntime(subscribedRuntime);
                subscribedRuntime = null;
            }
            MirrorNetworkRuntime.Instance?.ShutdownNetwork();
            if ((steamReadyProbe?.Invoke() ?? SteamBootstrap.IsInitialized) && lobbyId.IsValid())
            {
                if (steamLeaveLobbyProbe != null) steamLeaveLobbyProbe(lobbyId.m_SteamID);
                else SteamMatchmaking.LeaveLobby(lobbyId);
            }
            playerIdsBySteamId.Clear(); nextPlayerId = 2;
            lobbyId = CSteamID.Nil; currentRoom = null; pendingHostSnapshot = null; originalHostSteamId = 0; requestedPlayerCount = 0;
            isHost = false; gameStartedRaised = false; authoritySnapshotReceived = false;
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

        private ulong ReadSteamAccount() => steamAccountProbe?.Invoke() ?? SteamUser.GetSteamID().m_SteamID;
        private ulong ReadLobbyOwner() => steamOwnerProbe?.Invoke(lobbyId.m_SteamID) ?? SteamMatchmaking.GetLobbyOwner(lobbyId).m_SteamID;
        private int ReadLobbyMembership(ulong steamId)
        {
            int count = steamMemberCountProbe?.Invoke(lobbyId.m_SteamID) ?? SteamMatchmaking.GetNumLobbyMembers(lobbyId);
            if (count <= 0) return 0;
            for (int i = 0; i < count; i++)
                if ((steamMemberProbe?.Invoke(lobbyId.m_SteamID, i) ?? SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i).m_SteamID) == steamId) return 1;
            return -1;
        }

        private bool ConfigureStartedClientReconnect(RoomState room)
        {
            if (MirrorCommandTransport.Instance != null && MirrorCommandTransport.Instance.HasCompletedSettlement)
            { subscribedRuntime?.ClearSteamClientReconnect(); return false; }
            if (disposed || !acceptNetworkEvents || isHost || !authoritySnapshotReceived || !room.HasStarted || subscribedRuntime == null ||
                !lobbyId.IsValid() || room.RoomId != lobbyId.m_SteamID.ToString() || string.IsNullOrWhiteSpace(room.GameId)) return false;
            try
            {
                ulong localId = ReadSteamAccount();
                var local = room.Seats.Find(seat => seat.PlayerId == room.LocalPlayerId && seat.SteamId == localId);
                var host = room.Seats.Find(seat => seat.PlayerId == room.HostPlayerId && seat.SteamId == originalHostSteamId);
                if (local == null || host == null || localId == originalHostSteamId) return false;
                subscribedRuntime.ConfigureSteamClientReconnect(lobbyId.m_SteamID, originalHostSteamId, localId, room.GameId);
                return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            { Debug.LogWarning("[ClientReconnectLifecycle] 无法核验原Steam连接身份：" + error.Message); return false; }
        }

        private void OnClientTransportDisconnected()
        {
            if (disposed || !acceptNetworkEvents || isHost) return;
            if (authoritySnapshotReceived && currentRoom?.HasStarted == true && subscribedRuntime != null)
            {
                if (ConfigureStartedClientReconnect(currentRoom) && subscribedRuntime.TryBeginClientReconnect()) return;
            }
            OnNetworkDisconnected();
        }

        private void OnLobbyChatUpdated(LobbyChatUpdate_t value)
        {
            if (disposed || !acceptNetworkEvents || !lobbyId.IsValid() || value.m_ulSteamIDLobby != lobbyId.m_SteamID) return;
            var departed = (EChatMemberStateChange)value.m_rgfChatMemberStateChange;
            var terminal = EChatMemberStateChange.k_EChatMemberStateChangeLeft | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected |
                EChatMemberStateChange.k_EChatMemberStateChangeKicked | EChatMemberStateChange.k_EChatMemberStateChangeBanned;
            if ((departed & terminal) != 0 && (value.m_ulSteamIDUserChanged == originalHostSteamId || value.m_ulSteamIDUserChanged == ReadSteamAccount()))
            { ShutdownNetworkAndLobby(); RoomDisbanded?.Invoke(); return; }
            RefreshRoom();
        }
        private void OnWaitingRoomStateReceived(RoomState authoritativeRoom)
        {
            if (disposed || !acceptNetworkEvents || isHost || !lobbyId.IsValid()) return;
            if (!RoomState.TryLocalizeAuthoritativeSnapshot(
                    authoritativeRoom,
                    lobbyId.m_SteamID.ToString(),
                    ReadSteamAccount(),
                    out var localizedRoom))
            {
                return;
            }

            if (currentRoom != null && localizedRoom.StateRevision < currentRoom.StateRevision) return;
            authoritySnapshotReceived = true;
            PublishRoom(localizedRoom);
            pendingJoin?.TrySetResult(localizedRoom.Clone()); pendingJoin = null;
            pendingJoinLobbyId = CSteamID.Nil;
        }
        private void OnServerClientConnected(MirrorServerConnectionInfo connection)
        {
            if (TryAuthenticateServerConnection(connection)) RefreshRoom();
        }

        private bool TryAuthenticateServerConnection(MirrorServerConnectionInfo connection)
        {
            if (disposed || !acceptNetworkEvents || !isHost || !lobbyId.IsValid())
            {
                subscribedRuntime?.RequestServerDisconnect(connection.ConnectionId);
                return false;
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
                    return false;
                }
                steamId = ReadSteamAccount();
                claimedPlayerId = 1;
            }
            else if (!SteamIdentityAddress.TryParse(connection.Address, out steamId))
            {
                RejectServerConnection(connection.ConnectionId, "拒绝无法解析 SteamID 的连接：" + connection.Address);
                return false;
            }
            int membership = ReadLobbyMembership(steamId);
            if (membership == 0 && !connection.IsLocal)
            { pendingSteamConnections[connection.ConnectionId] = connection; subscribedRuntime?.RequestServerIdentityRetry(); return false; }
            pendingSteamConnections.Remove(connection.ConnectionId);
            if (membership < 0) { RejectServerConnection(connection.ConnectionId, "拒绝已离开原Lobby的Steam身份。"); return false; }
            if (!waitingRoomAuthority.TryVerifyIdentity(
                    connection.ConnectionId,
                    steamId,
                    claimedPlayerId,
                    out _))
            {
                RejectServerConnection(connection.ConnectionId, "拒绝非 Lobby 成员、重复或席位不匹配的 Steam 连接。");
                return false;
            }
            return true;
        }
        private void OnPendingSteamIdentityRetry()
        {
            if (disposed || !acceptNetworkEvents || !isHost || pendingSteamConnections.Count == 0) return;
            try { RefreshRoom(); }
            finally { if (pendingSteamConnections.Count != 0) subscribedRuntime?.RequestServerIdentityRetry(); }
        }
        private void OnServerClientDisconnected(int connectionId)
        {
            if (disposed || !acceptNetworkEvents || !isHost) return;
            pendingSteamConnections.Remove(connectionId);
            if (waitingRoomAuthority.Disconnect(connectionId, out _)) RefreshRoom();
        }
        private void RejectServerConnection(int connectionId, string reason)
        {
            waitingRoomAuthority.Disconnect(connectionId, out _);
            pendingSteamConnections.Remove(connectionId);
            Debug.LogWarning(reason);
            subscribedRuntime?.RequestServerDisconnect(connectionId);
            RefreshRoom();
        }
    }
}
