using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public static class CleanupSourceRecovery
    {
        // 由公共Host恢复入口调用；不得让JSON默认值掩盖被删掉的收据字段。
        public static bool HasRawFields(JObject state)
        {
            var nodes = state?["EffectRuntime"]?["EffectNodes"] as JArray;
            if (nodes == null) return false;
            foreach (var entry in nodes.SelectMany(n => ((n["SettlementWindow"] as JObject)?["Entries"] as JArray) ?? new JArray()))
            {
                var source = entry["CleanupSource"];
                if (source == null || source.Type == JTokenType.Null) continue; // 已知旧窗口继续原once协议。
                if (source is not JObject s || !Fields(s, "ProtocolVersion", "NextSequence", "RequestGeneration", "RepeatPolicy", "BodyHandlerRef",
                    "ActiveInvocationId", "RegistrationEventId", "RegistrationTargetId", "RegistrationReceiptId", "Sealed", "SourceRef", "VersionRef", "Invocations") ||
                    s["Invocations"] is not JArray invocations || !Integer(s, "ProtocolVersion", 1) || !Integer(s, "NextSequence", 1, int.MaxValue) ||
                    !Texts(s, "RepeatPolicy", "BodyHandlerRef", "ActiveInvocationId", "RegistrationEventId", "RegistrationTargetId", "RegistrationReceiptId") ||
                    !Integer(s, "RequestGeneration", 1, int.MaxValue) || s["Sealed"].Type != JTokenType.Boolean || !SourceFields(s["SourceRef"]) || !VersionFields(s["VersionRef"])) return false;
                foreach (var i in invocations)
                    if (i is not JObject o || !Fields(o, "ProtocolVersion", "Sequence", "InvocationId", "CommandId", "CommandDigest", "TargetEffectId",
                        "Status", "Outcome", "CompletionReceiptId", "SourceRef", "VersionRef", "RequestRef", "BodyEffectIds", "PaymentEffectIds") ||
                        !Integer(o, "ProtocolVersion", 1) || !Integer(o, "Sequence", 1, int.MaxValue) || !SourceFields(o["SourceRef"]) || !VersionFields(o["VersionRef"]) ||
                        !Texts(o, "InvocationId", "CommandId", "CommandDigest", "TargetEffectId", "Status", "Outcome", "CompletionReceiptId") ||
                        o["RequestRef"] is not JObject q || !Fields(q, "WindowId", "OwnerEffectId", "OptionId", "InteractionId", "FactsDigest", "AnsweringPlayerId", "RequestGeneration", "CandidateSetVersion") ||
                        !Integer(q, "RequestGeneration", 1, int.MaxValue) || !Integer(q, "AnsweringPlayerId", 1, int.MaxValue) ||
                        !Integer(q, "CandidateSetVersion", 1, int.MaxValue) || !Texts(q, "WindowId", "OwnerEffectId", "OptionId", "InteractionId", "FactsDigest") ||
                        o["BodyEffectIds"] is not JArray bodies || bodies.Any(v => v.Type != JTokenType.String) ||
                        o["PaymentEffectIds"] is not JArray payments || payments.Any(v => v.Type != JTokenType.String)) return false;
            }
            return true;
        }
        private static bool Fields(JObject o, params string[] names) => names.All(n => o[n] != null && o[n].Type != JTokenType.Null);
        private static bool Texts(JObject o, params string[] names) => names.All(n => o[n]?.Type == JTokenType.String);
        private static bool Integer(JObject o, string n, long min, long max = 1) => o[n]?.Type == JTokenType.Integer && o[n].Value<long>() >= min && o[n].Value<long>() <= max;
        private static bool SourceFields(JToken t) => t is JObject o && Texts(o, "SourceKey", "ItemId", "DefinitionId", "AbilityId", "ActiveFaceId", "DeclarationVersion", "AuthorizationRef", "EffectiveZoneId", "RuleOwnerItemId") &&
            Integer(o, "AuthorizedPlayerId", 1, int.MaxValue) && o["GrantingSourceRefs"] is JArray grants && grants.All(v => v.Type == JTokenType.String);
        private static bool VersionFields(JToken t) => t is JObject o && Texts(o, "ContractVersion", "ContentPackHash", "DefinitionVersion", "HandlerHash", "ExecutorVersion");

        public static bool TryValidateEntry(GameState state, SettlementWindowRuntimeState window, SettlementEntryRuntimeState entry,
            EffectRegistry registry, out string reason)
        {
            reason = "cleanup_source_state_invalid";
            var source = entry.CleanupSource; var runtime = state.EffectRuntime;
            if (source == null || source.ProtocolVersion != 1 || source.SourceRef == null || source.VersionRef == null || source.Invocations == null ||
                source.SourceRef.GrantingSourceRefs == null || source.RepeatPolicy != "once" && source.RepeatPolicy != "unbounded" ||
                window.Context.Kind != SettlementEntryService.PlayerCleanup || entry.EntryKind != SettlementEntryService.CleanupOrder ||
                entry.WholeEffectPolicy != SettlementWholeEffectPolicy.Optional || entry.Effects.Count != 0 || entry.Visibility != "owner" || entry.MaxUses != 1 ||
                source.SourceRef.AuthorizedPlayerId != window.Context.PlayerId || source.SourceRef.ItemId != entry.SourceItemId || source.SourceRef.AbilityId != entry.AbilityId ||
                source.SourceRef.SourceKey != StableIdFactory.Create("ability-source", source.SourceRef.ItemId, source.SourceRef.AbilityId, source.SourceRef.ActiveFaceId,
                    source.SourceRef.DeclarationVersion, window.Context.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)) ||
                source.SourceRef.DefinitionId != entry.SourceDefinitionId || source.RegistrationTargetId != entry.TargetEffectId ||
                source.RegistrationEventId != window.Context.TriggerId || entry.OptionId != CleanupSourceService.OptionId(window.Context.Id, source.SourceRef.SourceKey, entry.LocalId) ||
                source.RegistrationReceiptId != StableIdFactory.Create("cleanup-source-registration", window.Context.Id, entry.TargetEffectId, source.SourceRef.SourceKey) ||
                source.VersionRef.ExecutorVersion != CleanupSourceService.Version || source.VersionRef.ContractVersion != "QY-COMMON/1.0.1" ||
                string.IsNullOrEmpty(source.VersionRef.ContentPackHash) || string.IsNullOrEmpty(source.VersionRef.HandlerHash)) return false;
            var anchor = runtime.EffectNodes.SingleOrDefault(n => n.EffectId == entry.TargetEffectId);
            var owner = runtime.EffectNodes.SingleOrDefault(n => n.EffectId == window.Context.OwnerEffectId);
            if (anchor == null || owner == null || anchor.EffectTypeId != CleanupSourceInvocationExecutor.RegistrationTypeId || anchor.DefinitionVersion != CleanupSourceService.Version ||
                anchor.PlayerId != window.Context.PlayerId || anchor.ParentEffectId != owner.EffectId || !owner.ChildEffectIds.Contains(anchor.EffectId) ||
                anchor.ChildEffectIds.Count != 0 || (!window.IsClosed ? anchor.Status != EffectNodeStatus.Created :
                    anchor.Status != EffectNodeStatus.Failed || anchor.FailureReason != "whole_effect_declined") ||
                Text(anchor.NormalizedArguments, "sourceKey") != source.SourceRef.SourceKey ||
                Text(anchor.NormalizedArguments, "sourceProofDigest") != CleanupSourceService.SourceProofDigest(source.SourceRef) ||
                Text(anchor.NormalizedArguments, "contentHash") != source.VersionRef.ContentPackHash ||
                Text(anchor.NormalizedArguments, "handlerHash") != source.VersionRef.HandlerHash || anchor.Settlement == null ||
                Text(anchor.NormalizedArguments, "handlerRef") != source.BodyHandlerRef ||
                anchor.Settlement.AbilityId != entry.AbilityId || anchor.Settlement.LocalId != entry.LocalId || anchor.Settlement.SourceItemId != entry.SourceItemId ||
                anchor.Settlement.SourceEffectId != entry.SourceEffectId || anchor.Settlement.WholeEffectPolicy != entry.WholeEffectPolicy ||
                !runtime.DispatchReceipts.Any(r => r.EventId == window.Context.TriggerId && r.Succeeded && r.HandlerId == "ability-source.event.PlayerCleanupStarted" && r.AttachedEffectIds.Contains(anchor.EffectId))) return false;
            var declaration = (registry?.AbilityDeclarations ?? state.ContentDefinitions?.AbilitySources)?.SingleOrDefault(d => d.AbilityId == entry.AbilityId && d.DefinitionId == entry.SourceDefinitionId);
            if (declaration == null || declaration.Kind != "cleanup" || declaration.RepeatPolicy != source.RepeatPolicy ||
                declaration.HandlerRef != source.BodyHandlerRef || declaration.DeclarationVersion != source.VersionRef.DefinitionVersion ||
                source.SourceRef.DeclarationVersion != declaration.DeclarationVersion ||
                !state.ItemZones.Items.Any(i => i.ItemId == entry.SourceItemId && i.DefinitionId == entry.SourceDefinitionId)) return false;
            if (registry != null && (registry.ContentPackHash != source.VersionRef.ContentPackHash || registry.HandlerHash(source.BodyHandlerRef) != source.VersionRef.HandlerHash)) return false;
            if (registry != null && (!registry.TryGetSettlementSourceContract(entry.AbilityId, out var contract) || contract.DefinitionId != entry.SourceDefinitionId ||
                contract.LocalId != entry.LocalId || contract.EntryKind != entry.EntryKind || contract.WholeEffectPolicy != entry.WholeEffectPolicy || contract.AvailabilityHandlerId != entry.AvailabilityHandlerId)) return false;
            if (source.NextSequence != source.Invocations.Count + 1 || entry.AcceptedUseCount != source.Invocations.Count ||
                source.RepeatPolicy == "once" && source.Invocations.Count > 1) return false;
            for (int index = 0; index < source.Invocations.Count; index++)
            {
                var i = source.Invocations[index];
                if (i == null || i.ProtocolVersion != 1 || i.Sequence != index + 1 || i.InvocationId != CleanupSourceService.InvocationId(entry.OptionId, i.Sequence) ||
                    string.IsNullOrEmpty(i.CommandId) || i.RequestRef == null || i.SourceRef == null || i.VersionRef == null ||
                    i.SourceRef.SourceKey != source.SourceRef.SourceKey || i.SourceRef.AuthorizedPlayerId != window.Context.PlayerId ||
                    i.SourceRef.ItemId != entry.SourceItemId || i.SourceRef.AbilityId != entry.AbilityId || i.SourceRef.DefinitionId != entry.SourceDefinitionId || i.SourceRef.GrantingSourceRefs == null ||
                    i.VersionRef.ContentPackHash != source.VersionRef.ContentPackHash || i.VersionRef.HandlerHash != source.VersionRef.HandlerHash ||
                    i.VersionRef.DefinitionVersion != source.VersionRef.DefinitionVersion || i.VersionRef.ExecutorVersion != source.VersionRef.ExecutorVersion ||
                    i.RequestRef.WindowId != window.Context.Id || i.RequestRef.OwnerEffectId != window.Context.OwnerEffectId || i.RequestRef.OptionId != entry.OptionId || i.RequestRef.AnsweringPlayerId != window.Context.PlayerId ||
                    i.RequestRef.CandidateSetVersion != i.RequestRef.RequestGeneration ||
                    i.RequestRef.RequestGeneration != i.Sequence || i.RequestRef.InteractionId != StableIdFactory.Create("cleanup-source-request", window.Context.Id, entry.OptionId,
                        i.RequestRef.RequestGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture), i.RequestRef.FactsDigest) ||
                    i.CommandDigest != EffectTreeExecutor.CommandDigest(window.Context.PlayerId, i.RequestRef) || i.BodyEffectIds == null || i.PaymentEffectIds == null) return false;
                var target = runtime.EffectNodes.SingleOrDefault(n => n.EffectId == i.TargetEffectId);
                if (target == null || target.EffectTypeId != CleanupSourceInvocationExecutor.TypeId || target.DefinitionVersion != CleanupSourceService.Version || target.ParentEffectId != owner.EffectId ||
                    target.PlayerId != window.Context.PlayerId || !owner.ChildEffectIds.Contains(target.EffectId) || Text(target.NormalizedArguments, "invocationId") != i.InvocationId ||
                    Text(target.NormalizedArguments, "sourceProofDigest") != CleanupSourceService.SourceProofDigest(i.SourceRef) ||
                    !target.ChildEffectIds.Take(i.BodyEffectIds.Count).SequenceEqual(i.BodyEffectIds) ||
                    target.ChildEffectIds.Skip(i.BodyEffectIds.Count).Any(id => !runtime.DispatchReceipts.Any(r => r.Succeeded && r.AttachedEffectIds.Contains(id))) ||
                    i.BodyEffectIds.Distinct().Count() != i.BodyEffectIds.Count ||
                    i.BodyEffectIds.Any(id => !runtime.EffectNodes.Any(n => n.EffectId == id && n.ParentEffectId == target.EffectId)) ||
                    i.PaymentEffectIds.Distinct().Count() != i.PaymentEffectIds.Count || i.PaymentEffectIds.Any(id => !runtime.EffectNodes.Any(n => n.EffectId == id &&
                        InfluenceDepartureProtocol.IsWithin(state, id, target.EffectId) && CleanupSourceInvocationExecutor.IsPaid(state, n)))) return false;
                if (i.Status == "executing")
                {
                    if (CleanupSourceService.Terminal(target) || source.ActiveInvocationId != i.InvocationId || window.ActiveTargetEffectId != target.EffectId || i.CompletionReceiptId.Length > 0) return false;
                }
                else if (!new[] { "completed", "declined", "condition_failed" }.Contains(i.Status) || i.Outcome != i.Status || target.Status != EffectNodeStatus.Completed ||
                    !i.PaymentEffectIds.SequenceEqual(runtime.EffectNodes.Where(n => InfluenceDepartureProtocol.IsWithin(state, n.EffectId, target.EffectId) && CleanupSourceInvocationExecutor.IsPaid(state, n))
                        .Select(n => n.EffectId).OrderBy(id => id, StringComparer.Ordinal)) ||
                    Text(target.NormalizedResult, "outcome") != i.Outcome || i.CompletionReceiptId != StableIdFactory.Create("cleanup-invocation-complete", i.InvocationId, target.EffectId, i.Outcome)) return false;
            }
            var active = source.Invocations.Where(i => i.Status == "executing").ToList();
            if (active.Count > 1 || (active.Count == 0 ? source.ActiveInvocationId.Length != 0 : source.ActiveInvocationId != active[0].InvocationId) ||
                source.RequestGeneration != source.Invocations.Count + (active.Count == 0 ? 1 : 0) ||
                window.IsClosed != source.Sealed || window.IsClosed && active.Count > 0 ||
                entry.SettlementState != (active.Count > 0 ? "executing" : source.Sealed ? "declined" : source.RepeatPolicy == "once" && source.Invocations.Count > 0 ? "resolved" : "available")) return false;
            reason = ""; return true;
        }

        public static bool TryValidate(GameState state, EffectRegistry registry, out string reason)
        {
            if (AbilityActionRecovery.HasRetiredMarkerPayment(state))
            { reason = AbilityActionRecovery.RetiredMarkerPaymentDiagnostic; return false; }
            reason = "cleanup_source_duplicate_identity";
            var invocations = CleanupSourceService.Invocations(state).ToList();
            if (invocations.Select(i => i.InvocationId).Distinct().Count() != invocations.Count || invocations.Select(i => i.CommandId).Distinct().Count() != invocations.Count ||
                invocations.Select(i => i.TargetEffectId).Distinct().Count() != invocations.Count) return false;
            if (state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == CleanupSourceInvocationExecutor.TypeId && !invocations.Any(i => i.TargetEffectId == n.EffectId))) return false;
            foreach (var owner in state.EffectRuntime.EffectNodes.Where(n => n.SettlementWindow != null))
                foreach (var entry in CleanupSourceService.Sources(owner.SettlementWindow))
                    if (!TryValidateEntry(state, owner.SettlementWindow, entry, registry, out reason)) return false;
            reason = ""; return true;
        }
    }
}
