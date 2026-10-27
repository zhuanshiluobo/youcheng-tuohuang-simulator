using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;
using YC.Domain.Rules;

namespace YC.Domain.Effects
{
    /// <summary>选择一种资源组合并原子支付；作为 Condition 左侧，失败不会创建右侧节点。</summary>
    public static class ResourcePaymentChoiceEffectExecutor
    {
        public const string TypeId = "effect.resource.pay.choice";
        public const string DefinitionVersion = "1.1.0";
        public const string InteractionTypeId = "effect.resource.payment_choice";

        public static EffectSpec Create(int playerId, IList<ResourceSet> options, string promptKey)
        {
            var values = new List<NormalizedValue>();
            foreach (var cost in options)
            {
                var entries = new List<NormalizedValueEntry>();
                foreach (var item in cost.Enumerate())
                    entries.Add(new NormalizedValueEntry { Name = item.Key.ToString(), Value = NormalizedValue.CreateInteger(item.Value) });
                values.Add(NormalizedValue.CreateObject(entries));
            }
            return EffectSpec.Create(TypeId, NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "options", Value = NormalizedValue.CreateArray(values) },
                new NormalizedValueEntry { Name = "promptKey", Value = NormalizedValue.CreateString(promptKey) }
            }), playerId);
        }

        public static void Register(EffectRegistry registry)
        {
            EffectRegistration existing;
            if (registry.TryGet(TypeId, out existing)) return;
            registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, DefinitionVersion));
        }

        // 供现有付款页面预览费用；不会扣费、开新请求或自动插入异客正文。
        public static IReadOnlyDictionary<string, ResourceSet> GetPaymentOptionsForPreview(GameState state, EffectNodeRuntimeState node)
        {
            if (node?.Payment?.PaymentProtocolVersion == 2 && node.Payment.Demand?.Mode == "total")
                return new Dictionary<string, ResourceSet>(StringComparer.Ordinal) { { PaymentResources.Candidate(node.Payment.PaymentCost), node.Payment.PaymentCost.Clone() } };
            if (state == null || node?.EffectTypeId != TypeId || !TryReadCosts(node.NormalizedArguments, out var costs))
                throw new ArgumentException("invalid_payment_options");
            // 可支付资格由带Registry的执行入口产生真实候选；这里只读原费用值，由Host交集候选ID。
            return costs.ToDictionary(p => p.Key, p => GameStateCloneService.DeepClone(p.Value), StringComparer.Ordinal);
        }

        public static IEnumerable<KeyValuePair<string, ResourceSet>> EnumerateDemandOptions(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner, ResourcePaymentDemandState demand)
        {
            foreach (var allocation in ResourcePaymentAllocationService.EnumerateAllocations(demand))
                if (ResourcePaymentAllocationService.TryValidate(demand, allocation, out var gross, out _) && ResourcePaymentCostPreview.CanPay(state, registry, owner, gross))
                    yield return new KeyValuePair<string, ResourceSet>(PaymentResources.Candidate(gross), gross);
        }

        // 宿主在原页面选定费用草稿后调用，再为同一笔费用登记 Offer；最终确认仍使用原付款边界。
        public static bool TryInitializeSelectedPayment(GameState state, EffectNodeRuntimeState node, string optionId, out string reason, EffectRegistry registry = null)
        {
            reason = "payment_choice_context_invalid";
            registry = registry ?? ResourcePaymentSourceService.CostQueryRegistry(state);
            if (state?.EffectRuntime == null || node?.EffectTypeId != TypeId || node.FlowStage != "awaiting_payment" ||
                !state.EffectRuntime.InteractionRequests.Any(r => r.OwnerEffectId == node.EffectId && r.Status == "open" &&
                    r.InteractionTypeId == InteractionTypeId && r.AnsweringPlayerId == node.PlayerId && r.CandidateIds.Contains(optionId)) ||
                !TryReadCosts(node.NormalizedArguments, out var costs) || optionId == null || !costs.TryGetValue(optionId, out var cost) ||
                !(registry != null ? ResourcePaymentCostPreview.CanPay(state, registry, node, cost) : ResourceEffectExecutor.CanPayWithStoredMarkers(state, node.PlayerId, cost))) return false;
            string previous = ItemTransferEffectExecutor.Text(node.ContinuationData, "selectedPaymentOptionId");
            if (previous.Length > 0 && previous != optionId)
            {
                if (!ResourcePaymentSubstitutionEffectExecutor.TryUpdateUninvokedPaymentCost(state, node, cost, out reason, registry)) return false;
            }
            try { ResourcePaymentSubstitutionEffectExecutor.InitializePayment(node, cost); }
            catch (InvalidOperationException error) { reason = error.Message; return false; }
            ItemTransferEffectExecutor.Set(node, "selectedPaymentOptionId", NormalizedValue.CreateString(optionId));
            reason = ""; return true;
        }

        private static bool TryReadCosts(NormalizedValue args, out Dictionary<string, ResourceSet> costs)
        {
            costs = new Dictionary<string, ResourceSet>(StringComparer.Ordinal);
            var values = args?.Kind == NormalizedValueKind.Object ? args.Properties.Find(e => e.Name == "options")?.Value : null;
            if (values?.Kind != NormalizedValueKind.Array) return false;
            foreach (var value in values.Items)
            {
                if (value?.Kind != NormalizedValueKind.Object) return false;
                var cost = new ResourceSet();
                foreach (var entry in value.Properties)
                {
                    if (!Enum.TryParse(entry.Name, out ResourceType type) || !Enum.IsDefined(typeof(ResourceType), type) ||
                        entry.Value?.Kind != NormalizedValueKind.Integer || entry.Value.IntegerValue < 0 || entry.Value.IntegerValue > int.MaxValue)
                        return false;
                    cost.Set(type, (int)entry.Value.IntegerValue);
                }
                string id = "pay|" + cost.Originium + "|" + cost.Iron + "|" + cost.OriginiumShard + "|" + cost.GoldVoucher + "|" + cost.PureOriginium;
                costs[id] = cost;
            }
            return true;
        }

        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            var player = context.State.FindPlayer(context.Node.PlayerId);
            var args = context.Node.NormalizedArguments;
            if (context.Node.Payment != null)
            {
                string selected = ItemTransferEffectExecutor.Text(context.Node.ContinuationData, "selectedPaymentOptionId");
                if (!TryReadCosts(args, out var savedCosts) || !savedCosts.TryGetValue(selected, out var frozenCost))
                    return EffectStepResult.Failed("payment_choice_context_invalid");
                var payment = context.Node.Payment;
                var before = ResourcePaymentPreparationService.AwaitBeforePayment(context, frozenCost);
                if (before != null) return before;
                if (context.Node.FlowStage == ResourcePaymentPreparationService.BeforePaymentStage && !payment.IsCommitRequested)
                {
                    string previousId = ItemTransferEffectExecutor.Text(context.Node.ContinuationData, "paymentChoiceResumeInteractionId");
                    var previous = context.State.EffectRuntime.InteractionRequests.Find(request => request.GetStableInteractionId() == previousId &&
                        request.OwnerEffectId == context.Node.EffectId && request.Status == "invalidated");
                    if (previous == null) return EffectStepResult.Failed("payment_choice_resume_missing");
                    var resumed = new EffectInteractionSpec { InteractionTypeId = InteractionTypeId,
                        AnsweringPlayerId = previous.AnsweringPlayerId, Visibility = previous.Visibility, PromptKey = previous.PromptKey,
                        PromptParameters = previous.PromptParameters.Clone(), Presentation = previous.Presentation,
                        AnswerSchema = previous.AnswerSchema, MinSelections = previous.MinSelections, MaxSelections = previous.MaxSelections,
                        AllowDecline = previous.AllowDecline };
                    resumed.CandidateIds.AddRange(previous.CandidateIds);
                    return EffectStepResult.Continue("awaiting_payment").AddInteraction(resumed);
                }
                if (!payment.IsCommitted && !payment.IsCommitRequested)
                {
                    var savedAnswer = context.GetLatestInteractionAnswer();
                    if (savedAnswer?.Kind == NormalizedValueKind.Boolean && !savedAnswer.BooleanValue)
                        return EffectStepResult.Failed("player_declined");
                    if (savedAnswer?.Kind == NormalizedValueKind.Array && savedAnswer.Items.Count == 1) savedAnswer = savedAnswer.Items[0];
                    string answerId = savedAnswer?.Kind == NormalizedValueKind.StableReference ? savedAnswer.ReferenceId : savedAnswer?.StringValue;
                    if (answerId != selected) return EffectStepResult.NoProgress("等待原付款页面的最终确认或替代分配。");
                    payment.IsCommitRequested = true;
                }
                return ResourceEffectExecutor.ExecutePayment(context, frozenCost, NormalizedValue.CreateString(selected));
            }
            var optionEntry = args == null || args.Kind != NormalizedValueKind.Object ? null : args.Properties.Find(e => e.Name == "options");
            if (optionEntry == null || optionEntry.Value == null || optionEntry.Value.Kind != NormalizedValueKind.Array)
                return EffectStepResult.Failed("invalid_payment_options");
            var options = optionEntry.Value.Items;
            var costs = new Dictionary<string, ResourceSet>(StringComparer.Ordinal);
            foreach (var value in options)
            {
                if (value == null || value.Kind != NormalizedValueKind.Object) return EffectStepResult.Failed("invalid_payment_option");
                var cost = new ResourceSet();
                foreach (var entry in value.Properties)
                {
                    ResourceType type;
                    if (!Enum.TryParse(entry.Name, out type) || !Enum.IsDefined(typeof(ResourceType), type) || entry.Value == null || entry.Value.Kind != NormalizedValueKind.Integer ||
                        entry.Value.IntegerValue < 0 || entry.Value.IntegerValue > int.MaxValue)
                        return EffectStepResult.Failed("invalid_payment_option");
                    cost.Set(type, (int)entry.Value.IntegerValue);
                }
                // 候选 ID 只表达支付组合，回答时以节点中保存的组合重新校验。
                string id = "pay|" + cost.Originium + "|" + cost.Iron + "|" + cost.OriginiumShard + "|" + cost.GoldVoucher + "|" + cost.PureOriginium;
                // 正式请求与预览共用可支付性；这只开放选费，未点击来源不能自动消耗标记。
                if (player != null && ResourcePaymentCostPreview.CanPay(context.State, context.Registry, context.Node, cost)) costs[id] = cost;
            }
            if (context.Node.FlowStage == "awaiting_payment")
            {
                var answer = context.GetLatestInteractionAnswer();
                if (answer == null) return EffectStepResult.Failed("invalid_answer");
                if (answer.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue) return EffectStepResult.Failed("player_declined");
                if (answer.Kind == NormalizedValueKind.Array && answer.Items.Count == 1) answer = answer.Items[0];
                string id = answer.Kind == NormalizedValueKind.StableReference ? answer.ReferenceId : answer.StringValue;
                ResourceSet cost;
                if (id == null || !costs.TryGetValue(id, out cost))
                    return EffectStepResult.Failed("insufficient_resource");
                ResourcePaymentSubstitutionEffectExecutor.InitializePayment(context.Node, cost);
                ItemTransferEffectExecutor.Set(context.Node, "selectedPaymentOptionId", NormalizedValue.CreateString(id));
                context.Node.Payment.IsCommitRequested = true;
                return ResourceEffectExecutor.ExecutePayment(context, cost, NormalizedValue.CreateString(id));
            }
            if (costs.Count == 0) return EffectStepResult.Failed("insufficient_resource");
            var request = new EffectInteractionSpec
            {
                InteractionTypeId = InteractionTypeId, AnsweringPlayerId = context.Node.PlayerId,
                PromptKey = args.Properties.Find(e => e.Name == "promptKey")?.Value?.StringValue ?? "resource.choose_payment",
                Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, AllowDecline = true
            };
            request.CandidateIds.AddRange(costs.Keys);
            request.CandidateIds.Sort(StringComparer.Ordinal);
            return EffectStepResult.Continue("awaiting_payment").AddInteraction(request);
        }
    }
}
