using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using UnityEngine;
using YC.Application.Sessions;
using YC.Infrastructure.Multiplayer;
using YC.Infrastructure.Persistence;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation
{
    public sealed class MatchSaveController : MonoBehaviour
    {
        public static MatchSaveController Instance { get; private set; }
        private MatchSaveStore store;
        private GameSession session;
        private GameLaunchContext context;
        private int generation, dirty, saved;
        private bool saving, leaving, allowQuit;
        private float retryAt;
        private string lastFailure;
        private long observationSaveId;
        private MatchSavePerformanceOptions performance = default;
        private int tracedSaveOperations;
        private int peakAutomaticWindowSaves;
        private MatchSaveWeakObservation weakObservation;
        private long weakOnlyOperationId;
        private float traceUntil;
        public bool AutomaticSavingEnabled => !performance.DisableAutomatic;
        public MatchSaveQueueStatistics PerformanceSaveStatistics => store == null ? default : store.ReadQueueStatistics();
        public int TracedSaveOperations => tracedSaveOperations;
        public int PendingAutomaticWindowSaves => windowAutoSaves.Count;
        public int PeakAutomaticWindowSaves => peakAutomaticWindowSaves;
        public bool SavePerformanceDiagnosticsRequested => performance.Trace;
        public MatchSaveWeakSnapshot ReadSaveWeakObservation()
        {
            var queue = PerformanceSaveStatistics;
            return weakObservation == null ? MatchSaveWeakSnapshot.Disabled(performance.Weak, performance.DisableAutomatic, queue) : weakObservation.ReadSnapshot(queue);
        }
        private SimulationPublishedSnapshot completedHostSnapshot;
        private MatchSaveData completedHostHeader;
        private int completedHostSaveGeneration = -1;
        private MirrorCommandTransport saveTransport;
        private Action<SimulationCommandAppliedDto> networkApplied;
        private Action<SimulationWindowSavePoint> networkWindowSaved;
        private WindowAutoSaveQueue windowAutoSaves = new WindowAutoSaveQueue();
        private int hostMutationVersion, savedHostMutationVersion;
        private Action<DeterministicSimulationCoordinator> networkRecovered;
        private CancellationTokenSource saveLifetime = new CancellationTokenSource();
        public bool CanSave => session != null && context != null && context.Mode != LaunchMode.Client;
        public string CurrentGameId => context?.Mode == LaunchMode.Local ? session?.State.GameId : context?.GameId;
        public string LastFailure => lastFailure;
        public bool HasSaves => store.HasSaves();
        public static MatchSaveController Ensure()
        {
            if (Instance != null) return Instance;
            return new GameObject(nameof(MatchSaveController)).AddComponent<MatchSaveController>();
        }
        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            var saveRoot = Path.Combine(UnityEngine.Application.persistentDataPath, "MatchSaves");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var arguments = Environment.GetCommandLineArgs();
            saveRoot = PerformanceSavePath.Select(saveRoot, arguments);
            performance = MatchSavePerformanceOptions.Parse(arguments, saveRoot, UnityEngine.Application.dataPath);
            if ((performance.Trace || performance.Weak && !performance.DisableAutomatic) && !NetworkExecutionObservation.Enabled)
                throw new InvalidOperationException("保存诊断需要同时启用 --yc-network-observation 与 --yc-network-observation-output=<绝对JSONL路径>。");
#endif
            Instance = this; DontDestroyOnLoad(gameObject);
            store = new MatchSaveStore(saveRoot, performance.Performance);
            if (performance.Weak && !performance.DisableAutomatic) weakObservation = new MatchSaveWeakObservation();
            if (performance.Trace) traceUntil = Time.unscaledTime + 300f;
            UnityEngine.Application.wantsToQuit += WantsToQuit;
        }
        private void OnDestroy()
        {
            if (Instance != this) return;
            Unbind(); saveLifetime.Dispose(); UnityEngine.Application.wantsToQuit -= WantsToQuit; Instance = null;
        }
        public Task<System.Collections.Generic.List<MatchSaveSlot>> ListSlotsAsync() => store.ListAsync();
        public Task Delete(int slot) => store.DeleteAsync(slot);
        public MatchSaveData Read(int slot)
        {
            var data = store.Read(slot);
            GameSessionBootstrapper.Restore(null, data);
            store.EnsureResumeCapacity(data.GameId); store.SetRestoreSource(slot, data.GameId);
            return data;
        }
        public void Bind(GameSession value, GameLaunchContext launch)
        {
            Unbind();
            if (value == null || launch == null || launch.Mode == LaunchMode.Client) return;
            session = value; context = launch;
            if (NetworkExecutionObservation.Enabled) session.ExecutionTimings.Enabled = true;
            if (launch.Mode == LaunchMode.Local) session.StateCommitted += MarkDirty;
            else
            {
                // 联机规则提交在执行线程；保存生命周期只接受运输层主线程交付的通知。
                saveTransport = MirrorCommandTransport.Instance;
                if (saveTransport == null) throw new InvalidOperationException("联机保存缺少当前运输绑定。");
                int epoch = generation;
                bool Current() => epoch == generation && context == launch && session == value &&
                    ReferenceEquals(MirrorCommandTransport.Instance, saveTransport);
                // 每条Applied仅用于保存退出的一致性复核，不触发自动保存。
                networkApplied = applied => { if (Current() && applied.GameId == launch.GameId && applied.RuleResult?.Succeeded == true) { hostMutationVersion++; ClearCompletedHostSave(); } };
                networkRecovered = coordinator => { if (Current() && coordinator.SessionOptions.GameId == launch.GameId) { hostMutationVersion++; ClearCompletedHostSave(); } };
                networkWindowSaved = point => {
                    if (AutomaticSavingEnabled && !leaving && Current() && point.GameId == launch.GameId && point.LocalPlayerId == launch.LocalPlayerId && windowAutoSaves.Enqueue(point) && performance.Performance)
                        peakAutomaticWindowSaves = Math.Max(peakAutomaticWindowSaves, windowAutoSaves.Count);
                };
                saveTransport.SimulationCommandApplied += networkApplied;
                saveTransport.AutomaticSavePointConfirmed += networkWindowSaved;
                saveTransport.SimulationRecoveryCompleted += networkRecovered;
            }
            store.BeginMatch(launch.Mode == LaunchMode.Local ? session.State.GameId : launch.GameId, launch.RestoreData == null);
            weakObservation?.Bind(launch.Mode == LaunchMode.Local ? session.State.GameId : launch.GameId);
            dirty = launch.Mode == LaunchMode.Local && launch.RestoreData == null ? 1 : 0; saved = 0;
        }
        private void MarkDirty() { dirty++; ClearCompletedHostSave(); }
        private void ClearCompletedHostSave()
        {
            completedHostSnapshot = null; completedHostHeader = null; completedHostSaveGeneration = -1;
        }
        public void Unbind()
        {
            weakObservation?.CloseGame(CurrentGameId);
            saveLifetime.Cancel(); saveLifetime.Dispose(); saveLifetime = new CancellationTokenSource();
            if (session != null && context?.Mode == LaunchMode.Local) { session.StateCommitted -= MarkDirty; session.CommandsSuspended = false; }
            if (!ReferenceEquals(saveTransport, null))
            {
                saveTransport.SimulationCommandApplied -= networkApplied;
                saveTransport.AutomaticSavePointConfirmed -= networkWindowSaved;
                saveTransport.SimulationRecoveryCompleted -= networkRecovered;
            }
            saveTransport = null; networkApplied = null; networkRecovered = null; networkWindowSaved = null;
            windowAutoSaves = new WindowAutoSaveQueue(); hostMutationVersion = savedHostMutationVersion = 0;
            generation++; session = null; context = null; dirty = saved = 0; saving = leaving = false;
            ClearCompletedHostSave(); lastFailure = null; retryAt = 0f; allowQuit = false; observationSaveId = 0;
        }

        internal bool TryRetainCompletedHostSave(GameLaunchContext launch, out string reason)
        {
            reason = null;
            try
            {
                if (!CanSave || context != launch || context.Mode != LaunchMode.Host || context.IsCompletedSessionDetached)
                    throw new InvalidOperationException("当前对局没有可保留的房主终局确认存档。");
                var snapshot = ReadHostSaveSnapshot();
                if (!snapshot.IsCurrentBoundaryConfirmed) throw new InvalidOperationException("终局窗口尚未由规定副本完整确认。");
                if (!snapshot.FinalScoringResolved || snapshot.Phase != GamePhase.FinalScoring && snapshot.Phase != GamePhase.GameOver)
                    throw new InvalidOperationException("终局状态尚未形成同局一致的正式确认检查点。");
                completedHostSnapshot = snapshot;
                completedHostHeader = CreateHostSaveHeader();
                completedHostSaveGeneration = generation;
                // 释放运输层后仍由原保存队列写盘；失败保留此确认点供重试。
                return true;
            }
            catch (Exception error)
            {
                ClearCompletedHostSave();
                reason = lastFailure = error.Message;
                return false;
            }
        }

        private SimulationPublishedSnapshot ReadHostSaveSnapshot()
        {
            var coordinator = MirrorCommandTransport.Instance?.SimulationCoordinator;
            if (coordinator == null || !coordinator.TryReadPublishedSnapshot(out var snapshot) ||
                snapshot.GameId != context.GameId || snapshot.LocalPlayerId != context.LocalPlayerId || !snapshot.CanCreateSaveCheckpoint)
                throw new InvalidOperationException("联机正式确认保存点尚未就绪。");
            return snapshot;
        }

        private SimulationPublishedSnapshot ReadRetainedHostSnapshot()
        {
            var snapshot = completedHostSnapshot;
            if (snapshot == null || completedHostHeader == null || completedHostSaveGeneration != generation ||
                context.Mode != LaunchMode.Host || snapshot.GameId != context.GameId || snapshot.LocalPlayerId != context.LocalPlayerId ||
                !snapshot.FinalScoringResolved || !snapshot.CanCreateSaveCheckpoint ||
                snapshot.Phase != GamePhase.FinalScoring && snapshot.Phase != GamePhase.GameOver)
                throw new InvalidOperationException("终局确认存档已失效，不能跳过保存退出。");
            return snapshot;
        }

        private async void LateUpdate()
        {
            if (!AutomaticSavingEnabled || !CanSave || saving || leaving || Time.unscaledTime < retryAt) return;
            if (context.Mode == LaunchMode.Host ? windowAutoSaves.Count == 0 : dirty == saved) return;
            int epoch = generation;
            bool ok = context.Mode == LaunchMode.Host ? await SaveAutomaticWindow() : await SaveCore(-1, true);
            if (!ok && epoch == generation) retryAt = Time.unscaledTime + 2f;
        }
        private async Task<bool> SaveAutomaticWindow()
        {
            if (!AutomaticSavingEnabled || saving || !windowAutoSaves.TryPeek(out var point)) return false;
            int epoch = generation; saving = true;
            bool wantsWeakCandidate = weakObservation?.NeedsCandidate == true;
            var identity = CanTraceSave() || wantsWeakCandidate ? SaveObservationContext() : default;
            identity.GameId = point.GameId; identity.Sequence = point.GlobalSequence; identity.SimulationEpoch = point.SimulationEpoch; identity.Revision = -1;
            var observation = CanTraceSave() ? NetworkExecutionObservation.Begin("save.total", identity) : default;
            // 32笔详细trace用完后的Host仍有独立、无逐笔事件的负数操作ID。
            observationSaveId = identity.OperationId = observation.Id != 0 ? observation.Id : wantsWeakCandidate ? --weakOnlyOperationId : 0;
            observation.Context = identity;
            var weak = wantsWeakCandidate && weakObservation.BeginCandidate(identity.OperationId, point.GameId, LaunchMode.Host) ? weakObservation : null;
            long weakId = weak?.CandidateId ?? 0;
            var trace = CreateSaveTraceWithWeak(identity, weak);
            IMatchSaveTrace operationTrace = trace ?? (IMatchSaveTrace)weak;
            MatchSaveTraceScope.Point(trace, "save.kind.host.automatic");
            bool weakSuccess = false, weakCancelled = false;
            try
            {
                var header = CreateHostSaveHeader();
                await store.SavePreparedWithValidationAsync(validation => MatchSaveData.CreateFromWindowSavePointAsync(header, point, validation), point.GameId, point.ContentHash, -1, saveLifetime.Token, operationTrace, weak);
                if (epoch != generation) return false;
                windowAutoSaves.Complete(point.Id); lastFailure = null;
                weakSuccess = true;
                MatchSaveTraceScope.Point(trace, "save.complete"); return true;
            }
            catch (Exception error)
            {
                weakCancelled = error is OperationCanceledException;
                MatchSaveTraceScope.Point(trace, error is OperationCanceledException ? "save.cancel" : "save.fail");
                if (epoch == generation) lastFailure = error.Message; return false;
            }
            finally
            {
                if (epoch == generation) { saving = false; observationSaveId = 0; }
                weak?.ControllerFinished(weakId, weakSuccess, weakCancelled, PerformanceSaveStatistics);
                if (trace != null) observation.Context.Revision = trace.SourceRevision;
                else if (weak != null) observation.Context.Revision = weak.CandidateRevision;
                observation.Dispose();
            }
        }
        public MatchSaveData Capture()
        {
            if (!CanSave) throw new InvalidOperationException("只有本地对局或房主可以保存。");
            if (context.Mode != LaunchMode.Local)
                throw new InvalidOperationException("联机存档必须通过异步保存入口从固定确认点生成。");
            return CaptureLiveSession();
        }

        private MatchSaveData CaptureLiveSession(GameSessionRecoveryExport frozen = null)
        {
            using var captureObservation = NetworkExecutionObservation.Begin("save.capture", SaveObservationContext(), true, session.ExecutionTimings);
            var export = frozen ?? session.CaptureStableSavePoint();
            var source = export.Archive.Snapshot.State;
            string archive = HostRecoveryService.SerializeArchive(export.Archive);
            string continuation = SimulationConfirmedRecoveryInstaller.SerializeContinuation(export.Continuation);
            var data = new MatchSaveData { SavedUtcTicks = DateTime.UtcNow.Ticks, Mode = context.Mode,
                LocalPlayerId = context.LocalPlayerId, LocalTestNetwork = LocalMirrorTestMode.IsEnabled,
                MapId = source.MapId, GameId = source.GameId, Round = source.Round,
                ContentHash = source.ContentPackHash, ArchiveJson = archive, ContinuationJson = continuation,
                SimulationCheckpointJson = null };
            foreach (var player in source.Players)
            {
                var seat = context.Players.Find(s => s.PlayerId == player.PlayerId);
                data.Seats.Add(new MatchSaveSeat { PlayerId = player.PlayerId, Color = player.Color,
                    PlayerName = seat?.PlayerName ?? player.Name, SteamId = seat?.SteamId ?? 0, OperatorId = seat?.OperatorId });
            }
            data.HostSteamId = data.Seats.Find(s => s.PlayerId == data.LocalPlayerId)?.SteamId ?? 0;
            data.Validate(); return data;
        }
        private MatchSaveData CreateHostSaveHeader()
        {
            var seats = CopySaveSeats();
            return new MatchSaveData { Mode = LaunchMode.Host, GameId = context.GameId,
                LocalPlayerId = context.LocalPlayerId, LocalTestNetwork = LocalMirrorTestMode.IsEnabled,
                HostSteamId = seats.Find(value => value.PlayerId == context.LocalPlayerId)?.SteamId ?? 0,
                Seats = seats, SavedUtcTicks = DateTime.UtcNow.Ticks };
        }

        private MatchSaveData CreateLocalSaveHeader(GameSessionRecoveryExport capture)
        {
            var source = capture.Archive.Snapshot.State;
            var header = new MatchSaveData { SavedUtcTicks = DateTime.UtcNow.Ticks, Mode = LaunchMode.Local,
                GameId = source.GameId, ContentHash = source.ContentPackHash, LocalPlayerId = context.LocalPlayerId,
                LocalTestNetwork = LocalMirrorTestMode.IsEnabled };
            foreach (var player in source.Players)
            {
                var seat = context.Players.Find(s => s.PlayerId == player.PlayerId);
                header.Seats.Add(new MatchSaveSeat { PlayerId = player.PlayerId, Color = player.Color,
                    PlayerName = seat?.PlayerName ?? player.Name, SteamId = seat?.SteamId ?? 0, OperatorId = seat?.OperatorId });
            }
            header.HostSteamId = header.Seats.Find(s => s.PlayerId == header.LocalPlayerId)?.SteamId ?? 0;
            return header;
        }

        private System.Collections.Generic.List<MatchSaveSeat> CopySaveSeats()
        {
            var seats = new System.Collections.Generic.List<MatchSaveSeat>();
            foreach (var seat in context.Players)
                seats.Add(new MatchSaveSeat { PlayerId = seat.PlayerId, Color = seat.Color,
                    PlayerName = seat.PlayerName, SteamId = seat.SteamId, OperatorId = seat.OperatorId });
            return seats;
        }

        public Task<bool> Save(int slot = -1) => SaveCore(slot, false);
        private async Task<bool> SaveCore(int slot, bool automatic)
        {
            if (automatic && !AutomaticSavingEnabled) return false;
            if (saving) { lastFailure = "正在写入存档，请稍候。"; return false; }
            var epoch = generation; int version = dirty, hostVersion = hostMutationVersion; saving = true;
            var observationContext = SaveObservationContext(slot);
            var saveObservation = NetworkExecutionObservation.Begin("save.total", observationContext);
            observationSaveId = observationContext.OperationId = saveObservation.Id;
            saveObservation.Context = observationContext;
            var weak = automatic && weakObservation != null && weakObservation.BeginCandidate(observationContext.OperationId, observationContext.GameId, context.Mode) ? weakObservation : null;
            long weakId = weak?.CandidateId ?? 0;
            var trace = CreateSaveTraceWithWeak(observationContext, weak);
            IMatchSaveTrace operationTrace = trace ?? (IMatchSaveTrace)weak;
            bool weakSuccess = false, weakCancelled = false;
            MatchSaveTraceScope.Point(trace, automatic ? "save.kind.local.automatic" : "save.kind.manual");
            try
            {
                Task write;
                if (context?.Mode == LaunchMode.Host)
                {
                    var snapshot = context.IsCompletedSessionDetached ? ReadRetainedHostSnapshot() : ReadHostSaveSnapshot();
                    var header = context.IsCompletedSessionDetached ? GameStateCloneService.DeepClone(completedHostHeader) : CreateHostSaveHeader();
                    header.SavedUtcTicks = DateTime.UtcNow.Ticks;
                    using (NetworkExecutionObservation.Begin("save.enqueue_sync", observationContext))
                        write = store.SavePreparedWithValidationAsync(validation => MatchSaveData.CreateFromPublishedSnapshotAsync(header, snapshot, validation),
                            snapshot.GameId, snapshot.ContentHash, slot, saveLifetime.Token, operationTrace, weak);
                }
                else
                {
                    GameSessionRecoveryExport export;
                    MatchSaveTraceScope.Point(trace, "save.capture.begin");
                    try
                    {
                        using (NetworkExecutionObservation.Begin("save.capture", observationContext, true, session.ExecutionTimings))
                            export = await session.CaptureStableSavePointAsync(saveLifetime.Token);
                    }
                    finally { MatchSaveTraceScope.Point(trace, "save.capture.end"); }
                    if (epoch != generation) return false;
                    version = dirty;
                    MatchSaveTraceScope.Source(operationTrace, export.Archive.Snapshot.State.EffectRuntime?.StateRevision ?? 0);
                    var header = CreateLocalSaveHeader(export);
                    using (NetworkExecutionObservation.Begin("save.enqueue_sync", observationContext))
                        write = store.SavePreparedWithValidationAsync(validation => MatchSaveData.CreateFromLocalCaptureAsync(header, export, validation),
                            header.GameId, header.ContentHash, slot, saveLifetime.Token, operationTrace, weak);
                }
                NetworkExecutionObservation.ObserveCompletion(write, observationContext);
                using (NetworkExecutionObservation.Begin("save.await_resume", observationContext)) await write;
                if (epoch != generation) return false;
                saved = version; savedHostMutationVersion = hostVersion; lastFailure = null;
                weakSuccess = true;
                MatchSaveTraceScope.Point(trace, "save.complete");
                return true;
            }
            catch (Exception error)
            {
                weakCancelled = error is OperationCanceledException;
                MatchSaveTraceScope.Point(trace, error is OperationCanceledException ? "save.cancel" : "save.fail");
                if (epoch == generation) lastFailure = error.Message; return false;
            }
            finally
            {
                if (epoch == generation) saving = false;
                if (epoch == generation) observationSaveId = 0;
                weak?.ControllerFinished(weakId, weakSuccess, weakCancelled, PerformanceSaveStatistics);
                if (trace != null) saveObservation.Context.Revision = trace.SourceRevision;
                else if (weak != null) saveObservation.Context.Revision = weak.CandidateRevision;
                saveObservation.Dispose();
            }
        }

        private MatchSavePerformanceTrace CreateSaveTrace(NetworkExecutionObservation.Context identity)
            => CreateSaveTraceWithWeak(identity, null);
        private MatchSavePerformanceTrace CreateSaveTraceWithWeak(NetworkExecutionObservation.Context identity, MatchSaveWeakObservation weak)
        {
            if (!CanTraceSave()) return null;
            tracedSaveOperations++; return new MatchSavePerformanceTrace(identity, weak);
        }
        private bool CanTraceSave() => performance.Trace && NetworkExecutionObservation.Enabled && tracedSaveOperations < 32 && Time.unscaledTime <= traceUntil;
        private NetworkExecutionObservation.Context SaveObservationContext(int slot = -1)
        {
            if (!NetworkExecutionObservation.Enabled) return default;
            var identity = MirrorCommandTransport.Instance != null ? MirrorCommandTransport.Instance.ReadObservationContext() : default;
            identity.GameId = context?.Mode == LaunchMode.Local ? session?.State.GameId : context?.GameId;
            identity.Mode = context?.Mode.ToString();
            identity.PlayerId = context?.LocalPlayerId ?? -1;
            if (context?.Mode == LaunchMode.Local) identity.Revision = session?.State.EffectRuntime?.StateRevision ?? 0;
            identity.Frame = Time.frameCount; identity.Slot = slot;
            identity.OperationId = observationSaveId;
            return identity;
        }
        public async void RequestLeave(Action action)
        {
            if (leaving) return;
            if (!CanSave) { action(); return; }
            var epoch = generation; leaving = true;
            if (context.Mode == LaunchMode.Local) session.CommandsSuspended = true;
            // 离开不创建保存：取消队列中尚未开始的请求，已进入原子写盘的请求安全完成。
            // 此后到 Unbind 期间也不再接收窗口自动保存点。
            saveLifetime.Cancel();
            windowAutoSaves = new WindowAutoSaveQueue();
            try
            {
                await store.FlushAsync();
            }
            catch (Exception) { /* 取消或此前写入失败不阻止离开，也不打开手动保存页。 */ }
            // 若退出由结算内的回调请求，先让当前事务退出调用栈；这里不捕获、不推进规则。
            while (epoch == generation && context?.Mode == LaunchMode.Local && session != null &&
                session.ReadSaveCaptureSafety() == SaveCaptureSafety.Executing)
                await Task.Yield();
            if (epoch != generation) return;
            Unbind(); action();
        }
        private bool WantsToQuit()
        {
            if (allowQuit || !CanSave) return true;
            RequestLeave(() => { allowQuit = true; UnityEngine.Application.Quit(); }); return false;
        }
    }
}
