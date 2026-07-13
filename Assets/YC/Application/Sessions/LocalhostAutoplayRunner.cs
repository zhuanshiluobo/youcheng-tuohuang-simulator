using System.Collections.Generic;
using System.Text;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Application.Setup;
using YC.Domain.Cards;
using YC.Domain.CardFlows;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Exploration;
using YC.Domain.Facilities;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Application.DevTools
{
    public static class LocalhostAutoplayRunner
    {
        public const string DefaultRoomId = "DEV_LOCALHOST_AUTOPLAY";

        private static readonly string[] InitialLocations = { "G-01", "A-01", "A-02", "B-01" };
        private const int RequiredExploreSuccesses = 1;
        private const int RequiredMoveCitySuccesses = 1;
        private const int RequiredDispatchInfluenceSuccesses = 1;
        private const int RequiredPaidRouteCollectionSuccesses = 1;
        private const int RequiredOpponentRouteRecipientCollectionSuccesses = 1;
        private const int FormalSupplyBuildPlayerId = 4;
        private const string FormalSupplyBlueFacilityId = FacilityCardDatabase.TradeDistrict;
        private const string FormalSupplyRedFacilityId = FacilityCardDatabase.EquipmentWarehouse;
        private const string AutoplayCityStyleId = CityStyleDatabase.MilitaryIndustrialArea;

        public static LocalhostAutoplayResult RunToRound8Settlement()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var seats = CreateJoinedSeats();
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

            if (SubmitInitialPlacements(dispatcher, session.State, result) &&
                LoopEndActions(dispatcher, session.State, result))
            {
                result.Succeeded = session.State.Round == 8 && session.State.Phase == GamePhase.FinalScoring;
                if (!result.Succeeded)
                {
                    result.FailureReason = "自动跑局未到达第 8 回合结算点。";
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
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, seats, map.MapId, eventDeckSeed);
            EnsureAutoplayFormalFacilitySupply(state);

            eventDeckService.InitializeDecks(
                state.Decks,
                EventCardDatabase.GreenCardIds,
                EventCardDatabase.YellowCardIds,
                EventCardDatabase.RedCardIds);

            var session = new GameSession(state);
            session.RegisterHandler(new SetupCommandHandler(
                mapQuery,
                eventDeckService,
                resourceTokenService,
                new TurnOrderService()));
            session.RegisterHandler(new BuildFacilityCommandHandler(buildFacilityService, new RoundAdvanceService()));
            session.RegisterHandler(new DeclareCityStyleCommandHandler());
            session.RegisterHandler(new CoverCharacterCardCommandHandler());
            session.RegisterHandler(new UseCharacterCardCommandHandler());
            session.RegisterHandler(new DeployInfluenceCommandHandler(influenceService));
            session.RegisterHandler(new DispatchInfluenceCommandHandler(influenceService));
            session.RegisterHandler(new ExploreLocationCommandHandler(explorationService));
            session.RegisterHandler(new MoveCityCommandHandler(movementService));
            session.RegisterHandler(new ResolveFacilityEffectCommandHandler(
                buildFacilityService,
                entryEffectService,
                influenceService,
                movementService,
                explorationService,
                mapQuery));
            session.RegisterHandler(new EndActionCommandHandler(
                new RoundAdvanceService(),
                new FinalScoringService(mapQuery)));
            session.RegisterHandler(new CollectResourceCommandHandler(new ResourceCollectionService(mapQuery, resourceTokenService)));
            return session;
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
            for (var i = 0; i < InitialLocations.Length; i++)
            {
                var playerId = i + 1;
                if (!SubmitCommand(dispatcher, CreateCommand(
                    "autoplay-place-" + playerId,
                    GameCommandKind.ChooseInitialLocation,
                    playerId,
                    InitialLocations[i]), result))
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

                var cardId = player.HandCardIds.Count > 0
                    ? player.HandCardIds[0]
                    : (player.DiscardCardIds.Count > 0 ? player.DiscardCardIds[0] : string.Empty);
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

        private static bool TrySubmitAutoplayPendingChoice(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            LocalhostAutoplayResult result)
        {
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
                return SubmitCommand(dispatcher, command, result);
            }

            result.FailureReason = "自动跑局遇到未处理选择：" +
                                   (pendingChoice == null ? "未知待选会话" : pendingChoice.ChoiceType);
            return false;
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
                    return TrySetFirstFacilityOption(command, pending, out failureReason);
                case FacilityPendingChoiceTypes.DeployTwoInfluences:
                    SetFacilityOption(command, FacilityPendingChoiceTypes.ConfirmOption);
                    var deploySlots = FindLegalInfluencePlacementSlots(state, player, 2);
                    if (deploySlots.Count == 0)
                    {
                        failureReason = "护航调度中心没有可部署的影响力槽位。";
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
            var map = StaticMapDefinitions.CreateFourPlayerMap();
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

            failureReason = "载具仓库既没有可执行的移除/调度，也没有合法探索目标。";
            return false;
        }

        private static bool TryConfigureFacilityExplore(
            GameState state,
            PlayerState player,
            GameCommand command)
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
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
            var map = StaticMapDefinitions.CreateFourPlayerMap();
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

            var map = StaticMapDefinitions.CreateFourPlayerMap();
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

        private static bool TrySubmitAutoplayMainAction(
            AuthoritativeCommandDispatcher dispatcher,
            GameState state,
            PlayerState player,
            LocalhostAutoplayResult result)
        {
            if (result.ExploreLocationSuccesses < RequiredExploreSuccesses &&
                TrySubmitAutoplayExplore(dispatcher, state, player, result))
            {
                return true;
            }

            if (result.MoveCitySuccesses < RequiredMoveCitySuccesses &&
                TrySubmitAutoplayMoveCity(dispatcher, state, player, result))
            {
                return true;
            }

            if (result.DispatchInfluenceSuccesses < RequiredDispatchInfluenceSuccesses &&
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
                if (IsFormalSupplyEvidenceFacility(player.PlayerId, facilityId))
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
            if (player.PlayerId != FormalSupplyBuildPlayerId ||
                player.DeclaredCityStyleIds.Contains(AutoplayCityStyleId))
            {
                return true;
            }

            var validation = new DeclareCityStyleService().Validate(state, player.PlayerId, AutoplayCityStyleId);
            if (!validation.IsValid)
            {
                return true;
            }

            var command = CreateCommand(
                "autoplay-declare-city-style-r" + state.Round + "-a" + state.ActionRound + "-p" + player.PlayerId,
                GameCommandKind.DeclareCityStyle,
                player.PlayerId,
                AutoplayCityStyleId);
            command.Parameters[DeclareCityStyleCommandHandler.CityStyleIdParameter] = AutoplayCityStyleId;

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

        private static int ResolveAutoplayFacilitySlot(GameState state, PlayerState player, string facilityId)
        {
            if (player.PlayerId == FormalSupplyBuildPlayerId)
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
            var map = StaticMapDefinitions.CreateFourPlayerMap();
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
            var map = StaticMapDefinitions.CreateFourPlayerMap();
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
            var map = StaticMapDefinitions.CreateFourPlayerMap();
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

            var map = StaticMapDefinitions.CreateFourPlayerMap();
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
            var influenceService = new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
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
            if (player.PlayerId == FormalSupplyBuildPlayerId)
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

        private static bool IsFormalSupplyEvidenceFacility(int playerId, string facilityId)
        {
            return playerId == FormalSupplyBuildPlayerId &&
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
            var map = StaticMapDefinitions.CreateFourPlayerMap();
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

        private static List<PlayerSeat> CreateJoinedSeats()
        {
            return new List<PlayerSeat>
            {
                CreateSeat(1, PlayerColor.Blue),
                CreateSeat(2, PlayerColor.Red),
                CreateSeat(3, PlayerColor.Green),
                CreateSeat(4, PlayerColor.Yellow)
            };
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
            builder.AppendLine("DeclareCityStyleAttempts: " + result.DeclareCityStyleAttempts);
            builder.AppendLine("DeclareCityStyleSuccesses: " + result.DeclareCityStyleSuccesses);
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
            builder.AppendLine("CityStyleDeclarations: " + FormatIds(result.CityStyleDeclarations));
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

        private static void AppendFailureDiagnostics(StringBuilder builder, GameState state, LocalhostAutoplayResult result)
        {
            builder.AppendLine("失败诊断快照:");
            builder.AppendLine("FailedCommand: " + result.FailedCommandSummary);
            builder.AppendLine("PendingChoice: " + FormatPendingChoice(state.PendingChoice));
            builder.AppendLine("PendingCardSession: " + FormatPendingCardSession(state.PendingCardSession));
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

        private static EventCardDefinition PeekEventCard(GameState state, string locationId)
        {
            var eventColor = StaticMapDefinitions.GetEventColor(locationId);
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
        public int DeclareCityStyleAttempts;
        public int DeclareCityStyleSuccesses;
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
        public List<string> CityStyleDeclarations = new List<string>();
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






