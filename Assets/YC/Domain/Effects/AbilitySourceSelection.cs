using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    /// <summary>004在选面同一步提交调用。候选到实体／面的映射取自Host正式请求，草稿不能启用来源。</summary>
    public static class AbilitySourceSelection
    {
        public static void CommitSelectedFace(EffectExecutionContext context, string itemId, string faceId, string interactionId, string candidateId)
        {
            var state = context.State; var node = context.Node;
            var request = state.EffectRuntime.InteractionRequests.SingleOrDefault(r => r.GetStableInteractionId() == interactionId && r.OwnerEffectId == node.EffectId && r.AnsweringPlayerId == node.PlayerId && r.Status == "answered");
            var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == itemId && i.ControllerSubject.Kind == ItemSubjectKind.Player && i.ControllerSubject.PlayerId == node.PlayerId);
            if (request == null || item == null || !MatchesSelection(request, item, faceId, candidateId) || !context.Registry.AbilityDeclarations.Any(d => d.DefinitionId == item.DefinitionId && d.ActiveFaceId == faceId && d.AuthorizationPolicy == "active_selection"))
                throw new InvalidOperationException("ability_selection_authorization_invalid");
            var properties = item.LocalState.Properties;
            Put(properties, "activeFaceId", NormalizedValue.CreateString(faceId));
            Put(properties, "activationAuthorizationRef", NormalizedValue.CreateString(node.EffectId));
            Put(properties, "sourceSelectionReceipt", Object("protocolVersion", NormalizedValue.CreateInteger(1), "requestId", NormalizedValue.CreateString(interactionId),
                "ownerEffectId", NormalizedValue.CreateString(node.EffectId), "candidateId", NormalizedValue.CreateString(candidateId), "faceId", NormalizedValue.CreateString(faceId)));
            state.EffectRuntime.AbilityActionProtocolVersion = 1;
        }
        public static bool ValidateLocalState(GameState state, ItemInstanceState item, out string reason)
        {
            reason = "ability_source_local_state_invalid";
            foreach (string field in new[] { "activeFaceId", "activationAuthorizationRef" })
                if (Value(item.LocalState, field) is NormalizedValue text && text.Kind != NormalizedValueKind.String) return false;
            var grants = Value(item.LocalState, "grantingSourceRefs");
            if (grants != null && (grants.Kind != NormalizedValueKind.Array || grants.Items.Any(v => v.Kind != NormalizedValueKind.String || string.IsNullOrEmpty(v.StringValue)) ||
                grants.Items.Select(v => v.StringValue).Distinct().Count() != grants.Items.Count)) return false;
            var receipt = Value(item.LocalState, "sourceSelectionReceipt");
            if (Text(item.LocalState, "activationAuthorizationRef").Length > 0 && receipt == null) return false;
            int selectingPlayer = state.EffectRuntime.InteractionRequests.Find(r => r.GetStableInteractionId() == Text(receipt, "requestId"))?.AnsweringPlayerId ?? -1;
            if (receipt != null && (receipt.Kind != NormalizedValueKind.Object || receipt.Properties.Count != 5 ||
                receipt.Properties.Any(p => !new[] { "protocolVersion", "requestId", "ownerEffectId", "candidateId", "faceId" }.Contains(p.Name)) ||
                selectingPlayer < 0 || !HasSelectedFaceReceipt(state, item, Text(item.LocalState, "activeFaceId"), selectingPlayer))) return false;
            reason = ""; return true;
        }
        public static bool HasSelectedFaceReceipt(GameState state, ItemInstanceState item, string face, int player)
        {
            var receipt = Value(item.LocalState, "sourceSelectionReceipt");
            if (Number(receipt, "protocolVersion") != 1 || Text(receipt, "faceId") != face || Text(receipt, "ownerEffectId") != Text(item.LocalState, "activationAuthorizationRef")) return false;
            var request = state.EffectRuntime.InteractionRequests.SingleOrDefault(r => r.GetStableInteractionId() == Text(receipt, "requestId") && r.OwnerEffectId == Text(receipt, "ownerEffectId") && r.AnsweringPlayerId == player && r.Status == "answered");
            return request != null && MatchesSelection(request, item, face, Text(receipt, "candidateId"));
        }
        private static bool MatchesSelection(InteractionRequest request, ItemInstanceState item, string face, string candidate)
        {
            var answer = request.NormalizedAnswer;
            if (answer.Kind == NormalizedValueKind.Array && answer.Items.Count == 1) answer = answer.Items[0];
            string selected = answer.Kind == NormalizedValueKind.StableReference ? answer.ReferenceId : answer.StringValue;
            var candidates = Value(request.PromptParameters, "abilitySourceCandidates");
            return selected == candidate && request.CandidateIds.Contains(candidate) && candidates?.Kind == NormalizedValueKind.Array && candidates.Items.Any(c =>
                Text(c, "candidateId") == candidate && Text(c, "itemId") == item.ItemId && Text(c, "definitionId") == item.DefinitionId && Text(c, "faceId") == face);
        }
        public static void GrantSharedRules(EffectExecutionContext context, string ruleOwnerItemId, IEnumerable<AbilitySourceRef> grantingSources)
        {
            var item = context.State.ItemZones.Items.SingleOrDefault(i => i.ItemId == ruleOwnerItemId && i.ControllerSubject.Kind == ItemSubjectKind.Player && i.ControllerSubject.PlayerId == context.Node.PlayerId);
            var sources = grantingSources.ToList();
            if (item == null || sources.Any(s => s.AuthorizedPlayerId != context.Node.PlayerId || !AbilitySourceService.IsCurrent(context.State, context.Registry.AbilityDeclarations, s)))
                throw new InvalidOperationException("ability_shared_grant_invalid");
            Put(item.LocalState.Properties, "grantingSourceRefs", Strings(sources.Select(s => s.SourceKey).Distinct().OrderBy(id => id, StringComparer.Ordinal)));
            context.State.EffectRuntime.AbilityActionProtocolVersion = 1;
        }
        private static void Put(List<NormalizedValueEntry> values, string key, NormalizedValue value)
        { values.RemoveAll(v => v.Name == key); values.Add(new NormalizedValueEntry { Name = key, Value = value }); values.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name)); }
    }
}
