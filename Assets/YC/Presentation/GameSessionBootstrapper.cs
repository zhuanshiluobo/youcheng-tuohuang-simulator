using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Application.Setup;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Economy;
using YC.Domain.Effects;
using YC.Domain.Exploration;
using YC.Domain.Facilities;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Infrastructure.Lua;
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
            public CityMovementService MovementService;
            public ExplorationService ExplorationService;
            public ResourceCollectionService ResourceCollectionService;
            public EventDeckService EventDeckService;
            public int LocalPlayerId;
        }

        public static Result Build(
            GameLaunchContext launchContext,
            bool useRightCardSmokeState = false,
            bool prepareSharedCityStyleSmokeState = false)
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.Resolve(
                launchContext == null ? StaticMapDefinitions.FourPlayerMapId : launchContext.MapId));
            var launchMode = launchContext == null ? LaunchMode.Local : launchContext.Mode;
            var eventDeckSeed = GetEventDeckSeed(launchContext);
            var eventDeckService = new EventDeckService(eventDeckSeed);
            var localPlayerId = ResolveLocalPlayerId(launchContext);

            var players = launchContext == null ? null : launchContext.Players;
            var state = useRightCardSmokeState
                ? RightCardSmokeStateFactory.CreateInitialState(
                    launchMode,
                    localPlayerId,
                    players,
                    mapQuery.Map.MapId,
                    eventDeckSeed,
                    prepareSharedCityStyleSmokeState)
                : GameLaunchStateFactory.CreateInitialState(
                    launchMode,
                    localPlayerId,
                    players,
                    mapQuery.Map.MapId,
                    eventDeckSeed);
            if (useRightCardSmokeState)
            {
                localPlayerId = state.CurrentPlayerId;
            }

            eventDeckService.InitializeDecks(
                state.Decks,
                EventCardDatabase.GetCardIds(YC.Domain.Rules.EventColor.Green),
                EventCardDatabase.GetCardIds(YC.Domain.Rules.EventColor.Yellow),
                EventCardDatabase.GetCardIds(YC.Domain.Rules.EventColor.Red),
                mapQuery.Map.MapId == StaticMapDefinitions.ThreePlayerMapId ? 3 : 4);

            var resourceTokenService = new ResourceTokenService();
            var session = new GameSession(state);
            var influenceService = new InfluenceService(mapQuery);
            var effectRegistry = new EffectRegistry();
            InfluenceEffectExecutor.Register(effectRegistry, influenceService);
            ResourceEffectExecutor.Register(effectRegistry);

            var travelCostService = new TravelCostService(mapQuery);
            var movementService = new CityMovementService(
                mapQuery,
                influenceService,
                travelCostService,
                eventDeckService,
                resourceTokenService);
            CityMoveEffectExecutor.Register(
                effectRegistry,
                mapQuery,
                influenceService,
                movementService,
                travelCostService,
                resourceTokenService);
            var mainActionBudgetService = new MainActionBudgetService();
            var specialActionLifecycleService = new SpecialActionLifecycleService();
            var resourceSaleService = new ResourceSaleService();
            var turnOrderService = new TurnOrderService();
            var characterCardService = new CharacterCardService(
                turnOrderService,
                resourceSaleService,
                mapQuery,
                influenceService,
                movementService);
            var roundExecutionService = new RoundExecutionService(
                turnOrderService,
                characterCardService,
                mainActionBudgetService,
                specialActionLifecycleService,
                effectRegistry,
                null,
                new FinalScoringService(mapQuery));
            var roundAdvanceService = new RoundAdvanceService(roundExecutionService);
            session.RegisterHandler(new DeployInfluenceCommandHandler(influenceService, roundAdvanceService, effectRegistry));
            session.RegisterHandler(new DispatchInfluenceCommandHandler(influenceService, roundAdvanceService, effectRegistry));
            var moveCityCommandHandler = new MoveCityCommandHandler(
                movementService,
                roundAdvanceService,
                effectRegistry);
            var specialActionOptionQuery = new SpecialActionOptionQueryService(
                mapQuery,
                influenceService,
                movementService,
                specialActionLifecycleService,
                mainActionBudgetService);
            var candidatePolicies = new CandidatePolicyRegistry();
            CityStyleSpecialActionEffectExecutor.Register(
                effectRegistry,
                specialActionOptionQuery,
                candidatePolicies,
                mapQuery,
                influenceService,
                movementService);
            CityStyleLuaCatalog.Register(effectRegistry);
            session.RegisterHandler(new UseSpecialActionCommandHandler(
                effectRegistry,
                specialActionOptionQuery,
                moveCityCommandHandler));
            session.RegisterHandler(moveCityCommandHandler);

            var explorationService = new ExplorationService(
                mapQuery,
                influenceService,
                eventDeckService,
                resourceTokenService);
            ExplorationEffectExecutor.Register(
                effectRegistry,
                mapQuery,
                explorationService,
                eventDeckService,
                resourceTokenService);
            EventCardEffectExecutor.Register(
                effectRegistry,
                mapQuery,
                influenceService,
                eventDeckService,
                resourceTokenService);
            if (EventCardDatabase.IsInitialized)
            {
                EventCardLuaCatalog.Register(effectRegistry);
            }
            var exploreLocationCommandHandler = new ExploreLocationCommandHandler(
                explorationService,
                roundAdvanceService,
                effectRegistry);
            session.RegisterHandler(exploreLocationCommandHandler);
            FacilityEntryEffectExecutor.Register(effectRegistry);
            FacilityLuaCatalog.Register(effectRegistry);
            var buildFacilityService = BuildFacilityService.CreateForEffectTree();
            session.RegisterHandler(new BuildFacilityCommandHandler(
                buildFacilityService,
                roundAdvanceService,
                effectRegistry));
            session.RegisterHandler(new DeclareCityStyleCommandHandler(new DeclareCityStyleService()));
            RoundStartedRedZoneRule.Register(effectRegistry, mapQuery);
            CharacterCardLuaCatalog.Register(effectRegistry);
            session.RegisterHandler(new SetupCommandHandler(
                mapQuery,
                eventDeckService,
                resourceTokenService,
                turnOrderService,
                roundExecutionService));
            session.RegisterHandler(new CoverCharacterCardCommandHandler(characterCardService, effectRegistry));
            session.RegisterHandler(new UseCharacterCardCommandHandler(characterCardService, effectRegistry));
            session.RegisterHandler(new AnswerInteractionCommandHandler(effectRegistry, roundExecutionService));
            session.RegisterHandler(new EndActionCommandHandler(
                roundAdvanceService,
                new FinalScoringService(mapQuery)));
            var resourceCollectionService = new ResourceCollectionService(mapQuery);
            session.RegisterHandler(new CollectResourceCommandHandler(resourceCollectionService, roundAdvanceService));

            if (session.State.Phase == GamePhase.Entrance)
            {
                var entrance = roundExecutionService.StartEntrance(session.State);
                if (!entrance.IsValid) throw new System.InvalidOperationException(entrance.Reason);
            }

            return new Result
            {
                Session = session,
                MapQuery = mapQuery,
                InfluenceService = influenceService,
                MovementService = movementService,
                ExplorationService = explorationService,
                ResourceCollectionService = resourceCollectionService,
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
