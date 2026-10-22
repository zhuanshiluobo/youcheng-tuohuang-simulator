using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.CardFlows;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Effects;
using YC.Domain.Exploration;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Application.Gameplay
{
    public sealed class ExploreLocationCommandHandler : IGameCommandHandler
    {
        public const string RouteIdsParameter = "routeIds";
        public const string PathLocationIdsParameter = "pathLocationIds";
        public const string EventOptionIdParameter = "eventOptionId";
        public const string InfluenceSlotIdParameter = "influenceSlotId";
        public const string EventInfluenceSlotIdParameter = "eventInfluenceSlotId";
        public const string EventInfluenceSlotIdsParameter = "eventInfluenceSlotIds";
        public const string PaymentRecipientsParameter = "paymentRecipients";
        public const string ExploreEventChoiceType = "explore_event";

        private readonly ExplorationService explorationService;
        private readonly RoundAdvanceService roundAdvanceService;
        private readonly EffectRegistry effectRegistry;

        public ExploreLocationCommandHandler(ExplorationService explorationService)
            : this(explorationService, CreateRoundAdvanceService(explorationService))
        {
        }

        public ExploreLocationCommandHandler(
            ExplorationService explorationService,
            RoundAdvanceService roundAdvanceService)
        {
            this.explorationService = explorationService ?? throw new ArgumentNullException(nameof(explorationService));
            this.roundAdvanceService = roundAdvanceService ?? throw new ArgumentNullException(nameof(roundAdvanceService));
            effectRegistry = roundAdvanceService.Execution.EffectRegistry;
            ExplorationActionEffectExecutor.Register(effectRegistry, explorationService.MapQuery);
        }

        public ExploreLocationCommandHandler(
            ExplorationService explorationService,
            RoundAdvanceService roundAdvanceService,
            EffectRegistry effectRegistry)
            : this(explorationService, roundAdvanceService)
        {
            this.effectRegistry = effectRegistry ?? throw new ArgumentNullException(nameof(effectRegistry));
            ExplorationActionEffectExecutor.Register(this.effectRegistry, explorationService.MapQuery);
        }

        private static RoundAdvanceService CreateRoundAdvanceService(ExplorationService explorationService)
        {
            if (explorationService == null) throw new ArgumentNullException(nameof(explorationService));
            var registry = new EffectRegistry();
            RouteResourceSelectionEffectExecutor.Register(registry, explorationService.MapQuery);
            return new RoundAdvanceService(new RoundExecutionService(registry));
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null &&
                   (command.Kind == GameCommandKind.ExploreLocation ||
                    command.Kind == GameCommandKind.ResolvePendingChoice);
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            if (command.Kind == GameCommandKind.ResolvePendingChoice)
                return HandleResolveExploreEvent(state, command);
            return HandleConfirmedExploreSelection(state, command);
        }

        private CommandResult HandleConfirmedExploreSelection(GameState state, GameCommand command)
        {
            var guard = MainActionCommandGuard.Validate(state, command);
            if (!guard.IsValid) return CommandResult.Invalid(guard);
            if (string.IsNullOrEmpty(command.TargetId) || !string.IsNullOrEmpty(command.SourceId) || command.OptionIds?.Count > 0)
                return Invalid(CommandErrorCode.InvalidTarget, "exploration_preselection_arguments_invalid");
            if (command.Parameters != null)
                foreach (var key in command.Parameters.Keys)
                    if (key != RouteIdsParameter && key != PathLocationIdsParameter && key != PaymentRecipientsParameter &&
                        !key.StartsWith("payment:", StringComparison.Ordinal))
                        return Invalid(CommandErrorCode.InvalidTarget, "exploration_preselection_arguments_invalid");
            var service = new RouteResourceSelectionService(explorationService.MapQuery);
            var validation = service.TryCreateExplorationPreview(state, command.PlayerId, out var session, out var draft, out _);
            if (!validation.IsValid) return CommandResult.Invalid(validation);
            validation = ParseSelectedIds(GetParameter(command, RouteIdsParameter), out var routeIds);
            if (!validation.IsValid) return CommandResult.Invalid(validation);
            validation = ParseSelectedRecipients(command, out var recipients);
            if (!validation.IsValid) return CommandResult.Invalid(validation);
            draft = draft.WithSelection(routeIds, new[] { command.TargetId }, recipients);
            var projection = service.Query(state, session, draft, session.SelectableLocationUpperBound);
            if (!projection.Validation.IsValid) return CommandResult.Invalid(projection.Validation);
            if (projection.Preview == null) return Invalid(CommandErrorCode.InvalidTarget, "route_selection_target_required");
            if (!projection.Preview.FinalizationValidation.IsValid) return CommandResult.Invalid(projection.Preview.FinalizationValidation);
            validation = ValidateSelectedPath(GetParameter(command, PathLocationIdsParameter), session, command.TargetId, projection);
            if (!validation.IsValid) return CommandResult.Invalid(validation);

            var registry = effectRegistry ?? roundAdvanceService.Execution.EffectRegistry;
            if (!registry.TryGet(ExplorationEffectTypeIds.Explore, out _))
                return Invalid(CommandErrorCode.InvalidTarget, "exploration_preselection_executor_missing");
            ExplorationActionEffectExecutor.Register(registry, explorationService.MapQuery);
            var executor = new EffectExecutor(state, registry);
            var spec = ExplorationActionEffectExecutor.Create(command.PlayerId, command.TargetId, routeIds, recipients, command.CommandId);
            if (!executor.TryCreatePlayerActionEffect(command.PlayerId, spec, string.Empty, command.CommandId, out var nodeId))
                return Invalid(CommandErrorCode.InvalidTarget, executor.LastDiagnostic);
            var report = executor.RunUntilQuiescent();
            if (report.Faulted) return Invalid(CommandErrorCode.InvalidTarget, report.FaultCode);
            var node = state.EffectRuntime.EffectNodes.Find(value => value.EffectId == nodeId);
            if (node == null || node.Status == EffectNodeStatus.Failed || node.PendingOutcome == EffectPendingOutcome.Failed)
                return Invalid(CommandErrorCode.InvalidTarget, node?.FailureReason ?? "exploration_preselection_node_missing");
            return CommandResult.SuccessResult(new List<GameEvent>(), string.Empty);
        }

        private static ValidationResult ParseSelectedIds(string encoded, out List<string> ids)
        {
            ids = new List<string>();
            if (string.IsNullOrEmpty(encoded)) return ValidationResult.Success;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in encoded.Split(new[] { ',', ';', '|' }))
            {
                var id = part.Trim();
                if (id.Length == 0 || !seen.Add(id)) return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "route_selection_ids_invalid");
                ids.Add(id);
            }
            return ValidationResult.Success;
        }

        private static ValidationResult ParseSelectedRecipients(GameCommand command, out List<RouteRecipientChoice> recipients)
        {
            recipients = new List<RouteRecipientChoice>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var encoded = GetParameter(command, PaymentRecipientsParameter);
            if (!string.IsNullOrEmpty(encoded))
                foreach (var entry in encoded.Split(new[] { ';', '|' }))
                {
                    var parts = entry.Split(new[] { '=', ':' });
                    if (parts.Length != 2 || !AddSelectedRecipient(parts[0], parts[1], seen, recipients))
                        return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "route_selection_recipient_structure");
                }
            if (command.Parameters != null)
                foreach (var entry in command.Parameters)
                    if (entry.Key.StartsWith("payment:", StringComparison.Ordinal) &&
                        !AddSelectedRecipient(entry.Key.Substring("payment:".Length), entry.Value, seen, recipients))
                        return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "route_selection_recipient_structure");
            return ValidationResult.Success;
        }

        private static bool AddSelectedRecipient(string routeId, string encodedPlayer, HashSet<string> seen, List<RouteRecipientChoice> recipients)
        {
            routeId = routeId.Trim();
            if (routeId.Length == 0 || !seen.Add(routeId) ||
                !int.TryParse(encodedPlayer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var playerId) ||
                (playerId != -1 && playerId <= 0)) return false;
            recipients.Add(new RouteRecipientChoice(routeId, playerId == -1 ? "bank" : "player:" + playerId.ToString(CultureInfo.InvariantCulture)));
            return true;
        }

        // 可省略兼容路径字段；提交时只作为封存连通图中的见证，不能改变费用或自动补收费路线。
        private static ValidationResult ValidateSelectedPath(string encoded, RouteSelectionSession session, string target, RouteSelectionProjection projection)
        {
            var parsed = ParseSelectedIds(encoded, out var locations);
            if (!parsed.IsValid) return parsed;
            if (locations.Count == 0) return ValidationResult.Success;
            if (locations[0] != session.StartLocationId || locations[locations.Count - 1] != target)
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "route_selection_path_invalid");
            var effective = new HashSet<string>(projection.Preview.EffectiveRouteIds, StringComparer.Ordinal);
            for (int i = 1; i < locations.Count; i++)
            {
                bool connected = false;
                foreach (var route in projection.RouteOptions)
                    if (effective.Contains(route.RouteId) && route.CoveredLocationIds.Contains(locations[i - 1]) && route.CoveredLocationIds.Contains(locations[i]))
                    { connected = true; break; }
                if (!connected) return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "route_selection_path_invalid");
            }
            return ValidationResult.Success;
        }
        /// <summary>
        /// 由设施、角色牌等额外效果赠送探索时复用正常探索的路径、路费与事件投影，
        /// 但不额外消耗一次主要行动。
        /// </summary>
        public CommandResult BeginGrantedExplore(
            GameState state,
            GameCommand sourceCommand,
            string targetLocationId)
        {
            if (sourceCommand == null)
            {
                throw new ArgumentNullException(nameof(sourceCommand));
            }

            if (state == null) return Invalid(CommandErrorCode.InvalidTarget, "探索状态为空。");
            if (string.IsNullOrEmpty(state.MapId) || !string.Equals(state.MapId, explorationService.MapQuery.Map?.MapId, StringComparison.Ordinal))
                return Invalid(CommandErrorCode.InvalidTarget, "route_selection_map_mismatch");
            var eligible = explorationService.GetSelectableLocationIds(state, sourceCommand.PlayerId, true);
            if (!string.IsNullOrEmpty(targetLocationId) && !ContainsLocation(eligible, targetLocationId))
                return Invalid(CommandErrorCode.InvalidTarget, "赠送探索目标当前不满足资格。");
            if (eligible.Count == 0) return Invalid(CommandErrorCode.InvalidTarget, "当前没有满足资格的探索资源点。");
            var legacyFacility = state.PendingCardSession;
            if (state.HasPendingChoice() && (legacyFacility == null || legacyFacility.ScenarioId != YC.Domain.Facilities.FacilityPendingChoiceTypes.ScenarioId || legacyFacility.PlayerId != sourceCommand.PlayerId))
                return Invalid(CommandErrorCode.PendingChoiceRequired, "请先处理当前待选择项。");
            EffectExecutor executor;
            if (effectRegistry != null)
            {
                RouteResourceSelectionEffectExecutor.Register(effectRegistry, explorationService.MapQuery);
                executor = new EffectExecutor(state, effectRegistry);
            }
            else executor = roundAdvanceService.Execution.CreateCommandExecutor(state);
            var spec = RouteResourceSelectionEffectExecutor.CreateExploration(sourceCommand.PlayerId, sourceCommand.CommandId, false, targetLocationId);
            if (!executor.TryCreatePlayerActionEffect(sourceCommand.PlayerId, spec, string.Empty, sourceCommand.CommandId, out var nodeId))
                return Invalid(CommandErrorCode.InvalidTarget, executor.LastDiagnostic);
            if (legacyFacility != null)
            {
                state.PendingCardSession = null;
                state.PendingChoice = null;
            }
            var report = executor.RunUntilQuiescent();
            if (report.Faulted) return Invalid(CommandErrorCode.InvalidTarget, "赠送探索选择流程无法继续：" + report.FaultCode);
            var selectionNode = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == nodeId);
            if (selectionNode?.Status == EffectNodeStatus.Failed) return Invalid(CommandErrorCode.InvalidTarget, selectionNode.FailureReason);
            return CommandResult.SuccessResult(new List<GameEvent>(), "已开始赠送探索选路。");
        }

        private static bool ContainsLocation(IReadOnlyList<string> locations, string target)
        {
            foreach (var location in locations) if (location == target) return true;
            return false;
        }

        private CommandResult HandleBeginExploreEvent(
            GameState state,
            GameCommand command,
            string targetLocationId,
            MapPath path,
            bool allowExistingPendingChoice,
            bool consumeMainAction)
        {
            var influenceSlotId = GetParameter(command, InfluenceSlotIdParameter);
            Dictionary<string, int> paymentRecipients;
            var paymentRecipientsValidation = ResolvePaymentRecipients(command, out paymentRecipients);
            if (!paymentRecipientsValidation.IsValid)
            {
                return CommandResult.Invalid(paymentRecipientsValidation);
            }

            var result = explorationService.BeginExploreEvent(
                state,
                command.PlayerId,
                targetLocationId,
                path,
                influenceSlotId,
                paymentRecipients,
                command.CommandId,
                allowExistingPendingChoice,
                consumeMainAction);
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            var card = EventCardDatabase.Get(state, result.EventCardId);
            var message = "Player " + command.PlayerId + " began exploring " + result.TargetLocationId + ".";
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.CardMoved,
                    PlayerId = command.PlayerId,
                    SubjectId = result.EventCardId,
                    Message = "Exploration event revealed " + card.Name + ".",
                    Data =
                    {
                        { "cardName", card.Name },
                        { "cardDescription", card.Description },
                        { "eventColor", result.EventColor.ToString() },
                        { "targetLocationId", result.TargetLocationId }
                    }
                },
                new GameEvent
                {
                    Kind = GameEventKind.ResourceChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = result.TargetLocationId,
                    Message = "Exploration placed resource token."
                },
                new GameEvent
                {
                    Kind = GameEventKind.ChoiceOpened,
                    PlayerId = command.PlayerId,
                    SubjectId = result.EventCardId,
                    Message = "Exploration event choice opened for " + card.Name + ".",
                    Data =
                    {
                        { "cardName", card.Name },
                        { "cardDescription", card.Description },
                        { "targetLocationId", result.TargetLocationId }
                    }
                }
            }, message);
        }

        private CommandResult HandleResolveExploreEvent(GameState state, GameCommand command)
        {
            var pendingChoice = CardFlowStateAdapter.GetPendingChoiceView(state);
            if (pendingChoice == null || pendingChoice.ChoiceType != ExploreEventChoiceType)
            {
                return Invalid(CommandErrorCode.PendingChoiceRequired, "当前没有待处理的探索事件。");
            }

            if (pendingChoice.PlayerId != command.PlayerId)
            {
                return Invalid(CommandErrorCode.NotCurrentPlayer, "只有正在探索的玩家可以处理该事件。");
            }

            var selectedOptionId = GetSelectedOptionId(command);
            if (!pendingChoice.OptionIds.Contains(selectedOptionId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "探索事件选项不可用。");
            }

            int selectedOptionIndex;
            if (!int.TryParse(selectedOptionId, out selectedOptionIndex))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "探索事件选项必须是有效选项编号。");
            }

            var consumeMainAction = ExplorationService.PendingExploreConsumesMainAction(state);
            var influenceSlotId = GetParameter(command, InfluenceSlotIdParameter);
            var eventInfluenceSlotIds = ResolveEventInfluenceSlotIds(command);
            var result = explorationService.ResolveExploreEvent(
                state,
                command.PlayerId,
                pendingChoice.TargetId,
                pendingChoice.CardId,
                selectedOptionIndex,
                influenceSlotId,
                eventInfluenceSlotIds);
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            if (consumeMainAction)
            {
                roundAdvanceService.MarkMainActionComplete(state, command.PlayerId);
            }

            var card = EventCardDatabase.Get(state, result.EventCardId);
            var message = "Player " + command.PlayerId + " resolved exploration event " + card.Name + ".";
            var events = new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.ChoiceResolved,
                    PlayerId = command.PlayerId,
                    SubjectId = result.EventCardId,
                    Message = message,
                    Data =
                    {
                        { "cardName", card.Name },
                        { "cardDescription", card.Description },
                        { "selectedOptionIndex", result.SelectedOptionIndex.ToString() },
                        { "pendingEffect", GetPendingEffect(card, result.SelectedOptionIndex) }
                    }
                },
                new GameEvent
                {
                    Kind = GameEventKind.ResourceChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = result.TargetLocationId,
                    Message = "Exploration event granted reward.",
                    Data =
                    {
                        { "rewardOriginium", result.Reward.Originium.ToString() },
                        { "rewardOriginiumShard", result.Reward.OriginiumShard.ToString() },
                        { "rewardIron", result.Reward.Iron.ToString() },
                        { "rewardPureOriginium", result.Reward.PureOriginium.ToString() },
                        { "rewardGoldVoucher", result.Reward.GoldVoucher.ToString() }
                    }
                },
                new GameEvent
                {
                    Kind = GameEventKind.InfluencePlaced,
                    PlayerId = command.PlayerId,
                    SubjectId = result.InfluencePlacement != null ? result.InfluencePlacement.SlotId : string.Empty,
                    Message = "Exploration placed influence at " + result.TargetLocationId + "."
                }
            };

            if (HasScorePendingEffect(card, result.SelectedOptionIndex))
            {
                events.Add(new GameEvent
                {
                    Kind = GameEventKind.ScoreChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = result.EventCardId,
                    Message = "Exploration event changed score."
                });
            }

            return CommandResult.SuccessResult(events, message);
        }

        private ValidationResult ResolvePath(
            GameState state,
            GameCommand command,
            string targetLocationId,
            out MapPath path)
        {
            path = null;
            if (!string.IsNullOrEmpty(GetParameter(command, RouteIdsParameter)))
            {
                path = new MapPath
                {
                    LocationIds = SplitIds(GetParameter(command, PathLocationIdsParameter)),
                    RouteIds = SplitIds(GetParameter(command, RouteIdsParameter))
                };
                return ValidationResult.Success;
            }

            return ValidationResult.Failure(CommandErrorCode.NoRoute, "探索必须由玩家明确确认航道，不能自动补默认路线。");
        }

        private static ValidationResult ResolvePaymentRecipients(
            GameCommand command,
            out Dictionary<string, int> paymentRecipients)
        {
            paymentRecipients = new Dictionary<string, int>();
            var encoded = GetParameter(command, PaymentRecipientsParameter);
            if (!string.IsNullOrEmpty(encoded))
            {
                var entries = encoded.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < entries.Length; i++)
                {
                    var parts = entries[i].Split(new[] { '=', ':' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 || string.IsNullOrEmpty(parts[0].Trim()))
                    {
                        return InvalidPaymentRecipients();
                    }

                    int playerId;
                    if (!int.TryParse(parts[1], out playerId))
                    {
                        return InvalidPaymentRecipients();
                    }

                    paymentRecipients[parts[0].Trim()] = playerId;
                }
            }

            if (command.Parameters != null)
            {
                foreach (var entry in command.Parameters)
                {
                    if (!entry.Key.StartsWith("payment:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int playerId;
                    var routeId = entry.Key.Substring("payment:".Length).Trim();
                    if (string.IsNullOrEmpty(routeId) || !int.TryParse(entry.Value, out playerId))
                    {
                        return InvalidPaymentRecipients();
                    }

                    paymentRecipients[routeId] = playerId;
                }
            }

            return ValidationResult.Success;
        }

        private static List<string> ResolveEventInfluenceSlotIds(GameCommand command)
        {
            var result = SplitIds(GetParameter(command, EventInfluenceSlotIdsParameter));
            var singleSlotId = GetParameter(command, EventInfluenceSlotIdParameter);
            if (!string.IsNullOrEmpty(singleSlotId))
            {
                result.Add(singleSlotId.Trim());
            }

            return result;
        }

        private static bool HasExplicitEventOption(GameCommand command)
        {
            return !string.IsNullOrEmpty(GetParameter(command, EventOptionIdParameter)) ||
                   (command.OptionIds != null && command.OptionIds.Count > 0);
        }

        private static string GetSelectedOptionId(GameCommand command)
        {
            var optionId = GetParameter(command, EventOptionIdParameter);
            if (!string.IsNullOrEmpty(optionId))
            {
                return optionId;
            }

            if (command.OptionIds != null && command.OptionIds.Count > 0)
            {
                return command.OptionIds[0];
            }

            return command.TargetId;
        }

        private static int ResolveSelectedOptionIndex(GameCommand command)
        {
            var optionId = GetParameter(command, EventOptionIdParameter);
            if (string.IsNullOrEmpty(optionId) && command.OptionIds != null && command.OptionIds.Count > 0)
            {
                optionId = command.OptionIds[0];
            }

            if (string.IsNullOrEmpty(optionId))
            {
                return 0;
            }

            int selectedOptionIndex;
            return int.TryParse(optionId, out selectedOptionIndex) ? selectedOptionIndex : -1;
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
                result.Add(parts[i].Trim());
            }

            return result;
        }

        private static string GetParameter(GameCommand command, string key)
        {
            if (command.Parameters == null)
            {
                return string.Empty;
            }

            string value;
            return command.Parameters.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static string GetPendingEffect(EventCardDefinition card, int selectedOptionIndex)
        {
            if (card == null ||
                selectedOptionIndex < 0 ||
                selectedOptionIndex >= card.ChoicePendingEffects.Count)
            {
                return string.Empty;
            }

            return EventEffectUtility.Format(card.ChoicePendingEffects[selectedOptionIndex]);
        }

        private static bool HasScorePendingEffect(EventCardDefinition card, int selectedOptionIndex)
        {
            if (card == null ||
                selectedOptionIndex < 0 ||
                selectedOptionIndex >= card.ChoicePendingEffects.Count)
            {
                return false;
            }

            return EventEffectUtility.HasScoreEffect(card.ChoicePendingEffects[selectedOptionIndex]);
        }

        private static CommandResult Invalid(CommandErrorCode errorCode, string reason)
        {
            return CommandResult.Invalid(ValidationResult.Failure(errorCode, reason));
        }

        private static ValidationResult InvalidPaymentRecipients()
        {
            return ValidationResult.Failure(
                CommandErrorCode.InvalidTarget,
                "路费接收方参数格式不正确。");
        }
    }
}
