namespace YC.Infrastructure.Multiplayer
{
    public interface IRoomService
    {
        RoomState CreateRoom(string hostPlayerName, int playerCount);
        RoomState JoinRoom(string roomId, string playerName);
        RoomState SetReady(int playerId, bool isReady);
        RoomState GetCurrentRoom();
    }

}
