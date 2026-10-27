using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace YC.Application.Sessions
{
    /// <summary>每会话独立的可选诊断累计；不属于规则状态、存档或协议数据。</summary>
    public sealed class ExecutionTimingCounters
    {
        private static readonly string[] Phases = {
            "submit.total", "submit.clone", "submit.handler", "submit.publish", "recovery.capture",
            "projection.build", "projection.reuse", "consumer.permission", "consumer.applied", "consumer.validation",
            "pump.total", "pump.apply", "pump.windows", "pump.facts", "pump.notifications", "pump.validation", "boundary.digest",
            "handler.clone", "handler.copy", "ui.sync", "ui.full", "ui.permission"
        };
        private readonly long[] calls = new long[Phases.Length];
        private readonly long[] ticks = new long[Phases.Length];

        private int enabled;
        public bool Enabled { get => Volatile.Read(ref enabled) != 0; set => Volatile.Write(ref enabled, value ? 1 : 0); }

        // 显式开发采样才启用；只观察同步SubmitCore的当前owner线程，不存规则图或结果。
        public bool CaptureSubmitAllocations { get; set; }
        public bool CurrentThreadAllocationAvailable { get; set; }
        public event Action<SubmitAllocationObservation> SubmitAllocationObserved;
        private long allocationOrdinal, allocationParent;
        private int allocationDepth;

        public readonly struct SubmitAllocationObservation
        {
            public readonly string CommandId;
            public readonly long Id, ParentId, ElapsedTicks;
            public readonly int Depth, OwnerThreadId, EndingThreadId, Gen0Before, Gen1Before, Gen2Before, Gen0After, Gen1After, Gen2After;
            public readonly long? AllocatedBytes;
            internal SubmitAllocationObservation(string command, long id, long parent, int depth, int owner, int ending,
                long elapsed, long? allocated, int g0, int g1, int g2)
            {
                CommandId = command; Id = id; ParentId = parent; Depth = depth; OwnerThreadId = owner; EndingThreadId = ending;
                ElapsedTicks = elapsed; AllocatedBytes = allocated; Gen0Before = g0; Gen1Before = g1; Gen2Before = g2;
                Gen0After = GC.CollectionCount(0); Gen1After = GC.CollectionCount(1); Gen2After = GC.CollectionCount(2);
            }
        }

        public SubmitAllocationScope MeasureSubmitAllocation(string commandId) =>
            CaptureSubmitAllocations ? new SubmitAllocationScope(this, commandId) : default;

        public readonly struct SubmitAllocationScope : IDisposable
        {
            private readonly ExecutionTimingCounters counters;
            private readonly string command;
            private readonly long id, parent, began, allocatedBefore;
            private readonly int depth, owner, g0, g1, g2;
            internal SubmitAllocationScope(ExecutionTimingCounters counters, string command)
            {
                this.counters = counters; this.command = command;
                parent = counters.allocationParent; id = ++counters.allocationOrdinal;
                counters.allocationParent = id; depth = ++counters.allocationDepth;
                owner = Thread.CurrentThread.ManagedThreadId;
                g0 = GC.CollectionCount(0); g1 = GC.CollectionCount(1); g2 = GC.CollectionCount(2);
                allocatedBefore = ReadAllocated(counters.CurrentThreadAllocationAvailable);
                began = Stopwatch.GetTimestamp();
            }
            private static long ReadAllocated(bool available)
            {
                if (!available) return -1;
                try { return GC.GetAllocatedBytesForCurrentThread(); } catch { return -1; }
            }
            public void Dispose()
            {
                if (counters == null) return;
                long elapsed = Stopwatch.GetTimestamp() - began;
                int ending = Thread.CurrentThread.ManagedThreadId;
                long after = ReadAllocated(counters.CurrentThreadAllocationAvailable);
                long? allocated = owner == ending && allocatedBefore >= 0 && after >= allocatedBefore ? after - allocatedBefore : (long?)null;
                counters.allocationParent = parent; counters.allocationDepth--;
                try { counters.SubmitAllocationObserved?.Invoke(new SubmitAllocationObservation(command, id, parent, depth, owner, ending, elapsed, allocated, g0, g1, g2)); }
                catch { counters.CaptureSubmitAllocations = false; } // 诊断失败不改变原Submit返回或异常。
            }
        }

        public void AddSample(string phase, long elapsedTicks)
        {
            if (!Enabled || elapsedTicks < 0) return;
            int index = PhaseIndex(phase);
            if (index < 0) return;
            // 诊断累计不能因溢出改变原执行或异常路径。
            AddSaturated(ref calls[index], 1);
            AddSaturated(ref ticks[index], elapsedTicks);
        }

        public Dictionary<string, long> Snapshot()
        {
            var snapshot = new Dictionary<string, long>(Phases.Length * 2 + 1, System.StringComparer.Ordinal) {
                ["clock.frequency"] = Stopwatch.Frequency
            };
            for (int i = 0; i < Phases.Length; i++)
            {
                snapshot.Add(Phases[i] + ".calls", Interlocked.Read(ref calls[i]));
                snapshot.Add(Phases[i] + ".ticks", Interlocked.Read(ref ticks[i]));
            }
            return snapshot;
        }

        private static void AddSaturated(ref long target, long value)
        {
            long before, after;
            do { before = Interlocked.Read(ref target); after = value > long.MaxValue - before ? long.MaxValue : before + value; }
            while (Interlocked.CompareExchange(ref target, after, before) != before);
        }

        private static int PhaseIndex(string phase)
        {
            switch (phase)
            {
                case "submit.total": return 0;
                case "submit.clone": return 1;
                case "submit.handler": return 2;
                case "submit.publish": return 3;
                case "recovery.capture": return 4;
                case "projection.build": return 5;
                case "projection.reuse": return 6;
                case "consumer.permission": return 7;
                case "consumer.applied": return 8;
                case "consumer.validation": return 9;
                case "pump.total": return 10;
                case "pump.apply": return 11;
                case "pump.windows": return 12;
                case "pump.facts": return 13;
                case "pump.notifications": return 14;
                case "pump.validation": return 15;
                case "boundary.digest": return 16;
                case "handler.clone": return 17;
                case "handler.copy": return 18;
                case "ui.sync": return 19;
                case "ui.full": return 20;
                case "ui.permission": return 21;
                default: return -1;
            }
        }
    }
}
