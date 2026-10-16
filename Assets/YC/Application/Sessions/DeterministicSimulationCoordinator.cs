using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    /// <summary>单线程会话协调。编序、一次规则提交、边界汇合分别使用原有唯一组件。</summary>
    public sealed class DeterministicSimulationCoordinator
    {
        // 仅诊断累计；关闭时不读时钟，值类型作用域不分配堆对象。
        private readonly struct ProtocolExecutionSample : IDisposable
        {
            private readonly ExecutionTimingCounters counters;
            private readonly string phase;
            private readonly long started;

            internal ProtocolExecutionSample(ExecutionTimingCounters counters, string phase)
            {
                this.phase = phase;
                this.counters = counters.Enabled ? counters : null;
                started = this.counters == null ? 0 : Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                if (counters != null && counters.Enabled)
                    counters.AddSample(phase, Stopwatch.GetTimestamp() - started);
            }
        }

        private sealed class WindowRecord
        {
            internal OperationWindowDescriptorDto Descriptor;
            internal List<int> Available;
            internal bool Open, Permitted, ClosureCaptured;
            internal long PermissionFence;
        }
        private readonly GameSession session;
        private readonly DeterministicSimulationSessionOptions options;
        private readonly OrderedCommandSequencer sequencer;
        private readonly DeterministicCommandReplica replica;
        private readonly SortedDictionary<long, OrderedOperationCommandDto> queue = new SortedDictionary<long, OrderedOperationCommandDto>();
        private readonly Dictionary<string, WindowRecord> windows = new Dictionary<string, WindowRecord>(StringComparer.Ordinal);
        private readonly SortedDictionary<long, SimulationBoundaryDto> boundaries = new SortedDictionary<long, SimulationBoundaryDto>();
        private readonly SortedDictionary<long, SimulationBoundaryDto> announcements = new SortedDictionary<long, SimulationBoundaryDto>();
        private readonly Dictionary<long, Dictionary<string, SimulationBoundaryDto>> ready = new Dictionary<long, Dictionary<string, SimulationBoundaryDto>>();
        private readonly Dictionary<long, SimulationWindowPermitDto> permits = new Dictionary<long, SimulationWindowPermitDto>();
        private readonly SortedDictionary<long, CommandValidationPrefixDto> validations = new SortedDictionary<long, CommandValidationPrefixDto>();
        private OperationCommandProposalDto pendingLocal;
        private SimulationProtocolFaultDto lastFault;
        private long nextLocalSubmission = 1, validatedThrough, publishedValidation;
        private bool begun, stopped, pumping, requiresRecovery;
        private bool recoveryFrozen, deferNotifications;
        private readonly List<Action> deferredNotifications = new List<Action>();
        private GameSessionRecoveryPoint confirmedPoint;
        private List<SimulationWindowFactDto> confirmedWindows;

        public event Action<SimulationCommandAppliedDto> SimulationCommandApplied;
        public event Action<CommandValidationPrefixDto> CommandValidationPrefixAdvanced;
        public event Action<SimulationWindowPermissionDto> OperationWindowPermissionChanged;
        public event Action<SimulationProtocolFaultDto> SimulationProtocolFaulted;
        public event Action<SimulationProposalResultDto> SimulationProposalRejected;
        // 运输只消费这些原件事件；任何事件都不执行第二次业务提交。
        public event Action<OrderedOperationCommandDto> OrderedCommandProduced;
        public event Action<SimulationBoundaryDto> BoundaryProduced;
        public event Action<SimulationWindowReadyDto> WindowReadyProduced;
        public event Action<SimulationWindowPermitDto> WindowPermitProduced;
        public event Action<SimulationMissingRangeRequestDto> MissingRangeRequested;

        public long LastAppliedSequence => replica.LastAppliedSequence;
        public long LastSequencedSequence => sequencer?.LastSequencedSequence ?? LastAppliedSequence;
        public long ValidatedThroughSequence => validatedThrough;
        public bool IsStopped => stopped;
        public bool RequiresRecovery => requiresRecovery;
        public bool HasPendingLocalApplication => pendingLocal != null;
        public bool IsRecoveringConfirmedPrefix => recoveryFrozen;
        public DeterministicSimulationSessionOptions SessionOptions => options.Snapshot();

        public DeterministicSimulationCoordinator(GameSession session, DeterministicSimulationSessionOptions options)
            : this(session, options, null, null) { }

        private DeterministicSimulationCoordinator(GameSession session, DeterministicSimulationSessionOptions options,
            SimulationRecoveryBaselineDto baseline, GameState preparedState)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.options = (options ?? throw new ArgumentNullException(nameof(options))).Snapshot();
            var value = this.options;
            var identityState = preparedState ?? session.State;
            OperationCommandCanonicalizer.ValidateIdentity(value.GameId, value.SimulationEpoch, value.MembershipVersion);
            if (identityState.GameId != value.GameId || identityState.ContentPackHash != value.ContentPackHash ||
                value.FrozenPlayerIds == null || value.FrozenPlayerIds.Count == 0 || value.FrozenPlayerIds.Count > 256 ||
                value.FrozenPlayerIds.Any(id => id <= 0) || value.FrozenPlayerIds.Distinct().Count() != value.FrozenPlayerIds.Count ||
                !value.FrozenPlayerIds.OrderBy(id => id).SequenceEqual(identityState.Players.Select(player => player.PlayerId).OrderBy(id => id)) ||
                !value.FrozenPlayerIds.Contains(value.LocalPlayerId) || value.RequiredReplicaIds == null ||
                !value.RequiredReplicaIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(value.FrozenPlayerIds.Select(DeterministicSimulationSessionOptions.ReplicaIdForPlayer).OrderBy(id => id, StringComparer.Ordinal)) ||
                value.MaximumQueuedCommands <= 0 || value.MaximumLogCommands <= 0 || value.MaximumLogCommands > 4096 || value.MaximumRegisteredWindows <= 0 ||
                value.MaximumRegisteredWindows > 4096 || value.MaximumBoundaryFacts <= 0 || value.MaximumBoundaryFacts > 4096 || value.PumpCommandsPerFrame <= 0)
                throw new ArgumentException("simulation_options_invalid");
            replica = baseline != null ? DeterministicCommandReplica.FromConfirmedRecovery(session, value, baseline, identityState) : new DeterministicCommandReplica(session, value.SimulationEpoch, value.MembershipVersion, value.MaximumQueuedCommands, value.MaximumLogCommands,
                value.CheckpointGlobalSequence, value.CheckpointPrefixDigest, null, value.MaximumRegisteredWindows);
            if (value.IsHost) sequencer = baseline != null ? OrderedCommandSequencer.FromConfirmedRecovery(value, baseline) : new OrderedCommandSequencer(value.GameId, value.SimulationEpoch, value.MembershipVersion, value.MaximumLogCommands,
                value.CheckpointGlobalSequence, value.CheckpointPrefixDigest, value.MaximumRegisteredWindows);
            publishedValidation = validatedThrough = value.CheckpointGlobalSequence;
            if (baseline != null)
            {
                nextLocalSubmission = baseline.NextSubmissionSequences[value.LocalPlayerId];
                foreach (var fact in baseline.Windows)
                    windows.Add(fact.Window.OperationWindowId, new WindowRecord { Descriptor = OperationCommandCanonicalizer.SnapshotRecoveryWindow(fact).Window,
                        Available = new List<int>(fact.AvailablePlayerIds), Open = !fact.IsClosed, ClosureCaptured = fact.IsClosed, PermissionFence = validatedThrough });
                begun = true; recoveryFrozen = true;
            }
        }

        public static DeterministicSimulationCoordinator FromConfirmedRecovery(GameSession session, DeterministicSimulationSessionOptions targetOptions,
            SimulationRecoveryBaselineDto baseline, GameState preparedState)
            => new DeterministicSimulationCoordinator(session, targetOptions, OperationCommandCanonicalizer.SnapshotRecoveryBaseline(baseline), preparedState);

        public void Begin()
        {
            if (begun || stopped) return;
            begun = true;
            try
            {
                deferNotifications = true;
                if (options.IsHost) confirmedPoint = session.EnableConfirmedRecoveryCapture();
                RefreshWindows(true); CaptureBoundary(); CaptureConfirmedFacts(null); FlushNotifications();
            }
            catch (Exception exception) { Fault("simulation_begin_failed", exception.Message); }
            finally { deferNotifications = false; deferredNotifications.Clear(); }
        }

        public bool TryCreateLocalProposal(GameCommand command, out OperationCommandProposalDto proposal, out ValidationResult validation)
        {
            proposal = null;
            validation = new ValidationResult(false, CommandErrorCode.WrongPhase, "当前模拟未就绪或已停止。");
            if (!begun || stopped || recoveryFrozen || command == null) return false;
            if (pendingLocal != null) { validation = new ValidationResult(false, CommandErrorCode.WrongPhase, "上一条本地命令尚未执行。"); return false; }
            if (command.PlayerId != options.LocalPlayerId) { validation = new ValidationResult(false, CommandErrorCode.InvalidPlayer, "只能提交本人命令。"); return false; }
            int revision = session.State.EffectRuntime?.StateRevision ?? 0;
            if (command.Parameters != null && command.Parameters.TryGetValue("expectedRevision", out var expected) &&
                (!int.TryParse(expected, out var expectedRevision) || expectedRevision != revision))
            { validation = new ValidationResult(false, CommandErrorCode.StateRevisionMismatch, "选择基于旧版本，请保留原选择并刷新。"); return false; }
            if (!OperationWindowService.TryResolveCommandContext(session.State, command, out var context, out validation)) return false;
            if (!windows.TryGetValue(context.OperationWindowId, out var window) || !window.Open || !window.Permitted || window.Descriptor.WindowGeneration != context.WindowGeneration || !window.Available.Contains(command.PlayerId))
            { validation = new ValidationResult(false, CommandErrorCode.WrongPhase, "当前操作窗口尚未汇合就绪，或该玩家已无当前操作资格。"); return false; }
            try
            {
                proposal = OperationCommandCanonicalizer.SnapshotProposal(new OperationCommandProposalDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch,
                    MembershipVersion = options.MembershipVersion, ClientSubmissionSequence = nextLocalSubmission, ObservedGlobalSequence = LastAppliedSequence,
                    ObservedStateRevision = revision, OperationWindowId = context.OperationWindowId, WindowGeneration = context.WindowGeneration, Command = GameCommandDto.FromCommand(command) });
                OperationCommandCanonicalizer.CanonicalProposalDigest(proposal);
                pendingLocal = OperationCommandCanonicalizer.SnapshotProposal(proposal); validation = ValidationResult.Success; return true;
            }
            catch (ArgumentException exception) { proposal = null; validation = new ValidationResult(false, CommandErrorCode.UnknownCommand, exception.Message); return false; }
        }

        public SimulationProposalResultDto AdmitProposal(int verifiedSourcePlayerId, OperationCommandProposalDto proposal)
        {
            var result = new SimulationProposalResultDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion,
                Status = OperationAdmissionStatus.Rejected, ErrorCode = CommandErrorCode.WrongPhase, Reason = "当前模拟不受理提案。",
                HostSequencedThrough = LastSequencedSequence, ExpectedClientSubmissionSequence = sequencer?.ReadNextSubmissionSequence(verifiedSourcePlayerId) ?? 1 };
            if (!begun || stopped || recoveryFrozen || sequencer == null) return result;
            try { result.Proposal = OperationCommandCanonicalizer.SnapshotProposal(proposal); OperationCommandCanonicalizer.CanonicalProposalDigest(result.Proposal); }
            catch (ArgumentException exception) { result.ErrorCode = CommandErrorCode.UnknownCommand; result.Reason = exception.Message; return result; }
            var owned = result.Proposal;
            if (!Identity(owned.GameId, owned.SimulationEpoch, owned.MembershipVersion) || !options.FrozenPlayerIds.Contains(verifiedSourcePlayerId) || owned.Command.PlayerId != verifiedSourcePlayerId)
            { result.ErrorCode = CommandErrorCode.InvalidPlayer; result.Reason = "提案身份或实际来源不匹配。"; return result; }
            if (sequencer.TryReadAdmittedProposal(verifiedSourcePlayerId, owned, out var previous)) return AdmissionResult(result, previous);
            if (owned.ObservedGlobalSequence != sequencer.LastSequencedSequence || LastAppliedSequence == sequencer.LastSequencedSequence && owned.ObservedStateRevision != (session.State.EffectRuntime?.StateRevision ?? 0))
            { result.ErrorCode = CommandErrorCode.StateRevisionMismatch; result.RequiresRefresh = true; result.Reason = "已编序前缀已越过提案观察位置，请补原日志并刷新选择。"; return result; }
            if (!windows.TryGetValue(owned.OperationWindowId, out var window) || !window.Open || !window.Permitted || window.Descriptor.WindowGeneration != owned.WindowGeneration || !window.Available.Contains(verifiedSourcePlayerId))
            { result.Reason = "当前窗口未许可，或该玩家无当前操作资格。"; return result; }
            if (queue.Count >= options.MaximumQueuedCommands) { result.Status = OperationAdmissionStatus.Backpressure; result.Reason = "待执行队列已满，尚未分配全局号。"; return result; }
            var admission = sequencer.AdmitProposal(verifiedSourcePlayerId, owned);
            AdmissionResult(result, admission);
            if (admission.Status == OperationAdmissionStatus.Accepted)
            {
                // 先交运输广播，随后入本机队列；业务只有 Pump 执行。
                OrderedCommandProduced?.Invoke(OperationCommandCanonicalizer.SnapshotOrdered(admission.OrderedCommand));
                ReceiveOrdered(admission.OrderedCommand);
            }
            return result;
        }

        private SimulationProposalResultDto AdmissionResult(SimulationProposalResultDto result, OperationCommandAdmissionResult admission)
        {
            result.Status = admission.Status; result.Reason = admission.Reason; result.ExpectedClientSubmissionSequence = admission.ExpectedClientSubmissionSequence;
            result.HostSequencedThrough = LastSequencedSequence; result.OrderedCommand = admission.OrderedCommand;
            result.ErrorCode = admission.Accepted ? CommandErrorCode.None : CommandErrorCode.WrongPhase;
            if (admission.ErrorCode == "submission_conflict" || admission.ErrorCode == "command_id_conflict") Fault(admission.ErrorCode, admission.Reason);
            return result;
        }

        public void ReceiveProposalResult(SimulationProposalResultDto result)
        {
            if (stopped || result == null || result.ProtocolVersion != OperationCommandProtocol.Version || !Identity(result.GameId, result.SimulationEpoch, result.MembershipVersion) || pendingLocal == null || result.Proposal == null) return;
            try
            {
                if (OperationCommandCanonicalizer.CanonicalProposalDigest(pendingLocal) != OperationCommandCanonicalizer.CanonicalProposalDigest(result.Proposal)) return;
                if (result.Status == OperationAdmissionStatus.Accepted || result.Status == OperationAdmissionStatus.Duplicate)
                { if (result.OrderedCommand != null) ReceiveOrdered(result.OrderedCommand); return; }
                if (result.ExpectedClientSubmissionSequence > 0) nextLocalSubmission = result.ExpectedClientSubmissionSequence;
                pendingLocal = null; SimulationProposalRejected?.Invoke(result);
                if (result.RequiresRefresh) RequestMissing(LastAppliedSequence + 1);
            }
            catch (ArgumentException exception) { Fault("proposal_result_invalid", exception.Message); }
        }

        public OperationCommandReplicaResult ReceiveOrdered(OrderedOperationCommandDto command)
        {
            if (stopped || recoveryFrozen) return Reply(OperationReplicaStatus.Stopped, "simulation_stopped");
            if (!begun || command == null || !Identity(command.GameId, command.SimulationEpoch, command.MembershipVersion)) return Reply(OperationReplicaStatus.Rejected, "ordered_identity_mismatch");
            try
            {
                var owned = OperationCommandCanonicalizer.SnapshotOrdered(command);
                if (OperationCommandCanonicalizer.CanonicalOrderedCommandDigest(owned) != owned.OrderedCommandDigest || !options.FrozenPlayerIds.Contains(owned.Command.PlayerId))
                    return FaultReply("ordered_invalid", "编序载荷摘要或玩家无效。", owned);
                var applied = replica.GetAppliedCommand(owned.GlobalCommandSequence);
                if (applied != null) return applied.OrderedCommandDigest == owned.OrderedCommandDigest ? Reply(OperationReplicaStatus.Duplicate, "") : FaultReply("ordered_sequence_conflict", "已执行同号命令冲突。", owned);
                if (owned.GlobalCommandSequence <= options.CheckpointGlobalSequence) return Reply(OperationReplicaStatus.Rejected, "ordered_before_base");
                if (queue.TryGetValue(owned.GlobalCommandSequence, out var queued))
                    return queued.OrderedCommandDigest == owned.OrderedCommandDigest ? Reply(OperationReplicaStatus.Duplicate, "") : FaultReply("ordered_sequence_conflict", "待执行同号命令冲突。", owned);
                if (queue.Count >= options.MaximumQueuedCommands) return FaultReply("queue_capacity", "原命令队列容量已满，保留原件等待恢复。", owned);
                if (owned.ObservedGlobalSequence >= owned.GlobalCommandSequence) return FaultReply("observed_prefix_invalid", "编序观察前缀无效。", owned);
                queue.Add(owned.GlobalCommandSequence, owned);
                if (pendingLocal != null && owned.Command.PlayerId == options.LocalPlayerId && owned.Command.CommandId == pendingLocal.Command.CommandId)
                {
                    if (owned.ProposalDigest != OperationCommandCanonicalizer.CanonicalProposalDigest(pendingLocal)) return FaultReply("local_proposal_conflict", "本机原提案与编序结果不一致。", owned);
                    nextLocalSubmission = owned.ClientSubmissionSequence + 1;
                }
                if (owned.GlobalCommandSequence > LastAppliedSequence + 1) RequestMissing(LastAppliedSequence + 1);
                return Reply(OperationReplicaStatus.Buffered, "queued");
            }
            catch (ArgumentException exception) { return FaultReply("ordered_invalid", exception.Message, null); }
        }

        public int Pump(int maximumCommands)
        {
            if (maximumCommands <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCommands));
            if (stopped || !begun || pumping || recoveryFrozen && !options.IsHost || queue.Count == 0) return 0;
            if (session.State.GameId != options.GameId || session.State.ContentPackHash != options.ContentPackHash)
            { Fault("simulation_identity_changed", "运行中会话局或内容身份发生变化。"); return 0; }
            using var pumpSample = new ProtocolExecutionSample(session.ExecutionTimings, "pump.total");
            pumping = true; int count = 0;
            try
            {
                while (!stopped && count < maximumCommands && queue.TryGetValue(LastAppliedSequence + 1, out var command))
                {
                    OperationCommandReplicaResult result;
                    using (new ProtocolExecutionSample(session.ExecutionTimings, "pump.apply"))
                        result = replica.ApplyOrdered(command, 1);
                    if (result.Status != OperationReplicaStatus.Applied)
                    {
                        using (new ProtocolExecutionSample(session.ExecutionTimings, "pump.notifications"))
                        {
                            if (result.RuleResult != null) SimulationCommandApplied?.Invoke(new SimulationCommandAppliedDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion, Command = CloneCommand(command.Command),
                                GlobalCommandSequence = command.GlobalCommandSequence, OperationWindowId = command.OperationWindowId, WindowGeneration = command.WindowGeneration,
                                WindowCommandSequence = command.WindowCommandSequence, RuleResult = result.RuleResult });
                            Fault(result.ErrorCode, result.Reason, command, result.RuleResult);
                        }
                        break;
                    }
                    count++;
                    // 保存边界在消费者回调之前，防止回调把后续状态当旧前缀。
                    deferNotifications = true;
                    using (new ProtocolExecutionSample(session.ExecutionTimings, "pump.windows"))
                    {
                        bool boundary = RefreshWindows(false);
                        if (boundary || announcements.ContainsKey(LastAppliedSequence)) CaptureBoundary();
                    }
                    if (stopped) throw new InvalidOperationException("confirmed_protocol_facts_failed");
                    using (new ProtocolExecutionSample(session.ExecutionTimings, "pump.facts"))
                        CaptureConfirmedFacts(command.Command.CommandId);
                    if (options.IsHost) { validatedThrough = LastAppliedSequence; if (boundaries.ContainsKey(validatedThrough)) TryPermit(validatedThrough); }
                    queue.Remove(command.GlobalCommandSequence);
                    if (pendingLocal?.Command.CommandId == command.Command.CommandId) pendingLocal = null;
                    using (new ProtocolExecutionSample(session.ExecutionTimings, "pump.notifications"))
                    {
                        FlushNotifications();
                        SimulationCommandApplied?.Invoke(new SimulationCommandAppliedDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion, Command = CloneCommand(command.Command),
                            GlobalCommandSequence = command.GlobalCommandSequence, OperationWindowId = command.OperationWindowId,
                            WindowGeneration = command.WindowGeneration, WindowCommandSequence = command.WindowCommandSequence, RuleResult = result.RuleResult });
                    }
                    using (new ProtocolExecutionSample(session.ExecutionTimings, "pump.validation"))
                        ConsumeValidation();
                }
                PublishHostValidation();
                return count;
            }
            catch (Exception exception) { Fault("simulation_pump_exception", exception.Message); return count; }
            finally { pumping = false; deferNotifications = false; deferredNotifications.Clear(); }
        }

        private bool RefreshWindows(bool initial)
        {
            var actual = OperationWindowService.ReadOpenWindows(session.State);
            var openIds = new HashSet<string>(actual.Select(window => window.OperationWindowId), StringComparer.Ordinal);
            bool boundary = initial;
            foreach (var record in windows.Values.Where(record => record.Open && !openIds.Contains(record.Descriptor.OperationWindowId)))
            { record.Open = false; record.Permitted = false; record.Available.Clear(); boundary = true; PublishPermission(record); }
            foreach (var window in actual)
            {
                string contract = OperationCommandCanonicalizer.CanonicalWindowContractDigest(window);
                if (!windows.TryGetValue(window.OperationWindowId, out var record))
                {
                    if (windows.Count >= options.MaximumRegisteredWindows) throw new InvalidOperationException("window_registration_capacity");
                    var descriptor = OperationWindowProjection.ToDescriptor(session.State, window, options.SimulationEpoch, options.MembershipVersion, LastAppliedSequence, 0,
                        options.RequiredReplicaIds, OperationCommandCanonicalizer.EmptyPrefixDigest, contract);
                    replica.RegisterWindow(descriptor); sequencer?.RegisterWindow(descriptor);
                    record = new WindowRecord { Descriptor = descriptor, Open = true, Available = window.AvailablePlayerIds.OrderBy(id => id).ToList(), PermissionFence = LastAppliedSequence };
                    windows.Add(window.OperationWindowId, record); boundary = true; PublishPermission(record);
                }
                else
                {
                    if (!record.Open || window.WindowGeneration != record.Descriptor.WindowGeneration || record.Descriptor.WindowContractDigest != contract)
                        throw new InvalidOperationException("window_contract_changed");
                    var available = window.AvailablePlayerIds.OrderBy(id => id).ToList();
                    if (available.Except(record.Available).Any()) { record.Permitted = false; record.PermissionFence = LastAppliedSequence; boundary = true; }
                    if (!record.Available.SequenceEqual(available)) { record.Available = available; PublishPermission(record); }
                }
            }
            return boundary;
        }

        private void CaptureBoundary()
        {
            long fence = LastAppliedSequence;
            if (boundaries.ContainsKey(fence)) return;
            if (boundaries.Count >= options.MaximumBoundaryFacts) throw new InvalidOperationException("boundary_capacity");
            replica.TryReadAppliedPrefix(fence, out var prefix);
            var boundary = new SimulationBoundaryDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion,
                GlobalFenceSequence = fence, CommandPrefixDigest = prefix, RuleStateDigestVersion = RuleStateDigestService.DigestVersion, RuleStateDigest = ComputeBoundaryRuleDigest() };
            foreach (var record in windows.Values.Where(record => record.Open || !record.ClosureCaptured))
            {
                var progress = replica.ReadWindowProgress(record.Descriptor.OperationWindowId);
                boundary.Windows.Add(new SimulationWindowFactDto { Window = record.Descriptor, AvailablePlayerIds = new List<int>(record.Available), IsClosed = !record.Open,
                    IsNewWindow = record.Open && record.Descriptor.OpenedAtGlobalSequence == fence,
                    RequiresPermit = record.Open && !record.Permitted && record.PermissionFence == fence,
                    FinalCommandSequence = progress.LastWindowCommandSequence, CommandCount = progress.CommandCount, WindowPrefixDigest = progress.PrefixDigest });
                if (!record.Open) record.ClosureCaptured = true;
            }
            boundary.FactDigest = OperationCommandCanonicalizer.CanonicalBoundaryDigest(boundary);
            boundary = OperationCommandCanonicalizer.SnapshotBoundary(boundary); boundaries.Add(fence, boundary);
            if (announcements.TryGetValue(fence, out var announced) && announced.FactDigest != boundary.FactDigest) { Fault("boundary_mismatch", "同前缀窗口或规则摘要不一致。"); return; }
            if (options.IsHost) { var notice = ReadBoundary(fence); Emit(() => BoundaryProduced?.Invoke(notice)); }
            var localReady = new SimulationWindowReadyDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion,
                PlayerId = options.LocalPlayerId, ReplicaId = DeterministicSimulationSessionOptions.ReplicaIdForPlayer(options.LocalPlayerId), Boundary = ReadBoundary(fence) };
            if (options.IsHost) { ReceiveWindowReady(options.LocalPlayerId, localReady); TryPermit(fence); }
            else Emit(() => WindowReadyProduced?.Invoke(localReady));
            if (permits.TryGetValue(fence, out var permit)) ApplyPermit(permit);
        }

        public bool ReceiveBoundary(SimulationBoundaryDto boundary)
        {
            if (!begun || stopped || options.IsHost || boundary == null || !Identity(boundary.GameId, boundary.SimulationEpoch, boundary.MembershipVersion)) return false;
            try
            {
                var owned = OperationCommandCanonicalizer.SnapshotBoundary(boundary); long fence = owned.GlobalFenceSequence;
                if (announcements.TryGetValue(fence, out var previous) && previous.FactDigest != owned.FactDigest) { Fault("boundary_conflict", "同前缀边界广播冲突。"); return false; }
                if (!announcements.ContainsKey(fence)) { if (announcements.Count >= options.MaximumBoundaryFacts) throw new InvalidOperationException("boundary_capacity"); announcements.Add(fence, owned); }
                if (fence > LastAppliedSequence) { RequestMissing(LastAppliedSequence + 1); return true; }
                if (!boundaries.ContainsKey(fence))
                { if (fence != LastAppliedSequence) { Fault("boundary_history_missing", "已越过边界但无原前缀快照，禁止改用当前状态。"); return false; } CaptureBoundary(); }
                if (boundaries[fence].FactDigest != owned.FactDigest) { Fault("boundary_mismatch", "同前缀窗口或规则摘要不一致。"); return false; }
                WindowReadyProduced?.Invoke(new SimulationWindowReadyDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion,
                    PlayerId = options.LocalPlayerId, ReplicaId = DeterministicSimulationSessionOptions.ReplicaIdForPlayer(options.LocalPlayerId), Boundary = ReadBoundary(fence) }); return true;
            }
            catch (Exception exception) { Fault("boundary_invalid", exception.Message); return false; }
        }

        public bool ReceiveWindowReady(int verifiedSourcePlayerId, SimulationWindowReadyDto value)
        {
            if (!begun || stopped || !options.IsHost || value == null || value.ProtocolVersion != OperationCommandProtocol.Version || !Identity(value.GameId, value.SimulationEpoch, value.MembershipVersion) ||
                verifiedSourcePlayerId != value.PlayerId || !options.FrozenPlayerIds.Contains(value.PlayerId) || value.ReplicaId != DeterministicSimulationSessionOptions.ReplicaIdForPlayer(value.PlayerId)) return false;
            try
            {
                var fact = OperationCommandCanonicalizer.SnapshotBoundary(value.Boundary);
                if (!Identity(fact.GameId, fact.SimulationEpoch, fact.MembershipVersion) || fact.GlobalFenceSequence > LastSequencedSequence) return false;
                if (!ready.TryGetValue(fact.GlobalFenceSequence, out var group))
                { if (ready.Count >= options.MaximumBoundaryFacts) throw new InvalidOperationException("ready_capacity"); group = new Dictionary<string, SimulationBoundaryDto>(StringComparer.Ordinal); ready.Add(fact.GlobalFenceSequence, group); }
                if (group.TryGetValue(value.ReplicaId, out var previous) && previous.FactDigest != fact.FactDigest) { Fault("ready_conflict", "同副本同前缀就绪载荷冲突。"); return false; }
                group[value.ReplicaId] = fact; TryPermit(fact.GlobalFenceSequence); return !stopped;
            }
            catch (Exception exception) { Fault("ready_invalid", exception.Message); return false; }
        }

        private void TryPermit(long fence)
        {
            if (!options.IsHost || stopped || recoveryFrozen || permits.ContainsKey(fence) || LastAppliedSequence < fence || !boundaries.TryGetValue(fence, out var boundary) || !ready.TryGetValue(fence, out var group)) return;
            foreach (var pair in group) if (pair.Value.FactDigest != boundary.FactDigest) { Fault("ready_rule_mismatch", "规定副本的准确前缀规则摘要或窗口尾号不一致。"); return; }
            if (!options.RequiredReplicaIds.All(group.ContainsKey)) return;
            if (validatedThrough < fence || !replica.TryReadAppliedPrefix(fence, out var hostPrefix) || hostPrefix != boundary.CommandPrefixDigest) return;
            foreach (var fact in boundary.Windows.Where(fact => fact.IsClosed))
            {
                var accepted = sequencer.ReadWindowProgress(fact.Window.OperationWindowId);
                if (accepted.LastWindowCommandSequence != fact.FinalCommandSequence || accepted.CommandCount != fact.CommandCount || accepted.PrefixDigest != fact.WindowPrefixDigest)
                { Fault("closed_window_tail_conflict", "窗口关闭前缀后仍存在已接受尾段，不能放行后继。"); return; }
            }
            var permit = new SimulationWindowPermitDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion,
                Boundary = ReadBoundary(fence), HostValidatedThroughSequence = fence, HostValidationPrefixDigest = hostPrefix,
                ReadyReplicaIds = options.RequiredReplicaIds.OrderBy(id => id, StringComparer.Ordinal).ToList() };
            permits.Add(fence, permit); ApplyPermit(permit); var notice = ClonePermit(permit); Emit(() => WindowPermitProduced?.Invoke(notice));
        }

        public bool ReceiveWindowPermit(SimulationWindowPermitDto permit)
        {
            if (!begun || stopped || options.IsHost || permit == null || permit.ProtocolVersion != OperationCommandProtocol.Version || !Identity(permit.GameId, permit.SimulationEpoch, permit.MembershipVersion)) return false;
            try
            {
                var owned = ClonePermit(permit);
                if (!Identity(owned.Boundary.GameId, owned.Boundary.SimulationEpoch, owned.Boundary.MembershipVersion)) return false;
                if (owned.HostValidatedThroughSequence != owned.Boundary.GlobalFenceSequence || owned.HostValidationPrefixDigest != owned.Boundary.CommandPrefixDigest)
                    throw new ArgumentException("permit_host_prefix_mismatch");
                if (owned.ReadyReplicaIds == null || !owned.ReadyReplicaIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(options.RequiredReplicaIds.OrderBy(id => id, StringComparer.Ordinal))) throw new ArgumentException("permit_replicas_mismatch");
                long fence = owned.Boundary.GlobalFenceSequence;
                if (permits.TryGetValue(fence, out var previous) && previous.Boundary.FactDigest != owned.Boundary.FactDigest) throw new ArgumentException("permit_conflict");
                if (!permits.ContainsKey(fence)) { if (permits.Count >= options.MaximumBoundaryFacts) throw new InvalidOperationException("permit_capacity"); permits.Add(fence, owned); }
                if (!boundaries.ContainsKey(fence)) return ReceiveBoundary(owned.Boundary);
                return ApplyPermit(owned);
            }
            catch (Exception exception) { Fault("permit_invalid", exception.Message); return false; }
        }

        private bool ApplyPermit(SimulationWindowPermitDto permit)
        {
            long fence = permit.Boundary.GlobalFenceSequence;
            if (!boundaries.TryGetValue(fence, out var boundary) || boundary.FactDigest != permit.Boundary.FactDigest) { Fault("permit_boundary_mismatch", "许可未绑定本地保存的同前缀事实。"); return false; }
            foreach (var fact in boundary.Windows.Where(fact => fact.RequiresPermit && !fact.IsClosed))
                if (windows.TryGetValue(fact.Window.OperationWindowId, out var record) && record.Open && record.PermissionFence == fence)
                { record.Permitted = true; PublishPermission(record); }
            return true;
        }

        public bool ReceiveValidationPrefix(CommandValidationPrefixDto prefix)
        {
            if (!begun || stopped || options.IsHost || prefix == null || !Identity(prefix.GameId, prefix.SimulationEpoch, prefix.MembershipVersion)) return false;
            try
            {
                if (prefix.ProtocolVersion != OperationCommandProtocol.Version || prefix.DigestVersion != OperationCommandCanonicalizer.DigestVersion || prefix.ValidatedThroughSequence < options.CheckpointGlobalSequence ||
                    prefix.Outcomes == null || prefix.Outcomes.Count > options.MaximumLogCommands) throw new ArgumentException("validation_invalid");
                OperationCommandCanonicalizer.NormalizeDigest(prefix.PrefixDigest);
                var owned = new CommandValidationPrefixDto { GameId = prefix.GameId, SimulationEpoch = prefix.SimulationEpoch, MembershipVersion = prefix.MembershipVersion,
                    ValidatedThroughSequence = prefix.ValidatedThroughSequence, PrefixDigest = prefix.PrefixDigest, Outcomes = prefix.Outcomes.Select(OperationCommandCanonicalizer.SnapshotOutcome).ToList() };
                if (validations.TryGetValue(owned.ValidatedThroughSequence, out var previous) && (previous.PrefixDigest != owned.PrefixDigest || !SameOutcomes(previous.Outcomes, owned.Outcomes))) throw new ArgumentException("validation_conflict");
                if (owned.ValidatedThroughSequence <= validatedThrough) return VerifyValidation(owned, false);
                if (!validations.ContainsKey(owned.ValidatedThroughSequence)) { if (validations.Count >= options.MaximumLogCommands) throw new InvalidOperationException("validation_capacity"); validations.Add(owned.ValidatedThroughSequence, owned); }
                ConsumeValidation(); return !stopped;
            }
            catch (Exception exception) { Fault("validation_invalid", exception.Message); return false; }
        }

        private void ConsumeValidation()
        {
            if (options.IsHost || stopped) return;
            foreach (var pair in validations.Where(pair => pair.Key <= LastAppliedSequence).ToList())
            {
                if (!VerifyValidation(pair.Value, true)) return;
                validations.Remove(pair.Key);
            }
        }

        private bool VerifyValidation(CommandValidationPrefixDto value, bool advance)
        {
            if (!replica.TryReadAppliedPrefix(value.ValidatedThroughSequence, out var digest) || digest != value.PrefixDigest) { Fault("validation_prefix_mismatch", "Host核验前缀与本地实际执行日志不一致。"); return false; }
            long previous = -1;
            foreach (var outcome in value.Outcomes)
            {
                var command = replica.GetAppliedCommand(outcome.GlobalCommandSequence); var result = replica.GetAppliedRuleResult(outcome.GlobalCommandSequence);
                if (outcome.GlobalCommandSequence <= previous || outcome.GlobalCommandSequence > value.ValidatedThroughSequence || command == null || result == null ||
                    command.Command.CommandId != outcome.CommandId || command.Command.PlayerId != outcome.PlayerId || command.OperationWindowId != outcome.OperationWindowId ||
                    command.WindowCommandSequence != outcome.WindowCommandSequence || outcome.Succeeded != result.Succeeded || outcome.ErrorCode != result.Validation.ErrorCode || outcome.Reason != result.Validation.Reason)
                { Fault("validation_outcome_mismatch", "Host核验结果没有对应本地原提交结果。"); return false; }
                var local = replica.CreateValidationPrefix(outcome.GlobalCommandSequence - 1).Outcomes.First();
                if (local.StateRevision != outcome.StateRevision) { Fault("validation_revision_mismatch", "核验结果版本不一致。"); return false; }
                previous = outcome.GlobalCommandSequence;
            }
            if (advance && value.ValidatedThroughSequence > validatedThrough) { validatedThrough = value.ValidatedThroughSequence; CommandValidationPrefixAdvanced?.Invoke(value); }
            return true;
        }

        private static bool SameOutcomes(List<OperationCommandOutcomeDto> left, List<OperationCommandOutcomeDto> right) =>
            left.Count == right.Count && left.Zip(right, (a, b) => a.CommandId == b.CommandId && a.PlayerId == b.PlayerId && a.GlobalCommandSequence == b.GlobalCommandSequence &&
                a.OperationWindowId == b.OperationWindowId && a.WindowCommandSequence == b.WindowCommandSequence && a.Succeeded == b.Succeeded && a.ErrorCode == b.ErrorCode && a.Reason == b.Reason && a.StateRevision == b.StateRevision).All(equal => equal);

        public OperationCommandRangeDto ReadMissingRange(long fromSequence, int maximumCount)
        { if (sequencer == null) throw new InvalidOperationException("simulation_not_host"); return sequencer.ReadRange(fromSequence, maximumCount); }

        public SimulationBoundaryDto ReadBoundary(long fenceSequence) => boundaries.TryGetValue(fenceSequence, out var boundary) ? OperationCommandCanonicalizer.SnapshotBoundary(boundary) : null;
        public IReadOnlyList<SimulationWindowPermissionDto> ReadWindowPermissions() => windows.Values.Select(Permission).ToList().AsReadOnly();
        public IReadOnlyList<OrderedOperationCommandDto> ReadPendingTail() => queue.Values.Select(OperationCommandCanonicalizer.SnapshotOrdered).ToList().AsReadOnly();
        public OperationCommandProposalDto ReadPendingLocalProposal() => pendingLocal == null ? null : OperationCommandCanonicalizer.SnapshotProposal(pendingLocal);
        public CommandValidationPrefixDto ReadLocalValidationPrefix() => options.IsHost ? replica.CreateValidationPrefix(0, validatedThrough) : replica.CreateValidationPrefix();
        public bool IsBoundaryPermitted(long fence) => !stopped && boundaries.TryGetValue(fence, out var boundary) && permits.TryGetValue(fence, out var permit) && boundary.FactDigest == permit.Boundary.FactDigest;
        public SimulationProtocolFaultDto LastFault => CloneFault(lastFault);
        public void BeginConfirmedRecovery()
        { if (recoveryFrozen) return; recoveryFrozen = true; foreach (var record in windows.Values.Where(record => record.Open)) PublishPermission(record); }

        private void CaptureConfirmedFacts(string commandId)
        {
            if (!options.IsHost) return;
            var point = confirmedPoint;
            if (commandId != null && !session.TryReadCommittedRecoveryPoint(commandId, out point)) throw new InvalidOperationException("confirmed_capture_missing");
            if (point == null) throw new InvalidOperationException("confirmed_capture_missing");
            var facts = new List<SimulationWindowFactDto>();
            foreach (var record in windows.Values)
            {
                var progress = replica.ReadWindowProgress(record.Descriptor.OperationWindowId);
                facts.Add(OperationCommandCanonicalizer.SnapshotRecoveryWindow(new SimulationWindowFactDto {
                    Window = record.Descriptor, AvailablePlayerIds = new List<int>(record.Available), IsClosed = !record.Open,
                    FinalCommandSequence = progress.LastWindowCommandSequence, CommandCount = progress.CommandCount, WindowPrefixDigest = progress.PrefixDigest }));
            }
            confirmedPoint = point; confirmedWindows = facts;
        }

        public bool TryCreateConfirmedRecoveryCheckpoint(long expectedConfirmedSequence, long targetEpoch,
            out SimulationRecoveryCheckpointDto checkpoint, out SimulationRecoveryResultDto result)
        {
            checkpoint = null; result = new SimulationRecoveryResultDto();
            try
            {
                if (!options.IsHost || !recoveryFrozen || pumping || !begun) throw new InvalidOperationException("recovery_not_frozen_host");
                if (!stopped && queue.Count != 0) throw new InvalidOperationException("recovery_original_queue_pending");
                if (expectedConfirmedSequence != validatedThrough || confirmedPoint == null || confirmedWindows == null) throw new InvalidOperationException("checkpoint_not_retained");
                if (options.SimulationEpoch >= long.MaxValue || targetEpoch != options.SimulationEpoch + 1) throw new ArgumentException("recovery_epoch_invalid");
                var exported = session.ExportConfirmedRecoveryPoint(confirmedPoint);
                var prefix = replica.CreateValidationPrefix(0, validatedThrough);
                var baseline = new SimulationRecoveryBaselineDto { GameId = options.GameId, SourceSimulationEpoch = options.SimulationEpoch,
                    SimulationEpoch = targetEpoch, MembershipVersion = options.MembershipVersion, ConfirmedGlobalSequence = validatedThrough,
                    CommandPrefixDigest = prefix.PrefixDigest, ContentPackHash = options.ContentPackHash, RuleDigestVersion = RuleStateDigestService.DigestVersion,
                    RuleStateDigest = RuleStateDigestService.Compute(exported.Archive.Snapshot.State), FrozenPlayerIds = new List<int>(options.FrozenPlayerIds),
                    RequiredReplicaIds = new List<string>(options.RequiredReplicaIds), ConfirmedCommands = replica.ReadConfirmedReceipts(validatedThrough) };
                foreach (int player in options.FrozenPlayerIds) baseline.NextSubmissionSequences.Add(player, 1);
                foreach (var receipt in baseline.ConfirmedCommands) baseline.NextSubmissionSequences[receipt.Outcome.PlayerId] = receipt.ClientSubmissionSequence + 1;
                foreach (var source in confirmedWindows)
                {
                    var fact = OperationCommandCanonicalizer.SnapshotRecoveryWindow(source); fact.Window.SimulationEpoch = targetEpoch;
                    fact.Window.BaseWindowSequence = fact.FinalCommandSequence; fact.Window.PrefixDigest = fact.WindowPrefixDigest;
                    fact.IsNewWindow = false; fact.RequiresPermit = !fact.IsClosed; baseline.Windows.Add(fact);
                }
                baseline.MetadataDigest = OperationCommandCanonicalizer.CanonicalRecoveryMetadataDigest(baseline);
                baseline.CheckpointId = OperationCommandCanonicalizer.RecoveryCheckpointId(baseline);
                var target = options.Snapshot(); target.SimulationEpoch = targetEpoch; target.CheckpointGlobalSequence = validatedThrough; target.CheckpointPrefixDigest = baseline.CommandPrefixDigest;
                OperationCommandCanonicalizer.ValidateRecoveryBaseline(target, baseline, exported.Archive.Snapshot.State);
                checkpoint = new SimulationRecoveryCheckpointDto { Baseline = baseline, Archive = exported.Archive, Continuation = exported.Continuation };
                result.Succeeded = true; result.CheckpointId = baseline.CheckpointId; return true;
            }
            catch (Exception exception) { result.ErrorCode = exception.Message == "checkpoint_not_retained" ? "checkpoint_not_retained" : "confirmed_checkpoint_export_failed"; result.Reason = exception.Message; return false; }
        }

        // 仅全员 Installed 后调用；生产失败保留停止态，不能靠安装跳过失败尾段。
        public void CompleteConfirmedRecovery(bool resumeAllowed)
        {
            if (!recoveryFrozen) return;
            if (options.IsHost)
            {
                confirmedPoint = session.EnableConfirmedRecoveryCapture(); CaptureConfirmedFacts(null);
            }
            if (!resumeAllowed) { stopped = requiresRecovery = true; return; }
            recoveryFrozen = false;
            CaptureBoundary();
        }
        public void Stop() { stopped = true; }
        public void PeerDisconnected(int playerId)
        {
            if (!options.IsHost || stopped) return;
            string id = DeterministicSimulationSessionOptions.ReplicaIdForPlayer(playerId);
            foreach (var group in ready.Values) group.Remove(id);
        }
        public void ReceiveRemoteFault(SimulationProtocolFaultDto fault)
        { if (!stopped && fault != null && fault.ProtocolVersion == OperationCommandProtocol.Version && Identity(fault.GameId, fault.SimulationEpoch, fault.MembershipVersion)) Fault(fault.FaultCode, fault.Diagnostic, null, null, fault); }

        private bool Identity(string gameId, long epoch, int membership) => gameId == options.GameId && epoch == options.SimulationEpoch && membership == options.MembershipVersion;
        private SimulationWindowPermissionDto Permission(WindowRecord record) => new SimulationWindowPermissionDto { Window = OperationCommandCanonicalizer.SnapshotWindow(record.Descriptor, options.GameId, options.SimulationEpoch, options.MembershipVersion),
            AvailablePlayerIds = new List<int>(record.Available), IsOpen = record.Open, IsPermitted = record.Permitted && !stopped && !recoveryFrozen,
            CanSubmitForLocalPlayer = record.Open && record.Permitted && !stopped && !recoveryFrozen && record.Available.Contains(options.LocalPlayerId), GlobalFenceSequence = record.PermissionFence };
        private void PublishPermission(WindowRecord record) { var notice = Permission(record); Emit(() => OperationWindowPermissionChanged?.Invoke(notice)); }
        private void Emit(Action notification) { if (deferNotifications) deferredNotifications.Add(notification); else notification(); }
        private void FlushNotifications()
        { var notifications = deferredNotifications.ToArray(); deferredNotifications.Clear(); deferNotifications = false; foreach (var notification in notifications) notification(); }
        private SimulationWindowPermitDto ClonePermit(SimulationWindowPermitDto value) => new SimulationWindowPermitDto { GameId = value.GameId, SimulationEpoch = value.SimulationEpoch,
            MembershipVersion = value.MembershipVersion, Boundary = OperationCommandCanonicalizer.SnapshotBoundary(value.Boundary), HostValidatedThroughSequence = value.HostValidatedThroughSequence,
            HostValidationPrefixDigest = value.HostValidationPrefixDigest, ReadyReplicaIds = value.ReadyReplicaIds == null ? null : new List<string>(value.ReadyReplicaIds) };
        private void RequestMissing(long from) => MissingRangeRequested?.Invoke(new SimulationMissingRangeRequestDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion, FromSequence = from });
        private OperationCommandReplicaResult Reply(OperationReplicaStatus status, string code) => new OperationCommandReplicaResult { Status = status, ErrorCode = code, ExpectedGlobalSequence = LastAppliedSequence + 1 };
        private OperationCommandReplicaResult FaultReply(string code, string reason, OrderedOperationCommandDto command) { Fault(code, reason, command); return Reply(OperationReplicaStatus.Stopped, code); }
        private void Fault(string code, string reason, OrderedOperationCommandDto command = null, CommandResult result = null, SimulationProtocolFaultDto remote = null)
        {
            if (stopped) return;
            stopped = requiresRecovery = true;
            PublishHostValidation();
            lastFault = CloneFault(remote) ?? new SimulationProtocolFaultDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion,
                FaultCode = code, Diagnostic = reason, FailureGlobalSequence = command?.GlobalCommandSequence ?? 0, Command = command?.Command,
                ErrorCode = result?.Validation?.ErrorCode ?? CommandErrorCode.UnknownCommand, LocalRuleResult = result, RequiresRecovery = true };
            lastFault.Command = CloneCommand(lastFault.Command); lastFault.RequiresRecovery = true;
            foreach (var record in windows.Values.Where(record => record.Open)) PublishPermission(record);
            SimulationProtocolFaulted?.Invoke(CloneFault(lastFault));
        }

        private static GameCommandDto CloneCommand(GameCommandDto value) => value == null ? null : new GameCommandDto { CommandId = value.CommandId, Kind = value.Kind, PlayerId = value.PlayerId,
            SourceId = value.SourceId, TargetId = value.TargetId, OptionIds = value.OptionIds == null ? null : new List<string>(value.OptionIds),
            Parameters = value.Parameters == null ? null : value.Parameters.Select(parameter => parameter == null ? null : new GameCommandParameterDto { Key = parameter.Key, Value = parameter.Value }).ToList() };
        private static SimulationProtocolFaultDto CloneFault(SimulationProtocolFaultDto value) => value == null ? null : new SimulationProtocolFaultDto {
            ProtocolVersion = value.ProtocolVersion, GameId = value.GameId, SimulationEpoch = value.SimulationEpoch, MembershipVersion = value.MembershipVersion,
            FaultCode = value.FaultCode, Diagnostic = value.Diagnostic, FailureGlobalSequence = value.FailureGlobalSequence, Command = CloneCommand(value.Command),
            ErrorCode = value.ErrorCode, RequiresRecovery = value.RequiresRecovery, LocalRuleResult = value.LocalRuleResult };

        private void PublishHostValidation()
        {
            if (!options.IsHost || validatedThrough <= publishedValidation) return;
            using var validationSample = new ProtocolExecutionSample(session.ExecutionTimings, "pump.validation");
            var prefix = replica.CreateValidationPrefix(publishedValidation, validatedThrough); publishedValidation = validatedThrough;
            CommandValidationPrefixAdvanced?.Invoke(prefix);
        }

        private string ComputeBoundaryRuleDigest()
        {
            using (new ProtocolExecutionSample(session.ExecutionTimings, "boundary.digest"))
                return RuleStateDigestService.Compute(session.State);
        }
    }
}
