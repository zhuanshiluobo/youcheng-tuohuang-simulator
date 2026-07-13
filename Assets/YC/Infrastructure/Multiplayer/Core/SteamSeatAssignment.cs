using System;
using System.Collections.Generic;

namespace YC.Infrastructure.Multiplayer
{
    public static class SteamSeatAssignment
    {
        public static IReadOnlyDictionary<int, ulong> Assign(
            ulong hostSteamId,
            int playerCount,
            IEnumerable<ulong> lobbyMembers,
            IReadOnlyDictionary<int, ulong> existingSeats)
        {
            if (hostSteamId == 0) throw new ArgumentOutOfRangeException(nameof(hostSteamId));
            if (playerCount < 1 || playerCount > 4) throw new ArgumentOutOfRangeException(nameof(playerCount));

            var members = new HashSet<ulong>();
            if (lobbyMembers != null)
            {
                foreach (var member in lobbyMembers)
                    if (member > 0) members.Add(member);
            }
            members.Add(hostSteamId);

            var result = new Dictionary<int, ulong> { [1] = hostSteamId };
            var assigned = new HashSet<ulong> { hostSteamId };
            for (var seat = 2; seat <= playerCount; seat++)
            {
                if (existingSeats != null && existingSeats.TryGetValue(seat, out var steamId) &&
                    steamId > 0 && members.Contains(steamId) && assigned.Add(steamId))
                    result[seat] = steamId;
                else
                    result[seat] = 0;
            }

            var unassigned = new List<ulong>();
            foreach (var member in members)
                if (!assigned.Contains(member)) unassigned.Add(member);
            unassigned.Sort();

            var nextMember = 0;
            for (var seat = 2; seat <= playerCount && nextMember < unassigned.Count; seat++)
            {
                if (result[seat] != 0) continue;
                result[seat] = unassigned[nextMember++];
            }
            return result;
        }
    }
}
