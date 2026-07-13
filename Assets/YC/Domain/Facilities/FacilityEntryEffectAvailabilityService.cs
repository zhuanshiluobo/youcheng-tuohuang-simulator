using System;
using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.Exploration;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    /// <summary>
    /// 查询强制设施入场效果当前是否至少存在一个合法执行目标。
    /// 本服务只读取状态，不负责创建待选会话或修改游戏状态。
    /// </summary>
    public sealed class FacilityEntryEffectAvailabilityService
    {
        private readonly IMapQueryService mapQuery;
        private readonly InfluenceService influenceService;
        private readonly CityMovementService cityMovementService;
        private readonly ExplorationService explorationService;

        public FacilityEntryEffectAvailabilityService(
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            CityMovementService cityMovementService,
            ExplorationService explorationService)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
            this.cityMovementService = cityMovementService ?? throw new ArgumentNullException(nameof(cityMovementService));
            this.explorationService = explorationService ?? throw new ArgumentNullException(nameof(explorationService));
        }

        public static FacilityEntryEffectAvailabilityService CreateDefault(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var map = state.MapId == StaticMapDefinitions.ThreePlayerMapId
                ? StaticMapDefinitions.CreateThreePlayerPlaceholder()
                : StaticMapDefinitions.CreateFourPlayerMap();
            var mapQuery = new MapQueryService(map);
            var influenceService = new InfluenceService(mapQuery);
            var eventDeckService = new EventDeckService();
            var resourceTokenService = new ResourceTokenService();
            var movementService = new CityMovementService(
                mapQuery,
                influenceService,
                new TravelCostService(mapQuery),
                eventDeckService,
                resourceTokenService);
            var explorationService = new ExplorationService(
                mapQuery,
                influenceService,
                eventDeckService,
                resourceTokenService);

            return new FacilityEntryEffectAvailabilityService(
                mapQuery,
                influenceService,
                movementService,
                explorationService);
        }

        public bool HasLegalFreeCityMove(GameState state, int playerId)
        {
            ValidateState(state);
            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                if (cityMovementService.CanMoveCityForFacility(
                        state,
                        playerId,
                        mapQuery.Map.Locations[i].LocationId).IsValid)
                {
                    return true;
                }
            }

            return false;
        }

        public bool HasLegalInfluenceDeployment(GameState state, int playerId)
        {
            ValidateState(state);
            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var location = mapQuery.Map.Locations[i];
                for (var slotIndex = 0; slotIndex < location.InfluenceSlotCount; slotIndex++)
                {
                    var slotId = InfluenceService.GetLocationSlotId(location.LocationId, slotIndex);
                    if (influenceService.CanPlace(state, playerId, slotId).IsValid)
                    {
                        return true;
                    }
                }
            }

            for (var i = 0; i < mapQuery.Map.Routes.Count; i++)
            {
                var route = mapQuery.Map.Routes[i];
                for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    var slotId = InfluenceService.GetRouteSlotId(route.RouteId, slotIndex);
                    if (influenceService.CanPlace(state, playerId, slotId).IsValid)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public bool HasLegalRemoveOrExplore(GameState state, int playerId)
        {
            ValidateState(state);
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                if (!string.IsNullOrEmpty(state.Map.Influences[i].SlotId))
                {
                    return true;
                }
            }

            return HasLegalFacilityExplore(state, playerId);
        }

        private bool HasLegalFacilityExplore(GameState state, int playerId)
        {
            var paymentRecipients = new Dictionary<string, int>();
            for (var locationIndex = 0; locationIndex < mapQuery.Map.Locations.Count; locationIndex++)
            {
                var targetLocationId = mapQuery.Map.Locations[locationIndex].LocationId;
                IReadOnlyList<MapPath> paths;
                try
                {
                    paths = explorationService.FindDefaultPathChoices(state, playerId, targetLocationId);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                for (var pathIndex = 0; pathIndex < paths.Count; pathIndex++)
                {
                    if (explorationService.CanExplore(
                            state,
                            playerId,
                            targetLocationId,
                            paths[pathIndex],
                            -1,
                            string.Empty,
                            paymentRecipients,
                            null,
                            false,
                            true).IsValid)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void ValidateState(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (state.Map == null)
            {
                throw new ArgumentException("GameState must include map runtime state.", nameof(state));
            }
        }
    }
}
