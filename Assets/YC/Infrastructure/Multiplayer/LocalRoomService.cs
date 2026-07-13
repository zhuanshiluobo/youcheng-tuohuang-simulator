using System;
using YC.Application.Sessions;
using YC.Domain.Rules;

namespace YC.Infrastructure.Multiplayer
{
    public sealed class LocalRoomService : IRoomService
    {
        private static readonly Random Random = new Random();
        private static readonly PlayerColor[] SeatColors =
        {
            PlayerColor.Blue,
            PlayerColor.Red,
            PlayerColor.Green,
            PlayerColor.Yellow
        };

        private RoomState currentRoom;

        public RoomState CreateRoom(string hostPlayerName, int playerCount)
        {
            playerCount = Math.Max(3, Math.Min(4, playerCount));
            currentRoom = CreateDefaultRoom(GenerateRoomId(), playerCount);
            currentRoom.HostPlayerId = 1;
            currentRoom.Seats[0].PlayerName = string.IsNullOrEmpty(hostPlayerName) ? "Player 1" : hostPlayerName;
            currentRoom.Seats[0].IsReady = true;
            return currentRoom;
        }

        public RoomState JoinRoom(string roomId, string playerName)
        {
            if (currentRoom == null || currentRoom.RoomId != roomId)
            {
                currentRoom = CreateDefaultRoom(string.IsNullOrEmpty(roomId) ? GenerateRoomId() : roomId, 4);
            }

            for (var i = 0; i < currentRoom.Seats.Count; i++)
            {
                var seat = currentRoom.Seats[i];
                if (!seat.IsReady && seat.PlayerId != currentRoom.HostPlayerId)
                {
                    seat.PlayerName = string.IsNullOrEmpty(playerName) ? "Player " + seat.PlayerId : playerName;
                    seat.IsReady = true;
                    break;
                }
            }

            return currentRoom;
        }

        public RoomState SetReady(int playerId, bool isReady)
        {
            if (currentRoom == null)
            {
                return null;
            }

            for (var i = 0; i < currentRoom.Seats.Count; i++)
            {
                if (currentRoom.Seats[i].PlayerId == playerId)
                {
                    currentRoom.Seats[i].IsReady = isReady;
                    break;
                }
            }

            return currentRoom;
        }

        public RoomState GetCurrentRoom()
        {
            return currentRoom;
        }

        private static RoomState CreateDefaultRoom(string roomId, int playerCount)
        {
            var room = new RoomState
            {
                RoomId = roomId,
                HostPlayerId = 1,
                PlayerCount = playerCount
            };

            for (var i = 0; i < playerCount; i++)
            {
                var playerId = i + 1;
                room.Seats.Add(new PlayerSeat
                {
                    PlayerId = playerId,
                    SteamId = 0,
                    NetworkClientId = 0,
                    PlayerName = "Player " + playerId,
                    Color = SeatColors[i],
                    IsReady = true
                });
            }

            return room;
        }

        private static string GenerateRoomId()
        {
            return Random.Next(100000, 1000000).ToString();
        }
    }
}
