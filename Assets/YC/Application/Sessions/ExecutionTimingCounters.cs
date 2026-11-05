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
            "handler.clone", "handler.copy", "ui.sync", "ui.full", "ui.permission", "runtime.validate", "canonical.hash",
            "kernel.retention", "kernel.departure", "kernel.attachment", "kernel.deferred",
            "lua.context", "lua.execute", "lua.compile", "lua.snapshot.stamp", "lua.snapshot.player_facts",
            "lua.snapshot.map", "lua.snapshot.visibility", "lua.snapshot.content", "lua.snapshot.source_facts",
            "entity.validate", "operation.sync", "state.compare", "kernel.schedule", "kernel.run", "kernel.execute",
            "kernel.apply", "kernel.dispatch", "kernel.commit", "round.advance", "round.project",
            "kernel.executor", "kernel.handler", "execution.validate",
            "lua.context.input", "lua.context.fixture", "lua.context.frame", "driver.move_next.inclusive",
            "input.callback", "fixture.append", "fixture.write", "fixture.capture",
            "ancestry.filter.index", "scoring.archive.descendants", "cleanup.obligation.descendants",
            "transfer.structural_preview",
            "entity.validation.compare", "entity.validation.snapshot", "entity.validation.reused", "attachment.descendant_index", "normalized.canonical_equal", "kernel.independent_execution_writes", "hud.action_panel.characters", "hud.action_panel.model", "hud.action_panel.rows", "hud.action_panel.frame", "hud.action_panel.modules", "hud.action_panel.expansion", "kernel.recycled_continuation_work", "kernel.unique_write_proof_downgraded", "handler.copy.journal_prefix", "character.item_world.publish"

        };
        private readonly long[] calls = new long[Phases.Length];
        private readonly long[] ticks = new long[Phases.Length];
        private readonly Dictionary<string, long[]> detailSamples = new Dictionary<string, long[]>(StringComparer.Ordinal);
        private long droppedDetails;

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
            if (index < 0)
            {
                if (string.IsNullOrEmpty(phase) || phase.Length > 160) return;
                lock (detailSamples)
                {
                    if (!detailSamples.TryGetValue(phase, out var detail))
                    {
                        if (detailSamples.Count >= 256) { AddSaturated(ref droppedDetails, 1); return; }
                        detailSamples.Add(phase, detail = new long[2]);
                    }
                    AddSaturated(ref detail[0], 1); AddSaturated(ref detail[1], elapsedTicks);
                }
                return;
            }
            // 诊断累计不能因溢出改变原执行或异常路径。
            AddSaturated(ref calls[index], 1);
            AddSaturated(ref ticks[index], elapsedTicks);
        }

        public Dictionary<string, long> Snapshot()
        {
            var snapshot = new Dictionary<string, long>(Phases.Length * 2 + 1, System.StringComparer.Ordinal) {
                ["clock.frequency"] = Stopwatch.Frequency,
                ["diagnostic.detail_dropped.calls"] = Interlocked.Read(ref droppedDetails)
            };
            for (int i = 0; i < Phases.Length; i++)
            {
                snapshot.Add(Phases[i] + ".calls", Interlocked.Read(ref calls[i]));
                snapshot.Add(Phases[i] + ".ticks", Interlocked.Read(ref ticks[i]));
            }
            lock (detailSamples)
                foreach (var pair in detailSamples)
                { snapshot.Add(pair.Key + ".calls", pair.Value[0]); snapshot.Add(pair.Key + ".ticks", pair.Value[1]); }
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
                case "runtime.validate": return 22;
                case "canonical.hash": return 23;
                case "kernel.retention": return 24;
                case "kernel.departure": return 25;
                case "kernel.attachment": return 26;
                case "kernel.deferred": return 27;
                case "lua.context": return 28;
                case "lua.execute": return 29;
                case "lua.compile": return 30;
                case "lua.snapshot.stamp": return 31;
                case "lua.snapshot.player_facts": return 32;
                case "lua.snapshot.map": return 33;
                case "lua.snapshot.visibility": return 34;
                case "lua.snapshot.content": return 35;
                case "lua.snapshot.source_facts": return 36;
                case "entity.validate": return 37;
                case "operation.sync": return 38;
                case "state.compare": return 39;
                case "kernel.schedule": return 40;
                case "kernel.run": return 41;
                case "kernel.execute": return 42;
                case "kernel.apply": return 43;
                case "kernel.dispatch": return 44;
                case "kernel.commit": return 45;
                case "round.advance": return 46;
                case "round.project": return 47;
                case "kernel.executor": return 48;
                case "kernel.handler": return 49;
                case "execution.validate": return 50;
                case "lua.context.input": return 51;
                case "lua.context.fixture": return 52;
                case "lua.context.frame": return 53;
                case "driver.move_next.inclusive": return 54;
                case "input.callback": return 55;
                case "fixture.append": return 56;
                case "fixture.write": return 57;
                case "fixture.capture": return 58;
                case "ancestry.filter.index": return 59;
                case "scoring.archive.descendants": return 60;
                case "cleanup.obligation.descendants": return 61;
                case "transfer.structural_preview": return 62;
                case "entity.validation.compare": return 63;
                case "entity.validation.snapshot": return 64;
                case "entity.validation.reused": return 65;
                case "attachment.descendant_index": return 66;
                case "normalized.canonical_equal": return 67;
                case "kernel.independent_execution_writes": return 68;
                case "hud.action_panel.characters": return 69;
                case "hud.action_panel.model": return 70;
                case "hud.action_panel.rows": return 71;
                case "hud.action_panel.frame": return 72;
                case "hud.action_panel.modules": return 73;
                case "hud.action_panel.expansion": return 74;
                case "kernel.recycled_continuation_work": return 75;
                case "kernel.unique_write_proof_downgraded": return 76;
                case "handler.copy.journal_prefix": return 77;
                case "character.item_world.publish": return 78;
                default: return -1;
            }
        }
    }
}
