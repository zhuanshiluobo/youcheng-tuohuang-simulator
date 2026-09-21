using System;
using System.Collections.Generic;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Domain.Interactions
{
    public sealed class CandidatePolicyContext
    {
        internal CandidatePolicyContext(
            CandidateSetDraft draft,
            CandidatePolicyRegistration registration,
            GameState state = null)
        {
            Draft = draft;
            Registration = registration;
            State = state;
        }

        public CandidateSetDraft Draft { get; private set; }
        public CandidatePolicyRegistration Registration { get; private set; }
        public GameState State { get; private set; }
    }

    public sealed class CandidatePolicyRegistration
    {
        public CandidatePolicyRegistration(
            string subscriptionId,
            string candidateKind,
            Func<CandidatePolicyContext, IList<CandidatePatch>> patchProvider,
            int routeTier = 0,
            int priority = 0,
            string sourceContentInstanceId = "",
            string sourceAbilityId = "",
            string handlerId = "")
        {
            if (string.IsNullOrEmpty(subscriptionId)) throw new ArgumentException("候选策略订阅 ID 不能为空。", nameof(subscriptionId));
            if (string.IsNullOrEmpty(candidateKind)) throw new ArgumentException("候选类型不能为空。", nameof(candidateKind));
            if (patchProvider == null) throw new ArgumentNullException(nameof(patchProvider));
            SubscriptionId = subscriptionId;
            CandidateKind = candidateKind;
            PatchProvider = patchProvider;
            RouteTier = routeTier;
            Priority = priority;
            SourceContentInstanceId = sourceContentInstanceId ?? string.Empty;
            SourceAbilityId = sourceAbilityId ?? string.Empty;
            HandlerId = string.IsNullOrEmpty(handlerId) ? subscriptionId : handlerId;
        }

        public string SubscriptionId { get; private set; }
        public string CandidateKind { get; private set; }
        public Func<CandidatePolicyContext, IList<CandidatePatch>> PatchProvider { get; private set; }
        public int RouteTier { get; private set; }
        public int Priority { get; private set; }
        public string SourceContentInstanceId { get; private set; }
        public string SourceAbilityId { get; private set; }
        public string HandlerId { get; private set; }
    }

    public sealed class CandidateSortPolicy
    {
        public CandidateSortPolicy(string candidateKind, Func<string, string> sortKeyProvider)
        {
            if (string.IsNullOrEmpty(candidateKind)) throw new ArgumentException("候选类型不能为空。", nameof(candidateKind));
            if (sortKeyProvider == null) throw new ArgumentNullException(nameof(sortKeyProvider));
            CandidateKind = candidateKind;
            SortKeyProvider = sortKeyProvider;
        }

        public string CandidateKind { get; private set; }
        public Func<string, string> SortKeyProvider { get; private set; }
    }

    public sealed class CandidatePolicyRegistry
    {
        private readonly List<CandidatePolicyRegistration> registrations = new List<CandidatePolicyRegistration>();
        private readonly List<CandidateSortPolicy> sortPolicies = new List<CandidateSortPolicy>();

        public void Register(CandidatePolicyRegistration registration)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            for (int i = 0; i < registrations.Count; i++)
            {
                if (string.Equals(registrations[i].SubscriptionId, registration.SubscriptionId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("候选策略订阅 ID 重复：" + registration.SubscriptionId);
                }
            }

            registrations.Add(registration);
        }

        public void RegisterSortPolicy(CandidateSortPolicy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            for (int i = 0; i < sortPolicies.Count; i++)
            {
                if (string.Equals(sortPolicies[i].CandidateKind, policy.CandidateKind, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("候选排序策略重复：" + policy.CandidateKind);
                }
            }

            sortPolicies.Add(policy);
        }

        internal IList<CandidatePolicyRegistration> GetMatching(string candidateKind)
        {
            var result = new List<CandidatePolicyRegistration>();
            for (int i = 0; i < registrations.Count; i++)
            {
                CandidatePolicyRegistration registration = registrations[i];
                if (registration.CandidateKind == candidateKind || registration.CandidateKind == "*")
                {
                    result.Add(registration);
                }
            }

            result.Sort(CompareRegistrations);
            return result;
        }

        internal string GetSortKey(string candidateKind, string candidateId)
        {
            for (int i = 0; i < sortPolicies.Count; i++)
            {
                if (sortPolicies[i].CandidateKind == candidateKind)
                {
                    string key = sortPolicies[i].SortKeyProvider(candidateId);
                    return key ?? string.Empty;
                }
            }

            return candidateId ?? string.Empty;
        }

        private static int CompareRegistrations(CandidatePolicyRegistration left, CandidatePolicyRegistration right)
        {
            int comparison = left.RouteTier.CompareTo(right.RouteTier);
            if (comparison != 0) return comparison;
            comparison = left.Priority.CompareTo(right.Priority);
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.SourceContentInstanceId, right.SourceContentInstanceId);
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.SourceAbilityId, right.SourceAbilityId);
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.HandlerId, right.HandlerId);
            if (comparison != 0) return comparison;
            return StringComparer.Ordinal.Compare(left.SubscriptionId, right.SubscriptionId);
        }
    }

    public sealed class CandidateResolutionResult
    {
        internal CandidateResolutionResult(
            bool succeeded,
            CandidateResolutionRecord record,
            string faultCode,
            string diagnostic)
        {
            Succeeded = succeeded;
            Record = record;
            FaultCode = faultCode ?? string.Empty;
            Diagnostic = diagnostic ?? string.Empty;
        }

        public bool Succeeded { get; private set; }
        public CandidateResolutionRecord Record { get; private set; }
        public string FaultCode { get; private set; }
        public string Diagnostic { get; private set; }
        public IList<string> FinalCandidateIds
        {
            get { return Record == null ? new List<string>() : Record.FinalCandidateIds.AsReadOnly(); }
        }
    }

    public sealed class CandidateSetResolver
    {
        public const string InvalidDraft = "invalid_draft";
        public const string InvalidTarget = "invalid_target";
        public const string PatchFault = "patch_fault";

        public CandidateResolutionResult Resolve(
            CandidateSetDraft draft,
            CandidatePolicyRegistry registry,
            int stateRevision = 0)
        {
            return ResolveInternal(draft, registry, stateRevision, null);
        }

        public CandidateResolutionResult Resolve(
            GameState state,
            CandidateSetDraft draft,
            CandidatePolicyRegistry registry,
            int stateRevision = 0)
        {
            return ResolveInternal(draft, registry, stateRevision, state);
        }

        private CandidateResolutionResult ResolveInternal(
            CandidateSetDraft draft,
            CandidatePolicyRegistry registry,
            int stateRevision,
            GameState state)
        {
            if (draft == null || registry == null)
            {
                return Failure(InvalidDraft, "候选草稿或策略注册表为空。");
            }

            string reason;
            if (!TryValidateDraft(draft, out reason))
            {
                return Failure(InvalidDraft, reason);
            }

            var current = new List<string>(draft.CurrentIds != null && draft.CurrentIds.Count > 0
                ? draft.CurrentIds
                : draft.DefaultCandidateIds);
            SortCandidates(current, draft.CandidateKind, registry);
            var record = new CandidateResolutionRecord
            {
                CandidateSetId = draft.CandidateSetId,
                Version = draft.Version,
                StateRevision = stateRevision,
                CandidateKind = draft.CandidateKind,
                SourceEffectId = draft.SourceEffectId ?? string.Empty,
                ExecutingPlayerId = draft.ExecutingPlayerId,
                CandidatePoolIds = new List<string>(draft.CandidatePoolIds),
                DefaultCandidateIds = new List<string>(draft.DefaultCandidateIds),
                AppliedPatches = new List<AppliedCandidatePatch>(),
                FinalCandidateIds = new List<string>()
            };

            IList<CandidatePolicyRegistration> policies = registry.GetMatching(draft.CandidateKind);
            for (int i = 0; i < policies.Count; i++)
            {
                CandidatePolicyRegistration policy = policies[i];
                IList<CandidatePatch> patches;
                try
                {
                    patches = policy.PatchProvider(new CandidatePolicyContext(draft.Clone(), policy, state));
                }
                catch (Exception exception)
                {
                    return Failure(PatchFault, "候选策略执行失败：" + policy.SubscriptionId + "；" + exception.Message);
                }

                if (patches == null)
                {
                    return Failure(PatchFault, "候选策略返回 null：" + policy.SubscriptionId);
                }

                for (int j = 0; j < patches.Count; j++)
                {
                    CandidatePatch patch = patches[j];
                    string patchReason;
                    if (!TryApplyPatch(current, draft.CandidatePoolIds, patch, out patchReason))
                    {
                        return Failure(InvalidTarget, policy.SubscriptionId + "：" + patchReason);
                    }

                    SortCandidates(current, draft.CandidateKind, registry);
                    record.AppliedPatches.Add(new AppliedCandidatePatch
                    {
                        SubscriptionId = policy.SubscriptionId,
                        SourceContentInstanceId = policy.SourceContentInstanceId,
                        SourceAbilityId = policy.SourceAbilityId,
                        ApplySequence = record.AppliedPatches.Count,
                        Operation = patch.Operation,
                        CandidateIds = new List<string>(patch.CandidateIds),
                        ReasonCode = patch.ReasonCode ?? string.Empty,
                        ResultingCandidateIds = new List<string>(current)
                    });
                }
            }

            record.FinalCandidateIds = new List<string>(current);
            return new CandidateResolutionResult(true, record, string.Empty, string.Empty);
        }

        public bool TryResolve(
            CandidateSetDraft draft,
            CandidatePolicyRegistry registry,
            out CandidateResolutionRecord record,
            out string faultCode,
            out string diagnostic,
            int stateRevision = 0)
        {
            CandidateResolutionResult result = Resolve(draft, registry, stateRevision);
            record = result.Record;
            faultCode = result.FaultCode;
            diagnostic = result.Diagnostic;
            return result.Succeeded;
        }

        public bool TryResolveAndCommit(
            GameState state,
            CandidateSetDraft draft,
            CandidatePolicyRegistry registry,
            out CandidateResolutionRecord record,
            out string faultCode,
            out string diagnostic)
        {
            record = null;
            faultCode = string.Empty;
            diagnostic = string.Empty;
            if (state == null || state.EffectRuntime == null)
            {
                faultCode = InvalidDraft;
                diagnostic = "候选解析缺少权威运行状态。";
                return false;
            }

            CandidateResolutionResult result = Resolve(
                state,
                draft,
                registry,
                state.EffectRuntime.StateRevision);
            if (!result.Succeeded)
            {
                faultCode = result.FaultCode;
                diagnostic = result.Diagnostic;
                return false;
            }

            for (int i = 0; i < state.EffectRuntime.CandidateResolutions.Count; i++)
            {
                CandidateResolutionRecord existing = state.EffectRuntime.CandidateResolutions[i];
                if (existing == null || existing.CandidateSetId != draft.CandidateSetId ||
                    existing.Version != draft.Version) continue;
                record = existing.Clone();
                return true;
            }

            try
            {
                GameState work = GameStateCloneService.DeepClone(state);
                CandidateResolutionRecord committedRecord = result.Record.Clone();
                committedRecord.StateRevision = work.EffectRuntime.StateRevision + 1;
                work.EffectRuntime.CandidateResolutions.Add(committedRecord);
                RuleCommit.Apply(
                    work,
                    "candidate.resolve:" + committedRecord.CandidateSetId,
                    new RuleJournalEntry
                    {
                        Kind = RuleJournalEntryKind.CandidateResolution,
                        EntityId = committedRecord.CandidateSetId,
                        Detail = "candidate_set_resolved"
                    });
                string reason;
                if (!work.EffectRuntime.TryValidate(out reason))
                {
                    faultCode = "invalid_runtime_state";
                    diagnostic = reason;
                    return false;
                }

                GameStateCloneService.CopyTo(state, work);
                record = committedRecord.Clone();
                return true;
            }
            catch (Exception exception)
            {
                faultCode = "candidate_commit_failed";
                diagnostic = exception.Message;
                return false;
            }
        }

        private static bool TryValidateDraft(CandidateSetDraft draft, out string reason)
        {
            if (string.IsNullOrEmpty(draft.CandidateSetId) || draft.Version < 0 ||
                string.IsNullOrEmpty(draft.CandidateKind) || draft.CandidatePoolIds == null ||
                draft.DefaultCandidateIds == null || draft.CurrentIds == null ||
                draft.Context == null || !draft.Context.IsValid())
            {
                reason = "候选草稿缺少稳定字段或 context 非法。";
                return false;
            }

            if (!HasUniqueIds(draft.CandidatePoolIds) || !HasUniqueIds(draft.DefaultCandidateIds) ||
                !HasUniqueIds(draft.CurrentIds))
            {
                reason = "候选草稿包含重复或空候选 ID。";
                return false;
            }

            for (int i = 0; i < draft.DefaultCandidateIds.Count; i++)
            {
                if (!draft.CandidatePoolIds.Contains(draft.DefaultCandidateIds[i]))
                {
                    reason = "default candidates 突破 hard constraint 候选池。";
                    return false;
                }
            }

            for (int i = 0; i < draft.CurrentIds.Count; i++)
            {
                if (!draft.CandidatePoolIds.Contains(draft.CurrentIds[i]))
                {
                    reason = "current candidates 突破 hard constraint 候选池。";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        private static bool TryApplyPatch(
            List<string> current,
            IList<string> pool,
            CandidatePatch patch,
            out string reason)
        {
            if (patch == null || patch.CandidateIds == null || !HasUniqueIds(patch.CandidateIds))
            {
                reason = "patch 为空、包含空 ID 或重复 ID。";
                return false;
            }

            for (int i = 0; i < patch.CandidateIds.Count; i++)
            {
                if (!pool.Contains(patch.CandidateIds[i]))
                {
                    reason = "patch target 不存在于 hard constraint 候选池：" + patch.CandidateIds[i];
                    return false;
                }
            }

            switch (patch.Operation)
            {
                case CandidatePatchOperation.Add:
                    for (int i = 0; i < patch.CandidateIds.Count; i++)
                    {
                        if (!current.Contains(patch.CandidateIds[i])) current.Add(patch.CandidateIds[i]);
                    }
                    break;
                case CandidatePatchOperation.Remove:
                    for (int i = 0; i < patch.CandidateIds.Count; i++) current.Remove(patch.CandidateIds[i]);
                    break;
                case CandidatePatchOperation.Intersect:
                    for (int i = current.Count - 1; i >= 0; i--)
                    {
                        if (!patch.CandidateIds.Contains(current[i])) current.RemoveAt(i);
                    }
                    break;
                default:
                    reason = "未知 patch operation。";
                    return false;
            }

            reason = string.Empty;
            return true;
        }

        private static void SortCandidates(List<string> candidates, string candidateKind, CandidatePolicyRegistry registry)
        {
            candidates.Sort((left, right) =>
            {
                int comparison = StringComparer.Ordinal.Compare(
                    registry.GetSortKey(candidateKind, left),
                    registry.GetSortKey(candidateKind, right));
                return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
            });
        }

        private static bool HasUniqueIds(IList<string> ids)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (string.IsNullOrEmpty(ids[i])) return false;
                for (int j = 0; j < i; j++)
                {
                    if (string.Equals(ids[i], ids[j], StringComparison.Ordinal)) return false;
                }
            }

            return true;
        }

        private static CandidateResolutionResult Failure(string code, string diagnostic)
        {
            return new CandidateResolutionResult(false, null, code, diagnostic);
        }
    }
}
