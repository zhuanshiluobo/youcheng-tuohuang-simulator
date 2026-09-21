using System;
using System.Collections.Generic;
using YC.Domain.State;
using YC.Domain.Rules;

namespace YC.Domain.Effects
{
    /// <summary>选择一种资源组合并原子支付；作为 Condition 左侧，失败不会创建右侧节点。</summary>
    public static class ResourcePaymentChoiceEffectExecutor
    {
        public const string TypeId = "effect.resource.pay.choice";
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
            registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, "1.0.0"));
        }

        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            var player = context.State.FindPlayer(context.Node.PlayerId);
            var args = context.Node.NormalizedArguments;
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
                if (player != null && player.Resources.CanPay(cost)) costs[id] = cost;
            }
            if (context.Node.FlowStage == "awaiting_payment")
            {
                var answer = context.GetLatestInteractionAnswer();
                if (answer == null) return EffectStepResult.Failed("invalid_answer");
                if (answer.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue) return EffectStepResult.Failed("player_declined");
                if (answer.Kind == NormalizedValueKind.Array && answer.Items.Count == 1) answer = answer.Items[0];
                string id = answer.Kind == NormalizedValueKind.StableReference ? answer.ReferenceId : answer.StringValue;
                ResourceSet cost;
                if (id == null || !costs.TryGetValue(id, out cost) || !player.Resources.TryPay(cost))
                    return EffectStepResult.Failed("insufficient_resource");
                return EffectStepResult.Completed(NormalizedValue.CreateString(id));
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
