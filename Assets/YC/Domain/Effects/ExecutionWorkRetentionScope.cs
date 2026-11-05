using System;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    // 只覆盖一次已审计内置执行及其正常提交。任何未声明的外部状态回调都会使凭据失效；
    // 不改变规则校验、回滚或公开处理器的默认隔离，不跨Run或线程保留工作状态。
    internal sealed class ExecutionWorkRetentionScope : IDisposable
    {
        [ThreadStatic] private static ExecutionWorkRetentionScope current;
        [ThreadStatic] internal static bool RecyclingDisabled;
        [ThreadStatic] internal static bool VerifyUniqueWrites;
        internal static IDisposable VerifyIndependentWrites() => new UniqueWriteVerificationScope();
        private sealed class UniqueWriteVerificationScope:IDisposable
        {
            private readonly bool previous=VerifyUniqueWrites;private bool closed;
            internal UniqueWriteVerificationScope(){VerifyUniqueWrites=true;}
            public void Dispose(){if(closed)return;closed=true;VerifyUniqueWrites=previous;}
        }
        private readonly ExecutionWorkRetentionScope previous;
        private GameState work;
        private GameStateCloneService.JournalPublicationProof journalProof;
        private bool escaped, closed, unverifiedWrites;
        internal bool CanRecycle => !escaped && !closed;
        internal bool CanTrustIndependentWrites => CanRecycle && !unverifiedWrites;
        internal ExecutionWorkRetentionScope(GameState work) : this(work, null, false) { }
        internal ExecutionWorkRetentionScope(GameState work, GameState committed, bool preservesJournalPrefix)
        {
            this.work = work; previous = current; current = this;
            if (preservesJournalPrefix) journalProof = GameStateCloneService.CaptureJournalPublicationProof(work, committed);
        }
        internal static IDisposable BeginJournalPublication(GameState work, GameState committed)
        {
            for (var scope=current;scope!=null;scope=scope.previous)
                if (ReferenceEquals(scope.work,work) && scope.CanTrustIndependentWrites && scope.journalProof.MatchesBeforeCopy(work,committed))
                    return GameStateCloneService.BeginJournalPrefixCopy(scope.journalProof);
            return null;
        }
        internal static void InvalidateAllJournalProofs()
        {
            for(var scope=current;scope!=null;scope=scope.previous)
                scope.journalProof=default;
        }
        internal static IDisposable DisableRecycling() => new DisableScope();
        private sealed class DisableScope : IDisposable
        {
            private readonly bool previous = RecyclingDisabled;
            private bool closed;
            internal DisableScope() { RecyclingDisabled = true; }
            public void Dispose() { if (closed) return; closed = true; RecyclingDisabled = previous; }
        }
        internal static bool IsTracking(GameState work)
        {
            for (var scope = current; scope != null; scope = scope.previous)
                if (ReferenceEquals(scope.work, work) && scope.CanRecycle) return true;
            return false;
        }
        // 不保留工作图只约束引用逃逸，不保证回调不会在图内建立别名。
        // 回调之后可继续复用，但必须重新检查整个图的可变引用独立性。
        internal static void NoteExternalStateCallback(GameState work)
        {
            // 回调即使收到另一份预览，也可能经先前持有的引用改动发布目标。
            InvalidateAllJournalProofs();
            if(work==null)return;
            for(var scope=current;scope!=null;scope=scope.previous)
                if(ReferenceEquals(scope.work,work))scope.unverifiedWrites=true;
        }
        internal static void Expose(GameState work,[System.Runtime.CompilerServices.CallerMemberName] string reason="")
        {
            InvalidateAllJournalProofs();
            for (var scope = current; scope != null; scope = scope.previous)
                if (ReferenceEquals(scope.work, work))
                {
                    if(!scope.escaped) { using var exposure=GameStateCloneService.MeasureDiagnostic("kernel.work_exposed",reason); }
                    scope.escaped = true;
                }
        }
        internal static bool IsAuditedRoundObserver(Action<GameState, EffectNodeRuntimeState> observer, EffectNodeRuntimeState node)
        {
            // 该封闭服务只改当前工作图并调用自身固定的投影/主链构造器；
            // 可能执行外部计分回调的入口已在服务内显式Expose。多播仍不能取得凭据。
            return observer.GetInvocationList().Length == 1 && observer.Target is RoundExecutionService && observer.Method.DeclaringType == typeof(RoundExecutionService) &&
                observer.Method.Name == "OnEffectTerminal";
        }
        internal static bool IsReadOnlyRoundObserver(Action<GameState, EffectNodeRuntimeState> observer, EffectNodeRuntimeState node)
        {
            // OnEffectTerminal在这条封闭分支只读取父节点后返回；另两类入口仍可能投影/构树/计分。
            // 这里只证明该次调用没有写入，不能把“不保留状态”的普通回调视为无别名写入。
            return node != null && node.EffectTypeId != RoundExecutionService.MainlineEffectTypeId &&
                node.EffectTypeId != RouteResourceSelectionEffectExecutor.TypeId && IsAuditedRoundObserver(observer, node);
        }
        public void Dispose()
        {
            if (closed) return;
            closed = true; work = null; journalProof = default;
            if (!ReferenceEquals(current, this)) return;
            var restored = previous;
            while (restored != null && restored.closed) restored = restored.previous;
            current = restored;
        }
    }
}
