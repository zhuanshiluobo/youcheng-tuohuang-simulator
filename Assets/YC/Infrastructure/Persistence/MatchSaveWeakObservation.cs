using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using YC.Application.Sessions;
using YC.Infrastructure.Multiplayer;

namespace YC.Infrastructure.Persistence
{
    // 一个控制器生命周期只选一笔。字段只允许两个弱引用、短身份和标量；无回调/事件订阅/目标正文。
    public sealed class MatchSaveWeakObservation : IMatchSaveTrace
    {
        private WeakReference targetA, targetB;
        private string gameId, oldChecksum, newChecksum;
        private long candidateId, selectedId, enqueueTick, preparedTick, oldReadTick, completeTick;
        private int candidateRevision = -1, sourceRevision = -1, newSlot = -1, oldSlot = -1;
        private int newArchiveChars, newContinuationChars, oldArchiveChars, oldContinuationChars;
        private int completeGc0 = -1, completeGc1 = -1, completeGc2 = -1, eventCount;
        private bool selected, candidateActive, completed, failed, cancelled, closed, conflict, diagnosticError;
        private LaunchMode mode;
        internal MatchSaveWeakObservation() { }
        internal long CandidateId { get { lock (this) return candidateActive ? candidateId : 0; } }
        internal int CandidateRevision { get { lock (this) return candidateRevision; } }
        internal bool NeedsCandidate { get { lock (this) return !selected && !closed && !diagnosticError; } }
        internal void Bind(string id)
        {
            lock (this) { if (gameId == null && !closed && !string.IsNullOrEmpty(id) && id.Length <= 128) gameId = id; }
        }
        internal bool BeginCandidate(long id, string match, LaunchMode launchMode)
        {
            lock (this)
            {
                if (selected || closed || diagnosticError || candidateActive || id == 0 || gameId != match) return false;
                candidateId = id; candidateRevision = -1; candidateActive = true; mode = launchMode; enqueueTick = 0;
                return true;
            }
        }
        public void SetSourceRevision(int revision)
        {
            lock (this)
            {
                if (!candidateActive || closed || diagnosticError) return;
                candidateRevision = revision;
                if (selected)
                {
                    if (selectedId == candidateId && sourceRevision != revision) { conflict = true; Emit("save.weak.source_conflict", default); }
                    return;
                }
                if (revision < 1000) return;
                selected = true; selectedId = candidateId; sourceRevision = revision;
                Emit("save.weak.selected", default);
                // Host可能在实际checkpoint准备中才得到修订；此前入队tick只作标量回填，不伪称原事件时间。
                if (enqueueTick != 0) Emit("save.weak.enqueue_recorded", default);
            }
        }
        public void Point(string stage) { } // 不扩展早32笔详细sample，不在阈值前写事件。
        public IDisposable Begin(string stage) => null;
        internal void Enqueued(long id, MatchSaveQueueStatistics queue)
        {
            lock (this) { if (!candidateActive || id != candidateId) return; enqueueTick = Stopwatch.GetTimestamp(); if (selected && id == selectedId) Emit("save.weak.enqueue", queue); }
        }
        internal void Prepared(long id, MatchSaveData data, MatchSaveQueueStatistics queue)
        {
            lock (this)
            {
                if (!Eligible(id) || targetA != null || data == null || data.GameId != gameId) return;
                try
                {
                    newArchiveChars = data.ArchiveJson?.Length ?? 0; newContinuationChars = data.ContinuationJson?.Length ?? 0;
                    targetA = new WeakReference(data, false); preparedTick = Stopwatch.GetTimestamp(); Emit("save.weak.prepare_return", queue);
                }
                catch { diagnosticError = true; }
            }
        }
        internal void OldSlotReturned(long id, int slot, MatchSaveData data, string validatedChecksum, MatchSaveQueueStatistics queue)
        {
            lock (this)
            {
                if (!Eligible(id) || targetB != null || data == null || data.GameId != gameId) return;
                try
                {
                    oldArchiveChars = data.ArchiveJson?.Length ?? 0; oldContinuationChars = data.ContinuationJson?.Length ?? 0;
                    oldSlot = slot; oldChecksum = ShortChecksum(validatedChecksum); targetB = new WeakReference(data, false);
                    oldReadTick = Stopwatch.GetTimestamp(); Emit("save.weak.readslot_return", queue);
                }
                catch { diagnosticError = true; }
            }
        }
        internal void WriteIdentity(long id, int slot, string checksum, MatchSaveQueueStatistics queue)
        { lock (this) { if (!Eligible(id)) return; newSlot = slot; newChecksum = ShortChecksum(checksum); Emit("save.weak.write_identity", queue); } }
        internal void QueueFinished(long id, bool success, bool wasCancelled, MatchSaveQueueStatistics queue)
        {
            lock (this)
            {
                if (!candidateActive || candidateId != id) return;
                if (selectedId == id && selected)
                {
                    completed = success; failed = !success && !wasCancelled; cancelled = wasCancelled;
                    completeTick = Stopwatch.GetTimestamp(); completeGc0 = GC.CollectionCount(0); completeGc1 = Gc(1); completeGc2 = Gc(2);
                    Emit(success ? "save.weak.queue_complete" : wasCancelled ? "save.weak.queue_cancel" : "save.weak.queue_fail", queue);
                }
                candidateActive = false;
            }
        }
        internal void ControllerFinished(long id, bool success, bool wasCancelled, MatchSaveQueueStatistics queue)
        {
            lock (this)
            {
                if (!selected || selectedId != id)
                { if (candidateActive && candidateId == id) candidateActive = false; return; }
                // 捕获后、入队前的异常也消耗该唯一选择，不再换笔。
                if (completeTick == 0)
                {
                    completed = success; failed = !success && !wasCancelled; cancelled = wasCancelled; candidateActive = false;
                    completeTick = Stopwatch.GetTimestamp(); completeGc0 = GC.CollectionCount(0); completeGc1 = Gc(1); completeGc2 = Gc(2);
                }
                Emit(success ? "save.weak.controller_complete" : wasCancelled ? "save.weak.controller_cancel" : "save.weak.controller_fail", queue);
            }
        }
        internal void CloseGame(string match)
        { lock (this) { if (gameId == null || gameId != match) return; closed = true; if (selected) Emit("save.weak.game_closed", default); } }
        private bool Eligible(long id) => selected && selectedId == id && !conflict && !diagnosticError;
        private static string ShortChecksum(string value) => value != null && value.Length <= 128 ? value : null;
        private static int Gc(int generation) => GC.MaxGeneration >= generation ? GC.CollectionCount(generation) : -1;
        private void Emit(string stage, MatchSaveQueueStatistics queue)
        {
            if (!selected || eventCount >= 12) return;
            eventCount++;
            try
            {
                var identity = new NetworkExecutionObservation.Context { GameId = gameId, OperationId = selectedId, Revision = sourceRevision,
                    Mode = mode == LaunchMode.Host ? "Host" : "Local", Frame = -1, Slot = oldSlot };
                NetworkExecutionObservation.Event(stage, identity, "gc0=" + GC.CollectionCount(0) + ";gc1=" + Gc(1) + ";gc2=" + Gc(2) +
                    ";enqueue_tick=" + enqueueTick + ";a=" + (targetA != null ? 1 : 0) + ";b=" + (targetB != null ? 1 : 0) +
                    ";queue_known=" + (queue.Enabled ? 1 : 0) + ";pending=" + queue.Pending + ";active=" + queue.Active + ";outstanding=" + queue.Outstanding);
            }
            catch { } // 事件失败不替换业务结果，预算仍消耗。
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadAlive(WeakReference weak)
        {
            if (weak == null) return -1;
            try { return weak.IsAlive ? 1 : 0; } catch { return -2; }
        }
        public MatchSaveWeakSnapshot ReadSnapshot(MatchSaveQueueStatistics queue)
        {
            lock (this)
            {
                int gcBefore = GC.CollectionCount(0), aliveA = ReadAlive(targetA), aliveB = ReadAlive(targetB), gcAfter = GC.CollectionCount(0);
                var coverage = diagnosticError ? MatchSaveWeakCoverage.DiagnosticError : conflict ? MatchSaveWeakCoverage.SourceConflict :
                    !selected ? (closed ? MatchSaveWeakCoverage.NoEligibleSourceBeforeLeave : MatchSaveWeakCoverage.WaitingForEligibleSource) :
                    failed ? MatchSaveWeakCoverage.Failed : cancelled ? MatchSaveWeakCoverage.Cancelled : !completed ? MatchSaveWeakCoverage.InFlight :
                    targetA == null ? MatchSaveWeakCoverage.NewDataUnavailable : targetB == null ? MatchSaveWeakCoverage.OldSlotUnavailable : MatchSaveWeakCoverage.PairCompleted;
                return new MatchSaveWeakSnapshot { Requested = true, Enabled = true, Selected = selected, NewObserved = targetA != null, OldObserved = targetB != null,
                    Completed = completed, GameClosed = closed, Coverage = coverage, GameId = gameId, OperationId = selectedId, SourceRevision = sourceRevision,
                    LastCandidateRevision = candidateRevision, NewSlot = newSlot, OldSlot = oldSlot, OldValidatedPayloadChecksum = oldChecksum, NewPreparedPayloadChecksum = newChecksum,
                    NewArchiveChars = newArchiveChars, NewContinuationChars = newContinuationChars, OldArchiveChars = oldArchiveChars, OldContinuationChars = oldContinuationChars,
                    NewAlive = aliveA, OldAlive = aliveB, EnqueueTick = enqueueTick, PreparedTick = preparedTick, OldReadTick = oldReadTick, CompleteTick = completeTick,
                    ReadTick = Stopwatch.GetTimestamp(), Frequency = Stopwatch.Frequency, CompleteGc0 = completeGc0, CompleteGc1 = completeGc1, CompleteGc2 = completeGc2,
                    ReadGc0Before = gcBefore, ReadGc0After = gcAfter, ReadGc1 = Gc(1), ReadGc2 = Gc(2), Events = eventCount, Queue = queue };
            }
        }
    }
}
