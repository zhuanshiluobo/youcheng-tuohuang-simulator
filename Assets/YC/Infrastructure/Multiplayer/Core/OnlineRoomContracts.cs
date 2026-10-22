using System;
using System.Threading.Tasks;

namespace YC.Infrastructure.Multiplayer
{
    public interface ILocalRoomPlayerControl
    {
        bool SupportsLocalPlayers { get; }
        Task<RoomConfigurationResult> AddLocalPlayerAsync(AddLocalRoomPlayerRequest request);
        Task<RoomConfigurationResult> RemoveLocalPlayerAsync(RemoveLocalRoomPlayerRequest request);
    }

    public interface IRoomNetworkingControl
    {
        bool IsOnline { get; }
        bool IsChangingNetworking { get; }
        Task<RoomState> SetOnlineAsync(bool enabled);
    }

    // 在公布网络房间前装入本地房间配置，避免先发布默认配置再逐条补写。
    public interface IPreconfiguredRoomHost
    {
        Task<RoomState> CreateRoomFromSnapshotAsync(RoomState localRoom);
    }

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
        Task<RoomConfigurationResult> ChangeConfigurationAsync(ChangeRoomConfigurationRequest request);
        Task<RoomConfigurationResult> SetReadyAsync(SetRoomReadyRequest request);
        Task<RoomConfigurationResult> SetSeatStatusAsync(SetRoomSeatStatusRequest request);
        Task<RoomConfigurationResult> RequestSeatSwapAsync(RequestRoomSeatSwapRequest request);
        Task<RoomConfigurationResult> ConfirmSeatSwapAsync(ConfirmRoomSeatSwapRequest request);
        Task<RoomConfigurationResult> StartConfiguredGameAsync(StartConfiguredGameRequest request);
        void InviteFriends();
        bool TryConsumePendingLobbyJoinRequest(out string lobbyId);
        RoomState GetCurrentRoom();
        void Shutdown();
    }
}
