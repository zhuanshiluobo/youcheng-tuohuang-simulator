using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Commands;
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
        private readonly CityMovementService cityMovementService;
        private readonly ExplorationService explorationService;
        private readonly IMapQueryService mapQuery;

        public ResolveFacilityEffectCommandHandler(
            BuildFacilityService buildFacilityService,
            FacilityEntryEffectService entryEffectService,
            InfluenceService influenceService,
            CityMovementService cityMovementService,
            ExplorationService explorationService,
            IMapQueryService mapQuery)
        {
            this.buildFacilityService = buildFacilityService ?? throw new ArgumentNullException(nameof(buildFacilityService));
            this.entryEffectService = entryEffectService ?? throw new ArgumentNullException(nameof(entryEffectService));
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
            facilityInfluenceEffectService = new FacilityInfluenceEffectService(this.influenceService);
            this.cityMovementService = cityMovementService ?? throw new ArgumentNullException(nameof(cityMovementService));
            this.explorationService = explorationService ?? throw new ArgumentNullException(nameof(explorationService));
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
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

        private static CommandResult ResolveExtensionHub(
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
            if (!int.TryParse(GetParameter(command, BuildFacilityCommandHandler.CityBoardSlotIndexParameter), out slotIndex) ||
                slotIndex < 0 ||
                slotIndex >= BuildFacilityService.CityBoardSlotCount)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "延伸枢纽槽位无效。");
            }

            if (FindFacilityPlacement(state, command.PlayerId, slotIndex) != null)
            {
                return Invalid(CommandErrorCode.OccupiedSlot, "延伸枢纽目标槽位已被占用。");
            }

            if (IsFacilityBuilt(state, optionId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "该颜色的延伸枢纽已经被建设。");
            }

            var extension = FacilityCardDatabase.Get(optionId);
            if (extension == null || !extension.ReserveOnly)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "延伸枢纽储备牌不存在。");
            }

            var player = state.FindPlayer(command.PlayerId);
            player.BuiltFacilityIds.Add(optionId);
            player.Score += extension.Score;
            state.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = command.PlayerId,
                FacilityCardId = optionId,
                CityBoardSlotIndex = slotIndex
            });
            ClearFacilityPending(state);
            return Success(command.PlayerId, optionId, "已免费建造延伸枢纽。");
        }

        private static CommandResult ResolveSellResources(
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
            if (!TryReadNonNegative(command, OriginiumAmountParameter, out originium) ||
                !TryReadNonNegative(command, OriginiumShardAmountParameter, out shard) ||
                !TryReadNonNegative(command, IronAmountParameter, out iron) ||
                !TryReadNonNegative(command, PureOriginiumAmountParameter, out pure))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "出售数量必须是非负整数。");
            }

            var player = state.FindPlayer(command.PlayerId);
            if (originium > player.Resources.Originium ||
                shard > player.Resources.OriginiumShard ||
                iron > player.Resources.Iron ||
                pure > player.Resources.PureOriginium)
            {
                return Invalid(CommandErrorCode.InsufficientResource, "出售数量超过玩家现有资源。");
            }

            player.Resources.Originium -= originium;
            player.Resources.OriginiumShard -= shard;
            player.Resources.Iron -= iron;
            player.Resources.PureOriginium -= pure;
            player.Resources.GoldVoucher += originium * 3 + shard * 3 + iron * 4 + pure * 15;
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

            var eventOptionIndex = ReadOptionalInt(command, ExploreLocationCommandHandler.EventOptionIdParameter, -1);
            var eventInfluenceSlots = SplitIds(GetParameter(command, ExploreLocationCommandHandler.EventInfluenceSlotIdsParameter));
            var result = cityMovementService.MoveCityForFacility(
                state,
                command.PlayerId,
                targetLocationId,
                eventOptionIndex,
                eventInfluenceSlots,
                command.CommandId);
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            TryPlaceRouteInfluence(state, command.PlayerId, result.RouteId, GetParameter(command, RouteInfluenceSlotIdParameter));
            if (state.PendingCardSession == pending)
            {
                ClearFacilityPending(state);
            }

            return Success(command.PlayerId, pending.CardId, "已执行高性能动力设施的免费城市移动。");
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
            var targetSlotId = ResolveOptionId(command, pending);
            if (!pending.OptionIds.Contains(targetSlotId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "替换目标已经不可用。");
            }

            var result = facilityInfluenceEffectService.ReplaceOnce(state, command.PlayerId, targetSlotId);
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            ClearFacilityPending(state);
            var message = result.ReplacementPlaced
                ? "已替换目标影响力。"
                : "已移除目标影响力；受放置限制影响，未放置自己的影响力。";
            return Success(command.PlayerId, pending.CardId, message);
        }

        private CommandResult ResolveDeployInfluences(
            GameState state,
            GameCommand command,
            PendingCardSessionState pending)
        {
            var slots = SplitIds(GetParameter(command, InfluenceSlotIdsParameter));
            if (slots.Count == 0 || slots.Count > 2)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "护航调度中心需要依次指定一至两个部署槽位。");
            }

            var placed = 0;
            for (var i = 0; i < slots.Count && i < 2; i++)
            {
                var result = influenceService.Place(state, command.PlayerId, slots[i]);
                if (result.Succeeded)
                {
                    placed += 1;
                }
            }

            if (placed == 0)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "所选槽位均不能部署影响力。");
            }

            ClearFacilityPending(state);
            return Success(command.PlayerId, pending.CardId, "护航调度中心已部署 " + placed + " 个影响力。");
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

            if (optionId == FacilityPendingChoiceTypes.ExploreOption)
            {
                return BeginWarehouseExplore(state, command, pending);
            }

            var succeeded = false;
            var removeSlotId = GetParameter(command, RemoveInfluenceSlotIdParameter);
            if (!string.IsNullOrEmpty(removeSlotId))
            {
                succeeded |= influenceService.Remove(state, removeSlotId).Succeeded;
            }

            var sourceSlotId = GetParameter(command, SourceInfluenceSlotIdParameter);
            var targetSlotId = GetParameter(command, TargetInfluenceSlotIdParameter);
            if (!string.IsNullOrEmpty(sourceSlotId) && !string.IsNullOrEmpty(targetSlotId))
            {
                succeeded |= influenceService.Move(state, command.PlayerId, sourceSlotId, targetSlotId).Succeeded;
            }

            if (!succeeded)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "移除与调度均无法执行。");
            }

            ClearFacilityPending(state);
            return Success(command.PlayerId, pending.CardId, "载具仓库已尽可能执行移除与调度。");
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

            MapPath path;
            var routeIds = SplitIds(GetParameter(command, ExploreLocationCommandHandler.RouteIdsParameter));
            if (routeIds.Count > 0)
            {
                path = new MapPath
                {
                    RouteIds = routeIds,
                    LocationIds = SplitIds(GetParameter(command, ExploreLocationCommandHandler.PathLocationIdsParameter))
                };
            }
            else
            {
                try
                {
                    path = explorationService.FindDefaultPath(state, command.PlayerId, targetLocationId);
                }
                catch (ArgumentException)
                {
                    path = null;
                }
            }

            if (path == null)
            {
                return Invalid(CommandErrorCode.NoRoute, "无法为载具仓库探索建立路径。");
            }

            Dictionary<string, int> paymentRecipients;
            if (!TryParsePaymentRecipients(
                    GetParameter(command, ExploreLocationCommandHandler.PaymentRecipientsParameter),
                    out paymentRecipients))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "探索路费接收者参数无效。");
            }

            var result = explorationService.BeginExploreEvent(
                state,
                command.PlayerId,
                targetLocationId,
                path,
                GetParameter(command, ExploreLocationCommandHandler.InfluenceSlotIdParameter),
                paymentRecipients,
                command.CommandId,
                true);
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            return Success(command.PlayerId, pending.CardId, "载具仓库已开始一次正常探索。");
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

        private static bool IsFacilityBuilt(GameState state, string facilityId)
        {
            for (var i = 0; i < state.Map.Facilities.Count; i++)
            {
                if (state.Map.Facilities[i].FacilityCardId == facilityId)
                {
                    return true;
                }
            }

            return false;
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

        private static int ReadOptionalInt(GameCommand command, string parameter, int fallback)
        {
            int value;
            return int.TryParse(GetParameter(command, parameter), out value) ? value : fallback;
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

        private static bool TryParsePaymentRecipients(string encoded, out Dictionary<string, int> recipients)
        {
            recipients = new Dictionary<string, int>();
            if (string.IsNullOrEmpty(encoded))
            {
                return true;
            }

            var entries = encoded.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < entries.Length; i++)
            {
                var parts = entries[i].Split(new[] { '=', ':' }, StringSplitOptions.RemoveEmptyEntries);
                int playerId;
                if (parts.Length != 2 || !int.TryParse(parts[1].Trim(), out playerId))
                {
                    return false;
                }

                recipients[parts[0].Trim()] = playerId;
            }

            return true;
        }

        private static void ClearFacilityPending(GameState state)
        {
            state.PendingCardSession = null;
            state.PendingChoice = null;
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
