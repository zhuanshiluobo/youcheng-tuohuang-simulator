using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    /// <summary>只按登记窗口的连续日志提交一次；业务拒绝或同号冲突停止，不实施回滚或 revision 适配。</summary>
    public sealed class DeterministicCommandReplica
    {
        private sealed class WindowLedger
        {
            internal OperationWindowDescriptorDto Facts;
            internal long Sequence;
            internal string Prefix;
        }
        private sealed class AppliedRecord
        {
            internal OrderedOperationCommandDto Command;
            internal CommandResult Result;
            internal OperationCommandOutcomeDto Outcome;
            internal string Prefix;
        }
        private readonly object gate = new object();
        private readonly GameSession session;
        private readonly string gameId;
        private readonly long epoch, baseSequence;
        private readonly int membership, bufferCapacity, historyCapacity, windowCapacity;
        private readonly GameStateViewer? viewer;
        private readonly Dictionary<string, WindowLedger> windows = new Dictionary<string, WindowLedger>(StringComparer.Ordinal);
        private readonly SortedDictionary<long, OrderedOperationCommandDto> buffered = new SortedDictionary<long, OrderedOperationCommandDto>();
        private readonly SortedDictionary<long, AppliedRecord> applied = new SortedDictionary<long, AppliedRecord>();
        private readonly Dictionary<string, long> commandIds = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<int, long> nextSubmission = new Dictionary<int, long>();
        private readonly List<SimulationConfirmedCommandReceiptDto> confirmedReceipts = new List<SimulationConfirmedCommandReceiptDto>();
        private long lastApplied;
        private string prefix;
        private readonly string basePrefix;
        private bool stopped;
        private bool applying;
        private string stopCode, stopReason;
        private OrderedOperationCommandDto failureCommand;
        private CommandResult failureResult;

        public DeterministicCommandReplica(GameSession session, long simulationEpoch, int membershipVersion,
            int maximumBufferedCommands = 256, int maximumAppliedCommands = 4096, long baseGlobalSequence = 0,
            string baseCommandPrefixDigest = null, GameStateViewer? projectionViewer = null, int maximumRegisteredWindows = 256)
            : this(session, session?.State.GameId, simulationEpoch, membershipVersion, maximumBufferedCommands,
                maximumAppliedCommands, baseGlobalSequence, baseCommandPrefixDigest, projectionViewer, maximumRegisteredWindows) { }

        private DeterministicCommandReplica(GameSession session, string identityGameId, long simulationEpoch, int membershipVersion,
            int maximumBufferedCommands, int maximumAppliedCommands, long baseGlobalSequence,
            string baseCommandPrefixDigest, GameStateViewer? projectionViewer, int maximumRegisteredWindows)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            gameId = identityGameId; OperationCommandCanonicalizer.ValidateIdentity(gameId, simulationEpoch, membershipVersion);
            if (maximumBufferedCommands <= 0 || maximumAppliedCommands <= 0 || maximumRegisteredWindows <= 0 || baseGlobalSequence < 0 || baseGlobalSequence >= long.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(maximumBufferedCommands));
            if (baseGlobalSequence != 0 && string.IsNullOrEmpty(baseCommandPrefixDigest)) throw new ArgumentException("global_base_prefix_missing");
            epoch = simulationEpoch; membership = membershipVersion; bufferCapacity = maximumBufferedCommands; historyCapacity = maximumAppliedCommands;
            windowCapacity = maximumRegisteredWindows;
            baseSequence = lastApplied = baseGlobalSequence; viewer = projectionViewer;
            basePrefix = prefix = OperationCommandCanonicalizer.NormalizeDigest(baseCommandPrefixDigest ?? OperationCommandCanonicalizer.EmptyPrefixDigest);
            if (baseGlobalSequence == 0 && prefix != OperationCommandCanonicalizer.EmptyPrefixDigest) throw new ArgumentException("global_base_prefix_conflict");
        }

        public long LastAppliedSequence { get { lock (gate) return lastApplied; } }
        public bool IsStopped { get { lock (gate) return stopped; } }
        public int BufferedCommandCount { get { lock (gate) return buffered.Count; } }
        public OrderedOperationCommandDto FailureCommand { get { lock (gate) return failureCommand == null ? null : OperationCommandCanonicalizer.SnapshotOrdered(failureCommand); } }
        public CommandResult FailureResult { get { lock (gate) return failureResult; } }

        public static DeterministicCommandReplica FromConfirmedRecovery(GameSession session, DeterministicSimulationSessionOptions options,
            SimulationRecoveryBaselineDto baseline, GameState preparedState)
        {
            OperationCommandCanonicalizer.ValidateRecoveryBaseline(options, baseline, preparedState);
            var value = new DeterministicCommandReplica(session, options.GameId, options.SimulationEpoch, options.MembershipVersion,
                options.MaximumQueuedCommands, options.MaximumLogCommands, baseline.ConfirmedGlobalSequence,
                baseline.CommandPrefixDigest, null, options.MaximumRegisteredWindows);
            foreach (var pair in baseline.NextSubmissionSequences) value.nextSubmission.Add(pair.Key, pair.Value);
            foreach (var receipt in baseline.ConfirmedCommands)
            {
                value.commandIds.Add(receipt.Outcome.CommandId, receipt.Outcome.GlobalCommandSequence);
                value.confirmedReceipts.Add(OperationCommandCanonicalizer.SnapshotConfirmedReceipt(receipt));
            }
            foreach (var fact in baseline.Windows) value.RegisterWindow(fact.Window);
            return value;
        }

        public void RegisterWindow(OperationWindowDescriptorDto descriptor)
        {
            lock (gate)
            {
                var facts = OperationCommandCanonicalizer.SnapshotWindow(descriptor, gameId, epoch, membership);
                if (facts.OpenedAtGlobalSequence > lastApplied || facts.BaseWindowSequence > lastApplied) throw new ArgumentException("window_opening_not_applied");
                if (windows.TryGetValue(facts.OperationWindowId, out var existing))
                {
                    if (!OperationCommandCanonicalizer.SameWindow(existing.Facts, facts)) throw new ArgumentException("window_registration_conflict");
                    return;
                }
                if (windows.Count >= windowCapacity) throw new InvalidOperationException("window_registration_capacity");
                windows.Add(facts.OperationWindowId, new WindowLedger { Facts = facts, Sequence = facts.BaseWindowSequence, Prefix = facts.PrefixDigest });
            }
        }

        public OperationCommandReplicaResult ApplyOrdered(OrderedOperationCommandDto command)
            => ApplyOrdered(command, int.MaxValue);

        public OperationCommandReplicaResult ApplyOrdered(OrderedOperationCommandDto command, int maximumCommandsToApply)
        {
            if (maximumCommandsToApply <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCommandsToApply));
            lock (gate)
            {
                if (applying) return Reply(OperationReplicaStatus.Backpressure, "replica_apply_in_progress", "已有命令正在提交，不重复进入会话。");
                applying = true;
                try
                {
                    if (stopped) return Reply(OperationReplicaStatus.Stopped, stopCode, stopReason, failureResult);
                    if (command == null || command.GameId != gameId || command.SimulationEpoch != epoch || command.MembershipVersion != membership)
                        return Reply(OperationReplicaStatus.Rejected, "ordered_identity_mismatch", "编序记录不属于当前局、代次或成员集合。");
                    OrderedOperationCommandDto owned;
                    try
                    {
                        owned = OperationCommandCanonicalizer.SnapshotOrdered(command);
                        string digest = OperationCommandCanonicalizer.CanonicalOrderedCommandDigest(owned);
                        if (owned.OrderedCommandDigest != digest) return Stop(owned, "ordered_digest_mismatch", "编序记录摘要不一致。", null);
                    }
                    catch (ArgumentException exception) { return Stop(null, "ordered_payload_invalid", "编序记录结构或摘要无效：" + exception.Message, null); }
                    if (applied.TryGetValue(owned.GlobalCommandSequence, out var old))
                    {
                        if (old.Command.OrderedCommandDigest != owned.OrderedCommandDigest) return Stop(owned, "ordered_sequence_conflict", "已执行全局号的载荷冲突。", null);
                        var duplicate = Reply(OperationReplicaStatus.Duplicate, string.Empty, string.Empty, old.Result);
                        duplicate.Outcomes.Add(OperationCommandCanonicalizer.SnapshotOutcome(old.Outcome)); return duplicate;
                    }
                    if (owned.GlobalCommandSequence <= baseSequence) return Reply(OperationReplicaStatus.Rejected, "ordered_before_base", "记录早于当前基线，不能凭序号确认未保存的命令。");
                    if (buffered.TryGetValue(owned.GlobalCommandSequence, out var waiting))
                    {
                        if (waiting.OrderedCommandDigest != owned.OrderedCommandDigest) return Stop(owned, "ordered_sequence_conflict", "已缓存全局号的载荷冲突。", null);
                        owned = waiting;
                    }
                    if (session.State.GameId != gameId) return Stop(owned, "replica_game_changed", "会话局身份在模拟中发生变化。", null);
                    if (!windows.TryGetValue(owned.OperationWindowId, out var window) || window.Facts.WindowGeneration != owned.WindowGeneration)
                        return Reply(OperationReplicaStatus.Rejected, "window_not_registered", "编序记录没有匹配已登记窗口。");
                    if (!window.Facts.AuthorizedPlayerIds.Contains(owned.Command.PlayerId))
                        return Reply(OperationReplicaStatus.Rejected, "window_not_authorized", "编序记录玩家没有窗口授权。");
                    if (owned.ObservedGlobalSequence >= owned.GlobalCommandSequence || owned.ObservedGlobalSequence < window.Facts.OpenedAtGlobalSequence)
                        return Stop(owned, "observed_prefix_invalid", "编序记录的观察前缀无效。", null);
                    if (owned.GlobalCommandSequence != lastApplied + 1)
                    {
                        if (!buffered.ContainsKey(owned.GlobalCommandSequence))
                        {
                            if (buffered.Count >= bufferCapacity) return Reply(OperationReplicaStatus.Backpressure, "replica_buffer_capacity", "乱序缓存已满，保留已有记录并请求缺号。");
                            buffered.Add(owned.GlobalCommandSequence, owned);
                        }
                        return Reply(OperationReplicaStatus.Buffered, "ordered_gap", "缺少前序全局命令，当前记录尚未执行。");
                    }

                    var reply = Reply(OperationReplicaStatus.Applied, string.Empty, string.Empty);
                    while (owned != null)
                    {
                        if (applied.Count + confirmedReceipts.Count >= historyCapacity)
                        {
                            if (!buffered.ContainsKey(owned.GlobalCommandSequence) && buffered.Count < bufferCapacity) buffered.Add(owned.GlobalCommandSequence, owned);
                            reply.Status = OperationReplicaStatus.Backpressure; reply.ErrorCode = "replica_history_capacity";
                            reply.Reason = "已应用账本容量已满，未继续执行。"; break;
                        }
                        window = windows[owned.OperationWindowId];
                        long expectedSubmission = nextSubmission.TryGetValue(owned.Command.PlayerId, out var next) ? next : 1;
                        if (owned.WindowCommandSequence != window.Sequence + 1 || owned.ClientSubmissionSequence != expectedSubmission || commandIds.ContainsKey(owned.Command.CommandId))
                        {
                            var fault = Stop(owned, "ordered_continuity_conflict", "窗口号、玩家提案号或命令标识不连续。", null);
                            fault.Outcomes.AddRange(reply.Outcomes); return fault;
                        }
                        // 保留原 expectedRevision；只调用一次真实 Submit，不在这里重建答案或额外推进 Effect。
                        CommandResult result;
                        try { result = session.Submit(owned.Command.ToCommand()); }
                        catch (Exception exception)
                        {
                            var failure = Stop(owned, "rule_submit_exception", "规则提交出现异常：" + exception.Message, null);
                            failure.Outcomes.AddRange(reply.Outcomes); return failure;
                        }
                        var outcome = new OperationCommandOutcomeDto
                        {
                            CommandId = owned.Command.CommandId, PlayerId = owned.Command.PlayerId, GlobalCommandSequence = owned.GlobalCommandSequence,
                            OperationWindowId = owned.OperationWindowId, WindowCommandSequence = owned.WindowCommandSequence,
                            Succeeded = result.Succeeded, ErrorCode = result.Validation?.ErrorCode ?? CommandErrorCode.UnknownCommand,
                            Reason = result.Validation?.Reason ?? string.Empty, StateRevision = session.State.EffectRuntime?.StateRevision ?? 0
                        };
                        if (!result.Succeeded)
                        {
                            var failure = Stop(owned, "rule_rejected", outcome.Reason, result);
                            failure.Outcomes.AddRange(reply.Outcomes); failure.Outcomes.Add(outcome); return failure;
                        }
                        string nextPrefix = OperationCommandCanonicalizer.CanonicalLogPrefixDigest(prefix, owned);
                        applied.Add(owned.GlobalCommandSequence, new AppliedRecord { Command = owned, Result = result, Outcome = outcome, Prefix = nextPrefix });
                        commandIds.Add(owned.Command.CommandId, owned.GlobalCommandSequence); nextSubmission[owned.Command.PlayerId] = expectedSubmission + 1;
                        window.Prefix = OperationCommandCanonicalizer.CanonicalLogPrefixDigest(window.Prefix, owned);
                        window.Sequence = owned.WindowCommandSequence; lastApplied = owned.GlobalCommandSequence; prefix = nextPrefix;
                        buffered.Remove(owned.GlobalCommandSequence); reply.RuleResult = result;
                        reply.Outcomes.Add(OperationCommandCanonicalizer.SnapshotOutcome(outcome));
                        if (viewer.HasValue)
                        {
                            try { session.RefreshLocalProjection(viewer.Value); }
                            catch (Exception exception)
                            {
                                var failure = Stop(owned, "projection_failed", "已提交后的投影刷新失败：" + exception.Message, result);
                                failure.Outcomes.AddRange(reply.Outcomes); return failure;
                            }
                        }
                        owned = reply.Outcomes.Count < maximumCommandsToApply && buffered.TryGetValue(lastApplied + 1, out var following) ? following : null;
                    }
                    reply.ExpectedGlobalSequence = lastApplied + 1; return reply;
                }
                finally { applying = false; }
            }
        }

        public CommandValidationPrefixDto CreateValidationPrefix(long afterSequence = 0)
            => CreateValidationPrefix(afterSequence, LastAppliedSequence);

        public CommandValidationPrefixDto CreateValidationPrefix(long afterSequence, long throughSequence)
        {
            lock (gate)
            {
                if (afterSequence < 0 || afterSequence > throughSequence || throughSequence < baseSequence || throughSequence > lastApplied)
                    throw new ArgumentOutOfRangeException(nameof(throughSequence));
                if (!TryReadAppliedPrefix(throughSequence, out var throughPrefix)) throw new ArgumentException("confirmed_prefix_missing");
                return new CommandValidationPrefixDto { GameId = gameId, SimulationEpoch = epoch, MembershipVersion = membership,
                    ValidatedThroughSequence = throughSequence, PrefixDigest = throughPrefix,
                    Outcomes = applied.Where(pair => pair.Key > afterSequence && pair.Key <= throughSequence)
                        .Select(pair => OperationCommandCanonicalizer.SnapshotOutcome(pair.Value.Outcome)).ToList() };
            }
        }

        public List<SimulationConfirmedCommandReceiptDto> ReadConfirmedReceipts(long throughSequence)
        {
            lock (gate)
            {
                if (throughSequence < baseSequence || throughSequence > lastApplied) throw new ArgumentOutOfRangeException(nameof(throughSequence));
                var result = confirmedReceipts.Select(OperationCommandCanonicalizer.SnapshotConfirmedReceipt).ToList();
                result.AddRange(applied.Where(pair => pair.Key <= throughSequence).Select(pair => new SimulationConfirmedCommandReceiptDto {
                    SourceSimulationEpoch = pair.Value.Command.SimulationEpoch, ClientSubmissionSequence = pair.Value.Command.ClientSubmissionSequence,
                    ProposalDigest = pair.Value.Command.ProposalDigest, OrderedDigest = pair.Value.Command.OrderedCommandDigest,
                    Outcome = OperationCommandCanonicalizer.SnapshotOutcome(pair.Value.Outcome) }));
                return result;
            }
        }

        public CommandResult GetAppliedRuleResult(long globalSequence)
        {
            lock (gate) return applied.TryGetValue(globalSequence, out var record) ? record.Result : null;
        }

        public OrderedOperationCommandDto GetAppliedCommand(long globalSequence)
        { lock (gate) return applied.TryGetValue(globalSequence, out var record) ? OperationCommandCanonicalizer.SnapshotOrdered(record.Command) : null; }

        public bool TryReadAppliedPrefix(long globalSequence, out string digest)
        {
            lock (gate)
            {
                digest = globalSequence == baseSequence ? basePrefix : applied.TryGetValue(globalSequence, out var record) ? record.Prefix : null;
                return digest != null;
            }
        }

        public OperationWindowCommandProgressDto ReadWindowProgress(string windowId)
        {
            lock (gate)
            {
                if (!windows.TryGetValue(windowId, out var window)) throw new ArgumentException("window_not_registered");
                return new OperationWindowCommandProgressDto { OperationWindowId = windowId, WindowGeneration = window.Facts.WindowGeneration,
                    LastWindowCommandSequence = window.Sequence, CommandCount = window.Sequence, PrefixDigest = window.Prefix };
            }
        }

        private OperationCommandReplicaResult Stop(OrderedOperationCommandDto command, string code, string reason, CommandResult result)
        {
            stopped = true; stopCode = code; stopReason = reason; failureResult = result;
            failureCommand = command == null ? null : OperationCommandCanonicalizer.SnapshotOrdered(command);
            return Reply(OperationReplicaStatus.Stopped, code, reason, result);
        }

        private OperationCommandReplicaResult Reply(OperationReplicaStatus status, string code, string reason, CommandResult result = null) =>
            new OperationCommandReplicaResult { Status = status, ErrorCode = code, Reason = reason, ExpectedGlobalSequence = lastApplied + 1, RuleResult = result };
    }
}
