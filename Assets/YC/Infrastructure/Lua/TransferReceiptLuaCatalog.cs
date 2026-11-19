using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Domain.Rules;

namespace YC.Infrastructure.Lua
{
    public static class TransferReceiptLuaCatalog
    {
        public const string Version = "transfer_receipt_page.v1";
        internal static LuaSnapshotValue Create(GameState state, EffectRegistry registry, AbilitySourceRef source, string ownerId, string handlerHash)
        {
            string anchor=Anchor(state,ownerId);
            return Snapshot(state,source.AuthorizedPlayerId,node=>
            {
                if(Anchor(state,node.EffectId)!=anchor)return false;
                bool allowed=registry.TryResolveAbilityExecutionHandler(state,node.EffectId,source.AbilityId,handlerHash,out var actual,out _)&&actual.SourceKey==source.SourceKey&&actual.ItemId==source.ItemId&&actual.AuthorizationRef==source.AuthorizationRef;
                var persistent=CharacterLifecycleService.PersistentSourceFactsRoot(state,node);
                return allowed||persistent?.PersistentSourceFacts?.SourceRef?.SourceKey==source.SourceKey&&persistent.PersistentSourceFacts.SourceRef.ItemId==source.ItemId;
            });
        }
        internal static LuaSnapshotValue Snapshot(GameState state,int player,Func<EffectNodeRuntimeState,bool> allowed)
        {
            var receipts=new List<LuaSnapshotValue>();var visible=new ItemZoneQuery().Project(state,GameStateViewer.Player(player));
            var visibleIds=new HashSet<string>(visible.Items.Select(i=>i.ItemId),StringComparer.Ordinal);var visibleZones=new HashSet<string>(visible.Zones.Where(z=>z.IdentitiesVisible).Select(z=>z.Zone.ZoneId),StringComparer.Ordinal);
            foreach(var node in state.EffectRuntime.EffectNodes)
            {
                var receipt=node.ItemMovementV4?.Receipt??node.CommonItemMovementV4?.Receipt??node.CharacterCoverMovementV4?.Receipt;
                if(receipt==null||receipt.ExecutingPlayerId!=player||!allowed(node)||!ItemMovementV4.TryValidateState(state,node,out _)||receipt.Items.Concat(receipt.LifecycleItems).Any(f=>!visibleIds.Contains(f.ItemId)&&!visibleZones.Contains(f.From.ZoneId)))continue;
                var rows=receipt.Items.Concat(receipt.LifecycleItems).Select(f=>O(E("itemId",S(f.ItemId)),E("fromZoneId",S(f.From.ZoneId)),E("fromSlotId",S(f.From.SlotId)),E("toZoneId",S(f.To.ZoneId)),E("toSlotId",S(f.To.SlotId)),E("originalOwner",S(LuaQuerySnapshotBuilder.Subject(f.Owner))),E("controllerBefore",S(LuaQuerySnapshotBuilder.Subject(f.Controller))),E("controllerAfter",S(LuaQuerySnapshotBuilder.Subject(f.ControllerAfter))))).ToList();
                receipts.Add(O(E("receiptId",S(receipt.ReceiptId)),E("items",LuaSnapshotValue.Array(rows))));
            }
            return O(E("version",S(Version)),E("stateRevision",LuaSnapshotValue.Integer(state.EffectRuntime.StateRevision)),E("authorizedPlayerId",S("p"+player)),E("receipts",LuaSnapshotValue.Array(receipts)));
        }
        private static string Anchor(GameState state, string id)
        {
            var seen = new HashSet<string>(); string last = "";
            while (!string.IsNullOrEmpty(id) && seen.Add(id))
            {
                var node = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == id); if (node == null) return ""; last = id;
                if (node.PersistentSourceFacts != null || node.EffectTypeId == EntrepreneurFlow.EntryTypeId || state.EffectRuntime.AbilitySourceUses.Any(u => u.OwnerEffectId == id) ||
                    state.EffectRuntime.AbilityActionInvocations.Any(a => a.OwnerEffectId == id) || CleanupSourceService.Invocations(state).Any(i => i.TargetEffectId == id) || state.EffectRuntime.CleanupObligations.Any(o => o.InvokeEffectId == id)) return id;
                id = node.ParentEffectId;
            }
            return last;
        }
        private static LuaSnapshotEntry E(string key, LuaSnapshotValue value) => new LuaSnapshotEntry(key, value);
        private static LuaSnapshotValue S(string value) => LuaSnapshotValue.String(value);
        private static LuaSnapshotValue O(params LuaSnapshotEntry[] entries) => LuaSnapshotValue.Object(entries);
    }
    public static partial class SourceFactsLuaCatalog
    {
        // 只接受本次真实Host调用/完成事件身份；不从事件payload、可见sourceKey或客户端receiptId推导授权。
        internal static LuaSnapshotValue ReceiptsForInvocation(GameState state, EffectRegistry registry, LuaHandlerDefinition handler, EffectEventHandlerContext context, int player)
        {
            if (context?.OwnerNode == null || !ReferenceEquals(state, context.State) || !state.EffectRuntime.EffectNodes.Any(n => ReferenceEquals(n, context.OwnerNode))) return LuaSnapshotValue.Null;
            AbilitySourceRef source = null;
            if (HostCalls.TryGetValue(context, out var host))
            {
                if (!ReferenceEquals(host.Registry, registry) || !ReferenceEquals(host.State, state) || !ReferenceEquals(host.Call.State, state) || !ReferenceEquals(host.Owner, host.Call.OwnerNode) ||
                    !ReferenceEquals(host.Action, host.Call.Action) || host.HandlerId != handler.Script.HandlerId || host.HandlerHash != handler.Script.ContentHash || host.HandlerVersion != handler.Script.DefinitionVersion ||
                    host.HandlerVersion != registry.GetAbilityHandlerVersion(host.HandlerId) || host.Call.SourceRef == null || !Same(host.Source, host.Call.SourceRef) ||
                    host.Call.SourceRef.AbilityId != handler.AbilityId || host.Call.SourceRef.AuthorizedPlayerId != player || host.Preview ||
                    !registry.TryResolveSourceFactsCall(host.Call, host.HandlerId, host.HandlerHash, out source, out _)) return LuaSnapshotValue.Null;
            }
            else if (handler.Role == EffectHandlerRole.Continuation && context.OwnerNode.ContinuationHandlerId == handler.Script.HandlerId &&
                context.OwnerNode.ContinuationContentHash == handler.Script.ContentHash && handler.Script.DefinitionVersion == context.Registration?.DefinitionVersion &&
                registry.TryGetContinuation(handler.Script.HandlerId, context.Event.EventType, context.OwnerNode.ContinuationContentId,
                    context.OwnerNode.ContinuationContentInstanceId, context.OwnerNode.ContinuationAbilityId, context.OwnerNode.ContinuationDefinitionVersion,
                    context.OwnerNode.ContinuationContentHash, out var continuation) && ReferenceEquals(continuation, context.Registration) &&
                state.EffectRuntime.RuleEvents.Any(e => ReferenceEquals(e, context.Event)) && context.Registration.HandlerId == handler.Script.HandlerId && context.Registration.ContentHash == handler.Script.ContentHash)
            {
                if (!registry.TryResolveAbilityExecutionHandler(state, context.OwnerNode.EffectId, handler.AbilityId, handler.Script.ContentHash, out source, out _) &&
                    !CharacterLifecycleService.TryResolvePersistentSourceFactsContinuation(registry, context, handler.Script.HandlerId, handler.Script.ContentHash, out source)) return CharacterReceiptsForInvocation(state,registry,handler,context,player);
            }
            if (source == null || source.AbilityId != handler.AbilityId || source.AuthorizedPlayerId != player) return LuaSnapshotValue.Null;
            return TransferReceiptLuaCatalog.Create(state, registry, source, context.OwnerNode.EffectId, handler.Script.ContentHash);
        }
    }
}
