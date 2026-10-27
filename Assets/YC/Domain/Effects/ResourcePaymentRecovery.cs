using YC.Domain.Rules;
using System;
using System.Linq;
using System.Globalization;
using Newtonsoft.Json.Linq;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public static class ResourcePaymentRecovery
    {
        public static bool HasRawFields(JObject p, out string reason)
        {
            reason = "payment_v2_raw_fields_missing";
            if (p?["IsCommitted"]?.Type == JTokenType.Boolean && (bool)p["IsCommitted"] && p["FinalReceipt"] is JObject oldFinal && oldFinal.Property("CostReceiptPolicyVersion") == null)
            { reason = "payment_token_receipt_legacy_requires_migration"; return false; }
            bool Fields(JObject o, params string[] names) => o != null && names.All(n => o.Property(n) != null);
            bool Integer(JToken v, int min, int max) => v?.Type == JTokenType.Integer && long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && n >= min && n <= max;
            bool Cost(JToken v) => v is JObject c && PaymentResources.Types.All(t => Integer(c[t.ToString()], 0, int.MaxValue));
            bool Rule(JToken v) => v is JObject r && Fields(r, "ProtocolVersion", "RuleId", "QuotaGroupId", "QuotaScope", "PurposeKind", "MaxUnits", "QuotaLimit", "SourceRef", "VersionRef", "AllowedResourceTypes", "UnitResourceCost", "ItemCost") &&
                Fields(r["SourceRef"] as JObject, "SourceKey", "ItemId", "DefinitionId", "AbilityId", "AuthorizedPlayerId", "AuthorizationRef", "DeclarationVersion", "EffectiveZoneId", "ActiveFaceId", "RuleOwnerItemId", "GrantingSourceRefs") &&
                Fields(r["VersionRef"] as JObject, "ContractVersion", "ContentPackHash", "DefinitionVersion", "HandlerHash", "ExecutorVersion") && Integer(r["ProtocolVersion"], 1, 1) && Integer(r["MaxUnits"], 1, 99) && Integer(r["QuotaLimit"], 1, 99) && Cost(r["UnitResourceCost"]) &&
                (r["ItemCost"]?.Type == JTokenType.Null || r["ItemCost"] is JObject item && Fields(item, "CardInstanceId", "LocalZoneId", "DestinationKind", "CountPerUnit", "OperationProfileId", "SelectionPolicy") && Integer(item["CountPerUnit"], 1, MaximumTokenCount));
            if (!Fields(p, "PaymentProtocolVersion", "PaymentId", "OwnerEffectId", "PlayerId", "PaymentCost", "IsCommitted", "IsCommitRequested", "PaymentGeneration", "BeforePaymentEventId", "BeforePaymentReady", "SourceKind", "RuleConditionOwnerEffectId", "Preparation",
                "Demand", "SelectedAllocation", "GrossCost", "FinalCost", "Phase", "SourceDrafts", "SourceEdits", "SourceReceipts", "BeforePaymentReceipts", "FinalReceipt", "SelectedSubstitution", "AllowsSubstitution") ||
                !Integer(p["PaymentProtocolVersion"], 2, 2) || !Integer(p["PaymentGeneration"], 1, int.MaxValue) || !Integer(p["PlayerId"], 1, int.MaxValue) || p["IsCommitted"]?.Type != JTokenType.Boolean || p["IsCommitRequested"]?.Type != JTokenType.Boolean || p["BeforePaymentReady"]?.Type != JTokenType.Boolean ||
                !Cost(p["PaymentCost"]) || !Cost(p["SelectedAllocation"]) || !Cost(p["GrossCost"]) || !Cost(p["FinalCost"]) ||
                !Fields(p["Demand"] as JObject, "ProtocolVersion", "Mode", "FixedCost", "AllowedResourceTypes", "TotalCount") || !Integer(p["Demand"]["ProtocolVersion"],1,1) || !Integer(p["Demand"]["TotalCount"],0,99) || !Cost(p["Demand"]["FixedCost"]) ||
                p["SourceDrafts"] is not JArray drafts || p["SourceEdits"] is not JArray edits || p["SourceReceipts"] is not JArray receipts || p["BeforePaymentReceipts"] is not JArray before) return false;
            foreach (var d in drafts)
                if (!Fields(d as JObject, "ProtocolVersion", "PaymentGeneration", "DraftRevision", "PaymentId", "BuildIdentity", "Status", "InvalidReason", "Rule", "Allocation", "SelectedItemIds") || !Integer(d["ProtocolVersion"],1,1) || !Integer(d["PaymentGeneration"],1,int.MaxValue) || !Integer(d["DraftRevision"],1,int.MaxValue) || !Rule(d["Rule"]) || !Cost(d["Allocation"]) || d["SelectedItemIds"] is not JArray) return false;
            foreach (var e in edits) if (!Fields(e as JObject, "EditId", "SourceKey", "RuleId", "Status", "PaymentGeneration", "EnteredDraftRevision", "PreviousDraft")) return false;
            foreach (var b in before) if (!Fields(b as JObject, "EventId", "Generation", "GrossCost", "Ready") || !Cost(b["GrossCost"])) return false;
            foreach (var r in receipts)
                if (!Fields(r as JObject, "ProtocolVersion", "PaymentGeneration", "ReceiptId", "PaymentId", "BuildIdentity", "Rule", "Allocation", "ActualResourceCost", "ItemIds", "OriginalSupplyZoneIds", "PositionChanges", "TokenArguments", "TokenTransfer") || !Integer(r["ProtocolVersion"], 2, 2) || !Rule(r["Rule"]) || !Cost(r["Allocation"]) || !Cost(r["ActualResourceCost"])) return false;
            if ((bool)p["IsCommitted"] && (!Fields(p["FinalReceipt"] as JObject, "ProtocolVersion", "ReceiptId", "ContentHash", "DefinitionVersion", "CostReceiptPolicyVersion", "Demand", "SelectedAllocation", "GrossCost", "FinalCost", "SourceReceiptIds") ||
                !Cost(p["FinalReceipt"]["SelectedAllocation"]) || !Cost(p["FinalReceipt"]["GrossCost"]) || !Cost(p["FinalReceipt"]["FinalCost"]))) return false;
            reason = ""; return true;
        }
        public static bool TryValidate(GameState state, EffectRegistry registry, out string reason)
        {
            reason = "payment_v2_state_invalid";
            foreach (var n in state.EffectRuntime.EffectNodes.Where(n => n.Payment?.PaymentProtocolVersion == 2))
                if (!TryValidateNode(state, registry, n, out reason)) return false;
            reason = ""; return true;
        }
        public static bool TryValidateNode(GameState state, EffectRegistry registry, EffectNodeRuntimeState node, out string reason)
        {
            reason = "payment_v2_state_invalid:" + node.EffectId; var p = node.Payment;
            // 正式准备入口已有独立的 Preparation 身份及显式 EnableV2 合同；旧合法准备不含此新标记。
            // 标记只区分原 Build 内新建的付款段，不能把未标记的旧内嵌付款重解释为 v2。
            if(node.EffectTypeId==FacilityBuildEffectExecutor.PaymentTypeId && p?.Preparation==null &&
                (Value(node.NormalizedArguments,"paymentProtocolVersion")?.Kind!=NormalizedValueKind.Integer || Number(node.NormalizedArguments,"paymentProtocolVersion")!=2)) return false;
            if (!ResourcePaymentSourceService.IsResourcePaymentNode(node) || p == null || p.PaymentProtocolVersion != 2 || p.PaymentId != node.EffectId || p.OwnerEffectId != node.EffectId || p.PlayerId != node.PlayerId || state.FindPlayer(p.PlayerId) == null ||
                p.PaymentGeneration < 1 || p.SelectedSubstitution != null || p.SourceDrafts == null || p.SourceReceipts == null || p.SourceEdits == null || p.BeforePaymentReceipts == null ||
                p.SourceDrafts.Any(d => d == null) || p.SourceReceipts.Any(r => r == null) || p.SourceEdits.Any(e => e == null) || p.BeforePaymentReceipts.Any(b => b == null) ||
                !new[] { "demand_frozen", "allocation", "before_payment", "confirmation", "committed", "cancelled" }.Contains(p.Phase) ||
                !ResourcePaymentAllocationService.TryValidate(p.Demand, p.SelectedAllocation, out var gross, out _) || !PaymentResources.Same(gross, p.PaymentCost) || !PaymentResources.Same(gross, p.GrossCost) || !PaymentResources.Valid(p.FinalCost) ||
                p.SourceDrafts.Select(d => ResourcePaymentSourceService.Key(d.Rule)).Distinct().Count() != p.SourceDrafts.Count || p.SourceReceipts.Select(r => r.ReceiptId).Distinct().Count() != p.SourceReceipts.Count ||
                p.SourceEdits.Select(e => e.EditId).Distinct().Count() != p.SourceEdits.Count || p.SourceEdits.Count(e => e.Status == "open") > 1) return false;
            if (p.BeforePaymentReceipts.Select(b => b.EventId).Distinct().Count() != p.BeforePaymentReceipts.Count) return false;
            foreach (var b in p.BeforePaymentReceipts)
            {
                var e = state.EffectRuntime.RuleEvents.Find(e => e.EventId == b.EventId);
                if (b.Generation < 1 || b.Generation > p.PaymentGeneration || !PaymentResources.Valid(b.GrossCost) || e?.EventType != ResourcePaymentPreparationService.BeforeResourcePayment || e.SourceEffectId != node.EffectId ||
                    Number(e.Payload, "paymentGeneration") != b.Generation || Text(e.Payload, "paymentId") != p.PaymentId ||
                    Value(e.Payload, "cost")?.ToDeterministicString() != ResourcePaymentPreparationService.Cost(b.GrossCost).ToDeterministicString()) return false;
            }
            if (p.BeforePaymentEventId == null || p.BeforePaymentReady && !p.BeforePaymentReceipts.Any(b => b.EventId == p.BeforePaymentEventId && b.Ready)) return false;
            foreach (var d in p.SourceDrafts)
                if (d.ProtocolVersion != 1 || d.PaymentId != p.PaymentId || d.PaymentGeneration != p.PaymentGeneration || d.DraftRevision < 1 || !ValidRule(d.Rule) ||
                    d.Rule.SourceRef.AuthorizedPlayerId != p.PlayerId || d.SelectedItemIds == null || d.SelectedItemIds.Distinct().Count() != d.SelectedItemIds.Count || !PaymentResources.Valid(d.Allocation) || !new[] { "valid", "reselect_required", "withdrawn" }.Contains(d.Status) ||
                    d.Status == "withdrawn" && (PaymentResources.Types.Any(t => d.Allocation.Get(t) != 0) || d.SelectedItemIds.Count != 0) ||
                    d.Status == "valid" && (PaymentResources.Types.Sum(t => (long)d.Allocation.Get(t)) < 1 || PaymentResources.Types.Sum(t => (long)d.Allocation.Get(t)) > d.Rule.MaxUnits ||
                        PaymentResources.Types.Any(t => d.Allocation.Get(t) > 0 && !d.Rule.AllowedResourceTypes.Contains(t)) ||
                        d.SelectedItemIds.Count != (d.Rule.ItemCost == null ? 0 : (long)d.Rule.ItemCost.CountPerUnit * PaymentResources.Types.Sum(t => (long)d.Allocation.Get(t))))) return false;
            try
            {
                if (PaymentResources.Types.Any(t => p.SourceDrafts.Where(d => d.Status == "valid").Sum(d => (long)d.Allocation.Get(t)) > gross.Get(t)) || !PaymentResources.Valid(ResourcePaymentPreparationService.Remaining(p))) return false;
            }
            catch (OverflowException) { return false; }
            foreach (var e in p.SourceEdits)
                if (string.IsNullOrEmpty(e.EditId) || string.IsNullOrEmpty(e.SourceKey) || string.IsNullOrEmpty(e.RuleId) || !new[] { "open", "saved", "cancelled" }.Contains(e.Status) || e.PaymentGeneration < 1 || e.PaymentGeneration > p.PaymentGeneration || e.Status == "open" && e.PaymentGeneration != p.PaymentGeneration) return false;
            if (p.Preparation != null)
            {
                var preparation = p.Preparation;
                var operation = state.EffectRuntime.OperationWindows.Find(value => value.OperationWindowId == preparation.OperationWindowId &&
                    value.WindowGeneration == preparation.WindowGeneration && value.WindowKind == RoundMainlineNodeTypeIds.PlayerActionWindow &&
                    value.SourceEffectId == node.ParentEffectId && value.AuthorizedPlayerIds.SequenceEqual(new[] { p.PlayerId }));
                bool validAuthorization = ResourcePaymentPreparationService.IsMainActionAuthorization(preparation) ? operation != null :
                    preparation.AuthorizationKind == "effect_build" && ResourcePaymentPreparationService.ValidateEffectBuildIdentity(state, node, out _);
                if (preparation.TargetId == null || preparation.BusinessBeforeEventId == null || preparation.SourceLocationId == null || preparation.PromotedActionEffectId == null ||
                    preparation.PreparationOwnerEffectId != node.EffectId || !validAuthorization || preparation.ActionKind != p.SourceKind ||
                    preparation.ActionKind != "build" && preparation.ActionKind != "city_move" || preparation.IsCancelled &&
                    (preparation.IsPromoted || p.IsCommitted || p.IsCommitRequested || p.SelectedSubstitution != null) ||
                    preparation.ActionArguments?.Kind != NormalizedValueKind.Object || !preparation.ActionArguments.TryValidate(out _) ||
                    preparation.IsPromoted != !string.IsNullOrEmpty(preparation.PromotedActionEffectId)) return false;
                if (preparation.IsPromoted && !state.EffectRuntime.EffectNodes.Any(action => action.EffectId == preparation.PromotedActionEffectId &&
                    action.PlayerId == p.PlayerId && action.ParentEffectId == node.ParentEffectId && Text(action.NormalizedArguments, "preparedPaymentOwnerEffectId") == node.EffectId)) return false;
                if (preparation.ActionKind == "city_move" && (preparation.BusinessBeforeReady && preparation.BusinessBeforeEventId.Length == 0 ||
                    preparation.BusinessBeforeEventId.Length > 0 && !state.EffectRuntime.RuleEvents.Any(value => value.EventId == preparation.BusinessBeforeEventId &&
                        value.SourceEffectId == node.EffectId && value.EventType == CityMoveEventTypeIds.BeforeCityMove && Text(value.Payload, "targetLocationId") == preparation.TargetId &&
                        Text(value.Payload, "sourceLocationId") == preparation.SourceLocationId))) return false;
            }
            if (!p.IsCommitted)
            {
                if (p.SourceReceipts.Count != 0 || p.FinalReceipt != null || p.Phase == "committed") return false;
                // 库存/来源的后续变化使草稿不可确认，不使合法未付请求无法恢复退出。
                reason = ""; return true;
            }
            var final = p.FinalReceipt;
            if (p.IsCommitRequested || p.Phase != "committed" || node.FlowStage != "payment_finished" || node.Status != EffectNodeStatus.Completed && node.PendingOutcome != EffectPendingOutcome.Completed || p.SourceEdits.Any(e => e.Status == "open") || final?.ProtocolVersion != 2 || final.SourceReceiptIds == null ||
                final.ReceiptId != StableIdFactory.Create("payment-receipt", p.PaymentId) || final.ContentHash != (registry?.ContentPackHash ?? state.ContentPackHash) || final.DefinitionVersion != node.DefinitionVersion ||
                final.CostReceiptPolicyVersion != ResourcePaymentSourceService.TokenCostContractVersion ||
                !PaymentResources.Same(final.GrossCost, gross) || !PaymentResources.Same(final.SelectedAllocation, p.SelectedAllocation) || !PaymentResources.Same(final.FinalCost, p.FinalCost) ||
                !ResourcePaymentAllocationService.ValidDemand(final.Demand) || final.Demand.Mode != p.Demand.Mode || final.Demand.TotalCount != p.Demand.TotalCount || !PaymentResources.Same(final.Demand.FixedCost, p.Demand.FixedCost) || !final.Demand.AllowedResourceTypes.SequenceEqual(p.Demand.AllowedResourceTypes) ||
                !final.SourceReceiptIds.SequenceEqual(p.SourceReceipts.Select(r => r.ReceiptId))) return false;
            var allocated = new ResourceSet(); var costs = new ResourceSet(); var ids = new System.Collections.Generic.HashSet<string>();
            try
            {
                foreach (var r in p.SourceReceipts)
                {
                    var d = p.SourceDrafts.SingleOrDefault(d => d.Status == "valid" && ResourcePaymentSourceService.Key(d.Rule) == ResourcePaymentSourceService.Key(r.Rule));
                    if (d == null || !ValidRule(r.Rule) || r.ItemIds == null || r.OriginalSupplyZoneIds == null || r.PositionChanges == null || r.ProtocolVersion != 2 || r.PaymentId != p.PaymentId || r.PaymentGeneration != p.PaymentGeneration || r.BuildIdentity != d.BuildIdentity ||
                        r.ReceiptId != StableIdFactory.Create("payment-source-receipt", p.PaymentId, ResourcePaymentSourceService.Key(r.Rule)) || ResourcePaymentSourceService.Fingerprint(r.Rule) != ResourcePaymentSourceService.Fingerprint(d.Rule) ||
                        !PaymentResources.Same(r.Allocation, d.Allocation) || !PaymentResources.Same(r.ActualResourceCost, PaymentResources.Scale(r.Rule.UnitResourceCost, PaymentResources.Total(r.Allocation))) ||
                        !r.ItemIds.SequenceEqual(d.SelectedItemIds) || r.ItemIds.Count != r.OriginalSupplyZoneIds.Count || r.ItemIds.Count != r.PositionChanges.Count || r.ItemIds.Any(id => !ids.Add(id)) ||
                        r.ItemIds.Where((id,i) => state.ItemZones.Items.Find(item => item.ItemId == id)?.OriginalSupplyZoneId != r.OriginalSupplyZoneIds[i]).Any() ||
                        r.ItemIds.Any(id => !r.PositionChanges.Any(c => c != null && c.ItemId == id && c.From != null && c.To != null && node.PositionChanges.Any(actual => actual.ItemId == id && actual.From.ZoneId == c.From.ZoneId && actual.To.ZoneId == c.To.ZoneId)))) return false;
                    if (!ValidateTokenReceipt(state, registry, node, r, out reason)) return false;
                    allocated = PaymentResources.Add(allocated, r.Allocation); costs = PaymentResources.Add(costs, r.ActualResourceCost);
                }
                if (p.SourceReceipts.Count != p.SourceDrafts.Count(d => d.Status == "valid")) return false;
                foreach (var group in p.SourceReceipts.GroupBy(r => r.Rule.QuotaScope + "|" + r.Rule.QuotaGroupId))
                    if (group.Any(r => group.Sum(v => (long)PaymentResources.Total(v.Allocation)) > r.Rule.QuotaLimit)) return false;
                foreach (var t in PaymentResources.Types) if (allocated.Get(t) > gross.Get(t) || checked(gross.Get(t) - allocated.Get(t) + costs.Get(t)) != p.FinalCost.Get(t)) return false;
            }
            catch (OverflowException) { return false; }
            reason = ""; return true;
        }
        internal static bool ValidRule(PaymentSourceRule r) => r?.ProtocolVersion == 1 && (r.Insertion == null || ResourcePaymentInsertionService.ValidDeclaration(r.Insertion)) && r.SourceRef != null && !string.IsNullOrEmpty(r.SourceRef.SourceKey) && !string.IsNullOrEmpty(r.SourceRef.ItemId) && r.VersionRef != null &&
            r.SourceRef.AuthorizedPlayerId >= 1 && r.SourceRef.ActiveFaceId != null && r.SourceRef.GrantingSourceRefs != null && !string.IsNullOrEmpty(r.SourceRef.AbilityId) && !string.IsNullOrEmpty(r.SourceRef.DeclarationVersion) &&
            r.SourceRef.SourceKey == StableIdFactory.Create("ability-source", r.SourceRef.ItemId, r.SourceRef.AbilityId, r.SourceRef.ActiveFaceId, r.SourceRef.DeclarationVersion, r.SourceRef.AuthorizedPlayerId.ToString(CultureInfo.InvariantCulture)) && r.VersionRef.DefinitionVersion == r.SourceRef.DeclarationVersion &&
            !string.IsNullOrEmpty(r.VersionRef.ContentPackHash) && !string.IsNullOrEmpty(r.VersionRef.HandlerHash) && !string.IsNullOrEmpty(r.RuleId) && !string.IsNullOrEmpty(r.QuotaGroupId) &&
            r.MaxUnits >= 1 && r.MaxUnits <= 99 && r.QuotaLimit >= 1 && r.QuotaLimit <= 99 && new[] { "payment", "build" }.Contains(r.QuotaScope) && PaymentResources.Valid(r.UnitResourceCost) &&
            r.AllowedResourceTypes != null && r.AllowedResourceTypes.Count > 0 && r.AllowedResourceTypes.Distinct().Count() == r.AllowedResourceTypes.Count && r.AllowedResourceTypes.All(t => Enum.IsDefined(typeof(ResourceType), t)) &&
            (r.ItemCost == null || r.ItemCost.CountPerUnit >= 1 && r.ItemCost.CountPerUnit <= MaximumTokenCount && r.ItemCost.CardInstanceId == r.SourceRef.ItemId && !string.IsNullOrEmpty(r.ItemCost.LocalZoneId) &&
                ItemTransferService.IsTokenProfile(r.ItemCost.OperationProfileId) && new[] { "explicit", "equivalent" }.Contains(r.ItemCost.SelectionPolicy) && new[] { "original_supply", "retired" }.Contains(r.ItemCost.DestinationKind));
        private static bool ValidateTokenReceipt(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner, PaymentSourceReceipt receipt, out string reason)
        {
            reason = "payment_token_receipt_invalid";
            if (registry != null && (!registry.AbilityDeclarations.Any(d => d.AbilityId == receipt.Rule.SourceRef.AbilityId && d.DefinitionId == receipt.Rule.SourceRef.DefinitionId &&
                d.DeclarationVersion == receipt.Rule.SourceRef.DeclarationVersion && registry.GetAbilityHandlerHash(d.HandlerRef) == receipt.Rule.VersionRef.HandlerHash) ||
                receipt.Rule.VersionRef.ContentPackHash != registry.ContentPackHash)) return false;
            var cost = receipt.Rule.ItemCost;
            if (cost == null) return receipt.ItemIds.Count == 0 && receipt.TokenTransfer == null && receipt.TokenArguments == null;
            var args = receipt.TokenArguments; var transfer = receipt.TokenTransfer;
            if (receipt.ItemIds.Any(id => !state.ItemZones.Items.Any(i => i.ItemId == id && i.OwnerSubject.Kind == ItemSubjectKind.Player && i.OwnerSubject.PlayerId == owner.PlayerId))) return false;
            if (transfer == null || args == null || ValidateTokenArguments(args).Length > 0 || transfer.Request?.OperationId != ResourcePaymentSourceService.TokenOperationId(owner, receipt.Rule) ||
                Text(args, "operationProfileId") != cost.OperationProfileId || Number(args, "requiredCount") != (long)cost.CountPerUnit * PaymentResources.Total(receipt.Allocation) ||
                !Value(args, "itemIds").Items.Select(i => i.StringValue).SequenceEqual(receipt.ItemIds)) return false;
            var zone = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == Text(Value(args, "expectedSource"), "zoneId"));
            if (zone?.ParentKind != ZoneParentKind.Item || zone.ParentId != cost.CardInstanceId || zone.LocalZoneId != cost.LocalZoneId ||
                (cost.DestinationKind == "original_supply" ? Text(Value(args, "destination"), "kind") != "original_supply" :
                    !state.ItemZones.Zones.Any(z => z.ZoneId == Text(Value(args, "destination"), "zoneId") && z.TemplateId == "core.zone.box_retired"))) return false;
            if (!ItemTransferService.TryValidateTokenCommit(state, owner.PlayerId, args, transfer, out reason)) return false;
            if (Newtonsoft.Json.JsonConvert.SerializeObject(receipt.PositionChanges) != Newtonsoft.Json.JsonConvert.SerializeObject(transfer.Result.PositionChanges))
            { reason = "payment_token_positions_changed"; return false; }
            reason = ""; return true;
        }
        public static bool TryMigrateUnpaidV1(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner, out ResourcePaymentRuntimeState migrated, out string reason)
        {
            migrated = null; reason = "payment_legacy_requires_original_executor"; var old = owner?.Payment;
            if (old?.PaymentProtocolVersion != 1 || old.IsCommitted || old.IsCommitRequested || !old.BeforePaymentReady ||
                ResourcePaymentPreparationService.HasPendingResponse(state, owner) || owner.SettlementWindow?.Entries.Any(e => e.SettlementState == "executing") == true) return false;
            PaymentSourceDraft draft = null;
            if (old.SelectedSubstitution != null)
            {
                var saved = old.SelectedSubstitution;
                var rules = ResourcePaymentSourceService.Query(state, registry, owner, old.PaymentCost).Where(r => r.SourceRef.ItemId == saved.SourceItemId && r.ItemCost != null &&
                    state.ItemZones.Zones.Any(z => z.ZoneId == saved.SourceZoneId && z.ParentId == r.SourceRef.ItemId && z.LocalZoneId == r.ItemCost.LocalZoneId)).ToList();
                if (rules.Count != 1 || !PaymentResources.Valid(saved.Allocation) || PaymentResources.Total(saved.Allocation) > rules[0].MaxUnits) return false;
                // 旧档没有逐枚选择证明：保留分配，明确重选；绝不自动取当前第一组标记。
                draft = new PaymentSourceDraft { PaymentId = old.PaymentId, PaymentGeneration = old.PaymentGeneration, Rule = rules[0], Allocation = saved.Allocation.Clone(),
                    BuildIdentity = ResourcePaymentSourceService.BuildIdentity(state, owner), Status = "reselect_required", InvalidReason = "legacy_payment_item_selection_required" };
            }
            migrated = GameStateCloneService.DeepClone(old); migrated.SelectedSubstitution = null;
            ResourcePaymentSubstitutionEffectExecutor.EnableV2(migrated, new ResourcePaymentDemandState { FixedCost = old.PaymentCost.Clone() }, new ResourceSet());
            if (draft != null) migrated.SourceDrafts.Add(draft);
            if (old.BeforePaymentEventId.Length > 0) migrated.BeforePaymentReceipts.Add(new PaymentBeforeReceipt { EventId = old.BeforePaymentEventId, Generation = old.PaymentGeneration, GrossCost = old.PaymentCost.Clone(), Ready = old.BeforePaymentReady });
            reason = ""; return true;
        }
    }
}
