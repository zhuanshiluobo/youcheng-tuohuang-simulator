using System;
using System.Diagnostics;
using System.Threading;

namespace YC.Presentation.Workflows
{
    /// <summary>默认关闭的有限UI观测桥；不持有State/Command，不负责规则或文件输出。</summary>
    public static class InteractionLatencyTrace
    {
        public readonly struct Sample
        {
            public readonly string Name, Edge, CommandId, Detail;
            public readonly long SpanId, ParentId, ElapsedTicks, Value;
            public Sample(string name, string edge, string commandId, string detail,
                long spanId, long parentId, long elapsedTicks, long value)
            { Name = name; Edge = edge; CommandId = commandId; Detail = detail;
                SpanId = spanId; ParentId = parentId; ElapsedTicks = elapsedTicks; Value = value; }
        }

        private static Action<Sample> sink;
        private static int enabled, emitted, maximum;
        private static long nextId, deadline;
        [ThreadStatic] private static long parent;
        [ThreadStatic] private static string command;
        public static bool Enabled => Volatile.Read(ref enabled) != 0;
        public static int Emitted => Volatile.Read(ref emitted);

        // 仅由显式开发诊断入口调用。包含终止行，最多8192项、最多300秒。
        public static bool TryStart(Action<Sample> target, int maximumEvents = 8192, int seconds = 300)
        {
            if (target == null || Enabled || maximumEvents < 2 || maximumEvents > 8192 || seconds < 1 || seconds > 300) return false;
            sink = target; maximum = maximumEvents; emitted = 0; nextId = 0;
            deadline = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
            parent = 0; command = null; Volatile.Write(ref enabled, 1);
            Point("trace.started", detail: "bounded;clock=Stopwatch;elapsed=wall;not-exclusive-cpu");
            return true;
        }

        public readonly struct Scope : IDisposable
        {
            private readonly string name, commandId, previousCommand;
            private readonly long id, previous, started;
            internal Scope(string name, string commandId)
            {
                this.name = name; previous = parent; previousCommand = command;
                this.commandId = commandId ?? command;
                id = Interlocked.Increment(ref nextId); started = Stopwatch.GetTimestamp();
                parent = id; command = this.commandId;
                Emit(new Sample(name, "begin", this.commandId, null, id, previous, 0, 0));
            }
            public void Dispose()
            {
                if (name == null) return;
                Emit(new Sample(name, "end", commandId, null, id, previous, Stopwatch.GetTimestamp() - started, 0));
                parent = previous; command = previousCommand;
            }
        }

        public static Scope Begin(string name, string commandId = null) => Enabled ? new Scope(name, commandId) : default;
        public static void Point(string name, string commandId = null, string detail = null, long value = 0)
        {
            if (Enabled) Emit(new Sample(name, "point", commandId ?? command, detail, parent, 0, 0, value));
        }
        private static void Emit(Sample sample)
        {
            if (!Enabled) return;
            try
            {
                int count = Interlocked.Increment(ref emitted);
                if (count >= maximum || Stopwatch.GetTimestamp() >= deadline)
                { Stop("trace.limit"); return; }
                sink?.Invoke(sample);
            }
            catch { Disable(); } // 诊断失败不改变业务返回或异常。
        }
        public static void Stop(string reason = "trace.stopped")
        {
            if (Interlocked.Exchange(ref enabled, 0) == 0) return;
            var target = sink; sink = null;
            try { target?.Invoke(new Sample(reason, "point", command, "end-or-truncated", parent, 0, 0, Emitted)); }
            catch { /* 原业务不受输出失败影响。 */ }
        }
        private static void Disable() { Interlocked.Exchange(ref enabled, 0); sink = null; }
    }
}
