using YC.Domain.Rules;
using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Presentation;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public static class ResourcePaymentDraftHost
    {
        public const string TotalStage = "awaiting_total_payment";
        public static NormalizedValue Projection(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner)
        {
            var p = owner.Payment; string candidate = PaymentResources.Candidate(p.PaymentCost);
            var values = ResourcePaymentPreparationService.Facts(state, owner, registry);
            void Put(string key, NormalizedValue value) { values.Properties.RemoveAll(e => e.Name == key); values.Properties.Add(new NormalizedValueEntry { Name = key, Value = value }); }
            if (p.Demand.Mode == "total")
            {
                Put("paymentOptions", NormalizedValue.CreateArray(new[] { Object("candidateId", NormalizedValue.CreateString(candidate), "cost", ResourcePaymentPreparationService.Cost(p.PaymentCost)) }));
                Put("paymentDraft", Object("candidateId", NormalizedValue.CreateString(candidate), "paymentId", NormalizedValue.CreateString(p.PaymentId),
                "windowId", NormalizedValue.CreateString(owner.SettlementWindow?.Context?.Id ?? p.PaymentId), "canChange", NormalizedValue.CreateBoolean(ResourcePaymentDraftService.Editable(p) && !p.SourceEdits.Any(e => e.Status == "open"))));
            }
            Put("paymentDemand", Object("mode", NormalizedValue.CreateString(p.Demand.Mode), "fixedCost", ResourcePaymentPreparationService.Cost(p.Demand.FixedCost),
                "allowedResourceTypes", Strings(p.Demand.AllowedResourceTypes.Select(t => t.ToString())), "totalCount", NormalizedValue.CreateInteger(p.Demand.TotalCount),
                "selectedAllocation", ResourcePaymentPreparationService.Cost(p.SelectedAllocation), "generation", NormalizedValue.CreateInteger(p.PaymentGeneration)));
            values.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
            if (!values.IsValid()) throw new InvalidOperationException("payment_projection_invalid");
            return values;
        }
        public const string SourceQueryVersion = "payment_sources.read.v1";
        public const int SourceQueryProtocolVersion = 1;
        public const int SourceQueryItemPageSize = 4;
        public static NormalizedValue SourceQuery(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner)
        {
            var view = QuerySources(state, registry, owner);
            return Object("protocolVersion", NormalizedValue.CreateInteger(SourceQueryProtocolVersion), "snapshotId", NormalizedValue.CreateString(view.SnapshotId));
        }
        public static PaymentSourceProjection QuerySources(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner)
        {
            var p = owner.Payment;
            var sources = ResourcePaymentSourceService.Query(state, registry, owner, p.PaymentCost);
            var rules = sources.Concat(p.SourceDrafts.Where(d => !sources.Any(r => ResourcePaymentSourceService.Key(r) == ResourcePaymentSourceService.Key(d.Rule))).Select(d => d.Rule))
                .OrderBy(ResourcePaymentSourceService.Key, StringComparer.Ordinal).ToList();
            var view = new PaymentSourceProjection { GameId = state.GameId, ContentPackHash = registry.ContentPackHash, PlayerId = owner.PlayerId,
                OwnerEffectId = owner.EffectId, PaymentId = p.PaymentId, StateRevision = state.EffectRuntime.StateRevision, PaymentGeneration = p.PaymentGeneration };
            var edit = p.SourceEdits.SingleOrDefault(e => e.Status == "open");
            if (edit != null) view.Edit = new PaymentSourceEditProjection { EditId = edit.EditId, SourceKey = edit.SourceKey, RuleId = edit.RuleId,
                PaymentGeneration = edit.PaymentGeneration, EnteredDraftRevision = edit.EnteredDraftRevision };
            foreach (var r in rules)
            {
                string key = ResourcePaymentSourceService.Key(r);
                var draft = p.SourceDrafts.SingleOrDefault(d => ResourcePaymentSourceService.Key(d.Rule) == key);
                bool eligible = sources.Any(s => ResourcePaymentSourceService.Key(s) == key && ResourcePaymentSourceService.Fingerprint(s) == ResourcePaymentSourceService.Fingerprint(r));
                bool valid = eligible && (draft == null || ResourcePaymentSourceService.Fingerprint(draft.Rule) == ResourcePaymentSourceService.Fingerprint(r));
                var other = p.SourceDrafts.Where(d => d.Status == "valid" && ResourcePaymentSourceService.Key(d.Rule) != key).ToList();
                int maximum = (int)Math.Min(r.MaxUnits, Math.Max(0L, (long)r.QuotaLimit - other.Where(d => d.Rule.QuotaGroupId == r.QuotaGroupId && d.Rule.QuotaScope == r.QuotaScope).Sum(d => (long)PaymentResources.Total(d.Allocation))));
                var row = new PaymentSourceRow { Rule = GameStateCloneService.DeepClone(r), Eligible = eligible, DraftRevision = draft?.DraftRevision ?? 0,
                    DisplayName = registry.AbilityPresentationNames.TryGetValue(r.SourceRef.AbilityId, out string name) ? name : r.SourceRef.DefinitionId,
                    Allocation = draft?.Allocation.Clone() ?? new ResourceSet(), SelectedItemIds = draft?.SelectedItemIds.ToList() ?? new List<string>() };
                foreach (var t in r.AllowedResourceTypes) row.RowMaximums.Set(t, (int)Math.Max(0L, (long)p.PaymentCost.Get(t) - other.Sum(d => (long)d.Allocation.Get(t))));
                maximum = (int)Math.Min(maximum, PaymentResources.Types.Sum(t => (long)row.RowMaximums.Get(t)));
                foreach (var t in PaymentResources.Types.Where(t => r.UnitResourceCost.Get(t) > 0))
                {
                    long fees = other.Sum(d => (long)d.Rule.UnitResourceCost.Get(t) * PaymentResources.Total(d.Allocation));
                    maximum = (int)Math.Min(maximum, Math.Max(0L, ((long)state.FindPlayer(owner.PlayerId).Resources.Get(t) - fees) / r.UnitResourceCost.Get(t)));
                }
                if (r.ItemCost != null && eligible)
                {
                    foreach (var item in ResourcePaymentSourceService.TransferableItems(state, registry, owner, r).Where(i => !other.Any(d => d.SelectedItemIds.Contains(i.ItemId))))
                            row.AvailableItems.Add(new PaymentSourceItemProjection { ItemId = item.ItemId, TypeId = item.TypeId, OriginalSupplyZoneId = item.OriginalSupplyZoneId,
                                SelectionGroupId = ResourcePaymentSourceService.ItemSelectionGroup(state, item), OwnerSubject = GameStateCloneService.DeepClone(item.OwnerSubject), LocalState = item.LocalState.Clone() });
                    maximum = Math.Min(maximum, Math.Min(row.AvailableItems.Count, MaximumTokenCount) / r.ItemCost.CountPerUnit);
                }
                if (!eligible) maximum = 0;
                foreach (var t in r.AllowedResourceTypes) row.RowMaximums.Set(t, Math.Min(row.RowMaximums.Get(t), maximum));
                if (draft?.Status == "valid" && r.ItemCost != null)
                    valid = valid && ResourcePaymentSourceService.TryPlanItemCost(state, registry, owner, r, PaymentResources.Total(draft.Allocation), draft.SelectedItemIds, out _, out _);
                row.MaxAmount = maximum; row.AvailableItemCount = row.AvailableItems.Count;
                row.DraftStatus = draft?.Status == "valid" && !valid ? "reselect_required" : draft?.Status ?? "unselected";
                view.Sources.Add(row);
            }
            view.SourceCount = view.Sources.Count;
            // 请求创建/编辑的提交会再推进StateRevision；该字段由正式请求单独校验，不进入快照摘要。
            // 全部来源、库存、规则/草稿版本和当前编辑均进入摘要，同修订下库存变化也不会接受旧查询。
            view.SnapshotId = StableIdFactory.Create("payment-source-view.v1", view.GameId, view.ContentPackHash, view.OwnerEffectId, view.PaymentId,
                Newtonsoft.Json.JsonConvert.SerializeObject(new { view.PaymentGeneration, p.Demand, p.SelectedAllocation, p.PaymentCost, p.BeforePaymentReady,
                    p.IsCommitRequested, p.IsCommitted, view.Sources, view.Edit }));
            return view;
        }
        public static void RefreshProjection(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner, InteractionRequest request)
        {
            // 替换旧瞬时投影字段；来源/编辑/墓碑历史仍保存在Payment，完整明细改由受权威约束的查询提供。
            request.PromptParameters.Properties.RemoveAll(e => e.Name == "paymentSources" || e.Name == "paymentEdit");
            foreach (var f in Projection(state, registry, owner).Properties)
            { request.PromptParameters.Properties.RemoveAll(e => e.Name == f.Name); request.PromptParameters.Properties.Add(f); }
            var cost = request.PromptParameters.Properties.SingleOrDefault(e => e.Name == "cost");
            if (cost != null) cost.Value = ResourcePaymentPreparationService.Cost(owner.Payment.PaymentCost);
            request.PromptParameters.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            if (!request.PromptParameters.TryValidate(out var reason)) throw new InvalidOperationException("payment_projection_invalid:" + reason);
        }
        public static EffectStepResult ExecuteTotal(EffectExecutionContext c)
        {
            var error = ResourcePaymentDemandParser.TryParse(c.Node.NormalizedArguments, out var demand, out var player);
            if (error.Length > 0 || player != c.Node.PlayerId) return EffectStepResult.Failed(error.Length > 0 ? error : "payment_permission_denied");
            if (c.Node.Payment == null)
            {
                if (!ResourcePaymentCostPreview.TryFirstAllocation(c.State, c.Registry, c.Node, demand, out var a, out var gross)) return EffectStepResult.Failed("insufficient_resource");
                var p = ResourcePaymentSubstitutionEffectExecutor.InitializePayment(c.Node, gross);
                ResourcePaymentSubstitutionEffectExecutor.EnableV2(p, demand, a);
                var parent = c.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == c.Node.ParentEffectId);
                if (parent?.EffectTypeId == EffectTypeIds.Condition && Flag(parent.NormalizedArguments, "ruleCondition") && parent.FlowStage == "left:0" &&
                    parent.NestedGroupSizes[0] == 1 && parent.ChildEffectIds.FirstOrDefault() == c.Node.EffectId)
                { p.SourceKind = "condition"; p.RuleConditionOwnerEffectId = parent.EffectId; }
            }
            var payment = c.Node.Payment;
            if (payment.PaymentProtocolVersion != 2 || payment.Demand?.Mode != "total" || payment.Demand.TotalCount != demand.TotalCount ||
                !PaymentResources.Same(payment.Demand.FixedCost, demand.FixedCost) || !payment.Demand.AllowedResourceTypes.SequenceEqual(demand.AllowedResourceTypes)) return EffectStepResult.Failed("payment_demand_changed");
            if (payment.IsCommitted) return EffectStepResult.Completed(c.Node.NormalizedResult?.Clone() ?? NormalizedValue.CreateNull()).WithFlowStage("payment_finished");
            var before = ResourcePaymentPreparationService.AwaitBeforePayment(c, payment.PaymentCost); if (before != null) return before;
            if (payment.IsCommitRequested) return ResourcePaymentPlanService.Commit(c, Object("paymentId", NormalizedValue.CreateString(payment.PaymentId)));
            if (c.Node.FlowStage == TotalStage)
            {
                var answer = c.GetLatestInteractionAnswer(); if (answer?.Kind == NormalizedValueKind.Array && answer.Items.Count == 1) answer = answer.Items[0];
                if (answer?.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue) { ResourcePaymentSubstitutionEffectExecutor.CancelDraft(c.Node); return EffectStepResult.Failed("player_declined"); }
                string id = answer?.Kind == NormalizedValueKind.StableReference ? answer.ReferenceId : answer?.StringValue;
                if (id != PaymentResources.Candidate(payment.PaymentCost)) return EffectStepResult.Failed("payment_draft_stale");
                if (!ResourcePaymentPlanService.TryBuild(c.State, c.Registry, c.Node, out _, out error)) return EffectStepResult.Failed(error);
                payment.IsCommitRequested = true;
                return ResourcePaymentPlanService.Commit(c, Object("paymentId", NormalizedValue.CreateString(payment.PaymentId)));
            }
            if (demand.TotalCount == 0) return ResourceEffectExecutor.ExecutePayment(c, payment.PaymentCost, Object("paymentId", NormalizedValue.CreateString(payment.PaymentId)));
            var request = new EffectInteractionSpec { InteractionTypeId = ResourcePaymentChoiceEffectExecutor.InteractionTypeId, AnswerSchema = "candidate_id", AnsweringPlayerId = player,
                Visibility = "owner", AllowDecline = true, MinSelections = 1, MaxSelections = 1, PromptParameters = Projection(c.State, c.Registry, c.Node),
                Presentation = TotalPaymentPresentation(c.State, c.Node) };
            request.CandidateIds.Add(PaymentResources.Candidate(payment.PaymentCost));
            return EffectStepResult.Continue(TotalStage).AddInteraction(request);
        }
        private static GameplayInteractionPresentation TotalPaymentPresentation(GameState state, EffectNodeRuntimeState owner)
        {
            var payment = owner.Payment;
            var parent = payment?.SourceKind == "condition" && payment.RuleConditionOwnerEffectId == owner.ParentEffectId
                ? state.EffectRuntime.EffectNodes.Find(n => n.EffectId == owner.ParentEffectId) : null;
            bool currentCondition = parent?.EffectTypeId == EffectTypeIds.Condition && parent.PlayerId == owner.PlayerId &&
                Flag(parent.NormalizedArguments, "ruleCondition") && parent.FlowStage == "left:0" &&
                parent.NestedGroupSizes?.FirstOrDefault() == 1 && parent.NestedEffects?.FirstOrDefault()?.EffectTypeId == ResourceEffectTypeIds.Pay &&
                parent.ChildEffectIds?.FirstOrDefault() == owner.EffectId && parent.PendingOutcome == EffectPendingOutcome.None &&
                parent.Status != EffectNodeStatus.Completed && parent.Status != EffectNodeStatus.Failed && parent.Status != EffectNodeStatus.Faulted;
            return ResourceEffectExecutor.MergePaymentPresentation(currentCondition ? parent.Presentation?.Interaction : null,
                new GameplayTextRef("core:resource.payment.choice.prompt"));
        }
    }

    public partial class EffectTreeExecutor
    {
        public bool TryPrepareTotalPaymentDraft(int player, string ownerId, string interactionId, int generation, ResourceSet allocation,
            int expectedRevision, string commandId, out string reason)
        {
            if (!TryPaymentDraftContext(player, ownerId, interactionId, expectedRevision, commandId, out var source, out reason)) return false;
            if (!ResourcePaymentDraftService.Editable(source.Payment) || source.Payment.Demand?.Mode != "total") { reason = "payment_total_context_invalid"; return false; }
            if (!ResourcePaymentAllocationService.TryValidate(source.Payment.Demand, allocation, out var gross, out reason) || !ResourcePaymentCostPreview.CanPay(state, registry, source, gross)) { if (reason.Length == 0) reason = "insufficient_resource"; return false; }
            var work = GameStateCloneService.DeepClone(state); var owner = work.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId);
            if (!ResourcePaymentDraftService.TryChangeAllocation(work, registry, owner, allocation, generation, out reason)) return false;
            var request = work.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == interactionId);
            request.CandidateIds.Clear(); request.CandidateIds.Add(PaymentResources.Candidate(owner.Payment.PaymentCost)); request.PromptParameters = ResourcePaymentDraftHost.Projection(work, registry, owner);
            Set(owner, "selectedPaymentOptionId", NormalizedValue.CreateString(PaymentResources.Candidate(owner.Payment.PaymentCost)));
            request.StateRevision = work.EffectRuntime.StateRevision + 1;
            CommitPreparedWork(work, commandId, new List<RuleJournalEntry> { new RuleJournalEntry { Kind = RuleJournalEntryKind.EffectNode, EntityId = ownerId, Detail = "payment_total_draft_prepared" } }, -1, -1, new[] { ownerId });
            reason = ""; return true;
        }
        private bool TryPaymentDraftContext(int player, string ownerId, string interactionId, int revision, string commandId, out EffectNodeRuntimeState owner, out string reason)
        {
            owner = null; reason = "payment_draft_context_invalid";
            return !string.IsNullOrEmpty(commandId) && TryPaymentRequestContext(player, ownerId, interactionId, revision, out owner, out reason);
        }
        private bool TryPaymentRequestContext(int player, string ownerId, string interactionId, int revision, out EffectNodeRuntimeState owner, out string reason)
        {
            owner = GetNode(ownerId); reason = "payment_draft_context_invalid";
            if (revision != Runtime.StateRevision) { reason = "state_revision_mismatch"; return false; }
            if (owner?.PlayerId != player || owner.Payment?.PaymentProtocolVersion != 2 ||
                owner.Status == EffectNodeStatus.Failed || owner.Status == EffectNodeStatus.Faulted || owner.Status == EffectNodeStatus.Completed || owner.PendingOutcome != EffectPendingOutcome.None ||
                !owner.Payment.BeforePaymentReady || !ResourcePaymentDraftService.Editable(owner.Payment) ||
                !Runtime.InteractionRequests.Any(r => r.OwnerEffectId == ownerId && r.GetStableInteractionId() == interactionId && r.AnsweringPlayerId == player && r.StateRevision == revision && r.Status == "open") ||
                ResourcePaymentPreparationService.HasPendingResponse(state, owner)) return false;
            var active = SettlementHostProtocol.FindActiveWindow(state);
            if (active != null && active.Context.OwnerEffectId != ownerId &&
                (string.IsNullOrEmpty(active.ActiveTargetEffectId) || !SettlementEntryService.IsWithin(Runtime, ownerId, active.ActiveTargetEffectId)))
            { reason = "settlement_window_not_active"; return false; }
            reason = ""; return true;
        }
        public bool TryQueryPaymentSources(PaymentSourceQuery query, out PaymentSourceProjection projection, out string reason)
        {
            projection = null; reason = "payment_source_query_invalid";
            if (query == null || !TryPaymentRequestContext(query.PlayerId, query.OwnerEffectId, query.InteractionId, query.StateRevision, out var owner, out reason)) return false;
            var request = Runtime.InteractionRequests.Single(r => r.GetStableInteractionId() == query.InteractionId);
            var descriptor = Value(request.PromptParameters, "paymentSourceQuery");
            if (owner.Payment.PaymentGeneration != query.PaymentGeneration || Number(descriptor, "protocolVersion") != ResourcePaymentDraftHost.SourceQueryProtocolVersion ||
                string.IsNullOrEmpty(query.SnapshotId) || Text(descriptor, "snapshotId") != query.SnapshotId) { reason = "payment_source_query_stale"; return false; }
            var current = ResourcePaymentDraftHost.QuerySources(state, registry, owner);
            if (current.SnapshotId != query.SnapshotId) { reason = "payment_source_query_stale"; return false; }
            if (query.SourceIndex < 0 || query.ItemOffset < 0 ||
                (current.SourceCount == 0 ? query.SourceIndex != 0 || query.ItemOffset != 0 : query.SourceIndex >= current.SourceCount)) { reason = "payment_source_query_cursor_invalid"; return false; }
            if (current.SourceCount > 0)
            {
                var row = current.Sources[query.SourceIndex];
                if (row.AvailableItemCount == 0 ? query.ItemOffset != 0 : query.ItemOffset >= row.AvailableItemCount) { reason = "payment_source_query_cursor_invalid"; return false; }
                // 游标明确描述后续页；不改变真实库存、候选或可选数量，客户端须读到NextSourceIndex=-1。
                row.AvailableItems = row.AvailableItems.Skip(query.ItemOffset).Take(ResourcePaymentDraftHost.SourceQueryItemPageSize).ToList();
                int after = query.ItemOffset + row.AvailableItems.Count;
                current.NextSourceIndex = after < row.AvailableItemCount ? query.SourceIndex : query.SourceIndex + 1 < current.SourceCount ? query.SourceIndex + 1 : -1;
                current.NextItemOffset = after < row.AvailableItemCount ? after : 0;
                current.Sources = new List<PaymentSourceRow> { row };
            }
            current.SourceIndex = query.SourceIndex; current.ItemOffset = query.ItemOffset;
            current.InteractionId = query.InteractionId;
            projection = current; reason = ""; return true;
        }
        public bool TryEditPaymentSource(int player, string ownerId, string interactionId, string operation, string sourceKey, string ruleId, string editId,
            int generation, int draftRevision, ResourceSet allocation, IReadOnlyList<string> itemIds, int revision, string commandId, out string reason)
        {
            if (!TryPaymentDraftContext(player, ownerId, interactionId, revision, commandId, out _, out reason)) return false;
            var work = GameStateCloneService.DeepClone(state); var owner = work.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId);
            var existingEdit = owner.Payment.SourceEdits.SingleOrDefault(e => e.EditId == editId && e.Status == "open");
            string boundSource = operation == "cancel_source_edit" ? existingEdit?.SourceKey : sourceKey;
            string boundRule = operation == "cancel_source_edit" ? existingEdit?.RuleId : ruleId;
            // 新声明只允许真实插入调用，不能用遗留编辑命令绕过受理次数；旧在途草稿合同保持。
            if (ResourcePaymentSourceService.Query(work, registry, owner, owner.Payment.PaymentCost).Any(r => r.Insertion != null && r.SourceRef.SourceKey == boundSource && r.RuleId == boundRule) ||
                owner.SettlementWindow?.Entries.Any(e => e.PaymentInsertion != null && e.PaymentInsertion.Rule.SourceRef.SourceKey == boundSource && e.PaymentInsertion.Rule.RuleId == boundRule) == true)
            { reason = "payment_source_insertion_required"; return false; }
            bool ok;
            if (operation == "begin_source_edit") ok = ResourcePaymentDraftService.TryBeginEdit(work, registry, owner, sourceKey, ruleId, generation, out _, out reason);
            else if (operation == "cancel_source_edit") ok = ResourcePaymentDraftService.TryCancelEdit(owner, editId, out reason);
            else if (operation == "withdraw_source") ok = ResourcePaymentDraftService.TryWithdrawSource(owner, sourceKey, ruleId, generation, draftRevision, out reason);
            else if (operation == "save_source") ok = ResourcePaymentDraftService.TrySetSourceDraft(work, registry, owner, sourceKey, ruleId, generation, draftRevision, allocation, itemIds, editId, out reason);
            else { reason = "payment_source_operation_invalid"; return false; }
            if (!ok) return false;
            var request = work.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == interactionId);
            ResourcePaymentDraftHost.RefreshProjection(work, registry, owner, request);
            request.StateRevision = work.EffectRuntime.StateRevision + 1;
            CommitPreparedWork(work, commandId, new List<RuleJournalEntry> { new RuleJournalEntry { Kind = RuleJournalEntryKind.EffectNode, EntityId = ownerId, Detail = operation } }, -1, -1, new[] { ownerId }); reason = ""; return true;
        }
        public bool TryChangePreparedPaymentMode(int player, string ownerId, string interactionId, string mode, int generation, int revision, string commandId, out string reason)
        {
            if (!TryPaymentDraftContext(player, ownerId, interactionId, revision, commandId, out var original, out reason)) return false;
            if (original.Payment.Preparation?.ActionKind != "build" || original.Payment.Preparation.IsPromoted || !new[] { "resources", "gold" }.Contains(mode)) { reason = "payment_mode_change_invalid"; return false; }
            var work = GameStateCloneService.DeepClone(state); var owner = work.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId);
            owner.Payment.Preparation.ActionArguments.Properties.RemoveAll(e => e.Name == "paymentMode");
            owner.Payment.Preparation.ActionArguments.Properties.Add(new NormalizedValueEntry { Name = "paymentMode", Value = NormalizedValue.CreateString(mode) });
            owner.Payment.Preparation.ActionArguments.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
            owner.NormalizedArguments.Properties.RemoveAll(e => e.Name == "paymentMode"); owner.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "paymentMode", Value = NormalizedValue.CreateString(mode) });
            owner.NormalizedArguments.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
            if (!ResourcePaymentPreparationService.TryQuotePreparation(work, owner, out var quote, out reason, registry) || !ResourcePaymentDraftService.TryChangeFixedQuote(work, registry, owner, quote, generation, out reason)) return false;
            var input = work.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == interactionId);
            ResourcePaymentDraftHost.RefreshProjection(work, registry, owner, input);
            input.StateRevision = work.EffectRuntime.StateRevision + 1;
            CommitPreparedWork(work, commandId, new List<RuleJournalEntry> { new RuleJournalEntry { Kind = RuleJournalEntryKind.EffectNode, EntityId = ownerId, Detail = "payment_mode_changed" } }, -1, -1, new[] { ownerId }); reason = ""; return true;
        }
    }
}
