using System;
using YC.Application.Sessions;

namespace YC.Infrastructure.Multiplayer
{
    public static class RoomHostSnapshot
    {
        public static RoomState Create(RoomState source, string roomId, PlayerSeat host,
            IRoomContentCapabilityProvider content)
        {
            if (!RoomStateSerializer.TryValidate(source, out var reason))
                throw new InvalidOperationException(reason);
            if (source.HasStarted) throw new InvalidOperationException("configuration_stale");
            var hostSlot = RoomConfigurationService.FindSlot(source, source.HostPlayerId);
            if (hostSlot == null || host == null) throw new InvalidOperationException("not_room_host");
            var room = source.Clone();
            room.RoomId = roomId;
            room.HostPlayerId = room.LocalPlayerId = host.PlayerId;
            room.PendingSeatSwaps.Clear();
            foreach (var slot in room.SeatSlots)
            {
                slot.Occupant = slot.SeatIndex == hostSlot.SeatIndex ? host.Clone() : null;
                slot.Status = slot.Occupant != null ? "occupied" : slot.Status == "closed" ? "closed" : "open";
                slot.IsUserReady = false;
                slot.ReadyConfigurationRevision = -1;
            }
            var occupant = room.SeatSlots[hostSlot.SeatIndex].Occupant;
            occupant.Color = RoomConfigurationService.GetSeatColor(hostSlot.SeatIndex);
            room.Configuration.PlayerCount = 1;
            room.Configuration.ConfigurationRevision++;
            room.StateRevision++;
            room.Configuration.ContentPackHash = content.GetContentPackHash(room.Configuration);
            occupant.ContentPackHash = room.Configuration.ContentPackHash;
            occupant.RuleContractVersion = room.Configuration.RuleContractVersion;
            return room;
        }
    }
}
