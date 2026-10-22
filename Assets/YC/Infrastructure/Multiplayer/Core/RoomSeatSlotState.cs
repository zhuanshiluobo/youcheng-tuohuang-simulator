using System;
using YC.Application.Sessions;

namespace YC.Infrastructure.Multiplayer
{
    [Serializable]
    public sealed class RoomSeatSlotState
    {
        public int SeatIndex;
        public string Status = "open";
        public PlayerSeat Occupant;
        public bool IsUserReady;
        public int ReadyConfigurationRevision = -1;
    }

    [Serializable]
    public sealed class RoomSeatSwapState
    {
        public string SwapRequestId = string.Empty;
        public int RequestingPlayerId;
        public int TargetPlayerId;
        public int SourceSeatIndex;
        public int TargetSeatIndex;
        public int ConfigurationRevision;
    }
}
