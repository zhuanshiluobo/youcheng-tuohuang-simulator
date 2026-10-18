using System;
using System.IO;
using System.Threading.Tasks;
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
        private MatchSaveData completedHostSave;
        private int completedHostSaveGeneration = -1;
        private int completedHostSaveRevision;
        private string completedHostSaveDigest;
        public bool CanSave => session != null && context != null && context.Mode != LaunchMode.Client;
        public string CurrentGameId => session?.State.GameId;
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
            Unbind(); UnityEngine.Application.wantsToQuit -= WantsToQuit; Instance = null;
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
            session.StateCommitted += MarkDirty;
            store.BeginMatch(session.State.GameId, launch.RestoreData == null);
            dirty = launch.RestoreData == null ? 1 : 0; saved = 0;
        }
        private void MarkDirty() { dirty++; ClearCompletedHostSave(); }
        private void ClearCompletedHostSave()
        {
            completedHostSave = null; completedHostSaveGeneration = -1;
            completedHostSaveRevision = 0; completedHostSaveDigest = null;
        }
        public void Unbind()
        {
            if (session != null) { session.StateCommitted -= MarkDirty; session.CommandsSuspended = false; }
            generation++; session = null; context = null; dirty = saved = 0; saving = leaving = false;
            ClearCompletedHostSave(); lastFailure = null; retryAt = 0f; allowQuit = false; observationSaveId = 0;
        }

        internal bool TryRetainCompletedHostSave(GameLaunchContext launch, out string reason)
        {
            reason = null;
            try
            {
                if (!CanSave || context != launch || context.Mode != LaunchMode.Host || context.IsCompletedSessionDetached ||
                    context.GameId != session.State.GameId || !IsCompletedState(session.State))
                    throw new InvalidOperationException("当前对局没有可保留的房主终局确认存档。");
                var data = CaptureLiveSession(true);
                completedHostSave = data;
                completedHostSaveGeneration = generation;
                completedHostSaveRevision = session.State.EffectRuntime.StateRevision;
                completedHostSaveDigest = RuleStateDigestService.Compute(session.State);
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

        private static bool IsCompletedState(GameState state) => state != null && RoundTrackRule.IsFinalState(state) &&
            state.FinalScoring != null && state.FinalScoring.IsResolved;

        private MatchSaveData CaptureDetachedCompletedHost()
        {
            using var captureObservation = NetworkExecutionObservation.Begin("save.capture", SaveObservationContext(), true, session.ExecutionTimings);
            if (completedHostSave == null || completedHostSaveGeneration != generation ||
                !IsCompletedState(session.State) || context.Mode != LaunchMode.Host ||
                completedHostSave.Mode != LaunchMode.Host || completedHostSave.GameId != session.State.GameId ||
                context.GameId != completedHostSave.GameId || completedHostSave.LocalPlayerId != context.LocalPlayerId ||
                completedHostSave.ContentHash != session.State.ContentPackHash || completedHostSave.MapId != session.State.MapId ||
                completedHostSaveRevision != session.State.EffectRuntime.StateRevision ||
                completedHostSaveDigest != RuleStateDigestService.Compute(session.State))
                throw new InvalidOperationException("终局确认存档已失效，不能跳过保存退出。");
            var data = GameStateCloneService.DeepClone(completedHostSave);
            data.SavedUtcTicks = DateTime.UtcNow.Ticks;
            data.Validate();
            return data;
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
            if (context.Mode == LaunchMode.Host && context.IsCompletedSessionDetached)
                return CaptureDetachedCompletedHost();
            return CaptureLiveSession(false);
        }

        private MatchSaveData CaptureLiveSession(bool requireCompletedCheckpoint)
        {
            using var captureObservation = NetworkExecutionObservation.Begin("save.capture", SaveObservationContext(), true, session.ExecutionTimings);
            string archive, continuation, simulation = null;
            if (context.Mode == LaunchMode.Host)
            {
                var coordinator = MirrorCommandTransport.Instance?.SimulationCoordinator;
                if (coordinator == null || coordinator.SessionOptions.GameId != session.State.GameId)
                    throw new InvalidOperationException("联机确认通道尚未就绪。");
                SimulationRecoveryCheckpointDto checkpoint;
                SimulationRecoveryResultDto result;
                using (NetworkExecutionObservation.Begin("save.checkpoint", SaveObservationContext(), true, session.ExecutionTimings))
                    if (!coordinator.TryCreateSaveCheckpoint(out checkpoint, out result))
                        throw new InvalidOperationException(result.Reason);
                if (requireCompletedCheckpoint)
                {
                    var confirmedState = checkpoint?.Archive?.Snapshot?.State;
                    string digest = RuleStateDigestService.Compute(session.State);
                    if (!IsCompletedState(confirmedState) || confirmedState.GameId != session.State.GameId ||
                        confirmedState.ContentPackHash != session.State.ContentPackHash ||
                        confirmedState.EffectRuntime?.StateRevision != session.State.EffectRuntime.StateRevision ||
                        checkpoint.Baseline?.GameId != session.State.GameId ||
                        checkpoint.Baseline.RuleDigestVersion != RuleStateDigestService.DigestVersion ||
                        checkpoint.Baseline.RuleStateDigest != digest || RuleStateDigestService.Compute(confirmedState) != digest)
                        throw new InvalidOperationException("终局状态尚未形成同局一致的正式确认检查点。");
                }
                using (NetworkExecutionObservation.Begin("save.archive_serialize", SaveObservationContext()))
                    archive = HostRecoveryService.SerializeArchive(checkpoint.Archive);
                using (NetworkExecutionObservation.Begin("save.continuation_serialize", SaveObservationContext()))
                    continuation = SimulationConfirmedRecoveryInstaller.SerializeContinuation(checkpoint.Continuation);
                using (NetworkExecutionObservation.Begin("save.simulation_serialize", SaveObservationContext()))
                    simulation = SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint);
            }
            else
            {
                var export = session.ExportConfirmedRecoveryPoint(session.EnableConfirmedRecoveryCapture());
                archive = HostRecoveryService.SerializeArchive(export.Archive);
                continuation = SimulationConfirmedRecoveryInstaller.SerializeContinuation(export.Continuation);
            }
            var data = new MatchSaveData { SavedUtcTicks = DateTime.UtcNow.Ticks, Mode = context.Mode,
                LocalPlayerId = context.LocalPlayerId, LocalTestNetwork = LocalMirrorTestMode.IsEnabled,
                MapId = session.State.MapId, GameId = session.State.GameId, Round = session.State.Round,
                ContentHash = session.State.ContentPackHash, ArchiveJson = archive, ContinuationJson = continuation,
                SimulationCheckpointJson = simulation };
            foreach (var player in session.State.Players)
            {
                var seat = context.Players.Find(s => s.PlayerId == player.PlayerId);
                data.Seats.Add(new MatchSaveSeat { PlayerId = player.PlayerId, Color = player.Color,
                    PlayerName = seat?.PlayerName ?? player.Name, SteamId = seat?.SteamId ?? 0, OperatorId = seat?.OperatorId });
            }
            data.HostSteamId = data.Seats.Find(s => s.PlayerId == data.LocalPlayerId)?.SteamId ?? 0;
            data.Validate(); return data;
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
                var capture = Capture();
                Task write;
                using (NetworkExecutionObservation.Begin("save.enqueue_sync", observationContext)) write = store.SaveAsync(capture, slot);
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
            identity.GameId = session?.State.GameId;
            identity.Mode = context?.Mode.ToString();
            identity.PlayerId = context?.LocalPlayerId ?? -1;
            identity.Revision = session?.State.EffectRuntime?.StateRevision ?? 0;
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
            finally { if (epoch == generation) { leaving = false; if (session != null) session.CommandsSuspended = false;
                if (!string.IsNullOrEmpty(lastFailure)) MatchSaveWindowView.OpenSaveFailure(lastFailure); } }
        }
        private bool WantsToQuit()
        {
            if (allowQuit || !CanSave) return true;
            RequestLeave(() => { allowQuit = true; UnityEngine.Application.Quit(); }); return false;
        }
    }
}
