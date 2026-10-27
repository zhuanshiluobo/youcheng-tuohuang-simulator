using System;
using System.Threading;
using System.Threading.Tasks;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public enum SaveCaptureSafety { Safe, Executing, PendingProgress, Faulted, OwnerUnavailable }

    public sealed partial class GameSession
    {
        private int saveCriticalDepth;
        private readonly struct SaveCriticalScope : IDisposable
        {
            private readonly GameSession session;
            internal SaveCriticalScope(GameSession session) { this.session = session; Interlocked.Increment(ref session.saveCriticalDepth); }
            public void Dispose() { Interlocked.Decrement(ref session.saveCriticalDepth); }
        }
        public SaveCaptureSafety ReadSaveCaptureSafety()
        {
            if (IsBackgroundReader) return SaveCaptureSafety.OwnerUnavailable;
            if (Volatile.Read(ref saveCriticalDepth) != 0 || EffectSaveActivity.IsExecuting(State)) return SaveCaptureSafety.Executing;
            if (State.EffectRuntime?.Status == EffectRuntimeStatus.PausedFault) return SaveCaptureSafety.Faulted;
            return EffectTreeExecutor.IsStableForSave(State) ? SaveCaptureSafety.Safe : SaveCaptureSafety.PendingProgress;
        }
        private void EnsureSaveCaptureOutsideExecution()
        {
            EnsureExecutionOwner();
            if (Volatile.Read(ref saveCriticalDepth) != 0 || EffectSaveActivity.IsExecuting(State)) throw new InvalidOperationException("save_capture_execution_active");
        }
        // 热座所有保存入口共用；始终从当前稳定状态及原日志continuation建立同一份固定捕获。
        public GameSessionRecoveryExport CaptureStableSavePoint()
        {
            var safety = ReadSaveCaptureSafety();
            if (safety != SaveCaptureSafety.Safe) throw new InvalidOperationException("save_capture_not_stable:" + safety);
            var capture = PrepareRecoveryCapture(State, pendingActionLog, latestRecoveryCapture?.Point.CommandId ?? string.Empty);
            // 一次热座保存不启用联机逐命令确认捕获；若联机此前已启用，则保持原状态。
            RetainRecoveryCapture(capture);
            return ExportConfirmedRecoveryPoint(capture.Point);
        }
        public async Task<GameSessionRecoveryExport> CaptureStableSavePointAsync(CancellationToken cancellation = default)
        {
            while (ReadSaveCaptureSafety() != SaveCaptureSafety.Safe)
            {
                cancellation.ThrowIfCancellationRequested();
                var status = ReadSaveCaptureSafety();
                if (status == SaveCaptureSafety.Faulted || status == SaveCaptureSafety.OwnerUnavailable)
                    throw new InvalidOperationException("当前结算已暂停或执行所有权不可用，保留原存档并先处理故障。");
                await Task.Yield();
            }
            cancellation.ThrowIfCancellationRequested(); return CaptureStableSavePoint();
        }
    }
}
