using System;
using System.Collections.Generic;
using System.Linq;

namespace YC.Application.Sessions
{
    /// <summary>有界单写者日志；来源玩家身份须先由运输验证，不调用任何业务会话。</summary>
    public sealed class OrderedCommandSequencer
    {
        private sealed class WindowLedger
        {
            internal OperationWindowDescriptorDto Facts;
            internal long Sequence;
            internal string Prefix;
        }
        private readonly object gate = new object();
        private readonly string gameId;
        private readonly long epoch, baseSequence;
        private readonly int membership, capacity, windowCapacity;
        private readonly Dictionary<string, WindowLedger> windows = new Dictionary<string, WindowLedger>(StringComparer.Ordinal);
        private readonly Dictionary<int, long> nextSubmission = new Dictionary<int, long>();
        private readonly Dictionary<int, Dictionary<long, OrderedOperationCommandDto>> submissions = new Dictionary<int, Dictionary<long, OrderedOperationCommandDto>>();
        private readonly Dictionary<string, OrderedOperationCommandDto> commandIds = new Dictionary<string, OrderedOperationCommandDto>(StringComparer.Ordinal);
        private readonly List<OrderedOperationCommandDto> log = new List<OrderedOperationCommandDto>();
        private readonly List<string> prefixes = new List<string>();
        private readonly HashSet<string> confirmedCommandIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly string basePrefix;
        private long lastSequence;
        private string prefix;
        private string windowExecutionGrant;

        internal bool TryReserveWindowExecution(string grantId, long expectedSequence, string expectedPrefix)
        {
            lock (gate)
            {
                if (string.IsNullOrWhiteSpace(grantId) || windowExecutionGrant != null || lastSequence != expectedSequence || prefix != expectedPrefix) return false;
                windowExecutionGrant = grantId; return true;
            }
        }
        internal void ReleaseWindowExecution(string grantId)
        { lock (gate) { if (windowExecutionGrant != grantId) throw new InvalidOperationException("window_execution_reservation_mismatch"); windowExecutionGrant = null; } }

        public OrderedCommandSequencer(string gameId, long simulationEpoch, int membershipVersion,
            int maximumLogCommands = 4096, long baseGlobalSequence = 0, string baseCommandPrefixDigest = null, int maximumRegisteredWindows = 256)
        {
            OperationCommandCanonicalizer.ValidateIdentity(gameId, simulationEpoch, membershipVersion);
            if (maximumLogCommands <= 0 || maximumRegisteredWindows <= 0 || baseGlobalSequence < 0 || baseGlobalSequence >= long.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(maximumLogCommands));
            if (baseGlobalSequence != 0 && string.IsNullOrEmpty(baseCommandPrefixDigest)) throw new ArgumentException("global_base_prefix_missing");
            this.gameId = gameId; epoch = simulationEpoch; membership = membershipVersion; capacity = maximumLogCommands;
            windowCapacity = maximumRegisteredWindows;
            baseSequence = lastSequence = baseGlobalSequence;
            basePrefix = prefix = OperationCommandCanonicalizer.NormalizeDigest(baseCommandPrefixDigest ?? OperationCommandCanonicalizer.EmptyPrefixDigest);
            if (baseGlobalSequence == 0 && basePrefix != OperationCommandCanonicalizer.EmptyPrefixDigest) throw new ArgumentException("global_base_prefix_conflict");
        }

        public long LastSequencedSequence { get { lock (gate) return lastSequence; } }
        public string PrefixDigest { get { lock (gate) return prefix; } }

        public static OrderedCommandSequencer FromConfirmedRecovery(DeterministicSimulationSessionOptions options, SimulationRecoveryBaselineDto baseline)
        {
            OperationCommandCanonicalizer.ValidateRecoveryBaseline(options, baseline);
            var value = new OrderedCommandSequencer(options.GameId, options.SimulationEpoch, options.MembershipVersion,
                options.MaximumLogCommands, baseline.ConfirmedGlobalSequence, baseline.CommandPrefixDigest, options.MaximumRegisteredWindows);
            foreach (var pair in baseline.NextSubmissionSequences) value.nextSubmission.Add(pair.Key, pair.Value);
            foreach (var receipt in baseline.ConfirmedCommands) value.confirmedCommandIds.Add(receipt.Outcome.CommandId);
            foreach (var fact in baseline.Windows) value.RegisterWindow(fact.Window);
            return value;
        }

        public long ReadNextSubmissionSequence(int playerId)
        { lock (gate) return nextSubmission.TryGetValue(playerId, out var next) ? next : 1; }

        // 已入账身份先于当前窗口/前缀资格判断；关闭窗口的重传仍返回同一原记录。
        public bool TryReadAdmittedProposal(int verifiedSourcePlayerId, OperationCommandProposalDto proposal, out OperationCommandAdmissionResult result)
        {
            lock (gate)
            {
                result = null;
                OperationCommandProposalDto owned;
                string digest;
                try { owned = OperationCommandCanonicalizer.SnapshotProposal(proposal); digest = OperationCommandCanonicalizer.CanonicalProposalDigest(owned); }
                catch (ArgumentException) { return false; }
                if (owned.GameId != gameId || owned.SimulationEpoch != epoch || owned.MembershipVersion != membership || owned.Command.PlayerId != verifiedSourcePlayerId) return false;
                long expected = ReadNextSubmissionSequence(verifiedSourcePlayerId);
                if (submissions.TryGetValue(verifiedSourcePlayerId, out var history) && history.TryGetValue(owned.ClientSubmissionSequence, out var previous))
                {
                    result = previous.ProposalDigest == digest ? new OperationCommandAdmissionResult { Status = OperationAdmissionStatus.Duplicate,
                        OrderedCommand = OperationCommandCanonicalizer.SnapshotOrdered(previous), ExpectedClientSubmissionSequence = expected } :
                        Reject("submission_conflict", "同一提案号的载荷发生冲突。", expected);
                    return true;
                }
                if (confirmedCommandIds.Contains(owned.Command.CommandId)) { result = Reject("command_already_confirmed", "该命令已经确认，不能重复提交。", expected); return true; }
                if (commandIds.ContainsKey(owned.Command.CommandId)) { result = Reject("command_id_conflict", "同一命令标识被用于不同提案。", expected); return true; }
                return false;
            }
        }

        // 返回隔离副本；首次登记的开启位置/基线属于合同，不能由后续首次使用时间重建。
        public bool TryReadRegisteredWindow(string operationWindowId, out OperationWindowDescriptorDto descriptor)
        {
            lock (gate)
            {
                descriptor = null;
                if (string.IsNullOrEmpty(operationWindowId) || !windows.TryGetValue(operationWindowId, out var existing)) return false;
                descriptor = OperationCommandCanonicalizer.SnapshotWindow(existing.Facts, gameId, epoch, membership);
                return true;
            }
        }

        public void RegisterWindow(OperationWindowDescriptorDto descriptor)
        {
            lock (gate)
            {
                var facts = OperationCommandCanonicalizer.SnapshotWindow(descriptor, gameId, epoch, membership);
                if (facts.OpenedAtGlobalSequence > lastSequence || facts.BaseWindowSequence > lastSequence) throw new ArgumentException("window_opening_not_sequenced");
                if (windows.TryGetValue(facts.OperationWindowId, out var existing))
                {
                    if (!OperationCommandCanonicalizer.SameWindow(existing.Facts, facts)) throw new ArgumentException("window_registration_conflict");
                    return;
                }
                if (windows.Count >= windowCapacity) throw new InvalidOperationException("window_registration_capacity");
                windows.Add(facts.OperationWindowId, new WindowLedger { Facts = facts, Sequence = facts.BaseWindowSequence, Prefix = facts.PrefixDigest });
            }
        }

        public OperationCommandAdmissionResult AdmitProposal(int verifiedSourcePlayerId, OperationCommandProposalDto proposal)
            => AdmitProposalCore(verifiedSourcePlayerId, proposal, null);

        internal OperationCommandAdmissionResult AdmitWindowExecutionProposal(string grantId, int verifiedSourcePlayerId, OperationCommandProposalDto proposal)
            => AdmitProposalCore(verifiedSourcePlayerId, proposal, grantId);

        private OperationCommandAdmissionResult AdmitProposalCore(int verifiedSourcePlayerId, OperationCommandProposalDto proposal, string grantId)
        {
            lock (gate)
            {
                OperationCommandProposalDto owned;
                string digest;
                try { owned = OperationCommandCanonicalizer.SnapshotProposal(proposal); digest = OperationCommandCanonicalizer.CanonicalProposalDigest(owned); }
                catch (ArgumentException exception) { return Reject("proposal_invalid", "命令提案结构或编码无效：" + exception.Message); }
                if (owned.GameId != gameId || owned.SimulationEpoch != epoch || owned.MembershipVersion != membership)
                    return Reject("proposal_identity_mismatch", "命令提案不属于当前局、模拟代次或成员集合。");
                if (verifiedSourcePlayerId <= 0 || owned.Command.PlayerId != verifiedSourcePlayerId)
                    return Reject("proposal_source_mismatch", "命令玩家与运输已验证来源不一致。");
                if (windowExecutionGrant != grantId)
                    return Reject("window_execution_reserved", "本人窗口正在连续执行，等待实际边界汇合。");
                if (!windows.TryGetValue(owned.OperationWindowId, out var window) || window.Facts.WindowGeneration != owned.WindowGeneration)
                    return Reject("window_not_registered", "命令没有匹配已登记窗口。");
                if (!window.Facts.AuthorizedPlayerIds.Contains(verifiedSourcePlayerId)) return Reject("window_not_authorized", "该玩家无此窗口操作授权。");
                long expected = nextSubmission.TryGetValue(verifiedSourcePlayerId, out var next) ? next : 1;
                if (submissions.TryGetValue(verifiedSourcePlayerId, out var history) && history.TryGetValue(owned.ClientSubmissionSequence, out var previous))
                {
                    if (previous.ProposalDigest != digest) return Reject("submission_conflict", "同一提案号的载荷发生冲突。", expected);
                    return new OperationCommandAdmissionResult { Status = OperationAdmissionStatus.Duplicate,
                        ExpectedClientSubmissionSequence = expected, OrderedCommand = OperationCommandCanonicalizer.SnapshotOrdered(previous) };
                }
                if (confirmedCommandIds.Contains(owned.Command.CommandId)) return Reject("command_already_confirmed", "该命令已经确认，不能重复提交。", expected);
                if (commandIds.ContainsKey(owned.Command.CommandId)) return Reject("command_id_conflict", "同一命令标识被用于不同提案。", expected);
                if (owned.ClientSubmissionSequence > expected)
                    return new OperationCommandAdmissionResult { Status = OperationAdmissionStatus.MissingSubmission, ErrorCode = "submission_gap",
                        Reason = "缺少该玩家前序提案，尚未分配业务序号。", ExpectedClientSubmissionSequence = expected };
                if (owned.ClientSubmissionSequence < expected) return Reject("submission_before_base", "提案号早于已知连续前缀，无法确认。", expected);
                if (owned.ObservedGlobalSequence > lastSequence || owned.ObservedGlobalSequence < window.Facts.OpenedAtGlobalSequence)
                    return Reject("observed_prefix_invalid", "提案观察前缀与已公布窗口不一致。", expected);
                if (log.Count + confirmedCommandIds.Count >= capacity || lastSequence >= long.MaxValue - 1 || window.Sequence >= long.MaxValue - 1)
                    return new OperationCommandAdmissionResult { Status = OperationAdmissionStatus.Backpressure, ErrorCode = "sequencer_capacity",
                        Reason = "编序日志或序号容量已满，保留原日志并暂停受理。", ExpectedClientSubmissionSequence = expected };

                var ordered = new OrderedOperationCommandDto(); OperationCommandCanonicalizer.CopyProposal(owned, ordered);
                ordered.GlobalCommandSequence = lastSequence + 1; ordered.WindowCommandSequence = window.Sequence + 1;
                ordered.ProposalDigest = digest; ordered.OrderedCommandDigest = OperationCommandCanonicalizer.CanonicalOrderedCommandDigest(ordered);
                string nextPrefix = OperationCommandCanonicalizer.CanonicalLogPrefixDigest(prefix, ordered);
                string nextWindowPrefix = OperationCommandCanonicalizer.CanonicalLogPrefixDigest(window.Prefix, ordered);
                if (history == null) { history = new Dictionary<long, OrderedOperationCommandDto>(); submissions.Add(verifiedSourcePlayerId, history); }
                log.Add(ordered); prefixes.Add(nextPrefix); commandIds.Add(ordered.Command.CommandId, ordered);
                history.Add(owned.ClientSubmissionSequence, ordered); nextSubmission[verifiedSourcePlayerId] = expected + 1;
                lastSequence = ordered.GlobalCommandSequence; prefix = nextPrefix;
                window.Sequence = ordered.WindowCommandSequence; window.Prefix = nextWindowPrefix;
                return new OperationCommandAdmissionResult { Status = OperationAdmissionStatus.Accepted, ExpectedClientSubmissionSequence = expected + 1,
                    OrderedCommand = OperationCommandCanonicalizer.SnapshotOrdered(ordered) };
            }
        }

        public OperationCommandRangeDto ReadRange(long fromSequence, int maximumCount)
        {
            lock (gate)
            {
                if (maximumCount <= 0 || maximumCount > capacity) throw new ArgumentOutOfRangeException(nameof(maximumCount));
                var result = new OperationCommandRangeDto { GameId = gameId, SimulationEpoch = epoch, MembershipVersion = membership,
                    BaseGlobalSequence = baseSequence, FromSequence = fromSequence, ThroughSequence = baseSequence, PrefixDigest = basePrefix };
                if (fromSequence <= baseSequence) { result.ErrorCode = "range_before_base"; return result; }
                if (fromSequence > lastSequence) { result.ErrorCode = "range_not_available"; return result; }
                int offset = checked((int)(fromSequence - baseSequence - 1));
                int count = Math.Min(maximumCount, log.Count - offset);
                for (int index = offset; index < offset + count; index++) result.Commands.Add(OperationCommandCanonicalizer.SnapshotOrdered(log[index]));
                result.ThroughSequence = result.Commands[result.Commands.Count - 1].GlobalCommandSequence;
                result.PrefixDigest = prefixes[offset + count - 1]; return result;
            }
        }

        public OperationWindowCommandProgressDto ReadWindowProgress(string operationWindowId)
        {
            lock (gate)
            {
                if (!windows.TryGetValue(operationWindowId, out var window)) throw new ArgumentException("window_not_registered");
                return new OperationWindowCommandProgressDto { OperationWindowId = operationWindowId, WindowGeneration = window.Facts.WindowGeneration,
                    LastWindowCommandSequence = window.Sequence, CommandCount = window.Sequence, PrefixDigest = window.Prefix };
            }
        }

        private static OperationCommandAdmissionResult Reject(string code, string reason, long expected = 0) =>
            new OperationCommandAdmissionResult { Status = OperationAdmissionStatus.Rejected, ErrorCode = code, Reason = reason,
                ExpectedClientSubmissionSequence = expected };
    }
}
