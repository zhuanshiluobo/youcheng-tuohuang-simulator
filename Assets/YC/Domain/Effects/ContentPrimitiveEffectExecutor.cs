using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Cards;
using YC.Domain.Economy;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Presentation;
using System.Linq;
using YC.Domain.Items;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    // 通用内容原语；不含角色 ID、卡面费用或奖励数值。
    public static class ContentPrimitiveEffectExecutor
    {
        public const string OverrideNextStartPlayer = LuaDomainEffectTypeIds.OverrideNextStartPlayer;
        public const string ActivateFacilityEntry = "effect.facility.activate_entry";
        public const string ChooseResources = "effect.resource.choose_amounts";
        public const string SelectInfluence = "effect.influence.select_action";
        public const string IncompleteEffect = "effect.content.incomplete";
        public const string LoadMarkers = "effect.card.load_markers", PayMarkers = "effect.card.pay_markers", StoreRemoved = "effect.card.store_removed";
        public const string ClearRouteRoad = "effect.map.clear_route_road";
        public const string QuantityInteractionTypeId = "effect.quantity.choose";
        public const string QuantityAnswerSchema = "bounded_amount";
        public static readonly string[] ContentFlowTypeIds = { LoadMarkers, PayMarkers, StoreRemoved, ClearRouteRoad, RewardAcquisitionFlow.TypeId };
        public static string[] ContentFlowFields(string type)
        {
            var fields = new List<string> { "apiVersion", "player" };
            if (type == RewardAcquisitionFlow.TypeId) fields.Add("poolRole");
            if (type == LoadMarkers || type == PayMarkers || type == StoreRemoved) fields.AddRange(new[] { "cardInstanceId", "localZoneId" });
            if (type == LoadMarkers) fields.Add("maxCount");
            if (type == PayMarkers) fields.Add("count");
            if (type == StoreRemoved) fields.Add("influenceItemId");
            if (type == ClearRouteRoad) fields.AddRange(new[] { "scoreRemovedPlayers", "scoreExecutor" });
            return fields.ToArray();
        }
        public static string ValidateContentFlow(string type, NormalizedValue args)
        {
            if (!ContentFlowTypeIds.Contains(type) || args?.Kind != NormalizedValueKind.Object ||
                args.Properties.Any(entry => !ContentFlowFields(type).Concat(new[] { "kind", "effectTypeId" }).Contains(entry.Name)) ||
                Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Number(args, "apiVersion", 0) != 1 ||
                !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1) return "content_flow_arguments_invalid";
            foreach (string key in ContentFlowFields(type).Except(new[] { "apiVersion", "player" }))
            {
                var value = Value(args, key);
                if (value == null) return "content_flow_arguments_incomplete";
                if (new[] { "maxCount", "count", "scoreRemovedPlayers", "scoreExecutor" }.Contains(key))
                {
                    int limit = key == "maxCount" ? 16 : key == "count" ? 16 : 99;
                    if (value.Kind != NormalizedValueKind.Integer || value.IntegerValue < (key == "count" ? 1 : 0) || value.IntegerValue > limit) return "content_flow_count_invalid";
                }
                else if (key == "destination")
                { if (value.Kind != NormalizedValueKind.Object || value.Properties.Count != 1 || Text(value, "zoneId").Length == 0) return "destination_rejected"; }
                else if (value.Kind != NormalizedValueKind.String || value.StringValue.Length == 0) return "content_flow_reference_invalid";
            }
            if (type == RewardAcquisitionFlow.TypeId && !new[] { "awards", "contracts" }.Contains(Text(args, "poolRole"))) return "reward_pool_rejected";
            return "";
        }
        public static string ValidateSaleArguments(NormalizedValue args)
        {
            if (Number(args, "apiVersion", 0) == 2)
                return args.Kind == NormalizedValueKind.Object &&
                    args.Properties.All(p => new[] { "apiVersion", "player", "saleMode", "pricePolicy", "interactionType", "promptKey", "kind", "effectTypeId" }.Contains(p.Name)) &&
                    Text(args, "saleMode") == "batch" && Text(args, "pricePolicy") == "core.resource_sale" &&
                    int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int actor) && actor > 0
                    ? "" : "sale_arguments_invalid";
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Any(p => !new[] { "apiVersion", "player", "targetPlayer", "resourceType", "saleMode", "unitPrice", "kind", "effectTypeId" }.Contains(p.Name)) ||
                Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Number(args, "apiVersion", 0) != 1 || Text(args, "saleMode") != "all" || !TryBasicResource(Text(args, "resourceType"), out _) ||
                Value(args, "unitPrice")?.Kind != NormalizedValueKind.Integer || Number(args, "unitPrice", 0) < 1 || Number(args, "unitPrice", 0) > 99 ||
                !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1 ||
                !int.TryParse(Text(args, "targetPlayer"), NumberStyles.None, CultureInfo.InvariantCulture, out int target) || target < 1) return "sale_arguments_invalid";
            return "";
        }
        public static bool TryValidateContentContinuations(GameState state, out string reason)
        {
            reason = "content_flow_state_invalid";
            var stages = new Dictionary<string, string[]> {
                { RewardAcquisitionFlow.TypeId, new[] { "", "choose_reward", "finished" } },
                { LoadMarkers, new[] { "", "choose_amount", "finished" } },
                { PayMarkers, new[] { "" } }, { StoreRemoved, new[] { "" } }, { ClearRouteRoad, new[] { "", "choose_route", "removing", "road", "scoring", "finished" } } };
            foreach (var node in state.EffectRuntime.EffectNodes.Where(n => ContentFlowTypeIds.Contains(n.EffectTypeId)))
            {
                if (ValidateContentFlow(node.EffectTypeId, node.NormalizedArguments).Length > 0 || Text(node.NormalizedArguments, "player") != node.PlayerId.ToString(CultureInfo.InvariantCulture) || state.FindPlayer(node.PlayerId) == null || !stages[node.EffectTypeId].Contains(CharacterLifecycleService.GetResumableFlowStage(node))) return false;
                if (node.EffectTypeId == RewardAcquisitionFlow.TypeId)
                {
                    bool finished = node.FlowStage == "finished";
                    string rewardId = Text(node.ContinuationData, "selectedRewardItemId"), role = Text(node.NormalizedArguments, "poolRole");
                    if (finished != Flag(node.ContinuationData, "acquisitionCompleted") || finished != (rewardId.Length > 0)) return false;
                    if (finished && (!state.ItemZones.Items.Any(i => i.ItemId == rewardId && i.TypeId == (role == "awards" ? "core.award_card" : "core.contract_card")) ||
                        node.PositionChanges.Count != 1 || !node.PositionChanges.Any(change => change.ItemId == rewardId &&
                            state.ItemZones.Zones.Any(z => z.ZoneId == change.From.ZoneId && z.TemplateId == "core.zone.reward_pool" && z.LocalZoneId == role) &&
                            state.ItemZones.Zones.Any(z => z.ZoneId == change.To.ZoneId && z.TemplateId == "core.zone.player_awards" && z.OwnerPlayerId == node.PlayerId)) ||
                        !Flag(node.NormalizedResult, "changed") || Text(node.NormalizedResult, "rewardItemId") != rewardId)) return false;
                    if (!finished && node.PositionChanges.Count > 0 || node.FlowStage == "choose_reward" && role != "awards") return false;
                }
                var selected = Value(node.ContinuationData, "selectedItemIds");
                if (selected != null && (selected.Kind != NormalizedValueKind.Array || selected.Items.Any(v => v.Kind != NormalizedValueKind.String || !state.ItemZones.Items.Any(i => i.ItemId == v.StringValue)) || selected.Items.Select(v => v.StringValue).Distinct().Count() != selected.Items.Count)) return false;
                if (node.EffectTypeId == LoadMarkers)
                {
                    var amount = Value(node.ContinuationData, "selectedAmount");
                    string maximum = "maxCount";
                    if (amount != null && (amount.Kind != NormalizedValueKind.Integer || amount.IntegerValue < 0 || amount.IntegerValue > Number(node.NormalizedArguments, maximum, 0))) return false;
                    if (node.FlowStage == "finished" && amount == null) return false;
                }
                if (node.EffectTypeId == ClearRouteRoad && !new[] { "", "choose_route" }.Contains(node.FlowStage))
                {
                    string route = Text(node.ContinuationData, "selectedRouteId");
                    if (!StaticMapDefinitions.Resolve(state.MapId).Routes.Any(r => r.RouteId == route) || selected == null || Number(node.ContinuationData, "nextIndex", -1) < 0 || Number(node.ContinuationData, "nextIndex", -1) > selected.Items.Count) return false;
                    var subjects = Value(node.ContinuationData, "processedSubjectKeys");
                    if (subjects?.Kind != NormalizedValueKind.Array || subjects.Items.Any(v => v.Kind != NormalizedValueKind.String || !(v.StringValue.StartsWith("player:") && int.TryParse(v.StringValue.Substring(7), out int player) && state.FindPlayer(player) != null || v.StringValue.StartsWith("company:")))) return false;
                }
            }
            reason = ""; return true;
        }
        private static bool TryBasicResource(string id, out ResourceType type)
        {
            type = id == "originium" ? ResourceType.Originium : id == "originium-shard" ? ResourceType.OriginiumShard : ResourceType.Iron;
            return new[] { "originium", "originium-shard", "iron" }.Contains(id);
        }
        public static void Register(EffectRegistry registry, IMapQueryService mapQuery = null, InfluenceService influenceService = null)
        {
            RouteResourceSelectionEffectExecutor.Register(registry);
            ContentLifecycleEffectExecutor.Register(registry);
            FacilitySelectionEffectExecutor.Register(registry);
            ExplorationSelectionEffectExecutor.Register(registry);
            if (!registry.TryGet(IncompleteEffect, out _)) registry.Register(new EffectRegistration(IncompleteEffect, context =>
                ItemTransferEffectExecutor.SkipIncomplete(context, ItemTransferEffectExecutor.Value(context.Node.NormalizedArguments, "missingFieldNames").Items.Select(value => value.StringValue)), EffectExecutorKind.Atomic));
            if (!registry.TryGet(OverrideNextStartPlayer, out _)) registry.Register(new EffectRegistration(OverrideNextStartPlayer, SetNextStart, EffectExecutorKind.Atomic, "1.0.0"));
            if (!registry.TryGet(SelectInfluence, out _)) registry.Register(new EffectRegistration(SelectInfluence, context => InfluenceAction(context, mapQuery, context.Registry.InfluenceService ?? influenceService), EffectExecutorKind.IntrinsicFlow, "v1"));
            Register(registry, ActivateFacilityEntry, ActivateEntry);
            Register(registry, ChooseResources, ChooseResourceAmounts);
            Register(registry, LuaDomainEffectTypeIds.ResourceSell, Sell);
            foreach (string type in ContentFlowTypeIds)
                if (!registry.TryGet(type, out _)) registry.Register(new EffectRegistration(type, context => ExecuteContentFlow(context, mapQuery, influenceService),
                    type == PayMarkers || type == StoreRemoved ? EffectExecutorKind.Atomic : EffectExecutorKind.IntrinsicFlow, "v1"));
        }
        private static void Register(EffectRegistry registry, string type, Func<EffectExecutionContext, EffectStepResult> execute)
        {
            if (!registry.TryGet(type, out _)) registry.Register(new EffectRegistration(type, execute, EffectExecutorKind.IntrinsicFlow, "1.0.0"));
        }
        private static EffectStepResult ExecuteContentFlow(EffectExecutionContext context, IMapQueryService mapQuery, InfluenceService influenceService)
        {
            CharacterLifecycleService.ResumeDeferredDependencies(context);
            string error = ValidateContentFlow(context.Node.EffectTypeId, context.Node.NormalizedArguments);
            if (error.Length > 0) return EffectStepResult.Failed(error);
            if (Text(context.Node.NormalizedArguments, "player") != context.Node.PlayerId.ToString(CultureInfo.InvariantCulture) || context.State.FindPlayer(context.Node.PlayerId) == null)
                return EffectStepResult.Failed("content_flow_permission_denied");
            if (context.ChildNodes.Any(child => child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted)) return EffectStepResult.Failed("content_flow_child_failed");
            switch (context.Node.EffectTypeId)
            {
                case RewardAcquisitionFlow.TypeId: return RewardAcquisitionFlow.Execute(context);
                case LoadMarkers: return LoadCardMarkers(context);
                case PayMarkers: return PayCardMarkers(context);
                case StoreRemoved: return StoreRemovedCube(context);
                default: return ClearAndRoad(context, mapQuery, influenceService);
            }
        }
        private static EffectStepResult ContentWait(EffectExecutionContext context, string stage, IEnumerable<string> candidates, int minimum = 1, int maximum = 1, bool allowDecline = false)
        {
            bool routeChoice = context.Node.EffectTypeId == ClearRouteRoad && stage == "choose_route";
            var request = new EffectInteractionSpec { InteractionTypeId = routeChoice ? "effect.route.choose" : "lua.choice",
                AnsweringPlayerId = context.Node.PlayerId, Visibility = "owner", PromptKey = routeChoice ? "effect.route.choose" : context.Node.EffectTypeId,
                MinSelections = minimum, MaxSelections = maximum, AllowDecline = allowDecline, AnswerSchema = maximum > 1 ? "candidate_id_array" : "candidate_id" };
            if (routeChoice)
                request.Presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:interaction.map.route") };
            request.CandidateIds.AddRange(candidates.OrderBy(id => id, StringComparer.Ordinal));
            return EffectStepResult.Continue(stage).AddInteraction(PrepareInteraction(context, request));
        }
        private static EffectStepResult QuantityWait(EffectExecutionContext context, int maximum, string kind,
            string rowId = "", string sourceItemId = "")
        {
            var parameters = Object("minAmount", NormalizedValue.CreateInteger(0),
                "maxAmount", NormalizedValue.CreateInteger(maximum), "amountKind", NormalizedValue.CreateString(kind));
            if (!string.IsNullOrEmpty(rowId))
            {
                parameters.Properties.Add(new NormalizedValueEntry { Name = "rowIds", Value = Strings(new[] { rowId }) });
                parameters.Properties.Add(new NormalizedValueEntry { Name = "rowMaximums", Value = NormalizedValue.CreateArray(new[] { NormalizedValue.CreateInteger(maximum) }) });
                parameters.Properties.Add(new NormalizedValueEntry { Name = "sourceItemId", Value = NormalizedValue.CreateString(sourceItemId) });
                parameters.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            }
            return EffectStepResult.Continue("choose_amount").AddInteraction(new EffectInteractionSpec {
                InteractionTypeId = QuantityInteractionTypeId, PromptKey = QuantityInteractionTypeId,
                AnsweringPlayerId = context.Node.PlayerId, Visibility = "owner", AnswerSchema = QuantityAnswerSchema,
                MinSelections = 0, MaxSelections = 0, AllowDecline = true,
                PromptParameters = parameters
            });
        }
        public static bool TryReadBoundedAmount(NormalizedValue parameters, NormalizedValue answer, out int amount)
        {
            amount = 0;
            return Value(parameters, "minAmount")?.Kind == NormalizedValueKind.Integer &&
                Value(parameters, "maxAmount")?.Kind == NormalizedValueKind.Integer &&
                Value(parameters, "minAmount").IntegerValue >= 0 &&
                Value(parameters, "maxAmount").IntegerValue <= int.MaxValue &&
                Value(parameters, "maxAmount").IntegerValue >= Value(parameters, "minAmount").IntegerValue &&
                answer?.Kind == NormalizedValueKind.String &&
                int.TryParse(answer.StringValue, NumberStyles.None, CultureInfo.InvariantCulture, out amount) &&
                amount >= Value(parameters, "minAmount").IntegerValue && amount <= Value(parameters, "maxAmount").IntegerValue;
        }
        private static bool Declined(EffectExecutionContext context) =>
            context.GetLatestInteractionAnswer()?.Kind == NormalizedValueKind.Boolean && !context.GetLatestInteractionAnswer().BooleanValue;
        private static ZoneInstanceState RequireCardZone(EffectExecutionContext context)
        {
            var args = context.Node.NormalizedArguments; var card = context.State.ItemZones.Items.Find(item => item.ItemId == Text(args, "cardInstanceId"));
            if (card?.ControllerSubject.Kind != ItemSubjectKind.Player || card.ControllerSubject.PlayerId != context.Node.PlayerId ||
                !context.State.ItemZones.Zones.Any(zone => zone.ZoneId == card.ZoneId && new[] { "core.zone.character_covered", "core.zone.character_persistent" }.Contains(zone.TemplateId))) return null;
            return context.State.ItemZones.Zones.Find(zone => zone.ParentKind == ZoneParentKind.Item && zone.ParentId == card.ItemId && zone.LocalZoneId == Text(args, "localZoneId") && zone.TemplateId == "core.zone.card_cube_storage");
        }
        private static EffectStepResult LoadCardMarkers(EffectExecutionContext context)
        {
            var zone = RequireCardZone(context); if (zone == null) return EffectStepResult.Failed("card_zone_permission_denied");
            string supply = FindZone(context.State, "player_markers", context.Node.PlayerId).ZoneId;
            var ids = context.State.ItemZones.Items.Where(item => item.ZoneId == supply && item.TypeId == "core.player_marker" && item.OwnerSubject.PlayerId == context.Node.PlayerId).Select(item => item.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList();
            int limit = Math.Min(Number(context.Node.NormalizedArguments, "maxCount", 0), ids.Count);
            int requested = context.Node.FlowStage == "choose_amount" && int.TryParse(Declined(context) ? "0" : Answer(context), NumberStyles.None, CultureInfo.InvariantCulture, out int answered) ? answered : Number(context.Node.NormalizedArguments, "maxCount", 0);
            if (requested >= 0 && CharacterLifecycleService.TryAwaitDeferredDependencies(context, new DeferredDependencyQuery {
                DependencyKind = DeferredDependencyKind.SupplyRequirement, OriginalSupplyZoneId = supply, RequiredTypeId = "core.player_marker", RequiredCount = requested }))
                return CharacterLifecycleService.DeferredDependencyWait();
            if (context.Node.FlowStage != "choose_amount" || !int.TryParse(Declined(context) ? "0" : Answer(context), NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 0 || count > limit)
                return QuantityWait(context, limit, "player_marker", "player_marker", Text(context.Node.NormalizedArguments, "cardInstanceId"));
            var selected = ids.Take(count).ToList(); Set(context.Node, "selectedAmount", NormalizedValue.CreateInteger(count));
            var moved = new ItemTransferService().RelocateItems(context, new ItemTransferRequest { ExecutingPlayerId = context.Node.PlayerId, ItemIds = selected,
                ExpectedSource = new ItemPositionRef { ZoneId = supply }, Destination = new ZoneRef { ZoneId = zone.ZoneId }, InsertionKind = "unordered", OperationProfileId = "core.cube_storage", RequiredCount = count });
            CharacterLifecycleService.ExitEmptyCarriers(context, new[] { zone.ZoneId });
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(moved.Changed), "movedCount", NormalizedValue.CreateInteger(moved.MovedCount), "movedItemIds", Strings(moved.MovedItemIds))).WithFlowStage("finished");
        }
        private static EffectStepResult PayCardMarkers(EffectExecutionContext context)
        {
            var zone = RequireCardZone(context); if (zone == null) return EffectStepResult.Failed("card_zone_permission_denied");
            int count = Number(context.Node.NormalizedArguments, "count", 0);
            if (CharacterLifecycleService.TryAwaitDeferredDependencies(context, new DeferredDependencyQuery {
                DependencyKind = DeferredDependencyKind.CardMarkerConsumption, SourceCardInstanceId = Text(context.Node.NormalizedArguments, "cardInstanceId"), RequiredCount = count }))
                return CharacterLifecycleService.DeferredDependencyWait();
            var cubes = context.State.ItemZones.Items.Where(item => item.ZoneId == zone.ZoneId && item.PhysicalKind == ItemPhysicalKind.Cube).OrderBy(item => item.ItemId, StringComparer.Ordinal).Take(count).ToList();
            if (cubes.Count < count) return EffectStepResult.Failed("insufficient_items");
            if (CharacterLifecycleService.TryAwaitEmptyCarrierExits(context, new[] { zone.ZoneId }, cubes.Select(item => item.ItemId))) return CharacterLifecycleService.DeferredDependencyWait();
            var requests = cubes.Select(item => new ItemTransferRequest { ExecutingPlayerId = context.Node.PlayerId, ItemIds = new List<string> { item.ItemId }, ExpectedSource = new ItemPositionRef { ZoneId = zone.ZoneId },
                Destination = new ZoneRef { ZoneId = CharacterLifecycleService.CubeSupply(context.State, item).ZoneId }, InsertionKind = "unordered", OperationProfileId = "core.cube_storage", RequiredCount = 1 }).ToList();
            var service = new ItemTransferService(); foreach (var request in requests) { var validation = service.Validate(context.State, request); if (!validation.IsValid) return EffectStepResult.Failed(validation.ReasonCode); }
            foreach (var request in requests) service.RelocateItems(context, request);
            CharacterLifecycleService.ExitEmptyCarriers(context, new[] { zone.ZoneId });
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(true), "movedCount", NormalizedValue.CreateInteger(count), "movedItemIds", Strings(cubes.Select(item => item.ItemId))));
        }
        private static EffectStepResult StoreRemovedCube(EffectExecutionContext context)
        {
            var target = RequireCardZone(context); var item = context.State.ItemZones.Items.Find(value => value.ItemId == Text(context.Node.NormalizedArguments, "influenceItemId"));
            if (target?.Configuration?.EntryPolicyId != "core.removed_owned_influence" || item?.PhysicalKind != ItemPhysicalKind.Cube ||
                item.OwnerSubject.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId != context.Node.PlayerId) return EffectStepResult.Failed("card_zone_permission_denied");
            var source = context.State.ItemZones.Zones.Find(zone => zone.ZoneId == item.ZoneId);
            var owner = context.State.EffectRuntime.EffectNodes.Find(node => node.EffectId == source?.OwnerEffectId);
            if (CharacterLifecycleService.TryAwaitDeferredDependencies(context, new DeferredDependencyQuery {
                DependencyKind = DeferredDependencyKind.ExactItemUse, ItemIds = new List<string> { item.ItemId } })) return CharacterLifecycleService.DeferredDependencyWait();
            if (EffectTreeExecutor.TryFindDeferredResponse(context.State, context.Node.EffectId, out var response))
            {
                if (source?.TemplateId != "core.zone.effect_staging" || response.ReservedItemId != item.ItemId || response.SourceCardInstanceId != target.ParentId ||
                    response.AnsweringPlayerId != context.Node.PlayerId || response.ReservationZoneId != source.ZoneId || source.OwnerEffectId != response.ResponseRootEffectId)
                    return EffectStepResult.Failed("deferred_destination_permission_denied");
            }
            else if (source?.TemplateId != "core.zone.effect_staging" || Text(owner?.ContinuationData, "pendingItemId") != item.ItemId || Flag(owner.ContinuationData, "destinationResolved") ||
                Text(owner.ContinuationData, "destinationHandoffResponseId").Length > 0 || !new[] { "remove", "replace" }.Contains(Text(owner.ContinuationData, "pendingDepartureKind"))) return EffectStepResult.Failed("pending_destination_required");
            var moved = new InfluenceDestinationResolver().Resolve(context, item.ItemId, target.ZoneId);
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(moved.Changed), "movedCount", NormalizedValue.CreateInteger(moved.MovedCount), "movedItemIds", Strings(moved.MovedItemIds)));
        }
        private static EffectStepResult ClearAndRoad(EffectExecutionContext context, IMapQueryService query, InfluenceService influence)
        {
            var map = query ?? new MapQueryService(StaticMapDefinitions.Resolve(context.State.MapId)); var service = influence ?? new InfluenceService(map);
            var node = context.Node; string routeId = Text(node.ContinuationData, "selectedRouteId");
            if (node.FlowStage.Length == 0 || node.FlowStage == "choose_route")
            {
                var routes = service.GetRoadCandidateRouteIds(context.State);
                if (routes.Count == 0) return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(false), "reasonCode", NormalizedValue.CreateString("no_candidates")));
                routeId = node.FlowStage == "choose_route" ? Answer(context) : "";
                if (!routes.Contains(routeId)) return ContentWait(context, "choose_route", routes);
                var order = context.State.EffectRuntime.PlayerOrderSnapshot.Count > 0 ? context.State.EffectRuntime.PlayerOrderSnapshot.ToList() : context.State.Players.Select(player => player.PlayerId).ToList();
                if (!order.Contains(node.PlayerId) || order.Distinct().Count() != context.State.Players.Count || order.Any(id => context.State.FindPlayer(id) == null)) return EffectStepResult.Failed("player_order_invalid");
                int first = order.IndexOf(node.PlayerId); order = Enumerable.Range(0, order.Count).Select(index => order[(first + index) % order.Count]).ToList();
                var ids = context.State.Map.Influences.Where(item => item.RouteId == routeId).OrderBy(item => order.IndexOf(item.PlayerId) < 0 ? int.MaxValue : order.IndexOf(item.PlayerId)).ThenBy(item => item.InfluenceId, StringComparer.Ordinal).Select(item => item.InfluenceId).ToList();
                Set(node, "selectedRouteId", NormalizedValue.CreateString(routeId)); Set(node, "selectedItemIds", Strings(ids)); Set(node, "nextIndex", NormalizedValue.CreateInteger(0)); Set(node, "processedSubjectKeys", Strings(Array.Empty<string>()));
            }
            if (node.FlowStage == "scoring") return EffectStepResult.Completed(Object("routeId", NormalizedValue.CreateString(routeId), "removedPlayerIds", NormalizedValue.CreateArray(Value(node.ContinuationData, "processedSubjectKeys").Items.Where(v => v.StringValue.StartsWith("player:")).Select(v => NormalizedValue.CreateInteger(int.Parse(v.StringValue.Substring(7)))).ToList()))).WithFlowStage("finished");
            var subjects = Value(node.ContinuationData, "processedSubjectKeys")?.Items.Select(value => value.StringValue).ToList() ?? new List<string>();
            if (node.FlowStage == "road")
            {
                var step = EffectStepResult.Continue("scoring");
                foreach (string subject in subjects) if (subject.StartsWith("player:") && int.TryParse(subject.Substring(7), out int player) && Number(node.NormalizedArguments, "scoreRemovedPlayers", 0) > 0)
                    step.AddChild(ResourceEffectSpecFactory.GainScore(player, Number(node.NormalizedArguments, "scoreRemovedPlayers", 0), node.SourceId));
                if (Number(node.NormalizedArguments, "scoreExecutor", 0) > 0) step.AddChild(ResourceEffectSpecFactory.GainScore(node.PlayerId, Number(node.NormalizedArguments, "scoreExecutor", 0), node.SourceId));
                return step;
            }
            int next = Number(node.ContinuationData, "nextIndex", 0);
            if (node.FlowStage == "removing")
            {
                var result = context.ChildNodes.Last().NormalizedResult;
                if (Value(result, "removedItemIds")?.Items.Count > 0)
                    foreach (var subject in Value(result, "originalSubjectKeys").Items) if (!subjects.Contains(subject.StringValue)) subjects.Add(subject.StringValue);
                Set(node, "processedSubjectKeys", Strings(subjects));
            }
            var frozen = Value(node.ContinuationData, "selectedItemIds").Items;
            while (next < frozen.Count)
            {
                string id = frozen[next++].StringValue; Set(node, "nextIndex", NormalizedValue.CreateInteger(next));
                if (context.State.Map.Influences.Any(item => item.InfluenceId == id && item.RouteId == routeId))
                    return EffectStepResult.Continue("removing").AddChild(InfluenceEffectSpecFactory.RemoveInfluence(node.PlayerId, id, node.SourceId));
            }
            return EffectStepResult.Continue("road").AddChild(EffectSpec.Create(LuaDomainEffectTypeIds.PlaceRoad, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(node.PlayerId.ToString(CultureInfo.InvariantCulture)), "routeId", NormalizedValue.CreateString(routeId)), node.PlayerId));
        }
        private static EffectStepResult SetNextStart(EffectExecutionContext context)
        {
            string target = Text(context.Node.NormalizedArguments, "targetPlayer");
            if (target.StartsWith("p", StringComparison.Ordinal)) target = target.Substring(1);
            if (!int.TryParse(target, out int playerId) || context.State.FindPlayer(playerId) == null) return EffectStepResult.Failed("invalid_player");
            if (Text(context.Node.NormalizedArguments, "scope", "current_round") != "current_round") return EffectStepResult.Failed("unsupported_start_player_scope");
            context.State.EffectRuntime.PendingNextRoundStartPlayerId = playerId;
            return EffectStepResult.Completed(NormalizedValue.CreateInteger(playerId));
        }
        private static EffectStepResult ActivateEntry(EffectExecutionContext context)
        {
            if (context.Node.FlowStage == "activated")
            {
                foreach (var child in context.ChildNodes)
                {
                    if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted)
                        return EffectStepResult.Failed("facility_entry_child_failed");
                    if (child.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("设施入场尚未完成。");
                }
                return EffectStepResult.Completed(NormalizedValue.CreateString("activated"));
            }
            string id = Text(context.Node.NormalizedArguments, "instanceId");
            foreach (var placement in context.State.Map.Facilities)
            {
                if (placement == null || placement.ContentInstanceId != id || placement.PlayerId != context.Node.PlayerId) continue;
                var definition = FacilityCardDatabase.Get(context.State, placement.FacilityCardId);
                if (definition == null || !definition.HasEntryEffect) break;
                return EffectStepResult.Continue("activated").AddChild(FacilityEntryEffectSpecFactory.Activate(
                    context.Node.PlayerId, placement.FacilityCardId, id, placement.CityBoardSlotIndex, context.Node.SourceId));
            }
            return EffectStepResult.Failed("facility_entry_target_invalid");
        }
        private static string Text(NormalizedValue args, string key, string fallback = "")
        {
            if (args?.Properties != null) foreach (var item in args.Properties) if (item.Name == key) return item.Value?.StringValue ?? fallback;
            return fallback;
        }
        private static int Number(NormalizedValue args, string key, int fallback)
        {
            if (args?.Properties != null) foreach (var item in args.Properties) if (item.Name == key && item.Value.Kind == NormalizedValueKind.Integer) return (int)item.Value.IntegerValue;
            return fallback;
        }
        private static string Answer(EffectExecutionContext context)
        {
            var answer = context.GetLatestInteractionAnswer();
            if (answer?.Kind == NormalizedValueKind.Array && answer.Items.Count == 1) answer = answer.Items[0];
            return answer?.Kind == NormalizedValueKind.String ? answer.StringValue : answer?.ReferenceId ?? "";
        }
        private static EffectStepResult Wait(EffectExecutionContext context, List<string> candidates, string stage, string type, string prompt, bool multiple = false)
        {
            var request = new EffectInteractionSpec { InteractionTypeId = type, AnsweringPlayerId = context.Node.PlayerId,
                Visibility = "owner", PromptKey = prompt, MinSelections = 1, MaxSelections = multiple ? 4 : 1,
                AnswerSchema = multiple ? "candidate_id_array" : "candidate_id" };
            request.CandidateIds.AddRange(candidates);
            return EffectStepResult.Continue(stage).AddInteraction(request);
        }
        private static EffectStepResult InfluenceAction(EffectExecutionContext context, IMapQueryService mapQuery, InfluenceService influenceService)
        {
            var node = context.Node;
            var args = node.NormalizedArguments;
            string operation = Text(args, "operation"), filter = Text(args, "subjectFilter", Text(args, "ownerFilter", "self"));
            int count = Number(args, "count", 1), required = Number(args, "requiredCount", 0);
            if (!new[] { "move", "replace", "remove" }.Contains(operation) || !new[] { "self", "player", "opponent", "any" }.Contains(filter) || count < 1 || count > 16 || required < 0 || required > count)
                return EffectStepResult.Failed("influence_selection_arguments_invalid");
            var map = mapQuery ?? new MapQueryService(StaticMapDefinitions.Resolve(context.State.MapId));
            var service = influenceService ?? new InfluenceService(map);
            string scope = Text(args, "candidateScope");
            if (!new[] { "", "resource_points", "routes" }.Contains(scope)) return EffectStepResult.Failed("influence_scope_invalid");
            if (Flag(args, "requireCompleteSelection")) return CompleteInfluenceSelection(context, service, operation, filter, count, required, scope);
            int index = ItemTransferEffectExecutor.Number(node.ContinuationData, "nextIndex");
            var processed = ItemTransferEffectExecutor.Value(node.ContinuationData, "processedSubjectKeys")?.Items?.Select(value => value.StringValue).ToList() ?? new List<string>();
            var selected = ItemTransferEffectExecutor.Value(node.ContinuationData, "selectedItemIds")?.Items?.Select(value => value.StringValue).ToList() ?? new List<string>();
            var excluded = ItemTransferEffectExecutor.Value(args, "excludedSubjectKeys")?.Items?.Select(value => value.StringValue).ToList() ?? new List<string>();
            excluded.AddRange(processed);
            if (node.FlowStage == "applied")
            {
                var child = context.ChildNodes.Last();
                if (child.Status == EffectNodeStatus.Failed) return EffectStepResult.Failed(child.FailureReason);
                if (Text(args, "distinctPolicy") == "subject" || Text(args, "distinctPolicy") == "owner")
                    foreach (var key in ItemTransferEffectExecutor.Value(child.NormalizedResult, "originalSubjectKeys")?.Items ?? new List<NormalizedValue>())
                        if (!processed.Contains(key.StringValue)) processed.Add(key.StringValue);
                index++;
                ItemTransferEffectExecutor.Set(node, "nextIndex", NormalizedValue.CreateInteger(index));
                ItemTransferEffectExecutor.Set(node, "processedSubjectKeys", ItemTransferEffectExecutor.Strings(processed));
                excluded.AddRange(processed);
            }
            else if (node.FlowStage != "" && node.FlowStage != "awaiting") return EffectStepResult.Failed("invalid_flow_stage");
            var ids = ItemZoneLegacyProjection.IsActive(context.State)
                ? new InfluenceSubjectQuery().GetCandidateItemIds(context.State, node.PlayerId, filter, excluded).ToList()
                : context.State.Map.Influences.Where(item => InfluenceSubjectQuery.Matches(LegacySubjectKey(item), node.PlayerId, filter) && !excluded.Contains(LegacySubjectKey(item)))
                    .Select(item => InfluenceIdentity.GetStableId(context.State, item)).ToList();
            ids.RemoveAll(id => !InScope(context.State, service.FindInfluence(context.State, id), scope));
            if (Text(args, "distinctPolicy") == "instance") ids.RemoveAll(selected.Contains);
            var candidates = new List<string>();
            foreach (var id in ids)
            {
                if (operation != "move") { candidates.Add(id); continue; }
                var placement = service.FindInfluence(context.State, id);
                foreach (var location in map.Map.Locations)
                    for (int slot = 0; slot < location.InfluenceSlotCount; slot++) AddMoveCandidate(candidates, context, service, id, InfluenceService.GetLocationSlotId(location.LocationId, slot), placement);
                foreach (var route in map.Map.Routes)
                    for (int slot = 0; slot < route.InfluenceSlotCount; slot++) AddMoveCandidate(candidates, context, service, id, InfluenceService.GetRouteSlotId(route.RouteId, slot), placement);
            }
            candidates.Sort(StringComparer.Ordinal);
            if (index >= count || candidates.Count == 0)
                return index < required ? EffectStepResult.Failed("insufficient_items") : EffectStepResult.Completed(ItemTransferEffectExecutor.Object(
                    "changed", NormalizedValue.CreateBoolean(index > 0), "completedCount", NormalizedValue.CreateInteger(index),
                    "reasonCode", NormalizedValue.CreateString(index == 0 ? "no_candidates" : "limit_reached"), "originalSubjectKeys", ItemTransferEffectExecutor.Strings(processed)));
            if (node.FlowStage == "awaiting")
            {
                string answer = Answer(context);
                if (candidates.Contains(answer))
                {
                    string id = answer;
                    EffectSpec child;
                    if (operation == "move")
                    {
                        var parts = answer.Split('|'); id = parts[1];
                        child = InfluenceEffectSpecFactory.MoveInfluence(node.PlayerId, id, parts[2]);
                    }
                    else child = operation == "replace" ? InfluenceEffectSpecFactory.ReplaceInfluence(node.PlayerId, id, node.SourceId) : InfluenceEffectSpecFactory.RemoveInfluence(node.PlayerId, id, node.SourceId);
                    if (!selected.Contains(id)) selected.Add(id);
                    ItemTransferEffectExecutor.Set(node, "selectedItemIds", ItemTransferEffectExecutor.Strings(selected));
                    // 旧基础状态结果缺主体数组，仍从操作前的真实实例保存原主体。
                    if (!ItemZoneLegacyProjection.IsActive(context.State) && (Text(args, "distinctPolicy") == "subject" || Text(args, "distinctPolicy") == "owner"))
                    {
                        string key = LegacySubjectKey(service.FindInfluence(context.State, id)); if (!processed.Contains(key)) processed.Add(key);
                        ItemTransferEffectExecutor.Set(node, "processedSubjectKeys", ItemTransferEffectExecutor.Strings(processed));
                    }
                    return EffectStepResult.Continue("applied").AddChild(child);
                }
            }
            string prompt = index == 0 ? Text(args, "promptKey") : Text(args, "nextPromptKey", Text(args, "promptKey"));
            var interaction = InfluenceSelectionInteraction(node, operation, prompt, 1, "candidate_id");
            interaction.CandidateIds.AddRange(candidates);
            return EffectStepResult.Continue("awaiting").AddInteraction(ItemTransferEffectExecutor.PrepareInteraction(context, interaction));
        }
        private static bool InScope(GameState state, InfluencePlacement placement, string scope)
        {
            if (scope.Length == 0) return true;
            if (placement == null) return false;
            if (scope == "routes") return placement.RouteId.Length > 0;
            return state.Map.ResourceTokens.Any(token => placement.LocationId == token.LocationId);
        }
        private static EffectStepResult CompleteInfluenceSelection(EffectExecutionContext context, InfluenceService service, string operation, string filter, int count, int required, string scope)
        {
            if (operation == "move" || required != count) return EffectStepResult.Failed("complete_selection_arguments_invalid");
            var node = context.Node;
            var selected = Value(node.ContinuationData, "selectedItemIds")?.Items.Select(v => v.StringValue).ToList() ?? new List<string>();
            int index = Number(node.ContinuationData, "nextIndex", 0), completed = Number(node.ContinuationData, "completedCount", 0);
            var candidates = new InfluenceSubjectQuery().GetCandidateItemIds(context.State, node.PlayerId, filter).Where(id => InScope(context.State, service.FindInfluence(context.State, id), scope)).OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (node.FlowStage == "")
            { if (candidates.Count < count) return EffectStepResult.Failed("insufficient_items"); }
            else if (node.FlowStage == "choose_complete")
            {
                var answer = context.GetLatestInteractionAnswer();
                var values = answer?.Kind == NormalizedValueKind.Array ? answer.Items : new List<NormalizedValue> { answer };
                selected = values.Select(v => v?.Kind == NormalizedValueKind.StableReference ? v.ReferenceId : v?.StringValue ?? "").ToList();
                if (selected.Count != count || selected.Distinct().Count() != count || selected.Any(id => !candidates.Contains(id)))
                { if (candidates.Count < count) return EffectStepResult.Failed("insufficient_items"); selected.Clear(); }
                else Set(node, "selectedItemIds", Strings(selected));
            }
            else if (node.FlowStage == "applied_complete")
            {
                var child = context.ChildNodes.Last();
                if (child.Status != EffectNodeStatus.Completed || Value(child.NormalizedResult, operation == "remove" ? "removedItemIds" : "replacedItemIds")?.Items.Count != 1) return EffectStepResult.Failed("incomplete_influence_payment");
                index++; completed++; Set(node, "nextIndex", NormalizedValue.CreateInteger(index)); Set(node, "completedCount", NormalizedValue.CreateInteger(completed));
            }
            else return EffectStepResult.Failed("invalid_flow_stage");
            if (selected.Count == 0)
            {
                var request = InfluenceSelectionInteraction(node, operation, Text(node.NormalizedArguments, "promptKey"), count, "candidate_id_array");
                request.CandidateIds.AddRange(candidates); return EffectStepResult.Continue("choose_complete").AddInteraction(PrepareInteraction(context, request));
            }
            if (index == count) return EffectStepResult.Completed(Object("completedCount", NormalizedValue.CreateInteger(completed), "changed", NormalizedValue.CreateBoolean(completed == count)));
            if (!candidates.Contains(selected[index])) return EffectStepResult.Failed("incomplete_influence_payment");
            return EffectStepResult.Continue("applied_complete").AddChild(operation == "remove" ? InfluenceEffectSpecFactory.RemoveInfluence(node.PlayerId, selected[index], node.SourceId) : InfluenceEffectSpecFactory.ReplaceInfluence(node.PlayerId, selected[index], node.SourceId));
        }
        private static EffectInteractionSpec InfluenceSelectionInteraction(EffectNodeRuntimeState node, string operation, string prompt, int count, string schema)
        {
            string type = Text(node.NormalizedArguments, "interactionType", "lua.choice");
            if (operation == "move" && (type.Length == 0 || type == "lua.choice")) type = "character.ability.choice.move";
            if (prompt.Length == 0 || prompt == "effect.influence.choose") prompt = "effect.influence." + operation + ".choose_target";
            var request = new EffectInteractionSpec { InteractionTypeId = type, AnsweringPlayerId = node.PlayerId,
                Visibility = "owner", PromptKey = prompt, MinSelections = count, MaxSelections = count, AnswerSchema = schema };
            if (node.Presentation?.Interaction == null)
                request.Presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:interaction.map.influence." + operation) };
            return request;
        }
        private static string LegacySubjectKey(InfluencePlacement placement) => placement?.OwnerSubject?.SubjectType == "company"
            ? "company:" + (string.IsNullOrEmpty(placement.OwnerSubject.DefinitionId) ? placement.OwnerSubject.InstanceId : placement.OwnerSubject.DefinitionId)
            : placement == null || placement.PlayerId < 1 ? "" : "player:" + placement.PlayerId.ToString(CultureInfo.InvariantCulture);
        private static void AddMoveCandidate(List<string> candidates, EffectExecutionContext context, InfluenceService service, string itemId, string slotId, InfluencePlacement placement)
        {
            bool legal = ItemZoneLegacyProjection.IsActive(context.State)
                ? service.ValidateItemPlacement(context.State, new ItemZoneQuery().FindItem(context.State, itemId).OwnerSubject, slotId).Succeeded
                : placement?.PlayerId == context.Node.PlayerId && service.CanMove(context.State, context.Node.PlayerId, itemId, slotId).IsValid;
            if (legal) candidates.Add("move|" + itemId + "|" + slotId);
        }
        private static EffectStepResult ChooseResourceAmounts(EffectExecutionContext context)
        {
            int amount = Number(context.Node.NormalizedArguments, "amount", 0);
            if (amount < 1 || amount > 99) return EffectStepResult.Failed("invalid_resource_choice_amount");
            if (context.Node.FlowStage == "applied") return EffectStepResult.Completed(NormalizedValue.CreateInteger(amount));
            if (context.Node.FlowStage == "awaiting")
            {
                string encoded = Answer(context);
                if (!YC.Domain.Interactions.ResourceAllocationAnswer.TryParse(encoded,
                    new[] { "Originium", "OriginiumShard", "Iron" }, amount, out var amounts, out _))
                    return EffectStepResult.Failed("invalid_resource_allocation");
                var result = EffectStepResult.Continue("applied");
                foreach (var entry in amounts)
                    if (entry.Value > 0) result.AddChild(ResourceEffectSpecFactory.Gain(context.Node.PlayerId, (YC.Domain.Rules.ResourceType)Enum.Parse(typeof(YC.Domain.Rules.ResourceType), entry.Key), entry.Value, context.Node.SourceId));
                return result;
            }
            var request = new EffectInteractionSpec { InteractionTypeId = "facility.entry.choice", AnsweringPlayerId = context.Node.PlayerId,
                Visibility = "owner", PromptKey = "effect.resource.choose_amounts", AnswerSchema = "resource_allocation", MinSelections = 1, MaxSelections = 1,
                PromptParameters = Object("totalAmount", NormalizedValue.CreateInteger(amount),
                    "rowIds", Strings(new[] { "Originium", "OriginiumShard", "Iron" }),
                    "rowMaximums", NormalizedValue.CreateArray(new[] { amount, amount, amount }.Select(value => NormalizedValue.CreateInteger(value)).ToArray())) };
            request.CandidateIds.AddRange(new[] { "Originium", "OriginiumShard", "Iron", "total:" + amount });
            return EffectStepResult.Continue("awaiting").AddInteraction(request);
        }

        internal static EffectStepResult MoveCards(EffectExecutionContext context)
        {
            if (Text(context.Node.NormalizedArguments, "sourceZone") != "discard" || Text(context.Node.NormalizedArguments, "destinationZone") != "hand") return EffectStepResult.Failed("unsupported_character_card_zone_move");
            var player = context.State.FindPlayer(context.Node.PlayerId);
            if (player == null) return EffectStepResult.Failed("invalid_player");
            var cards = new List<string>(player.DiscardCardIds);
            cards.RemoveAll(id => id == player.CoveredCharacterCardId);
            foreach (var id in cards) { player.DiscardCardIds.Remove(id); if (!player.HandCardIds.Contains(id)) player.HandCardIds.Add(id); }
            return EffectStepResult.Completed(NormalizedValue.CreateInteger(cards.Count));
        }
        internal static EffectStepResult CycleFacility(EffectExecutionContext context)
        {
            if (Text(context.Node.NormalizedArguments, "sourceZone") != "supply" || Text(context.Node.NormalizedArguments, "destinationZone") != "deck_bottom") return EffectStepResult.Failed("unsupported_facility_card_zone_move");
            if (context.Node.FlowStage == "awaiting")
            {
                string card = Answer(context);
                if (!context.State.Decks.FacilitySupply.Contains(card)) return EffectStepResult.Failed("facility_supply_target_invalid");
                FacilitySupplyService.ReturnSupplyCardToDeck(context.State.Decks.FacilitySupply, context.State.Decks.FacilityDeck, card);
                return EffectStepResult.Completed(NormalizedValue.CreateString(card));
            }
            if (context.State.Decks.FacilitySupply.Count == 0) return EffectStepResult.Completed(NormalizedValue.CreateString("no_target"));
            return Wait(context, new List<string>(context.State.Decks.FacilitySupply), "awaiting", Text(context.Node.NormalizedArguments, "interactionType", "lua.choice"), Text(context.Node.NormalizedArguments, "promptKey", "effect.facility.choose_supply"));
        }
        private static EffectStepResult Sell(EffectExecutionContext context)
        {
            var args = context.Node.NormalizedArguments;
            bool batch = Number(args, "apiVersion", 0) == 2;
            if (batch && (ValidateSaleArguments(args).Length > 0 || Text(args, "player") != context.Node.PlayerId.ToString(CultureInfo.InvariantCulture))) return EffectStepResult.Failed("sale_arguments_invalid");
            if (Value(args, "apiVersion") != null && !batch)
            {
                if (Number(args, "apiVersion", 0) != 1 || Text(args, "saleMode") != "all" || Text(args, "player") != context.Node.PlayerId.ToString(CultureInfo.InvariantCulture) ||
                    !int.TryParse(Text(args, "targetPlayer"), NumberStyles.None, CultureInfo.InvariantCulture, out int targetId) || context.State.FindPlayer(targetId) == null ||
                    !TryBasicResource(Text(args, "resourceType"), out var type) || Number(args, "unitPrice", 0) < 1 || Number(args, "unitPrice", 0) > 99) return EffectStepResult.Failed("sale_arguments_invalid");
                var target = context.State.FindPlayer(targetId); int amount = target.Resources.Get(type), revenue = checked(amount * Number(args, "unitPrice", 0));
                target.Resources.Set(type, 0); target.Resources.GoldVoucher = checked(target.Resources.GoldVoucher + revenue);
                return EffectStepResult.Completed(Object("amount", NormalizedValue.CreateInteger(amount), "revenue", NormalizedValue.CreateInteger(revenue), "targetPlayer", NormalizedValue.CreateInteger(targetId), "resourceType", NormalizedValue.CreateString(Text(args, "resourceType"))));
            }
            var player = context.State.FindPlayer(context.Node.PlayerId);
            if (player == null) return EffectStepResult.Failed("invalid_player");
            string[] ids = { "originium", "originium-shard", "iron", "pure-originium" };
            if (context.Node.FlowStage == "awaiting")
            {
                var answer = context.GetLatestInteractionAnswer();
                var values = answer?.Kind == NormalizedValueKind.Array ? answer.Items : new List<NormalizedValue> { answer };
                var amounts = new int[4]; var seen = new HashSet<int>();
                if (values.Count == 0 || values.Count > 4) return EffectStepResult.Failed("sale_answer_invalid");
                foreach (var value in values)
                {
                    var parts = (value?.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : value?.StringValue ?? "").Split('|');
                    if (parts.Length != 3 || parts[0] != "sale" || !int.TryParse(parts[2], out var amount) || amount < 0) return EffectStepResult.Failed("sale_answer_invalid");
                    int i = Array.IndexOf(ids, parts[1]);
                    if (i < 0 || !seen.Add(i)) return EffectStepResult.Failed("sale_answer_invalid");
                    amounts[i] = amount;
                }
                var result = new ResourceSaleService().Sell(player.Resources, new ResourceSaleRequest(amounts[0], amounts[1], amounts[2], amounts[3]));
                if (!result.Succeeded) return EffectStepResult.Failed("sale_insufficient_resources");
                return EffectStepResult.Completed(batch ? Object("revenue", NormalizedValue.CreateInteger(result.Revenue),
                    "soldCount", NormalizedValue.CreateInteger(amounts.Sum()), "finished", NormalizedValue.CreateBoolean(amounts.Sum() == 0),
                    "amounts", NormalizedValue.CreateArray(amounts.Select(a => NormalizedValue.CreateInteger(a)).ToList())) : NormalizedValue.CreateInteger(result.Revenue));
            }
            var candidates = new List<string> { "sale|originium|0" };
            int[] maximums = { player.Resources.Originium, player.Resources.OriginiumShard, player.Resources.Iron, player.Resources.PureOriginium };
            for (int i = 0; i < ids.Length; i++) for (int amount = 1; amount <= maximums[i]; amount++) candidates.Add("sale|" + ids[i] + "|" + amount);
            return Wait(context, candidates, "awaiting", Text(context.Node.NormalizedArguments, "interactionType", "lua.choice"), Text(context.Node.NormalizedArguments, "promptKey", "effect.resource.sell"), true);
        }
    }
}
