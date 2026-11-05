using System;
using System.Collections.Generic;
using System.Reflection;

namespace YC.Domain.State
{
    public static partial class GameStateCloneService
    {
        // 两个封闭DTO占历史图的大多数节点。形状不符就回原元数据路径；不按Kind省略闲置字段。
        private static readonly Lazy<bool> NormalizedShape = new Lazy<bool>(() =>
            HasShape(typeof(NormalizedValue), new Dictionary<string, Type> {
                {nameof(NormalizedValue.Kind),typeof(NormalizedValueKind)}, {nameof(NormalizedValue.BooleanValue),typeof(bool)},
                {nameof(NormalizedValue.IntegerValue),typeof(long)}, {nameof(NormalizedValue.StringValue),typeof(string)},
                {nameof(NormalizedValue.ReferenceType),typeof(string)}, {nameof(NormalizedValue.ReferenceId),typeof(string)},
                {nameof(NormalizedValue.Items),typeof(List<NormalizedValue>)}, {nameof(NormalizedValue.Properties),typeof(List<NormalizedValueEntry>)} }) &&
            HasShape(typeof(NormalizedValueEntry), new Dictionary<string, Type> {
                {nameof(NormalizedValueEntry.Name),typeof(string)}, {nameof(NormalizedValueEntry.Value),typeof(NormalizedValue)} }));

        private static bool HasShape(Type type, Dictionary<string, Type> expected)
        {
            int count = 0;
            foreach(var field in type.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))
            {
                if (field.IsNotSerialized) continue;
                if (!expected.TryGetValue(field.Name,out var fieldType) || field.FieldType != fieldType || field.IsInitOnly) return false;
                count++;
            }
            return count == expected.Count;
        }

        private static NormalizedValue CloneNormalized(NormalizedValue source, DiagnosticPath path, HashSet<object> active)
        {
            if (HasNoNormalizedChildren(source))
            {
                var leaf = source.CreateShallowCloneShell();
                bool compact = IsCompactQueryBaseline(active);
                leaf.Items = source.Items == null ? null : compact ? QueryBaselineEmptyList<NormalizedValue>.Value : new List<NormalizedValue>();
                leaf.Properties = source.Properties == null ? null : compact ? QueryBaselineEmptyList<NormalizedValueEntry>.Value : new List<NormalizedValueEntry>();
                return leaf;
            }
            EnterReference(source,path,active);
            try
            {
                var clone = source.CreateShallowCloneShell();
                clone.Items=(List<NormalizedValue>)CloneValue(source.Items,typeof(List<NormalizedValue>),path.AtField(nameof(source.Items)),active);
                clone.Properties=(List<NormalizedValueEntry>)CloneValue(source.Properties,typeof(List<NormalizedValueEntry>),path.AtField(nameof(source.Properties)),active);
                return clone;
            }
            finally { active.Remove(source); }
        }
        private static NormalizedValueEntry CloneNormalizedEntry(NormalizedValueEntry source, DiagnosticPath path, HashSet<object> active)
        {
            if(source.Value==null || HasNoNormalizedChildren(source.Value))
            {
                var leaf=source.CreateShallowCloneShell();
                leaf.Value=source.Value==null ? null : CloneNormalized(source.Value,path.AtField(nameof(source.Value)),active);
                return leaf;
            }
            EnterReference(source,path,active);
            try
            {
                var clone = source.CreateShallowCloneShell();
                clone.Value=(NormalizedValue)CloneValue(source.Value,typeof(NormalizedValue),path.AtField(nameof(source.Value)),active);
                return clone;
            }
            finally { active.Remove(source); }
        }
        private static List<T> CloneDtoList<T>(List<T> source, DiagnosticPath path, HashSet<object> active)
        {
            if (source.Count == 0 && IsCompactQueryBaseline(active)) return QueryBaselineEmptyList<T>.Value;
            var clone = new List<T>(source.Count);
            if (source.Count == 0) return clone;
            EnterReference(source,path,active);
            try
            {
                for(int i=0;i<source.Count;i++) clone.Add((T)CloneValue(source[i],typeof(T),path.AtIndex(i),active));
                return clone;
            }
            finally { active.Remove(source); }
        }
        private static NormalizedValue CopyNormalized(NormalizedValue target, NormalizedValue source, DiagnosticPath path,
            HashSet<ReferencePair> visited, HashSet<object> active)
        {
            if (target == null) return CloneNormalized(source,path,active);
            if (!copyingUniqueTarget && !visited.Add(new ReferencePair(target,source))) return target;
            if (HasNoNormalizedChildren(source))
            {
                CopyNormalizedScalars(target, source);
                target.Items = CopyEmptyNormalizedList(target.Items, source.Items, visited);
                target.Properties = CopyEmptyNormalizedList(target.Properties, source.Properties, visited);
                return target;
            }
            target.BooleanValue=source.BooleanValue; target.IntegerValue=source.IntegerValue; target.Kind=source.Kind;
            target.ReferenceId=source.ReferenceId; target.ReferenceType=source.ReferenceType; target.StringValue=source.StringValue;
            target.Items=(List<NormalizedValue>)CopyValueInto(target.Items,source.Items,typeof(List<NormalizedValue>),path.AtField(nameof(source.Items)),visited,active);
            target.Properties=(List<NormalizedValueEntry>)CopyValueInto(target.Properties,source.Properties,typeof(List<NormalizedValueEntry>),path.AtField(nameof(source.Properties)),visited,active);
            return target;
        }
        // 只处理无子引用的精确List类型；派生列表仍走原完整复制以保存附加字段与环诊断。
        private static bool HasNoNormalizedChildren(NormalizedValue value) =>
            (value.Items == null || value.Items.GetType() == typeof(List<NormalizedValue>) && value.Items.Count == 0) &&
            (value.Properties == null || value.Properties.GetType() == typeof(List<NormalizedValueEntry>) && value.Properties.Count == 0);

        private static void CopyNormalizedScalars(NormalizedValue target, NormalizedValue source)
        {
            target.BooleanValue=source.BooleanValue; target.IntegerValue=source.IntegerValue; target.Kind=source.Kind;
            target.ReferenceId=source.ReferenceId; target.ReferenceType=source.ReferenceType; target.StringValue=source.StringValue;
        }

        private static List<T> CopyEmptyNormalizedList<T>(List<T> target, List<T> source, HashSet<ReferencePair> visited)
        {
            if (source == null) return null;
            if (target == null || target.GetType() != typeof(List<T>)) return new List<T>();
            if (target.Count > 0 && (copyingUniqueTarget || visited.Add(new ReferencePair(target,source)))) target.Clear();
            return target;
        }
        private static NormalizedValueEntry CopyNormalizedEntry(NormalizedValueEntry target, NormalizedValueEntry source, DiagnosticPath path,
            HashSet<ReferencePair> visited, HashSet<object> active)
        {
            if (target == null) return CloneNormalizedEntry(source,path,active);
            if (!copyingUniqueTarget && !visited.Add(new ReferencePair(target,source))) return target;
            target.Name=source.Name;
            target.Value=(NormalizedValue)CopyValueInto(target.Value,source.Value,typeof(NormalizedValue),path.AtField(nameof(source.Value)),visited,active);
            return target;
        }
        private static List<T> CopyDtoList<T>(List<T> target, List<T> source, DiagnosticPath path,
            HashSet<ReferencePair> visited, HashSet<object> active)
        {
            if (target == null || target.GetType()!=source.GetType()) return CloneDtoList(source,path,active);
            if (target.Count==0 && source.Count==0) return target;
            if (!copyingUniqueTarget && !visited.Add(new ReferencePair(target,source))) return target;
            if (source.Count>target.Capacity) target.Capacity=source.Count;
            int shared=Math.Min(target.Count,source.Count);
            for(int i=0;i<shared;i++) target[i]=(T)CopyValueInto(target[i],source[i],typeof(T),path.AtIndex(i),visited,active);
            while(target.Count>source.Count) target.RemoveAt(target.Count-1);
            for(int i=target.Count;i<source.Count;i++) target.Add((T)CopyValueInto(null,source[i],typeof(T),path.AtIndex(i),visited,active));
            return target;
        }
    }
}
