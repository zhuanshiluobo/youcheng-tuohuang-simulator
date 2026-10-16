using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using YC.Application.Sessions;
using YC.Infrastructure.Multiplayer;
using YC.Infrastructure.Persistence;

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
            if (launch == null || launch.Mode == LaunchMode.Client) return;
            session = value; context = launch;
            session.StateCommitted += MarkDirty;
            store.BeginMatch(session.State.GameId, launch.RestoreData == null);
            dirty = launch.RestoreData == null ? 1 : 0; saved = 0;
        }
        private void MarkDirty() { dirty++; }
        public void Unbind()
        {
            if (session != null) { session.StateCommitted -= MarkDirty; session.CommandsSuspended = false; }
            generation++; session = null; context = null; dirty = saved = 0; saving = leaving = false;
        }
        private async void LateUpdate()
        {
            if (!CanSave || saving || leaving || dirty == saved || Time.unscaledTime < retryAt) return;
            if (!await Save()) retryAt = Time.unscaledTime + 2f;
        }
        public MatchSaveData Capture()
        {
            if (!CanSave) throw new InvalidOperationException("只有本地对局或房主可以保存。");
            string archive, continuation, simulation = null;
            if (context.Mode == LaunchMode.Host)
            {
                var coordinator = MirrorCommandTransport.Instance?.SimulationCoordinator;
                if (coordinator == null || coordinator.SessionOptions.GameId != session.State.GameId)
                    throw new InvalidOperationException("联机确认通道尚未就绪。");
                if (!coordinator.TryCreateSaveCheckpoint(out var checkpoint, out var result))
                    throw new InvalidOperationException(result.Reason);
                archive = HostRecoveryService.SerializeArchive(checkpoint.Archive);
                continuation = SimulationConfirmedRecoveryInstaller.SerializeContinuation(checkpoint.Continuation);
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
            try
            {
                var capture = Capture(); await store.SaveAsync(capture, slot);
                if (epoch == generation) { saved = version; lastFailure = null; }
                return true;
            }
            catch (Exception error) { if (epoch == generation) lastFailure = error.Message; return false; }
            finally { if (epoch == generation) saving = false; }
        }
        public async void RequestLeave(Action action)
        {
            if (leaving) return;
            if (!CanSave) { action(); return; }
            var epoch = generation; leaving = true;
            if (context.Mode == LaunchMode.Local) session.CommandsSuspended = true;
            try
            {
                while (saving) await Task.Yield();
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (epoch != generation) return;
                    if (!await Save()) throw new IOException(lastFailure);
                    await store.FlushAsync();
                    if (epoch != generation) return;
                    if (dirty != saved) continue;
                    Unbind(); action(); return;
                }
                lastFailure = "仍有新的操作正在结算，请稍后再保存退出。";
            }
            catch (Exception error) { lastFailure = error.Message; Debug.LogWarning("保存退出未完成：" + error.Message); }
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
