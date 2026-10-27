using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace YC.Domain.State
{
    /// <summary>
    /// GameState 的唯一深克隆入口。状态 DTO 只允许由可序列化字段和 List
    /// 组成，因此按缓存的字段访问计划遍历完整字段图：新增权威字段会自动进入克隆，
    /// 未受支持的持久化容器会明确失败，而不是静默浅拷贝。
    /// </summary>
    public static class GameStateCloneService
    {
        [ThreadStatic] private static CloneTimingScope currentTimingScope;
        [ThreadStatic] private static int timingOperationDepth;
        // 每线程最多保留一个已清空的遍历工作集合；不保留任何源/目标状态引用。
        // 超大图、同线程嵌套调用各用独立集合，超过上限的集合不进入缓存。
        private const int MaximumRetainedComparisonPairs = 262144;
        [ThreadStatic] private static HashSet<ReferencePair> reusableComparisonPairs;

        private static HashSet<ReferencePair> RentComparisonPairs()
        {
            var pairs = reusableComparisonPairs;
            reusableComparisonPairs = null;
            return pairs ?? new HashSet<ReferencePair>(ReferencePairComparer.Instance);
        }

        private static void ReturnComparisonPairs(HashSet<ReferencePair> pairs)
        {
            int count = pairs.Count;
            pairs.Clear();
            if (count <= MaximumRetainedComparisonPairs && reusableComparisonPairs == null)
                reusableComparisonPairs = pairs;
        }


        /// <summary>仅诊断当前线程动态范围内的顶层 GameState 克隆/复制；不进入持久化状态。</summary>
        public static IDisposable BeginTimingScope(Action<string, long> recordSample)
            => BeginTimingScope(recordSample, null);

        // 默认不启用。诊断宿主可把完整 GameState 的 Clone/Copy 对接到正式 Profiler；
        // Domain 保持无引擎依赖，不把游戏状态交给诊断回调。
        public static IDisposable BeginTimingScope(Action<string, long> recordSample, Action<string, bool> traceSample)
        {
            if (recordSample == null) throw new ArgumentNullException(nameof(recordSample));
            var scope = new CloneTimingScope(recordSample, currentTimingScope, traceSample);
            currentTimingScope = scope;
            return scope;
        }

        private sealed class CloneTimingScope : IDisposable
        {
            private readonly Action<string, long> recordSample;
            private readonly CloneTimingScope previous;
            private readonly Action<string, bool> traceSample;
            private readonly int ownerThreadId;
            private bool disposed;

            public CloneTimingScope(Action<string, long> recordSample, CloneTimingScope previous, Action<string, bool> traceSample)
            {
                this.recordSample = recordSample;
                this.previous = previous;
                this.traceSample = traceSample ?? previous?.traceSample;
                ownerThreadId = Thread.CurrentThread.ManagedThreadId;
            }

            public void Trace(string phase, bool begin)
            {
                try { traceSample?.Invoke(phase, begin); }
                catch (Exception) { }
            }

            public void Record(string phase, long elapsedTicks)
            {
                // 新诊断接收器的异常不能替换原克隆/复制结果或业务异常。
                try { recordSample(phase, elapsedTicks); }
                catch (Exception) { }
            }

            public void Dispose()
            {
                if (disposed || Thread.CurrentThread.ManagedThreadId != ownerThreadId) return;
                disposed = true;
                if (!ReferenceEquals(currentTimingScope, this)) return;
                var restored = previous;
                while (restored != null && restored.disposed) restored = restored.previous;
                currentTimingScope = restored;
            }
        }

        private readonly struct CloneTimingOperation : IDisposable
        {
            private readonly CloneTimingScope scope;
            private readonly string phase;
            private readonly long started;

            public CloneTimingOperation(string phase, bool isGameState)
            {
                scope = isGameState && timingOperationDepth == 0 ? currentTimingScope : null;
                this.phase = scope == null ? null : phase;
                started = scope == null ? 0 : Stopwatch.GetTimestamp();
                if (scope != null)
                {
                    timingOperationDepth++;
                    scope.Trace(this.phase, true);
                }
            }

            public void Dispose()
            {
                if (scope == null) return;
                try
                {
                    scope.Trace(phase, false);
                    scope.Record(phase, Stopwatch.GetTimestamp() - started);
                }
                finally { timingOperationDepth--; }
            }
        }

        public static GameState DeepClone(GameState source)
        {
            return DeepClone<GameState>(source);
        }

        public static GameState Clone(GameState source)
        {
            return DeepClone(source);
        }

        public static T DeepClone<T>(T source) where T : class
        {
            using var timing = new CloneTimingOperation("handler.clone", typeof(T) == typeof(GameState) || source is GameState);
            if (source == null)
            {
                return null;
            }

            var result = (T)CloneValue(source, typeof(T), new DiagnosticPath(typeof(T).Name), new HashSet<object>(ReferenceEqualityComparer.Instance));
            if (result is GameState state) YC.Domain.Items.ItemZoneLegacyProjection.Bind(state);
            return result;
        }

        public static void CopyTo(GameState target, GameState source)
        {
            using var timing = new CloneTimingOperation("handler.copy", true);
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (ReferenceEquals(target, source)) return;

            // 成功发布时保留旧业务代码可能持有的 PlayerState、MapRuntimeState
            // 等引用；新加入的集合元素仍会被克隆，避免把工作副本重新暴露给外部。
            // 同次 CopyTo 的新增分支复用循环检查集合，CloneValue 在 finally 中逐层移除；
            // 集合只活到本次调用结束，不缓存状态，也不改变兄弟别名分别深克隆的语义。
            var visited = RentComparisonPairs();
            try
            {
                CopyObjectFieldsInto(
                    target,
                    source,
                    new DiagnosticPath(typeof(GameState).Name),
                    visited,
                    new HashSet<object>(ReferenceEqualityComparer.Instance));
                YC.Domain.Items.ItemZoneLegacyProjection.Bind(target);
            }
            finally { ReturnComparisonPairs(visited); }
        }

        public static bool AreEquivalent(GameState left, GameState right)
        {
            return AreEquivalentInternal(left, right, false);
        }

        public static bool AreEquivalentIgnoringEffectRuntime(GameState left, GameState right)
        {
            return AreEquivalentInternal(left, right, true);
        }

        private static object CloneValue(
            object value,
            Type declaredType,
            DiagnosticPath path,
            HashSet<object> activeReferences)
        {
            if (value == null)
            {
                return null;
            }

            Type type = value.GetType();
            var metadata = StateCopyMetadata.For(type);
            if (metadata.IsScalar)
            {
                return value;
            }

            if (type.IsArray)
            {
                return CloneArray((Array)value, metadata, path, activeReferences);
            }

            if (metadata.IsList)
            {
                return CloneList((IList)value, metadata, path, activeReferences);
            }

            if (metadata.IsDictionary)
            {
                throw new InvalidOperationException(
                    "持久化状态禁止 Dictionary：" + path + "。请改用有序 List DTO。");
            }

            if (type.IsValueType)
            {
                return CloneObjectFields(value, metadata, path, activeReferences, false);
            }

            if (!metadata.IsSerializable)
            {
                throw new InvalidOperationException(
                    "状态字段必须是 [Serializable] DTO：" + path + " (" + type.FullName + ")。");
            }

            return CloneObjectFields(value, metadata, path, activeReferences, true);
        }

        private static object CloneArray(Array source, StateCopyMetadata metadata, DiagnosticPath path, HashSet<object> activeReferences)
        {
            EnterReference(source, path, activeReferences);
            try
            {
                if (metadata.IsScalarElement && source.Rank == 1 && source.GetLowerBound(0) == 0)
                    return source.Clone();
                Type elementType = metadata.ElementType;
                Array clone = Array.CreateInstance(elementType, source.Length);
                for (int i = 0; i < source.Length; i++)
                {
                    clone.SetValue(CloneValue(source.GetValue(i), elementType, path.AtIndex(i), activeReferences), i);
                }

                return clone;
            }
            finally
            {
                activeReferences.Remove(source);
            }
        }

        private static object CloneList(
            IList source,
            StateCopyMetadata metadata,
            DiagnosticPath path,
            HashSet<object> activeReferences)
        {
            EnterReference(source, path, activeReferences);
            try
            {
                if (metadata.IsScalarElement && metadata.TryCloneScalarList(source, out object scalarClone))
                    return scalarClone;
                IList clone = (IList)metadata.CreateList(source.Count);
                Type elementType = metadata.ElementType;
                bool scalarElement = metadata.IsScalarElement;
                for (int i = 0; i < source.Count; i++)
                {
                    clone.Add(scalarElement ? source[i] : CloneValue(source[i], elementType, path.AtIndex(i), activeReferences));
                }

                return clone;
            }
            finally
            {
                activeReferences.Remove(source);
            }
        }

        private static object CloneObjectFields(
            object source,
            StateCopyMetadata metadata,
            DiagnosticPath path,
            HashSet<object> activeReferences,
            bool trackReference)
        {
            if (trackReference)
            {
                EnterReference(source, path, activeReferences);
            }

            try
            {
                object clone = metadata.Create();
                StateCopyField[] fields = metadata.Fields;
                for (int i = 0; i < fields.Length; i++)
                {
                    StateCopyField field = fields[i];
                    if (field.IsScalar)
                    {
                        field.CopyScalar(clone, source);
                        continue;
                    }
                    object fieldValue = field.GetValue(source);
                    field.SetValue(
                        clone,
                        CloneValue(fieldValue, field.FieldType, path.AtField(field.Name), activeReferences));
                }

                return clone;
            }
            finally
            {
                if (trackReference)
                {
                    activeReferences.Remove(source);
                }
            }
        }

        private static void CopyObjectFieldsInto(
            object target,
            object source,
            DiagnosticPath path,
            HashSet<ReferencePair> visited,
            HashSet<object> cloneReferences)
        {
            if (target == null || source == null)
            {
                return;
            }

            if (target.GetType() != source.GetType())
            {
                throw new InvalidOperationException(
                    "状态发布遇到不兼容 DTO 类型：" + path + "。");
            }

            ReferencePair pair = new ReferencePair(target, source);
            if (!visited.Add(pair))
            {
                return;
            }

            StateCopyField[] fields = StateCopyMetadata.For(source.GetType()).Fields;
            for (int i = 0; i < fields.Length; i++)
            {
                StateCopyField field = fields[i];
                if (field.IsScalar)
                {
                    field.CopyScalar(target, source);
                    continue;
                }
                object sourceValue = field.GetValue(source);
                field.SetValue(
                    target,
                    CopyValueInto(
                        field.GetValue(target),
                        sourceValue,
                        field.FieldType,
                        path.AtField(field.Name),
                        visited,
                        cloneReferences));
            }
        }

        private static object CopyValueInto(
            object target,
            object source,
            Type declaredType,
            DiagnosticPath path,
            HashSet<ReferencePair> visited,
            HashSet<object> cloneReferences)
        {
            if (source == null)
            {
                return null;
            }

            Type type = source.GetType();
            var metadata = StateCopyMetadata.For(type);
            if (metadata.IsScalar)
            {
                return source;
            }

            if (type.IsArray)
            {
                Array sourceArray = (Array)source;
                Array targetArray = target as Array;
                if (targetArray == null || targetArray.GetType() != type || targetArray.Length != sourceArray.Length)
                {
                    return CloneValue(source, declaredType, path, cloneReferences);
                }

                ReferencePair pair = new ReferencePair(targetArray, sourceArray);
                if (visited.Add(pair))
                {
                    if (metadata.IsScalarElement && sourceArray.Rank == 1 && sourceArray.GetLowerBound(0) == 0)
                    {
                        Array.Copy(sourceArray, targetArray, sourceArray.Length);
                        return targetArray;
                    }
                    Type elementType = metadata.ElementType;
                    for (int i = 0; i < sourceArray.Length; i++)
                    {
                        targetArray.SetValue(
                            CopyValueInto(
                                targetArray.GetValue(i),
                                sourceArray.GetValue(i),
                                elementType,
                                path.AtIndex(i),
                                visited,
                                cloneReferences),
                            i);
                    }
                }

                return targetArray;
            }

            if (metadata.IsList)
            {
                IList sourceList = (IList)source;
                IList targetList = target as IList;
                if (targetList == null || targetList.GetType() != type)
                {
                    return CloneValue(source, declaredType, path, cloneReferences);
                }

                ReferencePair pair = new ReferencePair(targetList, sourceList);
                if (!visited.Add(pair))
                {
                    return targetList;
                }

                if (metadata.IsScalarElement && metadata.TryCopyScalarList(targetList, sourceList))
                    return targetList;
                if (sourceList.Count > targetList.Count) metadata.ReserveList(targetList, sourceList.Count);
                Type elementType = metadata.ElementType;
                bool scalarElement = metadata.IsScalarElement;
                int sharedCount = Math.Min(targetList.Count, sourceList.Count);
                for (int i = 0; i < sharedCount; i++)
                {
                    targetList[i] = scalarElement ? sourceList[i] : CopyValueInto(
                        targetList[i],
                        sourceList[i],
                        elementType,
                        path.AtIndex(i),
                        visited,
                        cloneReferences);
                }

                while (targetList.Count > sourceList.Count)
                {
                    targetList.RemoveAt(targetList.Count - 1);
                }

                for (int i = targetList.Count; i < sourceList.Count; i++)
                {
                    targetList.Add(scalarElement ? sourceList[i] : CopyValueInto(
                        null,
                        sourceList[i],
                        elementType,
                        path.AtIndex(i),
                        visited,
                        cloneReferences));
                }

                return targetList;
            }

            if (metadata.IsDictionary)
            {
                throw new InvalidOperationException(
                    "持久化状态禁止 Dictionary：" + path + "。请改用有序 List DTO。");
            }

            if (type.IsValueType)
            {
                return CloneValue(source, declaredType, path, cloneReferences);
            }

            if (!metadata.IsSerializable)
            {
                throw new InvalidOperationException(
                    "状态字段必须是 [Serializable] DTO：" + path + " (" + type.FullName + ")。");
            }

            if (target != null && target.GetType() == type)
            {
                CopyObjectFieldsInto(target, source, path, visited, cloneReferences);
                return target;
            }

            return CloneValue(source, declaredType, path, cloneReferences);
        }

        private static bool AreEquivalentInternal(GameState left, GameState right, bool ignoreEffectRuntime)
        {
            var visited = RentComparisonPairs();
            try
            {
                return AreEquivalentValue(
                    left,
                    right,
                    typeof(GameState),
                    visited,
                    ignoreEffectRuntime,
                    true);
            }
            finally { ReturnComparisonPairs(visited); }
        }

        private static bool AreEquivalentValue(
            object left,
            object right,
            Type declaredType,
            HashSet<ReferencePair> visited,
            bool ignoreEffectRuntime,
            bool isRoot)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;

            Type leftType = left.GetType();
            Type rightType = right.GetType();
            if (leftType != rightType) return false;
            var metadata = StateCopyMetadata.For(leftType);
            if (metadata.IsScalar)
            {
                return left.Equals(right);
            }

            if (leftType.IsArray)
            {
                Array leftArray = (Array)left;
                Array rightArray = (Array)right;
                if (leftArray.Length != rightArray.Length) return false;
                ReferencePair pair = new ReferencePair(left, right);
                if (!visited.Add(pair)) return true;
                for (int i = 0; i < leftArray.Length; i++)
                {
                    if (!AreEquivalentValue(
                            leftArray.GetValue(i),
                            rightArray.GetValue(i),
                            metadata.ElementType,
                            visited,
                            ignoreEffectRuntime,
                            false)) return false;
                }

                return true;
            }

            if (metadata.IsList)
            {
                IList leftList = (IList)left;
                IList rightList = (IList)right;
                if (leftList.Count != rightList.Count) return false;
                ReferencePair pair = new ReferencePair(left, right);
                if (!visited.Add(pair)) return true;
                Type elementType = metadata.ElementType;
                for (int i = 0; i < leftList.Count; i++)
                {
                    if (!AreEquivalentValue(
                            leftList[i],
                            rightList[i],
                            elementType,
                            visited,
                            ignoreEffectRuntime,
                            false)) return false;
                }

                return true;
            }

            if (metadata.IsDictionary)
            {
                throw new InvalidOperationException("状态比较遇到 Dictionary：" + leftType.FullName + "。");
            }

            ReferencePair objectPair = new ReferencePair(left, right);
            if (!visited.Add(objectPair)) return true;
            StateCopyField[] fields = metadata.Fields;
            for (int i = 0; i < fields.Length; i++)
            {
                if (isRoot && ignoreEffectRuntime && fields[i].Name == "EffectRuntime")
                {
                    continue;
                }

                StateCopyField field = fields[i];
                if (field.IsScalar)
                {
                    if (!field.AreEquivalentScalar(left, right)) return false;
                    continue;
                }
                if (!AreEquivalentValue(
                        field.GetValue(left),
                        field.GetValue(right),
                        field.FieldType,
                        visited,
                        ignoreEffectRuntime,
                        false)) return false;
            }

            return true;
        }

        private static void EnterReference(object value, DiagnosticPath path, HashSet<object> activeReferences)
        {
            if (!activeReferences.Add(value))
            {
                throw new InvalidOperationException("状态 DTO 不能包含循环引用：" + path + "。");
            }
        }

        // 每次同步克隆/发布独享路径缓冲；只在拒绝时物化文本。
        // 深度随值传递，兄弟分支覆写该深度的段，不跨调用保存状态对象或路径。
        private readonly struct DiagnosticPath
        {
            private readonly string root;
            private readonly List<PathSegment> segments;
            private readonly int depth;

            internal DiagnosticPath(string root)
            {
                this.root = root;
                segments = new List<PathSegment>();
                depth = 0;
            }

            private DiagnosticPath(string root, List<PathSegment> segments, int depth)
            {
                this.root = root;
                this.segments = segments;
                this.depth = depth;
            }

            internal DiagnosticPath AtField(string name) => Append(new PathSegment(name, 0));
            internal DiagnosticPath AtIndex(int index) => Append(new PathSegment(null, index));

            private DiagnosticPath Append(PathSegment segment)
            {
                if (segments.Count == depth) segments.Add(segment);
                else segments[depth] = segment;
                return new DiagnosticPath(root, segments, depth + 1);
            }

            public override string ToString()
            {
                var text = new StringBuilder(root);
                for (int i = 0; i < depth; i++)
                {
                    PathSegment segment = segments[i];
                    if (segment.Field != null) text.Append('.').Append(segment.Field);
                    else text.Append('[').Append(segment.Index).Append(']');
                }
                return text.ToString();
            }

            private readonly struct PathSegment
            {
                internal readonly string Field;
                internal readonly int Index;
                internal PathSegment(string field, int index) { Field = field; Index = index; }
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object left, object right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(object value)
            {
                return RuntimeHelpers.GetHashCode(value);
            }
        }

        // visited 本身已保存这两个引用；键无需为每次访问再分配一个包装对象。
        private readonly struct ReferencePair
        {
            public ReferencePair(object left, object right)
            {
                Left = left;
                Right = right;
            }

            public object Left { get; }
            public object Right { get; }
        }

        private sealed class ReferencePairComparer : IEqualityComparer<ReferencePair>
        {
            public static readonly ReferencePairComparer Instance = new ReferencePairComparer();

            public bool Equals(ReferencePair left, ReferencePair right)
            {
                return ReferenceEquals(left.Left, right.Left) && ReferenceEquals(left.Right, right.Right);
            }

            public int GetHashCode(ReferencePair value)
            {
                unchecked
                {
                    return (RuntimeHelpers.GetHashCode(value.Left) * 397) ^ RuntimeHelpers.GetHashCode(value.Right);
                }
            }
        }
    }
}
