using System;
using System.Collections.Generic;
using YC.Application.Sessions;

namespace YC.Infrastructure.Multiplayer
{
    [Serializable]
    public sealed class RoomState
    {
        public string RoomId = string.Empty;
        public int HostPlayerId = -1;
        public int LocalPlayerId = -1;
        public int PlayerCount;
        public bool HasStarted;
        public ResumeRoomOptions Resume;
        public List<RoomMember> WaitingMembers = new List<RoomMember>();
        public List<PlayerSeat> Seats = new List<PlayerSeat>();

        public RoomState Clone()
        {
            var clone = new RoomState
            {
                RoomId = RoomId,
                HostPlayerId = HostPlayerId,
                LocalPlayerId = LocalPlayerId,
                PlayerCount = PlayerCount,
                HasStarted = HasStarted,
                Resume = Resume?.Clone()
            };

            for (var i = 0; i < Seats.Count; i++)
            {
                var seat = Seats[i];
                clone.Seats.Add(new PlayerSeat
                {
                    PlayerId = seat.PlayerId,
                    SteamId = seat.SteamId,
                    OperatorId = seat.OperatorId,
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

            foreach (var member in WaitingMembers) clone.WaitingMembers.Add(new RoomMember { MemberId = member.MemberId, PlayerName = member.PlayerName });
            return clone;
        }

        public static bool TryLocalizeAuthoritativeSnapshot(
            RoomState snapshot,
            string expectedRoomId,
            ulong localSteamId,
            out RoomState localized)
        {
            localized = null;
            if (snapshot == null || snapshot.Seats == null || localSteamId == 0 ||
                string.IsNullOrEmpty(expectedRoomId) ||
                !string.Equals(snapshot.RoomId, expectedRoomId, StringComparison.Ordinal))
            {
                return false;
            }

            var localPlayerId = -1;
            for (var i = 0; i < snapshot.Seats.Count; i++)
            {
                var seat = snapshot.Seats[i];
                if (seat == null)
                {
                    return false;
                }

                if (seat.SteamId == localSteamId)
                {
                    localPlayerId = seat.PlayerId;
                    break;
                }
            }

            if (localPlayerId <= 0)
            {
                return false;
            }

            localized = snapshot.Clone();
            localized.LocalPlayerId = localPlayerId;
            return true;
        }
    }
}
