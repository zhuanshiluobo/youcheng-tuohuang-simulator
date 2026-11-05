using System;
using System.Collections.Generic;

namespace YC.Domain.State
{
    /// <summary>一次同步只读回调的临时投影。不得跨yield、命令或状态写入使用。</summary>
    public sealed class ReadOnlyStateQueryScope : IDisposable
    {
        [ThreadStatic] private static ReadOnlyStateQueryScope current;
        public static event Action<string> DiagnosticSample;
        private static void Sample(string phase) {try {DiagnosticSample?.Invoke(phase);}catch {}}
        private readonly ReadOnlyStateQueryScope previous;
        private GameState state;
        private readonly int revision;
        private readonly Dictionary<object,object> projections=new Dictionary<object,object>();
        private bool closed;
        private ReadOnlyStateQueryScope(GameState state)
        {this.state=state;revision=state.EffectRuntime?.StateRevision??-1;previous=current;current=this;}
        public static ReadOnlyStateQueryScope Begin(GameState state) => new ReadOnlyStateQueryScope(state??throw new ArgumentNullException(nameof(state)));
        public static bool IsActiveFor(GameState state) => state != null && current != null &&
            ReferenceEquals(current.state, state) && current.revision == (state.EffectRuntime?.StateRevision ?? -1);
        public static bool TryRead<T>(GameState state,object key,out T value) where T:class
        {
            value=null;var scope=current;
            if(scope==null){Sample("unscoped");return false;}
            if(!ReferenceEquals(scope.state,state)){Sample("different_state");return false;}
            if(scope.revision!=(state.EffectRuntime?.StateRevision??-1)){Sample("different_revision");return false;}
            if(!scope.projections.TryGetValue(key,out var cached)){Sample("key_missing");return false;}
            Sample("hit");
            value=cached as T;return value!=null;
        }
        public static void Store<T>(GameState state,object key,T value) where T:class
        {
            var scope=current;
            if(scope!=null && ReferenceEquals(scope.state,state) && scope.revision==(state.EffectRuntime?.StateRevision??-1) && scope.projections.Count<64)
            {scope.projections[key]=value;Sample("stored");}
        }
        public void Dispose()
        {
            if(closed)return;closed=true;state=null;projections.Clear();
            if(ReferenceEquals(current,this))
            {var restored=previous;while(restored!=null && restored.closed)restored=restored.previous;current=restored;}
        }
    }
}
