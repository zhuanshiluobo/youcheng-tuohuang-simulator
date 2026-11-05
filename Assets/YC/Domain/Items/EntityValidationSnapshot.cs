using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Items
{
    // 只在一次同步规则推进内保存一个私有、独立的有效输入快照，退出时释放。
    // 不凭revision或原图引用复用；实体校验读取的全部输入字段逐字段相同，且本校验读取的历史输入相同才命中。
    internal sealed class EntityValidationSnapshot : IDisposable
    {
        [ThreadStatic] private static EntityValidationSnapshot current;
        [ThreadStatic] private static bool full, verify;
        private readonly EntityValidationSnapshot previous;
        private GameState world;
        private List<InteractionRequest> requests;
        private readonly HashSet<string> selectionIds=new HashSet<string>(StringComparer.Ordinal);
        private readonly List<(string id, EffectNodeStatus status)> stagingOwners = new List<(string, EffectNodeStatus)>();
        private bool hasRuntime, closed;
        private EntityValidationSnapshot() { previous=current; current=this; }
        internal static IDisposable Begin() => new EntityValidationSnapshot();
        public static IDisposable UseFullForDiagnostics() => new DiagnosticScope(true,false);
        public static IDisposable VerifyForDiagnostics() => new DiagnosticScope(false,true);
        private sealed class DiagnosticScope : IDisposable
        {
            private readonly bool priorFull=full,priorVerify=verify;
            private bool closed;
            internal DiagnosticScope(bool f,bool v){full=f;verify=v;}
            public void Dispose(){if(closed)return;closed=true;full=priorFull;verify=priorVerify;}
        }
        internal static bool VerificationRequested => verify;
        internal static bool TryReuse(GameState state)
        {
            var scope=current;
            if(full || scope?.world==null || state==null || state.GetType()!=typeof(GameState))return false;
            using var comparison=GameStateCloneService.MeasureDiagnostic("entity.validation.compare");
            if(!GameStateCloneService.HaveIdenticalEntityWorld(scope.world,state))return false;
            if(scope.hasRuntime!=(state.EffectRuntime!=null))return false;
            if(scope.hasRuntime)
            {
                if(scope.selectionIds.Count>0)
                {
                    if(state.EffectRuntime.InteractionRequests==null)return false;
                    int selected=0;
                    foreach(var request in state.EffectRuntime.InteractionRequests)
                    {
                        if(request==null)return false;
                        if(!scope.selectionIds.Contains(request.GetStableInteractionId()))continue;
                        if(selected>=scope.requests.Count || !GameStateCloneService.HaveIdenticalEntitySelectionRequest(scope.requests[selected++],request))
                        {using var changed=GameStateCloneService.MeasureDiagnostic("entity.validation.different_selection_request");return false;}
                    }
                    if(selected!=scope.requests.Count)return false;
                }
                if(scope.stagingOwners.Count>0)
                {
                    var nodes=IndexNodes(state.EffectRuntime);
                    if(nodes==null)return false;
                    foreach(var owner in scope.stagingOwners)
                        if(!nodes.TryGetValue(owner.id,out var node) || node.Status!=owner.status)return false;
                }
            }
            using var reused=GameStateCloneService.MeasureDiagnostic("entity.validation.reused");
            return true;
        }
        internal static void Remember(GameState state)
        {
            var scope=current;
            if(full || scope==null || state==null || state.GetType()!=typeof(GameState))return;
            scope.world=null;scope.requests=null;scope.stagingOwners.Clear();scope.selectionIds.Clear();
            using var snapshot=GameStateCloneService.MeasureDiagnostic("entity.validation.snapshot");
            try
            {
                var world=GameStateCloneService.CloneEntityWorld(state);
                scope.hasRuntime=state.EffectRuntime!=null;
                if(scope.hasRuntime)
                {
                    foreach(var item in world.ItemZones.Items)
                    {
                        var receipt=YC.Domain.Effects.ItemTransferEffectExecutor.Value(item.LocalState,"sourceSelectionReceipt");
                        if(receipt!=null)scope.selectionIds.Add(YC.Domain.Effects.ItemTransferEffectExecutor.Text(receipt,"requestId"));
                    }
                    scope.requests=new List<InteractionRequest>();
                    if(scope.selectionIds.Count>0)
                        foreach(var request in state.EffectRuntime.InteractionRequests)
                            if(scope.selectionIds.Contains(request.GetStableInteractionId()))scope.requests.Add(GameStateCloneService.DeepClone(request));
                    Dictionary<string,EffectNodeRuntimeState> nodes=null;
                    foreach(var zone in world.ItemZones.Zones)
                        if(zone.TemplateId=="core.zone.effect_staging")
                        {
                            if(nodes==null)nodes=IndexNodes(state.EffectRuntime);
                            if(nodes==null || !nodes.TryGetValue(zone.OwnerEffectId,out var owner))return;
                            scope.stagingOwners.Add((owner.EffectId,owner.Status));
                        }
                }
                scope.world=world;
            }
            catch(InvalidOperationException)
            {
                // 额外世界字段若不支持独立复制，保持原校验的返回值，且不保留任何凭据。
                scope.world=null;scope.requests=null;scope.stagingOwners.Clear();scope.selectionIds.Clear();
            }
        }
        private static Dictionary<string,EffectNodeRuntimeState> IndexNodes(EffectRuntimeState runtime)
        {
            if(runtime.EffectNodes==null)return null;
            var result=new Dictionary<string,EffectNodeRuntimeState>(StringComparer.Ordinal);
            foreach(var node in runtime.EffectNodes)
                if(node==null || node.EffectId==null || result.ContainsKey(node.EffectId))return null;
                else result.Add(node.EffectId,node);
            return result;
        }
        public void Dispose()
        {
            if(closed)return;closed=true;world=null;requests=null;stagingOwners.Clear();selectionIds.Clear();
            if(ReferenceEquals(current,this))
            {var restored=previous;while(restored!=null && restored.closed)restored=restored.previous;current=restored;}
        }
    }
}
