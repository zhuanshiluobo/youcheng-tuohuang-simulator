using System;
using System.Linq;
using System.Globalization;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public static class AbilityActionRecovery
    {
        // 仅识别旧序列化类型并诊断拒绝；不是现行费用或执行原语入口。
        public const string RetiredMarkerPaymentTypeId = "effect.card.pay_markers";
        public const string RetiredMarkerPaymentDiagnostic = "retired_marker_payment_state_requires_migration";
        public static bool IsRetiredMarkerPayment(string typeId) => typeId == RetiredMarkerPaymentTypeId;
        public static bool HasRetiredMarkerPayment(GameState state)
        {
            bool Nested(System.Collections.Generic.IEnumerable<EffectSpecRuntimeState> specs) => specs != null &&
                specs.Any(s => s != null && (IsRetiredMarkerPayment(s.EffectTypeId) || Nested(s.NestedEffects)));
            return state?.EffectRuntime?.EffectNodes?.Any(n => n != null && (IsRetiredMarkerPayment(n.EffectTypeId) || Nested(n.NestedEffects))) == true;
        }
        public static bool TryValidateAbilityActions(GameState state, EffectRegistry registry, out string diagnostic)
        {
            diagnostic = ""; var runtime = state?.EffectRuntime;
            if (HasRetiredMarkerPayment(state)) return Fail(RetiredMarkerPaymentDiagnostic, out diagnostic);
            if (runtime?.AbilityActionInvocations == null || runtime.AbilitySourceUses == null) return Fail("lists_missing", out diagnostic);
            if (runtime.AbilityActionProtocolVersion != 0 && runtime.AbilityActionProtocolVersion != 1 || runtime.AbilityActionProtocolVersion == 0 &&
                (runtime.AbilityActionInvocations.Count > 0 || runtime.AbilitySourceUses.Count > 0)) return Fail("protocol_invalid", out diagnostic);
            if (runtime.AbilityActionInvocations.Select(i => i.InvocationId).Distinct().Count() != runtime.AbilityActionInvocations.Count ||
                runtime.AbilityActionInvocations.Select(i => i.CommandId).Distinct().Count() != runtime.AbilityActionInvocations.Count ||
                runtime.AbilitySourceUses.Select(i => i.ReceiptId).Distinct().Count() != runtime.AbilitySourceUses.Count) return Fail("duplicate_identity", out diagnostic);
            foreach (var i in runtime.AbilityActionInvocations)
            {
                string id = i.InvocationId; var owner = runtime.EffectNodes.Find(n => n.EffectId == i.OwnerEffectId);
                if (i.ProtocolVersion != 1 && i.ProtocolVersion != AbilityActionEntryCondition.InvocationProtocolVersion || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(i.CommandId) || string.IsNullOrEmpty(i.CommandDigest) ||
                    owner == null || owner.EffectTypeId != AbilityActionExecutor.TypeId && owner.EffectTypeId != AbilityActionExecutor.DirectTypeId ||
                    i.CommandDigest != AbilityActionExecutor.Digest(owner.NormalizedArguments) || i.ParentEffectId != owner.ParentEffectId ||
                    i.SourceRef == null || i.VersionRef == null || i.RequestRef == null || i.SourceRef.AuthorizedPlayerId != owner.PlayerId ||
                    !state.ItemZones.Items.Any(item => item.ItemId == i.SourceRef.ItemId && item.DefinitionId == i.SourceRef.DefinitionId) ||
                    i.VersionRef.ContractVersion != "QY-COMMON/1.0.1" || i.VersionRef.ExecutorVersion != AbilityActionExecutor.Version ||
                    i.PaymentEffectIds == null || i.BodyEffectIds == null || i.ReceiptIds == null || i.ReceiptIds.Distinct().Count() != i.ReceiptIds.Count ||
                    !new[] { "accepted", "entry_payment", "activated", "body", "completed", "faulted" }.Contains(i.Phase)) return Fail(id + ":identity_or_phase_invalid", out diagnostic);
                if (i.SourceRef.SourceKey != StableIdFactory.Create("ability-source", i.SourceRef.ItemId, i.SourceRef.AbilityId, i.SourceRef.ActiveFaceId, i.SourceRef.DeclarationVersion, owner.PlayerId.ToString(CultureInfo.InvariantCulture)) ||
                    i.SourceRef.RuleOwnerItemId != i.SourceRef.ItemId || i.RequestRef.AnsweringPlayerId != owner.PlayerId ||
                    i.RequestRef.AllowedAnswers == null || i.RequestRef.AllowedAnswers.Count != 1 || i.RequestRef.AllowedAnswers[0] != i.OptionId ||
                    owner.EffectTypeId == AbilityActionExecutor.TypeId && (i.OptionId != Text(owner.NormalizedArguments, "optionId") || i.RequestRef.InteractionId != Text(owner.NormalizedArguments, "requestId") || i.RequestRef.FactsDigest != Text(owner.NormalizedArguments, "factsDigest"))) return Fail(id + ":source_or_request_identity_invalid", out diagnostic);
                if ((i.Phase == "activated" || i.Phase == "body") && !i.ActivationCommitted || i.MainActionCommitted && !i.ActivationCommitted ||
                    i.Phase == "completed" && string.IsNullOrEmpty(i.Outcome) || i.Phase == "faulted" && (string.IsNullOrEmpty(i.FirstFaultPhase) || string.IsNullOrEmpty(i.FailureReason))) return Fail(id + ":receipt_phase_conflict", out diagnostic);
                var children = AbilityActionExecutor.Descendants(state, owner.EffectId);
                if (i.PaymentEffectIds.Concat(i.BodyEffectIds).Any(child => !children.Any(n => n.EffectId == child)) || i.PaymentEffectIds.Intersect(i.BodyEffectIds).Any() ||
                    i.PaymentCommitted && !AbilityActionExecutor.HasCommittedEntryPayment(state, i)) return Fail(id + ":payment_or_child_receipt_missing", out diagnostic);
                if (AbilityActionExecutor.EntryPaymentNodes(state, i).Any(n => n.Status == EffectNodeStatus.Completed &&
                    AbilityActionExecutor.TokenTransferReceiptRequired(n) && !AbilityActionExecutor.HasCommittedTokenTransfer(state, n))) return Fail(id + ":cost_transfer_receipt_missing", out diagnostic);
                if (i.ActivationCommitted && i.PaymentCommitted != AbilityActionExecutor.HasCommittedEntryPayment(state, i))
                    return Fail(id + ":entry_payment_commit_mismatch", out diagnostic);
                string entryConditionError = AbilityActionEntryCondition.ValidateReceipt(state, registry, i);
                if (entryConditionError.Length > 0) return Fail(id + ":" + entryConditionError, out diagnostic);
                if (registry != null)
                {
                    var declaration = registry.AbilityDeclarations.SingleOrDefault(d => d.AbilityId == i.SourceRef.AbilityId && d.DefinitionId == i.SourceRef.DefinitionId);
                    var action = declaration?.Actions.SingleOrDefault(a => a.ActionId == i.ActionId);
                    if (!registry.HasAbilityCatalog || action == null || i.SourceRef.DeclarationVersion != declaration.DeclarationVersion || i.VersionRef.ContentPackHash != registry.ContentPackHash ||
                        i.VersionRef.HandlerHash != registry.HandlerHash(action.BodyHandlerRef) || i.VersionRef.DefinitionVersion != declaration.DeclarationVersion) return Fail(id + ":frozen_content_mismatch", out diagnostic);
                    if (registry.NormalizeActionArguments(i.ActionId, Value(owner.NormalizedArguments, "arguments") ?? Object(), out _).Length > 0)
                        return Fail(id + ":arguments_invalid", out diagnostic);
                    if (action.UsageTrackId.Length > 0 && (string.IsNullOrEmpty(i.TrackBindingId) || registry.UsageProvider(action.UsageTrackId)?.DefinitionVersion != i.UsageProviderVersion)) return Fail(id + ":usage_provider_version_mismatch", out diagnostic);
                    if (i.CallPolicyRef == AbilityLegacyStyleBridge.PolicyRef)
                    { if (action.DirectCallPolicy != AbilityLegacyStyleBridge.PolicyRef || !AbilityLegacyStyleBridge.ValidateReceipt(state, i)) return Fail(id + ":legacy_style_grant_invalid", out diagnostic); }
                    else if (i.CallPolicyRef.Length > 0 && (!registry.TryCallPolicy(i.CallPolicyRef, out var policy) || policy.TargetActionId != i.ActionId ||
                        registry.ValidateActionCaller(state, i.ParentEffectId, policy, owner.PlayerId).Length > 0 || owner.ContinuationAbilityId != policy.CallerAbilityId || owner.ContinuationContentHash != policy.CallerHandlerHash)) return Fail(id + ":direct_policy_or_parent_invalid", out diagnostic);
                }
            }
            foreach (var receipt in runtime.AbilitySourceUses)
                if (receipt == null || receipt.ProtocolVersion != 1 || string.IsNullOrEmpty(receipt.SourceKey) || string.IsNullOrEmpty(receipt.ScopeKey) ||
                    receipt.SourceRef == null || receipt.VersionRef == null || receipt.SourceRef.SourceKey != receipt.SourceKey || receipt.VersionRef.ExecutorVersion != "1.0.0" ||
                    receipt.SourceRef.ItemId != receipt.ItemId || receipt.SourceRef.AbilityId != receipt.AbilityId || string.IsNullOrEmpty(receipt.VersionRef.HandlerHash) || string.IsNullOrEmpty(receipt.VersionRef.DefinitionVersion) ||
                    !runtime.EffectNodes.Any(n => n.EffectId == receipt.OwnerEffectId && n.PlayerId == receipt.PlayerId) ||
                    !state.ItemZones.Items.Any(i => i.ItemId == receipt.ItemId) || !runtime.RuleEvents.Any(e => e.EventId == receipt.TriggerEventId) ||
                    !runtime.DispatchReceipts.Any(r => r.EventId == receipt.TriggerEventId && r.AttachedEffectIds.Contains(receipt.OwnerEffectId))) return Fail("source_use_receipt_invalid", out diagnostic);
                else if (registry != null && (receipt.VersionRef.ContentPackHash != registry.ContentPackHash || !registry.AbilityDeclarations.Any(d => d.AbilityId == receipt.AbilityId && d.DeclarationVersion == receipt.VersionRef.DefinitionVersion &&
                    registry.HandlerHash(d.HandlerRef) == receipt.VersionRef.HandlerHash))) return Fail("source_use_frozen_content_mismatch", out diagnostic);
            return true;
        }
        private static bool Fail(string text, out string diagnostic) { diagnostic = "ability_action.v1:" + text; return false; }
    }
}
