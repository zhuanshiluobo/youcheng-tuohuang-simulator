using System;
using System.Runtime.CompilerServices;
using System.Threading;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    // 只记录进程内执行栈，不进入规则状态、摘要或存档。
    public static class EffectSaveActivity
    {
        internal sealed class Counter { internal int Depth; }
        private static readonly ConditionalWeakTable<GameState, Counter> Active = new ConditionalWeakTable<GameState, Counter>();
        public readonly struct Scope : IDisposable
        {
            private readonly Counter counter;
            internal Scope(Counter counter) { this.counter = counter; }
            public void Dispose() { if (counter != null) Interlocked.Decrement(ref counter.Depth); }
        }
        public static Scope Enter(GameState state)
        {
            if (state == null) return default;
            var counter = Active.GetValue(state, _ => new Counter()); Interlocked.Increment(ref counter.Depth);
            return new Scope(counter);
        }
        public static bool IsExecuting(GameState state) => state != null && Active.TryGetValue(state, out var counter) && Volatile.Read(ref counter.Depth) != 0;
    }
}
