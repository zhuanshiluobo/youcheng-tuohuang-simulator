using System;
using System.Collections;
using System.Collections.Generic;

namespace YC.Domain.State
{
    public static partial class GameStateCloneService
    {
        [ThreadStatic] private static bool disableUnchangedCopy;
        public static IDisposable UseFullHistoricalCopyForDiagnostics() => new FullHistoricalCopyScope();
        private sealed class FullHistoricalCopyScope : IDisposable
        {
            private readonly bool previous = disableUnchangedCopy;
            private bool closed;
            internal FullHistoricalCopyScope() { disableUnchangedCopy = true; }
            public void Dispose() { if (closed) return; closed = true; disableUnchangedCopy = previous; }
        }
        // baseline必须是刚完成的独立DeepClone：其有限无环字段图使逐字段匹配不需登记引用对。
        // true比规则摘要相等更严格：字符串需同实例、浮点/decimal需逐位相同，闲置字段也不省略。
        // false只表示走原摘要对照，绝不能据此直接判定处理器改写了规则状态。
        internal static bool HasIdenticalReadOnlyBaseline(GameState baseline, GameState current)
            => HaveIdenticalReadOnlyFields(baseline, current);

        // 仅用于Run已证明无共享可变引用的发布目标。未变化的历史DTO保留全部原引用；
        // 无证明、派生容器、值类型DTO或不支持的字段均回原完整CopyTo。
        // 目标是有限无环图，因此比较时无需再次登记每个引用对；源若有变化仍完整复制/检查环。
        private static bool HasIdenticalNormalizedList<T>(List<T> left, List<T> right) where T : class
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (!HasIdenticalCopyValue(left[i], right[i], typeof(T))) return false;
            return true;
        }

        private static bool HasIdenticalCopyValue(object left, object right, Type declaredType)
        {
            if (left == null || right == null) return ReferenceEquals(left, right);
            var type = left.GetType();
            if (type != declaredType || right.GetType() != type) return false;
            if (type == typeof(string)) return ReferenceEquals(left, right);
            if (type == typeof(float)) return BitConverter.SingleToInt32Bits((float)left) == BitConverter.SingleToInt32Bits((float)right);
            if (type == typeof(double)) return BitConverter.DoubleToInt64Bits((double)left) == BitConverter.DoubleToInt64Bits((double)right);
            if (type == typeof(decimal))
            {
                var a = decimal.GetBits((decimal)left); var b = decimal.GetBits((decimal)right);
                return a[0] == b[0] && a[1] == b[1] && a[2] == b[2] && a[3] == b[3];
            }
            if (type == typeof(NormalizedValue) && NormalizedShape.Value)
            {
                var a = (NormalizedValue)left; var b = (NormalizedValue)right;
                return a.BooleanValue == b.BooleanValue && a.IntegerValue == b.IntegerValue && a.Kind == b.Kind &&
                    ReferenceEquals(a.StringValue, b.StringValue) && ReferenceEquals(a.ReferenceType, b.ReferenceType) && ReferenceEquals(a.ReferenceId, b.ReferenceId) &&
                    HasIdenticalCopyValue(a.Items, b.Items, typeof(List<NormalizedValue>)) &&
                    HasIdenticalCopyValue(a.Properties, b.Properties, typeof(List<NormalizedValueEntry>));
            }
            if (type == typeof(NormalizedValueEntry) && NormalizedShape.Value)
            {
                var a = (NormalizedValueEntry)left; var b = (NormalizedValueEntry)right;
                return ReferenceEquals(a.Name, b.Name) && HasIdenticalCopyValue(a.Value, b.Value, typeof(NormalizedValue));
            }
            // 精确类型的归一化列表占历史图的大多数引用；不反复查元数据或经IList索引。
            // 前面的声明类型/运行时类型核验仍拒绝派生容器；空列表与原完整比较同义。
            if (type == typeof(List<NormalizedValue>))
                return HasIdenticalNormalizedList((List<NormalizedValue>)left, (List<NormalizedValue>)right);
            if (type == typeof(List<NormalizedValueEntry>))
                return HasIdenticalNormalizedList((List<NormalizedValueEntry>)left, (List<NormalizedValueEntry>)right);
            var plan = StateCopyMetadata.For(type);
            if (plan.IsScalar) return left.Equals(right);
            if (plan.IsList)
            {
                var a = (IList)left; var b = (IList)right;
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++)
                    if (!HasIdenticalCopyValue(a[i], b[i], plan.ElementType)) return false;
                return true;
            }
            return !type.IsArray && !plan.IsDictionary && plan.HasIdenticalCopyFields(left, right);
        }
    }
}
