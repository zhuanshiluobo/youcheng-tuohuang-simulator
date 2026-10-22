using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Exploration;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Domain.Effects
{
    public static class ExplorationEffectTypeIds
    {
        public const string Explore = "effect.exploration.resolve";
    }

    public static class ExplorationEffectSpecFactory
    {
        public static EffectSpec ExploreFinalized(int playerId, string selectionOwnerEffectId,
            string selectionSessionId, string commitmentId, int selectionRevision, string targetLocationId,
            string influenceSlotId = "", bool allowFacilityEntry = false, bool consumeMainAction = true,
            string sourceId = "")
        {
            return new EffectSpec(ExplorationEffectTypeIds.Explore, NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                Entry("paymentProtocol", NormalizedValue.CreateString(RouteTollEscrowService.Protocol)),
                Entry("selectionOwnerEffectId", NormalizedValue.CreateStableReference("effect", selectionOwnerEffectId)),
                Entry("selectionSessionId", NormalizedValue.CreateString(selectionSessionId)),
                Entry("commitmentId", NormalizedValue.CreateString(commitmentId)),
                Entry("selectionRevision", NormalizedValue.CreateInteger(selectionRevision)),
                Entry("targetLocation", NormalizedValue.CreateStableReference("location", targetLocationId)),
                Entry("influenceSlot", string.IsNullOrEmpty(influenceSlotId) ? NormalizedValue.CreateNull()
                    : NormalizedValue.CreateStableReference("slot", influenceSlotId)),
                Entry("allowFacilityEntry", NormalizedValue.CreateBoolean(allowFacilityEntry)),
                Entry("consumeMainAction", NormalizedValue.CreateBoolean(consumeMainAction))
            })) { PlayerId = playerId, SourceId = sourceId ?? string.Empty,
                DefinitionVersion = ExplorationEffectExecutor.DefinitionVersion };
        }

        // 只供原预付协议节点恢复；正常新探索使用引用权威选择承诺的ExploreFinalized。
        public static EffectSpec Explore(
            int playerId,
            string targetLocationId,
            MapPath path,
            string influenceSlotId,
            IDictionary<string, int> paymentRecipientsByRouteId,
            bool allowFacilityEntry = false,
            bool consumeMainAction = true,
            string sourceId = "")
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            var routeIds = new List<NormalizedValue>();
            for (int i = 0; i < path.RouteIds.Count; i++)
            {
                routeIds.Add(NormalizedValue.CreateStableReference("route", path.RouteIds[i]));
            }
            var locationIds = new List<NormalizedValue>();
            for (int i = 0; i < path.LocationIds.Count; i++)
            {
                locationIds.Add(NormalizedValue.CreateStableReference("location", path.LocationIds[i]));
            }
            var recipients = new List<NormalizedValue>();
            if (paymentRecipientsByRouteId != null)
            {
                var routeKeys = new List<string>(paymentRecipientsByRouteId.Keys);
                routeKeys.Sort(StringComparer.Ordinal);
                for (int i = 0; i < routeKeys.Count; i++)
                {
                    string routeId = routeKeys[i];
                    recipients.Add(NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                    {
                        Entry("playerId", NormalizedValue.CreateInteger(paymentRecipientsByRouteId[routeId])),
                        Entry("routeId", NormalizedValue.CreateStableReference("route", routeId))
                    }));
                }
            }

            return new EffectSpec(
                ExplorationEffectTypeIds.Explore,
                NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    Entry("allowFacilityEntry", NormalizedValue.CreateBoolean(allowFacilityEntry)),
                    Entry("consumeMainAction", NormalizedValue.CreateBoolean(consumeMainAction)),
                    Entry("influenceSlot", string.IsNullOrEmpty(influenceSlotId)
                        ? NormalizedValue.CreateNull()
                        : NormalizedValue.CreateStableReference("slot", influenceSlotId)),
                    Entry("pathLocationIds", NormalizedValue.CreateArray(locationIds)),
                    Entry("pathRouteIds", NormalizedValue.CreateArray(routeIds)),
                    Entry("paymentRecipients", NormalizedValue.CreateArray(recipients)),
                    Entry("targetLocation", NormalizedValue.CreateStableReference("location", targetLocationId ?? string.Empty))
                }))
            {
                PlayerId = playerId,
                SourceId = sourceId ?? string.Empty,
                DefinitionVersion = ExplorationEffectExecutor.DefinitionVersion
            };
        }

        private static NormalizedValueEntry Entry(string name, NormalizedValue value)
        {
            return new NormalizedValueEntry { Name = name, Value = value };
        }
    }

    /// <summary>
    /// 探索复用权威选择承诺，先将路费移入可恢复托管，完成事件后一次交付。
    /// 原预付节点继续按原协议恢复，客户端不能提交金额或改写收款计划。
    /// </summary>
    public sealed class ExplorationEffectExecutor
    {
        public const string DefinitionVersion = "1.0.0";

        private readonly IMapQueryService mapQuery;
        private readonly ExplorationService explorationService;
        private readonly RouteTollService routeTollService;
        private readonly EventDeckService eventDeckService;
        private readonly ResourceTokenService resourceTokenService;
        private readonly IRouteSelectionSessionAuthority selectionAuthority;
        private readonly RouteTollEscrowService escrowService;

        public ExplorationEffectExecutor(
            IMapQueryService mapQuery,
            ExplorationService explorationService,
            EventDeckService eventDeckService,
            ResourceTokenService resourceTokenService,
            IRouteSelectionSessionAuthority selectionAuthority = null)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.explorationService = explorationService ?? throw new ArgumentNullException(nameof(explorationService));
            this.routeTollService = new RouteTollService(mapQuery);
            this.eventDeckService = eventDeckService ?? throw new ArgumentNullException(nameof(eventDeckService));
            this.resourceTokenService = resourceTokenService ?? throw new ArgumentNullException(nameof(resourceTokenService));
            this.selectionAuthority = selectionAuthority ?? new RouteSelectionSessionAuthority(mapQuery);
            this.escrowService = new RouteTollEscrowService(mapQuery);
        }

        public static void Register(
            EffectRegistry registry,
            IMapQueryService mapQuery,
            ExplorationService explorationService,
            EventDeckService eventDeckService,
            ResourceTokenService resourceTokenService,
            IRouteSelectionSessionAuthority selectionAuthority = null)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            var executor = new ExplorationEffectExecutor(mapQuery, explorationService, eventDeckService, resourceTokenService, selectionAuthority);
            EffectRegistration ignored;
            if (registry.TryGet(ExplorationEffectTypeIds.Explore, out ignored)) return;
            registry.Register(new EffectRegistration(
                ExplorationEffectTypeIds.Explore,
                executor.Execute,
                EffectExecutorKind.IntrinsicFlow,
                DefinitionVersion)
            {
                Validator = ValidateSpec
            });
        }

        private EffectStepResult Execute(EffectExecutionContext context)
        {
            if (Get(context.Node.NormalizedArguments, "paymentProtocol") != null)
                return ExecuteEscrow(context);
            if (context.Node.FlowStage == RouteTollEscrowService.AwaitingEventStage ||
                Get(context.Node.ContinuationData, RouteTollEscrowService.ReceiptField) != null)
                throw new KernelException("route_toll_protocol_missing", "探索托管节点缺少原支付协议。");
            string targetLocationId;
            string influenceSlotId;
            MapPath path;
            Dictionary<string, int> recipients;
            bool allowFacilityEntry;
            bool consumeMainAction;
            string diagnostic;
            if (!TryReadArguments(
                    context.Node.NormalizedArguments,
                    out targetLocationId,
                    out influenceSlotId,
                    out path,
                    out recipients,
                    out allowFacilityEntry,
                    out consumeMainAction,
                    out diagnostic))
            {
                return EffectStepResult.Failed("invalid_arguments", NormalizedValue.CreateString(diagnostic));
            }

            string stage = context.Node.FlowStage ?? string.Empty;
            if (string.IsNullOrEmpty(stage))
            {
                influenceSlotId = explorationService.ResolveInfluenceSlotId(
                    context.State,
                    context.Node.PlayerId,
                    targetLocationId,
                    influenceSlotId);
                if (string.IsNullOrEmpty(influenceSlotId))
                {
                    return EffectStepResult.Failed(
                        "invalid_target",
                        NormalizedValue.CreateString("目标资源点没有可放置的影响力空格。"));
                }

                ValidationResult validation = explorationService.CanExplore(
                    context.State,
                    context.Node.PlayerId,
                    targetLocationId,
                    path,
                    -1,
                    influenceSlotId,
                    recipients,
                    null,
                    false,
                    allowFacilityEntry);
                if (!validation.IsValid) return EffectStepResult.Failed(validation.ErrorCode.ToString(), NormalizedValue.CreateString(validation.Reason));

                List<ExplorationTravelPayment> payments;
                ValidationResult paymentValidation = routeTollService.TryBuildPaymentPlan(
                    context.State,
                    context.Node.PlayerId,
                    path.RouteIds,
                    recipients,
                    RouteTollPaymentKeyMode.SharedRegion,
                    out payments);
                if (!paymentValidation.IsValid) return EffectStepResult.Failed(paymentValidation.ErrorCode.ToString(), NormalizedValue.CreateString(paymentValidation.Reason));

                var children = new List<EffectSpec>();
                for (int i = 0; i < payments.Count; i++)
                {
                    ExplorationTravelPayment payment = payments[i];
                    if (payment.Amount > 0)
                    {
                        children.Add(ResourceEffectSpecFactory.Pay(
                            context.Node.PlayerId,
                            ResourceType.GoldVoucher,
                            payment.Amount,
                            "exploration.route_toll:" + payment.RouteId));
                        if (payment.ReceiverPlayerId >= 0)
                        {
                            children.Add(ResourceEffectSpecFactory.Gain(
                                payment.ReceiverPlayerId,
                                ResourceType.GoldVoucher,
                                payment.Amount,
                                "exploration.route_toll:" + payment.RouteId));
                        }
                    }
                }

                EventColor color = StaticMapDefinitions.GetEventColor(mapQuery.Map, targetLocationId);
                children.Add(EventCardEffectSpecFactory.Resolve(
                    context.Node.PlayerId,
                    color,
                    targetLocationId,
                    influenceSlotId,
                    "exploration.event:" + targetLocationId));
                return EffectStepResult.Continue("awaiting_exploration_children").AddChildren(children);
            }

            if (stage == "awaiting_exploration_children")
            {
                IList<EffectNodeRuntimeState> children = context.ChildNodes;
                for (int i = 0; i < children.Count; i++)
                {
                    EffectNodeStatus status = children[i].Status;
                    if (status != EffectNodeStatus.Completed && status != EffectNodeStatus.Failed && status != EffectNodeStatus.Faulted)
                    {
                        return EffectStepResult.NoProgress("探索子 Effect 尚未全部结束。");
                    }
                    if (status == EffectNodeStatus.Failed || status == EffectNodeStatus.Faulted)
                    {
                        return EffectStepResult.Failed("exploration_child_failed");
                    }
                }

                if (consumeMainAction)
                {
                    new MainActionBudgetService().SpendCompletedMainAction(context.State, context.Node.PlayerId);
                }
                return EffectStepResult.Completed(NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry { Name = "outcome", Value = NormalizedValue.CreateString("completed") },
                    new NormalizedValueEntry { Name = "targetLocationId", Value = NormalizedValue.CreateString(targetLocationId) }
                }));
            }

            return EffectStepResult.Failed("invalid_flow_stage");
        }

        private static string ValidateSpec(EffectSpec spec)
        {
            if (Get(spec?.NormalizedArguments, "paymentProtocol") != null)
                return ValidateEscrowArguments(spec.NormalizedArguments);
            string target;
            string slot;
            MapPath path;
            Dictionary<string, int> recipients;
            bool allow;
            bool consume;
            string diagnostic;
            return TryReadArguments(spec == null ? null : spec.NormalizedArguments, out target, out slot, out path, out recipients, out allow, out consume, out diagnostic)
                ? string.Empty
                : diagnostic;
        }

        private EffectStepResult ExecuteEscrow(EffectExecutionContext context)
        {
            var node = context.Node;
            string error = ValidateEscrowArguments(node.NormalizedArguments);
            if (error.Length != 0) throw new KernelException("route_toll_arguments_invalid", error);
            string target = Get(node.NormalizedArguments, "targetLocation").ReferenceId;
            string selectionOwner = Get(node.NormalizedArguments, "selectionOwnerEffectId").ReferenceId;
            string sessionId = Get(node.NormalizedArguments, "selectionSessionId").StringValue;
            string commitmentId = Get(node.NormalizedArguments, "commitmentId").StringValue;
            int revision = (int)Get(node.NormalizedArguments, "selectionRevision").IntegerValue;
            bool allowFacility = Get(node.NormalizedArguments, "allowFacilityEntry").BooleanValue;
            bool consumeAction = Get(node.NormalizedArguments, "consumeMainAction").BooleanValue;
            string stage = node.FlowStage ?? string.Empty;
            if (stage.Length == 0)
            {
                var resolved = selectionAuthority.TryResolveFinalized(context.State, selectionOwner,
                    out var session, out var commitment);
                if (!resolved.IsValid) return EffectStepResult.Failed(resolved.Reason);
                if (session.SessionId != sessionId || commitment.CommitmentId != commitmentId ||
                    commitment.AcceptedAtStateRevision != revision || session.PlayerId != node.PlayerId ||
                    commitment.AcceptedPreview?.SelectedLocationIds == null ||
                    commitment.AcceptedPreview.SelectedLocationIds.Count != 1 ||
                    commitment.AcceptedPreview.SelectedLocationIds[0] != target)
                    return EffectStepResult.Failed("route_toll_selection_binding_mismatch");
                var slotArg = Get(node.NormalizedArguments, "influenceSlot");
                string slot = explorationService.ResolveInfluenceSlotId(context.State, node.PlayerId, target,
                    slotArg.Kind == NormalizedValueKind.Null ? string.Empty : slotArg.ReferenceId);
                MapPath witness = BuildConnectivityWitness(session, commitment.AcceptedPreview, target);
                // 只使用用户确认子图中的连通见证；费用始终由完整AcceptedPreview计划校验。
                var eligibility = explorationService.CanExplore(context.State, node.PlayerId, target, witness,
                    -1, slot, null, null, false, allowFacility);
                if (!eligibility.IsValid) return EffectStepResult.Failed(eligibility.Reason);
                var preflight = escrowService.TryValidateReserve(context.State, node, session, commitment);
                if (!preflight.IsValid) return EffectStepResult.Failed(preflight.Reason);

                var claim = selectionAuthority.TryClaimFinalized(context.State, node, selectionOwner, commitmentId,
                    out session, out commitment);
                if (!claim.IsValid) throw new KernelException("route_toll_claim_failed", claim.Reason);
                var reserved = escrowService.TryReserve(context.State, node, session, commitment, out var receipt, out bool changed);
                if (!reserved.IsValid) throw new KernelException("route_toll_reserve_invariant", reserved.Reason);
                if (!changed) throw new KernelException("route_toll_initial_receipt_conflict", "探索初始阶段已经存在托管收据。");
                return EffectStepResult.Continue(RouteTollEscrowService.AwaitingEventStage)
                    .AddChild(EventCardEffectSpecFactory.Resolve(node.PlayerId,
                        StaticMapDefinitions.GetEventColor(mapQuery.Map, target), target, slot, "exploration.event:" + target))
                    .AddLog(new EffectLogEntrySpec { PlayerId = node.PlayerId,
                        Message = "预留探索路费 " + receipt.OriginalAmount.ToString(CultureInfo.InvariantCulture) + " 金券。" });
            }
            if (stage != RouteTollEscrowService.AwaitingEventStage)
                throw new KernelException("route_toll_stage_invalid", "探索托管阶段无效。");
            foreach (var child in context.ChildNodes)
            {
                if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted)
                    throw new KernelException("route_toll_event_failed", "探索事件尚未成功完成，路费托管保持原状。");
                if (child.Status != EffectNodeStatus.Completed)
                    return EffectStepResult.NoProgress("探索事件及响应链尚未结束。");
            }
            var settled = escrowService.TrySettle(context.State, node, out var completedReceipt, out bool delivered);
            if (!settled.IsValid) throw new KernelException("route_toll_settlement_failed", settled.Reason);
            if (delivered && consumeAction)
                new MainActionBudgetService().SpendCompletedMainAction(context.State, node.PlayerId);
            var result = EffectStepResult.Completed(NormalizedValue.CreateObject(new List<NormalizedValueEntry> {
                new NormalizedValueEntry { Name = "outcome", Value = NormalizedValue.CreateString("completed") },
                new NormalizedValueEntry { Name = "targetLocationId", Value = NormalizedValue.CreateString(target) },
                new NormalizedValueEntry { Name = "tollSettlementReceiptId", Value = NormalizedValue.CreateString(completedReceipt.SettlementReceiptId) }
            }));
            if (delivered)
            {
                // 内核以节点+日志索引去重：预留固定占0，交付固定占1。
                result.AddLog(new EffectLogEntrySpec { PlayerId = node.PlayerId,
                    Message = "预留探索路费 " + completedReceipt.OriginalAmount.ToString(CultureInfo.InvariantCulture) + " 金券。" });
                result.AddLog(new EffectLogEntrySpec { PlayerId = node.PlayerId,
                    Message = "交付探索路费 " + completedReceipt.OriginalAmount.ToString(CultureInfo.InvariantCulture) + " 金券。" });
            }
            return result;
        }

        private static MapPath BuildConnectivityWitness(RouteSelectionSession session, RouteSelectionPreview preview, string target)
        {
            var locations = new List<string> { target }; var routes = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal); string current = target;
            while (current != session.StartLocationId)
            {
                if (!seen.Add(current)) throw new KernelException("route_toll_witness_invalid", "选择承诺的连通见证含环。");
                RouteConnectivityLink found = null;
                foreach (var link in preview.ConnectivityWitness)
                    if (link.LocationId == current) { if (found != null) throw new KernelException("route_toll_witness_invalid", "连通见证重复。"); found = link; }
                if (found == null || string.IsNullOrEmpty(found.ParentLocationId) || string.IsNullOrEmpty(found.ViaRouteId))
                    throw new KernelException("route_toll_witness_invalid", "选择承诺缺少目标连通见证。");
                routes.Add(found.ViaRouteId); current = found.ParentLocationId; locations.Add(current);
            }
            locations.Reverse(); routes.Reverse();
            return new MapPath { LocationIds = locations, RouteIds = routes };
        }

        private static string ValidateEscrowArguments(NormalizedValue args)
        {
            if (args?.Kind != NormalizedValueKind.Object || !args.IsValid()) return "托管探索参数无效。";
            if (Get(args, "paymentProtocol")?.Kind != NormalizedValueKind.String ||
                Get(args, "paymentProtocol").StringValue != RouteTollEscrowService.Protocol) return "探索支付协议不受支持。";
            foreach (string name in new[] { "selectionOwnerEffectId", "targetLocation" })
            {
                var value = Get(args, name);
                if (value?.Kind != NormalizedValueKind.StableReference ||
                    value.ReferenceType != (name == "targetLocation" ? "location" : "effect") ||
                    string.IsNullOrEmpty(value.ReferenceId)) return "探索选择引用缺失。";
            }
            var slot = Get(args, "influenceSlot");
            if (slot == null || slot.Kind != NormalizedValueKind.Null && (slot.Kind != NormalizedValueKind.StableReference ||
                slot.ReferenceType != "slot" || string.IsNullOrEmpty(slot.ReferenceId))) return "探索影响力槽参数无效。";
            foreach (string name in new[] { "selectionSessionId", "commitmentId" })
                if (Get(args, name)?.Kind != NormalizedValueKind.String || string.IsNullOrEmpty(Get(args, name).StringValue))
                    return "探索选择会话或承诺ID缺失。";
            if (Get(args, "selectionRevision")?.Kind != NormalizedValueKind.Integer || Get(args, "selectionRevision").IntegerValue < 0 ||
                Get(args, "allowFacilityEntry")?.Kind != NormalizedValueKind.Boolean ||
                Get(args, "consumeMainAction")?.Kind != NormalizedValueKind.Boolean) return "探索选择版本或行动参数无效。";
            return string.Empty;
        }

        private static NormalizedValue Get(NormalizedValue args, string name)
        {
            if (args?.Properties == null) return null;
            return args.Properties.Find(entry => entry != null && entry.Name == name)?.Value;
        }

        private static bool TryReadArguments(
            NormalizedValue args,
            out string targetLocationId,
            out string influenceSlotId,
            out MapPath path,
            out Dictionary<string, int> recipients,
            out bool allowFacilityEntry,
            out bool consumeMainAction,
            out string diagnostic)
        {
            targetLocationId = string.Empty;
            influenceSlotId = string.Empty;
            path = new MapPath();
            recipients = new Dictionary<string, int>(StringComparer.Ordinal);
            allowFacilityEntry = false;
            consumeMainAction = true;
            diagnostic = string.Empty;
            NormalizedValue target;
            NormalizedValue slot;
            NormalizedValue locations;
            NormalizedValue routes;
            NormalizedValue paymentValues;
            NormalizedValue allow;
            NormalizedValue consume;
            if (!TryGet(args, "targetLocation", out target) || !TryGet(args, "influenceSlot", out slot) ||
                !TryGet(args, "pathLocationIds", out locations) || !TryGet(args, "pathRouteIds", out routes) ||
                !TryGet(args, "paymentRecipients", out paymentValues) || !TryGet(args, "allowFacilityEntry", out allow) ||
                !TryGet(args, "consumeMainAction", out consume) ||
                target == null || target.Kind != NormalizedValueKind.StableReference || target.ReferenceType != "location" ||
                slot == null ||
                (slot.Kind != NormalizedValueKind.Null &&
                 (slot.Kind != NormalizedValueKind.StableReference || slot.ReferenceType != "slot")) ||
                locations == null || locations.Kind != NormalizedValueKind.Array || routes == null || routes.Kind != NormalizedValueKind.Array ||
                paymentValues == null || paymentValues.Kind != NormalizedValueKind.Array ||
                allow == null || allow.Kind != NormalizedValueKind.Boolean || consume == null || consume.Kind != NormalizedValueKind.Boolean)
            {
                diagnostic = "探索 Effect 参数结构无效。";
                return false;
            }
            targetLocationId = target.ReferenceId;
            influenceSlotId = slot.Kind == NormalizedValueKind.Null ? string.Empty : slot.ReferenceId;
            allowFacilityEntry = allow.BooleanValue;
            consumeMainAction = consume.BooleanValue;
            if (string.IsNullOrEmpty(targetLocationId))
            {
                diagnostic = "探索 Effect 的目标地块不能为空。";
                return false;
            }

            if (!ReadStableIds(locations, "location", path.LocationIds) || !ReadStableIds(routes, "route", path.RouteIds))
            {
                diagnostic = "探索路径必须只包含稳定的 location/route 引用。";
                return false;
            }
            if (path.RouteIds.Count == 0 || path.LocationIds.Count == 0)
            {
                diagnostic = "探索路径不能为空。";
                return false;
            }
            if (paymentValues.Items != null)
            {
                for (int i = 0; i < paymentValues.Items.Count; i++)
                {
                    NormalizedValue item = paymentValues.Items[i];
                    NormalizedValue route;
                    NormalizedValue player;
                    if (!TryGet(item, "routeId", out route) || !TryGet(item, "playerId", out player) ||
                        route == null || route.Kind != NormalizedValueKind.StableReference || route.ReferenceType != "route" ||
                        player == null || player.Kind != NormalizedValueKind.Integer || player.IntegerValue < 0)
                    {
                        diagnostic = "路费接收人参数无效。";
                        return false;
                    }
                    recipients[route.ReferenceId] = (int)player.IntegerValue;
                }
            }
            return true;
        }

        private static bool ReadStableIds(NormalizedValue value, string referenceType, List<string> destination)
        {
            if (value.Items == null) return false;
            for (int i = 0; i < value.Items.Count; i++)
            {
                NormalizedValue item = value.Items[i];
                if (item == null || item.Kind != NormalizedValueKind.StableReference || item.ReferenceType != referenceType || string.IsNullOrEmpty(item.ReferenceId)) return false;
                destination.Add(item.ReferenceId);
            }
            return true;
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
}
