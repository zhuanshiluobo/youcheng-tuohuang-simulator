using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    // v2窗口通道。只能在原执行owner线程调用；不新增游戏预校验。
    [Serializable]
    public sealed class WindowExecutionGrant
    {
        public const int Version = 2;
        public int ProtocolVersion = Version;
        public string GameId, ContentPackHash, RootWindowId, GrantId;
        public long SimulationEpoch, GrantSerial, BaseGlobalSequence, NextClientSubmission;
        public int MembershipVersion, OwnerPlayerId, RootGeneration;
        public string BasePrefixDigest, BaseRuleDigest;
        public List<string> RequiredReplicaIds = new List<string>();
        public List<int> FrozenPlayerIds = new List<int>();
        public List<SimulationWindowFactDto> BaseWindows = new List<SimulationWindowFactDto>();
    }

    [Serializable]
    public sealed class WindowExecutionRecord
    {
        public string GrantId, PreviousRecordDigest, RecordDigest;
        public long OperationSequence;
        public OrderedOperationCommandDto Ordered;
        public int ResultRevision;
        public string BoundaryReason = "";
    }

    [Serializable]
    public sealed class WindowExecutionSeal
    {
        public string GrantId, PrefixDigest, RuleDigest, WindowProgressDigest, Reason, SealDigest;
        public long OperationCount, GlobalSequence;
    }

    [Serializable]
    public sealed class WindowExecutionConfirmation
    {
        public WindowExecutionSeal Seal;
        public List<string> ReadyReplicaIds = new List<string>();
    }

    public sealed class WindowExecutionResult
    {
        public string Status, Reason = "";
        public CommandResult RuleResult;
        public WindowExecutionRecord Record;
    }

    /// <summary>协议规范编码；沿用原命令摘要，不改v1存档或原请求合同。</summary>
    public static class WindowExecutionCodec
    {
        public static T Copy<T>(T value) where T : class => GameStateCloneService.DeepClone(value);
        public static string Hash(params string[] fields)
        {
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true), true))
            {
                foreach (string field in fields)
                {
                    if (field == null) throw new ArgumentException("window_stream_null_field");
                    var data = new UTF8Encoding(false, true).GetBytes(field);
                    if (data.Length > 1024 * 1024 || bytes.Length + data.Length > 8 * 1024 * 1024)
                        throw new ArgumentException("window_stream_payload_capacity");
                    writer.Write(data.Length); writer.Write(data);
                }
                writer.Flush();
                using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        public static string Number(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static string GrantDigest(WindowExecutionGrant g) => Hash("yc.window-execution.grant.v2", Number(g.ProtocolVersion), g.GameId, g.ContentPackHash,
            Number(g.SimulationEpoch), Number(g.MembershipVersion), Number(g.GrantSerial), Number(g.OwnerPlayerId), g.RootWindowId,
            Number(g.RootGeneration), Number(g.BaseGlobalSequence), Number(g.NextClientSubmission), g.BasePrefixDigest, g.BaseRuleDigest,
            Hash(g.RequiredReplicaIds.OrderBy(x => x, StringComparer.Ordinal).ToArray()), Hash(g.FrozenPlayerIds.OrderBy(x => x).Select(x => Number(x)).ToArray()),
            Hash(g.BaseWindows.OrderBy(w => w.Window.OperationWindowId, StringComparer.Ordinal).Select(w => Hash(w.Window.OperationWindowId,
                Number(w.Window.ProtocolVersion), w.Window.DigestVersion, w.Window.GameId, Number(w.Window.SimulationEpoch), Number(w.Window.MembershipVersion),
                Number(w.Window.WindowGeneration), Number(w.Window.OpenedAtGlobalSequence), Number(w.Window.BaseWindowSequence), w.Window.PrefixDigest,
                Hash(w.Window.AuthorizedPlayerIds.OrderBy(x => x).Select(x => Number(x)).ToArray()), Hash(w.Window.RequiredReplicaIds.OrderBy(x => x, StringComparer.Ordinal).ToArray()),
                w.Window.WindowContractDigest, Number(w.FinalCommandSequence), Number(w.CommandCount), w.WindowPrefixDigest,
                w.IsClosed ? "closed" : "open", Hash(w.AvailablePlayerIds.OrderBy(x => x).Select(x => Number(x)).ToArray()))).ToArray()));
        public static string RecordDigest(WindowExecutionRecord r) => Hash("yc.window-execution.record.v2", r.GrantId, Number(r.OperationSequence),
            r.PreviousRecordDigest, OperationCommandCanonicalizer.CanonicalOrderedCommandDigest(r.Ordered), Number(r.ResultRevision), r.BoundaryReason);
        public static string SealDigest(WindowExecutionSeal s) => Hash("yc.window-execution.seal.v2", s.GrantId, Number(s.OperationCount), Number(s.GlobalSequence),
            s.PrefixDigest, s.RuleDigest, s.WindowProgressDigest, s.Reason);

        // 调用者必须传入原协调器实际产生且运输已认证的permit；不能由客户端自造许可。
        public static WindowExecutionGrant CreateGrant(GameSession session, DeterministicSimulationSessionOptions options,
            SimulationWindowPermitDto permit, int owner, string rootWindowId, long grantSerial, long nextClientSubmission)
        {
            var b = permit?.Boundary;
            if (!options.IsHost || b == null || grantSerial < 1 || nextClientSubmission < 1 ||
                permit.GameId != options.GameId || permit.SimulationEpoch != options.SimulationEpoch || permit.MembershipVersion != options.MembershipVersion ||
                b.GameId != options.GameId || b.SimulationEpoch != options.SimulationEpoch || b.MembershipVersion != options.MembershipVersion ||
                permit.HostValidatedThroughSequence != b.GlobalFenceSequence || permit.HostValidationPrefixDigest != b.CommandPrefixDigest ||
                b.RuleStateDigestVersion != RuleStateDigestService.DigestVersion || b.FactDigest != OperationCommandCanonicalizer.CanonicalBoundaryDigest(b) ||
                !Same(permit.ReadyReplicaIds, options.RequiredReplicaIds) || b.RuleStateDigest != RuleStateDigestService.Compute(session.State))
                throw new ArgumentException("grant_requires_actual_confirmed_boundary");
            var open = OperationWindowService.ReadOpenWindows(session.State);
            var root = open.SingleOrDefault(w => w.OperationWindowId == rootWindowId);
            if (root == null || root.AuthorizedPlayerIds.Count != 1 || root.AuthorizedPlayerIds[0] != owner || !root.AvailablePlayerIds.Contains(owner) ||
                root.WindowKind != RoundMainlineNodeTypeIds.PlayerActionWindow && root.WindowKind != RoundMainlineNodeTypeIds.PlayerCleanupWindow ||
                open.SelectMany(w => w.AvailablePlayerIds).Any(p => p != owner))
                throw new ArgumentException("grant_requires_exclusive_main_writer");
            var value = new WindowExecutionGrant { GameId = options.GameId, ContentPackHash = options.ContentPackHash,
                SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion, GrantSerial = grantSerial,
                RootWindowId = rootWindowId, RootGeneration = root.WindowGeneration, OwnerPlayerId = owner, BaseGlobalSequence = b.GlobalFenceSequence,
                NextClientSubmission = nextClientSubmission, BasePrefixDigest = b.CommandPrefixDigest, BaseRuleDigest = b.RuleStateDigest,
                RequiredReplicaIds = new List<string>(options.RequiredReplicaIds), FrozenPlayerIds = new List<int>(options.FrozenPlayerIds),
                BaseWindows = Copy(b.Windows) };
            value.GrantId = GrantDigest(value); return value;
        }
        internal static bool Same(IEnumerable<string> a, IEnumerable<string> b) => a != null && b != null &&
            a.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(b.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// 已获独占写流许可的真实规则执行与复制。ExecuteLocal不访问Host、不等消息；每条仅调用一次原GameSession.Submit。
    /// 原协调器必须在授予存续期间挡住其它写入；多writer不能用本类冒充已解决的并发合并。
    /// </summary>
    public sealed class WindowLocalExecution
    {
        private readonly GameSession session;
        private readonly WindowExecutionGrant grant;
        private readonly int localPlayer;
        private readonly bool isHost;
        private readonly OrderedCommandSequencer hostSequencer;
        private readonly Func<string, int, OperationCommandProposalDto, OperationCommandAdmissionResult> hostAdmission;
        private readonly int capacity;
        private readonly SortedDictionary<long, WindowExecutionRecord> records = new SortedDictionary<long, WindowExecutionRecord>();
        private readonly SortedDictionary<long, WindowExecutionRecord> pending = new SortedDictionary<long, WindowExecutionRecord>();
        private readonly Dictionary<string, long> windowCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> windowPrefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> confirmedIds;
        private readonly Dictionary<string, string> ready = new Dictionary<string, string>(StringComparer.Ordinal);
        private WindowExecutionSeal seal;
        private WindowExecutionSeal confirmedSeal;
        public bool HasConfirmedSeal => confirmedSeal != null;
        private bool busy, connected = true;
        private string previousRecord, prefix, boundaryReason = "";
        private GameState confirmedState;
        private readonly bool retainStandaloneCheckpoint;
        private OrderedOperationCommandDto attemptedCommand;
        public OrderedOperationCommandDto FailureCommand => FirstFault == null || attemptedCommand == null ? null : WindowExecutionCodec.Copy(attemptedCommand);
        public long Tail { get; private set; }
        public long ConfirmedGlobalSequence { get; private set; }
        public long GlobalSequence => grant.BaseGlobalSequence + Tail;
        public string FirstFault { get; private set; }
        public object FirstFaultOperation { get; private set; }
        public object FirstRejectedMessage { get; private set; }
        public GameState FaultState { get; private set; }
        public bool IsStopped => FirstFault != null;
        public bool NeedsBoundary => boundaryReason != "" || seal != null;
        public bool CanExecuteLocal => !busy && connected && !IsStopped && !NeedsBoundary && localPlayer == grant.OwnerPlayerId;
        public int ActualRuleExecutions { get; private set; }
        public int FullRuleDigestComputations { get; private set; }
        public string PendingBoundaryReason => boundaryReason;
        public event Action<WindowExecutionRecord, CommandResult> CommandExecuted;

        public WindowLocalExecution(GameSession session, WindowExecutionGrant value, int localPlayer, bool isHost,
            OrderedCommandSequencer hostSequencer = null, IEnumerable<string> alreadyConfirmedCommandIds = null, int capacity = 4096,
            Func<string, int, OperationCommandProposalDto, OperationCommandAdmissionResult> hostAdmission = null, bool retainStandaloneCheckpoint = true)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session)); grant = WindowExecutionCodec.Copy(value);
            this.localPlayer = localPlayer; this.isHost = isHost; this.hostSequencer = hostSequencer; this.capacity = capacity;
            this.hostAdmission = hostAdmission;
            this.retainStandaloneCheckpoint = retainStandaloneCheckpoint;
            if (capacity < 1 || capacity > 4096 || grant == null || grant.ProtocolVersion != WindowExecutionGrant.Version ||
                grant.GrantId != WindowExecutionCodec.GrantDigest(grant) || !grant.FrozenPlayerIds.Contains(localPlayer) ||
                !grant.FrozenPlayerIds.Contains(grant.OwnerPlayerId) || grant.FrozenPlayerIds.Distinct().Count() != grant.FrozenPlayerIds.Count ||
                !WindowExecutionCodec.Same(grant.RequiredReplicaIds, grant.FrozenPlayerIds.Select(DeterministicSimulationSessionOptions.ReplicaIdForPlayer)) ||
                grant.SimulationEpoch < 1 || grant.MembershipVersion < 1 || grant.GrantSerial < 1 || grant.BaseGlobalSequence < 0 ||
                grant.NextClientSubmission < 1 || grant.BaseGlobalSequence > long.MaxValue - capacity - 1 || grant.NextClientSubmission > long.MaxValue - capacity - 1 ||
                session.State.GameId != grant.GameId || session.State.ContentPackHash != grant.ContentPackHash ||
                !session.State.Players.Select(p => p.PlayerId).OrderBy(x => x).SequenceEqual(grant.FrozenPlayerIds.OrderBy(x => x)) ||
                grant.BaseRuleDigest != Digest() || isHost && hostSequencer == null)
                throw new ArgumentException("window_execution_identity_or_baseline_invalid");
            if (isHost && (hostSequencer.LastSequencedSequence != grant.BaseGlobalSequence || hostSequencer.PrefixDigest != grant.BasePrefixDigest ||
                hostSequencer.ReadNextSubmissionSequence(grant.OwnerPlayerId) != grant.NextClientSubmission))
                throw new ArgumentException("host_ledger_not_at_grant_base");
            var root = OperationWindowService.ReadOpenWindows(session.State).SingleOrDefault(w => w.OperationWindowId == grant.RootWindowId && w.WindowGeneration == grant.RootGeneration);
            if (root == null || root.AuthorizedPlayerIds.Count != 1 || root.AuthorizedPlayerIds[0] != grant.OwnerPlayerId ||
                root.WindowKind != RoundMainlineNodeTypeIds.PlayerActionWindow && root.WindowKind != RoundMainlineNodeTypeIds.PlayerCleanupWindow ||
                OperationWindowService.ReadOpenWindows(session.State).SelectMany(w => w.AvailablePlayerIds).Any(p => p != grant.OwnerPlayerId))
                throw new ArgumentException("grant_actual_writer_mismatch");
            foreach (var w in grant.BaseWindows) { windowCounts.Add(w.Window.OperationWindowId, w.FinalCommandSequence); windowPrefixes.Add(w.Window.OperationWindowId, w.WindowPrefixDigest); }
            confirmedIds = new HashSet<string>(alreadyConfirmedCommandIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            previousRecord = grant.GrantId; prefix = grant.BasePrefixDigest;
            confirmedState = retainStandaloneCheckpoint ? GameStateCloneService.DeepClone(session.State) : null; ConfirmedGlobalSequence = grant.BaseGlobalSequence;
        }

        // 当前UI生成的原命令，保持其revision/候选/费用参数。失败事务不耗序号、不发送。
        public WindowExecutionResult ExecuteLocal(GameCommand command)
        {
            if (!CanExecuteLocal) return Result("blocked", IsStopped ? FirstFault : "local_busy_disconnected_or_boundary");
            if (command == null || command.PlayerId != localPlayer) return Result("rejected", "only_granted_local_actor");
            if (confirmedIds.Contains(command.CommandId)) return Result("rejected", "command_already_confirmed");
            var prior = records.Values.FirstOrDefault(r => r.Ordered.Command.CommandId == command.CommandId);
            if (prior != null)
            {
                var retry = WindowExecutionCodec.Copy(prior.Ordered); retry.Command = GameCommandDto.FromCommand(command);
                if (OperationCommandCanonicalizer.CanonicalProposalDigest(retry) != prior.Ordered.ProposalDigest) return Stop("command_id_conflict");
                return new WindowExecutionResult { Status = "duplicate", Record = WindowExecutionCodec.Copy(prior) };
            }
            if (records.Count >= capacity) return Result("backpressure", "retained_window_log_capacity_yield_required");
            busy = true;
            try
            {
                if (!TryContext(command, out var context, out string why)) return Result("rejected", why);
                var ordered = MakeOrdered(command, context);
                attemptedCommand = WindowExecutionCodec.Copy(ordered);
                // 唯一业务规则入口：这里没有网络批准、全状态预执行或第二套effect校验。
                ActualRuleExecutions++;
                var rule = session.Submit(ordered.Command.ToCommand());
                if (!rule.Succeeded)
                {
                    if (rule.Validation.ErrorCode == CommandErrorCode.UnknownCommand)
                    { FirstFaultOperation = GameCommandDto.FromCommand(command); return Stop("local_transaction_failed:" + rule.Validation.Reason); }
                    return new WindowExecutionResult { Status = "rule_rejected", RuleResult = rule, Reason = rule.Validation.Reason };
                }
                if (session.State.EffectRuntime.Status == EffectRuntimeStatus.PausedFault) return Stop("local_rule_fault_preserved");
                var record = new WindowExecutionRecord { GrantId = grant.GrantId, OperationSequence = Tail + 1, Ordered = ordered,
                    PreviousRecordDigest = previousRecord, ResultRevision = session.State.EffectRuntime.StateRevision, BoundaryReason = DetectBoundary() };
                record.RecordDigest = WindowExecutionCodec.RecordDigest(record);
                if (isHost && !AdmitAtHost(record)) return Stop("local_host_ledger_conflict_after_rule_execution");
                CommitRecord(record, rule);
                return new WindowExecutionResult { Status = "applied_locally", RuleResult = rule, Record = WindowExecutionCodec.Copy(record) };
            }
            catch (Exception ex) { return Stop("local_exception:" + ex.GetType().Name + ":" + ex.Message); }
            finally { busy = false; }
        }

        // Host只收已认证owner的原件；副本只收已认证Host广播。回显先查原件，不再Submit。
        public WindowExecutionResult Receive(int verifiedSourcePlayer, bool authenticatedHost, WindowExecutionRecord source)
        {
            if (IsStopped) return Result("stopped", FirstFault);
            if (!connected) return Result("blocked", "disconnected_preserve_original_tail");
            bool replayStarted = false;
            try
            {
                if (source == null || source.GrantId != grant.GrantId || (isHost ? verifiedSourcePlayer != grant.OwnerPlayerId : !authenticatedHost) ||
                    source.Ordered == null || source.Ordered.GameId != grant.GameId || source.Ordered.SimulationEpoch != grant.SimulationEpoch ||
                    source.Ordered.MembershipVersion != grant.MembershipVersion || source.Ordered.Command.PlayerId != grant.OwnerPlayerId)
                    return Reject(source, "record_identity_or_transport_source");
                var r = WindowExecutionCodec.Copy(source);
                if (r.OperationSequence < 1 || r.OperationSequence > capacity || r.RecordDigest != WindowExecutionCodec.RecordDigest(r) ||
                    r.Ordered.OrderedCommandDigest != OperationCommandCanonicalizer.CanonicalOrderedCommandDigest(r.Ordered) ||
                    r.Ordered.GlobalCommandSequence != grant.BaseGlobalSequence + r.OperationSequence ||
                    r.Ordered.ClientSubmissionSequence != grant.NextClientSubmission + r.OperationSequence - 1 ||
                    r.Ordered.ObservedGlobalSequence != r.Ordered.GlobalCommandSequence - 1)
                    return Reject(source, "record_shape_or_digest");
                if (records.TryGetValue(r.OperationSequence, out var old))
                    return old.RecordDigest == r.RecordDigest ? Result("duplicate") : Stop("same_sequence_different_payload", r);
                if (pending.TryGetValue(r.OperationSequence, out old))
                    return old.RecordDigest == r.RecordDigest ? Result("duplicate_buffered") : Stop("same_buffered_sequence_different_payload", r);
                if (r.OperationSequence <= Tail) return Stop("record_before_retained_tail");
                if (seal != null && r.OperationSequence > seal.OperationCount) return Stop("record_after_sealed_tail");
                if (records.Count + pending.Count >= capacity) return Result("backpressure", "record_buffer_capacity");
                pending.Add(r.OperationSequence, r);
                while (pending.TryGetValue(Tail + 1, out var next))
                {
                    if (seal != null && next.OperationSequence > seal.OperationCount) return Stop("buffered_record_after_sealed_tail", next);
                    if (boundaryReason != "") return Stop("command_after_real_boundary");
                    if (next.PreviousRecordDigest != previousRecord || confirmedIds.Contains(next.Ordered.Command.CommandId) ||
                        records.Values.Any(x => x.Ordered.Command.CommandId == next.Ordered.Command.CommandId)) return Stop("record_chain_or_command_id_conflict");
                    var command = next.Ordered.Command.ToCommand();
                    attemptedCommand = WindowExecutionCodec.Copy(next.Ordered);
                    if (!TryContext(command, out var context, out string reason)) return Stop("replay_context:" + reason);
                    var expected = MakeOrdered(command, context);
                    if (expected.OrderedCommandDigest != next.Ordered.OrderedCommandDigest) return Stop("replay_sequence_context_or_revision_conflict");
                    replayStarted = true;
                    if (isHost && !AdmitAtHost(next)) { FirstFaultOperation = WindowExecutionCodec.Copy(next); return Stop("host_admission_conflict"); }
                    ActualRuleExecutions++;
                    var rule = session.Submit(command);
                    if (!rule.Succeeded || session.State.EffectRuntime.Status == EffectRuntimeStatus.PausedFault)
                    { FirstFaultOperation = WindowExecutionCodec.Copy(next); return Stop("replay_rule_rejected_or_faulted:" + rule.Validation.Reason); }
                    if (session.State.EffectRuntime.StateRevision != next.ResultRevision || DetectBoundary() != next.BoundaryReason) return Stop("replay_result_or_boundary_conflict");
                    pending.Remove(next.OperationSequence); CommitRecord(next, rule);
                }
                return Result(r.OperationSequence > Tail ? "buffered" : "replayed");
            }
            catch (Exception ex)
            {
                if (replayStarted) { FirstFaultOperation = WindowExecutionCodec.Copy(source); return Stop("replay_exception:" + ex.Message); }
                return Reject(source, "record_decode:" + ex.Message);
            }
        }

        private bool TryContext(GameCommand command, out OperationCommandContext context, out string reason)
        {
            reason = "";
            if (!OperationWindowService.TryResolveCommandContext(session.State, command, out context, out var invalid)) { reason = invalid.Reason; return false; }
            if (context.ActingPlayerId != grant.OwnerPlayerId || context.MainWindowPlayerIdAtCreation != grant.OwnerPlayerId ||
                context.OriginMainOperationWindowId != grant.RootWindowId || context.OperationWindowId == grant.RootWindowId && context.WindowGeneration != grant.RootGeneration)
            { reason = "command_outside_granted_root"; return false; }
            return true;
        }
        private OrderedOperationCommandDto MakeOrdered(GameCommand command, OperationCommandContext c)
        {
            var o = new OrderedOperationCommandDto { GameId = grant.GameId, SimulationEpoch = grant.SimulationEpoch, MembershipVersion = grant.MembershipVersion,
                ClientSubmissionSequence = grant.NextClientSubmission + Tail, ObservedGlobalSequence = GlobalSequence,
                ObservedStateRevision = session.State.EffectRuntime.StateRevision, OperationWindowId = c.OperationWindowId, WindowGeneration = c.WindowGeneration,
                Command = GameCommandDto.FromCommand(command), GlobalCommandSequence = GlobalSequence + 1,
                WindowCommandSequence = (windowCounts.TryGetValue(c.OperationWindowId, out var count) ? count : 0) + 1 };
            o.ProposalDigest = OperationCommandCanonicalizer.CanonicalProposalDigest(o);
            o.OrderedCommandDigest = OperationCommandCanonicalizer.CanonicalOrderedCommandDigest(o); return o;
        }
        private bool AdmitAtHost(WindowExecutionRecord r)
        {
            var actual = OperationWindowService.ReadOpenWindows(session.State).SingleOrDefault(w => w.OperationWindowId == r.Ordered.OperationWindowId);
            // Host本地先执行时源窗可能刚关闭，使用规则持久化的原窗合同，不重建场景或业务状态。
            if (actual == null) actual = session.State.EffectRuntime.OperationWindows.SingleOrDefault(w => w.OperationWindowId == r.Ordered.OperationWindowId);
            if (actual == null) return false;
            if (!windowCounts.ContainsKey(actual.OperationWindowId))
            {
                RegisterHostWindowForFirstUse(hostSequencer, new OperationWindowDescriptorDto {
                GameId = grant.GameId, SimulationEpoch = grant.SimulationEpoch, MembershipVersion = grant.MembershipVersion, OperationWindowId = actual.OperationWindowId,
                WindowGeneration = actual.WindowGeneration, OpenedAtGlobalSequence = GlobalSequence, BaseWindowSequence = 0,
                AuthorizedPlayerIds = new List<int>(actual.AuthorizedPlayerIds), RequiredReplicaIds = new List<string>(grant.RequiredReplicaIds),
                PrefixDigest = OperationCommandCanonicalizer.EmptyPrefixDigest,
                WindowContractDigest = OperationCommandCanonicalizer.CanonicalWindowContractDigest(actual) });
            }
            var admission = hostAdmission == null ? hostSequencer.AdmitProposal(grant.OwnerPlayerId, r.Ordered) : hostAdmission(grant.GrantId, grant.OwnerPlayerId, r.Ordered);
            return admission.Accepted && admission.OrderedCommand.OrderedCommandDigest == r.Ordered.OrderedCommandDigest;
        }
        private static void RegisterHostWindowForFirstUse(OrderedCommandSequencer sequencer, OperationWindowDescriptorDto observed)
        {
            // 协调器在窗口出现时已登记；该窗口可能晚于其它兄弟命令才首次被使用。
            // observed的开启位置只是本次观察位置，已登记的开启位置/基线才是原合同。
            if (sequencer.TryReadRegisteredWindow(observed.OperationWindowId, out var registered))
            {
                observed.OpenedAtGlobalSequence = registered.OpenedAtGlobalSequence;
                observed.BaseWindowSequence = registered.BaseWindowSequence;
                observed.PrefixDigest = registered.PrefixDigest;
            }
            // 当前真实窗口的generation/授权/摘要仍与完整原描述严格比较，不覆盖旧登记。
            sequencer.RegisterWindow(observed);
        }
        private void CommitRecord(WindowExecutionRecord r, CommandResult rule)
        {
            records.Add(r.OperationSequence, WindowExecutionCodec.Copy(r)); Tail = r.OperationSequence; previousRecord = r.RecordDigest;
            prefix = OperationCommandCanonicalizer.CanonicalLogPrefixDigest(prefix, r.Ordered);
            string id = r.Ordered.OperationWindowId;
            windowCounts[id] = r.Ordered.WindowCommandSequence;
            windowPrefixes[id] = OperationCommandCanonicalizer.CanonicalLogPrefixDigest(windowPrefixes.TryGetValue(id, out var p) ? p : OperationCommandCanonicalizer.EmptyPrefixDigest, r.Ordered);
            boundaryReason = r.BoundaryReason;
            CommandExecuted?.Invoke(WindowExecutionCodec.Copy(r), rule);
        }
        private string DetectBoundary()
        {
            var open = OperationWindowService.ReadOpenWindows(session.State);
            if (!open.Any(w => w.OperationWindowId == grant.RootWindowId && w.WindowGeneration == grant.RootGeneration)) return "window_end";
            if (open.SelectMany(w => w.AvailablePlayerIds).Any(p => p != grant.OwnerPlayerId)) return "other_writer";
            if (!open.Any(w => w.AvailablePlayerIds.Contains(grant.OwnerPlayerId))) return "await_other_response";
            return "";
        }
        private string Digest() { FullRuleDigestComputations++; return RuleStateDigestService.Compute(session.State); }
        private string ProgressDigest() => WindowExecutionCodec.Hash(windowCounts.Keys.OrderBy(x => x, StringComparer.Ordinal).Select(id =>
            WindowExecutionCodec.Hash(id, WindowExecutionCodec.Number(windowCounts[id]), windowPrefixes[id])).ToArray());

        public WindowExecutionSeal Seal(bool explicitYield = false)
        {
            if (IsStopped || busy || !connected || pending.Count != 0 || boundaryReason == "" && !explicitYield)
                throw new InvalidOperationException("cannot_seal_unfinished_or_faulted_stream");
            if (seal != null) return WindowExecutionCodec.Copy(seal);
            seal = new WindowExecutionSeal { GrantId = grant.GrantId, OperationCount = Tail, GlobalSequence = GlobalSequence,
                PrefixDigest = prefix, RuleDigest = Digest(), WindowProgressDigest = ProgressDigest(), Reason = boundaryReason == "" ? "explicit_yield" : boundaryReason };
            seal.SealDigest = WindowExecutionCodec.SealDigest(seal); return WindowExecutionCodec.Copy(seal);
        }
        public WindowExecutionResult ReceiveSeal(int verifiedOwnerPlayer, WindowExecutionSeal value)
        {
            if (!isHost || verifiedOwnerPlayer != grant.OwnerPlayerId || value == null || value.GrantId != grant.GrantId) return Reject(value, "seal_identity");
            if (IsStopped) return Result("stopped", FirstFault);
            try
            {
                if (value.OperationCount < 0 || value.OperationCount > capacity || value.GlobalSequence != grant.BaseGlobalSequence + value.OperationCount ||
                    value.SealDigest != WindowExecutionCodec.SealDigest(value)) return Reject(value, "seal_shape");
                if (value.OperationCount < Tail) return Stop("seal_tail_behind_applied");
                if (seal != null && seal.SealDigest != value.SealDigest) return Stop("seal_conflict");
                seal = WindowExecutionCodec.Copy(value); return Result(Tail == seal.OperationCount ? "seal_received" : "seal_wait_missing");
            }
            catch (Exception ex) { return Reject(value, "seal_decode:" + ex.Message); }
        }
        // BuildReady不执行命令；只有准确尾号/完整摘要一致才可向Host发送Ready。
        public WindowExecutionSeal BuildReady(WindowExecutionSeal requested)
        {
            if (IsStopped || !connected || busy || requested == null || requested.GrantId != grant.GrantId ||
                requested.OperationCount != Tail || requested.GlobalSequence != GlobalSequence || requested.SealDigest != WindowExecutionCodec.SealDigest(requested) || pending.Count != 0)
                return null;
            if (requested.Reason != (boundaryReason == "" ? "explicit_yield" : boundaryReason) || requested.PrefixDigest != prefix ||
                requested.WindowProgressDigest != ProgressDigest() || requested.RuleDigest != Digest())
            { Stop("boundary_digest_conflict"); return null; }
            if (seal != null && seal.SealDigest != requested.SealDigest) { Stop("boundary_seal_conflict"); return null; }
            seal = WindowExecutionCodec.Copy(requested); return WindowExecutionCodec.Copy(seal);
        }
        public bool ReceiveReady(int verifiedSourcePlayer, WindowExecutionSeal value)
        {
            if (!isHost || IsStopped || !grant.FrozenPlayerIds.Contains(verifiedSourcePlayer) || value == null || value.GrantId != grant.GrantId) return false;
            if (seal == null || Tail != seal.OperationCount || value.SealDigest != WindowExecutionCodec.SealDigest(value)) return false;
            string replica = DeterministicSimulationSessionOptions.ReplicaIdForPlayer(verifiedSourcePlayer);
            if (value.SealDigest != seal.SealDigest) { Stop("ready_digest_conflict"); return false; }
            ready[replica] = value.SealDigest; return true;
        }
        public WindowExecutionConfirmation TryConfirm()
        {
            if (!isHost || IsStopped || seal == null) return null;
            if (!grant.RequiredReplicaIds.All(id => ready.TryGetValue(id, out var digest) && digest == seal.SealDigest))
            {
                // 尚未全员Ready仍做原本地校验，保留等待期间的损坏检测。
                BuildReady(seal); return null;
            }
            var confirmation = new WindowExecutionConfirmation { Seal = WindowExecutionCodec.Copy(seal), ReadyReplicaIds = new List<string>(grant.RequiredReplicaIds) };
            // 同owner同步调用；最终入口会BuildReady，不能在此再重复完整规则摘要。
            if (!ReceiveConfirmation(true, confirmation)) return null; return confirmation;
        }
        public bool ReceiveConfirmation(bool authenticatedHost, WindowExecutionConfirmation value)
        {
            if (!authenticatedHost || value == null || !WindowExecutionCodec.Same(value.ReadyReplicaIds, grant.RequiredReplicaIds) || BuildReady(value.Seal) == null) return false;
            confirmedSeal = WindowExecutionCodec.Copy(value.Seal);
            if (ConfirmedGlobalSequence == value.Seal.GlobalSequence) return true;
            if (retainStandaloneCheckpoint) confirmedState = GameStateCloneService.DeepClone(session.State);
            ConfirmedGlobalSequence = value.Seal.GlobalSequence; confirmedIds.Clear(); return true;
        }
        // 历史已确认流只核保留原件，不能读取已被后继合法操作改变的共享会话摘要。
        public WindowExecutionResult ReceiveConfirmedSeal(WindowExecutionSeal value)
        {
            if (IsStopped) return Result("stopped", FirstFault);
            if (confirmedSeal == null || value == null || value.GrantId != grant.GrantId) return Reject(value, "confirmed_seal_identity");
            try
            {
                if (value.OperationCount != confirmedSeal.OperationCount || value.GlobalSequence != confirmedSeal.GlobalSequence ||
                    value.SealDigest != WindowExecutionCodec.SealDigest(value) || value.SealDigest != confirmedSeal.SealDigest)
                    return Stop("confirmed_seal_conflict", value);
                return Result("duplicate_confirmed_seal");
            }
            catch (ArgumentException ex) { return Stop("confirmed_seal_invalid:" + ex.Message, value); }
        }
        public GameState ReadConfirmedState() => confirmedState == null ? throw new InvalidOperationException("confirmed_state_owned_by_coordinator") : GameStateCloneService.DeepClone(confirmedState);
        // 只在全员确认的准确边界导出原v1恢复metadata；未确认尾段从不进入这里。
        // 生产接线仍须把它与原GameSessionRecoveryPoint/continuation的同前缀原件一起原子发布。
        public SimulationRecoveryBaselineDto ExportConfirmedBaseline(long targetEpoch, SimulationRecoveryBaselineDto prior = null)
        {
            if (IsStopped || seal == null || ConfirmedGlobalSequence != GlobalSequence || seal.OperationCount != Tail || targetEpoch != grant.SimulationEpoch + 1)
                throw new InvalidOperationException("export_requires_exact_confirmed_boundary");
            if (grant.BaseGlobalSequence != 0 && (prior == null || prior.ConfirmedGlobalSequence != grant.BaseGlobalSequence ||
                prior.CommandPrefixDigest != grant.BasePrefixDigest || prior.GameId != grant.GameId || prior.ContentPackHash != grant.ContentPackHash))
                throw new InvalidOperationException("export_requires_prior_confirmed_ledger");
            var baseline = new SimulationRecoveryBaselineDto { GameId = grant.GameId, SimulationEpoch = targetEpoch, SourceSimulationEpoch = grant.SimulationEpoch,
                MembershipVersion = grant.MembershipVersion, ConfirmedGlobalSequence = GlobalSequence, CommandPrefixDigest = prefix,
                ContentPackHash = grant.ContentPackHash, RuleDigestVersion = RuleStateDigestService.DigestVersion, RuleStateDigest = seal.RuleDigest,
                FrozenPlayerIds = new List<int>(grant.FrozenPlayerIds), RequiredReplicaIds = new List<string>(grant.RequiredReplicaIds),
                NextSubmissionSequences = prior == null ? grant.FrozenPlayerIds.ToDictionary(p => p, p => 1L) : new Dictionary<int, long>(prior.NextSubmissionSequences),
                ConfirmedCommands = prior == null ? new List<SimulationConfirmedCommandReceiptDto>() : WindowExecutionCodec.Copy(prior.ConfirmedCommands) };
            baseline.NextSubmissionSequences[grant.OwnerPlayerId] = grant.NextClientSubmission + Tail;
            foreach (var r in records.Values) baseline.ConfirmedCommands.Add(new SimulationConfirmedCommandReceiptDto {
                SourceSimulationEpoch = grant.SimulationEpoch, ClientSubmissionSequence = r.Ordered.ClientSubmissionSequence,
                ProposalDigest = r.Ordered.ProposalDigest, OrderedDigest = r.Ordered.OrderedCommandDigest, Outcome = new OperationCommandOutcomeDto {
                    CommandId = r.Ordered.Command.CommandId, PlayerId = grant.OwnerPlayerId, GlobalCommandSequence = r.Ordered.GlobalCommandSequence,
                    OperationWindowId = r.Ordered.OperationWindowId, WindowCommandSequence = r.Ordered.WindowCommandSequence,
                    Succeeded = true, ErrorCode = CommandErrorCode.None, Reason = "", StateRevision = r.ResultRevision } });
            var actual = session.State.EffectRuntime.OperationWindows.ToDictionary(w => w.OperationWindowId, StringComparer.Ordinal);
            var ids = grant.BaseWindows.Select(w => w.Window.OperationWindowId).Union(actual.Keys).OrderBy(id => id, StringComparer.Ordinal);
            foreach (string id in ids)
            {
                var old = grant.BaseWindows.SingleOrDefault(w => w.Window.OperationWindowId == id);
                actual.TryGetValue(id, out var w);
                bool closed = w == null || w.Lifecycle == OperationWindowLifecycle.Closed;
                long count = windowCounts.TryGetValue(id, out var n) ? n : 0;
                string wp = windowPrefixes.TryGetValue(id, out var p) ? p : OperationCommandCanonicalizer.EmptyPrefixDigest;
                var descriptor = old == null ? new OperationWindowDescriptorDto {
                    GameId = grant.GameId, MembershipVersion = grant.MembershipVersion, OperationWindowId = id, WindowGeneration = w.WindowGeneration,
                    OpenedAtGlobalSequence = records.Values.Where(r => r.Ordered.OperationWindowId == id).Select(r => r.Ordered.ObservedGlobalSequence).DefaultIfEmpty(GlobalSequence).Min(),
                    AuthorizedPlayerIds = new List<int>(w.AuthorizedPlayerIds), RequiredReplicaIds = new List<string>(grant.RequiredReplicaIds),
                    WindowContractDigest = OperationCommandCanonicalizer.CanonicalWindowContractDigest(w) } : WindowExecutionCodec.Copy(old.Window);
                descriptor.SimulationEpoch = targetEpoch; descriptor.BaseWindowSequence = count; descriptor.PrefixDigest = wp;
                baseline.Windows.Add(new SimulationWindowFactDto { Window = descriptor, AvailablePlayerIds = closed ? new List<int>() : new List<int>(w.AvailablePlayerIds),
                    IsClosed = closed, IsNewWindow = false, RequiresPermit = !closed, FinalCommandSequence = count, CommandCount = count, WindowPrefixDigest = wp });
            }
            baseline.MetadataDigest = OperationCommandCanonicalizer.CanonicalRecoveryMetadataDigest(baseline);
            baseline.CheckpointId = OperationCommandCanonicalizer.RecoveryCheckpointId(baseline); return baseline;
        }
        public IReadOnlyList<WindowExecutionRecord> ReadRange(long fromOperation, int maximumCount = 256) => records.Where(p => p.Key >= fromOperation).Take(Math.Max(0, Math.Min(maximumCount, capacity))).Select(p => WindowExecutionCodec.Copy(p.Value)).ToArray();
        public (long From, long Through)? MissingRange()
        {
            long end = seal?.OperationCount ?? (pending.Count == 0 ? Tail : pending.Keys.Max());
            if (end <= Tail) return null;
            long through = pending.Count == 0 ? end : Math.Min(end, pending.Keys.Min() - 1);
            return through >= Tail + 1 ? (Tail + 1, through) : (ValueTuple<long, long>?)null;
        }
        public void SetConnected(bool value) { connected = value; } // 同一代次重连只补原件，不重置状态或序号。
        private WindowExecutionResult Reject(object original, string reason)
        {
            if (FirstRejectedMessage == null) FirstRejectedMessage = WindowExecutionCodec.Copy(original);
            return Result("rejected", reason);
        }
        private WindowExecutionResult Stop(string reason, object operation = null)
        {
            if (FirstFault == null) { FirstFault = reason; if (operation != null) FirstFaultOperation = WindowExecutionCodec.Copy(operation); FaultState = GameStateCloneService.DeepClone(session.State); }
            return Result("stopped", FirstFault);
        }
        private static WindowExecutionResult Result(string status, string reason = "") => new WindowExecutionResult { Status = status, Reason = reason };
    }
}
