using System;
using System.Linq;
using YC.Application.Sessions;

namespace YC.Presentation.Workflows
{
    public enum GameplayLoadingStage
    {
        Scene, Initializing, Restoring, PreparingSynchronization, Synchronizing, WaitingForPlayers, Ready, Failed
    }

    /// <summary>加载展示状态。阶段权重只表示流程位置，不估算剩余时间，也不授予游戏操作权限。</summary>
    public sealed class GameplayLoadingProgress
    {
        public GameplayLoadingProgress(bool online, bool host, bool restoring)
        { Online = online; Host = host; Restoring = restoring; }
        public bool Online { get; }
        public bool Host { get; }
        public bool Restoring { get; }
        public GameplayLoadingStage Stage { get; private set; }
        public float Fill { get; private set; }
        public float SceneProgress { get; private set; }
        public int ReceivedBytes { get; private set; }
        public int TotalBytes { get; private set; }
        public int ReadyPlayers { get; private set; }
        public int TotalPlayers { get; private set; }
        public bool SessionPrepared { get; private set; }
        public bool IsFinished => Stage == GameplayLoadingStage.Ready || Stage == GameplayLoadingStage.Failed;

        public static bool HasConfirmedSimulationState(SimulationPublishedSnapshot snapshot, string gameId)
        {
            return snapshot != null && snapshot.GameId == gameId && !snapshot.IsStopped && !snapshot.RequiresRecovery &&
                !snapshot.IsRecoveringConfirmedPrefix && !snapshot.HasPendingLocalApplication &&
                (snapshot.IsCurrentBoundaryConfirmed || snapshot.FinalScoringResolved ||
                 snapshot.ReadWindowPermissions().Any(window => window.IsOpen && window.IsPermitted));
        }

        public void SetSceneProgress(float progress)
        {
            if (Stage != GameplayLoadingStage.Scene) return;
            SceneProgress = Math.Max(SceneProgress, Math.Max(0, Math.Min(1, progress)));
            Fill = SceneProgress * .6f;
        }
        public void BeginInitialization()
        {
            if (Stage != GameplayLoadingStage.Scene) return;
            Fill = .6f;
            Stage = Restoring ? GameplayLoadingStage.Restoring : GameplayLoadingStage.Initializing;
        }
        public void MarkSessionPrepared()
        {
            if (IsFinished) return;
            SessionPrepared = true;
            Fill = .8f;
            Stage = Online ? GameplayLoadingStage.PreparingSynchronization : GameplayLoadingStage.Initializing;
        }
        public void CompleteLocal(bool uiReady)
        {
            if (!Online && SessionPrepared && uiReady && !IsFinished) Complete();
        }
        public void ObserveNetwork(int receivedBytes, int totalBytes, int readyPlayers, int totalPlayers,
            bool localInstalled, bool localUiReady, bool boundaryReady, bool failed)
        {
            if (IsFinished || !Online || !SessionPrepared) return;
            if (failed) { Fail(); return; }
            TotalBytes = Math.Max(0, totalBytes);
            ReceivedBytes = Math.Max(0, Math.Min(receivedBytes, TotalBytes));
            TotalPlayers = Math.Max(0, totalPlayers);
            ReadyPlayers = Math.Max(0, Math.Min(readyPlayers, TotalPlayers));
            // 收齐字节、打开窗口、轮到本人，均不能单独当作加载完成。
            bool peersReady = !Host || TotalPlayers > 0 && ReadyPlayers == TotalPlayers;
            if (localInstalled && localUiReady && boundaryReady && peersReady) { Complete(); return; }
            Stage = localInstalled ? GameplayLoadingStage.WaitingForPlayers : GameplayLoadingStage.Synchronizing;
            float fraction = Host ? (TotalPlayers > 0 ? (float)ReadyPlayers / TotalPlayers : 0)
                : TotalBytes > 0 ? (float)ReceivedBytes / TotalBytes : 0;
            Fill = Math.Max(Fill, Math.Min(.95f, .8f + .15f * fraction));
        }
        public void Fail()
        {
            if (Stage != GameplayLoadingStage.Ready) Stage = GameplayLoadingStage.Failed;
        }
        private void Complete() { Stage = GameplayLoadingStage.Ready; Fill = 1; }
    }
}
