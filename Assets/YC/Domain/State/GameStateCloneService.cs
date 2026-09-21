using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace YC.Domain.State
{
    /// <summary>
    /// GameState 的唯一深克隆入口。状态 DTO 只允许由可序列化字段和 List
    /// 组成，因此这里用反射遍历完整字段图：新增权威字段会自动进入克隆，
    /// 未受支持的持久化容器会明确失败，而不是静默浅拷贝。
    /// </summary>
    public static class GameStateCloneService
    {
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
            if (source == null)
            {
                return null;
            }

            return (T)CloneValue(source, typeof(T), typeof(T).Name, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        public static void CopyTo(GameState target, GameState source)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (ReferenceEquals(target, source)) return;

            // 成功发布时保留旧业务代码可能持有的 PlayerState、MapRuntimeState
            // 等引用；新加入的集合元素仍会被克隆，避免把工作副本重新暴露给外部。
            CopyObjectFieldsInto(
                target,
                source,
                typeof(GameState).Name,
                new HashSet<ReferencePair>(ReferencePairComparer.Instance));
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
            string path,
            HashSet<object> activeReferences)
        {
            if (value == null)
            {
                return null;
            }

            Type type = value.GetType();
            if (type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal))
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

            if (typeof(IDictionary).IsAssignableFrom(type) || IsDictionaryType(type))
            {
                throw new InvalidOperationException(
                    "持久化状态禁止 Dictionary：" + path + "。请改用有序 List DTO。");
            }

            if (type.IsValueType)
            {
                return CloneObjectFields(value, type, path, activeReferences, false);
            }

            if (!Attribute.IsDefined(type, typeof(SerializableAttribute), true))
            {
                throw new InvalidOperationException(
                    "状态字段必须是 [Serializable] DTO：" + path + " (" + type.FullName + ")。");
            }

            return CloneObjectFields(value, type, path, activeReferences, true);
        }

        private static object CloneArray(Array source, string path, HashSet<object> activeReferences)
        {
            EnterReference(source, path, activeReferences);
            try
            {
                Type elementType = source.GetType().GetElementType();
                Array clone = Array.CreateInstance(elementType, source.Length);
                for (int i = 0; i < source.Length; i++)
                {
                    clone.SetValue(CloneValue(source.GetValue(i), elementType, path + "[" + i + "]", activeReferences), i);
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
            string path,
            HashSet<object> activeReferences)
        {
            EnterReference(source, path, activeReferences);
            try
            {
                IList clone = (IList)Activator.CreateInstance(listType);
                Type elementType = listType.GetGenericArguments()[0];
                for (int i = 0; i < source.Count; i++)
                {
                    clone.Add(CloneValue(source[i], elementType, path + "[" + i + "]", activeReferences));
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
            string path,
            HashSet<object> activeReferences,
            bool trackReference)
        {
            if (trackReference)
            {
                EnterReference(source, path, activeReferences);
            }

            try
            {
                object clone = Activator.CreateInstance(type);
                FieldInfo[] fields = GetSerializableFields(type);
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    object fieldValue = field.GetValue(source);
                    field.SetValue(
                        clone,
                        CloneValue(fieldValue, field.FieldType, path + "." + field.Name, activeReferences));
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
            string path,
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

            FieldInfo[] fields = GetSerializableFields(source.GetType());
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                object targetValue = field.GetValue(target);
                object sourceValue = field.GetValue(source);
                field.SetValue(
                    target,
                    CopyValueInto(
                        targetValue,
                        sourceValue,
                        field.FieldType,
                        path + "." + field.Name,
                        visited));
            }
        }

        private static object CopyValueInto(
            object target,
            object source,
            Type declaredType,
            string path,
            HashSet<ReferencePair> visited)
        {
            if (source == null)
            {
                return null;
            }

            Type type = source.GetType();
            if (type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal))
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
                    Type elementType = type.GetElementType();
                    for (int i = 0; i < sourceArray.Length; i++)
                    {
                        targetArray.SetValue(
                            CopyValueInto(
                                targetArray.GetValue(i),
                                sourceArray.GetValue(i),
                                elementType,
                                path + "[" + i + "]",
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

                Type elementType = type.GetGenericArguments()[0];
                int sharedCount = Math.Min(targetList.Count, sourceList.Count);
                for (int i = 0; i < sharedCount; i++)
                {
                    targetList[i] = CopyValueInto(
                        targetList[i],
                        sourceList[i],
                        elementType,
                        path + "[" + i + "]",
                        visited);
                }

                while (targetList.Count > sourceList.Count)
                {
                    targetList.RemoveAt(targetList.Count - 1);
                }

                for (int i = targetList.Count; i < sourceList.Count; i++)
                {
                    targetList.Add(CopyValueInto(
                        null,
                        sourceList[i],
                        elementType,
                        path + "[" + i + "]",
                        visited));
                }

                return targetList;
            }

            if (typeof(IDictionary).IsAssignableFrom(type) || IsDictionaryType(type))
            {
                throw new InvalidOperationException(
                    "持久化状态禁止 Dictionary：" + path + "。请改用有序 List DTO。");
            }

            if (type.IsValueType)
            {
                return CloneValue(source, declaredType, path, new HashSet<object>(ReferenceEqualityComparer.Instance));
            }

            if (!Attribute.IsDefined(type, typeof(SerializableAttribute), true))
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
            if (leftType == typeof(string) || leftType.IsPrimitive || leftType.IsEnum || leftType == typeof(decimal))
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
                            leftType.GetElementType(),
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
                Type elementType = leftType.GetGenericArguments()[0];
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

            if (typeof(IDictionary).IsAssignableFrom(leftType) || IsDictionaryType(leftType))
            {
                throw new InvalidOperationException("状态比较遇到 Dictionary：" + leftType.FullName + "。");
            }

            ReferencePair objectPair = new ReferencePair(left, right);
            if (!visited.Add(objectPair)) return true;
            FieldInfo[] fields = GetSerializableFields(leftType);
            for (int i = 0; i < fields.Length; i++)
            {
                if (isRoot && ignoreEffectRuntime && fields[i].Name == "EffectRuntime")
                {
                    continue;
                }

                FieldInfo field = fields[i];
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

        private static FieldInfo[] GetSerializableFields(Type type)
        {
            List<FieldInfo> fields = new List<FieldInfo>();
            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                FieldInfo[] declared = current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < declared.Length; i++)
                {
                    if (!declared[i].IsStatic && !declared[i].IsNotSerialized)
                    {
                        fields.Add(declared[i]);
                    }
                }
            }

            fields.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            return fields.ToArray();
        }

        private static bool IsListType(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
        }

        private static bool IsDictionaryType(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>);
        }

        private static void EnterReference(object value, string path, HashSet<object> activeReferences)
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
