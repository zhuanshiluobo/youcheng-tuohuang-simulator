using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.Presentation;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    [Serializable] public sealed class PaymentInsertionItemDraft
    {
        public int ProtocolVersion = 1, RequiredCount, AwaitingRevision;
        public bool Cancelled;
        public List<string> CandidateIds = new List<string>();
        public List<PaymentInsertionItemPick> Picks = new List<PaymentInsertionItemPick>();
    }
    [Serializable] public sealed class PaymentInsertionItemPick
    {
        public string EffectId = "", ItemId = "";
    }

    public static partial class ResourcePaymentInsertionService
    {
        // 内部草稿操作，不公开为Lua构造器，不扣除任何实物或写入最终付款收据。
        public const string PickItemTypeId = "flow.payment.insertion.pick_item", PickItemVersion = "1.0.0";
        public static void RegisterItemChoices(EffectRegistry registry)
        {
            if (!registry.TryGet(PickItemTypeId, out _)) registry.Register(new EffectRegistration(PickItemTypeId, PickItem,
                EffectExecutorKind.Atomic, PickItemVersion) { Validator = s => ValidatePickArguments(s.NormalizedArguments) });
        }
        public static string ValidatePickArguments(NormalizedValue args) => args?.Kind == NormalizedValueKind.Object && args.IsValid() && args.Properties.Count == 3 &&
            Text(args, "selectionOwnerId").Length > 0 && Text(args, "itemId").Length > 0 && Value(args, "revision")?.Kind == NormalizedValueKind.Integer &&
            Value(args, "revision").IntegerValue >= 0 && Value(args, "revision").IntegerValue < MaximumTokenCount &&
            args.Properties.All(p => new[] { "selectionOwnerId", "itemId", "revision" }.Contains(p.Name)) ? "" : "payment_insertion_pick_arguments_invalid";

        // 与DTO协议1分开记录恢复校验身份；不把缺失默认字段或可转换的错误JSON类型当作有效草稿。
        public const string ItemSelectionRecoveryVersion = "payment_insertion.item_draft_shape.v1.0.1";
        public static string ValidateItemSelectionRaw(Newtonsoft.Json.Linq.JObject node, bool runtimeNode)
        {
            var value = node?["PaymentInsertionItems"];
            if (value == null || value.Type == Newtonsoft.Json.Linq.JTokenType.Null) return "";
            try
            {
                return runtimeNode && ItemMovementV4.RawShape(value, typeof(PaymentInsertionItemDraft))
                    ? "" : "payment_insertion_item_draft_raw_shape_invalid";
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException ||
                error is OverflowException || error is InvalidCastException)
            {
                return "payment_insertion_item_draft_raw_shape_invalid";
            }
        }

        private static List<EffectNodeRuntimeState> SelectionChildren(GameState state, EffectNodeRuntimeState owner) => owner.ChildEffectIds
            .Where(id => !state.EffectRuntime.DispatchReceipts.Any(r => r.Succeeded && r.AttachedEffectIds.Contains(id)))
            .Select(id => state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id)).ToList();

        private static EffectSpec ItemChoice(EffectNodeRuntimeState owner, IList<string> ids, GameplayTextRef prompt)
        {
            var options = new List<string>(); var groups = new List<List<EffectSpec>>(); var labels = new List<GameplayOptionPresentation>();
            if (ids.Count > 16)
            {
                int pageSize = 16; while ((ids.Count + pageSize - 1) / pageSize > 16) pageSize = checked(pageSize * 16);
                for (int first = 0; first < ids.Count; first += pageSize)
                {
                    string id = "page:" + first.ToString(CultureInfo.InvariantCulture); options.Add(id);
                    groups.Add(new List<EffectSpec> { ItemChoice(owner, ids.Skip(first).Take(pageSize).ToList(), prompt) });
                    labels.Add(new GameplayOptionPresentation { CandidateId = id, Label = new GameplayTextRef("core:interaction.choice.item_group",
                        new GameplayTextArgument { Name = "minimum", Kind = "integer", Value = NormalizedValue.CreateInteger(first + 1) },
                        new GameplayTextArgument { Name = "maximum", Kind = "integer", Value = NormalizedValue.CreateInteger(Math.Min(first + pageSize, ids.Count)) }) });
                }
            }
            else foreach (string id in ids)
            {
                options.Add(id);
                var pick = EffectSpec.Create(PickItemTypeId, Object("selectionOwnerId", NormalizedValue.CreateString(owner.EffectId),
                    "revision", NormalizedValue.CreateInteger(owner.PaymentInsertionItems.Picks.Count), "itemId", NormalizedValue.CreateString(id)), owner.PlayerId);
                var text = new GameplayTextRef("core:interaction.payment.select_item", new GameplayTextArgument { Name = "entity", Kind = "item", Value = NormalizedValue.CreateStableReference("item", id) });
                pick.Presentation = new EffectPresentationDefinition { Started = text.Clone() };
                groups.Add(new List<EffectSpec> { pick }); labels.Add(new GameplayOptionPresentation { CandidateId = id, Label = text });
            }
            var choice = EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString(owner.PlayerId.ToString(CultureInfo.InvariantCulture)),
                "options", Strings(options), "minSelections", NormalizedValue.CreateInteger(0), "maxSelections", NormalizedValue.CreateInteger(1),
                "interactionType", NormalizedValue.CreateString("effect.choice.execute")), owner.PlayerId);
            choice.DecisionPlayerId = owner.PlayerId; choice.NestedEffectGroups = groups;
            choice.Presentation = new EffectPresentationDefinition { Interaction = new GameplayInteractionPresentation { Prompt = prompt.Clone(), Options = labels } };
            return choice;
        }

        private static EffectStepResult NextItemChoice(EffectExecutionContext c, PaymentSourceRule rule)
        {
            var draft = c.Node.PaymentInsertionItems;
            draft.AwaitingRevision = draft.Picks.Count;
            var selected = new HashSet<string>(draft.Picks.Select(p => p.ItemId), StringComparer.Ordinal);
            var choice = ItemChoice(c.Node, draft.CandidateIds.Where(id => !selected.Contains(id)).ToList(), rule.Insertion.Presentation.Description);
            choice.StableKey = "payment-item:" + draft.Picks.Count.ToString(CultureInfo.InvariantCulture);
            return EffectStepResult.Continue("selecting_item_branches").AddChild(choice);
        }

        private static bool SelectedBranchLeadsTo(GameState state, EffectNodeRuntimeState owner, EffectNodeRuntimeState leaf)
        {
            var child = leaf; var seen = new HashSet<string>();
            while (seen.Add(child.EffectId))
            {
                var parent = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == child.ParentEffectId && n.PlayerId == owner.PlayerId);
                if (parent == owner) return SelectionChildren(state, owner).Contains(child) && child.EffectTypeId == LuaDomainEffectTypeIds.Choice;
                if (parent?.EffectTypeId != LuaDomainEffectTypeIds.Choice || !parent.ChildEffectIds.Contains(child.EffectId) ||
                    parent.FlowStage != "branch_completed" || parent.NormalizedResult?.Kind != NormalizedValueKind.String) return false;
                int index = Value(parent.NormalizedArguments, "options")?.Items.FindIndex(v => v.StringValue == parent.NormalizedResult.StringValue) ?? -1;
                if (index < 0 || index >= parent.NestedGroupSizes.Count || parent.NestedGroupSizes[index] != 1) return false;
                var spec = parent.NestedEffects[parent.NestedGroupSizes.Take(index).Sum()];
                if (spec.EffectTypeId != child.EffectTypeId || spec.PlayerId != child.PlayerId ||
                    ItemTransferService.MovementV4Digest(new { spec.NormalizedArguments, spec.NestedGroupSizes, spec.NestedEffects }) !=
                    ItemTransferService.MovementV4Digest(new { child.NormalizedArguments, child.NestedGroupSizes, child.NestedEffects })) return false;
                child = parent;
            }
            return false;
        }

        private static EffectStepResult PickItem(EffectExecutionContext c)
        {
            var node = c.Node; var args = node.NormalizedArguments;
            if (ValidatePickArguments(args).Length > 0) return EffectStepResult.Failed("payment_insertion_pick_arguments_invalid");
            var selection = c.State.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == Text(args, "selectionOwnerId"));
            var draft = selection?.PaymentInsertionItems;
            if (selection?.EffectTypeId != ResourcePaymentSubstitutionEffectExecutor.TypeId || selection.PlayerId != node.PlayerId ||
                selection.FlowStage != "selecting_item_branches" || draft?.ProtocolVersion != 1 || draft.Cancelled || !SelectedBranchLeadsTo(c.State, selection, node))
                return EffectStepResult.Failed("payment_insertion_pick_owner_invalid");
            int revision = Number(args, "revision"); string item = Text(args, "itemId");
            if (node.FlowStage == "selected") return revision < draft.Picks.Count && draft.Picks[revision].EffectId == node.EffectId && draft.Picks[revision].ItemId == item
                ? EffectStepResult.Completed(node.NormalizedResult).WithFlowStage("selected") : EffectStepResult.Failed("payment_insertion_pick_receipt_invalid");
            if (node.FlowStage != "" || revision != draft.Picks.Count || revision != draft.AwaitingRevision || draft.Picks.Count >= draft.RequiredCount ||
                !draft.CandidateIds.Contains(item) || draft.Picks.Any(p => p.ItemId == item)) return EffectStepResult.Failed("payment_insertion_pick_stale");
            // 每次草稿编辑仍复验原付款上下文与完整库存，不能依靠UI提交任意实例。
            var owner = c.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == Text(selection.NormalizedArguments, "paymentOwnerEffectId"));
            var entry = owner?.SettlementWindow?.Entries.SingleOrDefault(e => e.PaymentInsertion != null && e.SettlementState == "executing" &&
                SettlementEntryService.IsWithin(c.State.EffectRuntime, selection.EffectId, e.TargetEffectId));
            if (entry == null || !TryCurrent(c.State, c.Registry, owner.SettlementWindow, entry, out owner, out var rule) ||
                Number(selection.NormalizedArguments, "paymentGeneration") != owner.Payment.PaymentGeneration ||
                Number(selection.NormalizedArguments, "draftRevision") != (Draft(owner.Payment, rule)?.DraftRevision ?? 0) ||
                ResourcePaymentSourceService.Fingerprint(rule) != Text(selection.ContinuationData, "insertionRule")) return EffectStepResult.Failed("payment_insertion_context_invalid");
            bool replace = Text(selection.ContinuationData, "insertionConfigurationMode") == "replace";
            Maximum(c.State, c.Registry, owner, rule, out _, out var items, replace);
            if (Inventory(c.State, items) != Text(selection.ContinuationData, "insertionInventory") || !items.Any(i => i.ItemId == item)) return EffectStepResult.Failed("payment_insertion_resume_identity_invalid");
            draft.Picks.Add(new PaymentInsertionItemPick { EffectId = node.EffectId, ItemId = item });
            return EffectStepResult.Completed(Object("draftChanged", NormalizedValue.CreateBoolean(true), "selectedCount", NormalizedValue.CreateInteger(draft.Picks.Count))).WithFlowStage("selected");
        }

        public static bool ValidateItemSelectionSaved(GameState state, EffectNodeRuntimeState node)
        {
            if (node.EffectTypeId == PickItemTypeId)
            {
                if (node.DefinitionVersion != PickItemVersion || ValidatePickArguments(node.NormalizedArguments).Length > 0 || node.PositionChanges.Count != 0 ||
                    state.EffectRuntime.InteractionRequests.Any(r => r.OwnerEffectId == node.EffectId) || SelectionChildren(state, node).Count != 0) return false;
                var owner = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == Text(node.NormalizedArguments, "selectionOwnerId"));
                if (owner?.PaymentInsertionItems == null || owner.PlayerId != node.PlayerId || !SelectedBranchLeadsTo(state, owner, node)) return false;
                int revision = Number(node.NormalizedArguments, "revision"); var picks = owner.PaymentInsertionItems.Picks;
                return node.FlowStage == "" ? !picks.Any(p => p.EffectId == node.EffectId) : node.FlowStage == "selected" && revision < picks.Count &&
                    picks[revision].EffectId == node.EffectId && picks[revision].ItemId == Text(node.NormalizedArguments, "itemId") &&
                    Flag(node.NormalizedResult, "draftChanged") && Number(node.NormalizedResult, "selectedCount", -1) == revision + 1;
            }
            if (node.EffectTypeId != ResourcePaymentSubstitutionEffectExecutor.TypeId) return node.PaymentInsertionItems == null;
            if (node.FlowStage == "selecting_items") return node.PaymentInsertionItems == null && (node.Status == EffectNodeStatus.Completed || node.Status == EffectNodeStatus.Failed); // 只保留已终结历史，旧待答多选拒绝。
            var draft = node.PaymentInsertionItems; if (draft == null) return node.FlowStage != "selecting_item_branches";
            if (draft.ProtocolVersion != 1 || draft.RequiredCount < 1 || draft.RequiredCount > MaximumTokenCount || draft.CandidateIds == null || draft.Picks == null ||
                draft.CandidateIds.Count < draft.RequiredCount || draft.CandidateIds.Any(string.IsNullOrEmpty) || draft.CandidateIds.Distinct(StringComparer.Ordinal).Count() != draft.CandidateIds.Count ||
                draft.Picks.Count > draft.RequiredCount || draft.Picks.Any(p => p == null || !draft.CandidateIds.Contains(p.ItemId)) ||
                draft.Picks.Select(p => p.ItemId).Distinct(StringComparer.Ordinal).Count() != draft.Picks.Count ||
                draft.Picks.Select(p => p.EffectId).Distinct(StringComparer.Ordinal).Count() != draft.Picks.Count ||
                draft.AwaitingRevision < 0 || draft.AwaitingRevision > draft.Picks.Count || draft.Picks.Count > draft.AwaitingRevision + 1) return false;
            for (int i = 0; i < draft.Picks.Count; i++)
            {
                var pick = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == draft.Picks[i].EffectId);
                if (pick?.EffectTypeId != PickItemTypeId || pick.PlayerId != node.PlayerId || Text(pick.NormalizedArguments, "selectionOwnerId") != node.EffectId ||
                    Number(pick.NormalizedArguments, "revision", -1) != i || Text(pick.NormalizedArguments, "itemId") != draft.Picks[i].ItemId || !SelectedBranchLeadsTo(state, node, pick)) return false;
            }
            return true;
        }
    }
}
