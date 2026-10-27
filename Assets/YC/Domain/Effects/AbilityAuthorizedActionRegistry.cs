using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public sealed partial class EffectRegistry
    {
        public IReadOnlyList<string> ActionCallPoliciesFor(string abilityId, string handlerHash) => actionCallPolicies.Values.Where(p => p.CallerAbilityId == abilityId && p.CallerHandlerHash == handlerHash).Select(p => p.PolicyRef).OrderBy(id => id, StringComparer.Ordinal).ToList();
        internal string ValidateActionCaller(GameState state, string parentId, AbilityActionCallPolicy policy, int player)
        {
            var parent = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == parentId);
            var invocation = state.EffectRuntime.AbilityActionInvocations.Find(i => i.OwnerEffectId == parentId && i.ActivationCommitted);
            bool actionParent = invocation != null && invocation.SourceRef.AbilityId == policy.CallerAbilityId && invocation.VersionRef.HandlerHash == policy.CallerHandlerHash;
            bool sourceParent = parent?.EffectTypeId == AbilitySourceEffectExecutor.TypeId && Text(parent.NormalizedArguments, "abilityId") == policy.CallerAbilityId &&
                state.EffectRuntime.AbilitySourceUses.Any(r => r.OwnerEffectId == parent.EffectId && r.AbilityId == policy.CallerAbilityId) &&
                HandlerHash(Text(parent.NormalizedArguments, "handlerRef")) == policy.CallerHandlerHash;
            bool cleanupParent = IsCleanupInvocationCaller(state, parentId, policy, player);
            return parent == null || parent.PlayerId != player || !actionParent && !sourceParent && !cleanupParent &&
                (parent.ContinuationAbilityId != policy.CallerAbilityId || parent.ContinuationContentHash != policy.CallerHandlerHash) ||
                !abilityDeclarations.Any(d => d.AbilityId == policy.CallerAbilityId && d.AllowedOperations.Contains("action.invoke:" + policy.PolicyRef))
                ? "action_direct_parent_unauthorized" : "";
        }
        internal string ValidateActionMainBudget(GameState state, int player, AbilityActionDeclaration action, AbilityActionCallPolicy policy)
        {
            if (action.EntryKind != "special" || policy?.OmitMainAction == true) return "";
            var budget = new MainActionBudgetService().ValidateCanSpendSpecial(state, player);
            return budget.IsValid ? "" : budget.Reason;
        }
        // 授权入口只按其policy省包装，不附加普通行动窗口；查询、受理和付款后激活共用此规则。
        internal string ValidateAuthorizedActionEntry(GameState state, AbilitySourceRef source, AbilityActionDeclaration action,
            AbilityActionCallPolicy policy, string ownInvocation = "", bool previewCost = true)
        {
            string occupancy = AbilitySourceService.OccupancyKey(state, source, action);
            if (state.EffectRuntime.AbilityActionInvocations.Any(i => i.InvocationId != ownInvocation && i.OccupancyKey == occupancy &&
                (AbilitySourceService.InvocationActive(state, i) || i.ActivationCommitted && AbilitySourceService.ConsumesLifecycle(action)))) return "action_group_occupied";
            if (action.UsageTrackId.Length > 0)
            {
                string reason = UsageProvider(action.UsageTrackId)?.Validate(state, source, action, out _) ?? "usage_track_unavailable";
                if (reason.Length > 0) return reason;
            }
            int player = source.AuthorizedPlayerId;
            if (action.LockPolicy == "character_action_round" && (state.FindPlayer(player).UsedCharacterThisTurn ||
                state.CharacterUses.Any(u => u.ExecutingPlayerId == player && !u.Finished && !u.SelectionCancelled))) return "character_action_round_conflict";
            string budgetReason = ValidateActionMainBudget(state, player, action, policy);
            if (budgetReason.Length > 0) return budgetReason;
            return previewCost && action.EntryCostRef.Length > 0 && !policy.OmitEntryCost ? ValidateActionEntryCost(state, source, action,
                ConstructAbilityBody(action.EntryCostRef, new AbilityActionCallContext { State = state, SourceRef = source, Action = action, Arguments = Object() })) : "";
        }
        public IReadOnlyList<AbilityActionOption> PreviewAuthorizedActions(GameState state,AbilitySourceRef caller,string handlerHash,string callPolicyRef,string executionOwnerId="")
        {
            if(caller==null || !QuerySources(state,caller.AuthorizedPlayerId).Any(s=>CleanupSourceService.SourceProofDigest(s)==CleanupSourceService.SourceProofDigest(caller)) ||
                !TryCallPolicy(callPolicyRef,out var policy) || policy.CallerAbilityId!=caller.AbilityId || policy.CallerHandlerHash!=handlerHash ||
                !SourceDeclaration(caller).AllowedOperations.Contains("action.invoke:"+callPolicyRef) ||
                (executionOwnerId.Length==0 ? HandlerHash(SourceDeclaration(caller).HandlerRef)!=handlerHash :
                    !TryResolveAbilityExecutionHandler(state,executionOwnerId,caller.AbilityId,handlerHash,out var actual,out _) ||
                    CleanupSourceService.SourceProofDigest(actual)!=CleanupSourceService.SourceProofDigest(caller) ||
                    ValidateActionCaller(state,executionOwnerId,policy,caller.AuthorizedPlayerId).Length>0))
                throw new InvalidOperationException("action_direct_preview_unauthorized");
            return QueryActionOptions(state,caller.AuthorizedPlayerId,"","",policy);
        }
        public IReadOnlyList<AbilityActionOption> QueryAuthorizedActions(GameState state, string parentEffectId, string callPolicyRef)
        {
            if (!TryCallPolicy(callPolicyRef, out var policy)) throw new InvalidOperationException("action_direct_policy_denied");
            var parent = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == parentEffectId);
            if (parent == null || ValidateActionCaller(state, parentEffectId, policy, parent.PlayerId).Length > 0) throw new InvalidOperationException("action_direct_parent_unauthorized");
            return QueryActionOptions(state,parent.PlayerId,"","",policy);
        }
    }
}
