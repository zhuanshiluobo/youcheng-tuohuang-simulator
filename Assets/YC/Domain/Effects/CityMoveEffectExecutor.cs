using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Presentation;
using System.Linq;
using YC.Domain.Items;

namespace YC.Domain.Effects
{
    public static class CityMoveEffectTypeIds
    {
        public const string Move = "effect.city.move";
        public const string CommitPosition = "effect.city.position.commit";
    }

    public static class CityMoveEventTypeIds
    {
        public const string BeforeCityMove = "BeforeCityMove";
        public const string CityMoveCompleted = "CityMoveCompleted";
    }

    public static class CityMoveEffectSpecFactory
    {
        public static EffectSpec Move(
            int playerId,
            string targetLocationId,
            bool waiveBaseCost = false,
            bool consumeMainAction = true,
            string sourceId = "",
            bool allowDecline = false,
            int decisionPlayerId = -1,
            string targetPolicy = "standard")
        {
            var spec = new EffectSpec(
                CityMoveEffectTypeIds.Move,
                Object(
                    Entry("targetLocation", string.IsNullOrEmpty(targetLocationId) ? NormalizedValue.CreateNull() : NormalizedValue.CreateStableReference("location", targetLocationId)),
                    Entry("targetPolicy", NormalizedValue.CreateString(targetPolicy)),
                    Entry("waiveBaseCost", NormalizedValue.CreateBoolean(waiveBaseCost)),
                    Entry("consumeMainAction", NormalizedValue.CreateBoolean(consumeMainAction))))
            {
                PlayerId = playerId,
                SourceId = sourceId ?? string.Empty,
                DefinitionVersion = CityMoveEffectExecutor.DefinitionVersion
            };
            return spec.WithExecutionOptions(
                allowDecline,
                decisionPlayerId,
                "city_move.decline",
                "legal_noop");
        }

        private static NormalizedValue Object(params NormalizedValueEntry[] entries)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>(entries));
        }

        private static NormalizedValueEntry Entry(string name, NormalizedValue value)
        {
            return new NormalizedValueEntry { Name = name, Value = value };
        }
    }

    /// <summary>
    /// 城市移动的权威流程：BeforeCityMove 响应完成后重新验证，再按 pay/remove/commit/place
    /// 顺序执行，最后发布 CityMoveCompleted。事件牌是完成事件的子 Effect。
    /// </summary>
    public sealed class CityMoveEffectExecutor
    {
        public const string DefinitionVersion = "1.2.0";
        public const string TargetInteractionTypeId = "effect.city.move.target";

        private readonly IMapQueryService mapQuery;
        private readonly InfluenceService influenceService;
        private readonly CityMovementService movementService;
        private readonly TravelCostService travelCostService;
        private readonly ResourceTokenService resourceTokenService;

        public CityMoveEffectExecutor(
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            CityMovementService movementService,
            TravelCostService travelCostService,
            ResourceTokenService resourceTokenService)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
            this.movementService = movementService ?? throw new ArgumentNullException(nameof(movementService));
            this.travelCostService = travelCostService ?? throw new ArgumentNullException(nameof(travelCostService));
            this.resourceTokenService = resourceTokenService ?? throw new ArgumentNullException(nameof(resourceTokenService));
        }

        public static void Register(
            EffectRegistry registry,
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            CityMovementService movementService,
            TravelCostService travelCostService,
            ResourceTokenService resourceTokenService)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            movementService.DestinationRegistry = registry;
            var executor = new CityMoveEffectExecutor(
                mapQuery,
                influenceService,
                movementService,
                travelCostService,
                resourceTokenService);
            EffectRegistration ignored;
            if (!registry.TryGet(CityMoveEffectTypeIds.Move, out ignored))
            {
                registry.Register(new EffectRegistration(
                    CityMoveEffectTypeIds.Move,
                    executor.Execute,
                    EffectExecutorKind.IntrinsicFlow,
                    DefinitionVersion)
                {
                    Validator = ValidateSpec
                });
            }

            CityPositionCommitEffectExecutor.Register(registry, mapQuery);
        }

        private EffectStepResult Execute(EffectExecutionContext context)
        {
            string targetLocationId;
            bool waiveBaseCost;
            bool consumeMainAction;
            string diagnostic;
            if (!TryReadArguments(context.Node.NormalizedArguments, out targetLocationId, out waiveBaseCost, out consumeMainAction, out diagnostic))
            {
                return EffectStepResult.Failed("invalid_arguments", NormalizedValue.CreateString(diagnostic));
            }

            string stage = context.Node.FlowStage ?? string.Empty;
            string preparedId = ReadString(context.Node.NormalizedArguments, "preparedPaymentOwnerEffectId", "");
            var prepared = preparedId.Length == 0 ? null : context.State.EffectRuntime.EffectNodes.Find(node => node.EffectId == preparedId);
            if (preparedId.Length > 0 && (prepared?.Payment?.Preparation?.PromotedActionEffectId != context.Node.EffectId ||
                !prepared.Payment.IsCommitted || !prepared.Payment.Preparation.BusinessBeforeReady ||
                prepared.Payment.Preparation.TargetId != targetLocationId || prepared.PlayerId != context.Node.PlayerId))
                return EffectStepResult.Failed("payment_preparation_context_invalid");
            if (prepared != null && stage.Length == 0) stage = "before_city_move";
            string policy = ReadString(context.Node.NormalizedArguments, "targetPolicy", "standard");
            if (stage == "awaiting_target")
            {
                var answer = context.GetLatestInteractionAnswer();
                if (answer != null && answer.Kind == NormalizedValueKind.Array && answer.Items.Count == 1) answer = answer.Items[0];
                targetLocationId = answer == null ? "" : answer.Kind == NormalizedValueKind.StableReference ? answer.ReferenceId : answer.StringValue;
                if (!QueryTargets(context, waiveBaseCost, policy).Contains(targetLocationId))
                    return EffectStepResult.Failed("city_move_target_invalid");
                foreach (var entry in context.Node.NormalizedArguments.Properties)
                    if (entry.Name == "targetLocation") entry.Value = NormalizedValue.CreateStableReference("location", targetLocationId);
                stage = "before_city_move";
            }
            if (stage == "before_city_move" && string.IsNullOrEmpty(targetLocationId))
            {
                if (HasFailedChild(context)) return EffectStepResult.Failed("before_city_move_response_failed");
                var targets = QueryTargets(context, waiveBaseCost, policy);
                if (targets.Count == 0) return EffectStepResult.Failed("city_move_no_target");
                var interaction = new EffectInteractionSpec
                {
                    InteractionTypeId = TargetInteractionTypeId, AnsweringPlayerId = context.Node.PlayerId,
                    Visibility = "owner", PromptKey = "city_move.choose_target", MinSelections = 1, MaxSelections = 1,
                    AnswerSchema = "candidate_id",
                    Presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:interaction.map.city_move") }
                };
                interaction.CandidateIds.AddRange(targets);
                return EffectStepResult.Continue("awaiting_target").AddInteraction(interaction);
            }
            if (!string.IsNullOrEmpty(targetLocationId) && (string.IsNullOrEmpty(stage) || stage == "before_city_move") &&
                !MatchesPolicy(context.State, context.Node.PlayerId, targetLocationId, policy))
                {
                if (prepared != null) throw new KernelException("city_destination_after_payment_invalid", "city_move_target_policy_invalid");
                return EffectStepResult.Failed("city_move_target_policy_invalid");
            }
            if (string.IsNullOrEmpty(stage))
            {
                if (!string.IsNullOrEmpty(targetLocationId))
                {
                    ValidationResult validation = Validate(context.State, context.Node.PlayerId, targetLocationId, waiveBaseCost, context.Node.EffectId);
                    if (!validation.IsValid)
                {
                    if (prepared != null) throw new KernelException("city_destination_after_payment_invalid", validation.Reason);
                    return EffectStepResult.Failed(validation.ErrorCode.ToString(), NormalizedValue.CreateString(validation.Reason));
                }
                }
                return EffectStepResult.Continue("before_city_move")
                    .AddEvent(CreateLifecycleEvent(
                        context.Node,
                        CityMoveEventTypeIds.BeforeCityMove,
                        targetLocationId,
                        "before",
                        CreateMovePayload(context.State, context.Node.PlayerId, targetLocationId, string.Empty)));
            }

            if (stage == "before_city_move")
            {
                if (HasFailedChild(context)) return EffectStepResult.Failed("before_city_move_response_failed");
                ValidationResult validation = Validate(context.State, context.Node.PlayerId, targetLocationId, waiveBaseCost || prepared != null, context.Node.EffectId, waiveBaseCost ? "free" : "specified");
                if (!validation.IsValid)
                {
                    if (prepared != null) throw new KernelException("city_destination_after_payment_invalid", validation.Reason);
                    return EffectStepResult.Failed(validation.ErrorCode.ToString(), NormalizedValue.CreateString(validation.Reason));
                }

                PlayerState player = context.State.FindPlayer(context.Node.PlayerId);
                MapRouteDefinition route = mapQuery.FindRoute(player.CityLocationId, targetLocationId);
                context.Node.NormalizedResult = ResultObject(
                    "sourceLocationId", player.CityLocationId,
                    "targetLocationId", targetLocationId,
                    "routeId", route.RouteId);
                InfluenceIdentity.Ensure(context.State);
                var children = new List<EffectSpec>();
                if (!waiveBaseCost && prepared == null)
                {
                    ResourceSet cost = travelCostService.GetCityMoveBaseCost();
                    children.Add(ResourceEffectSpecFactory.Pay(
                        context.Node.PlayerId,
                        cost,
                        "city_move.base_cost"));
                }

                var displacedInfluenceIds = new List<string>();
                for (int i = 0; i < context.State.Map.Influences.Count; i++)
                {
                    InfluencePlacement influence = context.State.Map.Influences[i];
                    if (influence == null || influence.PlayerId == context.Node.PlayerId) continue;
                    if (influence.RouteId == route.RouteId || influence.LocationId == targetLocationId)
                    {
                        displacedInfluenceIds.Add(influence.InfluenceId);
                    }
                }
                // 由主链按本局玩家顺序响应；公司独立排在玩家主体之后。
                displacedInfluenceIds = displacedInfluenceIds.OrderBy(id =>
                {
                    var placement = influenceService.FindInfluence(context.State, id);
                    int index = context.State.Players.FindIndex(owner => owner.PlayerId == placement.PlayerId);
                    return index < 0 ? context.State.Players.Count : index;
                }).ThenBy(id => id, StringComparer.Ordinal).ToList();
                for (int i = 0; i < displacedInfluenceIds.Count; i++)
                {
                    if (ReadString(context.Node.NormalizedArguments, "clearInfluencePolicy", "remove") == "move")
                        children.Add(InfluenceEffectSpecFactory.MoveInfluence(context.Node.PlayerId, displacedInfluenceIds[i], "", InfluenceCauseKinds.MoveCity));
                    else children.Add(InfluenceEffectSpecFactory.RemoveInfluence(
                        context.Node.PlayerId,
                        displacedInfluenceIds[i],
                        "city_move_displacement",
                        InfluenceCauseKinds.MoveCity));
                }

                children.Add(CityPositionCommitEffectSpecFactory.Commit(
                    context.Node.PlayerId,
                    player.CityLocationId,
                    targetLocationId,
                    route.RouteId, waiveBaseCost ? "free" : "specified", policy, context.Node.EffectId));

                string sourceSlotId = FindFirstAvailableSourceSlot(context.State, context.Node.PlayerId, player.CityLocationId);
                if (!string.IsNullOrEmpty(sourceSlotId))
                {
                    children.Add(InfluenceEffectSpecFactory.PlaceInfluence(
                        context.Node.PlayerId,
                        sourceSlotId,
                        "city_move_source",
                        null,
                        InfluenceCauseKinds.MoveCity));
                }

                return EffectStepResult.Continue("awaiting_movement_children").AddChildren(children);
            }

            if (stage == "awaiting_movement_children")
            {
                if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("城市移动子 Effect 尚未全部结束。");
                if (HasFailedChild(context)) return EffectStepResult.Failed("movement_child_failed");

                return EffectStepResult.Continue("awaiting_city_move_completed")
                    .AddEvent(CreateLifecycleEvent(
                        context.Node,
                        CityMoveEventTypeIds.CityMoveCompleted,
                        targetLocationId,
                        "completed",
                        CreateMovePayload(
                            context.State,
                            context.Node.PlayerId,
                            targetLocationId,
                            ReadString(context.Node.NormalizedResult, "sourceLocationId", string.Empty))));
            }

            if (stage == "awaiting_city_move_completed")
            {
                if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("城市移动完成事件的响应子 Effect 尚未结束。");
                bool continuationFailed = HasFailedChild(context);
                if (consumeMainAction)
                {
                    new MainActionBudgetService().SpendCompletedMainAction(context.State, context.Node.PlayerId);
                }
                string routeId = ReadString(context.Node.NormalizedResult, "routeId", string.Empty);
                var result = ResultObject("outcome", "completed",
                    "postMoveContinuationOutcome", continuationFailed ? "failed" : "completed",
                    "targetLocationId", targetLocationId, "routeId", routeId);
                var slots = new List<NormalizedValue>();
                if (!string.IsNullOrEmpty(routeId))
                {
                    var route = mapQuery.GetRoute(routeId);
                    for (int i = 0; i < route.InfluenceSlotCount; i++)
                    {
                        string slot = InfluenceService.GetRouteSlotId(routeId, i);
                        if (influenceService.CanPlace(context.State, context.Node.PlayerId, slot).IsValid)
                            slots.Add(NormalizedValue.CreateString(slot));
                    }
                }
                result.Properties.Add(new NormalizedValueEntry { Name = "traversedRouteSlots", Value = NormalizedValue.CreateArray(slots) });
                int removedOpponentCount = context.ChildNodes.Where(n => n.EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence && n.Status == EffectNodeStatus.Completed)
                    .Sum(n => ItemTransferEffectExecutor.Value(n.NormalizedResult, "removedItemIds")?.Items.Count ?? 0);
                result.Properties.Add(new NormalizedValueEntry { Name = "removedOpponentCount", Value = NormalizedValue.CreateInteger(removedOpponentCount) });
                result.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                return EffectStepResult.Completed(result);
            }

            return EffectStepResult.Failed("invalid_flow_stage");
        }

        private ValidationResult Validate(GameState state, int playerId, string targetLocationId, bool waiveBaseCost, string sourceEffectId = "", string destinationPurpose = "")
        {
            return movementService.CanMoveCityEffect(state, playerId, targetLocationId, waiveBaseCost, sourceEffectId, destinationPurpose.Length > 0 ? destinationPurpose : waiveBaseCost ? "free" : "specified");
        }

        private List<string> QueryTargets(EffectExecutionContext context, bool waive, string policy)
        {
            var result = new List<string>();
            var player = context.State.FindPlayer(context.Node.PlayerId);
            if (player == null || string.IsNullOrEmpty(player.CityLocationId)) return result;
            var query = new CityDestinationQuery(player.PlayerId, context.Node.EffectId, waive ? "free" : "specified", policy,
                mapQuery.GetAdjacentLocations(player.CityLocationId).Select(x => x.LocationId).ToArray());
            foreach (var target in new CityDestinationQueryService(mapQuery, context.Registry).Query(context.State, query).AllowedLocationIds)
                if (Validate(context.State, player.PlayerId, target, waive, context.Node.EffectId).IsValid) result.Add(target);
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private bool MatchesPolicy(GameState state, int playerId, string locationId, string policy)
        {
            return CityDestinationQueryService.MatchesPolicy(state, playerId, locationId, policy);
        }

        private string FindFirstAvailableSourceSlot(GameState state, int playerId, string locationId)
        {
            MapLocationDefinition location = mapQuery.GetLocation(locationId);
            for (int i = 0; i < location.InfluenceSlotCount; i++)
            {
                string slotId = InfluenceService.GetLocationSlotId(locationId, i);
                if (influenceService.CanPlace(state, playerId, slotId).IsValid) return slotId;
            }
            return string.Empty;
        }

        private static bool HasNonTerminalChild(EffectExecutionContext context)
        {
            IList<EffectNodeRuntimeState> children = context.ChildNodes;
            for (int i = 0; i < children.Count; i++)
            {
                EffectNodeStatus status = children[i].Status;
                if (status != EffectNodeStatus.Completed && status != EffectNodeStatus.Failed && status != EffectNodeStatus.Faulted) return true;
            }
            return false;
        }

        private static bool HasFailedChild(EffectExecutionContext context)
        {
            IList<EffectNodeRuntimeState> children = context.ChildNodes;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i].Status == EffectNodeStatus.Failed || children[i].Status == EffectNodeStatus.Faulted) return true;
            }
            return false;
        }

        private static EffectEventRequest CreateLifecycleEvent(
            EffectNodeRuntimeState node,
            string eventType,
            string targetLocationId,
            string semanticKey,
            NormalizedValue payload)
        {
            return new EffectEventRequest
            {
                EventId = StableIdFactory.Create("event", node.EffectId, eventType, targetLocationId, semanticKey),
                EventType = eventType,
                SourceEffectId = node.EffectId,
                OwnerNodeId = node.EffectId,
                RouteKey = targetLocationId,
                TargetEntityId = targetLocationId,
                PlayerId = node.PlayerId,
                Payload = payload,
                ResponseKind = RuleEventResponseKind.Effects,
                DefinitionVersion = DefinitionVersion,
                Visibility = "public",
                SemanticKey = semanticKey
            };
        }

        internal static EffectEventRequest CreatePreparedBeforeEvent(GameState state, EffectNodeRuntimeState owner, string targetLocationId)
            => CreateLifecycleEvent(owner, CityMoveEventTypeIds.BeforeCityMove, targetLocationId, "before",
                CreateMovePayload(state, owner.PlayerId, targetLocationId, owner.Payment.Preparation.SourceLocationId));

        private static NormalizedValue CreateMovePayload(GameState state, int playerId, string targetLocationId, string sourceLocationId)
        {
            PlayerState player = state.FindPlayer(playerId);
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "playerId", Value = NormalizedValue.CreateInteger(playerId) },
                new NormalizedValueEntry { Name = "sourceLocationId", Value = NormalizedValue.CreateString(string.IsNullOrEmpty(sourceLocationId) && player != null ? player.CityLocationId : sourceLocationId) },
                new NormalizedValueEntry { Name = "targetLocationId", Value = NormalizedValue.CreateString(targetLocationId) }
            });
        }

        private static NormalizedValue ResultObject(params string[] values)
        {
            var entries = new List<NormalizedValueEntry>();
            for (int i = 0; i + 1 < values.Length; i += 2)
            {
                entries.Add(new NormalizedValueEntry { Name = values[i], Value = NormalizedValue.CreateString(values[i + 1] ?? string.Empty) });
            }
            return NormalizedValue.CreateObject(entries);
        }

        private static bool TryReadArguments(NormalizedValue args, out string target, out bool waive, out bool consume, out string diagnostic)
        {
            target = string.Empty;
            waive = false;
            consume = true;
            diagnostic = string.Empty;
            NormalizedValue targetValue;
            NormalizedValue waiveValue;
            NormalizedValue consumeValue;
            if (!TryGet(args, "targetLocation", out targetValue) || targetValue == null || (targetValue.Kind != NormalizedValueKind.Null && (targetValue.Kind != NormalizedValueKind.StableReference || targetValue.ReferenceType != "location")) ||
                !TryGet(args, "waiveBaseCost", out waiveValue) || waiveValue == null || waiveValue.Kind != NormalizedValueKind.Boolean ||
                !TryGet(args, "consumeMainAction", out consumeValue) || consumeValue == null || consumeValue.Kind != NormalizedValueKind.Boolean)
            {
                diagnostic = "城市移动 Effect 参数必须包含 targetLocation、waiveBaseCost 和 consumeMainAction。";
                return false;
            }
            target = targetValue.Kind == NormalizedValueKind.Null ? string.Empty : targetValue.ReferenceId;
            waive = waiveValue.BooleanValue;
            consume = consumeValue.BooleanValue;
            return true;
        }

        private static string ValidateSpec(EffectSpec spec)
        {
            string target;
            bool waive;
            bool consume;
            string diagnostic;
            if (!TryReadArguments(spec == null ? null : spec.NormalizedArguments, out target, out waive, out consume, out diagnostic)) return diagnostic;
            string policy = ReadString(spec.NormalizedArguments, "targetPolicy", "standard");
            string clearing = ReadString(spec.NormalizedArguments, "clearInfluencePolicy", "remove");
            if (clearing != "remove" && clearing != "move") return "未登记的影响力离场规则。";
            return policy == "standard" || policy == "explored" || policy == "explored_own_influence" ? string.Empty : "未登记的城市移动目标规则。";
        }

        private static string ReadString(NormalizedValue value, string name, string fallback)
        {
            NormalizedValue candidate;
            return TryGet(value, name, out candidate) && candidate != null && candidate.Kind == NormalizedValueKind.String ? candidate.StringValue : fallback;
        }

        private static bool TryGet(NormalizedValue value, string name, out NormalizedValue result)
        {
            if (value != null && value.Kind == NormalizedValueKind.Object && value.Properties != null)
            {
                for (int i = 0; i < value.Properties.Count; i++)
                {
                    if (value.Properties[i] != null && value.Properties[i].Name == name)
                    {
                        result = value.Properties[i].Value;
                        return true;
                    }
                }
            }
            result = null;
            return false;
        }
    }

    public static class CityPositionCommitEffectSpecFactory
    {
        public static EffectSpec Commit(int playerId, string sourceLocationId, string targetLocationId, string routeId)
            => Commit(playerId, sourceLocationId, targetLocationId, routeId, "specified", "standard", "");

        public static EffectSpec Commit(int playerId, string sourceLocationId, string targetLocationId, string routeId,
            string destinationPurpose, string targetPolicy, string sourceOperationId)
        {
            return new EffectSpec(
                CityMoveEffectTypeIds.CommitPosition,
                NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    Entry("playerId", NormalizedValue.CreateStableReference("player", playerId.ToString(CultureInfo.InvariantCulture))),
                    Entry("sourceLocation", NormalizedValue.CreateStableReference("location", sourceLocationId ?? string.Empty)),
                    Entry("targetLocation", NormalizedValue.CreateStableReference("location", targetLocationId ?? string.Empty)),
                    Entry("route", NormalizedValue.CreateStableReference("route", routeId ?? string.Empty)),
                    Entry("destinationPurpose", NormalizedValue.CreateString(destinationPurpose ?? "specified")),
                    Entry("targetPolicy", NormalizedValue.CreateString(targetPolicy ?? "standard")),
                    Entry("sourceOperationId", NormalizedValue.CreateString(sourceOperationId ?? ""))
                }))
            {
                PlayerId = playerId,
                DefinitionVersion = CityPositionCommitEffectExecutor.DefinitionVersion
            };
        }

        private static NormalizedValueEntry Entry(string name, NormalizedValue value)
        {
            return new NormalizedValueEntry { Name = name, Value = value };
        }
    }

    public sealed class CityPositionCommitEffectExecutor
    {
        public const string DefinitionVersion = "1.0.0";
        private readonly IMapQueryService mapQuery;

        private CityPositionCommitEffectExecutor(IMapQueryService mapQuery)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
        }

        public static void Register(EffectRegistry registry, IMapQueryService mapQuery)
        {
            EffectRegistration ignored;
            if (registry.TryGet(CityMoveEffectTypeIds.CommitPosition, out ignored)) return;
            var executor = new CityPositionCommitEffectExecutor(mapQuery);
            registry.Register(new EffectRegistration(
                CityMoveEffectTypeIds.CommitPosition,
                executor.Execute,
                EffectExecutorKind.Atomic,
                DefinitionVersion)
            {
                Validator = spec => spec == null || spec.NormalizedArguments == null || spec.NormalizedArguments.Kind != NormalizedValueKind.Object
                    ? "城市位置提交 Effect 参数必须是对象。"
                    : string.Empty
            });
        }

        private EffectStepResult Execute(EffectExecutionContext context)
        {
            PlayerState player = context.State.FindPlayer(context.Node.PlayerId);
            string source = ReadStable(context.Node.NormalizedArguments, "sourceLocation");
            string target = ReadStable(context.Node.NormalizedArguments, "targetLocation");
            string routeId = ReadStable(context.Node.NormalizedArguments, "route");
            if (player == null || player.CityLocationId != source)
            {
                throw new KernelException("city_position_source_changed", "城市位置提交前起点已改变，保留已执行费用及响应。");
            }
            try
            {
                mapQuery.GetLocation(target);
                MapRouteDefinition route = mapQuery.FindRoute(source, target);
                if (route.RouteId != routeId) throw new KernelException("city_position_route_changed", "城市位置提交前路线已改变。");
            }
            catch (ArgumentException)
            {
                throw new KernelException("city_position_target_changed", "城市位置提交前目标已改变。");
            }
            var parentMove = context.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == context.Node.ParentEffectId && n.EffectTypeId == CityMoveEffectTypeIds.Move);
            if (parentMove != null)
            {
                foreach (var id in parentMove.ChildEffectIds.TakeWhile(id => id != context.Node.EffectId))
                {
                    var prior = context.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == id);
                    if (prior == null || prior.Status == EffectNodeStatus.Failed || prior.Status == EffectNodeStatus.Faulted)
                        throw new KernelException("city_position_required_step_failed", "城市位置提交前必要步骤未成功，保留已有支付和离场现场。");
                    if (prior.Status != EffectNodeStatus.Completed)
                    {
                        if (!context.TryAttachCompletionDependenciesOnWorking(new[] { prior.EffectId }, "city_position_prior_step"))
                            throw new KernelException("city_position_dependency_invalid", "城市位置提交无法绑定真实前置步骤。");
                        return EffectStepResult.Continue(context.Node.FlowStage);
                    }
                }
                // 离场本身已终止不代表其外部旁路已结束。只等待本次Move真实离场产生的响应，
                // 待其结清后再用同一registry复验目的地，保留已付与原位置。
                var responses=EffectTreeExecutor.GetDeferredResponseDependencies(context.State,new DeferredDependencyQuery {
                    DependencyKind=DeferredDependencyKind.FinalizeGame,OwnerEffectId=context.Node.EffectId }).Where(d=>{
                        var response=context.State.EffectRuntime.DeferredResponses.Single(r=>r.DeferredResponseId==d.DeferredResponseId);
                        return InfluenceDepartureProtocol.IsWithin(context.State,response.SourceOperationEffectId,parentMove.EffectId);
                    }).ToArray();
                if(responses.Length>0) {
                    if(!context.TryAttachDeferredResponseDependenciesOnWorking(responses)) throw new KernelException("city_position_response_dependency_invalid","城市位置无法绑定本次离场的真实响应。");
                    return EffectStepResult.Continue(context.Node.FlowStage);
                }
            }
            string purpose = ItemTransferEffectExecutor.Text(context.Node.NormalizedArguments, "destinationPurpose");
            string policy = ItemTransferEffectExecutor.Text(context.Node.NormalizedArguments, "targetPolicy");
            string operation = ItemTransferEffectExecutor.Text(context.Node.NormalizedArguments, "sourceOperationId");
            if (purpose.Length == 0) purpose = "specified";
            if (policy.Length == 0) policy = "standard";
            var admission = new CityDestinationQueryService(mapQuery, context.Registry).ValidateTarget(context.State,
                new CityDestinationQuery(player.PlayerId, operation, purpose, policy,
                    mapQuery.GetAdjacentLocations(source).Select(x => x.LocationId).ToArray()), target);
            if (!admission.IsValid) throw new KernelException("city_destination_commit_rejected", admission.Reason);
            if (ItemZoneLegacyProjection.IsActive(context.State))
            {
                var city = context.State.ItemZones.Items.Single(item => item.PhysicalKind == ItemPhysicalKind.MobileCity && item.OwnerSubject.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId == player.PlayerId);
                var parent = context.State.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.map_location" && zone.LocalZoneId == target);
                var dock = context.State.ItemZones.Zones.Single(zone => zone.ParentId == parent.ZoneId && zone.TemplateId == "core.zone.city_dock");
                var transfer = new ItemTransferService().RelocateItems(context, InfluenceService.Transfer(context, city, dock.ZoneId, "", "core.mobile_city_move"));
                if (!transfer.Changed || transfer.MovedCount != 1 || transfer.MovedItemIds.Count != 1 ||
                    transfer.MovedItemIds[0] != city.ItemId || city.ZoneId != dock.ZoneId)
                    throw new KernelException("city_position_not_committed", "城市实体尚未迁移至目标停靠区。");
                InfluenceLockRules.FollowCity(context, city.ItemId);
            }
            else player.CityLocationId = target;
            player.HasMovedCityThisRound = true;
            return EffectStepResult.Completed(Result(target, routeId));
        }

        private static string ReadStable(NormalizedValue value, string name)
        {
            NormalizedValue candidate;
            if (value != null && value.Kind == NormalizedValueKind.Object && value.Properties != null)
            {
                for (int i = 0; i < value.Properties.Count; i++)
                {
                    NormalizedValueEntry entry = value.Properties[i];
                    if (entry != null && entry.Name == name && entry.Value != null && entry.Value.Kind == NormalizedValueKind.StableReference)
                    {
                        candidate = entry.Value;
                        return candidate.ReferenceId;
                    }
                }
            }
            return string.Empty;
        }

        private static NormalizedValue Result(string target, string route)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "routeId", Value = NormalizedValue.CreateString(route) },
                new NormalizedValueEntry { Name = "targetLocationId", Value = NormalizedValue.CreateString(target) }
            });
        }
    }
}
