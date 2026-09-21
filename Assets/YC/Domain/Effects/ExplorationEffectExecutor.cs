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
    /// 探索是移动完成后的恢复型子流程：校验路径和路费，按资源 Effect 支付，
    /// 再把事件牌解析作为子 Effect 接上。客户端只提交稳定路线、地块和影响力槽位 ID。
    /// </summary>
    public sealed class ExplorationEffectExecutor
    {
        public const string DefinitionVersion = "1.0.0";

        private readonly IMapQueryService mapQuery;
        private readonly ExplorationService explorationService;
        private readonly RouteTollService routeTollService;
        private readonly EventDeckService eventDeckService;
        private readonly ResourceTokenService resourceTokenService;

        public ExplorationEffectExecutor(
            IMapQueryService mapQuery,
            ExplorationService explorationService,
            EventDeckService eventDeckService,
            ResourceTokenService resourceTokenService)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.explorationService = explorationService ?? throw new ArgumentNullException(nameof(explorationService));
            this.routeTollService = new RouteTollService(mapQuery);
            this.eventDeckService = eventDeckService ?? throw new ArgumentNullException(nameof(eventDeckService));
            this.resourceTokenService = resourceTokenService ?? throw new ArgumentNullException(nameof(resourceTokenService));
        }

        public static void Register(
            EffectRegistry registry,
            IMapQueryService mapQuery,
            ExplorationService explorationService,
            EventDeckService eventDeckService,
            ResourceTokenService resourceTokenService)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            var executor = new ExplorationEffectExecutor(mapQuery, explorationService, eventDeckService, resourceTokenService);
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

                EventColor color = StaticMapDefinitions.GetEventColor(targetLocationId);
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
