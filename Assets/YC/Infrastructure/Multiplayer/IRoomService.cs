using System.Collections.Generic;
using YC.Application.Sessions;

namespace YC.Infrastructure.Multiplayer
{
    public interface IRoomService
    {
        RoomState CreateRoom(string hostPlayerName, int playerCount);
        RoomState JoinRoom(string roomId, string playerName);
        RoomState SetReady(int playerId, bool isReady);
        RoomState GetCurrentRoom();
    }

    public sealed class RoomState
    {
        public string RoomId = string.Empty;
        public int HostPlayerId = -1;
        public int LocalPlayerId = -1;
        public int PlayerCount;
        public bool HasStarted;
        public List<PlayerSeat> Seats = new List<PlayerSeat>();

        public RoomState Clone()
        {
            var clone = new RoomState
            {
                RoomId = RoomId,
                HostPlayerId = HostPlayerId,
                LocalPlayerId = LocalPlayerId,
                PlayerCount = PlayerCount,
                HasStarted = HasStarted
            };

            for (var i = 0; i < Seats.Count; i++)
            {
                var seat = Seats[i];
                clone.Seats.Add(new PlayerSeat
                {
                    PlayerId = seat.PlayerId,
                    NetcodeClientId = seat.NetcodeClientId,
                    PlayerName = seat.PlayerName,
                    Color = seat.Color,
                    IsReady = seat.IsReady
                });
            }

            return clone;
        }
    }
}
