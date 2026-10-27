using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public sealed class WindowExecutionFrame
    {
        public readonly string GrantId;
        public readonly int OwnerPlayerId;
        public readonly bool AwaitingGrant, BoundaryPending;
        internal WindowExecutionFrame(string id, int owner, bool awaiting, bool boundary)
        { GrantId = id; OwnerPlayerId = owner; AwaitingGrant = awaiting; BoundaryPending = boundary; }
        public bool IsActive => !string.IsNullOrEmpty(GrantId);
        public bool BlocksSubmission => AwaitingGrant || BoundaryPending;
    }
    [Serializable]
    public sealed class WindowExecutionReadyMessage
    {
        public int PlayerId;
        public WindowExecutionSeal Seal;
    }
    [Serializable]
    public sealed class WindowExecutionRangeMessage : SimulationIdentityDto
    {
        public string GrantId;
        public int TargetPlayerId;
        public long FromOperation;
        public int MaximumCount = 4;
        public List<WindowExecutionRecord> Records = new List<WindowExecutionRecord>();
    }

    public sealed partial class DeterministicSimulationCoordinator
    {
        private int windowExecutionVersion;
        private long lastWindowGrantSerial;
        private WindowLocalExecution windowExecution;
        private WindowExecutionGrant activeWindowGrant;
        private bool windowSealPublished, windowReadyPublished;
        private readonly Dictionary<string, WindowLocalExecution> windowExecutions = new Dictionary<string, WindowLocalExecution>(StringComparer.Ordinal);
        private readonly Dictionary<string, WindowExecutionGrant> windowExecutionGrants = new Dictionary<string, WindowExecutionGrant>(StringComparer.Ordinal);
        private readonly List<WindowExecutionRecord> beforeWindowGrant = new List<WindowExecutionRecord>();
        public event Action<WindowExecutionGrant> WindowExecutionGranted;
        public event Action<WindowExecutionRecord> WindowExecutionRecordProduced;
        public event Action<WindowExecutionSeal> WindowExecutionSealProduced;
        public event Action<WindowExecutionReadyMessage> WindowExecutionReadyProduced;
        public event Action<WindowExecutionConfirmation> WindowExecutionConfirmed;
        public event Action<WindowExecutionRangeMessage> WindowExecutionMissingRequested;
        public event Action<WindowExecutionRangeMessage> WindowExecutionRangeProduced;

        public void ConfigureWindowExecution(int version)
        {
            if (background != null || version != 0 && version != WindowExecutionGrant.Version) throw new ArgumentException("window_execution_configuration_invalid");
            windowExecutionVersion = version;
        }
        private WindowExecutionFrame ReadWindowExecutionFrame()
        {
            if (windowExecutionVersion == 0) return null;
            if (windowExecution != null) return new WindowExecutionFrame(activeWindowGrant.GrantId, activeWindowGrant.OwnerPlayerId, false, windowExecution.NeedsBoundary || windowExecution.IsStopped);
            var eligible = ExclusiveWindow();
            return eligible == null ? null : new WindowExecutionFrame(null, eligible.AuthorizedPlayerIds[0], true, false);
        }
        private OperationWindowState ExclusiveWindow()
        {
            if (windowExecutionVersion == 0 || stopped || recoveryFrozen) return null;
            var actual = OperationWindowService.ReadOpenWindows(session.State);
            var writers = actual.SelectMany(w => w.AvailablePlayerIds).Distinct().ToArray();
            if (writers.Length != 1) return null;
            return actual.FirstOrDefault(w => w.AuthorizedPlayerIds.Count == 1 && w.AuthorizedPlayerIds[0] == writers[0] && w.AvailablePlayerIds.Contains(writers[0]) &&
                (w.WindowKind == RoundMainlineNodeTypeIds.PlayerActionWindow || w.WindowKind == RoundMainlineNodeTypeIds.PlayerCleanupWindow) &&
                windows.TryGetValue(w.OperationWindowId, out var record) && record.Permitted);
        }
        // 返回true表示本通道处理（包括合法拒绝/窗口待汇合），调用方不得再把同命令送旧Host提案。
        public bool TrySubmitWindowExecution(GameCommand command, out CommandResult result)
        {
            result = null;
            if (windowExecutionVersion == 0) return false;
            var frame = OnBackgroundMainThread ? visibleBackground?.WindowExecution : ReadWindowExecutionFrame();
            if (frame == null) return false; // 多writer原并发模式维持已有全局编序。
            if (frame.BlocksSubmission || !frame.IsActive || frame.OwnerPlayerId != options.LocalPlayerId)
            { result = CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.WrongPhase, "当前窗口正在等待许可或边界汇合。")); return true; }
            if (!TryCreateLocalProposal(command, out var proposal, out var invalid)) { result = CommandResult.Invalid(invalid); return true; }
            var owned = OperationCommandCanonicalizer.SnapshotProposal(proposal); string grantId = frame.GrantId;
            if (OnBackgroundMainThread)
            {
                if (!background.TryEnqueue(() => ExecuteWindowProposal(grantId, owned)))
                { BackgroundMainFault("window_execution_queue_capacity", "本地规则队列已满，原命令尚未执行。"); result = CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.WrongPhase, "本地规则队列已满。")); return true; }
                result = CommandResult.SuccessResult(null, "命令已交本地规则执行。"); return true;
            }
            result = ExecuteWindowProposal(grantId, owned); return true;
        }
        private CommandResult ExecuteWindowProposal(string grantId, OperationCommandProposalDto proposal)
        {
            if (stopped || recoveryFrozen || windowExecution == null || activeWindowGrant.GrantId != grantId)
                return RejectWindowProposal(proposal, ValidationResult.Failure(CommandErrorCode.WrongPhase, "原窗口许可已停止，保留原提交。"));
            long before = windowExecution.Tail;
            var applied = windowExecution.ExecuteLocal(proposal.Command.ToCommand());
            if (windowExecution.IsStopped) { Fault("window_execution_local_fault", windowExecution.FirstFault, windowExecution.FailureCommand, applied.RuleResult); return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.UnknownCommand, windowExecution.FirstFault)); }
            if (applied.Status == "backpressure")
            {
                Fault("window_execution_log_capacity", "窗口日志容量已满，保留未确认原件并停止，等待明确恢复。");
                return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.WrongPhase, "窗口日志容量已满，原操作已保留，需恢复确认前缀。"));
            }
            if (applied.Status != "applied_locally" && applied.Status != "duplicate")
                return RejectWindowProposal(proposal, applied.RuleResult?.Validation ?? ValidationResult.Failure(CommandErrorCode.InvalidTarget,
                    applied.Status == "backpressure" ? "窗口日志容量已满，原操作已保留，请在确认边界后恢复。" : "当前窗口不能继续该操作，请保留原选择并等待边界就绪。"));
            ProduceWindowRecords(windowExecution, before); FinishWindowExecution(windowExecution);
            return applied.RuleResult ?? CommandResult.SuccessResult(null, "原命令已经执行。");
        }
        private CommandResult RejectWindowProposal(OperationCommandProposalDto proposal, ValidationResult invalid)
        {
            pendingLocal = null;
            var notice = new SimulationProposalResultDto { GameId = options.GameId, SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion,
                Proposal = proposal, Status = OperationAdmissionStatus.Rejected, ErrorCode = invalid.ErrorCode, Reason = invalid.Reason, ExpectedClientSubmissionSequence = nextLocalSubmission };
            Emit(() => { if (background != null && backgroundPendingLocal?.Command.CommandId == proposal.Command.CommandId) { backgroundPendingLocal = null; backgroundPendingValidationLocal = false; } Deliver(SimulationProposalRejected, notice); });
            return CommandResult.Invalid(invalid);
        }
        private void TryGrantWindowExecution(SimulationWindowPermitDto permit)
        {
            if (windowExecutionVersion == 0 || !options.IsHost || windowExecution != null || queue.Count != 0 || sequencer.LastSequencedSequence != LastAppliedSequence) return;
            var root = ExclusiveWindow(); if (root == null) return;
            var grant = WindowExecutionCodec.CreateGrant(session, options, permit, root.AuthorizedPlayerIds[0], root.OperationWindowId,
                checked(lastWindowGrantSerial + 1), sequencer.ReadNextSubmissionSequence(root.AuthorizedPlayerIds[0]));
            if (!sequencer.TryReserveWindowExecution(grant.GrantId, grant.BaseGlobalSequence, grant.BasePrefixDigest)) return;
            try { InstallWindowExecution(grant); var owned = WindowExecutionCodec.Copy(grant); Emit(() => Deliver(WindowExecutionGranted, owned)); }
            catch { sequencer.ReleaseWindowExecution(grant.GrantId); throw; }
        }
        public void ReceiveWindowExecutionGrant(WindowExecutionGrant value)
        {
            if (value == null || options.IsHost || !Identity(value.GameId, value.SimulationEpoch, value.MembershipVersion)) return;
            var owned = WindowExecutionCodec.Copy(value);
            if (OnBackgroundMainThread) { PostBackground(() => ReceiveWindowExecutionGrant(owned)); return; }
            try
            {
                if (owned.GrantId != WindowExecutionCodec.GrantDigest(owned)) throw new ArgumentException("window_execution_grant_digest_conflict");
                if (windowExecutions.ContainsKey(owned.GrantId)) return;
                InstallWindowExecution(owned);
                foreach (var record in beforeWindowGrant.Where(r => r.GrantId == owned.GrantId).ToArray()) { beforeWindowGrant.Remove(record); ReceiveWindowExecutionRecord(0, true, record); }
            }
            catch (Exception ex) { Fault("window_execution_grant_invalid", ex.Message); }
        }
        private void InstallWindowExecution(WindowExecutionGrant grant)
        {
            if (stopped || recoveryFrozen || windowExecution != null || grant.GrantSerial <= lastWindowGrantSerial || grant.BaseGlobalSequence != LastAppliedSequence ||
                grant.ContentPackHash != options.ContentPackHash || !WindowExecutionCodec.Same(grant.RequiredReplicaIds, options.RequiredReplicaIds) ||
                windowExecutions.Count >= options.MaximumBoundaryFacts) throw new InvalidOperationException("window_execution_grant_order_or_identity");
            if (!replica.TryReadAppliedPrefix(LastAppliedSequence, out var prefix) || prefix != grant.BasePrefixDigest) throw new InvalidOperationException("window_execution_base_prefix_conflict");
            foreach (var fact in grant.BaseWindows)
            {
                var progress = replica.ReadWindowProgress(fact.Window.OperationWindowId);
                if (progress.LastWindowCommandSequence != fact.FinalCommandSequence || progress.PrefixDigest != fact.WindowPrefixDigest ||
                    !windows.TryGetValue(fact.Window.OperationWindowId, out var registered) || registered.Descriptor.WindowContractDigest != fact.Window.WindowContractDigest)
                    throw new InvalidOperationException("window_execution_base_window_conflict");
            }
            int remaining = options.MaximumLogCommands - replica.ReadConfirmedReceipts(LastAppliedSequence).Count;
            var stream = new WindowLocalExecution(session, grant, options.LocalPlayerId, options.IsHost, sequencer,
                replica.ReadConfirmedReceipts(LastAppliedSequence).Select(r => r.Outcome.CommandId), Math.Min(4096, remaining),
                options.IsHost ? (Func<string, int, OperationCommandProposalDto, OperationCommandAdmissionResult>)sequencer.AdmitWindowExecutionProposal : null, false);
            activeWindowGrant = WindowExecutionCodec.Copy(grant); windowExecution = stream; lastWindowGrantSerial = grant.GrantSerial;
            windowSealPublished = windowReadyPublished = false; windowExecutions.Add(grant.GrantId, stream); windowExecutionGrants.Add(grant.GrantId, WindowExecutionCodec.Copy(grant));
            stream.CommandExecuted += (record, result) => RecordWindowExecution(stream, record, result);
            foreach (var record in windows.Values.Where(w => w.Open)) PublishPermission(record);
        }
        private void RecordWindowExecution(WindowLocalExecution stream, WindowExecutionRecord record, CommandResult result)
        {
            var receipt = replica.RecordWindowExecution(record.Ordered, result);
            if (receipt.Status != OperationReplicaStatus.Applied) throw new InvalidOperationException(receipt.ErrorCode + ":" + receipt.Reason);
            if (record.Ordered.Command.PlayerId == options.LocalPlayerId) { nextLocalSubmission = record.Ordered.ClientSubmissionSequence + 1; pendingLocal = null; }
            RefreshWindows(false);
            // 本人后继请求继承已确认根窗许可；原TryResolveCommandContext仍校验真实因果归属。
            var root = session.State.EffectRuntime.OperationWindows.Find(w => w.OperationWindowId == activeWindowGrant.RootWindowId);
            if (record.BoundaryReason == "" && root != null)
                foreach (var w in OperationWindowService.ReadOpenWindows(session.State).Where(w => w.SourceMainNodeId == root.SourceMainNodeId && w.AvailablePlayerIds.Count > 0 &&
                    w.AuthorizedPlayerIds.All(p => p == activeWindowGrant.OwnerPlayerId) && w.AvailablePlayerIds.All(p => p == activeWindowGrant.OwnerPlayerId)))
                    if (windows.TryGetValue(w.OperationWindowId, out var inherited)) inherited.Permitted = true;
            foreach (var w in windows.Values.Where(w => w.Open)) PublishPermission(w);
            PublishApplied(record.Ordered, result);
        }
        private void ProduceWindowRecords(WindowLocalExecution stream, long previousTail)
        {
            var grant = windowExecutionGrants.First(p => ReferenceEquals(windowExecutions[p.Key], stream)).Value;
            if (!options.IsHost && grant.OwnerPlayerId != options.LocalPlayerId) return;
            foreach (var record in stream.ReadRange(previousTail + 1, options.MaximumLogCommands)) { var owned = record; Emit(() => Deliver(WindowExecutionRecordProduced, owned)); }
        }
        public void ReceiveWindowExecutionRecord(int verifiedSourcePlayer, bool authenticatedHost, WindowExecutionRecord value)
        {
            if (value?.Ordered == null || !Identity(value.Ordered.GameId, value.Ordered.SimulationEpoch, value.Ordered.MembershipVersion)) return;
            var owned = WindowExecutionCodec.Copy(value);
            if (OnBackgroundMainThread) { PostBackground(() => ReceiveWindowExecutionRecord(verifiedSourcePlayer, authenticatedHost, owned)); return; }
            if (stopped || recoveryFrozen) return;
            if (!windowExecutions.TryGetValue(owned.GrantId, out var stream))
            {
                if (options.IsHost || !authenticatedHost) return;
                if (beforeWindowGrant.Count >= options.MaximumQueuedCommands) { Fault("window_execution_before_grant_capacity", "缺少窗口许可，保留已有原件。"); return; }
                beforeWindowGrant.Add(owned); var request = RangeIdentity(owned.GrantId, 0, 0); Emit(() => Deliver(WindowExecutionMissingRequested, request)); return;
            }
            long before = stream.Tail; var result = stream.Receive(verifiedSourcePlayer, authenticatedHost, owned);
            if (stream.IsStopped) { Fault("window_execution_replay_fault", stream.FirstFault, owned.Ordered); return; }
            if (result.Status == "rejected") return;
            ProduceWindowRecords(stream, before); FinishWindowExecution(stream);
        }
        private void FinishWindowExecution(WindowLocalExecution stream)
        {
            if (stream == null || !ReferenceEquals(stream, windowExecution) || stopped || recoveryFrozen) return;
            if (stream.MissingRange().HasValue) { RequestWindowGap(stream); return; }
            if (pendingReplicaSeals.TryGetValue(activeWindowGrant.GrantId, out var requested) && stream.Tail == requested.OperationCount)
            {
                if (stream.BuildReady(requested) == null) { Fault("window_execution_seal_conflict", stream.FirstFault); return; }
                pendingReplicaSeals.Remove(activeWindowGrant.GrantId);
            }
            if (!stream.NeedsBoundary) return;
            var seal = stream.Seal();
            if (!windowSealPublished && (options.IsHost || options.LocalPlayerId == activeWindowGrant.OwnerPlayerId))
            { windowSealPublished = true; var owned = WindowExecutionCodec.Copy(seal); Emit(() => Deliver(WindowExecutionSealProduced, owned)); }
            if (!windowReadyPublished)
            {
                var local = stream.BuildReady(seal); if (local == null) { Fault("window_execution_boundary_conflict", stream.FirstFault); return; }
                windowReadyPublished = true;
                if (options.IsHost) stream.ReceiveReady(options.LocalPlayerId, local);
                else { var readyMessage = new WindowExecutionReadyMessage { PlayerId = options.LocalPlayerId, Seal = local }; Emit(() => Deliver(WindowExecutionReadyProduced, readyMessage)); }
            }
            if (options.IsHost)
            {
                foreach (var item in pendingWindowReady.Where(item => item.Message.Seal.GrantId == activeWindowGrant.GrantId).ToArray())
                {
                    pendingWindowReady.Remove(item);
                    if (!stream.ReceiveReady(item.Player, item.Message.Seal)) { Fault("window_execution_ready_conflict", stream.FirstFault ?? "原边界Ready与完整尾巴不一致。"); return; }
                }
                var confirmation = stream.TryConfirm();
                if (confirmation != null)
                {
                    validatedThrough = LastAppliedSequence; CaptureConfirmedFacts(replica.GetAppliedCommand(validatedThrough)?.Command.CommandId);
                    var notice = WindowExecutionCodec.Copy(confirmation); Emit(() => Deliver(WindowExecutionConfirmed, notice));
                    sequencer.ReleaseWindowExecution(activeWindowGrant.GrantId); windowExecution = null; activeWindowGrant = null;
                    CaptureBoundary(); PublishHostValidation();
                }
            }
        }
        public void ReceiveWindowExecutionSeal(int verifiedOwner, bool authenticatedHost, WindowExecutionSeal value)
        {
            if (value == null) return; var owned = WindowExecutionCodec.Copy(value);
            if (OnBackgroundMainThread) { PostBackground(() => ReceiveWindowExecutionSeal(verifiedOwner, authenticatedHost, owned)); return; }
            if (stopped || recoveryFrozen || !windowExecutions.TryGetValue(owned.GrantId, out var stream)) return;
            if (!ReferenceEquals(stream, windowExecution) && stream.HasConfirmedSeal)
            {
                if (options.IsHost ? verifiedOwner != windowExecutionGrants[owned.GrantId].OwnerPlayerId : !authenticatedHost) return;
                stream.ReceiveConfirmedSeal(owned);
                if (stream.IsStopped) Fault("window_execution_seal_conflict", stream.FirstFault);
                return;
            }
            if (options.IsHost) stream.ReceiveSeal(verifiedOwner, owned);
            else if (!authenticatedHost) return;
            else if (stream.Tail == owned.OperationCount && stream.BuildReady(owned) == null) { Fault("window_execution_seal_conflict", stream.FirstFault); return; }
            // 缺包副本从已认证Host接收尾号，借ReceiveSeal的同一有界校验登记待补尾巴。
            if (!options.IsHost && stream.Tail < owned.OperationCount) { pendingReplicaSeals[owned.GrantId] = owned; RequestWindowGap(stream, owned.OperationCount); return; }
            if (stream.IsStopped) { Fault("window_execution_seal_conflict", stream.FirstFault); return; } FinishWindowExecution(stream);
        }
        private readonly Dictionary<string, WindowExecutionSeal> pendingReplicaSeals = new Dictionary<string, WindowExecutionSeal>(StringComparer.Ordinal);
        public void ReceiveWindowExecutionReady(int verifiedSource, WindowExecutionReadyMessage value)
        {
            if (value?.Seal == null || !options.IsHost || value.PlayerId != verifiedSource) return; var owned = WindowExecutionCodec.Copy(value);
            if (OnBackgroundMainThread) { PostBackground(() => ReceiveWindowExecutionReady(verifiedSource, owned)); return; }
            if (stopped || !windowExecutions.TryGetValue(owned.Seal.GrantId, out var stream)) return;
            if (!stream.ReceiveReady(verifiedSource, owned.Seal))
            {
                if (stream.IsStopped) Fault("window_execution_ready_conflict", stream.FirstFault);
                else if (!pendingWindowReady.Any(r => r.Player == verifiedSource && r.Message.Seal.SealDigest == owned.Seal.SealDigest))
                {
                    if (pendingWindowReady.Count >= options.MaximumQueuedCommands) { Fault("window_execution_ready_capacity", "待核对边界收据已满，保留原件。"); return; }
                    pendingWindowReady.Add((verifiedSource, owned));
                }
                return;
            }
            FinishWindowExecution(stream);
        }
        private readonly List<(int Player, WindowExecutionReadyMessage Message)> pendingWindowReady = new List<(int, WindowExecutionReadyMessage)>();
        public void ReceiveWindowExecutionConfirmation(WindowExecutionConfirmation value)
        {
            if (value?.Seal == null || options.IsHost) return; var owned = WindowExecutionCodec.Copy(value);
            if (OnBackgroundMainThread) { PostBackground(() => ReceiveWindowExecutionConfirmation(owned)); return; }
            if (stopped || !windowExecutions.TryGetValue(owned.Seal.GrantId, out var stream) || !ReferenceEquals(stream, windowExecution)) return;
            if (!stream.ReceiveConfirmation(true, owned)) { Fault("window_execution_confirmation_conflict", stream.FirstFault ?? "确认边界不匹配本机实际尾巴。"); return; }
            windowExecution = null; activeWindowGrant = null; CaptureBoundary();
        }
        private WindowExecutionRangeMessage RangeIdentity(string grantId, long from, int target) => new WindowExecutionRangeMessage {
            GameId = options.GameId, SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion, GrantId = grantId, TargetPlayerId = target, FromOperation = from };
        private void RequestWindowGap(WindowLocalExecution stream, long requiredTail = 0)
        {
            var gap = stream.MissingRange(); long from = gap?.From ?? (requiredTail > stream.Tail ? stream.Tail + 1 : 0); if (from == 0) return;
            var request = RangeIdentity(activeWindowGrant.GrantId, from, options.IsHost ? activeWindowGrant.OwnerPlayerId : 0); Emit(() => Deliver(WindowExecutionMissingRequested, request));
        }
        public void RetryWindowExecutionReplication()
        {
            if (windowExecutionVersion == 0) return;
            if (OnBackgroundMainThread) { PostBackground(RetryWindowExecutionReplication); return; }
            if (stopped || windowExecution == null) return;
            if (options.IsHost && windowExecution.Tail == 0) { var grant = WindowExecutionCodec.Copy(activeWindowGrant); Emit(() => Deliver(WindowExecutionGranted, grant)); }
            if (pendingReplicaSeals.TryGetValue(activeWindowGrant.GrantId, out var seal))
            { if (windowExecution.Tail < seal.OperationCount) RequestWindowGap(windowExecution, seal.OperationCount); else { pendingReplicaSeals.Remove(activeWindowGrant.GrantId); ReceiveWindowExecutionSeal(0, true, seal); } }
            RequestWindowGap(windowExecution);
            foreach (var item in pendingWindowReady.ToArray()) { pendingWindowReady.Remove(item); ReceiveWindowExecutionReady(item.Player, item.Message); }
            FinishWindowExecution(windowExecution);
        }
        public void ReceiveWindowExecutionMissing(int verifiedSource, bool authenticatedHost, WindowExecutionRangeMessage request)
        {
            if (request == null || !Identity(request.GameId, request.SimulationEpoch, request.MembershipVersion) || request.MaximumCount < 1 || request.MaximumCount > 4 ||
                (options.IsHost ? !options.FrozenPlayerIds.Contains(verifiedSource) : !authenticatedHost)) return;
            var owned = WindowExecutionCodec.Copy(request); if (OnBackgroundMainThread) { PostBackground(() => ReceiveWindowExecutionMissing(verifiedSource, authenticatedHost, owned)); return; }
            if (!windowExecutions.TryGetValue(owned.GrantId, out var stream)) return;
            if (owned.FromOperation == 0) { if (options.IsHost) { var grant = WindowExecutionCodec.Copy(windowExecutionGrants[owned.GrantId]); Emit(() => Deliver(WindowExecutionGranted, grant)); } return; }
            var response = RangeIdentity(owned.GrantId, owned.FromOperation, options.IsHost ? verifiedSource : 0);
            response.Records = stream.ReadRange(owned.FromOperation, owned.MaximumCount).ToList(); Emit(() => Deliver(WindowExecutionRangeProduced, response));
        }
        public void ReceiveWindowExecutionRange(int verifiedSource, bool authenticatedHost, WindowExecutionRangeMessage value)
        {
            if (value?.Records == null || value.Records.Count > 4 || !Identity(value.GameId, value.SimulationEpoch, value.MembershipVersion)) return;
            foreach (var record in value.Records) if (record != null && record.GrantId == value.GrantId) ReceiveWindowExecutionRecord(verifiedSource, authenticatedHost, record);
        }
        private IEnumerable<OrderedOperationCommandDto> ReadUnconfirmedWindowTail() => windowExecutions.Values.Where(s => s.GlobalSequence > validatedThrough).SelectMany(s => s.ReadRange(1, options.MaximumLogCommands))
            .Where(r => r.Ordered.GlobalCommandSequence > validatedThrough).Select(r => OperationCommandCanonicalizer.SnapshotOrdered(r.Ordered));
    }
}
