using System;
using System.Linq;
using System.Runtime.CompilerServices;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Infrastructure.Lua
{
    public static partial class SourceFactsLuaCatalog
    {
        private sealed class ReceiptInvocationCall
        {
            internal GameState State;internal EffectRegistry Registry;internal LuaHandlerDefinition Handler;
        }
        private static readonly ConditionalWeakTable<EffectEventHandlerContext,ReceiptInvocationCall> ReceiptInvocationCalls=new ConditionalWeakTable<EffectEventHandlerContext,ReceiptInvocationCall>();
        internal static void BindReceiptInvocation(EffectEventHandlerContext context,GameState state,EffectRegistry registry,LuaHandlerDefinition handler)
        {
            if(context==null)return;ReceiptInvocationCalls.Remove(context);ReceiptInvocationCalls.Add(context,new ReceiptInvocationCall{State=state,Registry=registry,Handler=handler});
        }
        // 仅补真实角色模式完成续接的只读收据资格；不授予SourceFacts动作权限，也不相信payload中的source。
        internal static LuaSnapshotValue CharacterReceiptsForInvocation(GameState state,EffectRegistry registry,LuaHandlerDefinition handler,EffectEventHandlerContext context,int player)
        {
            if(context==null||!ReceiptInvocationCalls.TryGetValue(context,out var call)||!ReferenceEquals(call.State,state)||!ReferenceEquals(call.Registry,registry)||!ReferenceEquals(call.Handler,handler)||
                state?.EffectRuntime==null||state.ContentPackHash!=registry.ContentPackHash||!ReferenceEquals(context.State,state)||handler.Role!=EffectHandlerRole.Continuation)return LuaSnapshotValue.Null;
            var owner=context.OwnerNode;var e=context.Event;var registration=context.Registration;
            if(owner==null||e==null||registration==null||!state.EffectRuntime.EffectNodes.Any(n=>ReferenceEquals(n,owner))||!state.EffectRuntime.RuleEvents.Any(r=>ReferenceEquals(r,e))||
                e.EventType!="EffectCompleted"||e.EventId!=owner.CompletedEventId||e.SourceEffectId!=owner.EffectId||e.OwnerNodeId!=owner.EffectId||e.PlayerId!=player||owner.PlayerId!=player||
                owner.ContinuationHandlerId!=handler.Script.HandlerId||owner.ContinuationContentHash!=handler.Script.ContentHash||owner.ContinuationAbilityId!=handler.AbilityId||
                !registry.TryGetContinuation(handler.Script.HandlerId,"EffectCompleted",out var actualRegistration)||!ReferenceEquals(actualRegistration,registration)||registration.AbilityId!=handler.AbilityId||registration.ContentHash!=handler.Script.ContentHash||registration.DefinitionVersion!=handler.Script.DefinitionVersion||
                owner.ContinuationDefinitionVersion!=registration.DefinitionVersion||state.EffectRuntime.DispatchReceipts.Any(r=>r.EventId==e.EventId&&r.SubscriptionId==registration.SubscriptionId))return LuaSnapshotValue.Null;
            var binding=state.EffectRuntime.ContinuationBindings.SingleOrDefault(b=>b.BindingId==owner.ContinuationBindingId);
            if(binding==null||binding.SourceEffectId!=owner.EffectId||binding.HandlerId!=registration.HandlerId||binding.ContentHash!=registration.ContentHash||binding.DefinitionVersion!=registration.DefinitionVersion||binding.AbilityId!=handler.AbilityId||binding.CompletionEventId!=e.EventId||binding.Status==ContinuationBindingStatus.Cancelled)return LuaSnapshotValue.Null;
            if(!registry.TryGetCharacterReceiptScope(state,owner,handler.AbilityId,handler.Script.ContentHash,handler.Script.DefinitionVersion,player,out var owned))return LuaSnapshotValue.Null;
            return TransferReceiptLuaCatalog.Snapshot(state,player,n=>owned.Contains(n.EffectId));
        }
    }
}
