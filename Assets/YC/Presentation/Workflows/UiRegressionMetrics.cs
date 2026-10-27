using System;
using System.Diagnostics;

namespace YC.Presentation.Workflows
{
    // 显式启用的只读诊断；不参与请求、权限或状态更新。
    public static class UiRegressionMetrics
    {
        public static bool Enabled { get; set; }
        public static event Action<Sample> Recorded;
        public sealed class Sample
        {
            public string Phase;
            public double Milliseconds;
            public long AllocatedBytes;
            public string Detail;
        }
        public static Scope Measure(string phase) => new Scope(phase);
        public static void Note(string phase, string detail)
        { if (Enabled) Recorded?.Invoke(new Sample { Phase = phase, Detail = detail }); }
        public readonly struct Scope : IDisposable
        {
            private readonly string phase;
            private readonly long ticks, allocated;
            public Scope(string phase)
            {
                this.phase = Enabled ? phase : null;
                ticks = this.phase == null ? 0 : Stopwatch.GetTimestamp();
                allocated = this.phase == null ? 0 : GC.GetAllocatedBytesForCurrentThread();
            }
            public void Dispose()
            {
                var currentAllocated = phase == null ? 0 : GC.GetAllocatedBytesForCurrentThread();
                if (phase != null) Recorded?.Invoke(new Sample { Phase = phase,
                    Milliseconds = (Stopwatch.GetTimestamp() - ticks) * 1000d / Stopwatch.Frequency,
                    AllocatedBytes = allocated == 0 && currentAllocated == 0 ? -1 : currentAllocated - allocated });
            }
        }
    }
}
