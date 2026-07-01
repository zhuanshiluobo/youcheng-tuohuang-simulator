using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Application.Setup;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Exploration;
using YC.Domain.Facilities;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;
using UnityEngine;

namespace YC.Presentation
{
    internal static class GameSessionBootstrapper
    {
        public sealed class Result
        {
            public GameSession Session;
            public MapQueryService MapQuery;
            public InfluenceService InfluenceService;
            public ExplorationService ExplorationService;
            public EventDeckService EventDeckService;
            public int LocalPlayerId;
        }

        public static Result Build(GameLaunchContext launchContext)
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var launchMode = launchContext == null ? LaunchMode.Local : launchContext.Mode;
            var eventDeckSeed = GetEventDeckSeed(launchContext);
            var eventDeckService = new EventDeckService(eventDeckSeed);
            var localPlayerId = ResolveLocalPlayerId(launchContext);

            var state = GameLaunchStateFactory.CreateInitialState(
                launchMode,
                localPlayerId,
                launchContext == null ? null : launchContext.Players,
                mapQuery.Map.MapId,
                eventDeckSeed);

            eventDeckService.InitializeDecks(
                state.Decks,
                EventCardDatabase.GreenCardIds,
                EventCardDatabase.YellowCardIds,
                EventCardDatabase.RedCardIds);

            var session = new GameSession(state);
            session.RegisterHandler(new SetupCommandHandler(
                mapQuery,
                eventDeckService,
                new ResourceTokenService(),
                new TurnOrderService()));
            session.RegisterHandler(new EndActionCommandHandler(
                new RoundAdvanceService(),
                new FinalScoringService(mapQuery)));

            var influenceService = new InfluenceService(mapQuery);
            session.RegisterHandler(new DeployInfluenceCommandHandler(influenceService));
            session.RegisterHandler(new DispatchInfluenceCommandHandler(influenceService));

            var travelCostService = new TravelCostService(mapQuery);
            var movementService = new CityMovementService(
                mapQuery,
                influenceService,
                travelCostService,
                eventDeckService,
                new ResourceTokenService());
            session.RegisterHandler(new MoveCityCommandHandler(movementService));

            var explorationService = new ExplorationService(
                mapQuery,
                influenceService,
                eventDeckService,
                new ResourceTokenService());
            session.RegisterHandler(new ExploreLocationCommandHandler(explorationService));
            session.RegisterHandler(new BuildFacilityCommandHandler(new BuildFacilityService(), new RoundAdvanceService()));
            session.RegisterHandler(new DeclareCityStyleCommandHandler(new DeclareCityStyleService()));
            session.RegisterHandler(new CollectResourceCommandHandler(new ResourceCollectionService(mapQuery)));

            return new Result
            {
                Session = session,
                MapQuery = mapQuery,
                InfluenceService = influenceService,
                ExplorationService = explorationService,
                EventDeckService = eventDeckService,
                LocalPlayerId = localPlayerId
            };
        }

        public static int GetEventDeckSeed(GameLaunchContext launchContext)
        {
            if (launchContext == null || string.IsNullOrEmpty(launchContext.RoomId))
            {
                return EventDeckService.DefaultSeed;
            }

            return EventDeckService.CreateSeed(launchContext.RoomId);
        }

        public static bool IsNetworkLaunch(GameLaunchContext launchContext)
        {
            return launchContext != null && GameLaunchStateFactory.IsNetworkLaunch(launchContext.Mode);
        }

        public static bool LaunchContextContainsLocalPlayer(GameLaunchContext launchContext)
        {
            return launchContext != null &&
                   GameLaunchStateFactory.ContainsPlayer(launchContext.Players, launchContext.LocalPlayerId);
        }

        private static int ResolveLocalPlayerId(GameLaunchContext launchContext)
        {
            if (IsNetworkLaunch(launchContext) && !LaunchContextContainsLocalPlayer(launchContext))
            {
                Debug.LogError("Network launch is missing a valid local player id. The local client will remain spectator-only.");
                return -1;
            }

            if (launchContext != null && launchContext.LocalPlayerId > 0)
            {
                return launchContext.LocalPlayerId;
            }

            return 1;
        }
    }
}
