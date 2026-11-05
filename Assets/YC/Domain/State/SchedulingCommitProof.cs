using System;
using System.Collections.Generic;

namespace YC.Domain.State
{
    // 仅由同一同步Run中刚完整校验或沿同一封闭路径发布且无外部引用的独占图产生；只覆盖紧邻的一个
    // Created->Ready或Ready->Running提交。不能缓存到命令、帧或公开状态中。
    internal readonly struct SchedulingCommitProof
    {
        private readonly long nextWindowOrdinal;
        internal EffectNodeRuntimeState ChangedNode => node;
        internal bool NewlyReady => previousStatus == EffectNodeStatus.Created;
        private readonly GameState state;
        private readonly EffectRuntimeState runtime;
        private readonly EffectNodeRuntimeState node;
        private readonly EffectNodeStatus previousStatus;
        private readonly int revision, sequence, journalCount, steps, nodeIndex, nodeCount;
        private readonly List<EffectNodeRuntimeState> nodes;
        private readonly List<RuleJournalEntry> journal;
        private readonly int mainCount, blockerCount, requestCount, candidateCount, eventCount, receiptCount, bindingCount;
        internal SchedulingCommitProof(GameState state, EffectNodeRuntimeState node, int index)
        {
            this.state=state;runtime=state.EffectRuntime;this.node=node;nodeIndex=index;previousStatus=node.Status;
            nextWindowOrdinal=runtime.NextOperationWindowOrdinal;
            revision=runtime.StateRevision;sequence=runtime.NextCommitSequence;journal=runtime.Journal;journalCount=journal.Count;
            steps=runtime.StepCount;nodes=runtime.EffectNodes;nodeCount=nodes.Count;mainCount=runtime.MainNodes.Count;blockerCount=runtime.Blockers.Count;
            requestCount=runtime.InteractionRequests.Count;candidateCount=runtime.CandidateResolutions.Count;
            eventCount=runtime.RuleEvents.Count;receiptCount=runtime.DispatchReceipts.Count;bindingCount=runtime.ContinuationBindings.Count;
        }
        internal bool Matches(GameState current, bool stepAdvanced=true)
        {
            return ReferenceEquals(state,current) && ReferenceEquals(runtime,current.EffectRuntime) &&
                runtime.SchemaVersion==EffectRuntimeState.CurrentSchemaVersion && runtime.Status==EffectRuntimeStatus.Active &&
                runtime.StateRevision==revision && runtime.NextCommitSequence==sequence && runtime.StepCount==(long)steps+(stepAdvanced?1:0) &&
                runtime.NextOperationWindowOrdinal>=nextWindowOrdinal &&
                ReferenceEquals(journal,runtime.Journal) && journal.Count==journalCount &&
                ReferenceEquals(nodes,runtime.EffectNodes) && nodes.Count==nodeCount && nodeIndex>=0 && nodeIndex<nodes.Count && ReferenceEquals(nodes[nodeIndex],node) &&
                (previousStatus==EffectNodeStatus.Created && node.Status==EffectNodeStatus.Ready ||
                 previousStatus==EffectNodeStatus.Ready && node.Status==EffectNodeStatus.Running) &&
                runtime.MainNodes.Count==mainCount && runtime.Blockers.Count==blockerCount && runtime.InteractionRequests.Count==requestCount &&
                runtime.CandidateResolutions.Count==candidateCount && runtime.RuleEvents.Count==eventCount &&
                runtime.DispatchReceipts.Count==receiptCount && runtime.ContinuationBindings.Count==bindingCount;
        }
    }
    public static partial class GameStateCloneService
    {
        [ThreadStatic] internal static bool FullSchedulingValidationRequested;
        public static IDisposable UseFullSchedulingValidationForDiagnostics() => new FullSchedulingValidationScope();
        private sealed class FullSchedulingValidationScope : IDisposable
        {
            private readonly bool previous=FullSchedulingValidationRequested;
            private bool closed;
            internal FullSchedulingValidationScope(){FullSchedulingValidationRequested=true;}
            public void Dispose(){if(closed)return;closed=true;FullSchedulingValidationRequested=previous;}
        }
    }
    public sealed partial class EffectRuntimeState
    {
        // 仅与刚通过Matches的闭合纯状态凭据配对：历史上下文、请求、响应及调度组未改。
        // 窗口同步只改窗口表，完整检查该表和当前命令上下文；新Ready只重验自己的父屏障。
        internal bool TryValidateSchedulingStatusDelta(SchedulingCommitProof proof,out string reason)
        {
            if(SchemaVersion!=CurrentSchemaVersion || Status!=EffectRuntimeStatus.Active || StateRevision<0 || NextCommitSequence<1 || StepCount<0)
            { reason="运行状态 revision、nextCommitSequence 或 stepCount 无效。";return false; }
            if(!YC.Domain.Rules.OperationWindowService.TryValidateSchedulingWindows(this,out reason))return false;
            if(proof.NewlyReady)
            {
                var changed=proof.ChangedNode;
                var parent=EffectNodes.Find(n=>n.EffectId==changed.ParentEffectId);
                if(parent!=null && parent.ChildSchedulingGroups.Count>0 &&
                    !IsChildSchedulingEligible(parent,parent.ChildEffectIds.IndexOf(changed.EffectId)))
                {reason="子节点越过未完成的顺序屏障。";return false;}
            }
            using var sample=GameStateCloneService.MeasureDiagnostic("kernel.validation.status_dependencies_delta");
            reason=string.Empty;return true;
        }
    }
}
