using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static partial class UsageTracks
    {
        [ThreadStatic] private static bool fullCleanupObligationTraversal, verifyCleanupObligationTraversal;
        public static IDisposable UseFullCleanupObligationTraversalForDiagnostics() => new CleanupTraversalScope(true, false);
        public static IDisposable VerifyCleanupObligationTraversalForDiagnostics() => new CleanupTraversalScope(false, true);
        private sealed class CleanupTraversalScope : IDisposable
        {
            private readonly bool oldFull=fullCleanupObligationTraversal, oldVerify=verifyCleanupObligationTraversal;
            private bool closed;
            internal CleanupTraversalScope(bool full, bool verify) { fullCleanupObligationTraversal=full; verifyCleanupObligationTraversal=verify; }
            public void Dispose() { if(closed)return; closed=true; fullCleanupObligationTraversal=oldFull; verifyCleanupObligationTraversal=oldVerify; }
        }
        private static bool CleanupObligationsComplete(EffectRuntimeState runtime, UsageTrackResetProgress progress)
        {
            if(fullCleanupObligationTraversal)return CleanupObligationsCompleteReference(runtime,progress);
            var expected=verifyCleanupObligationTraversal ? GameStateCloneService.DeepClone(progress) : null;
            bool result=CleanupObligationsCompleteIndexed(runtime,progress);
            if(expected!=null)
            {
                bool original=CleanupObligationsCompleteReference(runtime,expected);
                if(original!=result || !GameStateCloneService.AreEquivalentPersistentValues(expected,progress,typeof(UsageTrackResetProgress)))
                    throw new InvalidOperationException("收尾义务索引与原父链遍历结果或完整进度字段不一致。");
            }
            return result;
        }
        private static bool CleanupObligationsCompleteReference(EffectRuntimeState runtime, UsageTrackResetProgress progress)
        {
            var roots=new HashSet<string>(progress.RequiredObligationIds);
            bool IsObligation(EffectNodeRuntimeState n)
            {
                var visited=new HashSet<string>();
                while(n!=null && visited.Add(n.EffectId)) { if(roots.Contains(n.EffectId))return true; n=runtime.EffectNodes.Find(p=>p.EffectId==n.ParentEffectId); }
                return false;
            }
            foreach(var node in runtime.EffectNodes.Where(IsObligation).ToList())
                if(!progress.RequiredObligationIds.Contains(node.EffectId))progress.RequiredObligationIds.Add(node.EffectId);
            return !progress.RequiredObligationIds.Any(id=>!runtime.EffectNodes.Any(n=>n.EffectId==id && (n.Status==EffectNodeStatus.Completed || n.Status==EffectNodeStatus.Failed)));
        }
        private static bool CleanupObligationsCompleteIndexed(EffectRuntimeState runtime, UsageTrackResetProgress progress)
        {
            // 只借用本次调用的节点；不跨提交保存索引。异常输入回原算法，保留重复ID/空节点的原行为。
            if(runtime.EffectNodes==null)return CleanupObligationsCompleteReference(runtime,progress);
            var nodes=new Dictionary<string,EffectNodeRuntimeState>(StringComparer.Ordinal);
            foreach(var node in runtime.EffectNodes)
            {
                if(node==null || string.IsNullOrEmpty(node.EffectId) || nodes.ContainsKey(node.EffectId))
                    return CleanupObligationsCompleteReference(runtime,progress);
                nodes.Add(node.EffectId,node);
            }
            using var sample=GameStateCloneService.MeasureDiagnostic("usage.reset.obligation_index");
            var roots=new HashSet<string>(progress.RequiredObligationIds,StringComparer.Ordinal);
            var required=new HashSet<string>(progress.RequiredObligationIds,StringComparer.Ordinal);
            var memo=new Dictionary<string,bool>(StringComparer.Ordinal);
            var path=new List<string>(); var active=new HashSet<string>(StringComparer.Ordinal);
            foreach(var origin in runtime.EffectNodes)
            {
                path.Clear(); active.Clear(); var cursor=origin; bool reachesRoot=false;
                while(cursor!=null)
                {
                    if(roots.Contains(cursor.EffectId)) { reachesRoot=true;break; }
                    if(memo.TryGetValue(cursor.EffectId,out reachesRoot))break;
                    if(!active.Add(cursor.EffectId))break;
                    path.Add(cursor.EffectId);
                    if(cursor.ParentEffectId==null || !nodes.TryGetValue(cursor.ParentEffectId,out cursor))cursor=null;
                }
                foreach(string id in path)memo[id]=reachesRoot;
                if(reachesRoot && required.Add(origin.EffectId))progress.RequiredObligationIds.Add(origin.EffectId);
            }
            foreach(string id in progress.RequiredObligationIds)
                if(id==null || !nodes.TryGetValue(id,out var node) || node.Status!=EffectNodeStatus.Completed && node.Status!=EffectNodeStatus.Failed)return false;
            return true;
        }
    }
}
