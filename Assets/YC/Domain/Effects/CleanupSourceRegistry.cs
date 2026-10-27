using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public sealed partial class EffectRegistry
    {
        public EffectSpec CreateCleanupSourceRegistration(AbilitySourceRef source, RuleEvent trigger)
        {
            var declaration = SourceDeclaration(source);
            var spec = EffectSpec.Create(CleanupSourceInvocationExecutor.RegistrationTypeId, Object(
                "sourceKey", NormalizedValue.CreateString(source.SourceKey), "handlerRef", NormalizedValue.CreateString(declaration.HandlerRef),
                "triggerEventId", NormalizedValue.CreateString(trigger.EventId), "contentHash", NormalizedValue.CreateString(ContentPackHash),
                "handlerHash", NormalizedValue.CreateString(HandlerHash(declaration.HandlerRef)),
                "sourceProofDigest", NormalizedValue.CreateString(CleanupSourceService.SourceProofDigest(source))), source.AuthorizedPlayerId);
            spec.SourceId = source.ItemId;
            return spec;
        }
        internal SettlementEntryRuntimeState RegisterAttachedCleanupSource(GameState work, EffectNodeRuntimeState owner,
            EffectNodeRuntimeState target, SettlementSourceContract contract)
        {
            var source = QuerySources(work, owner.PlayerId, "cleanup", "PlayerCleanupStarted")
                .SingleOrDefault(s => s.SourceKey == Text(target.NormalizedArguments, "sourceKey"));
            if (source == null || source.AbilityId != target.Settlement.AbilityId || source.ItemId != target.Settlement.SourceItemId ||
                target.SourceId != source.ItemId || Text(target.NormalizedArguments, "triggerEventId") != owner.SettlementWindow.Context.TriggerId ||
                Text(target.NormalizedArguments, "contentHash") != ContentPackHash ||
                Text(target.NormalizedArguments, "sourceProofDigest") != CleanupSourceService.SourceProofDigest(source) ||
                Text(target.NormalizedArguments, "handlerRef") != SourceDeclaration(source).HandlerRef ||
                Text(target.NormalizedArguments, "handlerHash") != HandlerHash(SourceDeclaration(source).HandlerRef))
                throw new ArgumentException("cleanup_source_attached_identity_invalid");
            return CleanupSourceService.RegisterSource(work, owner.SettlementWindow, target, source, SourceDeclaration(source), contract);
        }
        private Func<GameState, SettlementWindowRuntimeState, SettlementEntryRuntimeState, NormalizedValue> cleanupSourceFacts;
        private Func<GameState, SettlementWindowRuntimeState, SettlementEntryRuntimeState, SettlementAvailabilityResult> cleanupSourceAvailability;
        public void RegisterCleanupSourceQueries(
            Func<GameState, SettlementWindowRuntimeState, SettlementEntryRuntimeState, NormalizedValue> facts,
            Func<GameState, SettlementWindowRuntimeState, SettlementEntryRuntimeState, SettlementAvailabilityResult> availability)
        {
            if (facts == null || availability == null || cleanupSourceFacts != null) throw new ArgumentException("cleanup_source_query_registration_invalid");
            cleanupSourceFacts = facts; cleanupSourceAvailability = availability;
        }

        public AbilitySourceRef CurrentCleanupSource(GameState state, SettlementEntryRuntimeState entry) => QuerySources(state,
            entry.CleanupSource.SourceRef.AuthorizedPlayerId, "cleanup", "PlayerCleanupStarted")
            .SingleOrDefault(s => s.SourceKey == entry.CleanupSource.SourceRef.SourceKey);

        public SettlementAvailabilityResult QueryCleanupSource(GameState state, SettlementWindowRuntimeState window, SettlementEntryRuntimeState entry)
        {
            string reason = CleanupSourceService.BoundaryReason(state, window, entry);
            if (reason.Length == 0 && CurrentCleanupSource(state, entry) == null) reason = "settlement_source_unavailable";
            var version = entry.CleanupSource.VersionRef;
            if (reason.Length == 0 && (version.ContentPackHash != ContentPackHash || version.HandlerHash != HandlerHash(entry.CleanupSource.BodyHandlerRef) ||
                version.ExecutorVersion != CleanupSourceService.Version || version.DefinitionVersion != SourceDeclaration(entry.CleanupSource.SourceRef).DeclarationVersion))
                reason = "cleanup_source_version_mismatch";
            if (reason.Length > 0) return new SettlementAvailabilityResult { Enabled = false, DisabledReasonKey = reason };
            if (cleanupSourceAvailability == null) return new SettlementAvailabilityResult { DisabledReasonKey = "cleanup_source_query_unavailable" };
            var result = cleanupSourceAvailability(state, window, entry);
            if (result == null || !result.Enabled && string.IsNullOrEmpty(result.DisabledReasonKey))
                throw new InvalidOperationException("cleanup_source_availability_invalid");
            return result;
        }

        public CleanupSourceRequestRef GetCleanupSourceRequest(GameState state, SettlementWindowRuntimeState window, SettlementEntryRuntimeState entry)
        {
            if (cleanupSourceFacts == null) throw new InvalidOperationException("cleanup_source_facts_unavailable");
            var facts = cleanupSourceFacts(state, window, entry);
            if (facts?.Kind != NormalizedValueKind.Object || !facts.IsValid()) throw new InvalidOperationException("cleanup_source_facts_invalid");
            var source = entry.CleanupSource;
            var current = CurrentCleanupSource(state, entry);
            string digest = AbilityActionExecutor.Digest(Object("policy", NormalizedValue.CreateString(CleanupSourceService.RequestPolicy),
                "facts", facts, "source", NormalizedValue.CreateString(source.SourceRef.SourceKey),
                "currentProof", NormalizedValue.CreateString(current == null ? "" : CleanupSourceService.SourceProofDigest(current)),
                "sourceEffective", NormalizedValue.CreateBoolean(current != null),
                "closed", NormalizedValue.CreateBoolean(window.IsClosed || source.Sealed), "active", NormalizedValue.CreateString(window.ActiveTargetEffectId),
                "generation", NormalizedValue.CreateInteger(source.RequestGeneration), "content", NormalizedValue.CreateString(ContentPackHash),
                "handler", NormalizedValue.CreateString(HandlerHash(source.BodyHandlerRef))));
            return new CleanupSourceRequestRef { WindowId = window.Context.Id, OwnerEffectId = window.Context.OwnerEffectId,
                OptionId = entry.OptionId, AnsweringPlayerId = window.Context.PlayerId,
                RequestGeneration = source.RequestGeneration, CandidateSetVersion = source.RequestGeneration, FactsDigest = digest,
                InteractionId = StableIdFactory.Create("cleanup-source-request", window.Context.Id, entry.OptionId,
                    source.RequestGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture), digest) };
        }

        internal IList<EffectSpec> ConstructCleanupInvocationBody(GameState state, EffectNodeRuntimeState owner,
            SettlementEntryRuntimeState entry, AbilitySourceRef source) => ConstructAbilityBody(entry.CleanupSource.BodyHandlerRef,
                new AbilityActionCallContext { State = state, OwnerNode = owner, SourceRef = source, Arguments = Object() });

        // 留给公共授权入口的精确接点；旧once仍沿原SourceUse证明，不改其策略。
        internal bool IsCleanupInvocationCaller(GameState state, string parentId, AbilityActionCallPolicy policy, int player)
        {
            var parent = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == parentId);
            var invocation = CleanupSourceService.Invocations(state).SingleOrDefault(i => i.TargetEffectId == parentId && i.Status == "executing");
            return parent?.EffectTypeId == CleanupSourceInvocationExecutor.TypeId && parent.PlayerId == player &&
                parent.Status != EffectNodeStatus.Faulted && !CleanupSourceService.Terminal(parent) && invocation != null &&
                invocation.SourceRef.AuthorizedPlayerId == player && invocation.SourceRef.AbilityId == policy.CallerAbilityId &&
                invocation.VersionRef.HandlerHash == policy.CallerHandlerHash && invocation.VersionRef.ContentPackHash == ContentPackHash;
        }
    }
}
