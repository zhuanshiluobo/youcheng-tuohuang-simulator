using System;

namespace YC.Application.Sessions
{
    // 仅保存边界的诊断端口；实现不得持有Session、导出、JObject或Task。
    public interface IMatchSaveTrace
    {
        void SetSourceRevision(int revision);
        void Point(string stage);
        IDisposable Begin(string stage);
    }

    public readonly struct MatchSaveTraceScope : IDisposable
    {
        private readonly IDisposable scope;
        private MatchSaveTraceScope(IDisposable scope) { this.scope = scope; }
        public static MatchSaveTraceScope Begin(IMatchSaveTrace trace, string stage)
        {
            if (trace == null) return default;
            try { return new MatchSaveTraceScope(trace.Begin(stage)); } catch { return default; }
        }
        public void Dispose() { try { scope?.Dispose(); } catch { } }
        public static void Point(IMatchSaveTrace trace, string stage) { try { trace?.Point(stage); } catch { } }
        public static void Source(IMatchSaveTrace trace, int revision) { try { trace?.SetSourceRevision(revision); } catch { } }
    }

    public readonly struct MatchSaveQueueStatistics
    {
        public readonly bool Enabled;
        public readonly long Enqueued, Started, Completed, Failed, Cancelled, Pending, Active, Outstanding, PeakOutstanding;
        public MatchSaveQueueStatistics(bool enabled, long enqueued, long started, long completed, long failed,
            long cancelled, long pending, long active, long outstanding, long peak)
        {
            Enabled = enabled; Enqueued = enqueued; Started = started; Completed = completed; Failed = failed;
            Cancelled = cancelled; Pending = pending; Active = active; Outstanding = outstanding; PeakOutstanding = peak;
        }
    }

    // 只含值/短身份；不把弱目标、任务或其正文带出同步读取方法。
    public enum MatchSaveWeakCoverage { Disabled, DisabledByAutoOff, WaitingForEligibleSource, NoEligibleSourceBeforeLeave, InFlight, PairCompleted, NewDataUnavailable, OldSlotUnavailable, Failed, Cancelled, SourceConflict, DiagnosticError }
    public struct MatchSaveWeakSnapshot
    {
        public bool Requested, Enabled, Selected, NewObserved, OldObserved, Completed, GameClosed;
        public MatchSaveWeakCoverage Coverage;
        public string GameId, OldValidatedPayloadChecksum, NewPreparedPayloadChecksum;
        public long OperationId, EnqueueTick, PreparedTick, OldReadTick, CompleteTick, ReadTick, Frequency;
        public int SourceRevision, LastCandidateRevision, NewSlot, OldSlot;
        public int NewArchiveChars, NewContinuationChars, OldArchiveChars, OldContinuationChars;
        // -1未捕获，-2读取不可用，0 Dead，1 Alive；不是泄漏结论。
        public int NewAlive, OldAlive, CompleteGc0, CompleteGc1, CompleteGc2, ReadGc0Before, ReadGc0After, ReadGc1, ReadGc2, Events;
        public MatchSaveQueueStatistics Queue;
        public static MatchSaveWeakSnapshot Disabled(bool requested, bool autoOff, MatchSaveQueueStatistics queue)
            => new MatchSaveWeakSnapshot { Requested = requested, Coverage = autoOff && requested ? MatchSaveWeakCoverage.DisabledByAutoOff : MatchSaveWeakCoverage.Disabled,
                SourceRevision = -1, LastCandidateRevision = -1, NewSlot = -1, OldSlot = -1, NewAlive = -1, OldAlive = -1,
                CompleteGc0 = -1, CompleteGc1 = -1, CompleteGc2 = -1, ReadGc0Before = -1, ReadGc0After = -1, ReadGc1 = -1, ReadGc2 = -1, Queue = queue };
    }
}
