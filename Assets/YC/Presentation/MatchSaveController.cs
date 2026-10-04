using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using YC.Application.Sessions;
using YC.Infrastructure.Multiplayer;
using YC.Infrastructure.Persistence;

namespace YC.Presentation
{
    /// <summary>无界面的本机存档协调器：自动保存、串行写盘及退出等待，不依赖预制体。</summary>
    public sealed class MatchSaveController : MonoBehaviour
    {
        public static MatchSaveController Instance { get; private set; }
        private MatchSaveStore store;
        private GameSession session;
        [NonSerialized] private MatchSaveData configuration;
        private bool allowQuit, flushing;
        private int generation;
        private long saveSequence, completedSaveSequence;
        private string lastFailure;
        public bool CanSave => session != null && configuration != null;

        public static MatchSaveController Ensure()
        {
            if (Instance != null) return Instance;
            return new GameObject(nameof(MatchSaveController)).AddComponent<MatchSaveController>();
        }
        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            store = new MatchSaveStore(Path.Combine(UnityEngine.Application.persistentDataPath, "MatchSaves"));
            UnityEngine.Application.wantsToQuit += WantsToQuit;
        }
        private void OnDestroy()
        {
            if (Instance != this) return;
            Unbind();
            UnityEngine.Application.wantsToQuit -= WantsToQuit;
            Instance = null;
        }
        // 保留手动槽位和读档预检能力，暂不提供界面入口。
        public List<MatchSaveSlot> ListSlots() => store.List();
        public MatchSaveData Read(int slot, bool backup = false)
        {
            var data = store.Read(slot, backup);
            GameSessionBootstrapper.Restore(null, data);
            return data;
        }
        public void Bind(GameSession value, string contentHash, GameLaunchContext context, bool restored)
        {
            Unbind();
            if (context != null && context.Mode == LaunchMode.Client) return;
            session = value;
            configuration = new MatchSaveData {
                Mode = context == null ? LaunchMode.Local : context.Mode,
                LocalPlayerId = context == null ? 1 : context.LocalPlayerId,
                LocalTestNetwork = LocalMirrorTestMode.IsEnabled, ContentHash = contentHash
            };
            foreach (var player in session.State.Players)
            {
                var seat = context?.Players.Find(s => s.PlayerId == player.PlayerId);
                configuration.Seats.Add(new MatchSaveSeat { PlayerId = player.PlayerId,
                    SteamId = seat == null ? 0 : seat.SteamId, OperatorId = seat?.OperatorId,
                    PlayerName = seat == null ? player.PlayerId.ToString() : seat.PlayerName, Color = player.Color });
            }
            configuration.HostSteamId = configuration.Seats.Find(s => s.PlayerId == 1)?.SteamId ?? 0;
            foreach (var slot in store.List())
            {
                if (slot.Data == null) continue;
                try { GameSessionBootstrapper.Restore(null, slot.Data); }
                catch { store.ProtectAutomaticSlot(slot.Index); }
            }
            session.StateCommitted += AutoSave;
            // 成功读档本身不写原文件；下一次提交才自动保存。新局初始化完成保存一次。
            if (!restored) AutoSave();
        }
        public void Unbind()
        {
            if (session != null)
            {
                session.StateCommitted -= AutoSave;
                session.CommandsSuspended = false;
            }
            flushing = false;
            session = null; configuration = null; generation++;
            lastFailure = null;
        }
        private MatchSaveData Capture()
        {
            if (!CanSave) throw new InvalidOperationException("本机不是当前对局的权威端。");
            var data = new MatchSaveData {
                SavedUtcTicks = DateTime.UtcNow.Ticks, Mode = configuration.Mode,
                LocalTestNetwork = configuration.LocalTestNetwork, LocalPlayerId = configuration.LocalPlayerId,
                HostSteamId = configuration.HostSteamId, ContentHash = configuration.ContentHash,
                MapId = session.State.MapId, GameId = session.State.GameId, Round = session.State.Round,
                Seats = configuration.Seats, Archive = session.CreateHostArchive(configuration.ContentHash)
            };
            return data;
        }
        private async void AutoSave() { await Save(-1); }
        public async Task<bool> Save(int slot = -1)
        {
            var epoch = generation;
            var sequence = ++saveSequence;
            try
            {
                await store.SaveAsync(Capture(), slot);
                if (epoch == generation && sequence >= completedSaveSequence) { completedSaveSequence = sequence; lastFailure = null; }
                return true;
            }
            catch (Exception ex)
            {
                if (epoch == generation && sequence >= completedSaveSequence) { completedSaveSequence = sequence; lastFailure = ex.Message; }
                Debug.LogWarning("Match save: " + ex.Message);
                return false;
            }
        }
        public async void RequestLeave(Action action)
        {
            if (flushing) return;
            if (!CanSave) { action(); return; }
            var epoch = generation;
            flushing = true;
            session.CommandsSuspended = true;
            try
            {
                // 上次保存失败时，本次退出请求重试；不创建确认或错误弹窗。
                if (!string.IsNullOrEmpty(lastFailure) && !await Save())
                    throw new IOException(lastFailure);
                await store.FlushAsync();
                if (epoch != generation) return;
                if (!string.IsNullOrEmpty(lastFailure)) throw new IOException(lastFailure);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("退出前存档未完成，已取消退出：" + ex.Message);
                return;
            }
            finally
            {
                if (epoch == generation)
                {
                    flushing = false;
                    if (session != null) session.CommandsSuspended = false;
                }
            }
            Unbind();
            action();
        }
        private bool WantsToQuit()
        {
            if (allowQuit || !CanSave) return true;
            RequestLeave(() => { allowQuit = true; UnityEngine.Application.Quit(); });
            return false;
        }
    }
}
