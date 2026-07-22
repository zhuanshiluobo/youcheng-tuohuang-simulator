using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.CardFlows;
using YC.Domain.Commands;
using YC.Domain.Economy;
using YC.Domain.Events;
using YC.Domain.Exploration;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class ResolveFacilityEffectCommandHandler : IGameCommandHandler
    {
        public const string PendingSessionIdParameter = "pendingSessionId";
        public const string OptionIdParameter = "optionId";
        public const string OriginiumAmountParameter = "originiumAmount";
        public const string OriginiumShardAmountParameter = "originiumShardAmount";
        public const string IronAmountParameter = "ironAmount";
        public const string PureOriginiumAmountParameter = "pureOriginiumAmount";
        public const string InfluenceSlotIdsParameter = "influenceSlotIds";
        public const string RemoveInfluenceSlotIdParameter = "removeInfluenceSlotId";
        public const string SourceInfluenceSlotIdParameter = "sourceInfluenceSlotId";
        public const string TargetInfluenceSlotIdParameter = "targetInfluenceSlotId";
        public const string TargetLocationIdParameter = "targetLocationId";
        public const string RouteInfluenceSlotIdParameter = "routeInfluenceSlotId";

        private readonly BuildFacilityService buildFacilityService;
        private readonly FacilityEntryEffectService entryEffectService;
        private readonly InfluenceService influenceService;
        private readonly FacilityInfluenceEffectService facilityInfluenceEffectService;
        private readonly MoveCityCommandHandler moveCityCommandHandler;
        private readonly ExploreLocationCommandHandler exploreLocationCommandHandler;
        private readonly IMapQueryService mapQuery;
        private readonly ResourceSaleService resourceSaleService;

        public ResolveFacilityEffectCommandHandler(
            BuildFacilityService buildFacilityService,
            FacilityEntryEffectService entryEffectService,
            InfluenceService influenceService,
            CityMovementService cityMovementService,
            ExplorationService explorationService,
            IMapQueryService mapQuery)
            : this(
                buildFacilityService,
                entryEffectService,
                influenceService,
                cityMovementService,
                explorationService,
                mapQuery,
                new ResourceSaleService())
        {
        }

        public ResolveFacilityEffectCommandHandler(
            BuildFacilityService buildFacilityService,
            FacilityEntryEffectService entryEffectService,
            InfluenceService influenceService,
            CityMovementService cityMovementService,
            ExplorationService explorationService,
            IMapQueryService mapQuery,
            ResourceSaleService resourceSaleService)
            : this(
                buildFacilityService,
                entryEffectService,
                influenceService,
                new MoveCityCommandHandler(
                    cityMovementService ?? throw new ArgumentNullException(nameof(cityMovementService))),
                new ExploreLocationCommandHandler(
                    explorationService ?? throw new ArgumentNullException(nameof(explorationService))),
                mapQuery,
                resourceSaleService)
        {
        }

        public ResolveFacilityEffectCommandHandler(
            BuildFacilityService buildFacilityService,
            FacilityEntryEffectService entryEffectService,
            InfluenceService influenceService,
            MoveCityCommandHandler moveCityCommandHandler,
            ExploreLocationCommandHandler exploreLocationCommandHandler,
            IMapQueryService mapQuery,
            ResourceSaleService resourceSaleService)
        {
            this.buildFacilityService = buildFacilityService ?? throw new ArgumentNullException(nameof(buildFacilityService));
            this.entryEffectService = entryEffectService ?? throw new ArgumentNullException(nameof(entryEffectService));
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
            facilityInfluenceEffectService = new FacilityInfluenceEffectService(this.influenceService);
            this.moveCityCommandHandler = moveCityCommandHandler ??
                                          throw new ArgumentNullException(nameof(moveCityCommandHandler));
            this.exploreLocationCommandHandler = exploreLocationCommandHandler ??
                                                 throw new ArgumentNullException(nameof(exploreLocationCommandHandler));
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.resourceSaleService = resourceSaleService ?? throw new ArgumentNullException(nameof(resourceSaleService));
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null && command.Kind == GameCommandKind.ResolvePendingChoice;
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            var pending = state == null ? null : state.PendingCardSession;
            if (pending == null ||
                pending.ScenarioId != FacilityPendingChoiceTypes.ScenarioId ||
                !pending.IsValid())
            {
                return Invalid(CommandErrorCode.PendingChoiceRequired, "当前没有待处理的设施入场效果。");
            }

            if (pending.PlayerId != command.PlayerId)
            {
                return Invalid(CommandErrorCode.NotCurrentPlayer, "只有建设该设施的玩家可以处理入场效果。");
            }

            var submittedSessionId = GetParameter(command, PendingSessionIdParameter);
            if (string.IsNullOrEmpty(submittedSessionId) || submittedSessionId != pending.SessionId)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "设施效果选择已经过期，请按当前待选项重新选择。");
            }

            CommandResult result;
            switch (pending.ChoiceType)
            {
                case FacilityPendingChoiceTypes.CopyAdjacentEntryEffect:
                    result = ResolveCopyAdjacent(state, command, pending);
                    break;
                case FacilityPendingChoiceTypes.BuildAdditionalFacility:
                    result = ResolveAdditionalBuild(state, command, pending);
                    break;
                case FacilityPendingChoiceTypes.BuildExtensionHub:
                    result = ResolveExtensionHub(state, command, pending);
                    break;
                case FacilityPendingChoiceTypes.SellResources:
                    result = ResolveSellResources(state, command, pending);
                    break;
                case FacilityPendingChoiceTypes.FreeCityMove:
                    result = ResolveFreeCityMove(state, command, pending);
                    break;
                case FacilityPendingChoiceTypes.ChooseFiveBasicResources:
                    result = ResolveFiveResources(state, command, pending);
                    break;
                case FacilityPendingChoiceTypes.ReplaceOneInfluence:
                    result = ResolveReplaceInfluence(state, command, pending);
                    break;
                case FacilityPendingChoiceTypes.DeployTwoInfluences:
                    result = ResolveDeployInfluences(state, command, pending);
                    break;
                case FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore:
                    result = ResolveWarehouse(state, command, pending);
                    break;
                default:
                    return Invalid(CommandErrorCode.PendingChoiceRequired, "当前待选项不属于设施入场效果。");
            }

            return result;
        }

        private CommandResult ResolveCopyAdjacent(
            GameState state,
            GameCommand command,
            PendingCardSessionState pending)
        {
            var optionId = ResolveOptionId(command, pending);
            if (!pending.OptionIds.Contains(optionId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "所选相邻设施已经不可用。");
            }

            int targetSlotIndex;
            if (!int.TryParse(optionId, out targetSlotIndex))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "相邻设施槽位无效。");
            }

            var placement = FindFacilityPlacement(state, command.PlayerId, targetSlotIndex);
            FacilityCardDefinition target;
            if (placement == null ||
                !FacilityCardDatabase.TryGet(placement.FacilityCardId, out target) ||
                !target.HasEntryEffect ||
                string.Equals(target.Color, "rainbow", StringComparison.OrdinalIgnoreCase))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "只能重放十字相邻的非彩色设施入场效果。");
            }

            ClearFacilityPending(state);
            entryEffectService.Resolve(state, state.FindPlayer(command.PlayerId), target, targetSlotIndex);
            return Success(command.PlayerId, pending.CardId, "已重新结算相邻设施的入场效果。");
        }

        private CommandResult ResolveAdditionalBuild(
            GameState state,
            GameCommand command,
            PendingCardSessionState pending)
        {
            var facilityId = ResolveOptionId(command, pending);
            if (!pending.OptionIds.Contains(facilityId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "额外建设所选设施不在原有可选供应中。");
            }

            int slotIndex;
            if (!int.TryParse(GetParameter(command, BuildFacilityCommandHandler.CityBoardSlotIndexParameter), out slotIndex))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "额外建设必须指定城市面板槽位。");
            }

            var paymentMode = GetParameter(command, BuildFacilityCommandHandler.PaymentModeParameter);
            var savedPending = pending;
            ClearFacilityPending(state);
            var buildResult = buildFacilityService.Build(
                state,
                command.PlayerId,
                facilityId,
                slotIndex,
                paymentMode);
            if (!buildResult.Succeeded)
            {
                state.PendingCardSession = savedPending;
                return CommandResult.Invalid(buildResult.Validation);
            }

            return Success(command.PlayerId, buildResult.Facility.FacilityId, "已执行简陋工程营提供的额外建设。");
        }

        private CommandResult ResolveExtensionHub(
            GameState state,
            GameCommand command,
            PendingCardSessionState pending)
        {
            var optionId = ResolveOptionId(command, pending);
            if (!pending.OptionIds.Contains(optionId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "延伸枢纽选项无效。");
            }

            if (optionId == FacilityPendingChoiceTypes.SkipOption)
            {
                ClearFacilityPending(state);
                return Success(command.PlayerId, pending.CardId, "已放弃建造延伸枢纽。");
            }

            int slotIndex;
            if (!int.TryParse(GetParameter(command, BuildFacilityCommandHandler.CityBoardSlotIndexParameter), out slotIndex))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "延伸枢纽槽位无效。");
            }

            var buildResult = buildFacilityService.BuildReserveForFree(
                state,
                command.PlayerId,
                optionId,
                slotIndex);
            if (!buildResult.Succeeded)
            {
                return CommandResult.Invalid(buildResult.Validation);
            }

            ClearFacilityPending(state);
            return Success(command.PlayerId, optionId, "已免费建造延伸枢纽。");
        }

        private CommandResult ResolveSellResources(
            GameState state,
            GameCommand command,
            PendingCardSessionState pending)
        {
            var optionId = ResolveOptionId(command, pending);
            if (!pending.OptionIds.Contains(optionId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "贸易街区选项无效。");
            }

            if (optionId == FacilityPendingChoiceTypes.SkipOption)
            {
                ClearFacilityPending(state);
                return Success(command.PlayerId, pending.CardId, "已放弃出售资源。");
            }

            int originium;
            int shard;
            int iron;
            int pure;
            if (!TryReadInteger(command, OriginiumAmountParameter, out originium) ||
                !TryReadInteger(command, OriginiumShardAmountParameter, out shard) ||
                !TryReadInteger(command, IronAmountParameter, out iron) ||
                !TryReadInteger(command, PureOriginiumAmountParameter, out pure))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "出售数量必须是非负整数。");
            }

            var player = state.FindPlayer(command.PlayerId);
            var sale = resourceSaleService.Sell(
                player.Resources,
                new ResourceSaleRequest(originium, shard, iron, pure));
            if (!sale.Succeeded)
            {
                if (sale.FailureKind == ResourceSaleFailureKind.InvalidAmount)
                {
                    return Invalid(CommandErrorCode.InvalidTarget, "出售数量必须是非负整数。");
                }

                return Invalid(CommandErrorCode.InsufficientResource, "出售数量超过玩家现有资源。");
            }

            ClearFacilityPending(state);
            return Success(command.PlayerId, pending.CardId, "贸易街区已完成资源出售。");
        }

        private CommandResult ResolveFreeCityMove(
            GameState state,
            GameCommand command,
            PendingCardSessionState pending)
        {
            var targetLocationId = GetParameter(command, TargetLocationIdParameter);
            if (string.IsNullOrEmpty(targetLocationId))
            {
                targetLocationId = command.TargetId;
            }

            CityMovementResult movementResult;
            var result = moveCityCommandHandler.HandleGrantedMove(
                state,
                command,
                targetLocationId,
                out movementResult);
            if (!result.Succeeded)
            {
                return result;
            }

            TryPlaceRouteInfluence(
                state,
                command.PlayerId,
                movementResult.RouteId,
                GetParameter(command, RouteInfluenceSlotIdParameter));
            if (state.PendingCardSession == pending)
            {
                ClearFacilityPending(state);
            }

            var events = new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.ChoiceResolved,
                    PlayerId = command.PlayerId,
                    SubjectId = pending.CardId,
                    Message = "高性能动力设施已选择免费城市移动。"
                }
            };
            events.AddRange(result.Events);
            return CommandResult.SuccessResult(events, "已执行高性能动力设施的免费城市移动。");
        }

        private static CommandResult ResolveFiveResources(
            GameState state,
            GameCommand command,
            PendingCardSessionState pending)
        {
            int originium;
            int shard;
            int iron;
            if (!TryReadNonNegative(command, OriginiumAmountParameter, out originium) ||
                !TryReadNonNegative(command, OriginiumShardAmountParameter, out shard) ||
                !TryReadNonNegative(command, IronAmountParameter, out iron) ||
                originium + shard + iron != 5)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "开采电铲必须在三种基础资源之间恰好分配 5 个。");
            }

            var player = state.FindPlayer(command.PlayerId);
            player.Resources.Originium += originium;
            player.Resources.OriginiumShard += shard;
            player.Resources.Iron += iron;
            ClearFacilityPending(state);
            return Success(command.PlayerId, pending.CardId, "开采电铲已分配 5 个基础资源。");
        }

        private CommandResult ResolveReplaceInfluence(
            GameState state,
            GameCommand command,
            PendingCardSessionState pending)
        {
            var optionId = ResolveOptionId(command, pending);
            if (!pending.OptionIds.Contains(optionId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "佣兵指挥部分支无效或已经不可用。");
            }

            if (optionId == FacilityPendingChoiceTypes.SkipOption)
            {
                ClearFacilityPending(state);
                return Success(command.PlayerId, pending.CardId, "佣兵指挥部没有合法目标，已完成入场结算。");
            }

            var targetSlotId = GetParameter(command, TargetInfluenceSlotIdParameter);
            if (string.IsNullOrEmpty(targetSlotId))
            {
                targetSlotId = command.TargetId;
            }

            if (optionId == FacilityPendingChoiceTypes.ReplaceInfluenceOption)
            {
                var replacement = facilityInfluenceEffectService.ReplaceOnce(
                    state,
                    command.PlayerId,
                    targetSlotId);
                if (!replacement.Succeeded)
                {
                    return CommandResult.Invalid(replacement.Validation);
                }

                ClearFacilityPending(state);
                var replaceMessage = replacement.ReplacementPlaced
                    ? "已替换目标影响力。"
                    : "已移除目标影响力；受放置限制影响，未放置自己的影响力。";
                return Success(command.PlayerId, pending.CardId, replaceMessage);
            }

            if (optionId == FacilityPendingChoiceTypes.DeployInfluenceOption)
            {
                var placement = influenceService.Place(state, command.PlayerId, targetSlotId);
                if (!placement.Succeeded)
                {
                    return CommandResult.Invalid(placement.Validation);
                }

                ClearFacilityPending(state);
                return Success(command.PlayerId, pending.CardId, "佣兵指挥部已放置 1 个影响力。");
            }

            return Invalid(CommandErrorCode.InvalidTarget, "佣兵指挥部分支无效。");
        }

        private CommandResult ResolveDeployInfluences(
            GameState state,
            GameCommand command,
            PendingCardSessionState pending)
        {
            var slots = SplitIds(GetParameter(command, InfluenceSlotIdsParameter));
            if (slots.Count != 2)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "护航调度中心必须指定两个不同的部署槽位。");
            }

            var placement = influenceService.PlaceAtomically(state, command.PlayerId, slots);
            if (!placement.Succeeded)
            {
                return CommandResult.Invalid(placement.Validation);
            }

            ClearFacilityPending(state);
            return Success(command.PlayerId, pending.CardId, "护航调度中心已部署 2 个影响力。");
        }

        private CommandResult ResolveWarehouse(
            GameState state,
            GameCommand command,
            PendingCardSessionState pending)
        {
            var optionId = ResolveOptionId(command, pending);
            if (!pending.OptionIds.Contains(optionId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "载具仓库分支无效。");
            }

            if (optionId == FacilityPendingChoiceTypes.SkipOption)
            {
                ClearFacilityPending(state);
                return Success(command.PlayerId, pending.CardId, "载具仓库没有合法目标，已完成入场结算。");
            }

            if (optionId == FacilityPendingChoiceTypes.ExploreOption)
            {
                return BeginWarehouseExplore(state, command, pending);
            }

            var removeSlotId = GetParameter(command, RemoveInfluenceSlotIdParameter);
            var sourceSlotId = GetParameter(command, SourceInfluenceSlotIdParameter);
            var targetSlotId = GetParameter(command, TargetInfluenceSlotIdParameter);
            if (string.IsNullOrEmpty(removeSlotId) ||
                string.IsNullOrEmpty(sourceSlotId) ||
                string.IsNullOrEmpty(targetSlotId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "载具仓库必须同时选择要移除的影响力和一次调度。");
            }

            var operation = influenceService.RemoveThenMoveAtomically(
                state,
                command.PlayerId,
                removeSlotId,
                new InfluenceMoveRequest(sourceSlotId, targetSlotId));
            if (!operation.Succeeded)
            {
                return CommandResult.Invalid(operation.Validation);
            }

            ClearFacilityPending(state);
            return Success(command.PlayerId, pending.CardId, "载具仓库已完成移除与调度。");
        }

        private CommandResult BeginWarehouseExplore(
            GameState state,
            GameCommand command,
            PendingCardSessionState pending)
        {
            var targetLocationId = GetParameter(command, TargetLocationIdParameter);
            if (string.IsNullOrEmpty(targetLocationId))
            {
                targetLocationId = command.TargetId;
            }

            var result = exploreLocationCommandHandler.BeginGrantedExplore(
                state,
                command,
                targetLocationId);
            if (!result.Succeeded)
            {
                return result;
            }

            var events = new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.ChoiceResolved,
                    PlayerId = command.PlayerId,
                    SubjectId = pending.CardId,
                    Message = "载具仓库已选择执行探索。"
                }
            };
            events.AddRange(result.Events);
            return CommandResult.SuccessResult(events, "载具仓库已开始一次正常探索。");
        }

        private void TryPlaceRouteInfluence(GameState state, int playerId, string routeId, string requestedSlotId)
        {
            if (string.IsNullOrEmpty(routeId))
            {
                return;
            }

            if (!string.IsNullOrEmpty(requestedSlotId) && IsSlotOnRoute(requestedSlotId, routeId))
            {
                if (influenceService.Place(state, playerId, requestedSlotId).Succeeded)
                {
                    return;
                }
            }

            MapRouteDefinition route;
            try
            {
                route = mapQuery.GetRoute(routeId);
            }
            catch (ArgumentException)
            {
                return;
            }

            for (var i = 0; i < route.InfluenceSlotCount; i++)
            {
                if (influenceService.Place(state, playerId, InfluenceService.GetRouteSlotId(routeId, i)).Succeeded)
                {
                    return;
                }
            }
        }

        private static bool IsSlotOnRoute(string slotId, string routeId)
        {
            return slotId.StartsWith("route:" + routeId + ":", StringComparison.Ordinal);
        }

        private static FacilityPlacement FindFacilityPlacement(GameState state, int playerId, int slotIndex)
        {
            for (var i = 0; i < state.Map.Facilities.Count; i++)
            {
                var placement = state.Map.Facilities[i];
                if (placement.PlayerId == playerId && placement.CityBoardSlotIndex == slotIndex)
                {
                    return placement;
                }
            }

            return null;
        }

        private static string ResolveOptionId(GameCommand command, PendingCardSessionState pending)
        {
            var optionId = GetParameter(command, OptionIdParameter);
            if (string.IsNullOrEmpty(optionId) && command.OptionIds != null && command.OptionIds.Count > 0)
            {
                optionId = command.OptionIds[0];
            }

            if (string.IsNullOrEmpty(optionId) && pending.OptionIds.Count == 1)
            {
                optionId = pending.OptionIds[0];
            }

            return optionId;
        }

        private static bool TryReadNonNegative(GameCommand command, string parameter, out int value)
        {
            var encoded = GetParameter(command, parameter);
            if (string.IsNullOrEmpty(encoded))
            {
                value = 0;
                return true;
            }

            return int.TryParse(encoded, out value) && value >= 0;
        }

        private static bool TryReadInteger(GameCommand command, string parameter, out int value)
        {
            var encoded = GetParameter(command, parameter);
            if (string.IsNullOrEmpty(encoded))
            {
                value = 0;
                return true;
            }

            return int.TryParse(encoded, out value);
        }

        private static List<string> SplitIds(string encoded)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(encoded))
            {
                return result;
            }

            var parts = encoded.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var value = parts[i].Trim();
                if (!string.IsNullOrEmpty(value))
                {
                    result.Add(value);
                }
            }

            return result;
        }

        private static void ClearFacilityPending(GameState state)
        {
            CardFlowStateAdapter.ClearPendingSession(state);
        }

        private static string GetParameter(GameCommand command, string key)
        {
            if (command == null || command.Parameters == null)
            {
                return string.Empty;
            }

            string value;
            return command.Parameters.TryGetValue(key, out value) ? value ?? string.Empty : string.Empty;
        }

        private static CommandResult Success(int playerId, string subjectId, string message)
        {
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.ChoiceResolved,
                    PlayerId = playerId,
                    SubjectId = subjectId ?? string.Empty,
                    Message = message
                }
            }, message);
        }

        private static CommandResult Invalid(CommandErrorCode code, string reason)
        {
            return CommandResult.Invalid(ValidationResult.Failure(code, reason));
        }
    }
}
