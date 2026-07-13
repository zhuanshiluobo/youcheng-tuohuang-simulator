using System;
using System.Threading.Tasks;

namespace YC.Infrastructure.Multiplayer
{
    public interface IOnlineRoomService : IDisposable
    {
        bool SupportsFriendInvites { get; }
        bool HasPendingLobbyJoinRequest { get; }
        event Action<RoomState> RoomUpdated;
        event Action<RoomState> GameStarted;
        event Action RoomDisbanded;
        event Action<string> ErrorOccurred;
        event Action<string> LobbyJoinRequested;
        void Initialize();
        Task<RoomState> CreateRoomAsync(string hostPlayerName, int playerCount);
        Task<RoomState> JoinRoomAsync(string roomId, string playerName);
        Task StartGameAsync();
        void InviteFriends();
        bool TryConsumePendingLobbyJoinRequest(out string lobbyId);
        RoomState GetCurrentRoom();
        void Shutdown();
    }
}
