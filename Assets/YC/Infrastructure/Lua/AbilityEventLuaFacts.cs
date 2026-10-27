using System;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Infrastructure.Lua
{
    public static class AbilityEventLuaFacts
    {
        public const string Version = "ability_events.v2.1.0.0";
        public static bool MatchesCompletion(GameState state, RuleEvent ruleEvent, AbilitySourceDeclaration declaration)
        {
            if (ruleEvent.EventType != "EffectCompleted") return true;
            var node = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == ruleEvent.SourceEffectId);
            return node != null && node.CompletedEventId == ruleEvent.EventId &&
                declaration.CompletedEffectTypes.Contains(node.EffectTypeId) && node.EffectTypeId != AbilitySourceEffectExecutor.TypeId &&
                (declaration.CompletionPolicy == "terminal" || node.Status == EffectNodeStatus.Completed || node.PendingOutcome == EffectPendingOutcome.Completed);
        }

        public static NormalizedValue Enrich(GameState state, RuleEvent ruleEvent, NormalizedValue original)
        {
            var payload = FacilityLayerLuaCatalog.EnrichEventPayload(state, ruleEvent, original);
            if (ruleEvent.EventType != "EffectCompleted") return payload;
            var choice = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == ruleEvent.SourceEffectId &&
                n.EffectTypeId == LuaDomainEffectTypeIds.Choice && n.CompletedEventId == ruleEvent.EventId);
            if (choice == null) return payload;
            var requests = state.EffectRuntime.InteractionRequests.Where(r => r.OwnerEffectId == choice.EffectId && r.Status == "answered").ToList();
            // 空候选可正常完成；没有实际回答就不补造interactionId。
            if (requests.Count == 0) return payload;
            if (requests.Count != 1 || requests[0].AnsweredAtRevision < 1 || requests[0].AnsweredAtRevision > state.EffectRuntime.StateRevision)
                throw new InvalidOperationException("choice_completed_interaction_invalid");
            string id = requests[0].GetStableInteractionId();
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
