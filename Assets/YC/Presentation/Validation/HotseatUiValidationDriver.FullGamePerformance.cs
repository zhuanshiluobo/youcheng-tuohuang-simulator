#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Profiling;
using YC.Domain.Commands;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        private bool FullGamePerformance => Argument("--yc-full-game-performance=") == "true";
        private StreamWriter fullGamePerformanceLog;
        private int fullGamePerformanceOrdinal;
        private Action<string,string,long> fullGameLuaTiming;
        private Action<string,long> fullGameInputDiagnostic;
        private Action<YC.Presentation.Workflows.UiRegressionMetrics.Sample> fullGameUiTiming;
        private bool fullGameUiMetricsWereEnabled;
        private bool fullGameMemoryDiagnosticsStarted, fullGameArtworkWasEnabled, fullGameIconsWereEnabled;
        private readonly HashSet<string> fullGameLuaCallers = new HashSet<string>(StringComparer.Ordinal);
        private int fullGameDriverDepth, fullGameInputDepth;
        private FixtureTimingScope MeasureFixtureWork(string phase) => fullGamePerformanceLog==null ? default : new FixtureTimingScope(this,phase);
        private readonly struct FixtureTimingScope : IDisposable
        {
            private readonly HotseatUiValidationDriver owner;
            private readonly string phase;
            private readonly long began;
            private readonly IDisposable diagnostic;
            internal FixtureTimingScope(HotseatUiValidationDriver owner,string phase)
            {
                this.owner=owner;this.phase=phase;began=Stopwatch.GetTimestamp();
                diagnostic=phase=="input.callback" && owner.fullGameInputDiagnostic!=null
                    ? YC.Domain.State.GameStateCloneService.BeginTimingScope(owner.fullGameInputDiagnostic) : null;
                if(phase=="driver.move_next.inclusive")owner.fullGameDriverDepth++;
                if(phase=="input.callback")owner.fullGameInputDepth++;
            }
            public void Dispose()
            {
                if(owner==null)return;
                diagnostic?.Dispose();
                if(phase=="driver.move_next.inclusive")owner.fullGameDriverDepth--;
                if(phase=="input.callback")owner.fullGameInputDepth--;
                owner.session?.ExecutionTimings.AddSample(phase,Stopwatch.GetTimestamp()-began);
            }
        }
        private void BindFullGameTiming()
        {
            if (!FullGamePerformance) return;
            session.ExecutionTimings.Enabled=true;
            // Submit内部另有原计时作用域；这里补测该作用域之外的产品查询，使用独立前缀。
            fullGameInputDiagnostic=(phase,ticks)=>session.ExecutionTimings.AddSample("input."+phase,ticks);
            fullGameUiMetricsWereEnabled=YC.Presentation.Workflows.UiRegressionMetrics.Enabled;
            fullGameUiTiming=sample=> {
                if(sample.Phase==null || sample.Milliseconds<0)return;
                string origin=fullGameInputDepth>0?"input":fullGameDriverDepth>0?"fixture":"frame";
                session.ExecutionTimings.AddSample("query."+origin+"."+sample.Phase,
                    (long)(sample.Milliseconds*Stopwatch.Frequency/1000d));
            };
            YC.Presentation.Workflows.UiRegressionMetrics.Recorded+=fullGameUiTiming;
            YC.Presentation.Workflows.UiRegressionMetrics.Enabled=true;
            fullGameLuaTiming=(phase,definition,ticks)=> {
                session.ExecutionTimings.AddSample(phase,ticks);
                if (phase != "lua.context") return;
                session.ExecutionTimings.AddSample("lua.context."+(fullGameInputDepth>0?"input":fullGameDriverDepth>0?"fixture":"frame"),ticks);
                session.ExecutionTimings.AddSample(phase+"."+definition,ticks);
                string key=stage+":"+definition;
                if(fullGameLuaCallers.Count < 128 && fullGameLuaCallers.Add(key))
                    Append("Lua查询调用来源.jsonl",new { Stage=stage,Definition=definition,Sample=fullGamePerformanceOrdinal,
                        Stack=new StackTrace(false).ToString(), StateRevision=session.State.EffectRuntime.StateRevision });
            };
            YC.Infrastructure.Lua.LuaEffectEventDispatcher.CpuTimingSample+=fullGameLuaTiming;
        }
        private void UnbindFullGameTiming()
        {
            if (fullGameMemoryDiagnosticsStarted)
            {
                fullGameMemoryDiagnosticsStarted = false;
                if (!fullGameArtworkWasEnabled) ExternalContentRuntime.SetMemoryDiagnosticsEnabled(false);
                if (!fullGameIconsWereEnabled) GameplayIconResourceResolver.SetMemoryDiagnosticsEnabled(false);
            }
            if(fullGameLuaTiming!=null)YC.Infrastructure.Lua.LuaEffectEventDispatcher.CpuTimingSample-=fullGameLuaTiming;
            fullGameLuaTiming=null;
            fullGameInputDiagnostic=null;
            if(fullGameUiTiming!=null)
            {
                YC.Presentation.Workflows.UiRegressionMetrics.Recorded-=fullGameUiTiming;
                YC.Presentation.Workflows.UiRegressionMetrics.Enabled=fullGameUiMetricsWereEnabled;
                fullGameUiTiming=null;
            }
            fullGameLuaCallers.Clear();
        }
        private void OnDestroy()
        {
            UnbindFullGameTiming();
            UnityEngine.Application.logMessageReceived-=OnLog;
            var log=fullGamePerformanceLog;fullGamePerformanceLog=null;
            log?.Dispose();
        }
        private sealed class FullGameSample
        {
            public int Index, Round, Revision, Frame;
            public string Name, Kind, Utc;
            public long Tick, Managed, MonoHeap, UnityAllocated, UnityReserved, WorkingSet, Private;
            public ArtworkMemorySnapshot Artwork, Icons;
            public Dictionary<string,long> Timings;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint Size, PageFaultCount;
            public UIntPtr PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool,
                PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
        private void InitializeFullGamePerformance()
        {
            if (!FullGamePerformance) return;
            BeginFullGameMemoryDiagnostics();
            fullGamePerformanceLog = new StreamWriter(Path.Combine(output, "逐步性能.jsonl"), false) { AutoFlush = true };
        }
        private void BeginFullGameMemoryDiagnostics()
        {
            if (fullGameMemoryDiagnosticsStarted) return;
            fullGameArtworkWasEnabled = ExternalContentRuntime.ReadMemoryDiagnostics().Enabled;
            fullGameIconsWereEnabled = GameplayIconResourceResolver.ReadMemoryDiagnostics().Enabled;
            ExternalContentRuntime.SetMemoryDiagnosticsEnabled(true);
            GameplayIconResourceResolver.SetMemoryDiagnosticsEnabled(true);
            fullGameMemoryDiagnosticsStarted = true;
        }
        private FullGameSample BeginFullGameSample(string kind, string name)
        {
            if (fullGamePerformanceLog == null) return null;
            var value = CaptureFullGameSample();
            value.Index = ++fullGamePerformanceOrdinal; value.Kind = kind; value.Name = name;
            fullGamePerformanceLog.WriteLine(JsonConvert.SerializeObject(new { Boundary = "begin", Sample = value }));
            value.Tick = Stopwatch.GetTimestamp(); // 日志写入不计入被测段。
            return value;
        }
        private FullGameSample CaptureFullGameSample()
        {
            var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            using (var process = Process.GetCurrentProcess())
                if (!GetProcessMemoryInfo(process.Handle, ref counters, counters.Size))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return new FullGameSample { Utc = DateTime.UtcNow.ToString("o"), Tick = Stopwatch.GetTimestamp(),
                Frame = Time.frameCount,
                Round = session?.State.Round ?? 0, Revision = session?.State.EffectRuntime.StateRevision ?? -1,
                Managed = GC.GetTotalMemory(false), MonoHeap = Profiler.GetMonoHeapSizeLong(),
                UnityAllocated = Profiler.GetTotalAllocatedMemoryLong(), UnityReserved = Profiler.GetTotalReservedMemoryLong(),
                Artwork = ExternalContentRuntime.ReadMemoryDiagnostics(), Icons = GameplayIconResourceResolver.ReadMemoryDiagnostics(),
                WorkingSet = (long)counters.WorkingSet.ToUInt64(), Private = (long)counters.PrivateUsage.ToUInt64(),
                Timings = session?.ExecutionTimings.Snapshot() };
        }
        private void EndFullGameSample(FullGameSample before, string commandId = null)
        {
            if (before == null) return;
            long ended = Stopwatch.GetTimestamp();
            var after = CaptureFullGameSample();
            var delta = new Dictionary<string,long>();
            if (after.Timings != null && before.Timings != null)
                foreach (var pair in after.Timings)
                    if (pair.Key != "clock.frequency") delta[pair.Key] = pair.Value - (before.Timings.TryGetValue(pair.Key,out long prior)?prior:0);
            double seconds = (ended - before.Tick) / (double)Stopwatch.Frequency;
            fullGamePerformanceLog.WriteLine(JsonConvert.SerializeObject(new { Boundary = "end", before.Index,
                before.Kind, before.Name, CommandId = commandId, GameId = session?.State.GameId,
                ContentHash = session?.State.ContentPackHash, Seconds = seconds, TargetSeconds = 5,
                OverFiveSeconds = seconds >= 5, OverTenSeconds = seconds >= 10,
                Before = before, After = after, DeltaWorkingSet = after.WorkingSet-before.WorkingSet,
                DeltaPrivate = after.Private-before.Private, DeltaManaged = after.Managed-before.Managed,
                TimingDelta = delta, Frequency = Stopwatch.Frequency, ForcedGc = false }));
        }
    }
}
#endif
