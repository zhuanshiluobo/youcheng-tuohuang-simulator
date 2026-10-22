using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    // gate只覆盖进出队，不覆盖Submit、投影、摘要、捕获或消费者。
    internal sealed class DeterministicSimulationExecutionLoop
    {
        private sealed class Publication
        {
            internal SimulationPublishedSnapshot Snapshot;
            internal GameState UiState;
            internal GameStateView UiView;
            internal Action[] Notifications;
        }
        private static long generations;
        private readonly DeterministicSimulationCoordinator owner;
        private readonly GameSession session;
        private readonly int capacity;
        private readonly object gate = new object();
        private readonly Queue<Action> incoming = new Queue<Action>();
        private readonly Queue<Publication> outgoing = new Queue<Publication>();
        private readonly List<Action> notifications = new List<Action>();
        private readonly List<Exception> consumerErrors = new List<Exception>();
        private readonly AutoResetEvent available = new AutoResetEvent(false);
        private readonly TaskCompletionSource<object> completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread thread;
        private readonly int mainThreadId = Thread.CurrentThread.ManagedThreadId;
        private SimulationPublishedSnapshot latest;
        private Exception fatal;
        private bool deliveredFatal;
        private bool signalDisposed;
        private int stopping, threadId;
        private long publicationVersion, lastStateCommitVersion = -1;
        internal long Generation { get; } = Interlocked.Increment(ref generations);
        internal bool IsWorkerThread => Thread.CurrentThread.ManagedThreadId == Volatile.Read(ref threadId);
        internal bool IsMainThread => Thread.CurrentThread.ManagedThreadId == mainThreadId;
        internal void EnsureMainThread()
        { if (Thread.CurrentThread.ManagedThreadId != mainThreadId) throw new InvalidOperationException("simulation_requires_main_thread"); }
        internal bool IsStopping => Volatile.Read(ref stopping) != 0;
        internal Task Completion => completion.Task;
        internal Exception Fatal => Volatile.Read(ref fatal);
        internal SimulationPublishedSnapshot Latest => Volatile.Read(ref latest);
        internal bool HasCapacity { get { lock (gate) return !IsStopping && Fatal == null && !completion.Task.IsCompleted && incoming.Count < capacity && outgoing.Count < capacity; } }

        internal DeterministicSimulationExecutionLoop(DeterministicSimulationCoordinator owner, GameSession session, int capacity)
        {
            this.owner = owner; this.session = session; this.capacity = capacity;
            thread = new Thread(Run) { IsBackground = true, Name = "YC deterministic session " + Generation };
        }
        internal void Start() => thread.Start();
        internal void CancelStartup()
        {
            RequestStop();
            lock (gate) { if (!signalDisposed) { signalDisposed = true; available.Dispose(); } }
            completion.TrySetResult(null);
        }
        internal bool TryEnqueue(Action action)
        {
            lock (gate)
            {
                if (IsStopping || Fatal != null || completion.Task.IsCompleted || incoming.Count >= capacity) return false;
                incoming.Enqueue(action);
                available.Set();
            }
            return true;
        }
        internal void AddNotification(Action action) => notifications.Add(action);
        internal void AddConsumerError(Exception exception) => consumerErrors.Add(exception);
        internal void RequestStop()
        {
            Interlocked.Exchange(ref stopping, 1);
            lock (gate) { if (!signalDisposed) available.Set(); Monitor.PulseAll(gate); }
        }
        private void Run()
        {
            Volatile.Write(ref threadId, Thread.CurrentThread.ManagedThreadId);
            try
            {
                session.ClaimBackgroundOwner(Generation);
                owner.InitializeBackgroundWorker(); Publish();
                while (!IsStopping)
                {
                    Action action = null;
                    lock (gate) if (incoming.Count > 0) action = incoming.Dequeue();
                    bool worked = action != null;
                    if (action != null) { action(); Publish(); }
                    if (!IsStopping && owner.CanPumpBackground()) { owner.Pump(1); Publish(); worked = true; }
                    if (!worked && !IsStopping) available.WaitOne();
                }
            }
            catch (Exception exception)
            {
                Volatile.Write(ref fatal, exception);
                try { owner.BackgroundWorkerFault(exception); Publish(); } catch (Exception publishError) { Volatile.Write(ref fatal, new AggregateException(exception, publishError)); }
            }
            finally
            {
                session.ReleaseBackgroundOwner(Generation);
                lock (gate) { signalDisposed = true; available.Dispose(); }
                completion.TrySetResult(null);
            }
        }
        private void Publish()
        {
            var snapshot = owner.CreateBackgroundSnapshot(++publicationVersion, Generation);
            bool changed = lastStateCommitVersion != snapshot.CommitVersion;
            // 为原展示端口准备独立图的工作也在owner线程；snapshot私有捕获不交给UI修改。
            var frame = new Publication { Snapshot = snapshot, Notifications = notifications.ToArray(),
                UiState = changed ? snapshot.CreateUiReadState() : null, UiView = changed ? snapshot.ReadView() : null };
            notifications.Clear(); lastStateCommitVersion = snapshot.CommitVersion;
            Volatile.Write(ref latest, snapshot);
            lock (gate)
            {
                while (!IsStopping && outgoing.Count >= capacity) Monitor.Wait(gate);
                if (!IsStopping) outgoing.Enqueue(frame);
            }
        }
        internal int Drain(int maximumCount)
        {
            EnsureMainThread();
            if (maximumCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCount));
            int count = 0;
            while (count < maximumCount)
            {
                Publication frame;
                lock (gate)
                {
                    if (outgoing.Count == 0) break;
                    frame = outgoing.Dequeue(); Monitor.PulseAll(gate);
                }
                if (IsStopping || frame.Snapshot.Generation != Generation) continue;
                owner.SetVisibleBackgroundSnapshot(frame.Snapshot);
                if (frame.UiState != null)
                    consumerErrors.AddRange(session.PublishBackgroundReadState(Generation, frame.Snapshot.CommitVersion, frame.UiState, frame.UiView, () => !IsStopping));
                foreach (var notification in frame.Notifications)
                {
                    if (IsStopping) break;
                    try { notification(); } catch (Exception exception) { consumerErrors.Add(exception); }
                }
                count++;
            }
            if (consumerErrors.Count > 0)
            {
                var failures = consumerErrors.ToArray(); consumerErrors.Clear();
                throw new AggregateException("background_consumer_notification_failed：已提交结果保持，主线程消费者异常。", failures);
            }
            if (!IsStopping && !deliveredFatal && Fatal != null && Latest?.ReadFault() == null)
            { deliveredFatal = true; owner.DeliverBackgroundFatal(Fatal); }
            return count;
        }
    }

    public sealed partial class DeterministicSimulationCoordinator
    {
        private DeterministicSimulationExecutionLoop background;
        private SimulationPublishedSnapshot visibleBackground;
        private OperationCommandProposalDto backgroundPendingLocal;
        private bool backgroundPendingValidationLocal;
        private int backgroundRetirementRequested;
        private long backgroundNextSubmission;
        private bool backgroundRecoveryRequested, backgroundCompleteRequested;
        private bool backgroundAdmitting;
        private SimulationProtocolFaultDto backgroundMainFault;
        private readonly SortedDictionary<long, OrderedOperationCommandDto> backgroundKnown = new SortedDictionary<long, OrderedOperationCommandDto>();
        private readonly Dictionary<long, SimulationBoundaryDto> backgroundFrozenBoundaries = new Dictionary<long, SimulationBoundaryDto>();
        private readonly Dictionary<string, RequestAuthorityStamp> backgroundRequestAuthorities = new Dictionary<string, RequestAuthorityStamp>(StringComparer.Ordinal);
        private sealed class PendingAdmission
        {
            internal int PlayerId;
            internal OperationCommandProposalDto Proposal;
            internal string Digest;
        }
        private readonly List<PendingAdmission> backgroundAdmissions = new List<PendingAdmission>();
        private bool OnBackgroundMainThread
        {
            get
            {
                if (background == null || background.IsWorkerThread) return false;
                background.EnsureMainThread(); return true;
            }
        }
        public bool IsBackgroundExecutionEnabled => background != null;
        public Task ExecutionStopped => background?.Completion ?? Task.CompletedTask;
        public Exception BackgroundExecutionException => background?.Fatal;

        /// <summary>仅正式联机入口显式调用。Local/Sequential不调用此方法，所有默认同步行为保留。</summary>
        public void StartBackgroundExecution()
        {
            if (background != null) { background.EnsureMainThread(); return; }
            backgroundNextSubmission = nextLocalSubmission;
            backgroundRecoveryRequested = recoveryFrozen;
            var loop = new DeterministicSimulationExecutionLoop(this, session, options.MaximumQueuedCommands);
            session.BindBackgroundExecution(loop.Generation, options.LocalPlayerId);
            background = loop;
            try { loop.Start(); }
            catch { loop.CancelStartup(); background = null; session.CancelBackgroundBinding(loop.Generation); throw; }
        }
        public int DrainMainThreadNotifications(int maximumCount)
        {
            if (background == null) return 0;
            background.EnsureMainThread();
            if (Interlocked.Exchange(ref backgroundRetirementRequested, 0) != 0) RetirePendingAdmissions("原会话已停止，原等待提案未获准入。");
            try { return background.Drain(maximumCount); }
            finally { ProcessPendingAdmissions(); }
        }
        public bool TryReadPublishedSnapshot(out SimulationPublishedSnapshot snapshot)
        {
            background?.EnsureMainThread();
            var source = visibleBackground;
            snapshot = source?.WithMainThreadFacts(LastSequencedSequence, backgroundPendingLocal != null,
                background.IsStopping || background.Fatal != null, backgroundRecoveryRequested, backgroundMainFault);
            return snapshot != null;
        }
        public void RequestStop()
        {
            if (background == null) { stopped = true; return; }
            background.RequestStop();
            if (background.IsMainThread) RetirePendingAdmissions("原会话已停止，原等待提案未获准入。");
            else Interlocked.Exchange(ref backgroundRetirementRequested, 1);
        }
        public Task<SimulationRecoveryCheckpointDto> CreateSaveCheckpointAsync()
        {
            if (background == null)
            {
                return TryCreateSaveCheckpoint(out var checkpoint, out var result) ? Task.FromResult(checkpoint) :
                    Task.FromException<SimulationRecoveryCheckpointDto>(new InvalidOperationException(result.ErrorCode + ":" + result.Reason));
            }
            if (!TryReadPublishedSnapshot(out var snapshot)) return FailedBackgroundCheckpoint("simulation_snapshot_not_ready");
            return snapshot.CreateSaveCheckpointAsync();
        }
        public Task<SimulationRecoveryCheckpointDto> CreateConfirmedRecoveryCheckpointAsync(long expectedConfirmedSequence, long targetEpoch)
        {
            if (background == null)
                return TryCreateConfirmedRecoveryCheckpoint(expectedConfirmedSequence, targetEpoch, out var checkpoint, out var result) ? Task.FromResult(checkpoint) :
                    Task.FromException<SimulationRecoveryCheckpointDto>(new InvalidOperationException(result.ErrorCode + ":" + result.Reason));
            background.EnsureMainThread();
            var snapshot = visibleBackground ?? background.Latest;
            if (snapshot?.ValidatedThroughSequence != expectedConfirmedSequence && background.Latest?.ValidatedThroughSequence == expectedConfirmedSequence)
                snapshot = background.Latest;
            return snapshot == null ? FailedBackgroundCheckpoint("simulation_snapshot_not_ready") :
                snapshot.WithMainThreadFacts(LastSequencedSequence, backgroundPendingLocal != null,
                    background.IsStopping || background.Fatal != null, backgroundRecoveryRequested, backgroundMainFault)
                    .CreateConfirmedRecoveryCheckpointAsync(expectedConfirmedSequence, targetEpoch);
        }
        private static Task<SimulationRecoveryCheckpointDto> FailedBackgroundCheckpoint(string code)
            => Task.FromException<SimulationRecoveryCheckpointDto>(new InvalidOperationException(code));

        private void PostBackground(Action action)
        {
            if (background.IsStopping) return;
            if (!background.TryEnqueue(action)) BackgroundMainFault("execution_queue_capacity", "执行消息队列已满或该代次已停止，保留原命令等待恢复。");
        }
        private void BackgroundMainFault(string code, string reason, OrderedOperationCommandDto command = null)
        {
            if (background.IsStopping) return;
            var notice = new SimulationProtocolFaultDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch,
                MembershipVersion = options.MembershipVersion, FaultCode = code, Diagnostic = reason,
                ErrorCode = CommandErrorCode.UnknownCommand, RequiresRecovery = true,
                FailureGlobalSequence = command?.GlobalCommandSequence ?? 0, Command = CloneCommand(command?.Command) };
            backgroundMainFault = notice; background.RequestStop();
            if (background.IsMainThread) RetirePendingAdmissions("原会话出现协议或执行故障，原等待提案未获准入。");
            else Interlocked.Exchange(ref backgroundRetirementRequested, 1);
            Deliver(SimulationProtocolFaulted, notice);
        }
        internal void DeliverBackgroundFatal(Exception exception) => BackgroundMainFault("background_publication_failed", exception.Message);
        private void Deliver<T>(Action<T> handlers, T value)
        {
            if (!IsBackgroundExecutionEnabled) { handlers?.Invoke(value); return; }
            if (background.IsWorkerThread) { background.AddNotification(() => Deliver(handlers, value)); return; }
            if (handlers == null) return;
            foreach (Action<T> listener in handlers.GetInvocationList())
            {
                if (background.IsStopping && !(value is SimulationProtocolFaultDto)) break;
                try { listener(value); } catch (Exception exception) { background.AddConsumerError(exception); }
            }
        }
        internal void SetVisibleBackgroundSnapshot(SimulationPublishedSnapshot snapshot)
        {
            visibleBackground = snapshot;
            if (backgroundCompleteRequested && !snapshot.IsRecoveringConfirmedPrefix)
            { backgroundCompleteRequested = false; backgroundRecoveryRequested = false; }
        }
        private IReadOnlyList<OrderedOperationCommandDto> ReadBackgroundPendingTail()
        {
            var tail = new SortedDictionary<long, OrderedOperationCommandDto>();
            if (visibleBackground != null)
                foreach (var command in visibleBackground.ReadPendingTail()) tail[command.GlobalCommandSequence] = command;
            foreach (var pair in backgroundKnown.Where(pair => pair.Key > LastAppliedSequence))
                tail[pair.Key] = OperationCommandCanonicalizer.SnapshotOrdered(pair.Value);
            return tail.Values.ToList().AsReadOnly();
        }
        internal void InitializeBackgroundWorker()
        {
            replica.EnableDeferredRevisionValidation();
            replica.ConfigureRequestContractValidation(requestContractVersion, checkpointStateRevision);
            session.ConfigureOrderedRequestContractValidation(requestContractVersion);
            session.EnableConfirmedRecoveryCapture();
        }
        internal bool CanPumpBackground() => begun && !stopped && (!recoveryFrozen || options.IsHost) && queue.ContainsKey(replica.LastAppliedSequence + 1);
        internal void BackgroundWorkerFault(Exception exception) => Fault("background_execution_exception", exception.Message);

        internal SimulationPublishedSnapshot CreateBackgroundSnapshot(long version, long generation)
        {
            var point = session.ReadLatestBackgroundRecoveryPoint();
            var previous = background.Latest;
            GameStateView view = previous != null && previous.CommitVersion == session.BackgroundCommitVersion ?
                previous.ReadView() : session.ProjectFor(GameStateViewer.Player(options.LocalPlayerId));
            SimulationPublishedCheckpoint checkpoint = null;
            foreach (var pair in boundaries)
                if (!backgroundFrozenBoundaries.ContainsKey(pair.Key))
                    backgroundFrozenBoundaries.Add(pair.Key, OperationCommandCanonicalizer.SnapshotBoundary(pair.Value));
            // 协议事实失败时实际Applied可能已越过确认前缀；恢复仍持原确认点，不能取当前State。
            if (options.IsHost && confirmedPoint != null && confirmedWindows != null)
            {
                var prefix = replica.CreateValidationPrefix(0, validatedThrough);
                var metadata = new SimulationRecoveryBaselineDto { GameId = options.GameId, SourceSimulationEpoch = options.SimulationEpoch,
                    MembershipVersion = options.MembershipVersion, ConfirmedGlobalSequence = validatedThrough,
                    CommandPrefixDigest = prefix.PrefixDigest, ContentPackHash = options.ContentPackHash,
                    RuleDigestVersion = RuleStateDigestService.DigestVersion, FrozenPlayerIds = new List<int>(options.FrozenPlayerIds),
                    RequiredReplicaIds = new List<string>(options.RequiredReplicaIds), ConfirmedCommands = replica.ReadConfirmedReceipts(validatedThrough) };
                foreach (int player in options.FrozenPlayerIds) metadata.NextSubmissionSequences.Add(player, 1);
                foreach (var receipt in metadata.ConfirmedCommands) metadata.NextSubmissionSequences[receipt.Outcome.PlayerId] = receipt.ClientSubmissionSequence + 1;
                foreach (var fact in confirmedWindows) metadata.Windows.Add(OperationCommandCanonicalizer.SnapshotRecoveryWindow(fact));
                checkpoint = new SimulationPublishedCheckpoint(session.FreezeBackgroundRecoveryPoint(confirmedPoint), metadata, options);
            }
            var openAuthorities = new Dictionary<string, RequestAuthorityStamp>(StringComparer.Ordinal);
            if (requestContractVersion == OperationCommandProtocol.RequestContractVersion)
                foreach (var request in point.CapturedState.EffectRuntime.InteractionRequests.Where(r => r.Status == "open"))
                {
                    var probe = new GameCommand { Kind = GameCommandKind.AnswerInteraction, CommandId = "request.authority.read", PlayerId = request.AnsweringPlayerId };
                    probe.Parameters["interactionId"] = request.GetStableInteractionId();
                    if (!InteractionRequestValidity.TryReadRequestAuthority(point.CapturedState, probe, out var authority, out _)) continue;
                    var stamp = backgroundRequestAuthorities.TryGetValue(authority.InteractionId, out var previousStamp) && previousStamp.Digest == authority.Digest
                        ? previousStamp : new RequestAuthorityStamp(authority.Digest, replica.LastAppliedSequence);
                    openAuthorities[authority.InteractionId] = stamp;
                }
            backgroundRequestAuthorities.Clear();
            foreach (var pair in openAuthorities) backgroundRequestAuthorities.Add(pair.Key, pair.Value);
            return new SimulationPublishedSnapshot(point, view, options, version, generation, replica.LastAppliedSequence,
                sequencer?.LastSequencedSequence ?? replica.LastAppliedSequence, validatedThrough, stopped, requiresRecovery, recoveryFrozen,
                windows.Values.Select(Permission), backgroundFrozenBoundaries, permits.Keys, options.IsHost ? replica.CreateValidationPrefix(0, validatedThrough) : replica.CreateValidationPrefix(),
                lastFault, queue.Values, checkpoint, session.BackgroundCommitVersion,
                replica.ReadObservedStateRevisions(checkpointStateRevision), openAuthorities);
        }

        private bool TryCreateBackgroundProposal(GameCommand command, out OperationCommandProposalDto proposal, out ValidationResult validation)
        {
            proposal = null; validation = new ValidationResult(false, CommandErrorCode.WrongPhase, "当前固定版本或窗口尚未就绪。");
            if (background.IsStopping || backgroundRecoveryRequested || backgroundPendingLocal != null || !TryReadPublishedSnapshot(out var snapshot) ||
                snapshot.IsStopped || snapshot.IsRecoveringConfirmedPrefix || command == null) return false;
            if (command.PlayerId != options.LocalPlayerId) { validation = new ValidationResult(false, CommandErrorCode.InvalidPlayer, "只能提交本人命令。"); return false; }
            if (command.Parameters != null && command.Parameters.TryGetValue("expectedRevision", out var expected) &&
                (!int.TryParse(expected, out var revision) || revision != snapshot.StateRevision))
            { validation = new ValidationResult(false, CommandErrorCode.StateRevisionMismatch, "选择基于旧版本，请保留原选择并刷新。"); return false; }
            if (!snapshot.TryResolveCommand(command, out var context, out validation)) return false;
            if (!snapshot.TryReadWindow(context.OperationWindowId, out var window) || !window.IsOpen || !window.IsPermitted ||
                window.Window.WindowGeneration != context.WindowGeneration || !window.AvailablePlayerIds.Contains(command.PlayerId)) return false;
            try
            {
                if (command.Parameters.ContainsKey(OperationCommandProtocol.RequestContractVersionParameter) || command.Parameters.ContainsKey(OperationCommandProtocol.RequestContractDigestParameter))
                { validation = ValidationResult.Failure(CommandErrorCode.InvalidTarget, "请求合同由首次正式提案构造，不能注入或重签。"); return false; }
                var ownedCommand = GameCommandDto.FromCommand(command);
                if (requestContractVersion == OperationCommandProtocol.RequestContractVersion && command.Kind == GameCommandKind.AnswerInteraction)
                {
                    if (snapshot.TryReadRequestAuthority(command, out var authority, out var authorityValidation))
                    {
                        ownedCommand.Parameters.Add(new GameCommandParameterDto { Key = OperationCommandProtocol.RequestContractVersionParameter, Value = requestContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                        ownedCommand.Parameters.Add(new GameCommandParameterDto { Key = OperationCommandProtocol.RequestContractDigestParameter, Value = authority.Digest });
                    }
                    else if (authorityValidation.Reason != "request_contract_profile_not_supported")
                    { validation = authorityValidation; return false; }
                }
                proposal = OperationCommandCanonicalizer.SnapshotProposal(new OperationCommandProposalDto { GameId = options.GameId,
                    SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion,
                    ClientSubmissionSequence = backgroundNextSubmission, ObservedGlobalSequence = snapshot.LastAppliedSequence,
                    ObservedStateRevision = snapshot.StateRevision, OperationWindowId = context.OperationWindowId,
                    WindowGeneration = context.WindowGeneration, Command = ownedCommand });
                OperationCommandCanonicalizer.CanonicalProposalDigest(proposal);
                backgroundPendingLocal = OperationCommandCanonicalizer.SnapshotProposal(proposal); backgroundPendingValidationLocal = false; validation = ValidationResult.Success; return true;
            }
            catch (ArgumentException exception) { validation = new ValidationResult(false, CommandErrorCode.UnknownCommand, exception.Message); proposal = null; return false; }
        }

        private SimulationProposalResultDto AdmitBackgroundProposal(int sourcePlayer, OperationCommandProposalDto proposal)
        {
            var result = new SimulationProposalResultDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch,
                MembershipVersion = options.MembershipVersion, Status = OperationAdmissionStatus.Rejected, ErrorCode = CommandErrorCode.WrongPhase,
                Reason = "当前模拟不受理提案。", HostSequencedThrough = LastSequencedSequence,
                ExpectedClientSubmissionSequence = sequencer?.ReadNextSubmissionSequence(sourcePlayer) ?? 1 };
            try { result.Proposal = OperationCommandCanonicalizer.SnapshotProposal(proposal); OperationCommandCanonicalizer.CanonicalProposalDigest(result.Proposal); }
            catch (ArgumentException exception) { result.ErrorCode = CommandErrorCode.UnknownCommand; result.Reason = exception.Message; return result; }
            if (background.IsStopping || backgroundRecoveryRequested || sequencer == null || backgroundAdmitting) return result;
            var owned = result.Proposal; var snapshot = background.Latest;
            if (!Identity(owned.GameId, owned.SimulationEpoch, owned.MembershipVersion) || !options.FrozenPlayerIds.Contains(sourcePlayer) || owned.Command.PlayerId != sourcePlayer)
            { result.ErrorCode = CommandErrorCode.InvalidPlayer; return result; }
            if (sequencer.TryReadAdmittedProposal(sourcePlayer, owned, out var old)) return BackgroundAdmissionResult(result, old);
            var pending = backgroundAdmissions.Find(p => p.PlayerId == sourcePlayer);
            if (pending != null)
            {
                if (pending.Proposal.ClientSubmissionSequence != owned.ClientSubmissionSequence)
                { result.Status = OperationAdmissionStatus.MissingSubmission; result.Reason = "本人原提案尚未完成准入。"; return result; }
                if (pending.Digest != OperationCommandCanonicalizer.CanonicalProposalDigest(owned))
                { BackgroundMainFault("submission_conflict", "同一等待提案号的原载荷冲突。"); result.Reason = "同号原提案载荷冲突。"; return result; }
                result.Status = OperationAdmissionStatus.PendingValidation; result.ErrorCode = CommandErrorCode.None; result.Reason = "原提案仍在等待准确规则事实。"; return result;
            }
            if (owned.ClientSubmissionSequence != sequencer.ReadNextSubmissionSequence(sourcePlayer))
            { result.Status = owned.ClientSubmissionSequence > sequencer.ReadNextSubmissionSequence(sourcePlayer) ? OperationAdmissionStatus.MissingSubmission : OperationAdmissionStatus.Rejected; result.Reason = "本人提案号与原连续前缀不一致。"; return result; }
            if (!InteractionRequestValidity.TryReadMarker(owned.Command.ToCommand(), out bool contract, out _, out var markerValidation) ||
                contract && requestContractVersion != OperationCommandProtocol.RequestContractVersion)
            { result.ErrorCode = CommandErrorCode.InvalidTarget; result.Reason = "当前会话未协商该请求合同语义。"; return result; }
            long tail = sequencer.LastSequencedSequence;
            if (owned.ObservedGlobalSequence > tail || owned.ObservedGlobalSequence < options.CheckpointGlobalSequence)
            { result.ErrorCode = CommandErrorCode.StateRevisionMismatch; result.RequiresRefresh = true; result.Reason = "原观察前缀不是当前代次的已发表前缀。"; return result; }
            if (owned.ObservedGlobalSequence != tail)
            {
                if (!contract)
                { result.ErrorCode = CommandErrorCode.StateRevisionMismatch; result.RequiresRefresh = true; result.Reason = "旧语义提案需基于当前准确前缀。"; return result; }
                if (snapshot == null || snapshot.LastAppliedSequence < tail)
                    return QueuePendingAdmission(result, sourcePlayer, owned);
                if (!snapshot.TryValidateRequestProposal(owned, out var currentValidation))
                { result.ErrorCode = currentValidation.ErrorCode; result.RequiresRefresh = true; result.Reason = currentValidation.Reason; return result; }
            }
            else if (snapshot != null && snapshot.LastAppliedSequence == owned.ObservedGlobalSequence && snapshot.StateRevision != owned.ObservedStateRevision)
            { result.ErrorCode = CommandErrorCode.StateRevisionMismatch; result.RequiresRefresh = true; result.Reason = "同准确观察前缀的原版本不匹配。"; return result; }
            if (contract && snapshot != null && snapshot.LastAppliedSequence == tail && owned.ObservedGlobalSequence == tail &&
                !snapshot.TryValidateRequestProposal(owned, out var exactValidation))
            { result.ErrorCode = exactValidation.ErrorCode; result.RequiresRefresh = true; result.Reason = exactValidation.Reason; return result; }
            if (snapshot == null || snapshot.IsStopped || snapshot.IsRecoveringConfirmedPrefix || !snapshot.TryReadWindow(owned.OperationWindowId, out var window) ||
                !window.IsOpen || !window.IsPermitted || window.Window.WindowGeneration != owned.WindowGeneration || !window.AvailablePlayerIds.Contains(sourcePlayer)) return result;
            long applied = snapshot.LastAppliedSequence;
            if (!background.HasCapacity || backgroundKnown.Count(p => p.Key > applied) >= options.MaximumQueuedCommands)
            {
                if (contract) return QueuePendingAdmission(result, sourcePlayer, owned);
                result.Status = OperationAdmissionStatus.Backpressure; result.Reason = "待执行队列已满，尚未分配全局号。"; return result;
            }
            backgroundAdmitting = true;
            try
            {
                var admission = sequencer.AdmitProposal(sourcePlayer, owned); BackgroundAdmissionResult(result, admission);
                if (admission.Status == OperationAdmissionStatus.Accepted)
                {
                    ReceiveBackgroundOrdered(admission.OrderedCommand);
                    Deliver(OrderedCommandProduced, OperationCommandCanonicalizer.SnapshotOrdered(admission.OrderedCommand));
                }
            }
            finally { backgroundAdmitting = false; }
            return result;
        }
        private SimulationProposalResultDto QueuePendingAdmission(SimulationProposalResultDto result, int player, OperationCommandProposalDto proposal)
        {
            if (backgroundAdmissions.Count >= options.MaximumQueuedCommands)
            { result.Status = OperationAdmissionStatus.Backpressure; result.Reason = "原提案待准入队列已满，尚未分配序号。"; return result; }
            backgroundAdmissions.Add(new PendingAdmission { PlayerId = player, Proposal = OperationCommandCanonicalizer.SnapshotProposal(proposal), Digest = OperationCommandCanonicalizer.CanonicalProposalDigest(proposal) });
            result.Status = OperationAdmissionStatus.PendingValidation; result.ErrorCode = CommandErrorCode.None;
            result.Reason = "保留原确认，等待已编序尾段的准确规则事实。";
            if (player == options.LocalPlayerId) backgroundPendingValidationLocal = true;
            return result;
        }
        private SimulationProposalResultDto BackgroundAdmissionResult(SimulationProposalResultDto target, OperationCommandAdmissionResult source)
        {
            target.Status = source.Status; target.Reason = source.Reason; target.ExpectedClientSubmissionSequence = source.ExpectedClientSubmissionSequence;
            target.HostSequencedThrough = LastSequencedSequence; target.OrderedCommand = source.OrderedCommand;
            target.ErrorCode = source.Accepted ? CommandErrorCode.None : CommandErrorCode.WrongPhase;
            if (source.ErrorCode == "submission_conflict" || source.ErrorCode == "command_id_conflict") BackgroundMainFault(source.ErrorCode, source.Reason);
            return target;
        }
        private void ProcessPendingAdmissions()
        {
            if (backgroundAdmissions.Count == 0 || backgroundAdmitting) return;
            if (background.IsStopping || backgroundRecoveryRequested || background.Latest?.IsStopped == true)
            { RetirePendingAdmissions("原会话已停止或进入恢复，原提案未获准入。"); return; }
            foreach (var pending in backgroundAdmissions.ToArray())
            {
                if (!backgroundAdmissions.Contains(pending)) continue;
                var snapshot = background.Latest;
                if (snapshot == null || snapshot.LastAppliedSequence < sequencer.LastSequencedSequence) break;
                backgroundAdmissions.Remove(pending);
                var result = AdmitBackgroundProposal(pending.PlayerId, pending.Proposal);
                if (result.Status == OperationAdmissionStatus.PendingValidation) continue;
                PublishFinalAdmissionResult(result);
            }
        }
        private void PublishFinalAdmissionResult(SimulationProposalResultDto result)
        {
            if (background.IsStopping)
            {
                if (result.Proposal?.Command.PlayerId == options.LocalPlayerId &&
                    backgroundPendingLocal?.Command.CommandId == result.Proposal.Command.CommandId)
                { backgroundPendingLocal = null; backgroundPendingValidationLocal = false; DeliverRetirement(SimulationProposalRejected, result); }
                DeliverRetirement(SimulationProposalResultProduced, result); return;
            }
            if (result.Proposal?.Command.PlayerId == options.LocalPlayerId) ReceiveBackgroundProposalResult(result);
            Deliver(SimulationProposalResultProduced, GameStateCloneService.DeepClone(result));
        }
        private void RetirePendingAdmissions(string reason)
        {
            var pending = backgroundAdmissions.ToArray(); backgroundAdmissions.Clear();
            foreach (var entry in pending)
            {
                var result = new SimulationProposalResultDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch,
                    MembershipVersion = options.MembershipVersion, Proposal = OperationCommandCanonicalizer.SnapshotProposal(entry.Proposal),
                    Status = OperationAdmissionStatus.Rejected, ErrorCode = CommandErrorCode.WrongPhase, RequiresRefresh = true,
                    HostSequencedThrough = LastSequencedSequence, ExpectedClientSubmissionSequence = sequencer.ReadNextSubmissionSequence(entry.PlayerId), Reason = reason };
                if (entry.PlayerId == options.LocalPlayerId && backgroundPendingLocal?.Command.CommandId == entry.Proposal.Command.CommandId)
                { backgroundPendingLocal = null; backgroundPendingValidationLocal = false; DeliverRetirement(SimulationProposalRejected, result); }
                DeliverRetirement(SimulationProposalResultProduced, result);
            }
            if (backgroundPendingValidationLocal && backgroundPendingLocal != null)
            {
                var proposal = backgroundPendingLocal; backgroundPendingLocal = null; backgroundPendingValidationLocal = false;
                DeliverRetirement(SimulationProposalRejected, new SimulationProposalResultDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch,
                    MembershipVersion = options.MembershipVersion, Proposal = proposal, Status = OperationAdmissionStatus.Rejected,
                    ErrorCode = CommandErrorCode.WrongPhase, RequiresRefresh = true, ExpectedClientSubmissionSequence = backgroundNextSubmission, Reason = reason });
            }
        }
        private void DeliverRetirement(Action<SimulationProposalResultDto> handlers, SimulationProposalResultDto result)
        {
            foreach (Action<SimulationProposalResultDto> listener in handlers?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { listener(GameStateCloneService.DeepClone(result)); } catch (Exception exception) { background.AddConsumerError(exception); }
        }
        private OperationCommandReplicaResult ReceiveBackgroundOrdered(OrderedOperationCommandDto command)
        {
            if (background.IsStopping || backgroundRecoveryRequested) return Reply(OperationReplicaStatus.Stopped, "simulation_stopped");
            if (command == null || !Identity(command.GameId, command.SimulationEpoch, command.MembershipVersion)) return Reply(OperationReplicaStatus.Rejected, "ordered_identity_mismatch");
            try
            {
                var owned = OperationCommandCanonicalizer.SnapshotOrdered(command);
                if (OperationCommandCanonicalizer.CanonicalOrderedCommandDigest(owned) != owned.OrderedCommandDigest || !options.FrozenPlayerIds.Contains(owned.Command.PlayerId)) throw new ArgumentException("ordered_invalid");
                if (backgroundKnown.TryGetValue(owned.GlobalCommandSequence, out var previous))
                {
                    if (previous.OrderedCommandDigest != owned.OrderedCommandDigest) throw new ArgumentException("ordered_sequence_conflict");
                    return Reply(OperationReplicaStatus.Duplicate, "");
                }
                if (owned.GlobalCommandSequence <= options.CheckpointGlobalSequence) return Reply(OperationReplicaStatus.Rejected, "ordered_before_base");
                if (owned.ObservedGlobalSequence >= owned.GlobalCommandSequence) throw new ArgumentException("observed_prefix_invalid");
                bool localMatch = backgroundPendingLocal != null && owned.Command.PlayerId == options.LocalPlayerId && owned.Command.CommandId == backgroundPendingLocal.Command.CommandId;
                if (localMatch && owned.ProposalDigest != OperationCommandCanonicalizer.CanonicalProposalDigest(backgroundPendingLocal))
                    throw new ArgumentException("local_proposal_conflict");
                if (backgroundKnown.Count >= options.MaximumLogCommands || !background.TryEnqueue(() => ReceiveOrdered(owned)))
                { BackgroundMainFault("queue_capacity", "原有序命令缓存已满，未丢弃或重签原件。", owned); return Reply(OperationReplicaStatus.Backpressure, "queue_capacity"); }
                backgroundKnown.Add(owned.GlobalCommandSequence, owned);
                if (localMatch) { backgroundNextSubmission = owned.ClientSubmissionSequence + 1; backgroundPendingValidationLocal = false; }
                return Reply(OperationReplicaStatus.Buffered, "queued");
            }
            catch (ArgumentException exception) { BackgroundMainFault(exception.Message, "原有序载荷或身份冲突。", command); return Reply(OperationReplicaStatus.Stopped, exception.Message); }
        }
        private void ReceiveBackgroundProposalResult(SimulationProposalResultDto result)
        {
            if (background.IsStopping || result == null || result.ProtocolVersion != OperationCommandProtocol.Version || !Identity(result.GameId, result.SimulationEpoch, result.MembershipVersion) ||
                result.Proposal == null || backgroundPendingLocal == null) return;
            try
            {
                if (OperationCommandCanonicalizer.CanonicalProposalDigest(result.Proposal) != OperationCommandCanonicalizer.CanonicalProposalDigest(backgroundPendingLocal)) return;
                if (result.Status == OperationAdmissionStatus.PendingValidation) { backgroundPendingValidationLocal = true; return; }
                if (result.Status == OperationAdmissionStatus.Accepted || result.Status == OperationAdmissionStatus.Duplicate)
                { if (result.OrderedCommand != null) ReceiveBackgroundOrdered(result.OrderedCommand); return; }
                if (result.ExpectedClientSubmissionSequence > 0) backgroundNextSubmission = result.ExpectedClientSubmissionSequence;
                backgroundPendingLocal = null; backgroundPendingValidationLocal = false; Deliver(SimulationProposalRejected, GameStateCloneService.DeepClone(result));
                if (result.RequiresRefresh) RequestMissing(LastAppliedSequence + 1);
            }
            catch (ArgumentException exception) { BackgroundMainFault("proposal_result_invalid", exception.Message); }
        }
        private void PublishApplied(OrderedOperationCommandDto command, CommandResult result)
        {
            var notice = new SimulationCommandAppliedDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion,
                Command = CloneCommand(command.Command), GlobalCommandSequence = command.GlobalCommandSequence, OperationWindowId = command.OperationWindowId,
                WindowGeneration = command.WindowGeneration, WindowCommandSequence = command.WindowCommandSequence,
                RuleResult = background == null ? result : GameSession.CopyCommandResult(result) };
            Emit(() =>
            {
                if (background != null && backgroundPendingLocal?.Command.CommandId == notice.Command.CommandId)
                { backgroundPendingLocal = null; backgroundPendingValidationLocal = false; }
                Deliver(SimulationCommandApplied, notice);
            });
        }
    }
}
