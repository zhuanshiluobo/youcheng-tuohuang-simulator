using System.Collections.Generic;
using YC.Application.Sessions;

namespace YC.Infrastructure.Multiplayer
{
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
                    SteamId = seat.SteamId,
                    NetworkClientId = seat.NetworkClientId,
                    PlayerName = seat.PlayerName,
                    Color = seat.Color,
                    LobbyMemberPresent = seat.LobbyMemberPresent,
                    TransportConnected = seat.TransportConnected,
                    IdentityVerified = seat.IdentityVerified,
                    GameStateSynchronized = seat.GameStateSynchronized,
                    IsReady = seat.IsReady
                });
            }

            return clone;
        }
    }
}
