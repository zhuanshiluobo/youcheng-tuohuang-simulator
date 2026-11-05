using System;
using System.Collections;
using System.Collections.Generic;

namespace YC.Domain.State
{
    public static partial class GameStateCloneService
    {
        [ThreadStatic] private static bool verifySchedulingCopies;
        internal static bool IsSchedulingCopyVerificationEnabled => verifySchedulingCopies;
        public static IDisposable VerifySchedulingPublicationCopies() => new SchedulingCopyVerification();
        private sealed class SchedulingCopyVerification : IDisposable
        {
            private readonly bool previous=verifySchedulingCopies;
            private bool closed;
            internal SchedulingCopyVerification(){verifySchedulingCopies=true;}
            public void Dispose(){if(closed)return;closed=true;verifySchedulingCopies=previous;}
        }

        [ThreadStatic] private static HashSet<object> reusableUniquenessReferences;
        private const int MaximumRetainedUniquenessReferences = 1048576;
        [ThreadStatic] private static bool useFullPublicationShapeTraversal;
        public static IDisposable UseFullPublicationShapeTraversalForDiagnostics() => new FullPublicationShapeScope();
        private sealed class FullPublicationShapeScope : IDisposable
        {
            private readonly bool previous = useFullPublicationShapeTraversal;
            private bool closed;
            internal FullPublicationShapeScope() { useFullPublicationShapeTraversal = true; }
            public void Dispose() { if (closed) return; closed = true; useFullPublicationShapeTraversal = previous; }
        }

        private static bool VisitKnownPublicationDto<T>(T value, HashSet<object> seen) where T : class
        {
            if (value == null) return true;
            if (requireNativeItemWorldReferences && !IsNativeItemWorldReference(value.GetType())) return false;
            // NormalizedValue的叶及闲置容器仍按原专门分支完整检查。
            if (value is NormalizedValue || value is NormalizedValueEntry) return VisitPublicationShape(value, seen);
            var plan = KnownDto<T>.Plan;
            if (plan.PublicationReferences == null) return VisitPublicationShape(value, seen);
            return seen.Add(value) && plan.PublicationReferences(value, seen);
        }

        private static bool VisitKnownPublicationList<T>(List<T> values, HashSet<object> seen) where T : class
        {
            for (int i = 0; i < values.Count; i++)
                if (!VisitKnownPublicationDto(values[i], seen)) return false;
            return true;
        }

        private static bool VisitPublicationList<T>(List<T> values, HashSet<object> seen)
        {
            // 调用者已经登记容器本身；普通标量值没有需要独立证明的可变引用。
            if (StateCopyMetadata.For(typeof(T)).IsScalar) return true;
            for (int i = 0; i < values.Count; i++)
                if (!VisitPublicationShape(values[i], seen)) return false;
            return true;
        }

        private static bool VisitPublicationShape(object value, HashSet<object> seen)
        {
            if (value == null) return true;
            if (requireNativeItemWorldReferences && !IsNativeItemWorldReference(value.GetType())) return false;
            // 经字段形状核验的封闭DTO直接遍历全部可变引用；不按Kind忽略闲置集合。
            if (NormalizedShape.Value)
            {
                if (value is NormalizedValue normalized)
                {
                    if (requireNativeItemWorldReferences &&
                        (normalized.Items != null && !IsNativeItemWorldReference(normalized.Items.GetType()) ||
                         normalized.Properties != null && !IsNativeItemWorldReference(normalized.Properties.GetType()))) return false;
                    if (HasNoNormalizedChildren(normalized))
                        return seen.Add(normalized) && (normalized.Items == null || seen.Add(normalized.Items)) &&
                            (normalized.Properties == null || seen.Add(normalized.Properties));
                    return seen.Add(normalized) && VisitPublicationShape(normalized.Items, seen) && VisitPublicationShape(normalized.Properties, seen);
                }
                if (value is NormalizedValueEntry entry)
                    return seen.Add(entry) && VisitPublicationShape(entry.Value, seen);
                if (value is List<NormalizedValue> values && value.GetType() == typeof(List<NormalizedValue>))
                {
                    if (!seen.Add(values)) return false;
                    for (int i = 0; i < values.Count; i++) if (!VisitPublicationShape(values[i], seen)) return false;
                    return true;
                }
                if (value is List<NormalizedValueEntry> properties && value.GetType() == typeof(List<NormalizedValueEntry>))
                {
                    if (!seen.Add(properties)) return false;
                    for (int i = 0; i < properties.Count; i++) if (!VisitPublicationShape(properties[i], seen)) return false;
                    return true;
                }
            }
            var plan = StateCopyMetadata.For(value.GetType());
            if (plan.IsScalar) return true;
            if (value is Array empty && empty.Length == 0) return true;
            if (plan.IsDictionary || !seen.Add(value)) return false;
            if (value is Array array)
            {
                if (plan.IsScalarElement) return true;
                foreach (var child in array) if (!VisitPublicationShape(child, seen)) return false;
                return true;
            }
            if (plan.IsList)
            {
                if (plan.IsScalarElement) return true;
                if (plan.PublicationList != null) return plan.PublicationList(value, seen);
                foreach (var child in (IList)value) if (!VisitPublicationShape(child, seen)) return false;
                return true;
            }
            if (!value.GetType().IsValueType && !plan.IsSerializable) return false;
            if (plan.PublicationReferences != null) return plan.PublicationReferences(value, seen);
            foreach (var field in plan.ReferenceFields) if (!VisitPublicationShape(field.GetValue(value), seen)) return false;
            return true;
        }


        // 每个Run只证明一次：CopyTo对无共享可变引用的目标仍保持该性质。
        // 有别名、环或不受支持的图沿原整图发布，不把私有工作图的别名带入目标。
        internal static bool HasUniqueMutableReferences(GameState state)
        {
            using var diagnostic=MeasureDiagnostic("kernel.publication_shape");
            var seen=reusableUniquenessReferences ?? new HashSet<object>(ReferenceEqualityComparer.Instance);
            reusableUniquenessReferences=null;
            bool Visit(object value)
            {
                if(value==null)return true;
                if(value is NormalizedValue normalized && NormalizedShape.Value && HasNoNormalizedChildren(normalized))
                    return seen.Add(normalized) && (normalized.Items==null || seen.Add(normalized.Items)) &&
                        (normalized.Properties==null || seen.Add(normalized.Properties));
                var plan=StateCopyMetadata.For(value.GetType());
                if(plan.IsScalar)return true;
                if(value is Array empty && empty.Length==0)return true;
                if(plan.IsDictionary || !seen.Add(value))return false;
                if(value is Array array){foreach(var entry in array)if(!Visit(entry))return false;return true;}
                if(plan.IsList){foreach(var entry in (IList)value)if(!Visit(entry))return false;return true;}
                if(!value.GetType().IsValueType && !plan.IsSerializable)return false;
                foreach(var field in plan.ReferenceFields)if(!Visit(field.GetValue(value)))return false;
                return true;
            }
            try
            {
                bool result = useFullPublicationShapeTraversal ? Visit(state) : VisitPublicationShape(state, seen);
                if (!useFullPublicationShapeTraversal && IsSchedulingCopyVerificationEnabled)
                {
                    seen.Clear();
                    if (Visit(state) != result) throw new InvalidOperationException("工作状态的可变引用证明与原完整遍历不一致。");
                }
                return result;
            }
            finally
            {
                int count=seen.Count;
                seen.Clear(); // 清除全部源引用，只复用有限容量的空工作集合。
                if(count<=MaximumRetainedUniquenessReferences && reusableUniquenessReferences==null)
                    reusableUniquenessReferences=seen;
            }
        }

        // 仅CommitStatus的封闭写集：节点状态/序号、同步窗口、提交尾、受保护请求版本、
        // 事件/收据提交序号以及派生分数标记槽位。前置校验和每条RuleCommit均照常执行。
        internal static bool TryPublishSchedulingState(GameState target, GameState source, string nodeId)
        {
            var to=target.EffectRuntime;var from=source.EffectRuntime;
            if(to==null || from==null || target.ItemZones?.Items==null || source.ItemZones?.Items==null ||
                to.SchemaVersion!=EffectRuntimeState.CurrentSchemaVersion || from.SchemaVersion!=to.SchemaVersion ||
                to.Status!=EffectRuntimeStatus.Active || from.Status!=EffectRuntimeStatus.Active ||
                from.StateRevision!=(long)to.StateRevision+1 || from.NextCommitSequence!=(long)to.NextCommitSequence+1 ||
                from.StepCount!=(long)to.StepCount+1 || from.Journal.Count!=(long)to.Journal.Count+1 ||
                from.EffectNodes.Count!=to.EffectNodes.Count || from.MainNodes.Count!=to.MainNodes.Count ||
                from.Blockers.Count!=to.Blockers.Count || from.InteractionRequests.Count!=to.InteractionRequests.Count ||
                from.RuleEvents.Count!=to.RuleEvents.Count || from.DispatchReceipts.Count!=to.DispatchReceipts.Count ||
                target.ItemZones.Items.Count!=source.ItemZones.Items.Count)return false;
            var old=to.EffectNodes.Find(n=>n.EffectId==nodeId);var next=from.EffectNodes.Find(n=>n.EffectId==nodeId);
            if(old==null || next==null || !(old.Status==EffectNodeStatus.Created && next.Status==EffectNodeStatus.Ready ||
                old.Status==EffectNodeStatus.Ready && next.Status==EffectNodeStatus.Running))return false;
            for(int i=0;i<to.InteractionRequests.Count;i++)if(to.InteractionRequests[i].GetStableInteractionId()!=from.InteractionRequests[i].GetStableInteractionId())return false;
            for(int i=0;i<to.RuleEvents.Count;i++)if(to.RuleEvents[i].EventId!=from.RuleEvents[i].EventId)return false;
            for(int i=0;i<to.DispatchReceipts.Count;i++)if(to.DispatchReceipts[i].ReceiptId!=from.DispatchReceipts[i].ReceiptId)return false;
            for(int i=0;i<target.ItemZones.Items.Count;i++)if(target.ItemZones.Items[i].ItemId!=source.ItemZones.Items[i].ItemId)return false;
            using var timing=new CloneTimingOperation("handler.copy.status",true);
            GameState reference=null;
            if(verifySchedulingCopies){reference=DeepClone(target);CopyTo(reference,source);}
            var tail=from.Journal[from.Journal.Count-1].Clone();
            var visited=RentComparisonPairs();
            try
            {
                to.OperationWindows=(List<OperationWindowState>)CopyValueInto(to.OperationWindows,from.OperationWindows,typeof(List<OperationWindowState>),
                    new DiagnosticPath("GameState").AtField("EffectRuntime").AtField("OperationWindows"),visited,new HashSet<object>(ReferenceEqualityComparer.Instance));
            }
            finally{ReturnComparisonPairs(visited);}
            old.Status=next.Status;old.LastCommitSequence=next.LastCommitSequence;
            to.OperationWindowSchemaVersion=from.OperationWindowSchemaVersion;to.NextOperationWindowOrdinal=from.NextOperationWindowOrdinal;
            to.StepCount=from.StepCount;to.StateRevision=from.StateRevision;to.NextCommitSequence=from.NextCommitSequence;
            for(int i=0;i<to.InteractionRequests.Count;i++)to.InteractionRequests[i].StateRevision=from.InteractionRequests[i].StateRevision;
            for(int i=0;i<to.RuleEvents.Count;i++){to.RuleEvents[i].StateRevision=from.RuleEvents[i].StateRevision;to.RuleEvents[i].CommitSequence=from.RuleEvents[i].CommitSequence;}
            for(int i=0;i<to.DispatchReceipts.Count;i++)to.DispatchReceipts[i].CommitSequence=from.DispatchReceipts[i].CommitSequence;
            for(int i=0;i<target.ItemZones.Items.Count;i++)target.ItemZones.Items[i].SlotId=source.ItemZones.Items[i].SlotId;
            to.Journal.Add(tail);
            YC.Domain.Items.ItemZoneLegacyProjection.Bind(target);
            if(reference!=null && !AreEquivalent(target,reference))throw new InvalidOperationException("纯调度发布与原完整复制结果不一致。");
            return true;
        }
    }
}
