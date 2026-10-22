using System;
using System.Linq;
using YC.Domain.Items;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Cards;
using YC.Domain.Economy;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static class FacilityEntryEffectSpecFactory
    {
        public static EffectSpec Activate(
            int playerId,
            string facilityId,
            string contentInstanceId,
            int sourceSlotIndex,
            string sourceId = "facility.build")
        {
            return new EffectSpec(
                FacilityEntryEffectTypeIds.Activate,
                Object(
                    Entry("facilityId", NormalizedValue.CreateString(facilityId ?? string.Empty)),
                    Entry("facilityInstanceId", NormalizedValue.CreateStableReference("facility", contentInstanceId ?? string.Empty)),
                    Entry("sourceSlotIndex", NormalizedValue.CreateInteger(sourceSlotIndex))))
            {
                PlayerId = playerId,
                SourceId = sourceId ?? string.Empty,
                DefinitionVersion = FacilityEntryEffectExecutor.DefinitionVersion,
                Visibility = "owner"
            };
        }

        public static EffectSpec Behavior(
            int playerId,
            string operation,
            int sourceSlotIndex,
            string sourceId = "facility.entry")
        {
            return new EffectSpec(
                FacilityEntryEffectTypeIds.Behavior,
                Object(
                    Entry("operation", NormalizedValue.CreateString(operation ?? string.Empty)),
                    Entry("sourceSlotIndex", NormalizedValue.CreateInteger(sourceSlotIndex))))
            {
                PlayerId = playerId,
                SourceId = sourceId ?? string.Empty,
                DefinitionVersion = FacilityEntryEffectExecutor.DefinitionVersion,
                Visibility = "owner"
            };
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

    public static class FacilityBuildEffectSpecFactory
    {
        public static EffectSpec Build(
            int playerId,
            string facilityId,
            int cityBoardSlotIndex,
            string paymentMode,
            bool reserveForFree,
            string sourceId,
            bool consumeMainAction = true, string expectedSourceZoneId = "")
        {
            return new EffectSpec(
                FacilityEntryEffectTypeIds.Build,
                NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry { Name = "facilityId", Value = NormalizedValue.CreateString(facilityId ?? string.Empty) },
                    new NormalizedValueEntry { Name = "cityBoardSlotIndex", Value = NormalizedValue.CreateInteger(cityBoardSlotIndex) },
                    new NormalizedValueEntry { Name = "paymentMode", Value = NormalizedValue.CreateString(paymentMode ?? BuildFacilityService.PaymentModeAuto) },
                    new NormalizedValueEntry { Name = "reserveForFree", Value = NormalizedValue.CreateBoolean(reserveForFree) },
                    new NormalizedValueEntry { Name = "consumeMainAction", Value = NormalizedValue.CreateBoolean(consumeMainAction) },
                    new NormalizedValueEntry { Name = "expectedSourceZoneId", Value = NormalizedValue.CreateString(expectedSourceZoneId) }
                }))
            {
                PlayerId = playerId,
                SourceId = sourceId ?? string.Empty,
                DefinitionVersion = FacilityBuildEffectExecutor.DefinitionVersion,
                Visibility = "owner"
            };
        }
    }

    /// <summary>
    /// 设施入口主链：先提交建设后的入口 Event，再由内容目录的 Lua handler
    /// 返回通用 Resource/Influence/Move/Explore 或再次进入本 Effect 的行为族。
    /// </summary>
    public sealed class FacilityEntryEffectExecutor
    {
        public const string DefinitionVersion = "1.3.0";
        private const string ActivatedStage = "activated";

        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            EffectRegistration ignored;
            if (!registry.TryGet(FacilityEntryEffectTypeIds.Activate, out ignored))
            {
                var executor = new FacilityEntryEffectExecutor();
                registry.Register(new EffectRegistration(
                    FacilityEntryEffectTypeIds.Activate,
                    executor.ExecuteActivation,
                    EffectExecutorKind.IntrinsicFlow,
                    DefinitionVersion)
                {
                    Validator = ValidateActivationSpec
                });
            }

            if (!registry.TryGet(FacilityEntryEffectTypeIds.Behavior, out ignored))
            {
                registry.Register(new EffectRegistration(
                    FacilityEntryEffectTypeIds.Behavior,
                    ExecuteBehavior,
                    EffectExecutorKind.IntrinsicFlow,
                    DefinitionVersion)
                {
                    Validator = ValidateBehaviorSpec
                });
            }

            FacilityBuildEffectExecutor.Register(registry);
        }

        private EffectStepResult ExecuteActivation(EffectExecutionContext context)
        {
            if (context.Node.PendingOutcome != EffectPendingOutcome.None)
            {
                return EffectStepResult.Completed(context.Node.NormalizedResult == null
                    ? NormalizedValue.CreateNull() : context.Node.NormalizedResult.Clone());
            }

            string facilityId = ReadString(context.Node.NormalizedArguments, "facilityId", string.Empty);
            string instanceId = ReadReference(context.Node.NormalizedArguments, "facilityInstanceId", "facility");
            int slotIndex = ReadInt(context.Node.NormalizedArguments, "sourceSlotIndex", -1);
            FacilityCardDefinition facility = FacilityCardDatabase.Get(context.State, facilityId);
            FacilityPlacement placement = FindPlacement(context.State, context.Node.PlayerId, facilityId, instanceId, slotIndex);
            if (facility == null || !facility.HasEntryEffect || placement == null)
            {
                return EffectStepResult.Failed("invalid_facility_entry");
            }

            string stage = context.Node.FlowStage ?? string.Empty;
            if (string.IsNullOrEmpty(stage))
            {
                FacilityInstanceStateService.EnsureIdentity(context.State, placement);
                FacilityInstanceRuntimeState instance = FacilityInstanceStateService.Find(context.State, placement.ContentInstanceId);
                if (instance == null) return EffectStepResult.Failed("facility_instance_limit");
                FacilityInstanceStateService.RecordActivation(context.State, placement.ContentInstanceId, context.State.Round);
                if (instance.AppliedBehaviorIds == null) instance.AppliedBehaviorIds = new List<string>();
                if (!instance.AppliedBehaviorIds.Contains(facility.EffectId)) instance.AppliedBehaviorIds.Add(facility.EffectId);

                return EffectStepResult.Continue(ActivatedStage).AddEvent(new EffectEventRequest
                {
                    EventId = StableIdFactory.Create("event", context.Node.EffectId, FacilityEntryEventTypeIds.Activated, placement.ContentInstanceId),
                    EventType = FacilityEntryEventTypeIds.Activated,
                    SourceEffectId = context.Node.EffectId,
                    OwnerNodeId = context.Node.EffectId,
                    RouteKey = facility.EffectId,
                    TargetEntityId = placement.ContentInstanceId,
                    PlayerId = context.Node.PlayerId,
                    ResponseKind = RuleEventResponseKind.Effects,
                    DefinitionVersion = DefinitionVersion,
                    Visibility = "owner",
                    SemanticKey = "activated",
                    Payload = CreateActivationPayload(context.State, facility, placement)
                });
            }

            if (stage == ActivatedStage)
            {
                if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("设施入场 Event 响应尚未结束。");
                if (HasFailedChild(context)) return EffectStepResult.Failed("facility_entry_response_failed");
                return EffectStepResult.Completed(NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry { Name = "facilityId", Value = NormalizedValue.CreateString(facilityId) },
                    new NormalizedValueEntry { Name = "contentInstanceId", Value = NormalizedValue.CreateString(placement.ContentInstanceId) },
                    new NormalizedValueEntry { Name = "behaviorFamily", Value = NormalizedValue.CreateString(FacilityBehaviorFamilyResolver.Resolve(facility)) }
                }));
            }

            return EffectStepResult.Failed("invalid_flow_stage");
        }

        private static NormalizedValue CreateActivationPayload(
            GameState state,
            FacilityCardDefinition facility,
            FacilityPlacement placement)
        {
            ResourceType rewardType;
            int rewardAmount;
            ReadReward(facility.OnBuiltReward, out rewardType, out rewardAmount);
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                Entry("playerId", NormalizedValue.CreateInteger(placement.PlayerId)),
                Entry("facilityId", NormalizedValue.CreateString(facility.FacilityId)),
                Entry("contentInstanceId", NormalizedValue.CreateString(placement.ContentInstanceId)),
                Entry("effectId", NormalizedValue.CreateString(facility.EffectId)),
                Entry("behaviorFamily", NormalizedValue.CreateString(FacilityBehaviorFamilyResolver.Resolve(facility))),
                Entry("sourceSlotIndex", NormalizedValue.CreateInteger(placement.CityBoardSlotIndex)),
                Entry("rewardResourceType", NormalizedValue.CreateString(rewardAmount > 0 ? rewardType.ToString() : string.Empty)),
                Entry("rewardAmount", NormalizedValue.CreateInteger(rewardAmount)),
                Entry("round", NormalizedValue.CreateInteger(state.Round))
            });
        }

        private static void ReadReward(ResourceSet reward, out ResourceType type, out int amount)
        {
            type = ResourceType.Originium;
            amount = 0;
            if (reward == null) return;
            if (reward.Originium > 0) { type = ResourceType.Originium; amount = reward.Originium; return; }
            if (reward.OriginiumShard > 0) { type = ResourceType.OriginiumShard; amount = reward.OriginiumShard; return; }
            if (reward.Iron > 0) { type = ResourceType.Iron; amount = reward.Iron; return; }
            if (reward.PureOriginium > 0) { type = ResourceType.PureOriginium; amount = reward.PureOriginium; return; }
            if (reward.GoldVoucher > 0) { type = ResourceType.GoldVoucher; amount = reward.GoldVoucher; }
        }

        private static string ValidateActivationSpec(EffectSpec spec)
        {
            if (spec == null || spec.NormalizedArguments == null || spec.NormalizedArguments.Kind != NormalizedValueKind.Object)
                return "设施入口 Effect 参数必须是对象。";
            string facilityId = ReadString(spec.NormalizedArguments, "facilityId", string.Empty);
            string instanceId = ReadReference(spec.NormalizedArguments, "facilityInstanceId", "facility");
            return string.IsNullOrEmpty(facilityId) || string.IsNullOrEmpty(instanceId)
                ? "设施入口 Effect 必须提供 facilityId 和 contentInstanceId。"
                : string.Empty;
        }

        private static string ValidateBehaviorSpec(EffectSpec spec)
        {
            if (spec == null || spec.NormalizedArguments == null || spec.NormalizedArguments.Kind != NormalizedValueKind.Object)
                return "设施行为 Effect 参数必须是对象。";
            return string.IsNullOrEmpty(ReadString(spec.NormalizedArguments, "operation", string.Empty))
                ? "设施行为 Effect 必须提供 operation。"
                : string.Empty;
        }

        private static FacilityPlacement FindPlacement(
            GameState state,
            int playerId,
            string facilityId,
            string instanceId,
            int slotIndex)
        {
            if (state == null || state.Map == null || state.Map.Facilities == null) return null;
            for (int i = 0; i < state.Map.Facilities.Count; i++)
            {
                FacilityPlacement placement = state.Map.Facilities[i];
                if (placement == null || placement.PlayerId != playerId || placement.FacilityCardId != facilityId) continue;
                FacilityInstanceStateService.EnsureIdentity(state, placement);
                if ((!string.IsNullOrEmpty(instanceId) && placement.ContentInstanceId == instanceId) ||
                    (string.IsNullOrEmpty(instanceId) && placement.CityBoardSlotIndex == slotIndex)) return placement;
            }

            return null;
        }

        private static bool HasNonTerminalChild(EffectExecutionContext context)
        {
            IList<EffectNodeRuntimeState> children = context.ChildNodes;
            for (int i = 0; i < children.Count; i++)
            {
                if (!IsTerminal(children[i].Status)) return true;
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

        private static bool IsTerminal(EffectNodeStatus status)
        {
            return status == EffectNodeStatus.Completed || status == EffectNodeStatus.Failed || status == EffectNodeStatus.Faulted;
        }

        internal static EffectStepResult ExecuteBehavior(EffectExecutionContext context)
        {
            throw new KernelException(EffectFaultCodes.DefinitionVersionMismatch, "设施旧行为适配已撤下，请使用外部 Lua 与通用 Effect。");
        }

        private static string ReadString(NormalizedValue value, string name, string fallback)
        {
            NormalizedValue item;
            if (!TryGet(value, name, out item) || item == null) return fallback;
            return item.Kind == NormalizedValueKind.String ? item.StringValue ?? fallback :
                item.Kind == NormalizedValueKind.StableReference ? item.ReferenceId ?? fallback : fallback;
        }

        private static string ReadString(NormalizedValue value)
        {
            return value != null && value.Kind == NormalizedValueKind.String ? value.StringValue ?? string.Empty : string.Empty;
        }

        private static string ReadReference(NormalizedValue value, string name, string referenceType)
        {
            NormalizedValue item;
            if (!TryGet(value, name, out item) || item == null) return string.Empty;
            return item.Kind == NormalizedValueKind.StableReference &&
                   (string.IsNullOrEmpty(item.ReferenceType) || item.ReferenceType == referenceType)
                ? item.ReferenceId ?? string.Empty
                : ReadString(value, name, string.Empty);
        }

        private static int ReadInt(NormalizedValue value, string name, int fallback)
        {
            NormalizedValue item;
            return TryGet(value, name, out item) && item != null && item.Kind == NormalizedValueKind.Integer
                ? (int)item.IntegerValue : fallback;
        }

        private static bool TryGet(NormalizedValue value, string name, out NormalizedValue item)
        {
            if (value != null && value.Kind == NormalizedValueKind.Object && value.Properties != null)
            {
                for (int i = 0; i < value.Properties.Count; i++)
                {
                    NormalizedValueEntry entry = value.Properties[i];
                    if (entry != null && entry.Name == name)
                    {
                        item = entry.Value;
                        return true;
                    }
                }
            }
            item = null;
            return false;
        }

        private static NormalizedValueEntry Entry(string name, NormalizedValue value)
        {
            return new NormalizedValueEntry { Name = name, Value = value };
        }


    }

    public sealed class FacilityBuildEffectExecutor
    {
        public const string DefinitionVersion = "1.2.0";

        public const string PlacementInteractionType = "facility.build.placement";
        public const string PaymentTypeId = "effect.facility.pay_build_cost";

        // 仅复核本次冻结来源；取牌后不要求已按空区政策弃置的载体继续在场。
        public static bool TryValidateBuildSources(GameState state, out string diagnostic)
        {
            diagnostic = "build_source_restore_invalid";
            foreach (var node in state.EffectRuntime.EffectNodes)
            {
                if (node.Status == EffectNodeStatus.Completed || node.Status == EffectNodeStatus.Failed || node.Status == EffectNodeStatus.Faulted) continue;
                var expectedArgument = Value(node.NormalizedArguments, "expectedSourceZoneId");
                if (node.EffectTypeId == FacilityEntryEffectTypeIds.Build && expectedArgument != null && expectedArgument.Kind != NormalizedValueKind.String) return false;
                if (node.EffectTypeId == FacilitySelectionEffectExecutor.TypeId && Value(node.ContinuationData, "selectedSourceZoneId") != null)
                {
                    string selected = Value(node.ContinuationData, "selectedItemIds")?.Items.FirstOrDefault()?.StringValue;
                    var item = state.ItemZones.Items.Find(value => value.ItemId == selected);
                    string source = Text(node.ContinuationData, "selectedSourceZoneId");
                    if (item == null || source.Length == 0) return false;
                    if (node.FlowStage == "payment" && item.ZoneId != source) return false;
                    if (node.FlowStage == "built" && !node.ChildEffectIds.Any(id => state.EffectRuntime.EffectNodes.Any(child => child.EffectId == id && child.EffectTypeId == FacilityEntryEffectTypeIds.Build && Text(child.NormalizedArguments, "expectedSourceZoneId") == source && Text(child.NormalizedArguments, "facilityId") == selected))) return false;
                }
                if (node.EffectTypeId != FacilityEntryEffectTypeIds.Build || Text(node.NormalizedArguments, "expectedSourceZoneId").Length == 0 || node.FlowStage.Length == 0) continue;
                string expected = Text(node.NormalizedArguments, "expectedSourceZoneId");
                string selectedId = Value(node.ContinuationData, "selectedItemIds")?.Items.FirstOrDefault()?.StringValue;
                var selectedItem = state.ItemZones.Items.Find(item => item.ItemId == selectedId);
                var sourceZone = state.ItemZones.Zones.Find(zone => zone.ZoneId == expected);
                var move = node.NestedEffects.FirstOrDefault();
                if (selectedItem == null || sourceZone == null || Text(node.NormalizedArguments, "facilityId") != selectedId ||
                    Text(Value(move?.NormalizedArguments, "expectedSource"), "zoneId") != expected ||
                    Text(Value(move?.NormalizedArguments, "itemRefs")?.Items.FirstOrDefault(), "itemId") != selectedId ||
                    Text(node.ContinuationData, "originalSupplySlotZoneId") != (sourceZone.TemplateId == "core.zone.facility_supply_slot" ? expected : "")) return false;
                if (node.FlowStage == "pay" && selectedItem.ZoneId != expected) return false;
                var payment = state.EffectRuntime.EffectNodes.Find(child => child.ParentEffectId == node.EffectId && child.EffectTypeId == PaymentTypeId);
                if (payment != null && Text(payment.NormalizedArguments, "facilityId") != selectedItem.DefinitionId) return false;
            }
            diagnostic = ""; return true;
        }

        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            EffectRegistration ignored;
            if (registry.TryGet(FacilityEntryEffectTypeIds.Build, out ignored)) return;
            var executor = new FacilityBuildEffectExecutor();
            registry.Register(new EffectRegistration(PaymentTypeId, PayEntityCost, EffectExecutorKind.IntrinsicFlow));
            registry.Register(new EffectRegistration(
                FacilityEntryEffectTypeIds.Build,
                executor.Execute,
                EffectExecutorKind.IntrinsicFlow,
                DefinitionVersion)
            {
                Validator = ValidateSpec
            });
        }

        private EffectStepResult Execute(EffectExecutionContext context)
        {
            if (ItemZoneLegacyProjection.IsActive(context.State)) return ExecuteEntity(context);
            if (context.Node.PendingOutcome != EffectPendingOutcome.None)
                return EffectStepResult.Completed(context.Node.NormalizedResult == null ? NormalizedValue.CreateNull() : context.Node.NormalizedResult.Clone());
            string facilityId = ReadString(context.Node.NormalizedArguments, "facilityId", string.Empty);
            int slot = ReadInt(context.Node.NormalizedArguments, "cityBoardSlotIndex", -1);
            string payment = ReadString(context.Node.NormalizedArguments, "paymentMode", BuildFacilityService.PaymentModeAuto);
            bool reserve = ReadBool(context.Node.NormalizedArguments, "reserveForFree", false);
            if (string.IsNullOrEmpty(context.Node.FlowStage) || context.Node.FlowStage == "awaiting_placement")
            {
                var service = BuildFacilityService.CreateForEffectTree();
                BuildFacilityResult result;
                if (context.Node.FlowStage == "awaiting_placement")
                {
                    var answer = context.GetLatestInteractionAnswer();
                    if (answer != null && answer.Kind == NormalizedValueKind.Array && answer.Items.Count == 1) answer = answer.Items[0];
                    var encoded = answer == null ? null : answer.Kind == NormalizedValueKind.StableReference ? answer.ReferenceId : answer.StringValue;
                    if (!int.TryParse(encoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot))
                        return EffectStepResult.Failed("invalid_build_placement");
                    result = service.PlacePaidFacility(context.State, context.Node.PlayerId, facilityId, slot, payment);
                }
                else if (!reserve && slot < 0)
                {
                    result = service.PayAndRefillForPlacement(context.State, context.Node.PlayerId, facilityId, payment);
                    if (!result.Succeeded) return EffectStepResult.Failed(result.Validation.ErrorCode.ToString(), NormalizedValue.CreateString(result.Validation.Reason));
                    var request = new EffectInteractionSpec
                    {
                        InteractionTypeId = PlacementInteractionType, AnsweringPlayerId = context.Node.PlayerId,
                        Visibility = "owner", PromptKey = "facility.build.place_paid", AnswerSchema = "candidate_id",
                        MinSelections = 1, MaxSelections = 1, AllowDecline = false,
                        PromptParameters = context.Node.NormalizedArguments.Clone()
                    };
                    for (var index = 0; index < BuildFacilityService.CityBoardSlotCount; index++)
                        if (service.ValidatePaidPlacement(context.State, context.Node.PlayerId, facilityId, index).IsValid)
                            request.CandidateIds.Add(index.ToString(CultureInfo.InvariantCulture));
                    // FlowStage 随请求一起持久化，恢复时只放置，不重复扣费或补牌。
                    return EffectStepResult.Continue("awaiting_placement").AddInteraction(request);
                }
                else result = reserve
                    ? service.BuildReserveForFree(context.State, context.Node.PlayerId, facilityId, slot)
                    : service.BuildForEffectTree(context.State, context.Node.PlayerId, facilityId, slot, payment);
                if (!result.Succeeded) return EffectStepResult.Failed(result.Validation.ErrorCode.ToString(), NormalizedValue.CreateString(result.Validation.Reason));
                if (!reserve && ReadBool(context.Node.NormalizedArguments, "consumeMainAction", true))
                {
                    new MainActionBudgetService().SpendCompletedMainAction(context.State, context.Node.PlayerId);
                }
                FacilityPlacement placement = null;
                for (int i = 0; i < context.State.Map.Facilities.Count; i++)
                    if (context.State.Map.Facilities[i] != null && context.State.Map.Facilities[i].PlayerId == context.Node.PlayerId && context.State.Map.Facilities[i].CityBoardSlotIndex == slot)
                        placement = context.State.Map.Facilities[i];
                var step = EffectStepResult.Continue("awaiting_entry");
                if (!reserve && placement != null && result.Facility.HasEntryEffect)
                    step.AddChild(FacilityEntryEffectSpecFactory.Activate(context.Node.PlayerId, result.Facility.FacilityId, placement.ContentInstanceId, slot, "facility.build.effect"));
                if (placement == null || reserve || !result.Facility.HasEntryEffect) return EffectStepResult.Completed(NormalizedValue.CreateString(result.Facility.FacilityId));
                return step;
            }

            if (context.Node.FlowStage == "awaiting_entry")
            {
                for (int i = 0; i < context.ChildNodes.Count; i++) if (!IsTerminal(context.ChildNodes[i].Status)) return EffectStepResult.NoProgress("额外建设的入场效果尚未结束。");
                return context.ChildNodes.Count > 0 && context.ChildNodes[0].Status != EffectNodeStatus.Completed
                    ? EffectStepResult.Failed("additional_build_entry_failed")
                    : EffectStepResult.Completed(NormalizedValue.CreateString(facilityId));
            }
            return EffectStepResult.Failed("invalid_build_effect_stage");
        }

        private static EffectStepResult PayEntityCost(EffectExecutionContext context)
        {
            var preparation = context.Node.Payment?.Preparation;
            if (preparation != null)
            {
                if (preparation.IsCancelled || !ResourcePaymentPreparationService.TryQuotePreparation(context.State, context.Node, out var preparedCost, out var reason))
                    return EffectStepResult.Failed("payment_preparation_source_changed");
                return ResourceEffectExecutor.ExecutePayment(context, preparedCost, Object("paid", NormalizedValue.CreateBoolean(true)));
            }
            var owner = context.State.EffectRuntime.EffectNodes.Find(node => node.EffectId == context.Node.ParentEffectId);
            if (owner?.EffectTypeId == FacilityEntryEffectTypeIds.Build && Text(owner.NormalizedArguments, "expectedSourceZoneId").Length > 0)
            {
                var frozen = owner.NestedEffects.FirstOrDefault();
                var request = ItemTransferEffectExecutor.Request(frozen.NormalizedArguments);
                if (request.ExpectedSource.ZoneId != Text(owner.NormalizedArguments, "expectedSourceZoneId") ||
                    request.ItemIds.Count != 1 || request.ItemIds[0] != Text(owner.NormalizedArguments, "facilityId")) return EffectStepResult.Failed("source_zone_changed");
                var item = context.State.ItemZones.Items.Find(value => value.ItemId == request.ItemIds[0]);
                if (item?.ZoneId != request.ExpectedSource.ZoneId || item.DefinitionId != Text(context.Node.NormalizedArguments, "facilityId")) return EffectStepResult.Failed("source_zone_changed");
                var source = context.State.ItemZones.Zones.Find(zone => zone.ZoneId == item.ZoneId);
                if (source?.TemplateId == "core.zone.card_facility_storage" && !FacilityBuildCandidateQuery.IsActiveStoredFacility(context.State, source, owner.PlayerId)) return EffectStepResult.Failed("source_zone_changed");
            }
            var definition = FacilityCardDatabase.Get(context.State, Text(context.Node.NormalizedArguments, "facilityId"));
            var player = context.State.FindPlayer(context.Node.PlayerId);
            if (definition == null || player == null) return EffectStepResult.Failed("invalid_build_target");
            var bound = Value(context.Node.NormalizedArguments, "maxGoldVoucherCost");
            if (bound != null && (bound.Kind != NormalizedValueKind.Integer || bound.IntegerValue < 0 || definition.GoldVoucherCost > bound.IntegerValue)) return EffectStepResult.Failed("facility_cost_above_limit");
            string mode = Text(context.Node.NormalizedArguments, "paymentMode");
            var cost = mode == BuildFacilityService.PaymentModeFree ? new ResourceSet() : mode == BuildFacilityService.PaymentModeGold ? new ResourceSet { GoldVoucher = definition.GoldVoucherCost } : new FacilityBuildCostService().GetEffectiveResourceCost(context.State, player, definition);
            return ResourceEffectExecutor.ExecutePayment(context, cost, Object("paid", NormalizedValue.CreateBoolean(true)));
        }
        private static EffectStepResult ExecuteEntity(EffectExecutionContext context)
        {
            var state = context.State;
            var node = context.Node;
            var service = BuildFacilityService.CreateForEffectTree();
            if (node.PendingOutcome != EffectPendingOutcome.None) return EffectStepResult.Completed(node.NormalizedResult);
            if (context.ChildNodes.Any(child => child.Status == EffectNodeStatus.Failed)) throw new InvalidOperationException("entity_build_child_failed");
            var query = new ItemZoneQuery();
            string stage = node.FlowStage;
            int slot = Number(node.NormalizedArguments, "cityBoardSlotIndex", -1);
            string preparedId = Text(node.NormalizedArguments, "preparedPaymentOwnerEffectId");
            var preparedPayment = preparedId.Length == 0 ? null : state.EffectRuntime.EffectNodes.Find(value => value.EffectId == preparedId);
            if (preparedId.Length > 0 && (preparedPayment?.Payment?.Preparation?.PromotedActionEffectId != node.EffectId ||
                !preparedPayment.Payment.IsCommitted || preparedPayment.Payment.Preparation.ActionKind != "build" ||
                preparedPayment.Payment.Preparation.TargetId != Text(node.NormalizedArguments, "facilityId")))
                return EffectStepResult.Failed("payment_preparation_context_invalid");
            if (stage.Length == 0)
            {
                string requested = Text(node.NormalizedArguments, "facilityInstanceId", Text(node.NormalizedArguments, "facilityId"));
                var item = query.FindItem(state, requested);
                if (item == null)
                {
                    var matches = state.ItemZones.Items.Where(value => value.DefinitionId == requested && state.ItemZones.Zones.Find(zone => zone.ZoneId == value.ZoneId)?.TemplateId == "core.zone.facility_supply_slot").ToList();
                    if (matches.Count != 1) return EffectStepResult.Failed("facility_instance_required");
                    item = matches[0];
                }
                var source = query.FindZone(state, item.ZoneId);
                string expectedSource = Text(node.NormalizedArguments, "expectedSourceZoneId");
                if (expectedSource.Length > 0 && item.ZoneId != expectedSource) return EffectStepResult.Failed("source_zone_changed");
                var definition = FacilityCardDatabase.Get(context.State, item.DefinitionId);
                bool reserve = ReadBool(node.NormalizedArguments, "reserveForFree", false);
                bool free = reserve || Text(node.NormalizedArguments, "paymentMode") == BuildFacilityService.PaymentModeFree;
                if (definition == null || !ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(item.TypeId, "core.facility_card") ||
                    !(source.TemplateId == "core.zone.facility_supply_slot" || FacilitySelectionEffectExecutor.IsActiveStoredFacility(state, source, node.PlayerId) || source.LocalZoneId == "facility_deck" || reserve && source.TemplateId == "core.zone.box_reserve")) return EffectStepResult.Failed("invalid_build_source");
                if (FacilityCardDatabase.PlayerHasBuiltUniqueFacility(state, state.FindPlayer(node.PlayerId), definition) || BuildFacilityService.FindFirstEmptyCityBoardSlot(state, node.PlayerId) < 0 || slot >= 0 && !service.ValidatePaidPlacement(state, node.PlayerId, item.DefinitionId, slot).IsValid) return EffectStepResult.Failed("invalid_build_slot");
                var bound = Value(node.NormalizedArguments, "maxGoldVoucherCost");
                if (bound != null && (bound.Kind != NormalizedValueKind.Integer || bound.IntegerValue < 0 || definition.GoldVoucherCost > bound.IntegerValue)) return EffectStepResult.Failed("facility_cost_above_limit");
                var resources = new FacilityBuildCostService().GetEffectiveResourceCost(state, state.FindPlayer(node.PlayerId), definition);
                string requestedMode = Text(node.NormalizedArguments, "paymentMode", "auto");
                string mode = free ? BuildFacilityService.PaymentModeFree : preparedPayment != null ? requestedMode :
                    requestedMode == BuildFacilityService.PaymentModeResources && ResourceEffectExecutor.CanPayWithStoredMarkers(state, node.PlayerId, resources) ? requestedMode :
                    BuildFacilityService.ResolvePaymentMode(state, state.FindPlayer(node.PlayerId), definition, resources, requestedMode);
                if (mode.Length == 0) return EffectStepResult.Failed("insufficient_resources");
                // 在支付前验证完整取牌权限；暂存区随 OwnerEffect 稳定保存。
                var declaration = new CardZoneDefinition { LocalZoneId = "build_pending", ZoneTemplateId = "core.zone.effect_staging", AcceptedItemKinds = new System.Collections.Generic.List<ItemPhysicalKind> { ItemPhysicalKind.Card }, AcceptedTypeIds = new System.Collections.Generic.List<string> { "core.facility_card" }, CapacityPolicy = "bounded", Capacity = 1, OrderingKind = ZoneOrderingKind.Unordered, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents", VisibilityPolicyId = ItemZoneVisibility.Owner };
                if (!new ItemZoneStateFactory().TryCreateEffectStaging(state, node.EffectId, declaration, out string staging, out string reason)) throw new InvalidOperationException(reason);
                node = context.Node;
                var transfer = new ItemTransferRequest { ItemIds = new System.Collections.Generic.List<string> { item.ItemId }, ExecutingPlayerId = node.PlayerId, ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId }, Destination = new ZoneRef { ZoneId = staging }, RequiredCount = 1, InsertionKind = "unordered", OperationProfileId = "core.facility_card_transfer" };
                var validation = new ItemTransferService().Validate(state, transfer);
                if (!validation.IsValid) return EffectStepResult.Failed(validation.ReasonCode);
                Set(node, "selectedItemIds", Strings(new[] { item.ItemId }));
                Set(node, "originalSupplySlotZoneId", NormalizedValue.CreateString(source.TemplateId == "core.zone.facility_supply_slot" ? source.ZoneId : ""));
                Set(node, "paid", NormalizedValue.CreateBoolean(false)); Set(node, "refilled", NormalizedValue.CreateBoolean(false));
                // 原源区是取牌子式的 ExpectedSource，保存于嵌套参数，不拼阶段字符串。
                node.NestedGroupSizes = new System.Collections.Generic.List<int> { 1 };
                node.NestedEffects = new System.Collections.Generic.List<EffectSpecRuntimeState> { MoveSpec(node.PlayerId, new[] { item.ItemId }, item.ZoneId, staging, "unordered", 1).ToRuntimeState() };
                var paymentArgs = Object("facilityId", NormalizedValue.CreateString(item.DefinitionId), "paymentMode", NormalizedValue.CreateString(mode));
                if (bound != null) { paymentArgs.Properties.Add(new NormalizedValueEntry { Name = "maxGoldVoucherCost", Value = bound.Clone() }); paymentArgs.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name)); }
                var payStep = EffectStepResult.Continue("pay");
                if (preparedPayment == null) payStep.AddChild(EffectSpec.Create(PaymentTypeId, paymentArgs, node.PlayerId));
                return payStep;
            }
            string selected = Value(node.ContinuationData, "selectedItemIds")?.Items.FirstOrDefault()?.StringValue;
            var selectedItem = query.FindItem(state, selected);
            if (selectedItem == null) throw new InvalidOperationException("entity_build_state_invalid");
            var facility = FacilityCardDatabase.Get(context.State, selectedItem.DefinitionId);
            if (stage == "pay")
            {
                Set(node, "paid", NormalizedValue.CreateBoolean(true));
                return EffectStepResult.Continue("take").AddChild(EffectRegistry.FromRuntimeSpec(node.NestedEffects[0]));
            }
            if (stage == "take")
            {
                string original = Text(node.ContinuationData, "originalSupplySlotZoneId");
                var step = EffectStepResult.Continue("refill");
                if (original.Length > 0) step.AddChild(RefillSpec(node.PlayerId, original));
                return step;
            }
            if (stage == "refill")
            {
                Set(node, "refilled", NormalizedValue.CreateBoolean(true));
                if (slot < 0)
                {
                    var request = new EffectInteractionSpec { InteractionTypeId = PlacementInteractionType, AnsweringPlayerId = node.PlayerId, Visibility = "owner", PromptKey = "facility.build.place_paid", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, AllowDecline = false };
                    for (int index = 0; index < BuildFacilityService.CityBoardSlotCount; index++) if (service.ValidatePaidPlacement(state, node.PlayerId, selectedItem.DefinitionId, index).IsValid) request.CandidateIds.Add(index.ToString(CultureInfo.InvariantCulture));
                    var paymentNode = preparedPayment ?? context.ChildNodes.Single(child => child.EffectTypeId == PaymentTypeId);
                    request.PromptParameters = Object("facilityId", NormalizedValue.CreateString(selectedItem.DefinitionId), "paymentMode", NormalizedValue.CreateString(Text(paymentNode.NormalizedArguments, "paymentMode")));
                    return EffectStepResult.Continue("awaiting_placement").AddInteraction(PrepareInteraction(context, request));
                }
            }
            if (stage == "refill" || stage == "awaiting_placement")
            {
                if (stage == "awaiting_placement" && !int.TryParse(Answer(context), NumberStyles.None, CultureInfo.InvariantCulture, out slot)) return EffectStepResult.Failed("invalid_build_placement");
                if (!service.ValidatePaidPlacement(state, node.PlayerId, selectedItem.DefinitionId, slot).IsValid) return EffectStepResult.Failed("invalid_build_placement");
                var destination = FindZone(state, "city_cell_" + slot, node.PlayerId);
                return EffectStepResult.Continue("place").AddChild(MoveSpec(node.PlayerId, new[] { selected }, selectedItem.ZoneId, destination.ZoneId, "unordered", 1));
            }
            if (stage == "place")
            {
                FacilityInstanceStateService.EnsureIdentity(state, state.Map.Facilities.Single(placement => placement.ContentInstanceId == selected));
                // 设施本身可为零分或负分；此业务提交保存原卡面分数，不伪造正金额奖励。
                state.FindPlayer(node.PlayerId).Score += facility.Score;
                return EffectStepResult.Continue("score");
            }
            if (stage == "score")
            {
                if (ReadBool(node.NormalizedArguments, "consumeMainAction", true) && !ReadBool(node.NormalizedArguments, "reserveForFree", false)) new MainActionBudgetService().SpendCompletedMainAction(state, node.PlayerId);
                var placement = state.Map.Facilities.Single(value => value.ContentInstanceId == selected);
                var step = EffectStepResult.Continue("awaiting_entry");
                if (facility.HasEntryEffect && !ReadBool(node.NormalizedArguments, "reserveForFree", false)) step.AddChild(FacilityEntryEffectSpecFactory.Activate(node.PlayerId, facility.FacilityId, selected, placement.CityBoardSlotIndex, node.SourceId));
                return step;
            }
            if (stage == "awaiting_entry") return EffectStepResult.Completed(NormalizedValue.CreateString(facility.FacilityId));
            throw new InvalidOperationException("entity_build_stage_invalid");
        }

        private static string ValidateSpec(EffectSpec spec)
        {
            return spec == null || string.IsNullOrEmpty(ReadString(spec.NormalizedArguments, "facilityId", string.Empty))
                ? "设施建设 Effect 必须提供 facilityId。" : string.Empty;
        }

        private static bool IsTerminal(EffectNodeStatus status)
        {
            return status == EffectNodeStatus.Completed || status == EffectNodeStatus.Failed || status == EffectNodeStatus.Faulted;
        }

        private static string ReadString(NormalizedValue value, string name, string fallback)
        {
            NormalizedValue item;
            return TryGet(value, name, out item) && item != null && item.Kind == NormalizedValueKind.String
                ? item.StringValue ?? fallback : fallback;
        }

        private static int ReadInt(NormalizedValue value, string name, int fallback)
        {
            NormalizedValue item;
            return TryGet(value, name, out item) && item != null && item.Kind == NormalizedValueKind.Integer
                ? (int)item.IntegerValue : fallback;
        }

        private static bool ReadBool(NormalizedValue value, string name, bool fallback)
        {
            NormalizedValue item;
            return TryGet(value, name, out item) && item != null && item.Kind == NormalizedValueKind.Boolean
                ? item.BooleanValue : fallback;
        }

        private static bool TryGet(NormalizedValue value, string name, out NormalizedValue item)
        {
            if (value != null && value.Kind == NormalizedValueKind.Object && value.Properties != null)
            {
                for (int i = 0; i < value.Properties.Count; i++)
                {
                    NormalizedValueEntry entry = value.Properties[i];
                    if (entry != null && entry.Name == name)
                    {
                        item = entry.Value;
                        return true;
                    }
                }
            }
            item = null;
            return false;
        }
    }
}
