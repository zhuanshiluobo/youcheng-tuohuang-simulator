using System;
using System.IO;
using System.Threading;
using UnityEngine;
using YC.Application.Sessions;
using YC.Infrastructure.Multiplayer;
using YC.Infrastructure.Persistence;

namespace YC.Presentation
{
    internal readonly struct MatchSavePerformanceOptions
    {
        internal readonly bool Performance, DisableAutomatic, Trace, Weak;
        internal MatchSavePerformanceOptions(bool performance, bool off, bool trace)
            : this(performance, off, trace, false) { }
        internal MatchSavePerformanceOptions(bool performance, bool off, bool trace, bool weak)
        { Performance = performance; DisableAutomatic = off; Trace = trace; Weak = weak; }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal static MatchSavePerformanceOptions Parse(string[] args, string saveRoot, string dataPath)
        {
            bool performance = Value(args, "--yc-ui-performance") == "true";
            if (!performance) return default;
            string automatic = Value(args, "--yc-perf-autosave"), trace = Value(args, "--yc-perf-save-trace");
            string weak = Value(args, "--yc-perf-save-weak");
            if (automatic != null && automatic != "on" && automatic != "off")
                throw new InvalidOperationException("自动保存实验参数只支持 on 或 off。");
            if (trace != null && trace != "true" && trace != "false")
                throw new InvalidOperationException("保存诊断参数只支持 true 或 false。");
            if (weak != null && weak != "true" && weak != "false")
                throw new InvalidOperationException("保存弱观察参数只支持 true 或 false。");
            if (automatic != null || trace == "true" || weak == "true")
            {
                if (Value(args, "--yc-performance-save-root") == null)
                    throw new InvalidOperationException("保存性能实验必须显式提供独立存档目录。");
                var ancestor = new DirectoryInfo(Path.GetFullPath(dataPath));
                bool projectFound = false;
                for (int i = 0; i < 10 && ancestor != null; i++, ancestor = ancestor.Parent)
                    if (Directory.Exists(Path.Combine(ancestor.FullName, "Assets")) && Directory.Exists(Path.Combine(ancestor.FullName, "ProjectSettings"))) { projectFound = true; break; }
                if (!projectFound)
                    throw new InvalidOperationException("保存性能实验必须从当前项目内运行。");
                string project = ancestor.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(saveRoot).StartsWith(project, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("性能存档目录必须位于当前项目内。");
            }
            return new MatchSavePerformanceOptions(true, automatic == "off", trace == "true", weak == "true");
        }
        private static string Value(string[] args, string key)
        {
            string value = null; bool found = false;
            foreach (string arg in args)
                if (arg != null && (arg == key || arg.StartsWith(key + "=", StringComparison.Ordinal)))
                {
                    if (found || arg == key) throw new InvalidOperationException("性能参数重复或缺少值：" + key);
                    found = true; value = arg.Substring(key.Length + 1);
                }
            return value;
        }
#endif
    }

    // 实例只含少量身份标量；静态sample只包围同线程同步段。
    internal sealed class MatchSavePerformanceTrace : IMatchSaveTrace
    {
        private readonly NetworkExecutionObservation.Context identity;
        private readonly MatchSaveWeakObservation weak;
        private int revision;
        internal int SourceRevision => Volatile.Read(ref revision);
        internal MatchSavePerformanceTrace(NetworkExecutionObservation.Context identity)
            : this(identity, null) { }
        internal MatchSavePerformanceTrace(NetworkExecutionObservation.Context identity, MatchSaveWeakObservation weak)
        { this.identity = identity; this.weak = weak; revision = -1; }
        public void SetSourceRevision(int value) { weak?.SetSourceRevision(value); Volatile.Write(ref revision, value); Point("save.source"); }
        private NetworkExecutionObservation.Context Context()
        { var context = identity; context.Revision = Volatile.Read(ref revision); return context; }
        public void Point(string stage)
        { try { NetworkExecutionObservation.Event(stage, Context()); } catch { } }
        public IDisposable Begin(string stage)
        {
            var scope = new SynchronousScope(stage, Context());
            scope.Start(); return scope;
        }
        private sealed class SynchronousScope : IDisposable
        {
            private readonly string stage;
            private readonly NetworkExecutionObservation.Context identity;
            private bool sampled;
            private int disposed;
            internal SynchronousScope(string stage, NetworkExecutionObservation.Context identity)
            { this.stage = stage; this.identity = identity; }
            internal void Start()
            {
                try { UnityEngine.Profiling.Profiler.BeginSample(stage); sampled = true; } catch { }
                try { NetworkExecutionObservation.Event(stage, identity, "begin"); } catch { }
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                try { NetworkExecutionObservation.Event(stage, identity, "end"); }
                catch { }
                finally { if (sampled) { try { UnityEngine.Profiling.Profiler.EndSample(); } catch { } } }
            }
        }
    }
}
