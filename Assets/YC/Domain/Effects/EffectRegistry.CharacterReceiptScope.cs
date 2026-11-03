using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public sealed partial class EffectRegistry
    {
        // 只返回真实模式树内的收据读取范围；不生成AbilitySourceRef或动作调用能力。
        public bool TryGetCharacterReceiptScope(GameState state,EffectNodeRuntimeState owner,string ability,string handlerHash,string handlerVersion,int player,out HashSet<string> owned)
        {
            owned=null;if(state?.EffectRuntime==null||owner==null||ContentPackHash!=state.ContentPackHash||!state.EffectRuntime.EffectNodes.Any(n=>ReferenceEquals(n,owner)))return false;
            var activation=owner;var seen=new HashSet<string>();
            while(activation!=null&&seen.Add(activation.EffectId)&&activation.EffectTypeId!=CharacterAbilityEffectExecutor.ActivationEffectTypeId)activation=state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==activation.ParentEffectId);
            if(activation?.EffectTypeId!=CharacterAbilityEffectExecutor.ActivationEffectTypeId||Text(activation.NormalizedArguments,"abilityId")!=ability||activation.PlayerId!=player)return false;
            var use=state.CharacterUses.SingleOrDefault(u=>u.UseId==Text(activation.NormalizedArguments,"useId")&&u.OwnerEffectId==activation.ParentEffectId&&u.ExecutingPlayerId==player);
            if(use==null||use.Finished||Value(activation.NormalizedArguments,"cardInstanceId")?.ReferenceId!=use.CardInstanceId||!use.ModeOrder.Contains(Text(activation.NormalizedArguments,"mode"))||!use.Modes.Any(m=>m.Mode==Text(activation.NormalizedArguments,"mode")&&m.AbilityId==ability)||!state.ItemZones.Items.Any(i=>i.ItemId==use.CardInstanceId))return false;
            if(!CharacterActivationSubscriptions.TryGetValue(ability,out string subscription))return false;
            var primary=FindEventHandler(subscription);
            if(primary==null||primary.Role!=EffectHandlerRole.Primary||primary.EventType!=CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType||primary.AbilityId!=ability||primary.ContentHash!=handlerHash||primary.DefinitionVersion!=handlerVersion)return false;
            string activationId=activation.EffectId;
            var trigger=state.EffectRuntime.RuleEvents.SingleOrDefault(r=>r.EventId==StableIdFactory.Create("event",activationId,CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType)&&r.SourceEffectId==activationId&&r.OwnerNodeId==activationId&&r.RouteKey==ability&&r.PlayerId==player);
            if(trigger==null||!state.EffectRuntime.DispatchReceipts.Any(r=>r.Succeeded&&r.EventId==trigger.EventId&&r.SubscriptionId==subscription&&r.HandlerId==primary.HandlerId&&r.AbilityId==ability&&r.DefinitionVersion==handlerVersion&&r.RouteTier==(int)EffectRouteTier.Targeted&&r.AttachedEffectIds.Count>0))return false;
            owned=CharacterLifecycleService.GetModeOwnedEffects(state,use,activation);return owned.Contains(owner.EffectId);
        }
    }
}
