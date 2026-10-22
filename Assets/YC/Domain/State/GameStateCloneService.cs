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

        /// <summary>仅诊断当前线程动态范围内的顶层 GameState 克隆/复制；不进入持久化状态。</summary>
        public static IDisposable BeginTimingScope(Action<string, long> recordSample)
        {
            if (recordSample == null) throw new ArgumentNullException(nameof(recordSample));
            var scope = new CloneTimingScope(recordSample, currentTimingScope);
            currentTimingScope = scope;
            return scope;
        }

        private sealed class CloneTimingScope : IDisposable
        {
            private readonly Action<string, long> recordSample;
            private readonly CloneTimingScope previous;
            private readonly int ownerThreadId;
            private bool disposed;

            public CloneTimingScope(Action<string, long> recordSample, CloneTimingScope previous)
            {
                this.recordSample = recordSample;
                this.previous = previous;
                ownerThreadId = Thread.CurrentThread.ManagedThreadId;
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

        // 诊断路径随递归复用缓冲区；只有报错才生成完整字符串，避免每个状态节点分配长路径。
        private sealed class CopyPath
        {
            private readonly StringBuilder buffer;
            internal CopyPath(string root) { buffer = new StringBuilder(root); }
            internal Segment Enter(string field)
            {
                var segment = new Segment(buffer);
                buffer.Append('.').Append(field);
                return segment;
            }
            internal Segment Enter(int index)
            {
                var segment = new Segment(buffer);
                buffer.Append('[').Append(index).Append(']');
                return segment;
            }
            public override string ToString() => buffer.ToString();
            internal readonly struct Segment : IDisposable
            {
                private readonly StringBuilder buffer;
                private readonly int length;
                internal Segment(StringBuilder buffer) { this.buffer = buffer; length = buffer.Length; }
                public void Dispose() => buffer.Length = length;
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
                if (scope != null) timingOperationDepth++;
            }

            public void Dispose()
            {
                if (scope == null) return;
                try { scope.Record(phase, Stopwatch.GetTimestamp() - started); }
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

            var result = (T)CloneValue(source, typeof(T), new CopyPath(typeof(T).Name), new HashSet<object>(ReferenceEqualityComparer.Instance));
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
            CopyObjectFieldsInto(
                target,
                source,
                new CopyPath(typeof(GameState).Name),
                new HashSet<ReferencePair>(ReferencePairComparer.Instance));
            YC.Domain.Items.ItemZoneLegacyProjection.Bind(target);
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
            CopyPath path,
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
                return CloneArray((Array)value, path, activeReferences);
            }

            if (IsListType(type))
            {
                return CloneList((IList)value, type, path, activeReferences);
            }

            if (metadata.IsDictionary)
            {
                throw new InvalidOperationException(
                    "持久化状态禁止 Dictionary：" + path + "。请改用有序 List DTO。");
            }

            if (type.IsValueType)
            {
                return CloneObjectFields(value, type, path, activeReferences, false);
            }

            if (!metadata.IsSerializable)
            {
                throw new InvalidOperationException(
                    "状态字段必须是 [Serializable] DTO：" + path + " (" + type.FullName + ")。");
            }

            return CloneObjectFields(value, type, path, activeReferences, true);
        }

        private static object CloneArray(Array source, CopyPath path, HashSet<object> activeReferences)
        {
            EnterReference(source, path, activeReferences);
            try
            {
                Type elementType = StateCopyMetadata.For(source.GetType()).ElementType;
                Array clone = Array.CreateInstance(elementType, source.Length);
                for (int i = 0; i < source.Length; i++)
                {
                    using var elementPath = path.Enter(i);
                    clone.SetValue(CloneValue(source.GetValue(i), elementType, path, activeReferences), i);
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
            Type listType,
            CopyPath path,
            HashSet<object> activeReferences)
        {
            EnterReference(source, path, activeReferences);
            try
            {
                var metadata = StateCopyMetadata.For(listType);
                IList clone = (IList)metadata.CreateList(source.Count);
                Type elementType = metadata.ElementType;
                bool scalarElement = StateCopyMetadata.For(elementType).IsScalar;
                for (int i = 0; i < source.Count; i++)
                {
                    using var elementPath = path.Enter(i);
                    clone.Add(scalarElement ? source[i] : CloneValue(source[i], elementType, path, activeReferences));
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
            Type type,
            CopyPath path,
            HashSet<object> activeReferences,
            bool trackReference)
        {
            if (trackReference)
            {
                EnterReference(source, path, activeReferences);
            }

            try
            {
                var metadata = StateCopyMetadata.For(type);
                bool shallow = metadata.ShallowClone != null;
                object clone = shallow ? metadata.ShallowClone(source) : metadata.Create();
                // 标量直接按类型复制，避免深图中逐字段装箱和两次委托调用；引用仍逐项深克隆。
                if (!shallow) metadata.CopyScalars?.Invoke(clone, source);
                StateCopyField[] fields = shallow || metadata.CopyScalars != null ? metadata.ReferenceFields : metadata.Fields;
                for (int i = 0; i < fields.Length; i++)
                {
                    StateCopyField field = fields[i];
                    using var fieldPath = path.Enter(field.Name);
                    object fieldValue = field.GetValue(source);
                    field.SetValue(
                        clone,
                        field.IsScalar ? fieldValue : CloneValue(fieldValue, field.FieldType, path, activeReferences));
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
            CopyPath path,
            HashSet<ReferencePair> visited)
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

            var metadata = StateCopyMetadata.For(source.GetType());
            metadata.CopyScalars?.Invoke(target, source);
            StateCopyField[] fields = metadata.CopyScalars != null ? metadata.ReferenceFields : metadata.Fields;
            for (int i = 0; i < fields.Length; i++)
            {
                StateCopyField field = fields[i];
                using var fieldPath = path.Enter(field.Name);
                object sourceValue = field.GetValue(source);
                field.SetValue(
                    target,
                    field.IsScalar ? sourceValue : CopyValueInto(
                        field.GetValue(target),
                        sourceValue,
                        field.FieldType,
                        path,
                        visited));
            }
        }

        private static object CopyValueInto(
            object target,
            object source,
            Type declaredType,
            CopyPath path,
            HashSet<ReferencePair> visited)
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
                    return CloneValue(source, declaredType, path, new HashSet<object>(ReferenceEqualityComparer.Instance));
                }

                ReferencePair pair = new ReferencePair(targetArray, sourceArray);
                if (visited.Add(pair))
                {
                    Type elementType = metadata.ElementType;
                    for (int i = 0; i < sourceArray.Length; i++)
                    {
                        using var elementPath = path.Enter(i);
                        targetArray.SetValue(
                            CopyValueInto(
                                targetArray.GetValue(i),
                                sourceArray.GetValue(i),
                                elementType,
                                path,
                                visited),
                            i);
                    }
                }

                return targetArray;
            }

            if (IsListType(type))
            {
                IList sourceList = (IList)source;
                IList targetList = target as IList;
                if (targetList == null || targetList.GetType() != type)
                {
                    return CloneValue(source, declaredType, path, new HashSet<object>(ReferenceEqualityComparer.Instance));
                }

                ReferencePair pair = new ReferencePair(targetList, sourceList);
                if (!visited.Add(pair))
                {
                    return targetList;
                }

                Type elementType = metadata.ElementType;
                bool scalarElement = StateCopyMetadata.For(elementType).IsScalar;
                int sharedCount = Math.Min(targetList.Count, sourceList.Count);
                for (int i = 0; i < sharedCount; i++)
                {
                    using var elementPath = path.Enter(i);
                    targetList[i] = scalarElement ? sourceList[i] : CopyValueInto(
                        targetList[i],
                        sourceList[i],
                        elementType,
                        path,
                        visited);
                }

                while (targetList.Count > sourceList.Count)
                {
                    targetList.RemoveAt(targetList.Count - 1);
                }

                for (int i = targetList.Count; i < sourceList.Count; i++)
                {
                    using var elementPath = path.Enter(i);
                    targetList.Add(scalarElement ? sourceList[i] : CopyValueInto(
                        null,
                        sourceList[i],
                        elementType,
                        path,
                        visited));
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
                return CloneValue(source, declaredType, path, new HashSet<object>(ReferenceEqualityComparer.Instance));
            }

            if (!metadata.IsSerializable)
            {
                throw new InvalidOperationException(
                    "状态字段必须是 [Serializable] DTO：" + path + " (" + type.FullName + ")。");
            }

            if (target != null && target.GetType() == type)
            {
                CopyObjectFieldsInto(target, source, path, visited);
                return target;
            }

            return CloneValue(source, declaredType, path, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        private static bool AreEquivalentInternal(GameState left, GameState right, bool ignoreEffectRuntime)
        {
            return AreEquivalentValue(
                left,
                right,
                typeof(GameState),
                new HashSet<ReferencePair>(ReferencePairComparer.Instance),
                ignoreEffectRuntime,
                true);
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

            if (IsListType(leftType))
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

        private static bool IsListType(Type type)
        {
            return StateCopyMetadata.For(type).IsList;
        }

        private static void EnterReference(object value, CopyPath path, HashSet<object> activeReferences)
        {
            if (!activeReferences.Add(value))
            {
                throw new InvalidOperationException("状态 DTO 不能包含循环引用：" + path + "。");
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

        private sealed class ReferencePair
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
