using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    public static class FacilityInstanceStateService
    {
        public const int CurrentSchemaVersion = 1;
        public const int MaxFacilityInstanceCount = 128;

        public static string EnsureIdentity(GameState state, FacilityPlacement placement)
        {
            if (state == null || placement == null) return string.Empty;
            if (string.IsNullOrEmpty(placement.ContentInstanceId))
            {
                placement.ContentInstanceId = StableIdFactory.Create(
                    "facility-instance",
                    state.GameId ?? string.Empty,
                    placement.PlayerId.ToString(CultureInfo.InvariantCulture),
                    placement.FacilityCardId ?? string.Empty,
                    placement.CityBoardSlotIndex.ToString(CultureInfo.InvariantCulture),
                    state.Map == null || state.Map.Facilities == null
                        ? "0"
                        : state.Map.Facilities.IndexOf(placement).ToString(CultureInfo.InvariantCulture));
            }

            if (state.Map == null)
            {
                state.Map = new MapRuntimeState();
            }

            if (state.Map.FacilityInstances == null)
            {
                state.Map.FacilityInstances = new List<FacilityInstanceRuntimeState>();
            }

            FacilityInstanceRuntimeState existing = Find(state, placement.ContentInstanceId);
            if (existing == null && state.Map.FacilityInstances.Count < MaxFacilityInstanceCount)
            {
                state.Map.FacilityInstances.Add(new FacilityInstanceRuntimeState
                {
                    ContentInstanceId = placement.ContentInstanceId,
                    FacilityCardId = placement.FacilityCardId ?? string.Empty,
                    OwnerPlayerId = placement.PlayerId,
                    CityBoardSlotIndex = placement.CityBoardSlotIndex,
                    SchemaVersion = CurrentSchemaVersion
                });
            }

            return placement.ContentInstanceId;
        }

        public static void EnsureAll(GameState state)
        {
            if (state == null || state.Map == null || state.Map.Facilities == null) return;
            for (int i = 0; i < state.Map.Facilities.Count; i++)
            {
                EnsureIdentity(state, state.Map.Facilities[i]);
            }
        }

        public static FacilityInstanceRuntimeState Find(GameState state, string contentInstanceId)
        {
            if (state == null || state.Map == null || state.Map.FacilityInstances == null ||
                string.IsNullOrEmpty(contentInstanceId)) return null;
            for (int i = 0; i < state.Map.FacilityInstances.Count; i++)
            {
                FacilityInstanceRuntimeState item = state.Map.FacilityInstances[i];
                if (item != null && item.ContentInstanceId == contentInstanceId) return item;
            }

            return null;
        }

        public static FacilityInstanceRuntimeState RecordActivation(
            GameState state,
            string contentInstanceId,
            int round)
        {
            FacilityInstanceRuntimeState item = Find(state, contentInstanceId);
            if (item == null) return null;
            item.ActivationCount = Math.Min(item.ActivationCount + 1, MaxFacilityInstanceCount);
            item.LastActivatedRound = round;
            return item;
        }

        public static bool HasCleanupMarker(GameState state, int playerId)
        {
            if (state == null || state.Map == null || state.Map.FacilityInstances == null) return false;
            for (int i = 0; i < state.Map.FacilityInstances.Count; i++)
            {
                FacilityInstanceRuntimeState item = state.Map.FacilityInstances[i];
                if (item != null && item.OwnerPlayerId == playerId && item.CleanupMarkerRegistered) return true;
            }

            return false;
        }
    }
}
