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
        internal string ValidateActionCaller(GameState state, string parentId, int player, bool requireActive = true)
        {
            var parent = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == parentId);
            return parent == null || parent.PlayerId != player || requireActive && (parent.Status == EffectNodeStatus.Faulted || parent.Status == EffectNodeStatus.Failed)
                ? "action_direct_parent_invalid" : "";
        }
        internal string ValidateActionMainBudget(GameState state, int player, AbilityActionDeclaration action, AbilityActionExecutionOptions options)
        {
            if (action.EntryKind != "special" || options?.OmitMainAction == true) return "";
            var budget = new MainActionBudgetService().ValidateCanSpendSpecial(state, player);
            return budget.IsValid ? "" : budget.Reason;
        }
        // 效果调用按调用参数处理费用，不附加普通行动窗口；查询、受理和激活共用实际资格。
        internal string ValidateAuthorizedActionEntry(GameState state, AbilitySourceRef source, AbilityActionDeclaration action,
            AbilityActionExecutionOptions options, string ownInvocation = "", bool previewCost = true)
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
            string budgetReason = ValidateActionMainBudget(state, player, action, options);
            if (budgetReason.Length > 0) return budgetReason;
            return previewCost && action.EntryCostRef.Length > 0 && options?.OmitEntryCost != true ? ValidateActionEntryCost(state, source, action,
                ConstructAbilityBody(action.EntryCostRef, new AbilityActionCallContext { State = state, SourceRef = source, Action = action, Arguments = Object() })) : "";
        }
        public IReadOnlyList<AbilityActionOption> PreviewAuthorizedActions(GameState state,AbilitySourceRef caller,string handlerHash,string executionOwnerId="")
        {
            if(caller==null || !QuerySources(state,caller.AuthorizedPlayerId).Any(s=>CleanupSourceService.SourceProofDigest(s)==CleanupSourceService.SourceProofDigest(caller)) ||
                (executionOwnerId.Length==0 ? HandlerHash(SourceDeclaration(caller).HandlerRef)!=handlerHash :
                    !TryResolveAbilityExecutionHandler(state,executionOwnerId,caller.AbilityId,handlerHash,out var actual,out _) ||
                    CleanupSourceService.SourceProofDigest(actual)!=CleanupSourceService.SourceProofDigest(caller) ||
                    ValidateActionCaller(state,executionOwnerId,caller.AuthorizedPlayerId).Length>0))
                throw new InvalidOperationException("action_direct_preview_unauthorized");
            // 查询只提供来源、次数及占用资格；调用效果携带的费用豁免在真正受理时复核。
            return QueryActionOptions(state,caller.AuthorizedPlayerId,"","",new AbilityActionExecutionOptions { OmitEntryCost = true, OmitMainAction = true });
        }
        public IReadOnlyList<AbilityActionOption> QueryAuthorizedActions(GameState state, string parentEffectId, AbilityActionExecutionOptions options)
        {
            var parent = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == parentEffectId);
            if (parent == null || ValidateActionCaller(state, parentEffectId, parent.PlayerId).Length > 0) throw new InvalidOperationException("action_direct_parent_invalid");
            return QueryActionOptions(state,parent.PlayerId,"","",options ?? new AbilityActionExecutionOptions());
        }
    }
}
