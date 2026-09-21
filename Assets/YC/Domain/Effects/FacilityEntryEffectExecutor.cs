using System;
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
            bool consumeMainAction = true)
        {
            return new EffectSpec(
                FacilityEntryEffectTypeIds.Build,
                NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry { Name = "facilityId", Value = NormalizedValue.CreateString(facilityId ?? string.Empty) },
                    new NormalizedValueEntry { Name = "cityBoardSlotIndex", Value = NormalizedValue.CreateInteger(cityBoardSlotIndex) },
                    new NormalizedValueEntry { Name = "paymentMode", Value = NormalizedValue.CreateString(paymentMode ?? BuildFacilityService.PaymentModeAuto) },
                    new NormalizedValueEntry { Name = "reserveForFree", Value = NormalizedValue.CreateBoolean(reserveForFree) },
                    new NormalizedValueEntry { Name = "consumeMainAction", Value = NormalizedValue.CreateBoolean(consumeMainAction) }
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
            FacilityCardDefinition facility = FacilityCardDatabase.Get(facilityId);
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
        public const string DefinitionVersion = "1.1.0";

        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            EffectRegistration ignored;
            if (registry.TryGet(FacilityEntryEffectTypeIds.Build, out ignored)) return;
            var executor = new FacilityBuildEffectExecutor();
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
            if (context.Node.PendingOutcome != EffectPendingOutcome.None)
                return EffectStepResult.Completed(context.Node.NormalizedResult == null ? NormalizedValue.CreateNull() : context.Node.NormalizedResult.Clone());
            string facilityId = ReadString(context.Node.NormalizedArguments, "facilityId", string.Empty);
            int slot = ReadInt(context.Node.NormalizedArguments, "cityBoardSlotIndex", -1);
            string payment = ReadString(context.Node.NormalizedArguments, "paymentMode", BuildFacilityService.PaymentModeAuto);
            bool reserve = ReadBool(context.Node.NormalizedArguments, "reserveForFree", false);
            if (string.IsNullOrEmpty(context.Node.FlowStage))
            {
                var service = BuildFacilityService.CreateForEffectTree();
                BuildFacilityResult result = reserve
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
