using System;
using System.Collections;

namespace YC.Domain.State
{
    public static partial class GameStateCloneService
    {
        private static bool HaveIdenticalReadOnlyList<T>(System.Collections.Generic.List<T> baseline, System.Collections.Generic.List<T> current) where T:class
        {
            if(baseline==null || current==null)return ReferenceEquals(baseline,current);
            if(baseline.GetType()!=typeof(System.Collections.Generic.List<T>) || current.GetType()!=typeof(System.Collections.Generic.List<T>))
                return HaveIdenticalReadOnlyFields(baseline,current);
            if(baseline.Count!=current.Count)return false;
            for(int i=0;i<baseline.Count;i++)if(!HaveIdenticalReadOnlyFields(baseline[i],current[i]))return false;
            return true;
        }

        // 仅在独立DeepClone基线上使用。比较全部持久字段及运行时类型；允许可空值装箱，
        // 不采用复制发布优化中“声明类型必须精确匹配/数组一律回退”的保守限制。
        // 基线有限无环，任何匹配路径都受基线长度约束；不省略闲置字段或登记跨调用缓存。
        private static bool HaveIdenticalReadOnlyFields(object baseline, object current)
        {
            if (baseline == null || current == null) return ReferenceEquals(baseline, current);
            Type type = baseline.GetType();
            if (current.GetType() != type) return false;
            if (type == typeof(string)) return ReferenceEquals(baseline, current);
            if (type == typeof(float)) return BitConverter.SingleToInt32Bits((float)baseline) == BitConverter.SingleToInt32Bits((float)current);
            if (type == typeof(double)) return BitConverter.DoubleToInt64Bits((double)baseline) == BitConverter.DoubleToInt64Bits((double)current);
            if (type == typeof(decimal))
            {
                var a=decimal.GetBits((decimal)baseline); var b=decimal.GetBits((decimal)current);
                return a[0]==b[0] && a[1]==b[1] && a[2]==b[2] && a[3]==b[3];
            }
            if (NormalizedShape.Value)
            {
                if (baseline is NormalizedValue a && current is NormalizedValue b)
                    return a.Kind==b.Kind && a.BooleanValue==b.BooleanValue && a.IntegerValue==b.IntegerValue &&
                        ReferenceEquals(a.StringValue,b.StringValue) && ReferenceEquals(a.ReferenceType,b.ReferenceType) && ReferenceEquals(a.ReferenceId,b.ReferenceId) &&
                        HaveIdenticalReadOnlyList(a.Items,b.Items) && HaveIdenticalReadOnlyList(a.Properties,b.Properties);
                if (baseline is NormalizedValueEntry leftEntry && current is NormalizedValueEntry rightEntry)
                    return ReferenceEquals(leftEntry.Name,rightEntry.Name) && HaveIdenticalReadOnlyFields(leftEntry.Value,rightEntry.Value);
            }
            var plan = StateCopyMetadata.For(type);
            if (plan.IsScalar) return baseline.Equals(current);
            if (type.IsArray)
            {
                var a=(Array)baseline; var b=(Array)current;
                if (a.Rank != b.Rank) return false;
                for (int i=0;i<a.Rank;i++) if(a.GetLowerBound(i)!=b.GetLowerBound(i) || a.GetLength(i)!=b.GetLength(i)) return false;
                var left=a.GetEnumerator(); var right=b.GetEnumerator();
                while(left.MoveNext()) { if(!right.MoveNext() || !HaveIdenticalReadOnlyFields(left.Current,right.Current)) return false; }
                return !right.MoveNext();
            }
            if (plan.IsList)
            {
                var a=(IList)baseline; var b=(IList)current;
                if(a.Count!=b.Count)return false;
                for(int i=0;i<a.Count;i++) if(!HaveIdenticalReadOnlyFields(a[i],b[i]))return false;
                return true;
            }
            if(plan.IsDictionary || !type.IsValueType && !plan.IsSerializable)return false;
            if(plan.ReadOnlyEquivalentFields!=null)return plan.ReadOnlyEquivalentFields(baseline,current);
            foreach(var field in plan.Fields) if(!HaveIdenticalReadOnlyFields(field.GetValue(baseline),field.GetValue(current)))return false;
            return true;
        }
    }
}
