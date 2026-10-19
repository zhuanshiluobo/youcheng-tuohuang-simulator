using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    /// <summary>固定捕获只在Application内部可读；旧公开导出仍遵守原会话的保留窗口。</summary>
    public sealed class GameSessionRecoveryPoint
    {
        internal GameSessionRecoveryPoint(GameSession owner, string commandId) : this(owner, commandId, null, null) { }
        internal GameSessionRecoveryPoint(GameSession owner, string commandId, GameState state, GameSessionLogContinuationDto continuation)
        { Owner = owner; CommandId = commandId ?? string.Empty; CapturedState = state; CapturedContinuation = continuation; }
        internal GameSession Owner { get; }
        internal GameState CapturedState { get; }
        internal GameSessionLogContinuationDto CapturedContinuation { get; }
        public string CommandId { get; }
        internal GameSessionRecoveryExport ExportDetached() => new GameSessionRecoveryExport(
            HostRecoveryService.CreateArchive(GameStateCloneService.DeepClone(CapturedState)), GameStateCloneService.DeepClone(CapturedContinuation));
    }

    public sealed class GameSessionRecoveryExport
    {
        internal GameSessionRecoveryExport(HostSessionArchiveDto archive, GameSessionLogContinuationDto continuation)
        { Archive = archive; Continuation = continuation; }
        public HostSessionArchiveDto Archive { get; }
        public GameSessionLogContinuationDto Continuation { get; }
    }

    public sealed class PreparedGameSessionRecovery
    {
        internal PreparedGameSessionRecovery(GameSession owner, long generation, GameState state, GameStateView view,
            GameSessionLogContinuationDto continuation, bool backgroundPrepared = false, object preparedLog = null)
        { Owner = owner; Generation = generation; State = state; View = view; Continuation = continuation; BackgroundPrepared = backgroundPrepared; PreparedLog = preparedLog; }
        internal GameSession Owner { get; }
        internal long Generation { get; }
        internal GameState State { get; }
        internal GameStateView View { get; }
        internal GameSessionLogContinuationDto Continuation { get; }
        internal bool BackgroundPrepared { get; }
        internal object PreparedLog { get; }
        internal bool Committed;
    }

    [Serializable]
    public sealed class GameSessionLogContinuationDto
    {
        public int SchemaVersion = 1;
        public bool HasPendingActionLog;
        public int PlayerId = -1;
        public GameCommandKind Kind;
        public string Message;
        public GameCommandDto Command;
        public GameSessionCommandResultDto InitialResult;
        public GameSessionCommandResultDto SettlementResult;
        public GameSessionActionSnapshotDto Before;
        public List<GameCommandDto> Commands;
    }

    [Serializable]
    public sealed class GameSessionCommandResultDto
    {
        public bool Succeeded;
        public bool IsValid;
        public CommandErrorCode ErrorCode;
        public string Reason = string.Empty;
        public string LogMessage = string.Empty;
        public List<GameSessionEventDto> Events = new List<GameSessionEventDto>();
    }

    [Serializable]
    public sealed class GameSessionEventDto
    {
        public string EventId = string.Empty;
        public GameEventKind Kind;
        public int PlayerId = -1;
        public string SubjectId = string.Empty;
        public string Message = string.Empty;
        public List<GameCommandParameterDto> Data = new List<GameCommandParameterDto>();
    }

    [Serializable]
    public sealed class GameSessionActionSnapshotDto
    {
        public List<GameSessionPlayerSnapshotDto> Players = new List<GameSessionPlayerSnapshotDto>();
        public List<GameSessionInfluenceSnapshotDto> Influences = new List<GameSessionInfluenceSnapshotDto>();
        public List<string> FacilitySupply = new List<string>();
        public string PendingCharacterChoiceType = string.Empty;
        public string PendingCharacterCardId = string.Empty;
        public string PendingCharacterRemainingEffectMode = string.Empty;
        public GameState ContentState;
    }

    [Serializable]
    public sealed class GameSessionPlayerSnapshotDto
    {
        public int PlayerId;
        public string Name = string.Empty;
        public int Score;
        public string CityLocationId = string.Empty;
        public ResourceSet Resources = new ResourceSet();
        public List<string> DiscardCardIds = new List<string>();
    }

    [Serializable]
    public sealed class GameSessionInfluenceSnapshotDto
    {
        public int PlayerId;
        public string SlotId = string.Empty;
        public string LocationId = string.Empty;
        public string RouteId = string.Empty;
    }

    public sealed partial class GameSession
    {
        private int backgroundBound, backgroundOwnerThread, backgroundPlayerId, backgroundOwnerExited;
        private long backgroundGeneration, backgroundCommitVersion, backgroundPublishedCommitVersion = -1;
        private GameState backgroundReadState;
        private bool IsBackgroundReader => Volatile.Read(ref backgroundBound) != 0 && Thread.CurrentThread.ManagedThreadId != Volatile.Read(ref backgroundOwnerThread);
        internal bool HasBackgroundExecutionOwner => Volatile.Read(ref backgroundOwnerThread) != 0;
        internal bool IsBackgroundExecutionQuiescent => Volatile.Read(ref backgroundBound) == 0 || Volatile.Read(ref backgroundOwnerExited) != 0;
        internal long BackgroundCommitVersion => Interlocked.Read(ref backgroundCommitVersion);
        internal void BindBackgroundExecution(long generation, int playerId = -1)
        {
            if (Interlocked.CompareExchange(ref backgroundBound, 1, 0) != 0) throw new InvalidOperationException("session_execution_owner_already_bound");
            backgroundGeneration = generation; backgroundPlayerId = playerId;
            Volatile.Write(ref backgroundOwnerExited, 0);
            backgroundReadState = null; backgroundPublishedCommitVersion = -1; backgroundCommitVersion = 0;
        }
        internal void ClaimBackgroundOwner(long generation)
        {
            if (backgroundGeneration != generation || Volatile.Read(ref backgroundBound) == 0 ||
                Interlocked.CompareExchange(ref backgroundOwnerThread, Thread.CurrentThread.ManagedThreadId, 0) != 0)
                throw new InvalidOperationException("session_execution_owner_conflict");
        }
        internal void ReleaseBackgroundOwner(long generation)
        { if (backgroundGeneration == generation) { Volatile.Write(ref backgroundOwnerThread, 0); Volatile.Write(ref backgroundOwnerExited, 1); } }
        internal void CancelBackgroundBinding(long generation)
        { if (backgroundGeneration == generation && !HasBackgroundExecutionOwner) Volatile.Write(ref backgroundBound, 0); }
        private void EnsureExecutionOwner()
        { if (IsBackgroundReader) throw new InvalidOperationException("session_mutation_requires_execution_owner"); }
        internal IReadOnlyList<Exception> PublishBackgroundReadState(long generation, long commitVersion, GameState uiState, GameStateView uiView, Func<bool> canNotify)
        {
            var errors = new List<Exception>();
            if (backgroundGeneration != generation || Volatile.Read(ref backgroundBound) == 0) return errors;
            Volatile.Write(ref backgroundReadState, uiState); View = uiView;
            bool notify = commitVersion > backgroundPublishedCommitVersion && commitVersion > 0;
            backgroundPublishedCommitVersion = commitVersion;
            if (notify)
                foreach (Action listener in StateCommitted?.GetInvocationList() ?? Array.Empty<Delegate>())
                {
                    if (!canNotify()) break;
                    try { listener(); } catch (Exception exception) { errors.Add(exception); }
                }
            return errors;
        }
        internal GameSessionRecoveryPoint ReadLatestBackgroundRecoveryPoint()
        {
            EnsureExecutionOwner();
            return FreezeBackgroundRecoveryPoint(latestRecoveryCapture?.Point ?? EnableConfirmedRecoveryCapture());
        }
        internal GameSessionRecoveryPoint FreezeBackgroundRecoveryPoint(GameSessionRecoveryPoint point)
        {
            EnsureExecutionOwner();
            if (point == null || !ReferenceEquals(point.Owner, this)) throw new InvalidOperationException("checkpoint_not_retained");
            var capture = ReferenceEquals(latestRecoveryCapture?.Point, point) ? latestRecoveryCapture :
                ReferenceEquals(previousRecoveryCapture?.Point, point) ? previousRecoveryCapture :
                ReferenceEquals(pinnedRecoveryCapture?.Point, point) ? pinnedRecoveryCapture : null;
            if (capture == null) throw new InvalidOperationException("checkpoint_not_retained");
            // 仅网络私有发布句柄持有强捕获；Local旧公开句柄继续只标识保留窗口。
            return new GameSessionRecoveryPoint(this, point.CommandId, capture.State, capture.Continuation);
        }
        internal static CommandResult CopyCommandResult(CommandResult result)
        {
            if (result == null) return null;
            if (!result.Succeeded) return CommandResult.Invalid(new ValidationResult(result.Validation.IsValid, result.Validation.ErrorCode, result.Validation.Reason));
            return CommandResult.SuccessResult(result.Events.Select(e => new GameEvent { EventId = e.EventId, Kind = e.Kind, PlayerId = e.PlayerId,
                SubjectId = e.SubjectId, Message = e.Message, Data = e.Data == null ? null : new Dictionary<string, string>(e.Data, StringComparer.Ordinal) }).ToList(), result.LogMessage);
        }

        private long mutationGeneration;
        private bool confirmedRecoveryCaptureEnabled;
        private RecoveryCapture latestRecoveryCapture, previousRecoveryCapture, pinnedRecoveryCapture;

        private sealed class RecoveryCapture
        {
            internal GameSessionRecoveryPoint Point;
            internal GameState State;
            internal GameSessionLogContinuationDto Continuation;
        }

        public GameSessionRecoveryPoint EnableConfirmedRecoveryCapture()
        {
            EnsureExecutionOwner();
            if (confirmedRecoveryCaptureEnabled && latestRecoveryCapture != null) return latestRecoveryCapture.Point;
            var capture = PrepareRecoveryCapture(State, pendingActionLog, string.Empty);
            confirmedRecoveryCaptureEnabled = true;
            RetainRecoveryCapture(capture);
            pinnedRecoveryCapture = capture;
            return capture.Point;
        }

        public bool TryReadCommittedRecoveryPoint(string commandId, out GameSessionRecoveryPoint point)
        {
            EnsureExecutionOwner();
            point = null;
            if (string.IsNullOrEmpty(commandId)) return false;
            var capture = latestRecoveryCapture?.Point.CommandId == commandId ? latestRecoveryCapture :
                previousRecoveryCapture?.Point.CommandId == commandId ? previousRecoveryCapture :
                pinnedRecoveryCapture?.Point.CommandId == commandId ? pinnedRecoveryCapture : null;
            if (capture == null) return false;
            pinnedRecoveryCapture = capture;
            point = capture.Point;
            return true;
        }

        public GameSessionRecoveryExport ExportConfirmedRecoveryPoint(GameSessionRecoveryPoint point)
        {
            EnsureExecutionOwner();
            if (point == null || !ReferenceEquals(point.Owner, this)) throw new InvalidOperationException("checkpoint_not_retained");
            var capture = ReferenceEquals(latestRecoveryCapture?.Point, point) ? latestRecoveryCapture :
                ReferenceEquals(previousRecoveryCapture?.Point, point) ? previousRecoveryCapture :
                ReferenceEquals(pinnedRecoveryCapture?.Point, point) ? pinnedRecoveryCapture : null;
            if (capture == null) throw new InvalidOperationException("checkpoint_not_retained");
            if (Volatile.Read(ref backgroundBound) != 0) return FreezeBackgroundRecoveryPoint(point).ExportDetached();
            return new GameSessionRecoveryExport(HostRecoveryService.CreateArchive(capture.State),
                GameStateCloneService.DeepClone(capture.Continuation));
        }

        public bool TryPrepareConfirmedRecoveryState(GameState restored, GameSessionLogContinuationDto continuation,
            GameStateViewer viewer, out PreparedGameSessionRecovery prepared, out string fault)
            => TryPrepareRecoveryState(restored, continuation, viewer, out prepared, out fault);

        /// <summary>本地热座按当前操作者即时投影，恢复时不固定某一玩家的展示快照。</summary>
        public bool TryPrepareLocalRecoveryState(GameState restored, GameSessionLogContinuationDto continuation,
            out PreparedGameSessionRecovery prepared, out string fault)
            => TryPrepareRecoveryState(restored, continuation, null, out prepared, out fault);

        private bool TryPrepareRecoveryState(GameState restored, GameSessionLogContinuationDto continuation,
            GameStateViewer? viewer, out PreparedGameSessionRecovery prepared, out string fault)
        {
            prepared = null; fault = string.Empty;
            try
            {
                if (!IsBackgroundExecutionQuiescent) throw new InvalidOperationException("recovery_execution_not_quiescent");
                if (restored == null || continuation == null || restored.GameId != activeState.GameId ||
                    restored.ContentPackHash != activeState.ContentPackHash || restored.EventDeckSeed != activeState.EventDeckSeed)
                    throw new InvalidOperationException("recovery_session_identity_mismatch");
                var detached = GameStateCloneService.DeepClone(restored);
                var detachedContinuation = GameStateCloneService.DeepClone(continuation);
                var preparedLog = RestoreContinuation(detachedContinuation, detached);
                if (detached.RandomRuntime != null && !GameplayRandomRuntime.TryValidate(detached.RandomRuntime,
                    detached.GameId, detached.EventDeckSeed, out string reason)) throw new InvalidOperationException(reason);
                if (detached.EffectRuntime != null && !detached.EffectRuntime.TryValidate(out string runtimeReason))
                    throw new InvalidOperationException(runtimeReason);
                var view = viewer.HasValue ? GameStateViewProjector.Project(detached, viewer.Value,
                    settlementProjectionProvider?.GetSettlementOptions(detached, viewer.Value)) : null;
                bool backgroundPrepared = Volatile.Read(ref backgroundBound) != 0;
                prepared = new PreparedGameSessionRecovery(this, mutationGeneration, detached, view, detachedContinuation, backgroundPrepared,
                    backgroundPrepared ? preparedLog : null);
                return true;
            }
            catch (Exception ex) { fault = ex.Message; return false; }
        }

        public bool TryCommitPreparedRecovery(PreparedGameSessionRecovery prepared, out string fault)
        {
            fault = string.Empty;
            if (!IsBackgroundExecutionQuiescent) { fault = "recovery_execution_not_quiescent"; return false; }
            if (prepared == null || !ReferenceEquals(prepared.Owner, this)) { fault = "recovery_wrong_session"; return false; }
            if (prepared.Committed) return true;
            if (prepared.Generation != mutationGeneration) { fault = "recovery_prepared_expired"; return false; }
            PendingActionLog nextLog;
            try { nextLog = prepared.BackgroundPrepared ? (PendingActionLog)prepared.PreparedLog : RestoreContinuation(prepared.Continuation, prepared.State); }
            catch (Exception ex) { fault = ex.Message; return false; }
            // 所有可能失败的复制、校验和投影都在发布前完成。暂停区内只替换根引用，不调用消费者。
            Volatile.Write(ref backgroundBound, 0); backgroundReadState = null;
            State = prepared.State;
            View = prepared.View;
            pendingActionLog = nextLog;
            mutationGeneration++;
            confirmedRecoveryCaptureEnabled = false;
            latestRecoveryCapture = previousRecoveryCapture = pinnedRecoveryCapture = null;
            prepared.Committed = true;
            return true;
        }

        private RecoveryCapture PrepareRecoveryCapture(GameState state, PendingActionLog log, string commandId)
        {
            using var captureSample = new ExecutionSample(ExecutionTimings, "recovery.capture");
            var captured = GameStateCloneService.DeepClone(state); var continuation = CaptureContinuation(log);
            return new RecoveryCapture { Point = new GameSessionRecoveryPoint(this, commandId), State = captured, Continuation = continuation };
        }

        private void RetainRecoveryCapture(RecoveryCapture capture)
        { previousRecoveryCapture = latestRecoveryCapture; latestRecoveryCapture = capture; }

        private void InvalidateConfirmedRecovery()
        {
            mutationGeneration++;
            confirmedRecoveryCaptureEnabled = false;
            latestRecoveryCapture = previousRecoveryCapture = pinnedRecoveryCapture = null;
        }

        private static GameSessionLogContinuationDto CaptureContinuation(PendingActionLog log)
        {
            if (log == null) return new GameSessionLogContinuationDto();
            var dto = new GameSessionLogContinuationDto { HasPendingActionLog = true, PlayerId = log.PlayerId,
                Kind = log.Kind, Message = log.Message, Command = GameCommandDto.FromCommand(log.Command),
                InitialResult = CaptureResult(log.InitialResult), SettlementResult = CaptureResult(log.SettlementResult),
                Before = CaptureSnapshot(log.Before), Commands = log.Commands.Select(GameCommandDto.FromCommand).ToList() };
            return GameStateCloneService.DeepClone(dto);
        }

        private static PendingActionLog RestoreContinuation(GameSessionLogContinuationDto dto, GameState state)
        {
            if (dto == null || dto.SchemaVersion != 1) throw new InvalidOperationException("recovery_continuation_schema_invalid");
            if (!dto.HasPendingActionLog)
            {
                if (dto.Command != null || dto.InitialResult != null || dto.SettlementResult != null || dto.Before != null ||
                    dto.Commands != null || dto.Message != null || dto.PlayerId != -1) throw new InvalidOperationException("recovery_unexpected_continuation");
                return null;
            }
            if (!Enum.IsDefined(typeof(GameCommandKind), dto.Kind) || state.FindPlayer(dto.PlayerId) == null || dto.Message == null ||
                dto.Command == null || dto.Command.PlayerId != dto.PlayerId || dto.Command.Kind != dto.Kind ||
                dto.InitialResult == null || dto.Before == null || dto.Commands == null || dto.Commands.Count == 0)
                throw new InvalidOperationException("recovery_continuation_incomplete");
            ValidateCommand(dto.Command);
            foreach (var command in dto.Commands) ValidateCommand(command);
            if (dto.Commands[0].CommandId != dto.Command.CommandId) throw new InvalidOperationException("recovery_log_root_mismatch");
            return new PendingActionLog { PlayerId = dto.PlayerId, Kind = dto.Kind, Message = dto.Message,
                Command = dto.Command.ToCommand(), InitialResult = RestoreResult(dto.InitialResult),
                SettlementResult = RestoreResult(dto.SettlementResult), Before = RestoreSnapshot(dto.Before),
                Commands = dto.Commands.Select(c => c.ToCommand()).ToList() };
        }

        private static void ValidateCommand(GameCommandDto dto)
        {
            if (dto == null || string.IsNullOrEmpty(dto.CommandId) || !Enum.IsDefined(typeof(GameCommandKind), dto.Kind) ||
                dto.OptionIds == null || dto.Parameters == null || dto.OptionIds.Any(x => x == null) ||
                dto.Parameters.Any(p => p == null || string.IsNullOrEmpty(p.Key) || p.Value == null) ||
                dto.Parameters.GroupBy(p => p.Key, StringComparer.Ordinal).Any(g => g.Count() != 1))
                throw new InvalidOperationException("recovery_command_invalid");
        }

        private static GameSessionCommandResultDto CaptureResult(CommandResult result)
        {
            if (result == null) return null;
            return new GameSessionCommandResultDto { Succeeded = result.Succeeded, IsValid = result.Validation.IsValid,
                ErrorCode = result.Validation.ErrorCode, Reason = result.Validation.Reason, LogMessage = result.LogMessage,
                Events = result.Events.Select(e => new GameSessionEventDto { EventId = e.EventId, Kind = e.Kind, PlayerId = e.PlayerId,
                    SubjectId = e.SubjectId, Message = e.Message,
                    Data = e.Data.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new GameCommandParameterDto { Key = p.Key, Value = p.Value }).ToList() }).ToList() };
        }

        private static CommandResult RestoreResult(GameSessionCommandResultDto dto)
        {
            if (dto == null) return null;
            if (dto.Events == null || !Enum.IsDefined(typeof(CommandErrorCode), dto.ErrorCode) ||
                dto.Succeeded != dto.IsValid || dto.Succeeded && dto.ErrorCode != CommandErrorCode.None ||
                !dto.Succeeded && dto.Events.Count != 0) throw new InvalidOperationException("recovery_result_invalid");
            var events = new List<GameEvent>();
            foreach (var e in dto.Events)
            {
                if (e == null || string.IsNullOrEmpty(e.EventId) || !Enum.IsDefined(typeof(GameEventKind), e.Kind) || e.Data == null ||
                    e.Data.Any(p => p == null || string.IsNullOrEmpty(p.Key) || p.Value == null) ||
                    e.Data.GroupBy(p => p.Key, StringComparer.Ordinal).Any(g => g.Count() != 1)) throw new InvalidOperationException("recovery_event_invalid");
                events.Add(new GameEvent { EventId = e.EventId, Kind = e.Kind, PlayerId = e.PlayerId, SubjectId = e.SubjectId,
                    Message = e.Message, Data = e.Data.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) });
            }
            return dto.Succeeded ? CommandResult.SuccessResult(events, dto.LogMessage) :
                CommandResult.Invalid(new ValidationResult(false, dto.ErrorCode, dto.Reason));
        }

        private static GameSessionActionSnapshotDto CaptureSnapshot(PublicActionStateSnapshot before)
        {
            if (before == null) throw new InvalidOperationException("recovery_before_missing");
            return new GameSessionActionSnapshotDto { ContentState = GameStateCloneService.DeepClone(before.ContentState),
                PendingCharacterChoiceType = before.PendingCharacterChoiceType, PendingCharacterCardId = before.PendingCharacterCardId,
                PendingCharacterRemainingEffectMode = before.PendingCharacterRemainingEffectMode,
                FacilitySupply = before.FacilitySupply.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                Players = before.Players.OrderBy(p => p.Key).Select(p => new GameSessionPlayerSnapshotDto { PlayerId = p.Value.PlayerId,
                    Name = p.Value.Name, Score = p.Value.Score, CityLocationId = p.Value.CityLocationId, Resources = p.Value.Resources.Clone(),
                    DiscardCardIds = p.Value.DiscardCardIds.OrderBy(x => x, StringComparer.Ordinal).ToList() }).ToList(),
                Influences = before.Influences.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new GameSessionInfluenceSnapshotDto {
                    PlayerId = p.Value.PlayerId, SlotId = p.Value.SlotId, LocationId = p.Value.LocationId, RouteId = p.Value.RouteId }).ToList() };
        }

        private static PublicActionStateSnapshot RestoreSnapshot(GameSessionActionSnapshotDto dto)
        {
            if (dto.ContentState == null || dto.Players == null || dto.Influences == null || dto.FacilitySupply == null ||
                dto.PendingCharacterChoiceType == null || dto.PendingCharacterCardId == null || dto.PendingCharacterRemainingEffectMode == null ||
                dto.Players.Any(p => p == null || p.Resources == null || p.DiscardCardIds == null || p.DiscardCardIds.Any(x => x == null)) ||
                dto.Players.GroupBy(p => p.PlayerId).Any(g => g.Count() != 1) || dto.Influences.Any(p => p == null || string.IsNullOrEmpty(p.SlotId)) ||
                dto.Influences.GroupBy(p => p.SlotId, StringComparer.Ordinal).Any(g => g.Count() != 1) || dto.FacilitySupply.Any(x => x == null) ||
                dto.FacilitySupply.Distinct(StringComparer.Ordinal).Count() != dto.FacilitySupply.Count)
                throw new InvalidOperationException("recovery_before_invalid");
            var before = new PublicActionStateSnapshot { ContentState = GameStateCloneService.DeepClone(dto.ContentState),
                PendingCharacterChoiceType = dto.PendingCharacterChoiceType, PendingCharacterCardId = dto.PendingCharacterCardId,
                PendingCharacterRemainingEffectMode = dto.PendingCharacterRemainingEffectMode };
            foreach (var p in dto.Players) before.Players.Add(p.PlayerId, new PublicPlayerActionSnapshot { PlayerId = p.PlayerId, Name = p.Name,
                Score = p.Score, CityLocationId = p.CityLocationId, Resources = p.Resources.Clone(), DiscardCardIds = new HashSet<string>(p.DiscardCardIds, StringComparer.Ordinal) });
            foreach (var p in dto.Influences) before.Influences.Add(p.SlotId, new PublicInfluenceActionSnapshot { PlayerId = p.PlayerId,
                SlotId = p.SlotId, LocationId = p.LocationId, RouteId = p.RouteId });
            foreach (var id in dto.FacilitySupply) before.FacilitySupply.Add(id);
            return before;
        }
    }
}
