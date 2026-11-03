using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public static class FacilityLayerRecovery
    {
        public const string Version = "1.0.0";
        public static bool TryValidateRaw(JObject state, out string reason)
        {
            reason = "facility_layer_raw_invalid";
            if (state?["ItemZones"] is not JObject zones || zones["FacilityLayerVersion"]?.Type != JTokenType.Integer ||
                zones.Value<int>("FacilityLayerVersion") != 1 || zones["FacilityLayerMigrationId"]?.Type != JTokenType.String)
                return false;
            if (state["EffectRuntime"]?["EffectNodes"] is not JArray nodes) return false;
            if (nodes.Any(n => n is not JObject)) return false;
            foreach (var node in nodes.OfType<JObject>())
            {
                var properties = (node["ContinuationData"] as JObject)?["Properties"] as JArray;
                if ((string)node["EffectTypeId"] == FacilityEntryEffectTypeIds.Build &&
                    (string)node["DefinitionVersion"] == FacilityBuildEffectExecutor.DefinitionVersion && (string)node["FlowStage"] == "awaiting_placement")
                {
                    reason = "facility_placement_observation_raw_invalid";
                    var observations = properties?.Where(p => (string)p["Name"] == "placementCandidates").ToArray();
                    if (observations == null || observations.Length != 1 ||
                        !HostRecoveryService.HasRawNormalizedFields(observations[0]["Value"] as JObject, 0)) return false;
                }
                bool hasBatch = properties?.Any(p => (string)p["Name"] == "transferBatchReceipt") == true;
                if (hasBatch)
                {
                    if (node["TransferBatchRequest"] is not JObject || !HasRawTransferDto(node["TransferBatchRequest"], typeof(ItemTransferBatchRequest)))
                    { reason = "transfer_batch_original_request_raw_invalid"; return false; }
                }
                else if (node["TransferBatchRequest"] != null && node["TransferBatchRequest"].Type != JTokenType.Null)
                { reason = "transfer_batch_original_without_receipt"; return false; }
                if (node["RequiredOutcome"] == null || node["ExecutionBinding"] == null) return false;
                if (node["RequiredOutcome"].Type != JTokenType.Null)
                {
                    if (node["RequiredOutcome"] is not JObject required || required["ProtocolVersion"]?.Type != JTokenType.Integer ||
                        required.Value<int>("ProtocolVersion") != 1 || required["ResultField"]?.Type != JTokenType.String ||
                        required["ExpectedValue"]?.Type != JTokenType.Integer) return false;
                }
                if (node["ExecutionBinding"].Type != JTokenType.Null)
                {
                    if (node["ExecutionBinding"] is not JObject binding || !HasRawBindingDto(binding, typeof(EffectExecutionBinding)) || binding["ProtocolVersion"]?.Type != JTokenType.Integer ||
                        binding.Value<int>("ProtocolVersion") != 1 || binding["PlayerId"]?.Type != JTokenType.Integer ||
                        binding["AuthorizedAtRevision"]?.Type != JTokenType.Integer || binding["SourceRef"] is not JObject ||
                        binding["VersionRef"] is not JObject || new[] { "ParentBuildId", "ChoiceInteractionId", "AuthorizationRef" }
                            .Any(key => binding[key]?.Type != JTokenType.String)) return false;
                }
                if (!ValidateNestedRaw(node["NestedEffects"] as JArray)) return false;
            }
            reason = ""; return true;
        }
        private static bool HasRawBindingDto(JToken token, Type type)
        {
            if (type == typeof(string)) return token?.Type == JTokenType.String;
            if (type == typeof(int)) return token?.Type == JTokenType.Integer && int.TryParse(token.ToString(), out _);
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return token is JArray array && array.All(v => HasRawBindingDto(v, type.GetGenericArguments()[0]));
            if (token is not JObject value || !new[] { typeof(EffectExecutionBinding), typeof(AbilitySourceRef), typeof(AbilityVersionRef) }.Contains(type)) return false;
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            return value.Properties().Count() == fields.Length && fields.All(f => HasRawBindingDto(value[f.Name], f.FieldType));
        }
        private static bool HasRawTransferDto(JToken token, Type type)
        {
            if (token == null) return false;
            if (token.Type == JTokenType.Null) return !type.IsValueType;
            if (type == typeof(string)) return token.Type == JTokenType.String;
            if (type == typeof(int)) return token.Type == JTokenType.Integer && int.TryParse(token.ToString(), out _);
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return token is JArray array && array.All(v => HasRawTransferDto(v, type.GetGenericArguments()[0]));
            if (!new[] { typeof(ItemTransferBatchRequest), typeof(ItemTransferRequest), typeof(ItemTransferOrderExpectation),
                typeof(ItemPositionRef), typeof(ZoneRef) }.Contains(type) || token is not JObject value) return false;
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            return value.Properties().Count() == fields.Length && fields.All(f => HasRawTransferDto(value[f.Name], f.FieldType));
        }
        private static bool ValidateNestedRaw(JArray nested)
        {
            if (nested == null || nested.Any(n => n is not JObject)) return false;
            foreach (var spec in nested.OfType<JObject>())
                if (spec["RequiredOutcome"] == null || spec["ExecutionBinding"] == null ||
                    spec["ExecutionBinding"].Type != JTokenType.Null && !HasRawBindingDto(spec["ExecutionBinding"], typeof(EffectExecutionBinding)) ||
                    !ValidateNestedRaw(spec["NestedEffects"] as JArray)) return false;
            return true;
        }
        // 当前实体合同为v2；缺少设施层元数据的旧表示没有完整类型/位置/收据证据。
        // Host和隔离调用均明确拒绝，不从零/单枚供应猜测可迁移，也不改写原始状态。
        public static bool TryMigrateLegacy(JObject rawState, GameState original, out GameState migrated, out string reason)
        {
            migrated = null; reason = "facility_layer_legacy_unsupported";
            return false;
        }
        public static bool TryValidateState(GameState state, EffectRegistry registry, out string reason)
        {
            reason = "facility_layer_state_invalid";
            if (state?.ItemZones?.FacilityLayerVersion != 1 || state.ItemZones.FacilityLayerMigrationId == null ||
                !new ItemZoneQuery().TryValidate(state, out reason)) return false;
            try
            {
                foreach (var build in state.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build))
                    if (!FacilityBuildJournal.TryValidate(state, build, out reason)) return false;
                foreach (var node in state.EffectRuntime.EffectNodes)
                {
                    if (!ItemTransferService.TryValidateBatchReceipt(node, out reason)) return false;
                    if (!RequiredEffectOutcome.IsValid(node.RequiredOutcome)) return false;
                    if (node.RequiredOutcome != null && (node.Status == EffectNodeStatus.Completed || node.Status == EffectNodeStatus.Failed))
                        RequiredEffectOutcome.ValidateBeforeTerminal(node, node.RequiredOutcome);
                    EffectExecutionBinding.ValidateNode(state, registry, node, node.ExecutionBinding);
                    ValidateNestedBindings(state, registry, node, node.NestedEffects, 0);
                }
            }
            catch (Exception e) when (e is InvalidOperationException || e is KernelException || e is ArgumentException)
            { reason = "facility_layer_state_invalid:" + e.Message; return false; }
            reason = ""; return true;
        }
        private static void ValidateNestedBindings(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner,
            IEnumerable<EffectSpecRuntimeState> nested, int depth)
        {
            if (nested == null || depth > 64) throw new InvalidOperationException("effect_execution_nested_invalid");
            foreach (var spec in nested)
            {
                if (spec == null) throw new InvalidOperationException("effect_execution_nested_invalid");
                var binding = spec.ExecutionBinding;
                if (binding != null)
                {
                    if(CleanupSourceService.Terminal(owner)) EffectExecutionBinding.ValidateHistorical(state,registry,binding);
                    else EffectExecutionBinding.Validate(state, registry, binding);
                    if (spec.PlayerId != binding.PlayerId || spec.DecisionPlayerId >= 0 && spec.DecisionPlayerId != binding.PlayerId ||
                        spec.EffectTypeId == FacilityEntryEffectTypeIds.Activate || !EffectExecutionBinding.Within(state, owner.EffectId, binding.ParentBuildId))
                        throw new InvalidOperationException("effect_execution_nested_invalid");
                    EffectExecutionBinding.ValidateOperationArguments(spec.NormalizedArguments, binding.PlayerId);
                }
                ValidateNestedBindings(state, registry, owner, spec.NestedEffects, depth + 1);
            }
        }
    }
}

