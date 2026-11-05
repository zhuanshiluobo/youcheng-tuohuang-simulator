using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    // 只在一次无回调的后代筛选中借用节点，不跨提交、命令或帧保存索引。
    internal static class EffectDescendantQuery
    {
        [ThreadStatic] private static bool fullTraversal, verifyTraversal;
        internal static IDisposable Diagnose(bool full, bool verify) => new DiagnosticScope(full, verify);
        private sealed class DiagnosticScope : IDisposable
        {
            private readonly bool previousFull = fullTraversal, previousVerify = verifyTraversal;
            private bool closed;
            internal DiagnosticScope(bool full, bool verify) { fullTraversal = full; verifyTraversal = verify; }
            public void Dispose()
            {
                if (closed) return;
                closed = true; fullTraversal = previousFull; verifyTraversal = previousVerify;
            }
        }

        internal static List<EffectNodeRuntimeState> Select(GameState state, string ancestor,
            bool completedOnly, bool excludeRoot, bool singleParent)
        {
            if (fullTraversal) return Reference(state, ancestor, completedOnly, excludeRoot, singleParent);
            var source = state.EffectRuntime.EffectNodes;
            var nodes = new Dictionary<string, EffectNodeRuntimeState>(StringComparer.Ordinal);
            if (source == null) return Reference(state, ancestor, completedOnly, excludeRoot, singleParent);
            foreach (var node in source)
            {
                // 重复ID在两个原调用点分别使用Find/Single；不替它们选择一种新行为。
                if (node == null || string.IsNullOrEmpty(node.EffectId) || nodes.ContainsKey(node.EffectId))
                    return Reference(state, ancestor, completedOnly, excludeRoot, singleParent);
                nodes.Add(node.EffectId, node);
            }
            using var sample = GameStateCloneService.MeasureDiagnostic("ancestry.filter.index");
            using var callerSample = GameStateCloneService.MeasureDiagnostic(singleParent ? "cleanup.obligation.descendants" : "scoring.archive.descendants");
            var memo = new Dictionary<string, bool>(StringComparer.Ordinal);
            var path = new List<string>();
            var active = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<EffectNodeRuntimeState>();
            foreach (var origin in source)
            {
                if ((excludeRoot && origin.EffectId == ancestor) || (completedOnly && origin.Status != EffectNodeStatus.Completed)) continue;
                string cursor = origin.EffectId;
                bool reachesRoot = false;
                path.Clear(); active.Clear();
                while (!string.IsNullOrEmpty(cursor))
                {
                    // 原算法先检查目标ID，再找节点；缺失父节点仍可能等于目标ID。
                    if (cursor == ancestor) { reachesRoot = true; break; }
                    if (memo.TryGetValue(cursor, out reachesRoot)) break;
                    if (!active.Add(cursor)) break;
                    path.Add(cursor);
                    cursor = nodes.TryGetValue(cursor, out var node) ? node.ParentEffectId : null;
                }
                foreach (string id in path) memo[id] = reachesRoot;
                if (reachesRoot) result.Add(origin);
            }
            if (verifyTraversal)
            {
                var expected = Reference(state, ancestor, completedOnly, excludeRoot, singleParent);
                if (result.Count != expected.Count || result.Where((node, i) => !ReferenceEquals(node, expected[i])).Any())
                    throw new InvalidOperationException("后代节点索引与原父链筛选的节点或顺序不一致。");
            }
            return result;
        }

        // 调用者已经建立首匹配索引和候选快照。每次筛选重新计算；外部验证器
        // 即使在两条映射之间修改父节点，也不会读到上一条映射的祖先结果。
        internal static List<EffectNodeRuntimeState> SelectFromExistingIndex(
            List<EffectNodeRuntimeState> candidates,Dictionary<string,EffectNodeRuntimeState> nodes,string ancestor)
        {
            bool ReferenceWithin(string id)
            {
                var seen=new HashSet<string>(StringComparer.Ordinal);
                while(!string.IsNullOrEmpty(id) && seen.Add(id))
                {if(id==ancestor)return true;id=nodes.TryGetValue(id,out var node)?node.ParentEffectId:null;}
                return false;
            }
            List<EffectNodeRuntimeState> ReferenceSelection()=>candidates.Where(node=>ReferenceWithin(node.EffectId)).ToList();
            if(fullTraversal)return ReferenceSelection();
            using var sample=GameStateCloneService.MeasureDiagnostic("attachment.descendant_index");
            var memo=new Dictionary<string,bool>(StringComparer.Ordinal);
            var path=new List<string>();var active=new HashSet<string>(StringComparer.Ordinal);
            var result=new List<EffectNodeRuntimeState>();
            foreach(var origin in candidates)
            {
                string cursor=origin.EffectId;bool reaches=false;path.Clear();active.Clear();
                while(!string.IsNullOrEmpty(cursor))
                {
                    if(cursor==ancestor){reaches=true;break;}
                    if(memo.TryGetValue(cursor,out reaches))break;
                    if(!active.Add(cursor))break;
                    path.Add(cursor);cursor=nodes.TryGetValue(cursor,out var node)?node.ParentEffectId:null;
                }
                foreach(string id in path)memo[id]=reaches;
                if(reaches)result.Add(origin);
            }
            if(verifyTraversal)
            {
                var expected=ReferenceSelection();
                if(result.Count!=expected.Count || result.Where((node,i)=>!ReferenceEquals(node,expected[i])).Any())
                    throw new InvalidOperationException("映射放置候选的祖先筛选与原首匹配父链不一致。");
            }
            return result;
        }

        private static List<EffectNodeRuntimeState> Reference(GameState state, string ancestor,
            bool completedOnly, bool excludeRoot, bool singleParent)
        {
            bool Within(string id)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (!string.IsNullOrEmpty(id) && visited.Add(id))
                {
                    if (id == ancestor) return true;
                    id = (singleParent ? state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == id) :
                        state.EffectRuntime.EffectNodes.Find(n => n.EffectId == id))?.ParentEffectId;
                }
                return false;
            }
            return state.EffectRuntime.EffectNodes.Where(n => (!excludeRoot || n.EffectId != ancestor) &&
                (!completedOnly || n.Status == EffectNodeStatus.Completed) && Within(n.EffectId)).ToList();
        }
    }
}
