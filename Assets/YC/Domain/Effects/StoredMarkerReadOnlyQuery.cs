using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static partial class ResourcePaymentCostPreview
    {
        // 只在BuildFacilityService已有的同步只读回调内复用同一问题的布尔答案。
        // 不按revision跨调用缓存；关闭、异常或不同工作副本均不能命中。
        [ThreadStatic] private static StoredMarkerQuery currentStoredMarkerQuery;
        internal static IDisposable BeginReadOnlyStoredMarkerQuery(GameState state)
        {
            var scope = new StoredMarkerQuery(state,currentStoredMarkerQuery);
            currentStoredMarkerQuery=scope; return scope;
        }
        internal static bool TryReadStoredMarkerQuery(GameState state, EffectRegistry registry, int player, ResourceSet cost,out bool result)
        {
            result=false; var scope=currentStoredMarkerQuery;
            return scope!=null && ReferenceEquals(scope.State,state) && scope.Answers.TryGetValue(new StoredMarkerKey(registry,player,cost),out result);
        }
        internal static void RecordStoredMarkerQuery(GameState state, EffectRegistry registry,int player,ResourceSet cost,bool result)
        {
            var scope=currentStoredMarkerQuery;
            if(scope!=null && ReferenceEquals(scope.State,state) && scope.Answers.Count<256)
                scope.Answers[new StoredMarkerKey(registry,player,cost)]=result;
        }
        private sealed class StoredMarkerQuery : IDisposable
        {
            internal GameState State;
            internal readonly Dictionary<StoredMarkerKey,bool> Answers=new Dictionary<StoredMarkerKey,bool>();
            private readonly StoredMarkerQuery previous;
            private bool closed;
            internal StoredMarkerQuery(GameState state,StoredMarkerQuery previous) { State=state;this.previous=previous; }
            public void Dispose()
            {
                if(closed)return;closed=true;State=null;Answers.Clear();
                if(ReferenceEquals(currentStoredMarkerQuery,this))
                {
                    var restored=previous;while(restored!=null && restored.closed)restored=restored.previous;
                    currentStoredMarkerQuery=restored;
                }
            }
        }
        private readonly struct StoredMarkerKey : IEquatable<StoredMarkerKey>
        {
            private readonly EffectRegistry registry;
            private readonly int player,originium,shard,iron,pure,gold;
            internal StoredMarkerKey(EffectRegistry registry,int player,ResourceSet cost)
            {this.registry=registry;this.player=player;originium=cost.Originium;shard=cost.OriginiumShard;iron=cost.Iron;pure=cost.PureOriginium;gold=cost.GoldVoucher;}
            public bool Equals(StoredMarkerKey other)=>ReferenceEquals(registry,other.registry) && player==other.player && originium==other.originium && shard==other.shard && iron==other.iron && pure==other.pure && gold==other.gold;
            public override bool Equals(object other)=>other is StoredMarkerKey key && Equals(key);
            public override int GetHashCode()
            { unchecked {int hash=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(registry);hash=hash*31+player;hash=hash*31+originium;hash=hash*31+shard;hash=hash*31+iron;hash=hash*31+pure;return hash*31+gold;} }
        }
    }
}
