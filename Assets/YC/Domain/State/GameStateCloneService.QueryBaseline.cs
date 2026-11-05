using System;
using System.Collections.Generic;

namespace YC.Domain.State
{
    public static partial class GameStateCloneService
    {
        // 专用集合仅随这一次私有递归传递。构造器或诊断回调嵌套调用公开DeepClone时，
        // 新调用持有自己的集合，不会得到紧凑基线的隐藏容器。
        private sealed class QueryBaselineReferences : HashSet<object>
        { internal QueryBaselineReferences() : base(ReferenceEqualityComparer.Instance) { } }
        private static bool IsCompactQueryBaseline(HashSet<object> active) => active is QueryBaselineReferences;
        private static class QueryBaselineEmptyList<T> { internal static readonly List<T> Value = new List<T>(); }
        [ThreadStatic] private static bool fullReadOnlyQueryBaselines;
        public static IDisposable UseFullReadOnlyQueryBaselinesForDiagnostics() => new FullQueryBaselineScope();
        private sealed class FullQueryBaselineScope : IDisposable
        {
            private readonly bool previous = fullReadOnlyQueryBaselines;
            private bool closed;
            internal FullQueryBaselineScope() { fullReadOnlyQueryBaselines = true; }
            public void Dispose() { if (closed) return; closed = true; fullReadOnlyQueryBaselines = previous; }
        }
        internal static ReadOnlyQueryBaseline CaptureReadOnlyQueryBaseline(GameState source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (fullReadOnlyQueryBaselines) return new ReadOnlyQueryBaseline(DeepClone(source));
            using var timing = new CloneTimingOperation("handler.readonly_baseline", true);
            var active = new QueryBaselineReferences();
            // 与原DeepClone相同地规范化兼容字段；Bind默认不清空或写入任何List，
            // 只绑定本私有副本的投影并重置兼容标量。基线绝不发布或交给处理器。
            var copy = (GameState)CloneValue(source, typeof(GameState), new DiagnosticPath(nameof(GameState)), active);
            YC.Domain.Items.ItemZoneLegacyProjection.Bind(copy);
            return new ReadOnlyQueryBaseline(copy);
        }
        // 基线永不交给处理器、发布器或调用者读取；精确空List可共用隐藏容器，
        // 原图和工作图的每个可变容器仍保持独立。非空、null、派生列表等语义全部保留。
        internal sealed class ReadOnlyQueryBaseline : IDisposable
        {
            private GameState snapshot;
            internal ReadOnlyQueryBaseline(GameState state) { snapshot = state; }
            internal bool HasIdenticalFieldsTo(GameState current) => HaveIdenticalReadOnlyFields(RequireSnapshot(), current);
            internal bool HasEquivalentFieldsTo(GameState current) => AreEquivalent(RequireSnapshot(), current);
            internal string ComputeRuleDigest() => RuleStateDigestService.Compute(RequireSnapshot());
            private GameState RequireSnapshot() => snapshot ?? throw new ObjectDisposedException(nameof(ReadOnlyQueryBaseline));
            public void Dispose() { snapshot = null; }
        }
    }
}
