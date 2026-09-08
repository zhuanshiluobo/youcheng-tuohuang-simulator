using System.Collections.Generic;
using System.Text;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Application.Setup;
using YC.Domain.Cards;
using YC.Domain.CardFlows;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Economy;
using YC.Domain.Exploration;
using YC.Domain.Facilities;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.SpecialActions;
using YC.Domain.State;

namespace YC.Application.DevTools
{
    public static class LocalhostAutoplayRunner
    {
        public const string DefaultRoomId = "DEV_LOCALHOST_AUTOPLAY";

        private static readonly string[] FourPlayerInitialLocations = { "G-01", "A-01", "A-02", "B-01" };
        private const int RequiredExploreSuccesses = 1;
        private const int RequiredMoveCitySuccesses = 1;
        private const int RequiredDispatchInfluenceSuccesses = 1;
        private const int RequiredPaidRouteCollectionSuccesses = 1;
        private const int RequiredOpponentRouteRecipientCollectionSuccesses = 1;
        private const int AutoplayCharacterPlayerId = 1;
        private const string AutoplayCharacterTemplateId = CharacterCardDatabase.Elysium;
        private static readonly string[] ThreePlayerInitialLocations = { "A-01", "A-02", "B-01" };
        private const string FormalSupplyBlueFacilityId = FacilityCardDatabase.TradeDistrict;
        private const string FormalSupplyRedFacilityId = FacilityCardDatabase.EquipmentWarehouse;
        private const int LevelTwoFixturePlayerId = 3;
        private const string LevelOneAutoplayCityStyleId = CityStyleDatabase.MilitaryIndustrialArea;
        private const string LevelTwoAutoplayCityStyleId = CityStyleDatabase.SourceStoneIndustrialHub;
        private const string LevelOneAutoplaySpecialActionId = SpecialActionDatabase.MilitaryIndustrialArea;
        private const string LevelTwoAutoplaySpecialActionId = SpecialActionDatabase.SourceStoneIndustrialHub;
        private const int LevelTwoAutoplayGoldVoucherFixture = 6;

        private static readonly string[] LevelTwoFixtureFacilityIds =
        {
            "building_016",
            "building_040",
            "building_017",
            "building_023",
            "building_041"
        };

        private static readonly int[] LevelTwoFixtureFacilitySlots = { 0, 3, 4, 6, 8 };

        public static LocalhostAutoplayResult RunToRound8Settlement(int playerCount = 4)
        {
            var map = StaticMapDefinitions.ForPlayerCount(playerCount);
            var seats = CreateJoinedSeats(playerCount);
            var eventDeckSeed = EventDeckService.CreateSeed(DefaultRoomId);
            var session = CreateHostSession(map, seats, eventDeckSeed);
            var dispatcher = new AuthoritativeCommandDispatcher(session);
            var acceptedCommands = 0;
            dispatcher.CommandAccepted += _ => acceptedCommands++;
            RegisterClientSeats(dispatcher, seats);

            var result = new LocalhostAutoplayResult
            {
                RoomId = DefaultRoomId,
                EventDeckSeed = eventDeckSeed,
                Seats = seats,
                FinalState = session.State
            };
            CaptureAutoplaySpecialActionFixtures(session.State, result);

            if (SubmitInitialPlacements(dispatcher, session.State, result) &&
                LoopEndActions(dispatcher, session.State, result))
            {
                var reachedFinalScoring =
                    session.State.Round == 8 && session.State.Phase == GamePhase.FinalScoring;
                var completedRequiredSpecialActions =
                    HasCompletedSpecialAction(result, LevelOneAutoplaySpecialActionId) &&
                    HasCompletedSpecialAction(result, LevelTwoAutoplaySpecialActionId);
                var completedRequiredActionCoverage = HasCompletedRequiredActionCoverage(result);
                result.Succeeded =
                    reachedFinalScoring &&
                    completedRequiredSpecialActions &&
                    completedRequiredActionCoverage;
                if (!reachedFinalScoring)
                {
                    result.FailureReason = "自动跑局未到达第 8 回合结算点。";
                }
                else if (!completedRequiredSpecialActions)
                {
                    result.FailureReason = "自动跑局未完成 I 级和 II 级特殊行动的正式结算。";
                }
                else if (!completedRequiredActionCoverage)
                {
                    result.FailureReason = "自动跑局未完整覆盖部署、探索、建设、发动角色卡、移动、宣告和特殊行动。";
                }
            }

            result.AcceptedCommands = acceptedCommands;
            result.Snapshot = BuildSnapshot(result);
            return result;
        }

        private static GameSession CreateHostSession(GameMapDefinition map, IList<PlayerSeat> seats, int eventDeckSeed)
        {
            var mapQuery = new MapQueryService(map);
            var influenceService = new InfluenceService(mapQuery);
            var resourceTokenService = new ResourceTokenService();
            var eventDeckService = new EventDeckService(eventDeckSeed);
            var explorationService = new ExplorationService(
                mapQuery,
                influenceService,
                eventDeckService,
                resourceTokenService);
            var movementService = new CityMovementService(
                mapQuery,
                influenceService,
                new TravelCostService(mapQuery),
                eventDeckService,
                resourceTokenService);
            var availabilityService = new FacilityEntryEffectAvailabilityService(
                mapQuery,
                influenceService,
                movementService,
                explorationService);
            var entryEffectService = new FacilityEntryEffectService(availabilityService);
            var buildFacilityService = new BuildFacilityService(
                new FacilityEntryEffectResolver(entryEffectService));
            var resourceSaleService = new ResourceSaleService();
            var turnOrderService = new TurnOrderService();
            var characterCardService = new CharacterCardService(
                turnOrderService,
                resourceSaleService,
                mapQuery,
                influenceService,
                movementService);
            var mainActionBudgetService = new MainActionBudgetService();
            var specialActionLifecycleService = new SpecialActionLifecycleService();
            var moveCityCommandHandler = new MoveCityCommandHandler(movementService);
            var specialActionOptionQuery = new SpecialActionOptionQueryService(
                mapQuery,
                influenceService,
                movementService,
                specialActionLifecycleService,
                mainActionBudgetService);
            var specialActionService = new SpecialActionService(
                specialActionOptionQuery,
                specialActionLifecycleService,
                influenceService,
                new FacilityInfluenceEffectService(influenceService),
                mainActionBudgetService);
            var exploreLocationCommandHandler = new ExploreLocationCommandHandler(explorationService);
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, seats, map.MapId, eventDeckSeed);
            EnsureAutoplaySpecialActionFixtures(state);
            EnsureAutoplayFormalFacilitySupply(state);

            eventDeckService.InitializeDecks(
                state.Decks,
                EventCardDatabase.GreenCardIds,
                EventCardDatabase.YellowCardIds,
                EventCardDatabase.RedCardIds,
                playerCount: seats.Count);

            var session = new GameSession(state);
            session.RegisterHandler(new SetupCommandHandler(
                mapQuery,
                eventDeckService,
                resourceTokenService,
                new TurnOrderService()));
            session.RegisterHandler(new BuildFacilityCommandHandler(buildFacilityService, new RoundAdvanceService()));
            session.RegisterHandler(new DeclareCityStyleCommandHandler());
            session.RegisterHandler(new CoverCharacterCardCommandHandler(characterCardService));
            session.RegisterHandler(new UseCharacterCardCommandHandler(characterCardService));
            session.RegisterHandler(new DeployInfluenceCommandHandler(influenceService));
            session.RegisterHandler(new DispatchInfluenceCommandHandler(influenceService));
            session.RegisterHandler(exploreLocationCommandHandler);
            session.RegisterHandler(new UseSpecialActionCommandHandler(
                specialActionService,
                specialActionOptionQuery,
                moveCityCommandHandler));
            session.RegisterHandler(moveCityCommandHandler);
            session.RegisterHandler(new ResolveFacilityEffectCommandHandler(
                buildFacilityService,
                entryEffectService,
                influenceService,
                moveCityCommandHandler,
                exploreLocationCommandHandler,
                mapQuery,
                resourceSaleService));
            session.RegisterHandler(new EndActionCommandHandler(
                new RoundAdvanceService(
                    turnOrderService,
                    characterCardService,
                    mainActionBudgetService,
                    specialActionLifecycleService),
                new FinalScoringService(mapQuery)));
            session.RegisterHandler(new CollectResourceCommandHandler(new ResourceCollectionService(mapQuery, resourceTokenService)));
            return session;
        }

        private static void EnsureAutoplaySpecialActionFixtures(GameState state)
        {
            if (state == null)
            {
                return;
            }

            // 三人 P2 承担 I 级正式建设：预算独立于起点奖励，不直接植入设施或宣告。
            if (state.Players.Count == 3)
            {
                var builder = state.FindPlayer(ResolveFormalSupplyBuildPlayerId(state));
                var budget = FacilityCardDatabase.Get(FormalSupplyBlueFacilityId).GoldVoucherCost +
                             FacilityCardDatabase.Get(FormalSupplyRedFacilityId).GoldVoucherCost;
                if (builder != null && builder.Resources.GoldVoucher < budget)
                {
                    builder.Resources.GoldVoucher = budget;
                }
            }

            var player = state.FindPlayer(LevelTwoFixturePlayerId);
            if (player == null)
            {
                return;
            }

            for (var i = 0; i < LevelTwoFixtureFacilityIds.Length; i++)
            {
                SeedAutoplayFacility(
                    state,
                    player,
                    LevelTwoFixtureFacilityIds[i],
                    LevelTwoFixtureFacilitySlots[i]);
            }

            while (state.Decks.FacilitySupply.Count < 6 && state.Decks.FacilityDeck.Count > 0)
            {
                var replacementId = state.Decks.FacilityDeck[0];
                state.Decks.FacilityDeck.RemoveAt(0);
                state.Decks.FacilitySupply.Add(replacementId);
            }

            if (player.Resources.GoldVoucher < LevelTwoAutoplayGoldVoucherFixture)
            {
                player.Resources.GoldVoucher = LevelTwoAutoplayGoldVoucherFixture;
            }
        }

        private static void SeedAutoplayFacility(
            GameState state,
            PlayerState player,
            string facilityId,
            int cityBoardSlotIndex)
        {
            for (var i = 0; i < state.Map.Facilities.Count; i++)
            {
                var existing = state.Map.Facilities[i];
                if (existing.PlayerId == player.PlayerId && existing.CityBoardSlotIndex == cityBoardSlotIndex)
                {
                    return;
                }
            }

            state.Decks.FacilitySupply.Remove(facilityId);
            state.Decks.FacilityDeck.Remove(facilityId);
            if (!player.BuiltFacilityIds.Contains(facilityId))
            {
                player.BuiltFacilityIds.Add(facilityId);
            }

            state.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = player.PlayerId,
                FacilityCardId = facilityId,
                CityBoardSlotIndex = cityBoardSlotIndex
            });
        }

        private static void CaptureAutoplaySpecialActionFixtures(
            GameState state,
            LocalhostAutoplayResult result)
        {
            var player = state == null ? null : state.FindPlayer(LevelTwoFixturePlayerId);
            if (player == null || result == null)
            {
                return;
            }

            if (state.Players.Count == 3)
            {
                var builder = state.FindPlayer(ResolveFormalSupplyBuildPlayerId(state));
                result.SpecialActionFixtures.Add(
                    "P" + builder.PlayerId + " goldVoucher=" + builder.Resources.GoldVoucher +
                    " purpose=level-one-formal-supply-build");
            }

            for (var fixtureIndex = 0; fixtureIndex < LevelTwoFixtureFacilityIds.Length; fixtureIndex++)
            {
                for (var placementIndex = 0; placementIndex < state.Map.Facilities.Count; placementIndex++)
                {
                    var placement = state.Map.Facilities[placementIndex];
                    if (placement.PlayerId != player.PlayerId ||
                        placement.FacilityCardId != LevelTwoFixtureFacilityIds[fixtureIndex] ||
                        placement.CityBoardSlotIndex != LevelTwoFixtureFacilitySlots[fixtureIndex])
                    {
                        continue;
                    }

                    result.SeededSpecialActionFacilityCount++;
                    result.SpecialActionFixtures.Add(
                        "P" + player.PlayerId +
                        " facility=" + placement.FacilityCardId +
                        " slot=" + placement.CityBoardSlotIndex);
                    break;
                }
            }

            result.SpecialActionFixtures.Add(
                "P" + player.PlayerId + " goldVoucher=" + player.Resources.GoldVoucher);
        }

        private static void EnsureAutoplayFormalFacilitySupply(GameState state)
        {
            if (state == null)
            {
                return;
            }

            MoveFacilityToFrontOfSupply(state, FacilityCardDatabase.SimpleEngineeringCamp);
            MoveFacilityToFrontOfSupply(state, FormalSupplyRedFacilityId);
            MoveFacilityToFrontOfSupply(state, FormalSupplyBlueFacilityId);
        }

        private static void MoveFacilityToFrontOfSupply(GameState state, string facilityId)
        {
            var supplyIndex = state.Decks.FacilitySupply.IndexOf(facilityId);
            if (supplyIndex >= 0)
            {
                state.Decks.FacilitySupply.RemoveAt(supplyIndex);
                state.Decks.FacilitySupply.Insert(0, facilityId);
                return;
            }

            var deckIndex = state.Decks.FacilityDeck.IndexOf(facilityId);
            if (deckIndex < 0)
            {
                return;
            }

            state.Decks.FacilityDeck.RemoveAt(deckIndex);
            if (state.Decks.FacilitySupply.Count >= 6)
            {
                var displacedFacilityId = state.Decks.FacilitySupply[state.Decks.FacilitySupply.Count - 1];
                state.Decks.FacilitySupply.RemoveAt(state.Decks.FacilitySupply.Count - 1);
                state.Decks.FacilityDeck.Insert(0, displacedFacilityId);
            }

            state.Decks.FacilitySupply.Insert(0, facilityId);
        }

        private static bool SubmitInitialPlacements(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            LocalhostAutoplayResult result)
        {
            var initialLocations = state.Players.Count == 3 ? ThreePlayerInitialLocations : FourPlayerInitialLocations;
            for (var i = 0; i < initialLocations.Length; i++)
            {
                var playerId = i + 1;
                if (!SubmitCommand(dispatcher, CreateCommand(
                    "autoplay-place-" + playerId,
                    GameCommandKind.ChooseInitialLocation,
                    playerId,
                    initialLocations[i]), result))
                {
                    return false;
                }

                var pendingChoice = CardFlowStateAdapter.GetPendingChoiceView(state);
                if (pendingChoice != null &&
                    pendingChoice.PlayerId == playerId &&
                    pendingChoice.OptionIds.Count > 0)
                {
                    if (!SubmitCommand(dispatcher, CreateResolveEntranceCommand(playerId, pendingChoice.OptionIds[0]), result))
                    {
                        return false;
                    }
                }
            }

            if (state.Phase == GamePhase.CharacterCover && state.Round == 1)
            {
                return true;
            }

            result.FailureReason = "初始选点未完成，无法进入第 1 回合行动轮。";
            return false;
        }

        private static bool LoopEndActions(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            LocalhostAutoplayResult result)
        {
            while (state.Phase != GamePhase.FinalScoring)
            {
                if (result.SubmittedCommands >= 320)
                {
                    result.FailureReason = "自动跑局超过最大命令数，可能出现回合推进卡住。";
                    return false;
                }

                if (state.HasPendingChoice())
                {
                    if (!TrySubmitAutoplayPendingChoice(dispatcher, state, result))
                    {
                        return false;
                    }

                    continue;
                }

                if (state.Phase == GamePhase.CharacterCover)
                {
                    if (!SubmitCharacterCovers(dispatcher, state, result))
                    {
                        return false;
                    }

                    continue;
                }

                if (state.Phase == GamePhase.ResourceCollection)
                {
                    if (!SubmitResourceCollection(dispatcher, state, result))
                    {
                        return false;
                    }

                    continue;
                }

                if (state.Phase == GamePhase.Cleanup)
                {
                    if (!SubmitCommand(dispatcher, CreateCommand(
                        "autoplay-cleanup-r" + state.Round + "-p" + state.StartPlayerId,
                        GameCommandKind.EndAction,
                        state.StartPlayerId,
                        string.Empty), result))
                    {
                        return false;
                    }

                    continue;
                }

                if (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2)
                {
                    result.FailureReason = "自动跑局进入了非行动阶段：" + state.Phase;
                    return false;
                }

                var player = state.FindPlayer(state.CurrentPlayerId);
                if (player == null)
                {
                    result.FailureReason = "当前行动玩家不存在：" + state.CurrentPlayerId;
                    return false;
                }

                if (!TrySubmitAutoplayCharacterCard(dispatcher, state, player, result))
                {
                    return false;
                }

                if (!TrySubmitAutoplayCityStyle(dispatcher, state, player, result))
                {
                    return false;
                }

                if (!player.ActedMainActionThisTurn &&
                    !TrySubmitAutoplayMainAction(dispatcher, state, player, result) &&
                    string.IsNullOrEmpty(result.FailureReason))
                {
                    player.ActedMainActionThisTurn = true;
                }

                if (state.HasPendingChoice())
                {
                    continue;
                }

                if (!SubmitCommand(dispatcher, CreateCommand(
                    "autoplay-end-r" + state.Round + "-a" + state.ActionRound + "-p" + state.CurrentPlayerId,
                    GameCommandKind.EndAction,
                    state.CurrentPlayerId,
                    string.Empty), result))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SubmitCharacterCovers(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            LocalhostAutoplayResult result)
        {
            var submitted = 0;
            while (state.Phase == GamePhase.CharacterCover)
            {
                if (submitted >= state.Players.Count)
                {
                    result.FailureReason = "角色牌盖放阶段未在全员提交后推进。";
                    return false;
                }

                var player = state.FindPlayer(state.CurrentPlayerId);
                if (player == null)
                {
                    result.FailureReason = "角色牌盖放阶段的当前玩家不存在。";
                    return false;
                }

                var cardId = ResolveAutoplayCoverCardId(player, result);
                if (string.IsNullOrEmpty(cardId))
                {
                    result.FailureReason = "玩家 " + player.PlayerId + " 没有可盖放的角色牌。";
                    return false;
                }

                var command = CreateCommand(
                    "autoplay-cover-r" + state.Round + "-p" + player.PlayerId,
                    GameCommandKind.CoverCharacterCard,
                    player.PlayerId,
                    cardId);
                command.Parameters[CoverCharacterCardCommandHandler.CardIdParameter] = cardId;
                if (!SubmitCommand(dispatcher, command, result))
                {
                    return false;
                }

                submitted++;
            }

            return state.Phase == GamePhase.ActionRound1;
        }

        private static string ResolveAutoplayCoverCardId(
            PlayerState player,
            LocalhostAutoplayResult result)
        {
            if (player == null)
            {
                return string.Empty;
            }

            var source = player.HandCardIds.Count > 0
                ? player.HandCardIds
                : player.DiscardCardIds;
            if (player.PlayerId == AutoplayCharacterPlayerId &&
                result.CharacterCardUseSuccesses == 0)
            {
                for (var i = 0; i < source.Count; i++)
                {
                    var definition = CharacterCardDatabase.Get(source[i]);
                    if (definition != null && definition.TemplateId == AutoplayCharacterTemplateId)
                    {
                        return source[i];
                    }
                }
            }

            return source.Count > 0 ? source[0] : string.Empty;
        }

        private static bool TrySubmitAutoplayPendingChoice(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            LocalhostAutoplayResult result)
        {
            var pendingSpecialAction = state.PendingSpecialAction;
            if (pendingSpecialAction != null &&
                pendingSpecialAction.IsValid() &&
                pendingSpecialAction.Step != SpecialActionPendingSteps.AwaitMoveEvent)
            {
                GameCommand specialActionCommand;
                string failureReason;
                if (!TryCreateSpecialActionResolutionCommand(
                        state,
                        pendingSpecialAction,
                        result.SubmittedCommands,
                        out specialActionCommand,
                        out failureReason))
                {
                    result.FailureReason = failureReason;
                    return false;
                }

                return SubmitSpecialActionResolution(
                    dispatcher,
                    state,
                    result,
                    specialActionCommand,
                    pendingSpecialAction);
            }

            var facilityPending = state.PendingCardSession;
            if (facilityPending != null &&
                facilityPending.IsValid() &&
                facilityPending.ScenarioId == FacilityPendingChoiceTypes.ScenarioId)
            {
                GameCommand facilityCommand;
                string failureReason;
                if (!TryCreateFacilityResolutionCommand(
                        state,
                        facilityPending,
                        result.SubmittedCommands,
                        out facilityCommand,
                        out failureReason))
                {
                    result.FailureReason = failureReason;
                    return false;
                }

                return SubmitCommand(dispatcher, facilityCommand, result);
            }

            var pendingChoice = CardFlowStateAdapter.GetPendingChoiceView(state);
            if (pendingChoice != null &&
                pendingChoice.OptionIds.Count > 0 &&
                (pendingChoice.ChoiceType == ExploreLocationCommandHandler.ExploreEventChoiceType ||
                 pendingChoice.ChoiceType == MoveCityCommandHandler.MoveCityEventChoiceType))
            {
                var optionId = pendingChoice.OptionIds[0];
                var command = CreateCommand(
                    "autoplay-resolve-card-r" + state.Round + "-p" + pendingChoice.PlayerId + "-" + result.SubmittedCommands,
                    GameCommandKind.ResolvePendingChoice,
                    pendingChoice.PlayerId,
                    pendingChoice.TargetId);
                command.OptionIds.Add(optionId);
                return pendingSpecialAction != null && pendingSpecialAction.IsValid()
                    ? SubmitSpecialActionResolution(
                        dispatcher,
                        state,
                        result,
                        command,
                        pendingSpecialAction)
                    : SubmitCommand(dispatcher, command, result);
            }

            result.FailureReason = "自动跑局遇到未处理选择：" +
                                   (pendingChoice == null ? "未知待选会话" : pendingChoice.ChoiceType);
            return false;
        }

        private static bool TryCreateSpecialActionResolutionCommand(
            GameState state,
            PendingSpecialActionState pending,
            int commandSequence,
            out GameCommand command,
            out string failureReason)
        {
            command = CreateCommand(
                "autoplay-resolve-special-r" + state.Round + "-a" + state.ActionRound +
                "-p" + pending.PlayerId + "-" + commandSequence,
                GameCommandKind.ResolvePendingChoice,
                pending.PlayerId,
                string.Empty);
            command.SourceId = pending.SessionId;
            command.Parameters[UseSpecialActionCommandHandler.SessionIdParameter] = pending.SessionId;
            failureReason = string.Empty;

            var player = state.FindPlayer(pending.PlayerId);
            if (player == null)
            {
                failureReason = "特殊行动待选玩家不存在：" + pending.PlayerId;
                return false;
            }

            var optionQuery = CreateAutoplaySpecialActionOptionQuery(state);
            switch (pending.Step)
            {
                case SpecialActionPendingSteps.AwaitMilitaryTargets:
                    var requiredCount = optionQuery.GetRequiredMilitaryPlacementCount(state, pending.PlayerId);
                    var legalInfluenceSlots = optionQuery.GetLegalInfluencePlacementSlotIds(state, pending.PlayerId);
                    if (requiredCount <= 0 || legalInfluenceSlots.Count < requiredCount)
                    {
                        failureReason = "军工化区域没有足够的合法影响力槽位。";
                        return false;
                    }

                    for (var i = 0; i < requiredCount; i++)
                    {
                        command.OptionIds.Add(legalInfluenceSlots[i]);
                    }

                    command.Parameters[UseSpecialActionCommandHandler.InfluenceSlotIdsParameter] =
                        string.Join(",", command.OptionIds.ToArray());
                    return true;

                case SpecialActionPendingSteps.AwaitMobilizationTarget:
                    var replaceableSlots = optionQuery.GetReplaceableInfluenceSlotIds(state, pending.PlayerId);
                    if (replaceableSlots.Count <= 0)
                    {
                        failureReason = "动员配套体系没有可替换的对手影响力。";
                        return false;
                    }

                    command.TargetId = replaceableSlots[0];
                    command.OptionIds.Add(replaceableSlots[0]);
                    command.Parameters[UseSpecialActionCommandHandler.TargetInfluenceSlotIdParameter] = replaceableSlots[0];
                    return true;

                case SpecialActionPendingSteps.AwaitFreeMoveTarget:
                    var moveTargets = optionQuery.GetLegalFreeMoveTargetIds(state, pending.PlayerId);
                    if (moveTargets.Count > 0)
                    {
                        command.TargetId = moveTargets[0];
                        command.OptionIds.Add(moveTargets[0]);
                        command.Parameters[UseSpecialActionCommandHandler.TargetLocationIdParameter] = moveTargets[0];
                    }

                    return true;

                case SpecialActionPendingSteps.AwaitRouteInfluence:
                    var routeSlots = optionQuery.GetLegalRouteInfluenceSlotIds(
                        state,
                        pending.PlayerId,
                        pending.TraversedRouteId);
                    if (routeSlots.Count > 0)
                    {
                        command.TargetId = routeSlots[0];
                        command.OptionIds.Add(routeSlots[0]);
                        command.Parameters[UseSpecialActionCommandHandler.RouteInfluenceSlotIdParameter] = routeSlots[0];
                    }

                    return true;

                default:
                    failureReason = "自动跑局不支持特殊行动待选阶段：" + pending.Step;
                    return false;
            }
        }

        private static bool SubmitSpecialActionResolution(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            LocalhostAutoplayResult result,
            GameCommand command,
            PendingSpecialActionState pending)
        {
            command.Parameters[UseSpecialActionCommandHandler.SessionIdParameter] = pending.SessionId;
            var specialActionId = pending.SpecialActionId;
            var declarationMarkerId = pending.DeclarationMarkerId;
            var sourceCommandId = pending.SourceCommandId;
            var previousStep = pending.Step;
            if (!SubmitCommand(dispatcher, command, result))
            {
                return false;
            }

            result.SpecialActionPendingSteps.Add(
                "P" + pending.PlayerId +
                " action=" + specialActionId +
                " step=" + previousStep +
                " next=" + FormatPendingSpecialActionStep(state.PendingSpecialAction));
            if (state.PendingSpecialAction == null || !state.PendingSpecialAction.IsValid())
            {
                RecordCompletedSpecialAction(
                    state,
                    result,
                    pending.PlayerId,
                    specialActionId,
                    declarationMarkerId,
                    sourceCommandId,
                    command.CommandId);
            }

            return true;
        }

        private static bool TryCreateFacilityResolutionCommand(
            GameState state,
            PendingCardSessionState pending,
            int commandSequence,
            out GameCommand command,
            out string failureReason)
        {
            command = CreateCommand(
                "autoplay-resolve-facility-r" + state.Round + "-a" + state.ActionRound +
                "-p" + pending.PlayerId + "-" + commandSequence,
                GameCommandKind.ResolvePendingChoice,
                pending.PlayerId,
                pending.CardId);
            command.Parameters[ResolveFacilityEffectCommandHandler.PendingSessionIdParameter] = pending.SessionId;
            failureReason = string.Empty;

            var player = state.FindPlayer(pending.PlayerId);
            if (player == null)
            {
                failureReason = "设施入场效果的玩家不存在：" + pending.PlayerId;
                return false;
            }

            switch (pending.ChoiceType)
            {
                case FacilityPendingChoiceTypes.CopyAdjacentEntryEffect:
                    return TrySetFirstFacilityOption(command, pending, out failureReason);
                case FacilityPendingChoiceTypes.BuildAdditionalFacility:
                    return TryConfigureAdditionalFacilityBuild(state, player, command, pending, out failureReason);
                case FacilityPendingChoiceTypes.BuildExtensionHub:
                case FacilityPendingChoiceTypes.SellResources:
                    return TrySetFacilityOption(
                        command,
                        pending,
                        FacilityPendingChoiceTypes.SkipOption,
                        out failureReason);
                case FacilityPendingChoiceTypes.FreeCityMove:
                    SetFacilityOption(command, FacilityPendingChoiceTypes.ConfirmOption);
                    return TryConfigureFreeCityMove(state, player, command, out failureReason);
                case FacilityPendingChoiceTypes.ChooseFiveBasicResources:
                    SetFacilityOption(command, FacilityPendingChoiceTypes.ConfirmOption);
                    command.Parameters[ResolveFacilityEffectCommandHandler.OriginiumAmountParameter] = "5";
                    command.Parameters[ResolveFacilityEffectCommandHandler.OriginiumShardAmountParameter] = "0";
                    command.Parameters[ResolveFacilityEffectCommandHandler.IronAmountParameter] = "0";
                    return true;
                case FacilityPendingChoiceTypes.ReplaceOneInfluence:
                    return TryConfigureMercenaryCommand(
                        state,
                        player,
                        command,
                        pending,
                        out failureReason);
                case FacilityPendingChoiceTypes.DeployTwoInfluences:
                    SetFacilityOption(command, FacilityPendingChoiceTypes.ConfirmOption);
                    var deploySlots = FindLegalInfluencePlacementSlots(state, player, 2);
                    if (deploySlots.Count != 2)
                    {
                        failureReason = "护航调度中心无法同时部署 2 个影响力。";
                        return false;
                    }

                    command.Parameters[ResolveFacilityEffectCommandHandler.InfluenceSlotIdsParameter] =
                        string.Join(",", deploySlots.ToArray());
                    return true;
                case FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore:
                    return TryConfigureEquipmentWarehouse(state, player, command, pending, out failureReason);
                default:
                    failureReason = "自动跑局不支持设施待选类型：" + pending.ChoiceType;
                    return false;
            }
        }

        private static bool TryConfigureAdditionalFacilityBuild(
            GameState state,
            PlayerState player,
            GameCommand command,
            PendingCardSessionState pending,
            out string failureReason)
        {
            var slotIndex = BuildFacilityService.FindFirstEmptyCityBoardSlot(state, player.PlayerId);
            if (slotIndex < 0)
            {
                failureReason = "简陋工程营没有可用于额外建设的空槽位。";
                return false;
            }

            var buildService = new BuildFacilityService();
            for (var i = 0; i < pending.OptionIds.Count; i++)
            {
                var facilityId = pending.OptionIds[i];
                var paymentMode = ResolveEffectiveFacilityPaymentMode(state, player, facilityId);
                if (string.IsNullOrEmpty(paymentMode) ||
                    !buildService.Validate(state, player.PlayerId, facilityId, slotIndex, paymentMode).IsValid)
                {
                    continue;
                }

                SetFacilityOption(command, facilityId);
                command.Parameters[BuildFacilityCommandHandler.FacilityIdParameter] = facilityId;
                command.Parameters[BuildFacilityCommandHandler.CityBoardSlotIndexParameter] = slotIndex.ToString();
                command.Parameters[BuildFacilityCommandHandler.PaymentModeParameter] = paymentMode;
                failureReason = string.Empty;
                return true;
            }

            failureReason = "简陋工程营的待选供应牌均已不可支付或不可建造。";
            return false;
        }

        private static bool TryConfigureFreeCityMove(
            GameState state,
            PlayerState player,
            GameCommand command,
            out string failureReason)
        {
            var map = StaticMapDefinitions.Resolve(state.MapId);
            var mapQuery = new MapQueryService(map);
            var movementService = new CityMovementService(
                mapQuery,
                new InfluenceService(mapQuery),
                new TravelCostService(mapQuery),
                new EventDeckService(),
                new ResourceTokenService());

            IReadOnlyList<MapLocationDefinition> adjacentLocations;
            try
            {
                adjacentLocations = mapQuery.GetAdjacentLocations(player.CityLocationId);
            }
            catch
            {
                failureReason = "高性能动力设施无法读取当前城市的相邻地点。";
                return false;
            }

            for (var i = 0; i < adjacentLocations.Count; i++)
            {
                var targetLocationId = adjacentLocations[i].LocationId;
                if (HasResourceToken(state, targetLocationId))
                {
                    if (!movementService.CanMoveCityForFacility(state, player.PlayerId, targetLocationId).IsValid)
                    {
                        continue;
                    }

                    command.TargetId = targetLocationId;
                    command.Parameters[ResolveFacilityEffectCommandHandler.TargetLocationIdParameter] = targetLocationId;
                    failureReason = string.Empty;
                    return true;
                }

                var card = PeekEventCard(state, targetLocationId);
                if (card == null)
                {
                    continue;
                }

                for (var optionIndex = 0; optionIndex < card.ChoiceRewards.Count; optionIndex++)
                {
                    if (!movementService.CanMoveCityForFacility(
                            state,
                            player.PlayerId,
                            targetLocationId,
                            optionIndex).IsValid)
                    {
                        continue;
                    }

                    command.TargetId = targetLocationId;
                    command.Parameters[ResolveFacilityEffectCommandHandler.TargetLocationIdParameter] = targetLocationId;
                    command.Parameters[ExploreLocationCommandHandler.EventOptionIdParameter] = optionIndex.ToString();
                    failureReason = string.Empty;
                    return true;
                }
            }

            failureReason = "高性能动力设施没有合法的免费城市移动目标。";
            return false;
        }

        private static bool TryConfigureEquipmentWarehouse(
            GameState state,
            PlayerState player,
            GameCommand command,
            PendingCardSessionState pending,
            out string failureReason)
        {
            if (pending.OptionIds.Contains(FacilityPendingChoiceTypes.RemoveDispatchOption))
            {
                string sourceSlotId;
                string targetSlotId;
                var hasDispatch = TryFindAnyDispatchPlan(state, player, out sourceSlotId, out targetSlotId);
                var removeSlotId = FindInfluenceSlotToRemove(state, hasDispatch ? sourceSlotId : string.Empty);
                if (hasDispatch || !string.IsNullOrEmpty(removeSlotId))
                {
                    SetFacilityOption(command, FacilityPendingChoiceTypes.RemoveDispatchOption);
                    if (!string.IsNullOrEmpty(removeSlotId))
                    {
                        command.Parameters[ResolveFacilityEffectCommandHandler.RemoveInfluenceSlotIdParameter] = removeSlotId;
                    }

                    if (hasDispatch)
                    {
                        command.Parameters[ResolveFacilityEffectCommandHandler.SourceInfluenceSlotIdParameter] = sourceSlotId;
                        command.Parameters[ResolveFacilityEffectCommandHandler.TargetInfluenceSlotIdParameter] = targetSlotId;
                    }

                    failureReason = string.Empty;
                    return true;
                }
            }

            if (pending.OptionIds.Contains(FacilityPendingChoiceTypes.ExploreOption))
            {
                SetFacilityOption(command, FacilityPendingChoiceTypes.ExploreOption);
                if (TryConfigureFacilityExplore(state, player, command))
                {
                    failureReason = string.Empty;
                    return true;
                }
            }

            if (pending.OptionIds.Contains(FacilityPendingChoiceTypes.SkipOption))
            {
                SetFacilityOption(command, FacilityPendingChoiceTypes.SkipOption);
                failureReason = string.Empty;
                return true;
            }

            failureReason = "载具仓库既没有可执行的移除/调度，也没有合法探索目标。";
            return false;
        }

        private static bool TryConfigureMercenaryCommand(
            GameState state,
            PlayerState player,
            GameCommand command,
            PendingCardSessionState pending,
            out string failureReason)
        {
            if (pending.OptionIds.Contains(FacilityPendingChoiceTypes.ReplaceInfluenceOption))
            {
                for (var i = 0; i < state.Map.Influences.Count; i++)
                {
                    var influence = state.Map.Influences[i];
                    if (influence.PlayerId == player.PlayerId || string.IsNullOrEmpty(influence.SlotId))
                    {
                        continue;
                    }

                    SetFacilityOption(command, FacilityPendingChoiceTypes.ReplaceInfluenceOption);
                    command.Parameters[ResolveFacilityEffectCommandHandler.TargetInfluenceSlotIdParameter] =
                        influence.SlotId;
                    failureReason = string.Empty;
                    return true;
                }
            }

            if (pending.OptionIds.Contains(FacilityPendingChoiceTypes.DeployInfluenceOption))
            {
                var deploySlots = FindLegalInfluencePlacementSlots(state, player, 1);
                if (deploySlots.Count == 1)
                {
                    SetFacilityOption(command, FacilityPendingChoiceTypes.DeployInfluenceOption);
                    command.Parameters[ResolveFacilityEffectCommandHandler.TargetInfluenceSlotIdParameter] =
                        deploySlots[0];
                    failureReason = string.Empty;
                    return true;
                }
            }

            if (pending.OptionIds.Contains(FacilityPendingChoiceTypes.SkipOption))
            {
                SetFacilityOption(command, FacilityPendingChoiceTypes.SkipOption);
                failureReason = string.Empty;
                return true;
            }

            failureReason = "佣兵指挥部既没有可替换目标，也没有合法放置槽位。";
            return false;
        }

        private static bool TryConfigureFacilityExplore(
            GameState state,
            PlayerState player,
            GameCommand command)
        {
            var map = StaticMapDefinitions.Resolve(state.MapId);
            var mapQuery = new MapQueryService(map);
            var explorationService = new ExplorationService(
                mapQuery,
                new InfluenceService(mapQuery),
                new EventDeckService(),
                new ResourceTokenService());
            var paymentRecipients = new Dictionary<string, int>();

            for (var i = 0; i < map.Locations.Count; i++)
            {
                var targetLocationId = map.Locations[i].LocationId;
                if (HasResourceToken(state, targetLocationId))
                {
                    continue;
                }

                IReadOnlyList<MapPath> paths;
                try
                {
                    paths = explorationService.FindDefaultPathChoices(state, player.PlayerId, targetLocationId);
                }
                catch
                {
                    continue;
                }

                var card = PeekEventCard(state, targetLocationId);
                if (card == null || card.ChoiceRewards.Count == 0)
                {
                    continue;
                }

                for (var pathIndex = 0; pathIndex < paths.Count; pathIndex++)
                {
                    var validation = explorationService.CanExplore(
                        state,
                        player.PlayerId,
                        targetLocationId,
                        paths[pathIndex],
                        0,
                        string.Empty,
                        paymentRecipients,
                        null,
                        true,
                        true);
                    if (!validation.IsValid)
                    {
                        continue;
                    }

                    command.TargetId = targetLocationId;
                    command.Parameters[ResolveFacilityEffectCommandHandler.TargetLocationIdParameter] = targetLocationId;
                    command.Parameters[ExploreLocationCommandHandler.PathLocationIdsParameter] =
                        string.Join(",", paths[pathIndex].LocationIds.ToArray());
                    command.Parameters[ExploreLocationCommandHandler.RouteIdsParameter] =
                        string.Join(",", paths[pathIndex].RouteIds.ToArray());
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindAnyDispatchPlan(
            GameState state,
            PlayerState player,
            out string sourceSlotId,
            out string targetSlotId)
        {
            sourceSlotId = string.Empty;
            targetSlotId = string.Empty;
            var map = StaticMapDefinitions.Resolve(state.MapId);
            var influenceService = new InfluenceService(new MapQueryService(map));
            var candidateSlots = GetAllInfluenceSlotIds(map);

            for (var influenceIndex = 0; influenceIndex < state.Map.Influences.Count; influenceIndex++)
            {
                var influence = state.Map.Influences[influenceIndex];
                if (influence.PlayerId != player.PlayerId || string.IsNullOrEmpty(influence.SlotId))
                {
                    continue;
                }

                for (var slotIndex = 0; slotIndex < candidateSlots.Count; slotIndex++)
                {
                    if (!influenceService.CanMove(
                            state,
                            player.PlayerId,
                            influence.SlotId,
                            candidateSlots[slotIndex]).IsValid)
                    {
                        continue;
                    }

                    sourceSlotId = influence.SlotId;
                    targetSlotId = candidateSlots[slotIndex];
                    return true;
                }
            }

            return false;
        }

        private static string FindInfluenceSlotToRemove(GameState state, string excludedSlotId)
        {
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var slotId = state.Map.Influences[i].SlotId;
                if (!string.IsNullOrEmpty(slotId) && slotId != excludedSlotId)
                {
                    return slotId;
                }
            }

            return string.Empty;
        }

        private static List<string> FindLegalInfluencePlacementSlots(
            GameState state,
            PlayerState player,
            int requestedCount)
        {
            var result = new List<string>();
            var remaining = player.InfluenceSupply < requestedCount ? player.InfluenceSupply : requestedCount;
            if (remaining <= 0)
            {
                return result;
            }

            var map = StaticMapDefinitions.Resolve(state.MapId);
            var influenceService = new InfluenceService(new MapQueryService(map));
            var candidateSlots = GetAllInfluenceSlotIds(map);
            for (var i = 0; i < candidateSlots.Count && result.Count < remaining; i++)
            {
                if (influenceService.CanPlace(state, player.PlayerId, candidateSlots[i]).IsValid)
                {
                    result.Add(candidateSlots[i]);
                }
            }

            return result;
        }

        private static List<string> GetAllInfluenceSlotIds(GameMapDefinition map)
        {
            var result = new List<string>();
            for (var locationIndex = 0; locationIndex < map.Locations.Count; locationIndex++)
            {
                var location = map.Locations[locationIndex];
                for (var slotIndex = 0; slotIndex < location.InfluenceSlotCount; slotIndex++)
                {
                    result.Add(InfluenceService.GetLocationSlotId(location.LocationId, slotIndex));
                }
            }

            for (var routeIndex = 0; routeIndex < map.Routes.Count; routeIndex++)
            {
                var route = map.Routes[routeIndex];
                for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    result.Add(InfluenceService.GetRouteSlotId(route.RouteId, slotIndex));
                }
            }

            return result;
        }

        private static bool TrySetFirstFacilityOption(
            GameCommand command,
            PendingCardSessionState pending,
            out string failureReason)
        {
            if (pending.OptionIds.Count == 0)
            {
                failureReason = "设施待选会话没有可用选项：" + pending.ChoiceType;
                return false;
            }

            SetFacilityOption(command, pending.OptionIds[0]);
            failureReason = string.Empty;
            return true;
        }

        private static bool TrySetFacilityOption(
            GameCommand command,
            PendingCardSessionState pending,
            string optionId,
            out string failureReason)
        {
            if (!pending.OptionIds.Contains(optionId))
            {
                failureReason = "设施待选会话缺少预期选项：" + optionId;
                return false;
            }

            SetFacilityOption(command, optionId);
            failureReason = string.Empty;
            return true;
        }

        private static void SetFacilityOption(GameCommand command, string optionId)
        {
            command.OptionIds.Clear();
            command.OptionIds.Add(optionId);
            command.Parameters[ResolveFacilityEffectCommandHandler.OptionIdParameter] = optionId;
        }

        private static bool TrySubmitAutoplayCharacterCard(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            PlayerState player,
            LocalhostAutoplayResult result)
        {
            if (result.CharacterCardUseSuccesses > 0 ||
                player.PlayerId != AutoplayCharacterPlayerId ||
                player.UsedCharacterThisRound ||
                string.IsNullOrEmpty(player.CoveredCharacterCardId))
            {
                return true;
            }

            var definition = CharacterCardDatabase.Get(player.CoveredCharacterCardId);
            if (definition == null || definition.TemplateId != AutoplayCharacterTemplateId)
            {
                return true;
            }

            var cardId = player.CoveredCharacterCardId;
            var resourceId = ResolveMinimumBasicResourceId(player.Resources);
            var beforeAmount = GetBasicResourceAmount(player.Resources, resourceId);
            var command = CreateCommand(
                "autoplay-use-character-r" + state.Round + "-a" + state.ActionRound +
                "-p" + player.PlayerId + "-" + result.CharacterCardUseAttempts,
                GameCommandKind.UseCharacterCard,
                player.PlayerId,
                cardId);
            command.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            command.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] =
                CharacterEffectModes.Strategy;
            command.Parameters[CharacterEffectParameterKeys.ResourceType] = resourceId;

            result.CharacterCardUseAttempts++;
            if (!SubmitCommand(dispatcher, command, result))
            {
                return false;
            }

            if (!player.UsedCharacterThisRound ||
                !string.IsNullOrEmpty(player.CoveredCharacterCardId) ||
                !player.DiscardCardIds.Contains(cardId))
            {
                result.FailureReason = "角色卡命令已接受，但角色卡没有从盖放区进入弃牌区。";
                return false;
            }

            result.CharacterCardUseSuccesses++;
            result.CharacterCardExecutions.Add(
                "P" + player.PlayerId +
                " card=" + cardId +
                " mode=" + CharacterEffectModes.Strategy +
                " resource=" + resourceId +
                " before=" + beforeAmount +
                " after=" + GetBasicResourceAmount(player.Resources, resourceId) +
                " discardCount=" + player.DiscardCardIds.Count);
            return true;
        }

        private static string ResolveMinimumBasicResourceId(ResourceSet resources)
        {
            var resourceId = "originium";
            var minimum = resources.Originium;
            if (resources.OriginiumShard < minimum)
            {
                resourceId = "originium-shard";
                minimum = resources.OriginiumShard;
            }

            if (resources.Iron < minimum)
            {
                resourceId = "iron";
            }

            return resourceId;
        }

        private static int GetBasicResourceAmount(ResourceSet resources, string resourceId)
        {
            switch (resourceId)
            {
                case "originium-shard":
                    return resources.OriginiumShard;
                case "iron":
                    return resources.Iron;
                default:
                    return resources.Originium;
            }
        }

        private static bool TrySubmitAutoplayMainAction(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            PlayerState player,
            LocalhostAutoplayResult result)
        {
            if (TrySubmitAutoplaySpecialAction(dispatcher, state, player, result))
            {
                return true;
            }

            // 先完成三人 I 级建设者的 0/1 槽，避免旅行支出抢占建设预算。
            var prioritizeLevelOneBuild = state.Players.Count == 3 &&
                player.PlayerId == ResolveFormalSupplyBuildPlayerId(state) &&
                (!player.BuiltFacilityIds.Contains(FormalSupplyBlueFacilityId) ||
                 !player.BuiltFacilityIds.Contains(FormalSupplyRedFacilityId));

            if (!prioritizeLevelOneBuild && result.ExploreLocationSuccesses < RequiredExploreSuccesses &&
                TrySubmitAutoplayExplore(dispatcher, state, player, result))
            {
                return true;
            }

            if (!prioritizeLevelOneBuild && result.MoveCitySuccesses < RequiredMoveCitySuccesses &&
                TrySubmitAutoplayMoveCity(dispatcher, state, player, result))
            {
                return true;
            }

            if (!prioritizeLevelOneBuild && result.DispatchInfluenceSuccesses < RequiredDispatchInfluenceSuccesses &&
                TrySubmitAutoplayDispatchInfluence(dispatcher, state, player, result))
            {
                return true;
            }

            var facilityId = FindAffordableFacility(state, player);
            if (string.IsNullOrEmpty(facilityId))
            {
                return TrySubmitAutoplayDeployInfluence(dispatcher, state, player, result);
            }

            var slotIndex = ResolveAutoplayFacilitySlot(state, player, facilityId);
            if (slotIndex < 0)
            {
                return false;
            }

            var command = CreateCommand(
                "autoplay-build-r" + state.Round + "-a" + state.ActionRound + "-p" + player.PlayerId + "-" + result.BuildFacilityAttempts,
                GameCommandKind.BuildFacility,
                player.PlayerId,
                facilityId);
            command.Parameters[BuildFacilityCommandHandler.FacilityIdParameter] = facilityId;
            command.Parameters[BuildFacilityCommandHandler.CityBoardSlotIndexParameter] = slotIndex.ToString();
            command.Parameters[BuildFacilityCommandHandler.PaymentModeParameter] = ResolveFacilityPaymentMode(player, facilityId);

            result.BuildFacilityAttempts++;
            var beforeBuiltCount = player.BuiltFacilityIds.Count;
            if (!SubmitCommand(dispatcher, command, result))
            {
                return false;
            }

            if (player.BuiltFacilityIds.Count > beforeBuiltCount)
            {
                result.BuildFacilitySuccesses++;
                if (IsFormalSupplyEvidenceFacility(state, player.PlayerId, facilityId))
                {
                    result.FormalSupplyBuilds.Add(
                        "P" + player.PlayerId + " facility=" + facilityId + " slot=" + slotIndex);
                }
            }

            return true;
        }

        private static bool TrySubmitAutoplayCityStyle(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            PlayerState player,
            LocalhostAutoplayResult result)
        {
            var cityStyleId = ResolveAutoplayCityStyleId(state, player.PlayerId);
            if (string.IsNullOrEmpty(cityStyleId) ||
                player.DeclaredCityStyles.Exists(declaration =>
                    declaration != null && declaration.CityStyleId == cityStyleId))
            {
                return true;
            }

            var cityStyle = CityStyleDatabase.Get(cityStyleId);
            var match = new CityStylePatternMatcher().Match(state, player.PlayerId, cityStyle);
            if (!match.Succeeded)
            {
                return true;
            }

            var selectedSlotIndexes = new List<int>(match.UsedCityBoardSlotIndexes);
            selectedSlotIndexes.Sort();
            var validation = new DeclareCityStyleService().Validate(
                state,
                player.PlayerId,
                cityStyleId,
                selectedSlotIndexes);
            if (!validation.IsValid)
            {
                return true;
            }

            var command = CreateCommand(
                "autoplay-declare-city-style-r" + state.Round + "-a" + state.ActionRound + "-p" + player.PlayerId,
                GameCommandKind.DeclareCityStyle,
                player.PlayerId,
                cityStyleId);
            command.Parameters[DeclareCityStyleCommandHandler.CityStyleIdParameter] = cityStyleId;
            command.Parameters[DeclareCityStyleCommandHandler.UsedCityBoardSlotIndexesParameter] =
                string.Join(",", selectedSlotIndexes.ToArray());

            result.DeclareCityStyleAttempts++;
            var beforeCount = player.DeclaredCityStyles.Count;
            if (!SubmitCommand(dispatcher, command, result))
            {
                return false;
            }

            if (player.DeclaredCityStyles.Count > beforeCount)
            {
                result.DeclareCityStyleSuccesses++;
                var declaration = player.DeclaredCityStyles[player.DeclaredCityStyles.Count - 1];
                result.CityStyleDeclarations.Add(
                    "P" + player.PlayerId +
                    " cityStyle=" + declaration.CityStyleId +
                    " score=" + CityStyleDatabase.Get(declaration.CityStyleId).Score +
                    " slots=" + string.Join(",", declaration.UsedCityBoardSlotIndexes.ToArray()));
            }

            return true;
        }

        private static bool TrySubmitAutoplaySpecialAction(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            PlayerState player,
            LocalhostAutoplayResult result)
        {
            var specialActionId = ResolveAutoplaySpecialActionId(state, player.PlayerId);
            if (string.IsNullOrEmpty(specialActionId) ||
                HasCompletedSpecialAction(result, specialActionId))
            {
                return false;
            }

            CityStyleDeclarationState declaration = null;
            for (var i = 0; i < player.DeclaredCityStyles.Count; i++)
            {
                var candidate = player.DeclaredCityStyles[i];
                if (candidate != null && candidate.UnlockedSpecialActionId == specialActionId)
                {
                    declaration = candidate;
                    break;
                }
            }

            if (declaration == null)
            {
                return false;
            }

            var option = CreateAutoplaySpecialActionOptionQuery(state)
                .Query(state, player.PlayerId)
                .Find(specialActionId, declaration.InfluenceMarkerId);
            if (option == null || !option.CanUse)
            {
                return false;
            }

            var command = CreateCommand(
                "autoplay-use-special-r" + state.Round + "-a" + state.ActionRound +
                "-p" + player.PlayerId + "-" + result.SpecialActionAttempts,
                GameCommandKind.UseSpecialAction,
                player.PlayerId,
                specialActionId);
            command.SourceId = declaration.InfluenceMarkerId;
            command.Parameters[UseSpecialActionCommandHandler.SpecialActionIdParameter] = specialActionId;
            command.Parameters[UseSpecialActionCommandHandler.DeclarationMarkerIdParameter] =
                declaration.InfluenceMarkerId;
            if (specialActionId == SpecialActionDatabase.CompositePowerSystem)
            {
                if (option.PaymentOptions.Count == 0)
                {
                    return false;
                }

                command.Parameters[UseSpecialActionCommandHandler.OriginiumAmountParameter] =
                    option.PaymentOptions[0].Originium.ToString();
                command.Parameters[UseSpecialActionCommandHandler.IronAmountParameter] =
                    option.PaymentOptions[0].Iron.ToString();
            }

            result.SpecialActionAttempts++;
            if (!SubmitCommand(dispatcher, command, result))
            {
                return false;
            }

            var pending = state.PendingSpecialAction;
            if (pending != null && pending.IsValid())
            {
                result.SpecialActionPendingSteps.Add(
                    "P" + player.PlayerId +
                    " action=" + specialActionId +
                    " step=" + pending.Step +
                    " remainingMainActions=" + player.RemainingMainActionsThisTurn);
            }
            else
            {
                RecordCompletedSpecialAction(
                    state,
                    result,
                    player.PlayerId,
                    specialActionId,
                    declaration.InfluenceMarkerId,
                    command.CommandId,
                    command.CommandId);
            }

            return true;
        }

        private static SpecialActionOptionQueryService CreateAutoplaySpecialActionOptionQuery(GameState state)
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId));
            var influenceService = new InfluenceService(mapQuery);
            var movementService = new CityMovementService(
                mapQuery,
                influenceService,
                new TravelCostService(mapQuery),
                new EventDeckService(),
                new ResourceTokenService());
            return new SpecialActionOptionQueryService(
                mapQuery,
                influenceService,
                movementService,
                new SpecialActionLifecycleService(),
                new MainActionBudgetService());
        }

        private static void RecordCompletedSpecialAction(
            GameState state,
            LocalhostAutoplayResult result,
            int playerId,
            string specialActionId,
            string declarationMarkerId,
            string sourceCommandId,
            string completionCommandId)
        {
            if (HasCompletedSpecialAction(result, specialActionId))
            {
                return;
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return;
            }

            CityStyleDeclarationState declaration = null;
            for (var i = 0; i < player.DeclaredCityStyles.Count; i++)
            {
                var candidate = player.DeclaredCityStyles[i];
                if (candidate != null && candidate.InfluenceMarkerId == declarationMarkerId)
                {
                    declaration = candidate;
                    break;
                }
            }

            result.SpecialActionSuccesses++;
            result.SpecialActionExecutions.Add(
                "P" + playerId +
                " action=" + specialActionId +
                " marker=" + declarationMarkerId +
                " area=" + (declaration == null ? "unknown" : declaration.MarkerArea) +
                " remainingUses=" + (declaration == null ? -1 : declaration.RemainingSpecialActionUses) +
                " remainingMainActions=" + player.RemainingMainActionsThisTurn +
                " pendingStep=" + FormatPendingSpecialActionStep(state.PendingSpecialAction) +
                " beginCommand=" + sourceCommandId +
                " completionCommand=" + completionCommandId);
        }

        private static bool HasCompletedSpecialAction(LocalhostAutoplayResult result, string specialActionId)
        {
            for (var i = 0; i < result.SpecialActionExecutions.Count; i++)
            {
                if (result.SpecialActionExecutions[i].Contains("action=" + specialActionId + " "))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasCompletedRequiredActionCoverage(LocalhostAutoplayResult result)
        {
            return result.DeployInfluenceSuccesses >= 1 &&
                   result.ExploreLocationSuccesses >= 1 &&
                   result.BuildFacilitySuccesses >= 1 &&
                   result.CharacterCardUseSuccesses >= 1 &&
                   result.MoveCitySuccesses >= 1 &&
                   result.DeclareCityStyleSuccesses >= 1 &&
                   result.SpecialActionSuccesses >= 1;
        }

        private static int ResolveFormalSupplyBuildPlayerId(GameState state)
        {
            return state.Players.Count == 3 ? 2 : 4;
        }

        private static string ResolveAutoplayCityStyleId(GameState state, int playerId)
        {
            if (playerId == ResolveFormalSupplyBuildPlayerId(state))
            {
                return LevelOneAutoplayCityStyleId;
            }

            return playerId == LevelTwoFixturePlayerId ? LevelTwoAutoplayCityStyleId : string.Empty;
        }

        private static string ResolveAutoplaySpecialActionId(GameState state, int playerId)
        {
            if (playerId == ResolveFormalSupplyBuildPlayerId(state))
            {
                return LevelOneAutoplaySpecialActionId;
            }

            return playerId == LevelTwoFixturePlayerId ? LevelTwoAutoplaySpecialActionId : string.Empty;
        }

        private static int ResolveAutoplayFacilitySlot(GameState state, PlayerState player, string facilityId)
        {
            if (player.PlayerId == ResolveFormalSupplyBuildPlayerId(state))
            {
                if (facilityId == FormalSupplyBlueFacilityId)
                {
                    return 0;
                }

                if (facilityId == FormalSupplyRedFacilityId)
                {
                    return 1;
                }
            }

            return BuildFacilityService.FindFirstEmptyCityBoardSlot(state, player.PlayerId);
        }

        private static bool TrySubmitAutoplayExplore(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            PlayerState player,
            LocalhostAutoplayResult result)
        {
            var map = StaticMapDefinitions.Resolve(state.MapId);
            var mapQuery = new MapQueryService(map);
            var explorationService = new ExplorationService(
                mapQuery,
                new InfluenceService(mapQuery),
                new EventDeckService(),
                new ResourceTokenService());

            for (var i = 0; i < map.Locations.Count; i++)
            {
                var targetLocationId = map.Locations[i].LocationId;
                if (HasResourceToken(state, targetLocationId))
                {
                    continue;
                }

                MapPath path;
                int optionIndex;
                if (!TryFindExplorePlan(state, player, targetLocationId, explorationService, out path, out optionIndex))
                {
                    continue;
                }

                var command = CreateCommand(
                    "autoplay-explore-r" + state.Round + "-a" + state.ActionRound + "-p" + player.PlayerId + "-" + result.ExploreLocationAttempts,
                    GameCommandKind.ExploreLocation,
                    player.PlayerId,
                    targetLocationId);
                command.OptionIds.Add(optionIndex.ToString());
                command.Parameters[ExploreLocationCommandHandler.EventOptionIdParameter] = optionIndex.ToString();
                command.Parameters[ExploreLocationCommandHandler.PathLocationIdsParameter] =
                    string.Join(",", path.LocationIds.ToArray());
                command.Parameters[ExploreLocationCommandHandler.RouteIdsParameter] =
                    string.Join(",", path.RouteIds.ToArray());

                result.ExploreLocationAttempts++;
                var beforeResourceTokenCount = state.Map.ResourceTokens.Count;
                if (!SubmitCommand(dispatcher, command, result))
                {
                    return false;
                }

                if (state.Map.ResourceTokens.Count > beforeResourceTokenCount)
                {
                    result.ExploreLocationSuccesses++;
                    result.ExploredLocationIds.Add(targetLocationId);
                }

                return true;
            }

            return false;
        }

        private static bool TryFindExplorePlan(
            GameState state,
            PlayerState player,
            string targetLocationId,
            ExplorationService explorationService,
            out MapPath path,
            out int optionIndex)
        {
            path = null;
            optionIndex = -1;

            IReadOnlyList<MapPath> choices;
            try
            {
                choices = explorationService.FindDefaultPathChoices(state, player.PlayerId, targetLocationId);
            }
            catch
            {
                return false;
            }

            var card = PeekEventCard(state, targetLocationId);
            if (card == null)
            {
                return false;
            }

            var paymentRecipients = new Dictionary<string, int>();
            var bestRewardScore = int.MinValue;
            for (var pathIndex = 0; pathIndex < choices.Count; pathIndex++)
            {
                for (var choiceIndex = 0; choiceIndex < card.ChoiceRewards.Count; choiceIndex++)
                {
                    var validation = explorationService.CanExplore(
                        state,
                        player.PlayerId,
                        targetLocationId,
                        choices[pathIndex],
                        choiceIndex,
                        string.Empty,
                        paymentRecipients);
                    if (!validation.IsValid)
                    {
                        continue;
                    }

                    var rewardScore = ScoreRewardForAutoplay(card.ChoiceRewards[choiceIndex]);
                    if (path != null && rewardScore <= bestRewardScore)
                    {
                        continue;
                    }

                    path = choices[pathIndex];
                    optionIndex = choiceIndex;
                    bestRewardScore = rewardScore;
                }
            }

            return path != null && optionIndex >= 0;
        }

        private static bool TrySubmitAutoplayMoveCity(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            PlayerState player,
            LocalhostAutoplayResult result)
        {
            var map = StaticMapDefinitions.Resolve(state.MapId);
            var mapQuery = new MapQueryService(map);
            var movementService = new CityMovementService(
                mapQuery,
                new InfluenceService(mapQuery),
                new TravelCostService(mapQuery),
                new EventDeckService(),
                new ResourceTokenService());

            IReadOnlyList<MapLocationDefinition> adjacentLocations;
            try
            {
                adjacentLocations = mapQuery.GetAdjacentLocations(player.CityLocationId);
            }
            catch
            {
                return false;
            }

            for (var i = 0; i < adjacentLocations.Count; i++)
            {
                var targetLocationId = adjacentLocations[i].LocationId;
                int optionIndex;
                if (!TryFindMoveCityOption(state, player, targetLocationId, movementService, out optionIndex))
                {
                    continue;
                }

                var command = CreateCommand(
                    "autoplay-move-city-r" + state.Round + "-a" + state.ActionRound + "-p" + player.PlayerId + "-" + result.MoveCityAttempts,
                    GameCommandKind.MoveCity,
                    player.PlayerId,
                    targetLocationId);
                if (optionIndex >= 0)
                {
                    command.OptionIds.Add(optionIndex.ToString());
                    command.Parameters[MoveCityCommandHandler.EventOptionIdParameter] = optionIndex.ToString();
                }

                result.MoveCityAttempts++;
                var sourceLocationId = player.CityLocationId;
                if (!SubmitCommand(dispatcher, command, result))
                {
                    return false;
                }

                if (player.CityLocationId == targetLocationId)
                {
                    result.MoveCitySuccesses++;
                    var route = mapQuery.FindRoute(sourceLocationId, targetLocationId);
                    result.MoveCityRoutes.Add(sourceLocationId + "->" + targetLocationId + "(" + route.RouteId + ")");
                }

                return true;
            }

            return false;
        }

        private static bool TryFindMoveCityOption(
            GameState state,
            PlayerState player,
            string targetLocationId,
            CityMovementService movementService,
            out int optionIndex)
        {
            optionIndex = -1;
            if (HasResourceToken(state, targetLocationId))
            {
                return movementService.CanMoveCity(state, player.PlayerId, targetLocationId).IsValid;
            }

            var card = PeekEventCard(state, targetLocationId);
            if (card == null)
            {
                return false;
            }

            var bestRewardScore = int.MinValue;
            for (var choiceIndex = 0; choiceIndex < card.ChoiceRewards.Count; choiceIndex++)
            {
                var validation = movementService.CanMoveCity(state, player.PlayerId, targetLocationId, choiceIndex);
                if (!validation.IsValid)
                {
                    continue;
                }

                var rewardScore = ScoreRewardForAutoplay(card.ChoiceRewards[choiceIndex]);
                if (optionIndex >= 0 && rewardScore <= bestRewardScore)
                {
                    continue;
                }

                optionIndex = choiceIndex;
                bestRewardScore = rewardScore;
            }

            return optionIndex >= 0;
        }

        private static bool TrySubmitAutoplayDispatchInfluence(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            PlayerState player,
            LocalhostAutoplayResult result)
        {
            var map = StaticMapDefinitions.Resolve(state.MapId);
            var mapQuery = new MapQueryService(map);
            var influenceService = new InfluenceService(mapQuery);

            string sourceSlotId;
            string targetSlotId;
            string routeId;
            if (!TryFindDispatchPlan(state, player, map, influenceService, out sourceSlotId, out targetSlotId, out routeId))
            {
                return false;
            }

            var command = CreateCommand(
                "autoplay-dispatch-r" + state.Round + "-a" + state.ActionRound + "-p" + player.PlayerId + "-" + result.DispatchInfluenceAttempts,
                GameCommandKind.DispatchInfluence,
                player.PlayerId,
                targetSlotId);
            command.SourceId = sourceSlotId;
            command.TargetId = targetSlotId;

            result.DispatchInfluenceAttempts++;
            if (!SubmitCommand(dispatcher, command, result))
            {
                return false;
            }

            result.DispatchInfluenceSuccesses++;
            result.DispatchInfluenceRoutes.Add(sourceSlotId + "->" + targetSlotId + "(" + routeId + ")");
            return true;
        }

        private static bool TryFindDispatchPlan(
            GameState state,
            PlayerState player,
            GameMapDefinition map,
            InfluenceService influenceService,
            out string sourceSlotId,
            out string targetSlotId,
            out string routeId)
        {
            sourceSlotId = string.Empty;
            targetSlotId = string.Empty;
            routeId = string.Empty;

            for (var influenceIndex = 0; influenceIndex < state.Map.Influences.Count; influenceIndex++)
            {
                var influence = state.Map.Influences[influenceIndex];
                if (influence.PlayerId != player.PlayerId || string.IsNullOrEmpty(influence.SlotId))
                {
                    continue;
                }

                for (var routeIndex = 0; routeIndex < map.Routes.Count; routeIndex++)
                {
                    var route = map.Routes[routeIndex];
                    if (!CanRouteSupportOpponentRecipientCollection(state, player.PlayerId, route.RouteId))
                    {
                        continue;
                    }

                    for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                    {
                        var candidateTarget = InfluenceService.GetRouteSlotId(route.RouteId, slotIndex);
                        var validation = influenceService.CanMove(state, player.PlayerId, influence.SlotId, candidateTarget);
                        if (!validation.IsValid)
                        {
                            continue;
                        }

                        sourceSlotId = influence.SlotId;
                        targetSlotId = candidateTarget;
                        routeId = route.RouteId;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool CanRouteSupportOpponentRecipientCollection(GameState state, int routeOwnerPlayerId, string routeId)
        {
            if (string.IsNullOrEmpty(routeId))
            {
                return false;
            }

            var map = StaticMapDefinitions.Resolve(state.MapId);
            var mapQuery = new MapQueryService(map);
            var pathSearch = new MapPathSearchService(mapQuery);

            for (var playerIndex = 0; playerIndex < state.Players.Count; playerIndex++)
            {
                var payer = state.Players[playerIndex];
                if (payer.PlayerId == routeOwnerPlayerId ||
                    HasRouteInfluenceOwnedBy(state, routeId, payer.PlayerId))
                {
                    continue;
                }

                for (var tokenIndex = 0; tokenIndex < state.Map.ResourceTokens.Count; tokenIndex++)
                {
                    var locationId = state.Map.ResourceTokens[tokenIndex].LocationId;
                    if (locationId == payer.CityLocationId ||
                        !CanPlayerCollectLocation(state, payer.PlayerId, locationId))
                    {
                        continue;
                    }

                    MapPath path;
                    try
                    {
                        path = pathSearch.FindShortestPath(payer.CityLocationId, locationId);
                    }
                    catch
                    {
                        continue;
                    }

                    if (path != null && ContainsRouteId(path.RouteIds, routeId))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TrySubmitAutoplayDeployInfluence(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            PlayerState player,
            LocalhostAutoplayResult result)
        {
            var slotId = FindDeployableInfluenceSlot(state, player);
            if (string.IsNullOrEmpty(slotId))
            {
                return false;
            }

            var command = CreateCommand(
                "autoplay-deploy-r" + state.Round + "-a" + state.ActionRound + "-p" + player.PlayerId + "-" + result.DeployInfluenceAttempts,
                GameCommandKind.DeployInfluence,
                player.PlayerId,
                slotId);

            result.DeployInfluenceAttempts++;
            var beforeInfluenceCount = state.Map.Influences.Count;
            if (!SubmitCommand(dispatcher, command, result))
            {
                return false;
            }

            if (state.Map.Influences.Count > beforeInfluenceCount)
            {
                result.DeployInfluenceSuccesses++;
            }

            return true;
        }

        private static string FindDeployableInfluenceSlot(GameState state, PlayerState player)
        {
            var influenceService = new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)));
            for (var tokenIndex = 0; tokenIndex < state.Map.ResourceTokens.Count; tokenIndex++)
            {
                var locationId = state.Map.ResourceTokens[tokenIndex].LocationId;
                for (var slotIndex = 0; slotIndex < 6; slotIndex++)
                {
                    var slotId = InfluenceService.GetLocationSlotId(locationId, slotIndex);
                    var validation = influenceService.CanPlace(state, player.PlayerId, slotId);
                    if (validation.IsValid)
                    {
                        return slotId;
                    }
                }
            }

            return string.Empty;
        }

        private static string FindAffordableFacility(GameState state, PlayerState player)
        {
            if (player.PlayerId == ResolveFormalSupplyBuildPlayerId(state))
            {
                if (!player.BuiltFacilityIds.Contains(FormalSupplyBlueFacilityId) &&
                    CanBuildSuppliedFacility(state, player, FormalSupplyBlueFacilityId))
                {
                    return FormalSupplyBlueFacilityId;
                }

                if (!player.BuiltFacilityIds.Contains(FormalSupplyRedFacilityId) &&
                    CanBuildSuppliedFacility(state, player, FormalSupplyRedFacilityId))
                {
                    return FormalSupplyRedFacilityId;
                }

                // 预算或供给异常时等待，不用无关设施填掉 I 级图案的预留槽。
                if (state.Players.Count == 3 &&
                    (!player.BuiltFacilityIds.Contains(FormalSupplyBlueFacilityId) ||
                     !player.BuiltFacilityIds.Contains(FormalSupplyRedFacilityId)))
                {
                    return string.Empty;
                }
            }

            for (var i = 0; i < state.Decks.FacilitySupply.Count; i++)
            {
                var facilityId = state.Decks.FacilitySupply[i];
                if (facilityId == FormalSupplyBlueFacilityId || facilityId == FormalSupplyRedFacilityId)
                {
                    continue;
                }

                FacilityCardDefinition facility;
                if (!FacilityCardDatabase.TryGet(facilityId, out facility))
                {
                    continue;
                }

                if (FacilityCardDatabase.PlayerHasBuiltUniqueFacility(player, facility))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(ResolveFacilityPaymentMode(player, facility.FacilityId)))
                {
                    return facility.FacilityId;
                }
            }

            return string.Empty;
        }

        private static bool CanBuildSuppliedFacility(GameState state, PlayerState player, string facilityId)
        {
            return state.Decks.FacilitySupply.Contains(facilityId) &&
                   !string.IsNullOrEmpty(ResolveFacilityPaymentMode(player, facilityId));
        }

        private static bool IsFormalSupplyEvidenceFacility(GameState state, int playerId, string facilityId)
        {
            return playerId == ResolveFormalSupplyBuildPlayerId(state) &&
                   (facilityId == FormalSupplyBlueFacilityId || facilityId == FormalSupplyRedFacilityId);
        }

        private static string ResolveFacilityPaymentMode(PlayerState player, string facilityId)
        {
            var facility = FacilityCardDatabase.Get(facilityId);
            if (facility == null || player == null)
            {
                return string.Empty;
            }

            if (player.Resources.CanPay(facility.ResourceCost))
            {
                return BuildFacilityService.PaymentModeResources;
            }

            return player.Resources.GoldVoucher >= facility.GoldVoucherCost
                ? BuildFacilityService.PaymentModeGold
                : string.Empty;
        }

        private static string ResolveEffectiveFacilityPaymentMode(
            GameState state,
            PlayerState player,
            string facilityId)
        {
            var facility = FacilityCardDatabase.Get(facilityId);
            if (state == null || player == null || facility == null)
            {
                return string.Empty;
            }

            var effectiveCost = new FacilityBuildCostService().GetEffectiveResourceCost(state, player, facility);
            if (player.Resources.CanPay(effectiveCost))
            {
                return BuildFacilityService.PaymentModeResources;
            }

            return player.Resources.GoldVoucher >= facility.GoldVoucherCost
                ? BuildFacilityService.PaymentModeGold
                : string.Empty;
        }

        private static bool SubmitResourceCollection(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            LocalhostAutoplayResult result)
        {
            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (player.HasCollectedResourcesThisRound)
                {
                    continue;
                }

                var command = CreateCommand(
                    "autoplay-collect-r" + state.Round + "-p" + player.PlayerId,
                    GameCommandKind.CollectResource,
                    player.PlayerId,
                    string.Empty);
                var needsOpponentRecipient =
                    result.OpponentRouteRecipientCollectionSuccesses < RequiredOpponentRouteRecipientCollectionSuccesses;
                var paidPlan = result.PaidRouteCollectionSuccesses < RequiredPaidRouteCollectionSuccesses ||
                               needsOpponentRecipient
                    ? FindPaidRouteCollectionPlan(state, player, needsOpponentRecipient)
                    : null;
                var locationIds = paidPlan != null
                    ? paidPlan.LocationIds
                    : FindAutoplayCollectionTargets(state, player);
                if (locationIds.Count > 0)
                {
                    command.Parameters[CollectResourceCommandHandler.LocationIdsParameter] =
                        string.Join(",", locationIds.ToArray());
                }
                if (paidPlan != null && paidPlan.RouteIds.Count > 0)
                {
                    command.Parameters[CollectResourceCommandHandler.RouteIdsParameter] =
                        string.Join(",", paidPlan.RouteIds.ToArray());
                    result.PaidRouteCollectionAttempts++;
                }
                if (paidPlan != null && paidPlan.PaymentRecipients.Count > 0)
                {
                    command.Parameters[CollectResourceCommandHandler.PaymentRecipientsParameter] =
                        EncodePaymentRecipients(paidPlan.PaymentRecipients);
                    result.OpponentRouteRecipientCollectionAttempts++;
                }

                var beforeGoldVoucher = player.Resources.GoldVoucher;
                var beforeRecipientGoldVoucher = CaptureRecipientGoldVouchers(state, paidPlan);
                if (!SubmitCommand(dispatcher, command, result))
                {
                    return false;
                }

                if (paidPlan != null && player.Resources.GoldVoucher < beforeGoldVoucher)
                {
                    result.PaidRouteCollectionSuccesses++;
                    result.PaidRouteCollectionRoutes.Add(
                        string.Join(",", paidPlan.RouteIds.ToArray()) + "=>" + string.Join(",", paidPlan.LocationIds.ToArray()));
                }
                if (paidPlan != null &&
                    paidPlan.PaymentRecipients.Count > 0 &&
                    HasRecipientGoldVoucherIncreased(state, beforeRecipientGoldVoucher))
                {
                    result.OpponentRouteRecipientCollectionSuccesses++;
                    result.OpponentRouteRecipientCollections.Add(
                        FormatPaymentRecipients(paidPlan.PaymentRecipients) + "=>" + string.Join(",", paidPlan.LocationIds.ToArray()));
                }

                result.ResourceCollectionSubmissions++;
            }

            return state.Phase == GamePhase.Cleanup;
        }

        private static AutoplayCollectionPlan FindPaidRouteCollectionPlan(
            GameState state,
            PlayerState player,
            bool requireOpponentRecipient)
        {
            var map = StaticMapDefinitions.Resolve(state.MapId);
            var mapQuery = new MapQueryService(map);
            var pathSearch = new MapPathSearchService(mapQuery);
            var collectionService = new ResourceCollectionService(mapQuery);

            for (var i = 0; i < state.Map.ResourceTokens.Count; i++)
            {
                var locationId = state.Map.ResourceTokens[i].LocationId;
                if (locationId == player.CityLocationId ||
                    !CanPlayerCollectLocation(state, player.PlayerId, locationId))
                {
                    continue;
                }

                MapPath path;
                try
                {
                    path = pathSearch.FindShortestPath(player.CityLocationId, locationId);
                }
                catch
                {
                    continue;
                }

                if (path == null ||
                    path.RouteIds == null ||
                    path.RouteIds.Count <= 0 ||
                    !HasPaidRouteForPlayer(state, path.RouteIds, player.PlayerId))
                {
                    continue;
                }

                var locationIds = new List<string> { locationId };
                var paymentRecipients = BuildOpponentPaymentRecipients(state, player.PlayerId, path.RouteIds);
                if (requireOpponentRecipient && paymentRecipients.Count <= 0)
                {
                    continue;
                }

                var validation = collectionService.CanCollect(
                    state,
                    player.PlayerId,
                    locationIds,
                    path.RouteIds,
                    paymentRecipients);
                if (!validation.IsValid)
                {
                    continue;
                }

                return new AutoplayCollectionPlan
                {
                    LocationIds = locationIds,
                    RouteIds = new List<string>(path.RouteIds),
                    PaymentRecipients = paymentRecipients
                };
            }

            return null;
        }

        private static Dictionary<string, int> BuildOpponentPaymentRecipients(
            GameState state,
            int playerId,
            IReadOnlyList<string> routeIds)
        {
            var result = new Dictionary<string, int>();
            if (routeIds == null)
            {
                return result;
            }

            for (var i = 0; i < routeIds.Count; i++)
            {
                var routeId = routeIds[i];
                var receiverPlayerId = FindOpponentInfluenceOwnerOnRoute(state, routeId, playerId);
                if (receiverPlayerId > 0)
                {
                    result[routeId] = receiverPlayerId;
                }
            }

            return result;
        }

        private static List<string> FindAutoplayCollectionTargets(GameState state, PlayerState player)
        {
            var result = new List<string>();
            if (player == null)
            {
                return result;
            }

            if (HasResourceToken(state, player.CityLocationId))
            {
                result.Add(player.CityLocationId);
            }

            return result;
        }

        private static bool CanPlayerCollectLocation(GameState state, int playerId, string locationId)
        {
            var player = state.FindPlayer(playerId);
            if (player != null && player.CityLocationId == locationId)
            {
                return true;
            }

            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId == playerId && influence.LocationId == locationId)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasPaidRouteForPlayer(GameState state, IReadOnlyList<string> routeIds, int playerId)
        {
            for (var i = 0; i < routeIds.Count; i++)
            {
                if (!HasRouteInfluenceOwnedBy(state, routeIds[i], playerId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasRouteInfluenceOwnedBy(GameState state, string routeId, int playerId)
        {
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId == playerId && influence.RouteId == routeId)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsRouteId(IReadOnlyList<string> routeIds, string routeId)
        {
            if (routeIds == null || string.IsNullOrEmpty(routeId))
            {
                return false;
            }

            for (var i = 0; i < routeIds.Count; i++)
            {
                if (routeIds[i] == routeId)
                {
                    return true;
                }
            }

            return false;
        }

        private static int FindOpponentInfluenceOwnerOnRoute(GameState state, string routeId, int playerId)
        {
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId != playerId && HasInfluenceOnRoute(influence, routeId))
                {
                    return influence.PlayerId;
                }
            }

            return -1;
        }

        private static bool HasInfluenceOnRoute(InfluencePlacement influence, string routeId)
        {
            return influence != null && influence.RouteId == routeId;
        }

        private static Dictionary<int, int> CaptureRecipientGoldVouchers(GameState state, AutoplayCollectionPlan plan)
        {
            var result = new Dictionary<int, int>();
            if (plan == null || plan.PaymentRecipients.Count <= 0)
            {
                return result;
            }

            foreach (var entry in plan.PaymentRecipients)
            {
                var receiver = state.FindPlayer(entry.Value);
                if (receiver != null && !result.ContainsKey(receiver.PlayerId))
                {
                    result[receiver.PlayerId] = receiver.Resources.GoldVoucher;
                }
            }

            return result;
        }

        private static bool HasRecipientGoldVoucherIncreased(
            GameState state,
            IReadOnlyDictionary<int, int> beforeGoldVouchers)
        {
            if (beforeGoldVouchers == null || beforeGoldVouchers.Count <= 0)
            {
                return false;
            }

            foreach (var entry in beforeGoldVouchers)
            {
                var receiver = state.FindPlayer(entry.Key);
                if (receiver != null && receiver.Resources.GoldVoucher > entry.Value)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasResourceToken(GameState state, string locationId)
        {
            if (string.IsNullOrEmpty(locationId))
            {
                return false;
            }

            for (var i = 0; i < state.Map.ResourceTokens.Count; i++)
            {
                if (state.Map.ResourceTokens[i].LocationId == locationId)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SubmitCommand(
            AuthoritativeCommandDispatcher dispatcher,
            GameCommand command,
            LocalhostAutoplayResult result)
        {
            result.SubmittedCommands++;
            var dto = GameCommandDto.FromCommand(command);
            var commandResult = command.PlayerId == 1
                ? dispatcher.SubmitHostCommand(dto)
                : dispatcher.ReceiveClientCommand((ulong)command.PlayerId, dto);

            if (commandResult.Succeeded)
            {
                return true;
            }

            result.FailedCommandSummary = FormatCommand(command);
            result.FailureReason = command.CommandId + " 失败：" +
                                   (commandResult.Validation == null ? "未知错误" : commandResult.Validation.Reason);
            return false;
        }

        private static GameCommand CreateCommand(string commandId, GameCommandKind kind, int playerId, string targetId)
        {
            return new GameCommand
            {
                CommandId = commandId,
                Kind = kind,
                PlayerId = playerId,
                TargetId = targetId
            };
        }

        private static GameCommand CreateResolveEntranceCommand(int playerId, string optionId)
        {
            var command = CreateCommand(
                "autoplay-resolve-entrance-" + playerId,
                GameCommandKind.ResolveEntranceEvent,
                playerId,
                optionId);
            command.OptionIds.Add(optionId);
            return command;
        }

        private static void RegisterClientSeats(AuthoritativeCommandDispatcher dispatcher, IList<PlayerSeat> seats)
        {
            for (var i = 0; i < seats.Count; i++)
            {
                if (seats[i].PlayerId != 1)
                {
                    dispatcher.RegisterClientPlayer((ulong)seats[i].PlayerId, seats[i].PlayerId);
                }
            }
        }

        private static List<PlayerSeat> CreateJoinedSeats(int playerCount)
        {
            var seats = new List<PlayerSeat>
            {
                CreateSeat(1, PlayerColor.Blue),
                CreateSeat(2, PlayerColor.Red),
                CreateSeat(3, PlayerColor.Green)
            };
            if (playerCount == 4)
            {
                seats.Add(CreateSeat(4, PlayerColor.Yellow));
            }
            return seats;
        }

        private static PlayerSeat CreateSeat(int playerId, PlayerColor color)
        {
            return new PlayerSeat
            {
                PlayerId = playerId,
                NetworkClientId = (ulong)playerId,
                PlayerName = "Player " + playerId,
                Color = color,
                IsReady = true
            };
        }

        private static string BuildSnapshot(LocalhostAutoplayResult result)
        {
            var state = result.FinalState;
            var builder = new StringBuilder();
            builder.AppendLine("本地联机自动跑局快照");
            builder.AppendLine("RoomId: " + result.RoomId);
            builder.AppendLine("MapId: " + state.MapId);
            builder.AppendLine("PlayerCount: " + state.Players.Count);
            builder.AppendLine("EvidenceScope: 进程内权威命令跑局；含设施/资金夹具与供给排序，不代替正常入口或多实例验收。");
            builder.AppendLine("Success: " + result.Succeeded);
            if (!string.IsNullOrEmpty(result.FailureReason))
            {
                builder.AppendLine("FailureReason: " + result.FailureReason);
            }

            builder.AppendLine("EventDeckSeed: " + result.EventDeckSeed);
            builder.AppendLine("SeatsReady: " + CountReadySeats(result.Seats) + "/" + result.Seats.Count);
            builder.AppendLine("SubmittedCommands: " + result.SubmittedCommands);
            builder.AppendLine("AcceptedCommands: " + result.AcceptedCommands);
            builder.AppendLine("ExploreLocationAttempts: " + result.ExploreLocationAttempts);
            builder.AppendLine("ExploreLocationSuccesses: " + result.ExploreLocationSuccesses);
            builder.AppendLine("MoveCityAttempts: " + result.MoveCityAttempts);
            builder.AppendLine("MoveCitySuccesses: " + result.MoveCitySuccesses);
            builder.AppendLine("DispatchInfluenceAttempts: " + result.DispatchInfluenceAttempts);
            builder.AppendLine("DispatchInfluenceSuccesses: " + result.DispatchInfluenceSuccesses);
            builder.AppendLine("BuildFacilityAttempts: " + result.BuildFacilityAttempts);
            builder.AppendLine("BuildFacilitySuccesses: " + result.BuildFacilitySuccesses);
            builder.AppendLine("CharacterCardUseAttempts: " + result.CharacterCardUseAttempts);
            builder.AppendLine("CharacterCardUseSuccesses: " + result.CharacterCardUseSuccesses);
            builder.AppendLine("DeclareCityStyleAttempts: " + result.DeclareCityStyleAttempts);
            builder.AppendLine("DeclareCityStyleSuccesses: " + result.DeclareCityStyleSuccesses);
            builder.AppendLine("SpecialActionAttempts: " + result.SpecialActionAttempts);
            builder.AppendLine("SpecialActionSuccesses: " + result.SpecialActionSuccesses);
            builder.AppendLine("PendingSpecialActionStep: " + FormatPendingSpecialActionStep(state.PendingSpecialAction));
            builder.AppendLine("DeployInfluenceAttempts: " + result.DeployInfluenceAttempts);
            builder.AppendLine("DeployInfluenceSuccesses: " + result.DeployInfluenceSuccesses);
            builder.AppendLine("ResourceCollectionSubmissions: " + result.ResourceCollectionSubmissions);
            builder.AppendLine("PaidRouteCollectionAttempts: " + result.PaidRouteCollectionAttempts);
            builder.AppendLine("PaidRouteCollectionSuccesses: " + result.PaidRouteCollectionSuccesses);
            builder.AppendLine("OpponentRouteRecipientCollectionAttempts: " + result.OpponentRouteRecipientCollectionAttempts);
            builder.AppendLine("OpponentRouteRecipientCollectionSuccesses: " + result.OpponentRouteRecipientCollectionSuccesses);
            builder.AppendLine("Round: " + state.Round + "/" + state.MaxRounds);
            builder.AppendLine("Phase: " + state.Phase);
            builder.AppendLine("ActionRound: " + state.ActionRound);
            builder.AppendLine("CurrentPlayerId: " + state.CurrentPlayerId);
            builder.AppendLine("RoundTrackIndex: " + RoundTrackRule.GetRoundIndex(state));
            builder.AppendLine("OpenLocations: " + string.Join(",", state.Map.OpenLocationIds.ToArray()));
            builder.AppendLine("ResourceTokens: " + state.Map.ResourceTokens.Count);
            builder.AppendLine("BuiltFacilities: " + state.Map.Facilities.Count);
            builder.AppendLine("Influences: " + state.Map.Influences.Count);
            builder.AppendLine("CollectedPlayers: " + CountCollectedPlayers(state) + "/" + state.Players.Count);
            builder.AppendLine("Logs: " + state.Logs.Count);
            AppendAutoplayCoverage(builder, result);
            AppendFacilities(builder, state);
            AppendSpecialActionMarkers(builder, state);
            AppendInfluences(builder, state);
            AppendFinalScoring(builder, state);
            builder.AppendLine("Players:");
            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                builder.AppendLine("- P" + player.PlayerId +
                                   " city=" + player.CityLocationId +
                                   " gold=" + player.Resources.GoldVoucher +
                                   " score=" + player.Score +
                                   " remainingMainActions=" + player.RemainingMainActionsThisTurn +
                                   " acted=" + player.ActedMainActionThisTurn);
            }

            if (!string.IsNullOrEmpty(result.FailureReason))
            {
                AppendFailureDiagnostics(builder, state, result);
            }

            return builder.ToString();
        }

        private static void AppendAutoplayCoverage(StringBuilder builder, LocalhostAutoplayResult result)
        {
            builder.AppendLine("ExploredLocations: " + FormatIds(result.ExploredLocationIds));
            builder.AppendLine("MoveCityRoutes: " + FormatIds(result.MoveCityRoutes));
            builder.AppendLine("DispatchInfluenceRoutes: " + FormatIds(result.DispatchInfluenceRoutes));
            builder.AppendLine("PaidRouteCollections: " + FormatIds(result.PaidRouteCollectionRoutes));
            builder.AppendLine("OpponentRouteRecipientCollections: " + FormatIds(result.OpponentRouteRecipientCollections));
            builder.AppendLine("FormalSupplyBuilds: " + FormatIds(result.FormalSupplyBuilds));
            builder.AppendLine("CharacterCardExecutions: " + FormatIds(result.CharacterCardExecutions));
            builder.AppendLine("CityStyleDeclarations: " + FormatIds(result.CityStyleDeclarations));
            builder.AppendLine("SpecialActionFixtures: " + FormatIds(result.SpecialActionFixtures));
            builder.AppendLine("SpecialActionPendingSteps: " + FormatIds(result.SpecialActionPendingSteps));
            builder.AppendLine("SpecialActionExecutions: " + FormatIds(result.SpecialActionExecutions));
        }

        private static void AppendFacilities(StringBuilder builder, GameState state)
        {
            builder.AppendLine("Facilities:");
            for (var i = 0; i < state.Map.Facilities.Count; i++)
            {
                var facility = state.Map.Facilities[i];
                builder.AppendLine("- P" + facility.PlayerId +
                                   " facility=" + facility.FacilityCardId +
                                   " slot=" + facility.CityBoardSlotIndex);
            }
        }

        private static void AppendInfluences(StringBuilder builder, GameState state)
        {
            builder.AppendLine("InfluencePlacements:");
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                builder.AppendLine("- P" + influence.PlayerId +
                                   " slot=" + influence.SlotId +
                                   " location=" + influence.LocationId +
                                   " route=" + influence.RouteId);
            }
        }

        private static void AppendSpecialActionMarkers(StringBuilder builder, GameState state)
        {
            builder.AppendLine("SpecialActionMarkers:");
            for (var playerIndex = 0; playerIndex < state.Players.Count; playerIndex++)
            {
                var player = state.Players[playerIndex];
                for (var declarationIndex = 0; declarationIndex < player.DeclaredCityStyles.Count; declarationIndex++)
                {
                    var declaration = player.DeclaredCityStyles[declarationIndex];
                    if (declaration == null || string.IsNullOrEmpty(declaration.UnlockedSpecialActionId))
                    {
                        continue;
                    }

                    builder.AppendLine(
                        "- P" + player.PlayerId +
                        " action=" + declaration.UnlockedSpecialActionId +
                        " marker=" + declaration.InfluenceMarkerId +
                        " area=" + declaration.MarkerArea +
                        " remainingUses=" + declaration.RemainingSpecialActionUses +
                        " remainingMainActions=" + player.RemainingMainActionsThisTurn);
                }
            }
        }

        private static void AppendFailureDiagnostics(StringBuilder builder, GameState state, LocalhostAutoplayResult result)
        {
            builder.AppendLine("失败诊断快照:");
            builder.AppendLine("FailedCommand: " + result.FailedCommandSummary);
            builder.AppendLine("PendingChoice: " + FormatPendingChoice(state.PendingChoice));
            builder.AppendLine("PendingCardSession: " + FormatPendingCardSession(state.PendingCardSession));
            builder.AppendLine("PendingSpecialActionStep: " + FormatPendingSpecialActionStep(state.PendingSpecialAction));
            builder.AppendLine("RecentLogs:");
            var start = state.Logs.Count > 5 ? state.Logs.Count - 5 : 0;
            for (var i = start; i < state.Logs.Count; i++)
            {
                var log = state.Logs[i];
                builder.AppendLine("- #" + log.Sequence + " P" + log.PlayerId + " " + log.CommandId + " " + log.Message);
            }
        }

        private static void AppendFinalScoring(StringBuilder builder, GameState state)
        {
            if (state.FinalScoring == null || !state.FinalScoring.IsResolved)
            {
                builder.AppendLine("FinalScoringResolved: False");
                return;
            }

            builder.AppendLine("FinalScoringResolved: True");
            builder.AppendLine("Winners: " + FormatWinnerIds(state.FinalScoring.WinnerPlayerIds));
            builder.AppendLine("Tiebreak: " + state.FinalScoring.TiebreakSummary);
            builder.AppendLine("FinalScores:");
            for (var i = 0; i < state.FinalScoring.PlayerScores.Count; i++)
            {
                var score = state.FinalScoring.PlayerScores[i];
                builder.AppendLine("- P" + score.PlayerId +
                                   " total=" + score.TotalScore +
                                   " base=" + score.BaseScore +
                                   " region=" + score.RegionScore +
                                   " resource=" + score.ResourceScore +
                                   " facility=" + score.FacilityScore +
                                   " cityStyle=" + score.CityStyleScore);
            }
        }

        private static string FormatWinnerIds(List<int> winnerPlayerIds)
        {
            if (winnerPlayerIds == null || winnerPlayerIds.Count == 0)
            {
                return string.Empty;
            }

            var parts = new string[winnerPlayerIds.Count];
            for (var i = 0; i < winnerPlayerIds.Count; i++)
            {
                parts[i] = "P" + winnerPlayerIds[i];
            }

            return string.Join(",", parts);
        }

        private static string FormatIds(List<string> ids)
        {
            if (ids == null || ids.Count <= 0)
            {
                return string.Empty;
            }

            return string.Join(",", ids.ToArray());
        }

        private static string EncodePaymentRecipients(Dictionary<string, int> paymentRecipients)
        {
            return FormatPaymentRecipients(paymentRecipients);
        }

        private static string FormatPaymentRecipients(Dictionary<string, int> paymentRecipients)
        {
            if (paymentRecipients == null || paymentRecipients.Count <= 0)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            foreach (var entry in paymentRecipients)
            {
                parts.Add(entry.Key + "=" + entry.Value);
            }

            return string.Join(";", parts.ToArray());
        }

        private static string FormatCommand(GameCommand command)
        {
            if (command == null)
            {
                return string.Empty;
            }

            return command.CommandId + " kind=" + command.Kind + " player=" + command.PlayerId + " target=" + command.TargetId;
        }

        private static string FormatPendingChoice(PendingChoiceState pendingChoice)
        {
            if (pendingChoice == null || !pendingChoice.IsValid())
            {
                return "None";
            }

            return pendingChoice.ChoiceType +
                   " player=" + pendingChoice.PlayerId +
                   " card=" + pendingChoice.CardId +
                   " target=" + pendingChoice.TargetId +
                   " options=" + string.Join(",", pendingChoice.OptionIds.ToArray());
        }

        private static string FormatPendingCardSession(PendingCardSessionState pendingCardSession)
        {
            if (pendingCardSession == null || !pendingCardSession.IsValid())
            {
                return "None";
            }

            return pendingCardSession.ChoiceType +
                   " player=" + pendingCardSession.PlayerId +
                   " card=" + pendingCardSession.CardId +
                   " target=" + pendingCardSession.TargetId +
                   " options=" + string.Join(",", pendingCardSession.OptionIds.ToArray());
        }

        private static string FormatPendingSpecialActionStep(PendingSpecialActionState pendingSpecialAction)
        {
            return pendingSpecialAction != null && pendingSpecialAction.IsValid()
                ? pendingSpecialAction.Step
                : "None";
        }

        private static EventCardDefinition PeekEventCard(GameState state, string locationId)
        {
            var eventColor = StaticMapDefinitions.GetEventColor(state.MapId, locationId);
            var eventDeckService = new EventDeckService();
            if (eventDeckService.RemainingCount(state.Decks, eventColor) <= 0)
            {
                return null;
            }

            return EventCardDatabase.Get(eventDeckService.Peek(state.Decks, eventColor));
        }

        private static int ScoreRewardForAutoplay(ResourceSet reward)
        {
            if (reward == null)
            {
                return 0;
            }

            return reward.OriginiumShard * 100 +
                   reward.GoldVoucher * 10 +
                   reward.Iron * 4 +
                   reward.Originium * 3 +
                   reward.PureOriginium * 2;
        }

        private static int CountReadySeats(IList<PlayerSeat> seats)
        {
            var count = 0;
            for (var i = 0; i < seats.Count; i++)
            {
                if (seats[i].IsReady)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountCollectedPlayers(GameState state)
        {
            var count = 0;
            for (var i = 0; i < state.Players.Count; i++)
            {
                if (state.Players[i].HasCollectedResourcesThisRound)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public sealed class LocalhostAutoplayResult
    {
        public bool Succeeded;
        public string FailureReason = string.Empty;
        public string RoomId = string.Empty;
        public int EventDeckSeed;
        public int SubmittedCommands;
        public int AcceptedCommands;
        public int ExploreLocationAttempts;
        public int ExploreLocationSuccesses;
        public int MoveCityAttempts;
        public int MoveCitySuccesses;
        public int DispatchInfluenceAttempts;
        public int DispatchInfluenceSuccesses;
        public int BuildFacilityAttempts;
        public int BuildFacilitySuccesses;
        public int CharacterCardUseAttempts;
        public int CharacterCardUseSuccesses;
        public int DeclareCityStyleAttempts;
        public int DeclareCityStyleSuccesses;
        public int SpecialActionAttempts;
        public int SpecialActionSuccesses;
        public int SeededSpecialActionFacilityCount;
        public int DeployInfluenceAttempts;
        public int DeployInfluenceSuccesses;
        public int ResourceCollectionSubmissions;
        public int PaidRouteCollectionAttempts;
        public int PaidRouteCollectionSuccesses;
        public int OpponentRouteRecipientCollectionAttempts;
        public int OpponentRouteRecipientCollectionSuccesses;
        public string FailedCommandSummary = string.Empty;
        public List<string> ExploredLocationIds = new List<string>();
        public List<string> MoveCityRoutes = new List<string>();
        public List<string> DispatchInfluenceRoutes = new List<string>();
        public List<string> PaidRouteCollectionRoutes = new List<string>();
        public List<string> OpponentRouteRecipientCollections = new List<string>();
        public List<string> FormalSupplyBuilds = new List<string>();
        public List<string> CharacterCardExecutions = new List<string>();
        public List<string> CityStyleDeclarations = new List<string>();
        public List<string> SpecialActionFixtures = new List<string>();
        public List<string> SpecialActionPendingSteps = new List<string>();
        public List<string> SpecialActionExecutions = new List<string>();
        public List<PlayerSeat> Seats = new List<PlayerSeat>();
        public GameState FinalState;
        public string Snapshot = string.Empty;
    }

    internal sealed class AutoplayCollectionPlan
    {
        public List<string> LocationIds = new List<string>();
        public List<string> RouteIds = new List<string>();
        public Dictionary<string, int> PaymentRecipients = new Dictionary<string, int>();
    }
}






