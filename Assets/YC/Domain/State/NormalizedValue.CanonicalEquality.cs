using System;

namespace YC.Domain.State
{
    public sealed partial class NormalizedValue
    {
        [ThreadStatic] private static bool fullCanonicalEquality,verifyCanonicalEquality;
        public static IDisposable UseFullCanonicalEqualityForDiagnostics() => new CanonicalEqualityScope(true,false);
        public static IDisposable VerifyCanonicalEqualityForDiagnostics() => new CanonicalEqualityScope(false,true);
        private sealed class CanonicalEqualityScope:IDisposable
        {
            private readonly bool priorFull=fullCanonicalEquality,priorVerify=verifyCanonicalEquality;
            private bool closed;
            internal CanonicalEqualityScope(bool full,bool verify){fullCanonicalEquality=full;verifyCanonicalEquality=verify;}
            public void Dispose(){if(closed)return;closed=true;fullCanonicalEquality=priorFull;verifyCanonicalEquality=priorVerify;}
        }
        // 等价于两次原规范串比较。仍先按原顺序完整验证两端，只省字符串构造；
        // 闲置字段不进入规范串，不能用全部持久字段相等来替代此合同。
        public bool HasSameCanonicalValue(NormalizedValue other)
        {
            if(fullCanonicalEquality)return ToDeterministicString()==other.ToDeterministicString();
            using var sample=GameStateCloneService.MeasureDiagnostic("normalized.canonical_equal");
            if(!TryValidate(out var reason))throw new InvalidOperationException("归一化值无效："+reason);
            if(!other.TryValidate(out reason))throw new InvalidOperationException("归一化值无效："+reason);
            bool same=CanonicalPayloadEquals(this,other);
            if(verifyCanonicalEquality && same!=(ToDeterministicString()==other.ToDeterministicString()))
                throw new InvalidOperationException("规范值直接比较与原规范串比较不一致。");
            return same;
        }
        private static bool CanonicalPayloadEquals(NormalizedValue left,NormalizedValue right)
        {
            if(ReferenceEquals(left,right))return true;
            if(left.Kind!=right.Kind)return false;
            switch(left.Kind)
            {
                case NormalizedValueKind.Null:return true;
                case NormalizedValueKind.Boolean:return left.BooleanValue==right.BooleanValue;
                case NormalizedValueKind.Integer:return left.IntegerValue==right.IntegerValue;
                case NormalizedValueKind.String:return string.Equals(left.StringValue,right.StringValue,StringComparison.Ordinal);
                case NormalizedValueKind.StableReference:return string.Equals(left.ReferenceType,right.ReferenceType,StringComparison.Ordinal) && string.Equals(left.ReferenceId,right.ReferenceId,StringComparison.Ordinal);
                case NormalizedValueKind.Array:
                    if(left.Items.Count!=right.Items.Count)return false;
                    for(int i=0;i<left.Items.Count;i++)if(!CanonicalPayloadEquals(left.Items[i],right.Items[i]))return false;
                    return true;
                case NormalizedValueKind.Object:
                    if(left.Properties.Count!=right.Properties.Count)return false;
                    for(int i=0;i<left.Properties.Count;i++)if(!string.Equals(left.Properties[i].Name,right.Properties[i].Name,StringComparison.Ordinal) || !CanonicalPayloadEquals(left.Properties[i].Value,right.Properties[i].Value))return false;
                    return true;
                default:throw new InvalidOperationException("完整校验通过后出现未知归一化值kind。");
            }
        }
    }
}
