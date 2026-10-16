using System.Collections.Generic;
using System.Diagnostics;

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

        public bool Enabled { get; set; }

        public void AddSample(string phase, long elapsedTicks)
        {
            if (!Enabled || elapsedTicks < 0) return;
            int index = PhaseIndex(phase);
            if (index < 0) return;
            // 诊断累计不能因溢出改变原执行或异常路径。
            if (calls[index] < long.MaxValue) calls[index]++;
            ticks[index] = elapsedTicks > long.MaxValue - ticks[index] ? long.MaxValue : ticks[index] + elapsedTicks;
        }

        public Dictionary<string, long> Snapshot()
        {
            var snapshot = new Dictionary<string, long>(Phases.Length * 2 + 1, System.StringComparer.Ordinal) {
                ["clock.frequency"] = Stopwatch.Frequency
            };
            for (int i = 0; i < Phases.Length; i++)
            {
                snapshot.Add(Phases[i] + ".calls", calls[i]);
                snapshot.Add(Phases[i] + ".ticks", ticks[i]);
            }
            return snapshot;
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
