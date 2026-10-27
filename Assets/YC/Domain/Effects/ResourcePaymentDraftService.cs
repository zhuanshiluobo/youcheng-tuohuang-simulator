using YC.Domain.Rules;
using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static class ResourcePaymentDraftService
    {
        public static bool Editable(ResourcePaymentRuntimeState p) => p?.PaymentProtocolVersion == 2 && !p.IsCommitted && !p.IsCommitRequested && p.Preparation?.IsCancelled != true;
        public static bool TryBeginEdit(GameState work, EffectRegistry registry, EffectNodeRuntimeState owner, string sourceKey, string ruleId,
            int generation, out PaymentSourceEdit edit, out string reason)
        {
            edit = null; reason = "payment_edit_invalid"; var p = owner?.Payment;
            if (!Editable(p) || p.PaymentGeneration != generation || p.SourceEdits.Any(e => e.Status == "open")) return false;
            var rule = ResourcePaymentSourceService.Query(work, registry, owner, p.PaymentCost).SingleOrDefault(r => r.SourceRef.SourceKey == sourceKey && r.RuleId == ruleId);
            if (rule == null) { reason = "payment_substitution_source_changed"; return false; }
            var previous = p.SourceDrafts.SingleOrDefault(d => ResourcePaymentSourceService.Key(d.Rule) == ResourcePaymentSourceService.Key(rule));
            edit = new PaymentSourceEdit { SourceKey = sourceKey, RuleId = ruleId, PaymentGeneration = generation, PreviousDraft = previous == null ? null : GameStateCloneService.DeepClone(previous),
                EnteredDraftRevision = previous?.DraftRevision ?? 0, EditId = StableIdFactory.Create("payment-edit", owner.EffectId, generation.ToString(System.Globalization.CultureInfo.InvariantCulture), sourceKey, ruleId, p.SourceEdits.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            p.SourceEdits.Add(edit); reason = ""; return true;
        }
        public static bool TryCancelEdit(EffectNodeRuntimeState owner, string editId, out string reason)
        {
            reason = "payment_edit_stale"; var p = owner?.Payment; var edit = p?.SourceEdits.SingleOrDefault(e => e.EditId == editId && e.Status == "open");
            if (!Editable(p) || edit == null || edit.PaymentGeneration != p.PaymentGeneration) return false;
            // 编辑中的输入只在最终保存时替换草稿；取消不会删原草稿，更不会删其他源。
            edit.Status = "cancelled"; reason = ""; return true;
        }
        public static bool TrySetSourceDraft(GameState work, EffectRegistry registry, EffectNodeRuntimeState owner, string sourceKey, string ruleId,
            int generation, int expectedDraftRevision, ResourceSet allocation, IReadOnlyList<string> itemIds, string editId, out string reason)
        {
            reason = "payment_draft_stale"; var p = owner?.Payment;
            if (!Editable(p) || p.PaymentGeneration != generation || !PaymentResources.Valid(allocation) || itemIds == null) return false;
            var edit = p.SourceEdits.SingleOrDefault(e => e.Status == "open");
            if (edit != null && (edit.EditId != editId || edit.SourceKey != sourceKey || edit.RuleId != ruleId || edit.PaymentGeneration != generation)) return false;
            var rules = ResourcePaymentSourceService.Query(work, registry, owner, p.PaymentCost);
            var rule = rules.SingleOrDefault(r => r.SourceRef.SourceKey == sourceKey && r.RuleId == ruleId);
            if (rule == null) { reason = "payment_substitution_source_changed"; return false; }
            var previous = p.SourceDrafts.SingleOrDefault(d => ResourcePaymentSourceService.Key(d.Rule) == ResourcePaymentSourceService.Key(rule));
            if ((previous?.DraftRevision ?? 0) != expectedDraftRevision) return false;
            if (expectedDraftRevision == int.MaxValue) { reason = "payment_draft_revision_exhausted"; return false; }
            long total = PaymentResources.Types.Sum(t => (long)allocation.Get(t));
            if (total < 0 || total > rule.MaxUnits || PaymentResources.Types.Any(t => allocation.Get(t) != 0 && !rule.AllowedResourceTypes.Contains(t))) { reason = "payment_substitution_allocation_invalid"; return false; }
            foreach (var t in PaymentResources.Types)
                if ((long)allocation.Get(t) + p.SourceDrafts.Where(d => d != previous && d.Status == "valid").Sum(d => (long)d.Allocation.Get(t)) > p.PaymentCost.Get(t))
                { reason = "payment_substitution_overlap"; return false; }
            int units = (int)total;
            try
            {
                var surcharge = PaymentResources.Scale(rule.UnitResourceCost, units);
                foreach (var other in p.SourceDrafts.Where(d => d != previous && d.Status == "valid")) surcharge = PaymentResources.Add(surcharge, PaymentResources.Scale(other.Rule.UnitResourceCost, PaymentResources.Total(other.Allocation)));
                foreach (var t in PaymentResources.Types)
                {
                    long substituted = allocation.Get(t) + p.SourceDrafts.Where(d => d != previous && d.Status == "valid").Sum(d => (long)d.Allocation.Get(t));
                    _ = checked((int)(p.PaymentCost.Get(t) - substituted + surcharge.Get(t)));
                }
            }
            catch (OverflowException) { reason = "payment_cost_overflow"; return false; }
            string build = ResourcePaymentSourceService.BuildIdentity(work, owner);
            long used = p.SourceDrafts.Where(d => d != previous && d.Status == "valid" && d.Rule.QuotaGroupId == rule.QuotaGroupId && d.Rule.QuotaScope == rule.QuotaScope).Sum(d => (long)PaymentResources.Total(d.Allocation));
            if (used + units > rule.QuotaLimit || rule.QuotaScope == "build" && build.Length == 0) { reason = "payment_source_quota_exceeded"; return false; }
            PaymentTokenCostPlan token = null;
            if (rule.ItemCost != null && units > 0 && itemIds.Count == 0)
            {
                if (!ResourcePaymentSourceService.TrySelectItems(work, registry, owner, rule, units,
                    p.SourceDrafts.Where(d => d != previous && d.Status == "valid").SelectMany(d => d.SelectedItemIds), out itemIds, out reason)) return false;
            }
            if (rule.ItemCost == null ? itemIds.Count != 0 : units > 0 && !ResourcePaymentSourceService.TryPlanItemCost(work, registry, owner, rule, units, itemIds, out token, out reason)) return false;
            if (units == 0 && itemIds.Count != 0) { reason = "payment_source_items_invalid"; return false; }
            if (p.SourceDrafts.Where(d => d != previous && d.Status == "valid").SelectMany(d => d.SelectedItemIds).Intersect(itemIds).Any()) { reason = "payment_source_item_reused"; return false; }
            var next = new PaymentSourceDraft { PaymentId = p.PaymentId, PaymentGeneration = generation, BuildIdentity = build, Rule = GameStateCloneService.DeepClone(rule), Allocation = allocation.Clone(),
                SelectedItemIds = itemIds.ToList(), DraftRevision = checked(expectedDraftRevision + 1), Status = units == 0 ? "withdrawn" : "valid" };
            if (previous != null) p.SourceDrafts.Remove(previous); p.SourceDrafts.Add(next);
            if (edit != null) edit.Status = "saved";
            p.Phase = "confirmation"; reason = ""; return true;
        }
        public static bool TryWithdrawSource(EffectNodeRuntimeState owner, string sourceKey, string ruleId, int generation, int revision, out string reason)
        {
            reason = "payment_draft_stale"; var p = owner?.Payment;
            if (!Editable(p) || p.PaymentGeneration != generation || p.SourceEdits.Any(e => e.Status == "open")) return false;
            var d = p.SourceDrafts.SingleOrDefault(v => v.Rule?.SourceRef?.SourceKey == sourceKey && v.Rule.RuleId == ruleId);
            if (d == null || d.DraftRevision != revision) return false;
            if (d.DraftRevision == int.MaxValue) { reason = "payment_draft_revision_exhausted"; return false; }
            d.Status = "withdrawn"; d.Allocation = new ResourceSet(); d.SelectedItemIds.Clear(); d.DraftRevision = checked(d.DraftRevision + 1); d.InvalidReason = ""; reason = ""; return true;
        }
        public static bool TryChangeAllocation(GameState work, EffectRegistry registry, EffectNodeRuntimeState owner, ResourceSet allocation, int generation, out string reason)
        {
            reason = "payment_draft_stale"; var p = owner?.Payment;
            if (!Editable(p) || p.PaymentGeneration != generation || p.SourceEdits.Any(e => e.Status == "open")) return false;
            if (!ResourcePaymentAllocationService.TryValidate(p.Demand, allocation, out var gross, out reason)) return false;
            if (PaymentResources.Same(p.SelectedAllocation, allocation)) { reason = ""; return true; }
            return Reprice(work, registry, owner, p.Demand, allocation, gross, out reason);
        }
        public static bool TryChangeFixedQuote(GameState work, EffectRegistry registry, EffectNodeRuntimeState owner, ResourceSet gross, int generation, out string reason)
        {
            reason = "payment_draft_stale"; var p = owner?.Payment;
            if (!Editable(p) || p.PaymentGeneration != generation || p.Demand?.Mode != "fixed" || !PaymentResources.Valid(gross) || p.SourceEdits.Any(e => e.Status == "open")) return false;
            if (PaymentResources.Same(p.PaymentCost, gross)) { reason = ""; return true; }
            return Reprice(work, registry, owner, new ResourcePaymentDemandState { FixedCost = gross.Clone() }, new ResourceSet(), gross, out reason);
        }
        private static bool Reprice(GameState work, EffectRegistry registry, EffectNodeRuntimeState owner, ResourcePaymentDemandState demand, ResourceSet allocation, ResourceSet gross, out string reason)
        {
            var p = owner.Payment;
            if (p.PaymentGeneration == int.MaxValue || p.SourceDrafts.Any(d => d.DraftRevision == int.MaxValue)) { reason = "payment_generation_exhausted"; return false; }
            var rules = ResourcePaymentSourceService.Query(work, registry, owner, gross);
            p.Demand = GameStateCloneService.DeepClone(demand);
            p.PaymentGeneration = checked(p.PaymentGeneration + 1); p.SelectedAllocation = allocation.Clone(); p.PaymentCost = gross; p.GrossCost = gross.Clone(); p.FinalCost = new ResourceSet();
            // 已受理的before响应历史不清空、不重播。新报价只做纯资格校验。
            foreach (var draft in p.SourceDrafts)
            {
                // 墓碑仍是当前付款的来源槽位：跟随代数/版本推进，但不复活或抹掉撤回事实。
                draft.PaymentGeneration = p.PaymentGeneration; draft.DraftRevision = checked(draft.DraftRevision + 1);
                if (draft.Status == "withdrawn") continue;
                var current = rules.SingleOrDefault(r => ResourcePaymentSourceService.Key(r) == ResourcePaymentSourceService.Key(draft.Rule));
                bool legal = current != null && ResourcePaymentSourceService.Fingerprint(current) == ResourcePaymentSourceService.Fingerprint(draft.Rule) &&
                    PaymentResources.Types.All(t => draft.Allocation.Get(t) <= gross.Get(t)) &&
                    PaymentResources.Types.All(t => p.SourceDrafts.Where(d => d.Status != "withdrawn").Sum(d => (long)d.Allocation.Get(t)) <= gross.Get(t));
                if (legal && draft.Rule.ItemCost != null)
                    legal = ResourcePaymentSourceService.TryPlanItemCost(work, registry, owner, draft.Rule, PaymentResources.Total(draft.Allocation), draft.SelectedItemIds, out _, out _);
                // 新代数必须由玩家明确重选；仍放得下的旧配比也不能静默变成新代数确认。
                draft.Status = "reselect_required";
                draft.InvalidReason = "payment_generation_reselect_required";
            }
            if (owner.SettlementWindow?.Context != null) owner.SettlementWindow.Context.PaymentCost = gross.Clone();
            reason = ""; return true;
        }
    }
}
