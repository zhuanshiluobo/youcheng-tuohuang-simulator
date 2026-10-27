using YC.Domain.Rules;
using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static class ResourcePaymentPlanService
    {
        public static bool TryBuild(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner, out PaymentCommitPlan plan, out string reason)
        {
            plan = null; reason = "payment_plan_invalid"; var p = owner?.Payment;
            if (p?.PaymentProtocolVersion != 2 || p.SelectedSubstitution != null || p.IsCommitted || p.PaymentId != owner.EffectId || p.OwnerEffectId != owner.EffectId || p.PlayerId != owner.PlayerId || p.Preparation?.IsCancelled == true || p.Phase == "cancelled" ||
                owner.Status == EffectNodeStatus.Failed || owner.Status == EffectNodeStatus.Faulted || owner.Status == EffectNodeStatus.Completed || owner.PendingOutcome == EffectPendingOutcome.Failed ||
                !p.BeforePaymentReady || p.SourceDrafts == null || p.SourceEdits == null || p.SourceDrafts.Any(d => d == null || d.Status == "reselect_required") || p.SourceEdits.Any(e => e == null || e.Status == "open") ||
                !ResourcePaymentAllocationService.TryValidate(p.Demand, p.SelectedAllocation, out var gross, out reason) || !PaymentResources.Same(gross, p.PaymentCost)) return false;
            if (p.Preparation != null && (!ResourcePaymentPreparationService.TryQuotePreparation(state, owner, out var quote, out reason,registry) || !PaymentResources.Same(quote, gross))) return false;
            IReadOnlyList<PaymentSourceRule> rules = p.SourceDrafts.Any(d => d.Status != "withdrawn") ?
                ResourcePaymentSourceService.Query(state, registry, owner, gross) : Array.Empty<PaymentSourceRule>();
            var result = new PaymentCommitPlan { PaymentId = p.PaymentId, Generation = p.PaymentGeneration, BuildIdentity = ResourcePaymentSourceService.BuildIdentity(state, owner), FinalCost = gross.Clone() };
            var ids = new HashSet<string>(StringComparer.Ordinal); var keys = new HashSet<string>(StringComparer.Ordinal);
            var totals = new ResourceSet(); var sourceCost = new ResourceSet();
            foreach (var d in p.SourceDrafts.Where(d => d.Status != "withdrawn"))
            {
                if (!p.AllowsSubstitution || d.Status != "valid" || d.PaymentId != p.PaymentId || d.PaymentGeneration != p.PaymentGeneration || !keys.Add(ResourcePaymentSourceService.Key(d.Rule)) ||
                    !PaymentResources.Valid(d.Allocation) || d.SelectedItemIds == null) { reason = "payment_source_draft_invalid"; return false; }
                var rule = rules.SingleOrDefault(r => ResourcePaymentSourceService.Key(r) == ResourcePaymentSourceService.Key(d.Rule));
                if (rule == null || ResourcePaymentSourceService.Fingerprint(rule) != ResourcePaymentSourceService.Fingerprint(d.Rule)) { reason = "payment_substitution_source_changed"; return false; }
                long units = PaymentResources.Types.Sum(t => (long)d.Allocation.Get(t));
                if (units < 1 || units > rule.MaxUnits || PaymentResources.Types.Any(t => d.Allocation.Get(t) != 0 && !rule.AllowedResourceTypes.Contains(t)) || d.BuildIdentity != result.BuildIdentity) { reason = "payment_source_allocation_invalid"; return false; }
                try { totals = PaymentResources.Add(totals, d.Allocation); sourceCost = PaymentResources.Add(sourceCost, PaymentResources.Scale(rule.UnitResourceCost, (int)units)); }
                catch (OverflowException) { reason = "payment_cost_overflow"; return false; }
                if (rule.ItemCost != null)
                {
                    if (!ResourcePaymentSourceService.TryPlanItemCost(state, registry, owner, rule, (int)units, d.SelectedItemIds, out var token, out reason)) return false;
                    result.TokenPlans.Add(token);
                }
                else if (d.SelectedItemIds.Count != 0) { reason = "payment_source_items_invalid"; return false; }
                foreach (string id in d.SelectedItemIds) if (!ids.Add(id)) { reason = "payment_source_item_reused"; return false; }
                result.Drafts.Add(GameStateCloneService.DeepClone(d));
            }
            foreach (var group in result.Drafts.GroupBy(d => d.Rule.QuotaScope + "|" + d.Rule.QuotaGroupId))
            {
                long units = group.Sum(d => (long)PaymentResources.Total(d.Allocation));
                if (group.Any(d => units > d.Rule.QuotaLimit || d.Rule.QuotaScope == "build" && result.BuildIdentity.Length == 0)) { reason = "payment_source_quota_exceeded"; return false; }
                if (group.First().Rule.QuotaScope == "build")
                {
                    long paid = state.EffectRuntime.EffectNodes.Where(n => n.EffectId != owner.EffectId && n.Payment?.IsCommitted == true)
                        .SelectMany(n => n.Payment.SourceReceipts ?? new List<PaymentSourceReceipt>()).Where(r => r.BuildIdentity == result.BuildIdentity && r.Rule.QuotaGroupId == group.First().Rule.QuotaGroupId)
                        .Sum(r => (long)PaymentResources.Total(r.Allocation));
                    if (group.Any(d => units + paid > d.Rule.QuotaLimit)) { reason = "payment_source_quota_exceeded"; return false; }
                }
            }
            try
            {
                foreach (var t in PaymentResources.Types)
                { if (totals.Get(t) > gross.Get(t)) { reason = "payment_substitution_overlap"; return false; } result.FinalCost.Set(t, checked(gross.Get(t) - totals.Get(t) + sourceCost.Get(t))); }
            }
            catch (OverflowException) { reason = "payment_cost_overflow"; return false; }
            if (state.FindPlayer(p.PlayerId)?.Resources.CanPay(result.FinalCost) != true) { reason = "insufficient_resource"; return false; }
            if (result.TokenPlans.Count == 0) { plan = result; reason = ""; return true; }
            // 所有物品费用在同一副本顺序预演，包含容量及载体政策，失败不提交前半笔。
            var preview = GameStateCloneService.DeepClone(state); var node = preview.EffectRuntime.EffectNodes.Single(n => n.EffectId == owner.EffectId);
            node.Payment.IsCommitRequested = true; // 只在预演副本进入消费合同，不更改真实请求。
            try { new ItemTransferService().RelocateTokenBatches(new EffectExecutionContext(preview, node, registry, EffectRuntimeLimits.Default), result.TokenPlans.Select(t => t.Request).ToList()); }
            catch (InvalidOperationException invalid) { reason = invalid.Message; return false; }
            plan = result; reason = ""; return true;
        }
        public static EffectStepResult Commit(EffectExecutionContext c, NormalizedValue result)
        {
            var p = c.Node.Payment;
            if (p?.IsCommitted == true) return EffectStepResult.Completed(c.Node.NormalizedResult?.Clone() ?? NormalizedValue.CreateNull()).WithFlowStage("payment_finished");
            if (!TryBuild(c.State, c.Registry, c.Node, out var plan, out var reason))
            {
                if (reason == "insufficient_resource" && ResourcePaymentPreparationService.TryRetryInsufficientConfirmation(c, out var retry)) return retry;
                return EffectStepResult.Failed(reason);
            }
            var zones = plan.TokenPlans.SelectMany(t => t.Request.Transfers).Select(t => t.ExpectedSource.ZoneId).Distinct().ToList();
            var ids = plan.TokenPlans.SelectMany(t => t.Request.Transfers).SelectMany(t => t.ItemIds).ToList();
            if (ids.Count > 0 && CharacterLifecycleService.TryAwaitDeferredDependencies(c, new DeferredDependencyQuery { DependencyKind = DeferredDependencyKind.ExactItemUse, ItemIds = ids }))
                return CharacterLifecycleService.DeferredDependencyWait();
            if (CharacterLifecycleService.TryAwaitEmptyCarrierExits(c, zones, ids)) return CharacterLifecycleService.DeferredDependencyWait();
            Apply(c, plan); return EffectStepResult.Completed(result).WithFlowStage("payment_finished");
        }
        public static void Apply(EffectExecutionContext c, PaymentCommitPlan plan)
        {
            if (c.Node.Payment?.PaymentId != plan.PaymentId || c.Node.Payment.PaymentGeneration != plan.Generation || !c.Node.Payment.IsCommitRequested || c.Node.Payment.IsCommitted) throw new InvalidOperationException("payment_commit_plan_stale");
            if (!TryBuild(c.State, c.Registry, c.Node, out var validated, out var reason) || !PaymentResources.Same(validated.FinalCost, plan.FinalCost)) throw new InvalidOperationException(reason.Length > 0 ? reason : "payment_commit_plan_changed");
            plan = validated;
            var work = GameStateCloneService.DeepClone(c.State);
            var workContext = new EffectExecutionContext(work, work.EffectRuntime.EffectNodes.Single(n => n.EffectId == c.Node.EffectId), c.Registry, EffectRuntimeLimits.Default);
            var transfers = new ItemTransferService().RelocateTokenBatches(workContext, plan.TokenPlans.Select(t => t.Request).ToList());
            var p = workContext.Node.Payment;
            if (!work.FindPlayer(p.PlayerId).Resources.TryPay(plan.FinalCost)) throw new InvalidOperationException("payment_balance_changed_during_commit");
            foreach (var d in plan.Drafts)
            {
                var receipt = new PaymentSourceReceipt { PaymentId = plan.PaymentId, PaymentGeneration = plan.Generation, BuildIdentity = plan.BuildIdentity, Rule = d.Rule, Allocation = d.Allocation.Clone(),
                    ActualResourceCost = PaymentResources.Scale(d.Rule.UnitResourceCost, PaymentResources.Total(d.Allocation)), ItemIds = d.SelectedItemIds.ToList(),
                    ReceiptId = StableIdFactory.Create("payment-source-receipt", plan.PaymentId, ResourcePaymentSourceService.Key(d.Rule)) };
                var token = plan.TokenPlans.SingleOrDefault(t => t.RuleKey == ResourcePaymentSourceService.Key(d.Rule));
                if (token != null)
                {
                    receipt.TokenArguments = token.Arguments.Clone();
                    receipt.TokenTransfer = GameStateCloneService.DeepClone(transfers.Single(t => t.Request.OperationId == token.Request.OperationId));
                    receipt.OriginalSupplyZoneIds = receipt.ItemIds.Select(id => work.ItemZones.Items.Single(i => i.ItemId == id).OriginalSupplyZoneId).ToList();
                    receipt.PositionChanges = GameStateCloneService.DeepClone(receipt.TokenTransfer.Result.PositionChanges);
                }
                p.SourceReceipts.Add(receipt);
            }
            p.FinalCost = plan.FinalCost.Clone(); p.IsCommitted = true; p.IsCommitRequested = false; p.Phase = "committed";
            p.FinalReceipt = new PaymentFinalReceipt { ReceiptId = StableIdFactory.Create("payment-receipt", plan.PaymentId), ContentHash = c.Registry.ContentPackHash, DefinitionVersion = c.Node.DefinitionVersion,
                CostReceiptPolicyVersion = ResourcePaymentSourceService.TokenCostContractVersion,
                Demand = GameStateCloneService.DeepClone(p.Demand), SelectedAllocation = p.SelectedAllocation.Clone(), GrossCost = p.PaymentCost.Clone(), FinalCost = plan.FinalCost.Clone(), SourceReceiptIds = p.SourceReceipts.Select(r => r.ReceiptId).ToList() };
            CharacterLifecycleService.ExitEmptyCarriers(workContext, plan.TokenPlans.SelectMany(t => t.Request.Transfers).Select(t => t.ExpectedSource.ZoneId));
            GameStateCloneService.CopyTo(c.State, work);
        }
    }
}
