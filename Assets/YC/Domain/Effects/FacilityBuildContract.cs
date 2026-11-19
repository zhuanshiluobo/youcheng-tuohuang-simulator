using System;
using System.Globalization;
using System.Linq;
using YC.Domain.Facilities;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    // API-11：Build 自身拥有选择与准备；这里没有新公开节点或旧 choose_build 转发。
    public static class FacilityBuildContract
    {
        public const string Version = "facility.build.v3.2";
        public static string ValidateSavedVersion(string type,string version,bool runtimeNode=false)=>type!=FacilityEntryEffectTypeIds.Build||version==FacilityBuildEffectExecutor.DefinitionVersion||!runtimeNode&&string.IsNullOrEmpty(version)?"":"facility_build_execution_version_unsupported";
        public static bool IsCompletedLegacy(string type,string version,int status) =>
            type == FacilityEntryEffectTypeIds.Build && version == "2.2.0" && status == (int)EffectNodeStatus.Completed;
        public static bool IsCompletedLegacy(EffectNodeRuntimeState node) => node != null &&
            IsCompletedLegacy(node.EffectTypeId,node.DefinitionVersion,(int)node.Status);
        public const string Semantic = "facility_build_v3";
        public static readonly string[] Fields = { "kind", "effectTypeId", "apiVersion", "targetMode", "player", "sourceKind",
            "sourceKey", "expectedSourceZoneId", "paymentMode", "consumeMainAction", "allowDecline", "facilityId", "cityBoardSlotIndex",
            "effectId", "includeStoredFacilities", "excludeRainbow", "maxGoldVoucherCost" };
        public static bool IsV3(NormalizedValue args) => Value(args, "apiVersion") != null || Value(args, "targetMode") != null;
        public static bool IsExplicitAuto(NormalizedValue args) =>
            Value(args, "apiVersion")?.Kind == NormalizedValueKind.Integer && Value(args, "apiVersion").IntegerValue == 3 &&
            Text(args, "targetMode") == "explicit" && Text(args, "paymentMode") == "auto";
        // 准备流程既包括选牌，也包括固定实例的支付方式选择；实际建设由付款后的子节点提交。
        public static bool IsSelection(string type, NormalizedValue args) => type == FacilityEntryEffectTypeIds.Build &&
            Value(args, "apiVersion")?.Kind == NormalizedValueKind.Integer && Value(args, "apiVersion").IntegerValue == 3 &&
            (Text(args, "targetMode") == "select" || IsExplicitAuto(args));
        public static bool IsSelectionOwner(EffectNodeRuntimeState node) => node != null &&
            IsSelection(node.EffectTypeId, node.NormalizedArguments);
        public static string SourceKind(string type, NormalizedValue args) => Text(args,
            "sourceKind", "supply");
        public static string SourceKind(EffectNodeRuntimeState node) => SourceKind(node.EffectTypeId, node.NormalizedArguments);

        public static string ValidateArguments(NormalizedValue args)
        {
            if (args?.Kind != NormalizedValueKind.Object || !args.IsValid() || args.Properties.Any(p => !Fields.Contains(p.Name)) ||
                Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Value(args, "apiVersion").IntegerValue != 3 ||
                !new[] { "select", "explicit" }.Contains(Text(args, "targetMode"))) return "build_contract_arguments_invalid";
            if (Value(args, "kind") != null && Text(args, "kind") != "effect_spec" ||
                Value(args, "effectTypeId") != null && Text(args, "effectTypeId") != FacilityEntryEffectTypeIds.Build) return "build_contract_arguments_invalid";
            if (Value(args, "player")?.Kind != NormalizedValueKind.String ||
                !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1)
                return "build_contract_player_invalid";
            foreach (string name in new[] { "sourceKind", "paymentMode", "sourceKey", "expectedSourceZoneId", "facilityId", "effectId" })
                if (Value(args, name) != null && Value(args, name).Kind != NormalizedValueKind.String) return "build_contract_arguments_invalid";
            string source = Text(args, "sourceKind"), mode = Text(args, "paymentMode");
            if (!new[] { "supply", "deck", "reserve", "authorized" }.Contains(source) ||
                !new[] { "auto", "resources", "gold", "free" }.Contains(mode) ||
                Value(args, "consumeMainAction")?.Kind != NormalizedValueKind.Boolean) return "build_contract_arguments_invalid";
            if (source == "authorized" ? Text(args, "sourceKey").Length == 0 : Text(args, "sourceKey").Length > 0)
                return "facility_source_authorization_required";
            if (source == "reserve" && (mode != "free" || Flag(args, "consumeMainAction"))) return "build_reserve_contract_invalid";
            foreach (string name in new[] { "allowDecline", "includeStoredFacilities", "excludeRainbow" })
                if (Value(args, name) != null && Value(args, name).Kind != NormalizedValueKind.Boolean) return "build_contract_arguments_invalid";
            var bound = Value(args, "maxGoldVoucherCost");
            if (bound != null && (bound.Kind != NormalizedValueKind.Integer || bound.IntegerValue < 0 || bound.IntegerValue > int.MaxValue))
                return "build_contract_arguments_invalid";
            bool select = Text(args, "targetMode") == "select";
            if (select)
            {
                if (Value(args, "facilityId") != null || Value(args, "cityBoardSlotIndex") != null ||
                    source != "supply" && Value(args, "includeStoredFacilities") != null) return "build_contract_modes_mixed";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(Text(args, "facilityId"))) return "build_contract_target_required";
                if (Flag(args, "allowDecline") || new[] { "effectId", "includeStoredFacilities", "excludeRainbow" }.Any(k => Value(args, k) != null))
                    return "build_contract_modes_mixed";
                var slot = Value(args, "cityBoardSlotIndex");
                if (slot != null && (slot.Kind != NormalizedValueKind.Integer || slot.IntegerValue < -1 || slot.IntegerValue >= BuildFacilityService.CityBoardSlotCount))
                    return "build_contract_arguments_invalid";
            }
            return "";
        }
        public static string ValidateSpec(EffectSpec spec)
        {
            string error = ValidateArguments(spec.NormalizedArguments);
            if (error.Length > 0) return error;
            if (Text(spec.NormalizedArguments, "player") != spec.PlayerId.ToString(CultureInfo.InvariantCulture) || spec.AllowDecline || spec.DecisionPlayerId != -1)
                return "build_contract_control_mismatch";
            return spec.Children.Count > 0 || spec.NestedEffectGroups.Count > 0 ? "build_contract_body_rejected" : "";
        }
        public static string ValidateSavedSpec(EffectSpecRuntimeState spec)
        {
            string error = ValidateArguments(spec.NormalizedArguments);
            if (error.Length > 0) return error;
            if (Text(spec.NormalizedArguments, "player") != spec.PlayerId.ToString(CultureInfo.InvariantCulture) || spec.AllowDecline || spec.DecisionPlayerId != -1)
                return "build_contract_control_mismatch";
            return spec.NestedEffects.Count > 0 || spec.NestedGroupSizes.Count > 0 ? "build_contract_body_rejected" : "";
        }
        // 旧版已经完成的建设只复验旧协议摘要；运行中的建设始终要求当前版本。
        private static string Digest(EffectNodeRuntimeState node) => StableIdFactory.Create(IsCompletedLegacy(node) ? "facility.build.v3.1" : Version,
            node.PlayerId.ToString(CultureInfo.InvariantCulture), node.SourceId ?? "", node.NormalizedArguments.ToDeterministicString());
        internal static void Initialize(EffectNodeRuntimeState node)
        {
            if (node.FlowStage == "" && Value(node.ContinuationData, "buildContractProtocolVersion") == null)
            {
                Set(node, "buildContractProtocolVersion", NormalizedValue.CreateInteger(1));
                Set(node, "buildContractArgumentsDigest", NormalizedValue.CreateString(Digest(node)));
            }
        }
        public static bool TryValidateNode(GameState state, EffectNodeRuntimeState node, out string error)
        {
            error = "";
            if (node?.EffectTypeId != FacilityEntryEffectTypeIds.Build) return true;
            bool progressed = Value(node.ContinuationData, "buildContractProtocolVersion") != null ||
                state.EffectRuntime.InteractionRequests.Any(q => q.OwnerEffectId == node.EffectId && Text(q.PromptParameters, "interactionSemantic") == Semantic);
            if (!IsV3(node.NormalizedArguments))
            {
                if (progressed) { error = "build_contract_mode_missing"; return false; }
                return true;
            }
            error = ValidateArguments(node.NormalizedArguments);
            if (error.Length > 0) return false;
            error = "build_contract_state_invalid";
            if (Text(node.NormalizedArguments, "player") != node.PlayerId.ToString(CultureInfo.InvariantCulture) || node.AllowDecline || node.DecisionPlayerId != node.PlayerId) return false;
            var protocol = Value(node.ContinuationData, "buildContractProtocolVersion");
            if (protocol == null)
            {
                bool unstarted = node.FlowStage == "" && (node.Status == EffectNodeStatus.Created || node.Status == EffectNodeStatus.Ready || node.Status == EffectNodeStatus.Running) &&
                    node.PendingOutcome == EffectPendingOutcome.None && node.NormalizedResult?.Kind == NormalizedValueKind.Null &&
                    node.ContinuationData?.Kind == NormalizedValueKind.Object && node.ContinuationData.Properties.Count == 0 &&
                    node.NestedEffects.Count == 0 && node.NestedGroupSizes.Count == 0 && node.ChildEffectIds.Count == 0 &&
                    !state.EffectRuntime.InteractionRequests.Any(q => q.OwnerEffectId == node.EffectId);
                if (unstarted) error = "";
                return unstarted;
            }
            if (protocol.Kind != NormalizedValueKind.Integer || protocol.IntegerValue != 1 || Text(node.ContinuationData, "buildContractArgumentsDigest") != Digest(node)) return false;
            if (node.Status == EffectNodeStatus.Completed || node.PendingOutcome == EffectPendingOutcome.Completed)
            {
                if (!ValidateCompletedResult(state, node)) { error = "build_contract_result_invalid"; return false; }
            }
            if (!IsSelection(node.EffectTypeId, node.NormalizedArguments))
            {
                if (Number(node.NormalizedArguments, "cityBoardSlotIndex", -1) >= 0 && node.FlowStage.Length > 0)
                {
                    var observation = Value(node.ContinuationData, "explicitSlotObservation");
                    if (observation == null || Number(observation, "slotIndex", -1) != Number(node.NormalizedArguments, "cityBoardSlotIndex", -1) ||
                        !FacilityPlacementObservations.TryValidate(NormalizedValue.CreateArray(new[] { observation }.ToList()), node.PlayerId,
                            Text(node.NormalizedArguments, "facilityId"), state.EffectRuntime.StateRevision, out error)) return false;
                }
                error = ""; return true;
            }
            if (!new[] { "", "deck_select", "deck_preparing", "deck_declined", "card", "built" }.Contains(node.FlowStage) ||
                node.NestedEffects.Count > 0 || node.NestedGroupSizes.Count > 0) return false;
            if (IsExplicitAuto(node.NormalizedArguments) && node.FlowStage.Length > 0 && Number(node.NormalizedArguments, "cityBoardSlotIndex", -1) >= 0)
            {
                var observation = Value(node.ContinuationData, "explicitSlotObservation");
                if (observation == null || Number(observation, "slotIndex", -1) != Number(node.NormalizedArguments, "cityBoardSlotIndex", -1) ||
                    !FacilityPlacementObservations.TryValidate(NormalizedValue.CreateArray(new[] { observation }.ToList()), node.PlayerId,
                        Text(node.NormalizedArguments, "facilityId"), state.EffectRuntime.StateRevision, out error)) return false;
            }
            error = "build_contract_state_invalid";
            foreach (var request in state.EffectRuntime.InteractionRequests.Where(q => q.OwnerEffectId == node.EffectId))
            {
                if (Text(request.PromptParameters, "interactionSemantic") != Semantic || Text(request.PromptParameters, "sourceKind") != SourceKind(node) ||
                    Text(request.PromptParameters, "ownerEffectTypeId") != FacilityEntryEffectTypeIds.Build || Number(request.PromptParameters, "buildApiVersion") != 3) return false;
                if (IsExplicitAuto(node.NormalizedArguments) && (request.CandidateIds.Count != 1 ||
                    request.CandidateIds[0] != Text(node.NormalizedArguments, "facilityId") ||
                    Text(request.PromptParameters, "fixedFacilityId") != request.CandidateIds[0])) return false;
                if (IsExplicitAuto(node.NormalizedArguments))
                {
                    var record = state.EffectRuntime.CandidateResolutions.SingleOrDefault(r => r.CandidateSetId == request.CandidateSetId && r.Version == request.CandidateSetVersion);
                    if (record == null || record.SourceEffectId != node.EffectId || !record.FinalCandidateIds.SequenceEqual(request.CandidateIds) ||
                        record.FacilitySourceProofs.Count != 1 || record.FacilitySourceProofs[0].ItemId != request.CandidateIds[0] ||
                        record.FacilitySourceProofs[0].SourceZoneId != Text(request.PromptParameters, "expectedSourceZoneId")) return false;
                }
            }
            if (node.ChildEffectIds.Distinct().Count() != node.ChildEffectIds.Count || node.ChildEffectIds.Any(id => !state.EffectRuntime.EffectNodes.Any(n => n.EffectId == id))) return false;
            var children = node.ChildEffectIds.Select(id => state.EffectRuntime.EffectNodes.First(n => n.EffectId == id))
                .Where(n => !state.EffectRuntime.DispatchReceipts.Any(r => r.Succeeded && r.AttachedEffectIds.Contains(n.EffectId))).ToList();
            if (children.Any(n => n.EffectTypeId != FacilityEntryEffectTypeIds.Build && n.EffectTypeId != FacilityBuildEffectExecutor.PaymentTypeId)) return false;
            var builds = children.Where(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build).ToList();
            if (builds.Count > 1 || node.FlowStage == "built" && builds.Count != 1 || builds.Count > 0 && node.FlowStage != "built") return false;
            foreach (var child in builds)
            {
                var selected = Value(node.ContinuationData, "selectedItemIds");
                if (selected?.Kind != NormalizedValueKind.Array || selected.Items.Count != 1 || selected.Items[0].Kind != NormalizedValueKind.String) return false;
                string item = selected.Items[0].StringValue;
                if (string.IsNullOrEmpty(item) || child.PlayerId != node.PlayerId || IsV3(child.NormalizedArguments)) return false;
                if (IsExplicitAuto(node.NormalizedArguments) && item != Text(node.NormalizedArguments, "facilityId")) return false;
                string mode = Text(child.NormalizedArguments, "paymentMode"), requested = Text(node.NormalizedArguments, "paymentMode");
                if (requested == "auto" ? mode != "resources" && mode != "gold" : mode != requested) return false;
                var expected = FacilityBuildEffectSpecFactory.Build(node.PlayerId, item, -1, mode, SourceKind(node) == "reserve", node.SourceId,
                    Flag(node.NormalizedArguments, "consumeMainAction"), Text(node.ContinuationData, "selectedSourceZoneId"), SourceKind(node), Text(node.NormalizedArguments, "sourceKey"));
                ApplyExplicitSlot(node, expected);
                var bound = Value(node.NormalizedArguments, "maxGoldVoucherCost");
                if (bound != null) expected.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "maxGoldVoucherCost", Value = bound.Clone() });
                if (mode != "free")
                {
                    var preparations = children.Where(n => n.Payment?.Preparation?.IsPromoted == true).Take(2).ToList();
                    if (preparations.Count != 1 || preparations[0].Payment.Preparation.PromotedActionEffectId != child.EffectId) return false;
                    var preparation = preparations[0];
                    expected.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "preparedPaymentOwnerEffectId", Value = NormalizedValue.CreateString(preparation.EffectId) });
                }
                expected.NormalizedArguments.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                if (!child.NormalizedArguments.HasSameCanonicalValue(expected.NormalizedArguments)) return false;
            }
            error = ""; return true;
        }
        private static bool ValidateCompletedResult(GameState state, EffectNodeRuntimeState node)
        {
            var result = node.NormalizedResult;
            if (result?.Kind != NormalizedValueKind.Object || !result.IsValid() || result.Properties.Count != 4 ||
                new[] { "outcome", "builtItemId", "definitionId", "buildEffectId" }.Any(k => Value(result, k)?.Kind != NormalizedValueKind.String)) return false;
            bool select = IsSelection(node.EffectTypeId, node.NormalizedArguments);
            var actuals = select ? state.EffectRuntime.EffectNodes.Where(n => n.ParentEffectId == node.EffectId &&
                n.EffectTypeId == FacilityEntryEffectTypeIds.Build && FacilityBuildJournal.Read(n) != null &&
                !state.EffectRuntime.DispatchReceipts.Any(r => r.Succeeded && r.AttachedEffectIds.Contains(n.EffectId))).Take(2).ToList() : new[] { node }.ToList();
            if (actuals.Count > 1) return false;
            var actual = actuals.FirstOrDefault();
            var facts = FacilityBuildJournal.Read(actual);
            if (facts != null)
                return Text(result, "outcome") == "built" && Text(result, "builtItemId") == facts.ItemId &&
                    Text(result, "definitionId") == facts.DefinitionId && Text(result, "buildEffectId") == facts.BuildId &&
                    (!select || actual.Status == EffectNodeStatus.Completed);
            return select && new[] { "player_declined", "no_legal_target" }.Contains(Text(result, "outcome")) &&
                new[] { "builtItemId", "definitionId", "buildEffectId" }.All(k => Text(result, k).Length == 0);
        }
        internal static EffectInteractionSpec Decorate(EffectNodeRuntimeState node, EffectInteractionSpec request)
        {
            if (!IsSelection(node.EffectTypeId, node.NormalizedArguments)) return request;
            if (request.PromptParameters?.Kind != NormalizedValueKind.Object) request.PromptParameters = Object();
            foreach (var entry in Object("interactionSemantic", NormalizedValue.CreateString(Semantic), "sourceKind", NormalizedValue.CreateString(SourceKind(node)),
                "ownerEffectTypeId", NormalizedValue.CreateString(node.EffectTypeId), "buildApiVersion", NormalizedValue.CreateInteger(3)).Properties)
                request.PromptParameters.Properties.Add(entry);
            if (IsExplicitAuto(node.NormalizedArguments))
                request.PromptParameters.Properties.Add(new NormalizedValueEntry { Name = "fixedFacilityId", Value = Value(node.NormalizedArguments, "facilityId").Clone() });
            request.PromptParameters.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            return request;
        }
        internal static void ApplyExplicitSlot(EffectNodeRuntimeState parent, EffectSpec child)
        {
            int slot = Number(parent.NormalizedArguments, "cityBoardSlotIndex", -1);
            if (!IsExplicitAuto(parent.NormalizedArguments) || slot < 0) return;
            child.NormalizedArguments.Properties.Single(p => p.Name == "cityBoardSlotIndex").Value = NormalizedValue.CreateInteger(slot);
            child.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "slotObservation",
                Value = Value(parent.ContinuationData, "explicitSlotObservation").Clone() });
        }
        internal static EffectStepResult Complete(EffectExecutionContext context, EffectStepResult result)
        {
            if (!result.HasPendingOutcome || result.PendingOutcome != EffectPendingOutcome.Completed) return result;
            bool select = IsSelection(context.Node.EffectTypeId, context.Node.NormalizedArguments);
            var build = select ? context.ChildNodes.SingleOrDefault(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build && FacilityBuildJournal.Read(n) != null &&
                !context.State.EffectRuntime.DispatchReceipts.Any(r => r.Succeeded && r.AttachedEffectIds.Contains(n.EffectId))) : context.Node;
            var facts = FacilityBuildJournal.Read(build);
            string outcome = facts != null ? "built" : result.NormalizedResult?.StringValue ?? Text(result.NormalizedResult, "outcome");
            if (!new[] { "built", "player_declined", "no_legal_target" }.Contains(outcome)) throw new InvalidOperationException("build_contract_result_invalid");
            var normalized = Object("outcome", NormalizedValue.CreateString(outcome), "builtItemId", NormalizedValue.CreateString(facts?.ItemId ?? ""),
                "definitionId", NormalizedValue.CreateString(facts?.DefinitionId ?? ""), "buildEffectId", NormalizedValue.CreateString(facts?.BuildId ?? ""));
            var completed = EffectStepResult.Completed(normalized).WithFlowStage(result.FlowStage).AddChildren(result.ChildEffects);
            completed.EventsBeforeChildren.AddRange(result.EventsBeforeChildren); completed.Events.AddRange(result.Events);
            completed.Interactions.AddRange(result.Interactions); completed.Logs.AddRange(result.Logs);
            return completed;
        }
        public static EffectSpec Select(int player, string sourceKind, string paymentMode, bool consumeMainAction, bool allowDecline) =>
            EffectSpec.Create(FacilityEntryEffectTypeIds.Build, Object("apiVersion", NormalizedValue.CreateInteger(3), "targetMode", NormalizedValue.CreateString("select"),
                "player", NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)), "sourceKind", NormalizedValue.CreateString(sourceKind),
                "paymentMode", NormalizedValue.CreateString(paymentMode), "consumeMainAction", NormalizedValue.CreateBoolean(consumeMainAction),
                "allowDecline", NormalizedValue.CreateBoolean(allowDecline)), player);
    }
}
