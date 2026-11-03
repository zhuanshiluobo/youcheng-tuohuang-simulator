using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    public sealed partial class LuaEffectEventDispatcher
    {
        private static void AttachAllocationResult(LuaFacadeSnapshot snapshot,LuaHandlerDefinition definition,EffectEventHandlerContext context)
        {
            var e=context.Event;var n=context.State.EffectRuntime.EffectNodes.Find(node=>node.EffectId==e.SourceEffectId);
            if(n?.EffectTypeId!=AllocationEffects.TypeId || n.AllocationV1?.Result==null || e.EventType!="EffectCompleted" || e.EventId!=n.CompletedEventId || e.OwnerNodeId!=n.EffectId ||
                !ReferenceEquals(context.OwnerNode,n) || !context.State.EffectRuntime.RuleEvents.Any(actual=>ReferenceEquals(actual,e)) || n.PendingOutcome!=EffectPendingOutcome.Completed || definition.Role!=EffectHandlerRole.Continuation ||
                definition.Script.HandlerId!=n.ContinuationHandlerId || definition.Script.ContentId!=n.ContinuationContentId || definition.Script.AbilityId!=n.ContinuationAbilityId ||
                definition.Script.DefinitionVersion!=n.ContinuationDefinitionVersion || definition.Script.ContentHash!=n.ContinuationContentHash)return;
            if(!context.State.EffectRuntime.ContinuationBindings.Any(b=>b.BindingId==n.ContinuationBindingId && b.SourceEffectId==n.EffectId && b.CompletionEventId==e.EventId &&
                b.HandlerId==definition.Script.HandlerId && b.ContentId==definition.Script.ContentId && b.ContentHash==definition.Script.ContentHash && b.DefinitionVersion==definition.Script.DefinitionVersion))return;
            if(!AllocationEffects.ValidateNode(context.State,n,out var reason))throw new ArgumentException(reason);
            var visible=GameStateViewProjector.ProjectForPlayer(context.State,n.PlayerId).Effects.ToDictionary(v=>v.EffectId,StringComparer.Ordinal);
            var result=GameStateCloneService.DeepClone(n.AllocationV1.Result);
            foreach(var execution in result.Executions)
                foreach(var root in execution.Roots)
                {
                    if(visible.TryGetValue(root.EffectId,out var view))root.NormalizedResult=view.NormalizedResult.Clone();
                    else {root.EffectId="";root.FailureReason="";root.NormalizedResult=NormalizedValue.CreateNull();}
                }
            snapshot.EventPayload.Properties.Add(new LuaSnapshotEntry("allocationResult",AllocationSnapshot(JToken.FromObject(result))));
            snapshot.EventPayload.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));
        }
        // 独立类型投影，不把完整结果转换为受 128 元素上限约束的 NormalizedValue。
        private static LuaSnapshotValue AllocationSnapshot(JToken value)
        {
            if(value is JArray array)return LuaSnapshotValue.Array(array.Select(AllocationSnapshot).ToList());
            if(value is JObject obj)
            {
                var fields=new List<LuaSnapshotEntry>();
                foreach(var p in obj.Properties())
                {
                    string name=char.ToLowerInvariant(p.Name[0])+p.Name.Substring(1);
                    fields.Add(new LuaSnapshotEntry(name,p.Name=="NormalizedResult"?ToLuaSnapshot(p.Value.ToObject<NormalizedValue>()):AllocationSnapshot(p.Value)));
                }
                return LuaSnapshotValue.Object(fields);
            }
            if(value.Type==JTokenType.String)return LuaSnapshotValue.String(value.Value<string>());
            if(value.Type==JTokenType.Integer)return LuaSnapshotValue.Integer(value.Value<long>());
            if(value.Type==JTokenType.Boolean)return LuaSnapshotValue.Boolean(value.Value<bool>());
            return LuaSnapshotValue.Null;
        }
    }
}
