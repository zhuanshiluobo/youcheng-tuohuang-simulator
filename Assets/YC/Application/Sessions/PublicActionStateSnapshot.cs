using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    internal sealed class PublicActionStateSnapshot
    {
        public readonly Dictionary<int, PublicPlayerActionSnapshot> Players =
            new Dictionary<int, PublicPlayerActionSnapshot>();
        public readonly Dictionary<string, PublicInfluenceActionSnapshot> Influences =
            new Dictionary<string, PublicInfluenceActionSnapshot>(StringComparer.Ordinal);
        public readonly HashSet<string> FacilitySupply =
            new HashSet<string>(StringComparer.Ordinal);

        public string PendingCharacterChoiceType = string.Empty;
        public string PendingCharacterCardId = string.Empty;
        public string PendingCharacterRemainingEffectMode = string.Empty;

        public static PublicActionStateSnapshot Capture(GameState state)
        {
            var snapshot = new PublicActionStateSnapshot();
            if (state == null)
            {
                return snapshot;
            }

            if (state.Players != null)
            {
                for (var i = 0; i < state.Players.Count; i++)
                {
                    var player = state.Players[i];
                    if (player == null)
                    {
                        continue;
                    }

                    snapshot.Players[player.PlayerId] = new PublicPlayerActionSnapshot
                    {
                        PlayerId = player.PlayerId,
                        Name = player.Name ?? string.Empty,
                        Score = player.Score,
                        CityLocationId = player.CityLocationId ?? string.Empty,
                        Resources = player.Resources == null ? new ResourceSet() : player.Resources.Clone(),
                        DiscardCardIds = player.DiscardCardIds == null
                            ? new HashSet<string>(StringComparer.Ordinal)
                            : new HashSet<string>(player.DiscardCardIds, StringComparer.Ordinal)
                    };
                }
            }

            if (state.Map != null && state.Map.Influences != null)
            {
                for (var i = 0; i < state.Map.Influences.Count; i++)
                {
                    var influence = state.Map.Influences[i];
                    if (influence == null || string.IsNullOrEmpty(influence.SlotId))
                    {
                        continue;
                    }

                    snapshot.Influences[influence.SlotId] = new PublicInfluenceActionSnapshot
                    {
                        PlayerId = influence.PlayerId,
                        SlotId = influence.SlotId,
                        LocationId = influence.LocationId ?? string.Empty,
                        RouteId = influence.RouteId ?? string.Empty
                    };
                }
            }

            if (state.Decks != null && state.Decks.FacilitySupply != null)
            {
                for (var i = 0; i < state.Decks.FacilitySupply.Count; i++)
                {
                    snapshot.FacilitySupply.Add(state.Decks.FacilitySupply[i]);
                }
            }

            var pending = state.PendingCharacterEffect;
            if (pending != null && pending.IsValid())
            {
                snapshot.PendingCharacterChoiceType = pending.ChoiceType ?? string.Empty;
                snapshot.PendingCharacterCardId = pending.CardId ?? string.Empty;
                snapshot.PendingCharacterRemainingEffectMode = pending.RemainingEffectMode ?? string.Empty;
            }

            return snapshot;
        }

        public PublicPlayerActionSnapshot FindPlayer(int playerId)
        {
            PublicPlayerActionSnapshot player;
            return Players.TryGetValue(playerId, out player) ? player : null;
        }

        public string GetPlayerLabel(int playerId)
        {
            var player = FindPlayer(playerId);
            return player == null || string.IsNullOrEmpty(player.Name)
                ? "\u73a9\u5bb6 " + playerId
                : player.Name;
        }
    }

    internal sealed class PublicPlayerActionSnapshot
    {
        public int PlayerId;
        public string Name = string.Empty;
        public int Score;
        public string CityLocationId = string.Empty;
        public ResourceSet Resources = new ResourceSet();
        public HashSet<string> DiscardCardIds = new HashSet<string>(StringComparer.Ordinal);
    }

    internal sealed class PublicInfluenceActionSnapshot
    {
        public int PlayerId;
        public string SlotId = string.Empty;
        public string LocationId = string.Empty;
        public string RouteId = string.Empty;
    }
}
