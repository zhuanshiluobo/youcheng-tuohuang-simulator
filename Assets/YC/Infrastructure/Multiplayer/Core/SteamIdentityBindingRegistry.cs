using System;
using System.Collections.Generic;

namespace YC.Infrastructure.Multiplayer
{
    public sealed class SteamIdentityBindingRegistry
    {
        private readonly object syncRoot = new object();
        private readonly Dictionary<ulong, int> playerBySteamId = new Dictionary<ulong, int>();
        private readonly Dictionary<int, ulong> steamIdByPlayer = new Dictionary<int, ulong>();
        private readonly Dictionary<int, int> playerByConnection = new Dictionary<int, int>();
        private readonly Dictionary<int, int> connectionByPlayer = new Dictionary<int, int>();

        public IReadOnlyList<int> ReplaceLobbySeats(IEnumerable<KeyValuePair<int, ulong>> seats)
        {
            lock (syncRoot)
            {
                var nextPlayerBySteamId = new Dictionary<ulong, int>();
                var nextSteamIdByPlayer = new Dictionary<int, ulong>();
                if (seats != null)
                {
                    foreach (var seat in seats)
                    {
                        if (seat.Key <= 0 || seat.Value == 0 || nextPlayerBySteamId.ContainsKey(seat.Value) ||
                            nextSteamIdByPlayer.ContainsKey(seat.Key))
                            continue;
                        nextPlayerBySteamId.Add(seat.Value, seat.Key);
                        nextSteamIdByPlayer.Add(seat.Key, seat.Value);
                    }
                }

                var removedConnections = new List<int>();
                foreach (var binding in playerByConnection)
                {
                    if (!steamIdByPlayer.TryGetValue(binding.Value, out var previousSteamId) ||
                        !nextSteamIdByPlayer.TryGetValue(binding.Value, out var nextSteamId) ||
                        previousSteamId != nextSteamId)
                        removedConnections.Add(binding.Key);
                }
                for (var i = 0; i < removedConnections.Count; i++)
                    UnbindConnectionLocked(removedConnections[i]);

                playerBySteamId.Clear();
                steamIdByPlayer.Clear();
                foreach (var pair in nextPlayerBySteamId) playerBySteamId.Add(pair.Key, pair.Value);
                foreach (var pair in nextSteamIdByPlayer) steamIdByPlayer.Add(pair.Key, pair.Value);
                return removedConnections;
            }
        }

        public bool TryBindConnection(int connectionId, ulong remoteSteamId, out int playerId)
        {
            lock (syncRoot)
            {
                playerId = -1;
                if (connectionId < 0 || !playerBySteamId.TryGetValue(remoteSteamId, out playerId)) return false;
                if (playerByConnection.TryGetValue(connectionId, out var existing)) return existing == playerId;
                if (connectionByPlayer.ContainsKey(playerId)) { playerId = -1; return false; }
                playerByConnection.Add(connectionId, playerId);
                connectionByPlayer.Add(playerId, connectionId);
                return true;
            }
        }

        public bool TryGetPlayer(int connectionId, out int playerId)
        {
            lock (syncRoot) return playerByConnection.TryGetValue(connectionId, out playerId);
        }

        public bool TryGetConnection(int playerId, out int connectionId)
        {
            lock (syncRoot) return connectionByPlayer.TryGetValue(playerId, out connectionId);
        }

        public bool IsPlayerIdentityVerified(int playerId, ulong steamId)
        {
            lock (syncRoot)
            {
                return steamIdByPlayer.TryGetValue(playerId, out var expectedSteamId) && expectedSteamId == steamId &&
                       connectionByPlayer.TryGetValue(playerId, out var connectionId) &&
                       playerByConnection.TryGetValue(connectionId, out var boundPlayerId) && boundPlayerId == playerId;
            }
        }

        public bool IsCommandOwner(int connectionId, int claimedPlayerId)
        {
            lock (syncRoot)
                return playerByConnection.TryGetValue(connectionId, out var actual) && actual == claimedPlayerId;
        }

        public bool UnbindConnection(int connectionId)
        {
            lock (syncRoot) return UnbindConnectionLocked(connectionId);
        }

        public void Clear()
        {
            lock (syncRoot)
            {
                playerBySteamId.Clear();
                steamIdByPlayer.Clear();
                playerByConnection.Clear();
                connectionByPlayer.Clear();
            }
        }

        private bool UnbindConnectionLocked(int connectionId)
        {
            if (!playerByConnection.TryGetValue(connectionId, out var playerId)) return false;
            playerByConnection.Remove(connectionId);
            connectionByPlayer.Remove(playerId);
            return true;
        }
    }
}
