using System;
using System.Collections.Generic;

namespace YC.Domain.State
{
    public static partial class GameStateCloneService
    {
        private static bool EquivalentNormalized(NormalizedValue left,NormalizedValue right,HashSet<ReferencePair> visited,bool ignoreRuntime)
        {
            if(ReferenceEquals(left,right))return true;
            if(left==null || right==null)return false;
            if(left.Kind!=right.Kind || left.BooleanValue!=right.BooleanValue || left.IntegerValue!=right.IntegerValue ||
                left.StringValue!=right.StringValue || left.ReferenceType!=right.ReferenceType || left.ReferenceId!=right.ReferenceId)return false;
            if(HasNoNormalizedChildren(left) && HasNoNormalizedChildren(right))
                return (left.Items==null)==(right.Items==null) && (left.Properties==null)==(right.Properties==null);
            if(!visited.Add(new ReferencePair(left,right)))return true;
            return AreEquivalentValue(left.Items,right.Items,typeof(List<NormalizedValue>),visited,ignoreRuntime,false) &&
                AreEquivalentValue(left.Properties,right.Properties,typeof(List<NormalizedValueEntry>),visited,ignoreRuntime,false);
        }
        private static bool EquivalentNormalizedEntry(NormalizedValueEntry left,NormalizedValueEntry right,HashSet<ReferencePair> visited,bool ignoreRuntime)
        {
            if(ReferenceEquals(left,right))return true;
            if(left==null || right==null || left.Name!=right.Name)return false;
            bool leaves=(left.Value==null || HasNoNormalizedChildren(left.Value)) && (right.Value==null || HasNoNormalizedChildren(right.Value));
            if(!leaves && !visited.Add(new ReferencePair(left,right)))return true;
            return EquivalentNormalized(left.Value,right.Value,visited,ignoreRuntime);
        }
    }
}
