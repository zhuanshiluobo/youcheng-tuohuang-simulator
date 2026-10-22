using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditorInternal;
using UnityEditor.Profiling;
#endif

namespace YC.Tests.PlayMode.ExpansionRules
{
    /// <summary>测试侧采样/可交互帧观察；功能阶段不启用Profiler，性能阶段临时记录并恢复设置。</summary>
    internal sealed class HotseatProfilerSampler : MonoBehaviour
    {
        internal const string RuleSubmit = "YC.RuleSubmit";
        private static readonly string[] Names = { RuleSubmit, "YC.GameplaySynchronize", "YC.PlayerProjection",
            "YC.GameplayUiRefresh", "YC.RoomContentValidation", "YC.RoomContentHash", "YC.RoomRender",
            "YC.MapInfluenceRefresh", "YC.MapInfluenceSnapshot",
            "YC.MapResourceTokenRefresh", "YC.MapResourceTokenSnapshot",
            "YC.LocalPlayerUiRefresh", "YC.ActionPanelRefresh",
            "YC.BuildInfoRefresh", "YC.ActionPanelViewModel" };
        private const int Capacity = 16384;
        // 默认选项会按帧合并并环绕覆盖。此处逐调用采集、只收当前主线程、满缓冲直接失败。
        private const ProfilerRecorderOptions Options = ProfilerRecorderOptions.StartImmediately |
            ProfilerRecorderOptions.CollectOnlyOnCurrentThread;
        private readonly Dictionary<string, Channel> channels = new Dictionary<string, Channel>();
        private readonly List<Sample> samples = new List<Sample>();
        private readonly List<object> checkpoints = new List<object>();
        private readonly List<string> faults = new List<string>();
        private string directory, phase = "无输入帧", requestId = "", requestType = "", inputLabel = "";
        private int inputId, lastFrame = -1;
        private int recorderThreadId;
        private long lastReadTick;
        private bool finished;
        internal bool Performance { get; private set; }
        private int iteration, warmupIterations;
        private readonly List<ReadyMeasurement> readyMeasurements = new List<ReadyMeasurement>();
        private ReadyWatch readyWatch;
        private int lastGameCameraFrame = -1, lastCanvasFrame = -1;
        private long endOfFrameSerial;
        private string runToken;
        private readonly Dictionary<string, ProfilerMarker> nativeBoundaries = new Dictionary<string, ProfilerMarker>();
        private readonly List<NativeSample> nativeSamples = new List<NativeSample>();
        private readonly List<string> nativeLimitations = new List<string>();
        private bool nativeActive, profilerChanged, previousProfilerEnabled, previousBinaryLog, previousDeepProfile;
        private string previousProfilerLog;
        private int nextNativeFrame;

        private sealed class NativeSample
        {
            public string Name, ParentName;
            public int Frame, ThreadIndex, SampleIndex, ParentIndex, SubtreeEnd;
            public ulong ThreadId, StartNs, DurationNs, ParentStartNs, ParentDurationNs;
            public bool TreeProven;
            public ulong? ExcludingDescendantProjectionNs;
        }

        internal sealed class ReadyTarget
        {
            internal GameObject Control;
            internal string StateKey, Criterion, Operation;
        }
        internal sealed class ReadyMeasurement
        {
            public string Scenario, Operation, RequestType, RequestId, Criterion, StateKey, TargetPath, SampleClass;
            public string ReleaseMarker, ReadyMarker;
            public int Iteration, InputId, ReleaseFrame, FirstConditionFrame, ObservedFrame;
            public long ReleaseTick, FirstConditionTick, ReadyTick;
            public double ElapsedMs;
            public bool Valid;
            public string Failure, RenderProof;
            public int? RoomBuildCountBefore, RoomBuildCountAfter;
        }
        private sealed class ReadyWatch
        {
            internal Func<ReadyTarget> Resolve;
            internal Func<int?> RoomBuildCount;
            internal ReadyMeasurement Measurement;
            internal string CandidateKey;
            internal int CandidateRenderedFrames;
            internal long CandidateEndOfFrameSerial;
        }

        private sealed class Channel
        {
            internal ProfilerRecorder Recorder;
            internal string Name, Category;
            internal int ReadIndex, AttachedFrame;
            internal long Calls, Nanoseconds;
        }

        private sealed class Sample
        {
            public string Marker, Phase, RequestId, RequestType, InputLabel;
            public int SampleIndex, InputId, ObservedFrame, PreviousReadFrame;
            public long Value, Count, PreviousReadTick, ObservedTick;
            public string Unit = "TimeNanoseconds";
            public bool Inclusive = true;
        }

        internal static HotseatProfilerSampler Create(string directory, bool performance, int iteration, int warmupIterations)
        {
            var owner = new GameObject("热座性能采样") { hideFlags = HideFlags.DontSave };
            DontDestroyOnLoad(owner);
            var result = owner.AddComponent<HotseatProfilerSampler>();
            result.directory = directory;
            result.Performance = performance; result.iteration = iteration; result.warmupIterations = warmupIterations;
            result.runToken = Guid.NewGuid().ToString("N");
            result.recorderThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            result.lastReadTick = Stopwatch.GetTimestamp();
            try
            {
                if (performance)
                {
                    result.BeginNativeCapture();
                    Camera.onPostRender += result.AfterCamera;
                    RenderPipelineManager.endFrameRendering += result.AfterPipeline;
                    Canvas.willRenderCanvases += result.BeforeCanvasRender;
                    result.StartCoroutine(result.RenderedFrames());
                    result.Discover();
                }
            }
            catch { result.Finish(false); Destroy(owner); throw; }
            return result;
        }

        internal void Discover()
        {
            if (finished || !Performance) return;
            Drain();
            var available = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(available);
            foreach (var handle in available)
            {
                var description = ProfilerRecorderHandle.GetDescription(handle);
                if (!Names.Contains(description.Name) || channels.ContainsKey(description.Name)) continue;
                if (description.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds || description.DataType != ProfilerMarkerDataType.Int64)
                {
                    Fault("不支持的marker单位/类型：" + description.Name + "/" + description.UnitType + "/" + description.DataType);
                    continue;
                }
                var recorder = new ProfilerRecorder(handle, Capacity, Options);
                if (!recorder.Valid || !recorder.IsRunning)
                {
                    recorder.Dispose();
                    Fault("marker存在但Recorder未有效运行：" + description.Name);
                    continue;
                }
                channels.Add(description.Name, new Channel { Name = description.Name, Category = description.Category.Name,
                    Recorder = recorder, AttachedFrame = Time.frameCount });
                checkpoints.Add(new { kind = "连接已注册标记", marker = description.Name,
                    category = description.Category.Name, flags = description.Flags.ToString(),
                    unit = description.UnitType.ToString(), dataType = description.DataType.ToString(),
                    frame = Time.frameCount, tick = Stopwatch.GetTimestamp(),
                    note = "连接前调用未采样，不补零、不追溯为冷启动通过" });
            }
            CheckHealthy();
        }

        private void LateUpdate()
        {
            if (finished || directory == null) return;
            try { PollReady(false); Drain(); if (readyWatch == null) CollectNativeFrames(); }
            catch (Exception exception) { Fault("LateUpdate采样失败：" + exception); }
        }

        private void Drain()
        {
            if (finished || !Performance) return;
            var tick = Stopwatch.GetTimestamp();
            foreach (var channel in channels.Values)
            {
                var recorder = channel.Recorder;
                if (!recorder.Valid || !recorder.IsRunning)
                {
                    Fault("Recorder失效或停止：" + channel.Name);
                    continue;
                }
                if (recorder.WrappedAround || recorder.Count >= recorder.Capacity)
                {
                    Fault("采样缓冲达到容量，无法证明计数完整：" + channel.Name);
                    continue;
                }
                if (recorder.Count < channel.ReadIndex)
                {
                    Fault("Recorder计数倒退/被重置：" + channel.Name);
                    continue;
                }
                while (channel.ReadIndex < recorder.Count)
                {
                    var value = recorder.GetSample(channel.ReadIndex);
                    if (value.Count != 1 || value.Value < 0)
                        Fault("逐调用采样合同不成立：" + channel.Name + " count=" + value.Count + " value=" + value.Value);
                    samples.Add(new Sample { Marker = channel.Name, SampleIndex = channel.ReadIndex++,
                        Value = value.Value, Count = value.Count, ObservedFrame = Time.frameCount, PreviousReadFrame = lastFrame,
                        ObservedTick = tick, PreviousReadTick = lastReadTick, Phase = phase, InputId = inputId,
                        InputLabel = inputLabel, RequestId = requestId, RequestType = requestType });
                    channel.Calls += value.Count;
                    channel.Nanoseconds += value.Value;
                }
            }
            // 空帧也保留有效性与累计数。0只有在有效且已出现正样本的marker上才可解释为无调用。
            if (lastFrame != Time.frameCount)
                checkpoints.Add(new { kind = "帧观察", frame = Time.frameCount, previousReadFrame = lastFrame,
                    tick, phase, inputId, inputLabel, requestId, requestType,
                    markers = Names.Select(name => channels.TryGetValue(name, out var c)
                        ? new { name, registered = true, valid = c.Recorder.Valid, running = c.Recorder.IsRunning,
                            records = c.ReadIndex, calls = (long?)c.Calls }
                        : new { name, registered = false, valid = false, running = false, records = 0, calls = (long?)null }).ToArray() });
            lastFrame = Time.frameCount;
            lastReadTick = tick;
        }

        internal void SetRequest(string id, string type)
        {
            Drain();
            requestId = id ?? ""; requestType = type ?? "";
            Point("请求观察开始");
        }

        internal void BeginInput(int id, string label, bool cardGesture, double gestureWindowMs)
        {
            Discover();
            inputId = id; inputLabel = label;
            Point("输入准备", new { cardGesture, configuredGestureWindowMs = gestureWindowMs });
        }

        internal void Point(string nextPhase, object detail = null)
        {
            if (finished || !Performance) return;
            Drain();
            phase = nextPhase;
            checkpoints.Add(new { kind = "输入/等待边界", frame = Time.frameCount, tick = Stopwatch.GetTimestamp(),
                phase, inputId, inputLabel, requestId, requestType, detail });
            CheckHealthy();
        }

        internal long LocalSubmitCount()
        {
            if (!Performance) return -1; // 未测哨兵；普通功能阶段不能把它写成零调用通过。
            Drain();
            CheckHealthy();
            Assert.That(channels.ContainsKey(RuleSubmit), Is.True,
                "未找到已注册YC.RuleSubmit；不能把缺失标记的0作为提交计数。须确认ENABLE_PROFILER和生产标记。");
            return channels[RuleSubmit].Calls;
        }

        internal void AssertLocalSubmissions(long before, long expected, string label)
        {
            if (!Performance) return;
            var actual = LocalSubmitCount() - before;
            Point("本地提交次数断言", new { label, before, actual, expected,
                boundary = "主线程唯一local session.Submit调用，包含拒绝；不适用于网络转发" });
            Assert.That(actual, Is.EqualTo(expected), label + "实际YC.RuleSubmit调用次数不符；无采样不能当通过");
        }

        internal void ValidateCoverage()
        {
            if (!Performance) return;
            Discover();
            foreach (var name in Names)
            {
                Assert.That(channels.ContainsKey(name), Is.True, "必需生产marker未注册：" + name);
                var channel = channels[name];
                Assert.That(channel.Calls, Is.GreaterThan(0), "marker没有正调用样本，不能认为耗时为0：" + name);
                Assert.That(channel.Nanoseconds, Is.GreaterThan(0), "marker没有正耗时样本：" + name);
            }
        }

        private void Fault(string message) { if (!faults.Contains(message)) faults.Add(message); }
        private void CheckHealthy() => Assert.That(faults, Is.Empty, "Profiler采样不完整：" + string.Join("；", faults));

        private void AfterCamera(Camera camera)
        {
            if (camera != null && camera.cameraType == CameraType.Game && camera.targetTexture == null && camera.targetDisplay == 0)
                lastGameCameraFrame = Time.frameCount;
        }
        private void AfterPipeline(ScriptableRenderContext context, Camera[] cameras)
        { foreach (var camera in cameras) AfterCamera(camera); }
        private void BeforeCanvasRender() => lastCanvasFrame = Time.frameCount;
        private IEnumerator RenderedFrames()
        {
            while (!finished)
            {
                yield return new WaitForEndOfFrame();
                endOfFrameSerial++;
                PollReady(true);
            }
        }

        internal void PrepareReadyWatch(Func<ReadyTarget> resolve, string operation, Func<int?> roomBuildCount = null)
        {
            if (!Performance || resolve == null) return;
            Assert.That(readyWatch, Is.Null, "前一个端到端观察尚未结束");
            var measurement = new ReadyMeasurement { Scenario = NUnit.Framework.TestContext.CurrentContext.Test.MethodName,
                Operation = operation, InputId = inputId, RequestType = requestType, RequestId = requestId,
                ReleaseTick = 0, ReleaseFrame = -1, Iteration = iteration,
                FirstConditionFrame = -1, SampleClass = iteration == 0 ? "冷态（首次）" : iteration <= warmupIterations ? "预热" : "热候选",
                RoomBuildCountBefore = roomBuildCount?.Invoke(),
                ReleaseMarker = "YC.Hotseat.Release." + runToken + "." + inputId,
                ReadyMarker = "YC.Hotseat.Ready." + runToken + "." + inputId };
            readyMeasurements.Add(measurement);
            readyWatch = new ReadyWatch { Resolve = resolve, Measurement = measurement, RoomBuildCount = roomBuildCount };
            RegisterNativeBoundary(measurement.ReleaseMarker);
            RegisterNativeBoundary(measurement.ReadyMarker);
        }

        internal void ReleaseReadyWatch(long tick)
        {
            if (readyWatch == null) return;
            readyWatch.Measurement.ReleaseTick = tick;
            readyWatch.Measurement.ReleaseFrame = Time.frameCount;
            NativeBoundary(readyWatch.Measurement.ReleaseMarker);
        }

        internal bool ReadyWatchCompleted => readyWatch == null;
        internal void ObserveReadyNow() => PollReady(false);
        private void PollReady(bool afterEndOfFrame)
        {
            if (readyWatch == null || readyWatch.Measurement.ReleaseTick == 0 || finished) return;
            var current = readyWatch.Resolve();
            if (current == null || current.Control == null ||
                !HotseatUiInputDriver.TryPoint(current.Control, out _, out _))
            { readyWatch.CandidateKey = null; return; }
            var key = current.StateKey + ":" + current.Control.GetInstanceID();
            if (key != readyWatch.CandidateKey)
            {
                readyWatch.CandidateKey = key;
                readyWatch.CandidateRenderedFrames = Time.renderedFrameCount;
                readyWatch.CandidateEndOfFrameSerial = endOfFrameSerial;
                readyWatch.Measurement.FirstConditionFrame = Time.frameCount;
                readyWatch.Measurement.FirstConditionTick = Stopwatch.GetTimestamp();
                // 当前EndOfFrame才首次观察到条件成立时，不能倒推它在渲染前已成立。
                return;
            }
            var value = readyWatch.Measurement;
            var renderedAtEnd = afterEndOfFrame && endOfFrameSerial > readyWatch.CandidateEndOfFrameSerial &&
                Math.Max(lastGameCameraFrame, lastCanvasFrame) >= value.FirstConditionFrame;
            var renderedBeforeThisFrame = Time.frameCount > value.FirstConditionFrame &&
                Time.renderedFrameCount > readyWatch.CandidateRenderedFrames &&
                Math.Max(lastGameCameraFrame, lastCanvasFrame) >= value.FirstConditionFrame;
            if (!renderedAtEnd && !renderedBeforeThisFrame) return;
            value.ReadyTick = Stopwatch.GetTimestamp(); value.ObservedFrame = Time.frameCount;
            value.ElapsedMs = (value.ReadyTick - value.ReleaseTick) * 1000d / Stopwatch.Frequency;
            value.Valid = true; value.Criterion = current.Criterion; value.StateKey = current.StateKey;
            value.Operation = current.Operation ?? value.Operation;
            value.TargetPath = HotseatUiInputDriver.PathOf(current.Control);
            value.RenderProof = renderedAtEnd ? "已观察Game相机/Canvas渲染回调且已到条件成立后的EndOfFrame" :
                "已观察Game相机/Canvas渲染回调且renderedFrameCount推进，当前已进入后续帧";
            value.RoomBuildCountAfter = readyWatch.RoomBuildCount?.Invoke();
            if (value.RoomBuildCountBefore.HasValue && value.RoomBuildCountAfter > value.RoomBuildCountBefore)
                value.SampleClass = "冷态（目录重建）";
            if (value.Operation.Contains("冷态")) value.SampleClass = "冷态（场景加载）";
            NativeBoundary(value.ReadyMarker);
            readyWatch = null;
        }

        internal void AbandonReadyWatch(string reason)
        {
            if (readyWatch == null) return;
            readyWatch.Measurement.Failure = reason;
            readyWatch = null;
        }

        private void RegisterNativeBoundary(string name)
        {
            if (nativeActive && !nativeBoundaries.ContainsKey(name)) nativeBoundaries.Add(name, new ProfilerMarker(name));
        }
        private void NativeBoundary(string name)
        {
            if (nativeActive && nativeBoundaries.TryGetValue(name, out var marker))
            { marker.Begin(); marker.End(); }
        }

        private void BeginNativeCapture()
        {
#if UNITY_EDITOR
            previousProfilerEnabled = UnityEngine.Profiling.Profiler.enabled;
            previousBinaryLog = UnityEngine.Profiling.Profiler.enableBinaryLog;
            previousProfilerLog = UnityEngine.Profiling.Profiler.logFile;
            previousDeepProfile = ProfilerDriver.deepProfiling;
            Assert.That(previousDeepProfile, Is.False, "当前已有Deep Profile；不更改该设置，不在此环境宣称性能达标。");
            if (previousBinaryLog)
            {
                nativeLimitations.Add("已有二进制Profiler记录；不重定向外部文件，保留Recorder inclusive数据。");
                return;
            }
            // 不改profileEditor/连接目标/内存上限/Deep Profile或日志路径等持久设置。
            nextNativeFrame = ProfilerDriver.lastFrameIndex + 1;
            if (!previousProfilerEnabled)
            {
                profilerChanged = true;
                UnityEngine.Profiling.Profiler.enabled = true;
            }
            nativeActive = true;
#else
            nativeLimitations.Add("Player没有Editor RawFrameDataView，仅保留Recorder inclusive数据。");
#endif
        }

        private void CollectNativeFrames()
        {
#if UNITY_EDITOR
            if (!nativeActive || finished) return;
            try
            {
                var first = ProfilerDriver.firstFrameIndex;
                var last = ProfilerDriver.lastFrameIndex;
                if (last < 0) return;
                if (nextNativeFrame < first)
                {
                    nativeLimitations.Add("Profiler帧已被覆盖：" + nextNativeFrame + ".." + (first - 1));
                    nextNativeFrame = first;
                }
                if (nextNativeFrame > last + 1)
                {
                    nativeLimitations.Add("Profiler帧序号发生重置，不能证明整个采样窗口连续。");
                    nextNativeFrame = first;
                }
                for (; nextNativeFrame <= last; nextNativeFrame++)
                for (var thread = 0; thread < 128; thread++)
                {
                    using (var frame = ProfilerDriver.GetRawFrameDataView(nextNativeFrame, thread))
                    {
                        if (!frame.valid) break;
                        if (frame.threadName != "Main Thread") continue;
                        var stack = new Stack<int>();
                        var frameSamples = new List<NativeSample>();
                        for (var index = 0; index < frame.sampleCount; index++)
                        {
                            while (stack.Count > 0 && index > stack.Peek() + frame.GetSampleChildrenCountRecursive(stack.Peek())) stack.Pop();
                            var parent = stack.Count == 0 ? -1 : stack.Peek();
                            var descendants = frame.GetSampleChildrenCountRecursive(index);
                            var name = frame.GetSampleName(index);
                            if (Names.Contains(name) || nativeBoundaries.ContainsKey(name))
                            {
                                var item = new NativeSample { Name = name, Frame = nextNativeFrame, ThreadIndex = thread,
                                    ThreadId = frame.threadId, SampleIndex = index, ParentIndex = parent, SubtreeEnd = index + descendants,
                                    StartNs = frame.GetSampleStartTimeNs(index), DurationNs = frame.GetSampleTimeNs(index),
                                    ParentName = parent < 0 ? null : frame.GetSampleName(parent),
                                    ParentStartNs = parent < 0 ? 0 : frame.GetSampleStartTimeNs(parent),
                                    ParentDurationNs = parent < 0 ? 0 : frame.GetSampleTimeNs(parent) };
                                item.TreeProven = item.SubtreeEnd < frame.sampleCount && (parent < 0 ||
                                    item.StartNs >= item.ParentStartNs && item.StartNs + item.DurationNs <= item.ParentStartNs + item.ParentDurationNs);
                                frameSamples.Add(item);
                            }
                            if (descendants > 0) stack.Push(index);
                        }
                        foreach (var ui in frameSamples.Where(s => s.Name == "YC.GameplayUiRefresh" && s.TreeProven))
                        {
                            var children = frameSamples.Where(s => s.Name == "YC.PlayerProjection" && s.TreeProven &&
                                s.SampleIndex > ui.SampleIndex && s.SampleIndex <= ui.SubtreeEnd &&
                                s.StartNs >= ui.StartNs && s.StartNs + s.DurationNs <= ui.StartNs + ui.DurationNs)
                                .OrderBy(s => s.StartNs).ToArray();
                            ulong union = 0, end = 0;
                            foreach (var child in children)
                            {
                                var childEnd = child.StartNs + child.DurationNs;
                                if (childEnd > end) union += childEnd - Math.Max(end, child.StartNs);
                                end = Math.Max(end, childEnd);
                            }
                            // 只减同帧、同线程、同一父调用子树且时间包含成立的投影区间并集。
                            ui.ExcludingDescendantProjectionNs = ui.DurationNs - union;
                        }
                        nativeSamples.AddRange(frameSamples);
                        break;
                    }
                }
            }
            catch (Exception error)
            {
                nativeLimitations.Add("RawFrameDataView不可安全读取，保留inclusive上界：" + error.GetType().Name + ":" + error.Message);
                nativeActive = false;
            }
#endif
        }

        private void RestoreNativeCapture()
        {
#if UNITY_EDITOR
            if (profilerChanged)
            {
                UnityEngine.Profiling.Profiler.enabled = previousProfilerEnabled;
                profilerChanged = false;
            }
#endif
            nativeActive = false;
        }

        internal void Finish(bool completed)
        {
            if (finished) return;
            Drain();
            AbandonReadyWatch("测试结束前端到端判据未完成");
            try { CollectNativeFrames(); }
            finally
            {
                RestoreNativeCapture();
                Camera.onPostRender -= AfterCamera;
                RenderPipelineManager.endFrameRendering -= AfterPipeline;
                Canvas.willRenderCanvases -= BeforeCanvasRender;
                StopAllCoroutines();
            }
            Directory.CreateDirectory(directory);
            var report = new
            {
                completed, stage = Performance ? "Performance" : "Function", iteration, warmupIterations,
                unity = UnityEngine.Application.unityVersion, stopwatchFrequency = Stopwatch.Frequency,
                profilerTimeUnit = "nanoseconds", nanosecondsPerMillisecond = 1000000, mainThreadOnly = true,
                nanosecondsPerSecond = 1000000000, recorderThreadId,
                cadence = "逐marker调用；LateUpdate及输入/断言检查点读取，不是固定Hz采样",
                environment = new { platform = UnityEngine.Application.platform.ToString(),
                    graphics = SystemInfo.graphicsDeviceType.ToString(), Screen.width, Screen.height,
                    QualitySettings.vSyncCount, targetFrameRate = UnityEngine.Application.targetFrameRate,
                    developmentBuild = UnityEngine.Debug.isDebugBuild },
                options = Options.ToString(), capacity = Capacity, faults,
                frameAttribution = "ObservedFrame是读取帧；Recorder样本没有起止tick/原生frameId。PreviousReadTick到ObservedTick是观察区间，不是精确marker起止。",
                inclusive = "每条为marker含子调用时间；Synchronize、Projection、UiRefresh、RoomRender可能嵌套，不相加冒充端到端或独占UI耗时。",
                performanceVerdict = Performance ? "端到端原始时钟和分段样本；跨重复同类样本须预热至少5、有效热样本至少30才可判P95。" :
                    "普通功能阶段不启用计时/提交计数，不把未测值当零通过。",
                nativeLimitations,
                nativeSettings = new { inspected = Performance, enabledBefore = previousProfilerEnabled, binaryBefore = previousBinaryLog,
                    logFileBefore = previousProfilerLog, deepBefore = previousDeepProfile,
                    enabledAfter = UnityEngine.Profiling.Profiler.enabled,
                    binaryAfter = UnityEngine.Profiling.Profiler.enableBinaryLog,
                    logFileAfter = UnityEngine.Profiling.Profiler.logFile,
                    note = "仅临时修改enabled并恢复；未改二进制日志、Deep Profile、连接目标或持久设置" },
                thresholdsMs = new { hotOperationP95 = 300, ruleSubmitP95 = 200, uiBodyP95 = 100,
                    roomWarmValidationAndHashP95 = 20, roomButtonP95 = 200 },
                markers = Names.Select(name =>
                {
                    channels.TryGetValue(name, out var c);
                    var values = samples.Where(s => s.Marker == name).Select(s => s.Value).OrderBy(v => v).ToArray();
                    return new { name, registered = c != null, unit = "TimeNanoseconds", inclusive = true,
                        attachedFrame = c?.AttachedFrame, rawSampleRecords = values.Length, invocationCount = c?.Calls,
                        totalNanoseconds = c?.Nanoseconds, allSamplesP95Ms = values.Length == 0 ? (double?)null :
                            values[(int)Math.Ceiling(values.Length * .95) - 1] / 1000000d,
                        verdict = values.Length == 0 ? "缺采样" : "全样本描述统计；未过滤冷态/断言投影，不作热操作P95判定" };
                }).ToArray()
            };
            try
            {
                File.WriteAllText(Path.Combine(directory, "profiler-summary.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
                File.WriteAllLines(Path.Combine(directory, "profiler-samples.jsonl"), samples.Select(s => JsonConvert.SerializeObject(s)));
                File.WriteAllLines(Path.Combine(directory, "profiler-checkpoints.jsonl"), checkpoints.Select(JsonConvert.SerializeObject));
                File.WriteAllLines(Path.Combine(directory, "readiness-samples.jsonl"), readyMeasurements.Select(JsonConvert.SerializeObject));
                File.WriteAllLines(Path.Combine(directory, "native-profiler-tree.jsonl"), nativeSamples.Select(JsonConvert.SerializeObject));
                File.WriteAllLines(Path.Combine(directory, "operation-metrics.jsonl"), readyMeasurements.Select(value =>
                {
                    var group = samples.Where(s => s.InputId == value.InputId).ToArray();
                    var release = nativeSamples.Where(s => s.Name == value.ReleaseMarker).ToArray();
                    var ready = nativeSamples.Where(s => s.Name == value.ReadyMarker).ToArray();
                    var exact = release.Length == 1 && ready.Length == 1 && ready[0].StartNs >= release[0].StartNs &&
                        release[0].ThreadId == ready[0].ThreadId &&
                        nativeLimitations.Count == 0;
                    var within = exact ? nativeSamples.Where(s => s.StartNs >= release[0].StartNs &&
                        s.StartNs + s.DurationNs <= ready[0].StartNs && s.ThreadId == release[0].ThreadId).ToArray() : Array.Empty<NativeSample>();
                    var ui = within.Where(s => s.Name == "YC.GameplayUiRefresh").ToArray();
                    return JsonConvert.SerializeObject(new { value.Scenario, value.Operation, value.RequestType, value.InputId,
                        value.Iteration, value.SampleClass, value.Valid, value.Failure, value.ElapsedMs,
                        value.ReleaseTick, value.ReadyTick, value.RenderProof, value.Criterion,
                        nativeClockCorrelated = exact,
                        nativeEndToEndMs = exact ? (double?)((ready[0].StartNs - release[0].StartNs) / 1000000d) : null,
                        ruleCalls = group.Where(s => s.Marker == RuleSubmit).Sum(s => s.Count),
                        ruleInclusiveMs = group.Any(s => s.Marker == RuleSubmit) ?
                            (double?)group.Where(s => s.Marker == RuleSubmit).Sum(s => s.Value) / 1000000d : null,
                        roomValidationAndHashMarkedUnionMs = exact ? (double?)IntervalUnionNs(within.Where(s =>
                            s.Name == "YC.RoomContentValidation" || s.Name == "YC.RoomContentHash")) / 1000000d : null,
                        uiBlockInclusiveMs = ui.Length > 0 ? (double?)IntervalUnionNs(ui) / 1000000d : null,
                        uiBlocks = ui.Select(s => new { s.Frame, s.ThreadId, s.SampleIndex, s.ParentIndex, s.TreeProven,
                            s.DurationNs, s.ExcludingDescendantProjectionNs }).ToArray(),
                        uiCoverage = "仅已标记UI块；不等价于全部UI本体。只报告已证明子树中的排除投影结果，不越父调用相减。" });
                }));
            }
            finally
            {
                finished = true;
                foreach (var channel in channels.Values) channel.Recorder.Dispose();
            }
        }

        private static ulong IntervalUnionNs(IEnumerable<NativeSample> source)
        {
            ulong total = 0, end = 0;
            foreach (var sample in source.OrderBy(s => s.StartNs))
            {
                var next = sample.StartNs + sample.DurationNs;
                if (next > end) total += next - Math.Max(end, sample.StartNs);
                end = Math.Max(end, next);
            }
            return total;
        }

        private void OnDestroy()
        {
            if (!finished && directory != null) Finish(false);
        }
    }
}
