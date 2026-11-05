using System;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Infrastructure.Lua
{
    public static class AbilityEventLuaFacts
    {
        public const string Version = "ability_events.v2.1.0.3";
        public static bool MatchesCompletion(GameState state, RuleEvent ruleEvent, AbilitySourceDeclaration declaration)
            => AbilityCompletionSourceCatalog.MatchesCompletion(state, ruleEvent, declaration);

        public static NormalizedValue Enrich(GameState state, RuleEvent ruleEvent, NormalizedValue original)
        {
            var payload = FacilityLayerLuaCatalog.EnrichEventPayload(state, ruleEvent, original);
            if (ruleEvent.EventType != "EffectCompleted") return payload;
            var choice = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == ruleEvent.SourceEffectId &&
                n.EffectTypeId == LuaDomainEffectTypeIds.Choice && n.CompletedEventId == ruleEvent.EventId);
            if (choice == null) return payload;
            var requests = state.EffectRuntime.InteractionRequests.Where(r => r.OwnerEffectId == choice.EffectId).ToList();
            // 空候选可正常完成；没有实际回答就不补造interactionId。
            if (requests.Count == 0) return payload;
            // 数量范围按真实库存刷新时，旧回答仍是历史。与执行器消费答案的顺序一致，
            // 只将最后一条实际完成请求作为续接身份，不把过期回答冒充本次完成。
            var completed = requests.Last();
            if (completed.Status != "answered" ||
                requests.Where(r => r.Status == "answered").Any(r => r.AnsweredAtRevision < 1 || r.AnsweredAtRevision > state.EffectRuntime.StateRevision))
                throw new InvalidOperationException("choice_completed_interaction_invalid");
            string id = completed.GetStableInteractionId();
            var result = payload?.Kind == NormalizedValueKind.Object ? payload.Clone() : Object("result", payload?.Clone() ?? NormalizedValue.CreateNull());
            var existing = Value(result, "interactionId");
            if (existing != null)
            {
                if (existing.Kind != NormalizedValueKind.String || existing.StringValue != id)
                    throw new InvalidOperationException("choice_completed_interaction_conflict");
                return result;
            }
            result.Properties.Add(new NormalizedValueEntry { Name = "interactionId", Value = NormalizedValue.CreateString(id) });
            result.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
            return result;
        }
    }
}
