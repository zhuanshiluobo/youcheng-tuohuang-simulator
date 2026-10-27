using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Application.Presentation;
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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using UnityEngine;

namespace YC.Presentation
{
    internal static class GameSessionBootstrapper
    {
        public sealed class Result
        {
            public GameSession Session;
            public SessionPresentationBinding ActivePresentation;
            public EffectRegistry EffectRegistry;
            public MapQueryService MapQuery;
            public InfluenceService InfluenceService;
            public CityMovementService MovementService;
            public ExplorationService ExplorationService;
            public ResourceCollectionService ResourceCollectionService;
            public EventDeckService EventDeckService;
            public int LocalPlayerId;
            public YC.Domain.Enterprises.EnterpriseDefinitions EnterpriseDefinitions;
            public System.Collections.Generic.IDictionary<string, CharacterModeContract> CharacterModes;
            public System.Collections.Generic.IReadOnlyList<PlayerDetailDisplayDefinition> PlayerDetailDefinitions;
            public YC.Domain.Facilities.FacilityBuildCandidateQuery FacilityBuildCandidates;
            public YC.Domain.Presentation.IGameplayTextCatalogSnapshot GameplayTextCatalog;
            public IReadOnlyDictionary<string, string> GameplayDefinitionNames;
            public GameplayTextResolver TextResolver;
        }

        public static Result BuildForGameplay(GameLaunchContext launchContext,
            bool useRightCardSmokeState, bool prepareSharedCityStyleSmokeState)
        {
            if (!IsNetworkLaunch(launchContext))
                return Build(launchContext, useRightCardSmokeState, prepareSharedCityStyleSmokeState);
            if (useRightCardSmokeState || prepareSharedCityStyleSmokeState)
                throw new System.InvalidOperationException("deterministic_network_launch_disallows_smoke_state");
            return Build(launchContext, false, false, launchContext.Mode == LaunchMode.Client);
        }

        public static Result Build(
            GameLaunchContext launchContext,
            bool useRightCardSmokeState = false,
            bool prepareSharedCityStyleSmokeState = false)
            => Build(launchContext, useRightCardSmokeState, prepareSharedCityStyleSmokeState, false);

        // 保留原三参数反射入口；新模式显式传入两个旧标志，避免可选参数重载歧义。
        internal static Result Build(
            GameLaunchContext launchContext,
            bool useRightCardSmokeState,
            bool prepareSharedCityStyleSmokeState,
            bool deferRuleInitialization = false)
            => BuildCore(launchContext, useRightCardSmokeState, prepareSharedCityStyleSmokeState, deferRuleInitialization, launchContext?.RestoreData);

        public static Result Restore(GameLaunchContext context, MatchSaveData data)
        {
            data.Validate();
            return BuildCore(context, false, false, false, data);
        }

        private static Result BuildCore(GameLaunchContext launchContext, bool useRightCardSmokeState,
            bool prepareSharedCityStyleSmokeState, bool deferRuleInitialization, MatchSaveData restore)
        {
            using var totalTiming = YC.Presentation.Workflows.UiRegressionMetrics.Measure("assembly.bootstrap_total");
            if (deferRuleInitialization && (launchContext == null || launchContext.Mode != LaunchMode.Client ||
                !launchContext.RandomReady || useRightCardSmokeState || prepareSharedCityStyleSmokeState))
                throw new System.InvalidOperationException("deferred_initialization_requires_frozen_client_start");
            if (launchContext == null && restore == null) launchContext = GameLaunchContext.Ensure();
            if (restore == null && !launchContext.RandomReady)
            {
                if (launchContext.Mode != LaunchMode.Local) throw new System.InvalidOperationException("random_initialization_required");
                var initialConfiguration = launchContext.RuleConfiguration ?? new GameRuleConfiguration
                { MapId = launchContext.MapId, PlayerCount = launchContext.Players.Count == 0 ? 1 : launchContext.Players.Count };
                launchContext.ConfigureNewLocal(launchContext.LocalPlayerId, launchContext.RoomId, launchContext.Players, initialConfiguration);
            }
            var mapQuery = new MapQueryService(StaticMapDefinitions.Resolve(
                restore != null ? restore.MapId : launchContext == null ? StaticMapDefinitions.FourPlayerMapId : launchContext.RuleConfiguration?.MapId ?? launchContext.MapId));
            var launchMode = restore?.Mode ?? (launchContext == null ? LaunchMode.Local : launchContext.Mode);
            var eventDeckSeed = restore?.EventDeckSeed ?? GetEventDeckSeed(launchContext);
            var eventDeckService = new EventDeckService(eventDeckSeed);
            var localPlayerId = restore?.LocalPlayerId ?? ResolveLocalPlayerId(launchContext);

            var players = restore != null ? restore.Seats.ConvertAll(s => s.ToSeat()) : launchContext == null ? null : launchContext.Players;
            var configuration = restore?.Configuration ?? launchContext?.RuleConfiguration?.Clone() ?? new GameRuleConfiguration
            { MapId = mapQuery.Map.MapId, PlayerCount = players == null || players.Count == 0 ? 1 : players.Count };
            configuration.PlayerStageSchedulingMode = launchMode == LaunchMode.Local
                ? PlayerStageSchedulingMode.Sequential : PlayerStageSchedulingMode.Concurrent;
            ActiveContentCatalog activeCatalog;
            using (YC.Presentation.Workflows.UiRegressionMetrics.Measure("assembly.active_catalog"))
                activeCatalog = ActiveContentCatalogBuilder.Build(ExternalContentRuntime.Pack, configuration);
            if (restore != null && activeCatalog.ContentPackHash != restore.ContentHash)
                throw new System.InvalidOperationException("存档的内容版本与当前配置不一致，请使用保存时的内容包。");
            var textResolver = new GameplayTextResolver(activeCatalog.TextCatalog, cacheScopeId: "catalog:" + activeCatalog.ContentPackHash);
            try { textResolver.Precompile(); }
            catch (GameplayTextFormatException error)
            {
                string source = "";
                (activeCatalog.TextCatalog as YC.Domain.Presentation.IGameplayTextSourceCatalog)?.TryGetTemplateSource(error.TextKey, out source);
                throw new System.IO.InvalidDataException("玩法文案目录语法错误：" + source + ":" + error.TextKey + "@" + error.Position + "：" + error.Message, error);
            }
            configuration.ContentPackHash = activeCatalog.ContentPackHash;
            var state = useRightCardSmokeState
                ? RightCardSmokeStateFactory.CreateInitialState(
                    launchMode,
                    localPlayerId,
                    players,
                    mapQuery.Map.MapId,
                    eventDeckSeed,
                    prepareSharedCityStyleSmokeState)
                : GameLaunchStateFactory.CreateInitialState(
                    restore != null ? LaunchMode.Client : launchMode,
                    localPlayerId,
                    players,
                    configuration, eventDeckSeed, activeCatalog.SetupRecipe, activeCatalog.ContentDefinitions,
                    restore?.LaunchPlayerOrder ?? launchContext.LaunchPlayerOrder, restore?.GameId ?? launchContext.GameId,
                    restore?.RandomState ?? launchContext.InitialRandomState);
            if (useRightCardSmokeState)
            {
                state.GameId = launchContext.GameId;
                state.EventDeckSeed = eventDeckSeed;
                state.RandomRuntime = launchContext.InitialRandomState.Clone();
                localPlayerId = state.CurrentPlayerId;
            }

            if (restore == null && launchMode != LaunchMode.Client && !YC.Domain.Items.ItemZoneLegacyProjection.IsActive(state)) eventDeckService.InitializeDecks(
                state,
                EventCardDatabase.GetCardIds(YC.Domain.Rules.EventColor.Green),
                EventCardDatabase.GetCardIds(YC.Domain.Rules.EventColor.Yellow),
                EventCardDatabase.GetCardIds(YC.Domain.Rules.EventColor.Red),
                mapId: state.MapId);

            var resourceTokenService = new ResourceTokenService();
            var session = new GameSession(state);
            var influenceService = new InfluenceService(mapQuery);
            var effectRegistry = new EffectRegistry();
            RouteResourceSelectionEffectExecutor.Register(effectRegistry, mapQuery);
            activeCatalog.Register(effectRegistry);
            session.ConfigurePaymentSourceQueries(effectRegistry);
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
            }
            var exploreLocationCommandHandler = new ExploreLocationCommandHandler(
                explorationService,
                roundAdvanceService,
                effectRegistry);
            session.RegisterHandler(exploreLocationCommandHandler);
            FacilityEntryEffectExecutor.Register(effectRegistry);
            var buildFacilityService = BuildFacilityService.CreateForEffectTree();
            session.RegisterHandler(new BuildFacilityCommandHandler(
                buildFacilityService,
                roundAdvanceService,
                effectRegistry));
            session.RegisterHandler(new DeclareCityStyleCommandHandler(new DeclareCityStyleService(), effectRegistry));
            RoundStartedRedZoneRule.Register(effectRegistry, mapQuery);
            session.RegisterHandler(new SetupCommandHandler(
                mapQuery,
                eventDeckService,
                resourceTokenService,
                turnOrderService,
                roundExecutionService));
            session.RegisterHandler(new CoverCharacterCardCommandHandler(characterCardService, effectRegistry));
            session.RegisterHandler(new UseCharacterCardCommandHandler(characterCardService, effectRegistry));
            session.RegisterHandler(new AnswerInteractionCommandHandler(effectRegistry, roundExecutionService));
            session.RegisterHandler(new InvokeSettlementOptionCommandHandler(effectRegistry, roundExecutionService));
            session.RegisterHandler(new PreparePaymentDraftCommandHandler(effectRegistry, roundExecutionService));
            session.RegisterHandler(new EndActionCommandHandler(
                roundAdvanceService,
                new FinalScoringService(mapQuery), effectRegistry, roundExecutionService, roundExecutionService.Advance));
            var resourceCollectionService = new ResourceCollectionService(mapQuery);
            session.RegisterHandler(new CollectResourceCommandHandler(resourceCollectionService, roundAdvanceService));

            if (restore != null)
            {
                var recovered = HostRecoveryService.RecoverJson(restore.ArchiveJson, effectRegistry);
                if (!recovered.Succeeded) throw new System.InvalidOperationException(recovered.FaultCode + ":" + recovered.Diagnostic);
                if (recovered.State.GameId != restore.GameId || recovered.State.ContentPackHash != activeCatalog.ContentPackHash ||
                    recovered.State.MapId != restore.MapId || recovered.State.EventDeckSeed != restore.EventDeckSeed ||
                    Newtonsoft.Json.JsonConvert.SerializeObject(recovered.State.RuleConfiguration) != Newtonsoft.Json.JsonConvert.SerializeObject(configuration))
                    throw new System.InvalidOperationException("存档恢复身份不一致。");
                if (restore.Mode == LaunchMode.Host)
                {
                    var baseline = SimulationConfirmedRecoveryInstaller.ReadBaseline(restore.SimulationCheckpointJson);
                    OperationCommandCanonicalizer.ValidateRecoveryBaseline(MatchSaveData.OptionsFor(baseline, true, localPlayerId), baseline, recovered.State);
                }
                var continuation = SimulationConfirmedRecoveryInstaller.ReadContinuation(restore.ContinuationJson);
                PreparedGameSessionRecovery prepared;
                string fault;
                bool ready = launchMode == LaunchMode.Local
                    ? session.TryPrepareLocalRecoveryState(recovered.State, continuation, out prepared, out fault)
                    : session.TryPrepareConfirmedRecoveryState(recovered.State, continuation, GameStateViewer.Player(localPlayerId), out prepared, out fault);
                if (!ready ||
                    !session.TryCommitPreparedRecovery(prepared, out fault)) throw new System.InvalidOperationException(fault);
            }
            else if (!deferRuleInitialization && session.State.Phase == GamePhase.Entrance)
            {
                if (session.State.RuleConfiguration != null && !string.IsNullOrEmpty(session.State.RuleConfiguration.ContentPackHash))
                    effectRegistry.ContentPackHash = session.State.RuleConfiguration.ContentPackHash;
                var entrance = roundExecutionService.StartEntrance(session.State);
                if (!entrance.IsValid) throw new System.InvalidOperationException(entrance.Reason);
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (launchContext != null && launchMode == LaunchMode.Host)
                NetworkGameValidationDriver.BindHostRecoverySession(session, effectRegistry);
#endif

            SessionPresentationBinding activePresentation;
            using (YC.Presentation.Workflows.UiRegressionMetrics.Measure("assembly.presentation_bind"))
                activePresentation = SessionPresentationBinding.Create(activeCatalog.Presentation, session, session.State.GameId,
                    session.State.RuleConfiguration, session.State.ContentPackHash, ExternalContentRuntime.Pack.ContentHash);
            if (activePresentation == null && !useRightCardSmokeState)
                throw new System.InvalidOperationException("active_presentation_binding_invalid");
            if (activePresentation == null)
                YC.Presentation.Workflows.UiRegressionMetrics.Note("assembly.presentation_unavailable", "legacy_smoke_without_matching_configuration");
            return new Result
            {
                Session = session,
                ActivePresentation = activePresentation,
                EffectRegistry = effectRegistry,
                MapQuery = mapQuery,
                InfluenceService = influenceService,
                MovementService = movementService,
                ExplorationService = explorationService,
                ResourceCollectionService = resourceCollectionService,
                EventDeckService = eventDeckService,
                LocalPlayerId = localPlayerId,
                EnterpriseDefinitions = effectRegistry.EnterpriseDefinitions,
                CharacterModes = effectRegistry.CharacterAbilityContracts,
                PlayerDetailDefinitions = activeCatalog.PlayerDetailDefinitions,
                FacilityBuildCandidates = effectRegistry.FacilityBuildCandidates,
                GameplayTextCatalog = activeCatalog.TextCatalog,
                GameplayDefinitionNames = new ReadOnlyDictionary<string, string>(activeCatalog.Definitions.ToDictionary(d => d.DefinitionId, d => d.DisplayName, System.StringComparer.Ordinal)),
                TextResolver = textResolver
            };
        }

        public static int GetEventDeckSeed(GameLaunchContext launchContext)
        {
            if (launchContext == null || !launchContext.RandomReady) throw new System.InvalidOperationException("random_initialization_required");
            return launchContext.EventDeckSeed;
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
