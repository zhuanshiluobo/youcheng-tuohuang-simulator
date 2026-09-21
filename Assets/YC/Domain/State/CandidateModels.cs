using System;
using System.Collections.Generic;

namespace YC.Domain.State
{
    public enum CandidatePatchOperation
    {
        Add,
        Remove,
        Intersect
    }

    [Serializable]
    public sealed class CandidateSetDraft
    {
        public string CandidateSetId = string.Empty;
        public int Version;
        public string CandidateKind = string.Empty;
        public string SourceEffectId = string.Empty;
        public int ExecutingPlayerId = -1;
        public List<string> CandidatePoolIds = new List<string>();
        public List<string> DefaultCandidateIds = new List<string>();
        public List<string> CurrentIds = new List<string>();
        public NormalizedValue Context = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());

        public CandidateSetDraft Clone()
        {
            return new CandidateSetDraft
            {
                CandidateSetId = CandidateSetId,
                Version = Version,
                CandidateKind = CandidateKind,
                SourceEffectId = SourceEffectId,
                ExecutingPlayerId = ExecutingPlayerId,
                CandidatePoolIds = CandidatePoolIds == null ? null : new List<string>(CandidatePoolIds),
                DefaultCandidateIds = DefaultCandidateIds == null ? null : new List<string>(DefaultCandidateIds),
                CurrentIds = CurrentIds == null ? null : new List<string>(CurrentIds),
                Context = Context == null ? null : Context.Clone()
            };
        }
    }

    [Serializable]
    public sealed class CandidatePatch
    {
        public CandidatePatchOperation Operation = CandidatePatchOperation.Add;
        public List<string> CandidateIds = new List<string>();
        public string ReasonCode = string.Empty;

        public CandidatePatch Clone()
        {
            return new CandidatePatch
            {
                Operation = Operation,
                CandidateIds = CandidateIds == null ? null : new List<string>(CandidateIds),
                ReasonCode = ReasonCode
            };
        }
    }

    [Serializable]
    public sealed class AppliedCandidatePatch
    {
        public string SubscriptionId = string.Empty;
        public string SourceContentInstanceId = string.Empty;
        public string SourceAbilityId = string.Empty;
        public int ApplySequence;
        public CandidatePatchOperation Operation = CandidatePatchOperation.Add;
        public List<string> CandidateIds = new List<string>();
        public string ReasonCode = string.Empty;
        public List<string> ResultingCandidateIds = new List<string>();

        public AppliedCandidatePatch Clone()
        {
            return new AppliedCandidatePatch
            {
                SubscriptionId = SubscriptionId,
                SourceContentInstanceId = SourceContentInstanceId,
                SourceAbilityId = SourceAbilityId,
                ApplySequence = ApplySequence,
                Operation = Operation,
                CandidateIds = CandidateIds == null ? null : new List<string>(CandidateIds),
                ReasonCode = ReasonCode,
                ResultingCandidateIds = ResultingCandidateIds == null
                    ? null
                    : new List<string>(ResultingCandidateIds)
            };
        }
    }

    [Serializable]
    public sealed class CandidateResolutionRecord
    {
        public string CandidateSetId = string.Empty;
        public int Version;
        public int StateRevision;
        public string CandidateKind = string.Empty;
        public string SourceEffectId = string.Empty;
        public int ExecutingPlayerId = -1;
        public List<string> CandidatePoolIds = new List<string>();
        public List<string> DefaultCandidateIds = new List<string>();
        public List<AppliedCandidatePatch> AppliedPatches = new List<AppliedCandidatePatch>();
        public List<string> FinalCandidateIds = new List<string>();

        public CandidateResolutionRecord Clone()
        {
            var clone = new CandidateResolutionRecord
            {
                CandidateSetId = CandidateSetId,
                Version = Version,
                StateRevision = StateRevision,
                CandidateKind = CandidateKind,
                SourceEffectId = SourceEffectId,
                ExecutingPlayerId = ExecutingPlayerId,
                CandidatePoolIds = CandidatePoolIds == null ? null : new List<string>(CandidatePoolIds),
                DefaultCandidateIds = DefaultCandidateIds == null ? null : new List<string>(DefaultCandidateIds),
                AppliedPatches = AppliedPatches == null ? null : new List<AppliedCandidatePatch>(),
                FinalCandidateIds = FinalCandidateIds == null ? null : new List<string>(FinalCandidateIds)
            };

            if (clone.AppliedPatches != null)
            {
                for (int i = 0; i < AppliedPatches.Count; i++)
                {
                    clone.AppliedPatches.Add(AppliedPatches[i] == null ? null : AppliedPatches[i].Clone());
                }
            }

            return clone;
        }

        public bool TryValidate(out string reason)
        {
            if (string.IsNullOrEmpty(CandidateSetId) || Version < 0 || StateRevision < 0 ||
                string.IsNullOrEmpty(CandidateKind) || CandidatePoolIds == null ||
                DefaultCandidateIds == null || AppliedPatches == null || FinalCandidateIds == null)
            {
                reason = "候选解析记录缺少稳定字段。";
                return false;
            }

            if (!HasUniqueIds(CandidatePoolIds) || !HasUniqueIds(DefaultCandidateIds) ||
                !HasUniqueIds(FinalCandidateIds))
            {
                reason = "候选解析记录包含重复候选 ID。";
                return false;
            }

            for (int i = 0; i < DefaultCandidateIds.Count; i++)
            {
                if (!CandidatePoolIds.Contains(DefaultCandidateIds[i]))
                {
                    reason = "基础候选超出 hard constraint 候选池。";
                    return false;
                }
            }

            for (int i = 0; i < FinalCandidateIds.Count; i++)
            {
                if (!CandidatePoolIds.Contains(FinalCandidateIds[i]))
                {
                    reason = "最终候选超出 hard constraint 候选池。";
                    return false;
                }
            }

            for (int i = 0; i < AppliedPatches.Count; i++)
            {
                AppliedCandidatePatch patch = AppliedPatches[i];
                if (patch == null || patch.CandidateIds == null || patch.ResultingCandidateIds == null ||
                    !HasUniqueIds(patch.CandidateIds) || !HasUniqueIds(patch.ResultingCandidateIds))
                {
                    reason = "候选解析记录包含非法 patch。";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
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
    }
}
