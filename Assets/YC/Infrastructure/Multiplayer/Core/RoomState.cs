using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Rules;
using Newtonsoft.Json;

namespace YC.Infrastructure.Multiplayer
{
    [Serializable]
    public sealed class RoomState
    {
        public string RoomId = string.Empty;
        public const int CurrentProtocolVersion = YC.Domain.State.GameProtocolVersions.Room;
        public int ProtocolVersion = CurrentProtocolVersion;
        public int StateRevision;
        public int HostPlayerId = -1;
        public int LocalPlayerId = -1;
        public bool HasStarted;
        public string GameId = string.Empty;
        public int EventDeckSeed;
        public bool RandomReady;
        public string RandomAlgorithmVersion = string.Empty;
        public GameplayRandomState RandomRuntime;
        public List<int> LaunchPlayerOrder = new List<int>();
        public GameRuleConfiguration Configuration = new GameRuleConfiguration();
        public ResumeRoomOptions Resume;
        public List<RoomMember> WaitingMembers = new List<RoomMember>();
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<RoomSeatSlotState> SeatSlots = CreateSeatSlots();
        public List<RoomSeatSwapState> PendingSeatSwaps = new List<RoomSeatSwapState>();
        // 旧接口只投影占位者，集合本身的增删不会修改房间。
        [JsonIgnore]
        public List<PlayerSeat> Seats
        {
            get
            {
                var seats = new List<PlayerSeat>();
                if (SeatSlots != null)
                    foreach (var slot in SeatSlots)
                        if (slot != null && slot.Status == "occupied" && slot.Occupant != null) seats.Add(slot.Occupant);
                return seats;
            }
        }
        public int PlayerCount
        {
            get { return Seats.Count; }
        }

        private static List<RoomSeatSlotState> CreateSeatSlots()
        {
            var slots = new List<RoomSeatSlotState>();
            for (var i = 0; i < 4; i++) slots.Add(new RoomSeatSlotState { SeatIndex = i });
            return slots;
        }

        public RoomState Clone()
        {
            var clone = new RoomState
            {
                RoomId = RoomId,
                HostPlayerId = HostPlayerId,
                LocalPlayerId = LocalPlayerId,
                ProtocolVersion = ProtocolVersion, StateRevision = StateRevision,
                Configuration = Configuration?.Clone(),
                Resume = Resume?.Clone(),
                WaitingMembers = WaitingMembers.ConvertAll(m => new RoomMember { MemberId = m.MemberId, PlayerName = m.PlayerName }),
                HasStarted = HasStarted,
                GameId = GameId, EventDeckSeed = EventDeckSeed, RandomReady = RandomReady,
                RandomAlgorithmVersion = RandomAlgorithmVersion, RandomRuntime = RandomRuntime?.Clone(),
                LaunchPlayerOrder = LaunchPlayerOrder == null ? null : new List<int>(LaunchPlayerOrder),
                SeatSlots = new List<RoomSeatSlotState>(),
                PendingSeatSwaps = new List<RoomSeatSwapState>()
            };

            foreach (var slot in SeatSlots)
            {
                clone.SeatSlots.Add(new RoomSeatSlotState
                {
                    SeatIndex = slot.SeatIndex, Status = slot.Status,
                    IsUserReady = slot.IsUserReady, ReadyConfigurationRevision = slot.ReadyConfigurationRevision,
                    Occupant = slot.Occupant?.Clone()
                });
            }
            foreach (var swap in PendingSeatSwaps)
                clone.PendingSeatSwaps.Add(new RoomSeatSwapState
                {
                    SwapRequestId = swap.SwapRequestId, RequestingPlayerId = swap.RequestingPlayerId,
                    TargetPlayerId = swap.TargetPlayerId, SourceSeatIndex = swap.SourceSeatIndex,
                    TargetSeatIndex = swap.TargetSeatIndex, ConfigurationRevision = swap.ConfigurationRevision
                });

            return clone;
        }

        public static bool TryLocalizeAuthoritativeSnapshot(
            RoomState snapshot,
            string expectedRoomId,
            ulong localSteamId,
            out RoomState localized)
        {
            localized = null;
            if (!RoomStateSerializer.TryValidate(snapshot, out _) || localSteamId == 0 ||
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
                if (snapshot.Resume == null || !snapshot.WaitingMembers.Exists(m => m.MemberId == localSteamId.ToString())) return false;
                localPlayerId = 0;
            }

            localized = snapshot.Clone();
            localized.LocalPlayerId = localPlayerId;
            return true;
        }
    }
}
