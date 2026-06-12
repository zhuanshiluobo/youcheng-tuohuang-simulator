using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Influence
{
    public sealed class InfluenceRemovalService
    {
        public InfluenceOperationResult Remove(GameState state, string slotId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return RemoveCore(state, slotId);
        }

        public List<InfluenceOperationResult> RemoveAll(GameState state, IEnumerable<string> slotIds, int? onlyPlayerId = null)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (slotIds == null) throw new ArgumentNullException(nameof(slotIds));
            return RemoveAllCore(state, slotIds, onlyPlayerId);
        }

        private InfluenceOperationResult RemoveCore(GameState state, string slotId)
        {
            var placement = FindInfluenceAtSlot(state, slotId);
            if (placement == null)
            {
                return InfluenceOperationResult.Failure(
                    InfluenceFailureCode.InfluenceNotFound,
                    "Influence must exist before it can be removed.",
                    -1, slotId, false);
            }

            var playerId = placement.PlayerId;
            var resolvedSlotId = placement.SlotId;
            state.Map.Influences.Remove(placement);

            var player = state.FindPlayer(playerId);
            if (player != null)
            {
                player.InfluenceSupply += 1;
            }

            return InfluenceOperationResult.Success(playerId, resolvedSlotId, true);
        }

        private List<InfluenceOperationResult> RemoveAllCore(GameState state, IEnumerable<string> slotIds, int? onlyPlayerId)
        {
            var results = new List<InfluenceOperationResult>();
            var targetSet = new HashSet<string>(slotIds);

            for (var i = state.Map.Influences.Count - 1; i >= 0; i--)
            {
                var placement = state.Map.Influences[i];
                if (!targetSet.Contains(placement.SlotId))
                {
                    continue;
                }

                if (onlyPlayerId.HasValue && placement.PlayerId != onlyPlayerId.Value)
                {
                    continue;
                }

                var playerId = placement.PlayerId;
                var slotId = placement.SlotId;
                state.Map.Influences.RemoveAt(i);

                var player = state.FindPlayer(playerId);
                if (player != null)
                {
                    player.InfluenceSupply += 1;
                }

                results.Add(InfluenceOperationResult.Success(playerId, slotId, true));
            }

            return results;
        }

        private static InfluencePlacement FindInfluenceAtSlot(GameState state, string slotId)
        {
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                if (state.Map.Influences[i].SlotId == slotId)
                {
                    return state.Map.Influences[i];
                }
            }

            return null;
        }
    }
}
