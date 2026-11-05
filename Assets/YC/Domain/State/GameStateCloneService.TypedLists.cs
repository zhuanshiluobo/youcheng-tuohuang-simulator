using System;
using System.Collections.Generic;

namespace YC.Domain.State
{
    public static partial class GameStateCloneService
    {
        // 封闭可序列化DTO跳过重复类型分类/元数据字典查找。
        // 仍走同一字段计划、循环诊断和引用对集合；静态字段只保存类型计划。
        private static class KnownDto<T> where T : class
        { internal static readonly StateCopyMetadata Plan = StateCopyMetadata.For(typeof(T)); }

        private static T CloneKnownDto<T>(T source, DiagnosticPath path, HashSet<object> active) where T : class
        {
            if (source == null) return null;
            var plan=KnownDto<T>.Plan;
            if (!plan.IsSerializable) return (T)CloneValue(source,typeof(T),path,active);
            return (T)CloneObjectFields(source,plan,path,active,true);
        }

        private static T CopyKnownDto<T>(T target, T source, DiagnosticPath path,
            HashSet<ReferencePair> visited, HashSet<object> active) where T : class
        {
            if (source == null) return null;
            if (target == null) return CloneKnownDto(source,path,active);
            CopyObjectFieldsInto(target,source,path,visited,active,KnownDto<T>.Plan);
            return target;
        }

        internal static List<T> CloneKnownList<T>(List<T> source, DiagnosticPath path, HashSet<object> active) where T : class
        {
            if (source.Count == 0 && IsCompactQueryBaseline(active)) return QueryBaselineEmptyList<T>.Value;
            var result=new List<T>(source.Count);
            if (source.Count == 0) return result;
            EnterReference(source,path,active);
            try
            {
                for(int i=0;i<source.Count;i++)result.Add(CloneKnownDto(source[i],path.AtIndex(i),active));
                return result;
            }
            finally {active.Remove(source);}
        }

        internal static List<T> CopyKnownList<T>(List<T> target, List<T> source, DiagnosticPath path,
            HashSet<ReferencePair> visited, HashSet<object> active) where T : class
        {
            if (TryCopyJournalPrefix(target, source, path, active)) return target;
            if (target == null || target.GetType()!=source.GetType())return CloneKnownList(source,path,active);
            if (target.Count==0 && source.Count==0)return target;
            if (!copyingUniqueTarget && !visited.Add(new ReferencePair(target,source)))return target;
            if (source.Count>target.Capacity)target.Capacity=source.Count;
            int shared=Math.Min(target.Count,source.Count);
            for(int i=0;i<shared;i++)
                if (!copyingUniqueTarget || disableUnchangedCopy || !HasIdenticalCopyValue(target[i], source[i], typeof(T)))
                    target[i]=CopyKnownDto(target[i],source[i],path.AtIndex(i),visited,active);
            while(target.Count>source.Count)target.RemoveAt(target.Count-1);
            for(int i=target.Count;i<source.Count;i++)target.Add(CloneKnownDto(source[i],path.AtIndex(i),active));
            return target;
        }
    }
}
