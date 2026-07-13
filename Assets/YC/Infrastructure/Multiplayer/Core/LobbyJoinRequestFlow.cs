using System;
using System.Threading.Tasks;

namespace YC.Infrastructure.Multiplayer
{
    public sealed class LobbyJoinRequestInbox
    {
        private readonly object syncRoot = new object();
        private string pendingLobbyId;

        public event Action<string> LobbyJoinRequested;

        public bool HasPending
        {
            get
            {
                lock (syncRoot)
                {
                    return !string.IsNullOrEmpty(pendingLobbyId);
                }
            }
        }

        public void Record(string lobbyId)
        {
            if (!SteamLobbyPolicy.TryParseLobbyId(lobbyId, out _))
                throw new ArgumentException("Lobby ID 无效。", nameof(lobbyId));

            var normalized = lobbyId.Trim();
            lock (syncRoot)
            {
                pendingLobbyId = normalized;
            }

            LobbyJoinRequested?.Invoke(normalized);
        }

        public bool TryConsume(out string lobbyId)
        {
            lock (syncRoot)
            {
                lobbyId = pendingLobbyId;
                pendingLobbyId = null;
                return !string.IsNullOrEmpty(lobbyId);
            }
        }

        public void Clear()
        {
            lock (syncRoot)
            {
                pendingLobbyId = null;
            }
        }
    }

    public enum LobbyJoinRequestStatus
    {
        None = 0,
        Deferred = 1,
        AlreadyInRoom = 2,
        Joined = 3,
        Canceled = 4,
        Failed = 5
    }

    public sealed class LobbyJoinRequestResult
    {
        private LobbyJoinRequestResult(
            LobbyJoinRequestStatus status,
            string lobbyId,
            RoomState room,
            string message)
        {
            Status = status;
            LobbyId = lobbyId ?? string.Empty;
            Room = room;
            Message = message ?? string.Empty;
        }

        public LobbyJoinRequestStatus Status { get; }
        public string LobbyId { get; }
        public RoomState Room { get; }
        public string Message { get; }

        public static LobbyJoinRequestResult None() =>
            new LobbyJoinRequestResult(LobbyJoinRequestStatus.None, string.Empty, null, string.Empty);

        public static LobbyJoinRequestResult Deferred() =>
            new LobbyJoinRequestResult(LobbyJoinRequestStatus.Deferred, string.Empty, null, string.Empty);

        public static LobbyJoinRequestResult AlreadyInRoom(string lobbyId, RoomState room) =>
            new LobbyJoinRequestResult(LobbyJoinRequestStatus.AlreadyInRoom, lobbyId, room, string.Empty);

        public static LobbyJoinRequestResult Joined(string lobbyId, RoomState room) =>
            new LobbyJoinRequestResult(LobbyJoinRequestStatus.Joined, lobbyId, room, string.Empty);

        public static LobbyJoinRequestResult Canceled(string lobbyId) =>
            new LobbyJoinRequestResult(LobbyJoinRequestStatus.Canceled, lobbyId, null, string.Empty);

        public static LobbyJoinRequestResult Failed(string lobbyId, string message) =>
            new LobbyJoinRequestResult(LobbyJoinRequestStatus.Failed, lobbyId, null, message);
    }

    public sealed class LobbyJoinRequestFlow
    {
        private readonly IOnlineRoomService roomService;
        private bool processing;

        public LobbyJoinRequestFlow(IOnlineRoomService roomService)
        {
            this.roomService = roomService ?? throw new ArgumentNullException(nameof(roomService));
        }

        public async Task<LobbyJoinRequestResult> ProcessPendingAsync(bool defer, string playerName)
        {
            if (!roomService.HasPendingLobbyJoinRequest)
                return LobbyJoinRequestResult.None();
            if (defer || processing)
                return LobbyJoinRequestResult.Deferred();
            if (!roomService.TryConsumePendingLobbyJoinRequest(out var lobbyId))
                return LobbyJoinRequestResult.None();

            var currentRoom = roomService.GetCurrentRoom();
            if (currentRoom != null && string.Equals(currentRoom.RoomId, lobbyId, StringComparison.Ordinal))
                return LobbyJoinRequestResult.AlreadyInRoom(lobbyId, currentRoom);

            processing = true;
            try
            {
                var room = await roomService.JoinRoomAsync(lobbyId, playerName ?? string.Empty);
                return room == null
                    ? LobbyJoinRequestResult.Failed(lobbyId, "受邀房间没有返回有效状态。")
                    : LobbyJoinRequestResult.Joined(lobbyId, room);
            }
            catch (OperationCanceledException)
            {
                return LobbyJoinRequestResult.Canceled(lobbyId);
            }
            catch (Exception ex)
            {
                return LobbyJoinRequestResult.Failed(lobbyId, ex.Message);
            }
            finally
            {
                processing = false;
            }
        }
    }
}
