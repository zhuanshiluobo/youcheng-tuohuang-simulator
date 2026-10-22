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
        private SimulationPublishedSnapshot completedHostSnapshot;
        private MatchSaveData completedHostHeader;
        private int completedHostSaveGeneration = -1;
        private MirrorCommandTransport saveTransport;
        private Action<SimulationCommandAppliedDto> networkApplied;
        private Action<CommandValidationPrefixDto> networkValidated;
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
            Instance = this; DontDestroyOnLoad(gameObject);
            store = new MatchSaveStore(Path.Combine(UnityEngine.Application.persistentDataPath, "MatchSaves"));
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
                networkApplied = applied => { if (Current() && applied.GameId == launch.GameId && applied.RuleResult?.Succeeded == true) MarkDirty(); };
                networkValidated = prefix => { if (Current() && prefix.GameId == launch.GameId) dirty++; };
                networkRecovered = coordinator => { if (Current() && coordinator.SessionOptions.GameId == launch.GameId) MarkDirty(); };
                saveTransport.SimulationCommandApplied += networkApplied;
                saveTransport.CommandValidationPrefixAdvanced += networkValidated;
                saveTransport.SimulationRecoveryCompleted += networkRecovered;
            }
            store.BeginMatch(launch.Mode == LaunchMode.Local ? session.State.GameId : launch.GameId, launch.RestoreData == null);
            dirty = launch.RestoreData == null ? 1 : 0; saved = 0;
        }
        private void MarkDirty() { dirty++; ClearCompletedHostSave(); }
        private void ClearCompletedHostSave()
        {
            completedHostSnapshot = null; completedHostHeader = null; completedHostSaveGeneration = -1;
        }
        public void Unbind()
        {
            saveLifetime.Cancel(); saveLifetime.Dispose(); saveLifetime = new CancellationTokenSource();
            if (session != null && context?.Mode == LaunchMode.Local) { session.StateCommitted -= MarkDirty; session.CommandsSuspended = false; }
            if (!ReferenceEquals(saveTransport, null))
            {
                saveTransport.SimulationCommandApplied -= networkApplied;
                saveTransport.CommandValidationPrefixAdvanced -= networkValidated;
                saveTransport.SimulationRecoveryCompleted -= networkRecovered;
            }
            saveTransport = null; networkApplied = null; networkValidated = null; networkRecovered = null;
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
                if (!snapshot.FinalScoringResolved || snapshot.Phase != GamePhase.FinalScoring && snapshot.Phase != GamePhase.GameOver)
                    throw new InvalidOperationException("终局状态尚未形成同局一致的正式确认检查点。");
                completedHostSnapshot = snapshot;
                completedHostHeader = CreateHostSaveHeader();
                completedHostSaveGeneration = generation;
                // 释放运输层后仍由原保存队列写盘；失败保留此确认点供重试。
                dirty++;
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
            if (!CanSave || saving || leaving || dirty == saved || Time.unscaledTime < retryAt) return;
            int epoch = generation;
            if (!await Save() && epoch == generation) retryAt = Time.unscaledTime + 2f;
        }
        public MatchSaveData Capture()
        {
            if (!CanSave) throw new InvalidOperationException("只有本地对局或房主可以保存。");
            if (context.Mode != LaunchMode.Local)
                throw new InvalidOperationException("联机存档必须通过异步保存入口从固定确认点生成。");
            return CaptureLiveSession();
        }

        private MatchSaveData CaptureLiveSession()
        {
            using var captureObservation = NetworkExecutionObservation.Begin("save.capture", SaveObservationContext(), true, session.ExecutionTimings);
            var export = session.ExportConfirmedRecoveryPoint(session.EnableConfirmedRecoveryCapture());
            string archive = HostRecoveryService.SerializeArchive(export.Archive);
            string continuation = SimulationConfirmedRecoveryInstaller.SerializeContinuation(export.Continuation);
            var data = new MatchSaveData { SavedUtcTicks = DateTime.UtcNow.Ticks, Mode = context.Mode,
                LocalPlayerId = context.LocalPlayerId, LocalTestNetwork = LocalMirrorTestMode.IsEnabled,
                MapId = session.State.MapId, GameId = session.State.GameId, Round = session.State.Round,
                ContentHash = session.State.ContentPackHash, ArchiveJson = archive, ContinuationJson = continuation,
                SimulationCheckpointJson = null };
            foreach (var player in session.State.Players)
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

        private System.Collections.Generic.List<MatchSaveSeat> CopySaveSeats()
        {
            var seats = new System.Collections.Generic.List<MatchSaveSeat>();
            foreach (var seat in context.Players)
                seats.Add(new MatchSaveSeat { PlayerId = seat.PlayerId, Color = seat.Color,
                    PlayerName = seat.PlayerName, SteamId = seat.SteamId, OperatorId = seat.OperatorId });
            return seats;
        }

        public async Task<bool> Save(int slot = -1)
        {
            if (saving) { lastFailure = "正在写入存档，请稍候。"; return false; }
            var epoch = generation; int version = dirty; saving = true;
            var observationContext = SaveObservationContext(slot);
            var saveObservation = NetworkExecutionObservation.Begin("save.total", observationContext);
            observationSaveId = observationContext.OperationId = saveObservation.Id;
            saveObservation.Context = observationContext;
            try
            {
                Task write;
                if (context?.Mode == LaunchMode.Host)
                {
                    var snapshot = context.IsCompletedSessionDetached ? ReadRetainedHostSnapshot() : ReadHostSaveSnapshot();
                    var header = context.IsCompletedSessionDetached ? GameStateCloneService.DeepClone(completedHostHeader) : CreateHostSaveHeader();
                    header.SavedUtcTicks = DateTime.UtcNow.Ticks;
                    using (NetworkExecutionObservation.Begin("save.enqueue_sync", observationContext))
                        write = store.SavePreparedAsync(() => MatchSaveData.CreateFromPublishedSnapshotAsync(header, snapshot),
                            snapshot.GameId, snapshot.ContentHash, slot, saveLifetime.Token);
                }
                else
                {
                    var capture = Capture();
                    using (NetworkExecutionObservation.Begin("save.enqueue_sync", observationContext)) write = store.SaveAsync(capture, slot);
                }
                NetworkExecutionObservation.ObserveCompletion(write, observationContext);
                using (NetworkExecutionObservation.Begin("save.await_resume", observationContext)) await write;
                if (epoch != generation) return false;
                saved = version; lastFailure = null;
                return true;
            }
            catch (Exception error) { if (epoch == generation) lastFailure = error.Message; return false; }
            finally
            {
                if (epoch == generation) saving = false;
                if (epoch == generation) observationSaveId = 0;
                saveObservation.Dispose();
            }
        }

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
            try
            {
                while (saving) { if (epoch != generation) return; await Task.Yield(); }
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (epoch != generation) return;
                    if (!await Save()) { if (epoch != generation) return; throw new IOException(lastFailure); }
                    await store.FlushAsync();
                    if (epoch != generation) return;
                    if (dirty != saved) continue;
                    Unbind(); action(); return;
                }
                lastFailure = "仍有新的操作正在结算，请稍后再保存退出。";
            }
            catch (Exception error)
            { if (epoch == generation) { lastFailure = error.Message; Debug.LogWarning("保存退出未完成：" + error.Message); } }
            finally { if (epoch == generation) { leaving = false; if (session != null && context?.Mode == LaunchMode.Local) session.CommandsSuspended = false;
                if (!string.IsNullOrEmpty(lastFailure)) MatchSaveWindowView.OpenSaveFailure(lastFailure); } }
        }
        private bool WantsToQuit()
        {
            if (allowQuit || !CanSave) return true;
            RequestLeave(() => { allowQuit = true; UnityEngine.Application.Quit(); }); return false;
        }
    }
}
