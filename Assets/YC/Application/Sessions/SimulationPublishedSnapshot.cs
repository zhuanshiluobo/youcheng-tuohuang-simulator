using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    internal readonly struct RequestAuthorityStamp
    {
        internal readonly string Digest;
        internal readonly long ValidSince;
        internal RequestAuthorityStamp(string digest, long validSince) { Digest = digest; ValidSince = validSince; }
    }
    /// <summary>进程内固定版本。状态图仅在本类内部只读使用；所有公开可变DTO均防御复制。</summary>
    public sealed class SimulationPublishedSnapshot
    {
        private readonly GameSessionRecoveryPoint point;
        private readonly GameStateView view;
        private readonly List<SimulationWindowPermissionDto> permissions;
        private readonly Dictionary<long, SimulationBoundaryDto> boundaries;
        private readonly HashSet<long> permittedFences;
        private readonly CommandValidationPrefixDto validation;
        private readonly SimulationProtocolFaultDto fault;
        private readonly List<OrderedOperationCommandDto> tail;
        private readonly SimulationPublishedCheckpoint checkpoint;
        private readonly Dictionary<long, int> observedRevisions;
        private readonly Dictionary<string, RequestAuthorityStamp> requestAuthorities;

        public long Version { get; }
        public long Generation { get; }
        public string GameId { get; }
        public string ContentHash { get; }
        public long SimulationEpoch { get; }
        public int MembershipVersion { get; }
        public int LocalPlayerId { get; }
        public bool IsHost { get; }
        public long LastAppliedSequence { get; }
        public long LastSequencedSequence { get; }
        public long ValidatedThroughSequence { get; }
        public int StateRevision { get; }
        public string MapId { get; }
        public int Round { get; }
        public GamePhase Phase { get; }
        public int CurrentPlayerId { get; }
        public bool FinalScoringResolved { get; }
        public bool IsStopped { get; }
        public bool RequiresRecovery { get; }
        public bool IsRecoveringConfirmedPrefix { get; }
        public bool HasPendingLocalApplication { get; }
        public bool CanCreateSaveCheckpoint => checkpoint != null && IsHost && !IsStopped && !IsRecoveringConfirmedPrefix &&
            !HasPendingLocalApplication && LastAppliedSequence == ValidatedThroughSequence && LastSequencedSequence == ValidatedThroughSequence;
        internal long CommitVersion { get; }
        internal GameSessionRecoveryPoint Point => point;

        internal SimulationPublishedSnapshot(GameSessionRecoveryPoint point, GameStateView view,
            DeterministicSimulationSessionOptions options, long version, long generation, long applied, long sequenced,
            long validated, bool stopped, bool requiresRecovery, bool recovering,
            IEnumerable<SimulationWindowPermissionDto> permissions, IDictionary<long, SimulationBoundaryDto> boundaries,
            IEnumerable<long> permittedFences,
            CommandValidationPrefixDto validation, SimulationProtocolFaultDto fault,
            IEnumerable<OrderedOperationCommandDto> tail, SimulationPublishedCheckpoint checkpoint, long commitVersion,
            IDictionary<long, int> observedRevisions, IDictionary<string, RequestAuthorityStamp> requestAuthorities)
        {
            this.point = point ?? throw new ArgumentNullException(nameof(point));
            this.view = GameStateCloneService.DeepClone(view ?? throw new ArgumentNullException(nameof(view)));
            this.permissions = permissions.Select(CopyPermission).ToList();
            // Coordinator中的边界只追加且不再修改；公开读取仍复制所有嵌套字段。
            this.boundaries = new Dictionary<long, SimulationBoundaryDto>(boundaries);
            this.permittedFences = new HashSet<long>(permittedFences);
            this.validation = CopyValidation(validation); this.fault = CopyFault(fault);
            this.tail = tail.Select(OperationCommandCanonicalizer.SnapshotOrdered).ToList();
            this.checkpoint = checkpoint; CommitVersion = commitVersion;
            this.observedRevisions = new Dictionary<long, int>(observedRevisions);
            this.requestAuthorities = new Dictionary<string, RequestAuthorityStamp>(requestAuthorities, StringComparer.Ordinal);
            Version = version; Generation = generation; GameId = options.GameId; ContentHash = options.ContentPackHash;
            SimulationEpoch = options.SimulationEpoch; MembershipVersion = options.MembershipVersion;
            LocalPlayerId = options.LocalPlayerId; IsHost = options.IsHost;
            LastAppliedSequence = applied; LastSequencedSequence = sequenced; ValidatedThroughSequence = validated;
            IsStopped = stopped; RequiresRecovery = requiresRecovery; IsRecoveringConfirmedPrefix = recovering;
            var state = point.CapturedState;
            StateRevision = state.EffectRuntime?.StateRevision ?? 0; MapId = state.MapId; Round = state.Round;
            Phase = state.Phase; CurrentPlayerId = state.CurrentPlayerId; FinalScoringResolved = state.FinalScoring?.IsResolved == true;
        }

        private SimulationPublishedSnapshot(SimulationPublishedSnapshot source, long sequenced, bool pending, bool stopped, bool recovering, SimulationProtocolFaultDto mainFault)
        {
            point = source.point; view = source.view; permissions = source.permissions; boundaries = source.boundaries; permittedFences = source.permittedFences;
            validation = source.validation; fault = CopyFault(mainFault) ?? source.fault; tail = source.tail; checkpoint = source.checkpoint;
            observedRevisions = source.observedRevisions; requestAuthorities = source.requestAuthorities;
            Version = source.Version; Generation = source.Generation; GameId = source.GameId; ContentHash = source.ContentHash;
            SimulationEpoch = source.SimulationEpoch; MembershipVersion = source.MembershipVersion; LocalPlayerId = source.LocalPlayerId; IsHost = source.IsHost;
            LastAppliedSequence = source.LastAppliedSequence; LastSequencedSequence = sequenced; ValidatedThroughSequence = source.ValidatedThroughSequence;
            StateRevision = source.StateRevision; MapId = source.MapId; Round = source.Round; Phase = source.Phase;
            CurrentPlayerId = source.CurrentPlayerId; FinalScoringResolved = source.FinalScoringResolved; CommitVersion = source.CommitVersion;
            IsStopped = stopped || source.IsStopped; RequiresRecovery = mainFault?.RequiresRecovery == true || source.RequiresRecovery;
            IsRecoveringConfirmedPrefix = recovering || source.IsRecoveringConfirmedPrefix; HasPendingLocalApplication = pending;
        }

        internal SimulationPublishedSnapshot WithMainThreadFacts(long sequenced, bool pending, bool stopped, bool recovering, SimulationProtocolFaultDto mainFault = null)
            => new SimulationPublishedSnapshot(this, sequenced, pending, stopped, recovering, mainFault);

        public GameStateView ReadView() => GameStateCloneService.DeepClone(view);
        public IReadOnlyList<SimulationWindowPermissionDto> ReadWindowPermissions()
        {
            var result = permissions.Select(CopyPermission).ToList();
            foreach (var item in result)
                item.CanSubmitForLocalPlayer &= !HasPendingLocalApplication && !IsStopped && !IsRecoveringConfirmedPrefix;
            return result.AsReadOnly();
        }
        public SimulationBoundaryDto ReadBoundary(long sequence) => boundaries.TryGetValue(sequence, out var value)
            ? OperationCommandCanonicalizer.SnapshotBoundary(value) : null;
        internal bool IsBoundaryPermitted(long sequence) => !IsStopped && permittedFences.Contains(sequence) && boundaries.ContainsKey(sequence);
        public CommandValidationPrefixDto ReadLocalValidationPrefix() => CopyValidation(validation);
        public SimulationProtocolFaultDto ReadFault() => CopyFault(fault);
        public IReadOnlyList<OrderedOperationCommandDto> ReadPendingTail() => tail.Select(OperationCommandCanonicalizer.SnapshotOrdered).ToList().AsReadOnly();
        internal GameState CreateUiReadState() => GameStateCloneService.DeepClone(point.CapturedState);
        internal bool TryResolveCommand(GameCommand command, out OperationCommandContext context, out ValidationResult result)
            => OperationWindowService.TryResolveCommandContext(point.CapturedState, command, out context, out result);
        internal bool TryReadRequestAuthority(GameCommand command, out InteractionRequestAuthority authority, out ValidationResult validation)
            => InteractionRequestValidity.TryReadRequestAuthority(point.CapturedState, command, out authority, out validation);
        internal bool TryReadObservedRevision(long sequence, out int revision) => observedRevisions.TryGetValue(sequence, out revision);
        internal bool TryValidateRequestProposal(OperationCommandProposalDto proposal, out ValidationResult validation)
        {
            var command = proposal.Command.ToCommand();
            validation = ValidationResult.Failure(CommandErrorCode.StateRevisionMismatch, "原观察前缀或请求合同已失效，请刷新后重新决策。");
            if (!TryReadObservedRevision(proposal.ObservedGlobalSequence, out int revision) || revision != proposal.ObservedStateRevision ||
                !command.Parameters.TryGetValue("expectedRevision", out var expected) || !int.TryParse(expected, out int originalRevision) || originalRevision != revision) return false;
            if (!InteractionRequestValidity.TryReadMarker(command, out bool present, out string digest, out validation)) return false;
            if (!present) { validation = ValidationResult.Failure(CommandErrorCode.InvalidTarget, "原提案没有明确请求合同标识。"); return false; }
            if (!TryReadRequestAuthority(command, out var authority, out validation)) return false;
            if (authority.OperationWindowId != proposal.OperationWindowId || authority.WindowGeneration != proposal.WindowGeneration)
            { validation = ValidationResult.Failure(CommandErrorCode.InvalidTarget, "原请求不属于该提案窗口或代次。"); return false; }
            if (!requestAuthorities.TryGetValue(authority.InteractionId, out var stamp) || stamp.Digest != digest ||
                proposal.ObservedGlobalSequence < stamp.ValidSince)
            { validation = ValidationResult.Failure(CommandErrorCode.StateRevisionMismatch, "原请求的连续有效合同已经改变，请重新决策。"); return false; }
            return InteractionRequestValidity.TryValidateAnswer(point.CapturedState, command, digest, out validation);
        }
        internal bool TryReadWindow(string id, out SimulationWindowPermissionDto permission)
        {
            var value = permissions.Find(p => p.Window.OperationWindowId == id);
            permission = value == null ? null : CopyPermission(value); return permission != null;
        }

        /// <summary>仅使用本固定确认点，在后台生成原有checkpoint DTO，不再访问活动会话。</summary>
        public Task<SimulationRecoveryCheckpointDto> CreateSaveCheckpointAsync()
        {
            if (!CanCreateSaveCheckpoint) return FailedCheckpoint("save_confirmation_pending");
            return checkpoint.CreateAsync(SimulationEpoch + 1);
        }
        public Task<SimulationRecoveryCheckpointDto> CreateConfirmedRecoveryCheckpointAsync(long expectedConfirmedSequence, long targetEpoch)
        {
            if (checkpoint == null || !IsHost || !IsRecoveringConfirmedPrefix || expectedConfirmedSequence != ValidatedThroughSequence ||
                (!IsStopped && (LastSequencedSequence != LastAppliedSequence || LastAppliedSequence != ValidatedThroughSequence)))
                return FailedCheckpoint("checkpoint_not_retained");
            return checkpoint.CreateAsync(targetEpoch);
        }
        private static Task<SimulationRecoveryCheckpointDto> FailedCheckpoint(string reason)
            => Task.FromException<SimulationRecoveryCheckpointDto>(new InvalidOperationException(reason));

        internal static SimulationWindowPermissionDto CopyPermission(SimulationWindowPermissionDto value) => new SimulationWindowPermissionDto {
            Window = OperationCommandCanonicalizer.SnapshotWindow(value.Window, value.Window.GameId, value.Window.SimulationEpoch, value.Window.MembershipVersion),
            AvailablePlayerIds = new List<int>(value.AvailablePlayerIds), IsOpen = value.IsOpen, IsPermitted = value.IsPermitted,
            CanSubmitForLocalPlayer = value.CanSubmitForLocalPlayer, GlobalFenceSequence = value.GlobalFenceSequence };
        internal static CommandValidationPrefixDto CopyValidation(CommandValidationPrefixDto value) => value == null ? null : new CommandValidationPrefixDto {
            ProtocolVersion = value.ProtocolVersion, DigestVersion = value.DigestVersion, GameId = value.GameId, SimulationEpoch = value.SimulationEpoch,
            MembershipVersion = value.MembershipVersion, ValidatedThroughSequence = value.ValidatedThroughSequence, PrefixDigest = value.PrefixDigest,
            Outcomes = value.Outcomes.Select(OperationCommandCanonicalizer.SnapshotOutcome).ToList() };
        internal static SimulationProtocolFaultDto CopyFault(SimulationProtocolFaultDto value) => value == null ? null : new SimulationProtocolFaultDto {
            ProtocolVersion = value.ProtocolVersion, GameId = value.GameId, SimulationEpoch = value.SimulationEpoch, MembershipVersion = value.MembershipVersion,
            FaultCode = value.FaultCode, Diagnostic = value.Diagnostic, FailureGlobalSequence = value.FailureGlobalSequence,
            Command = value.Command == null ? null : GameStateCloneService.DeepClone(value.Command), ErrorCode = value.ErrorCode,
            RequiresRecovery = value.RequiresRecovery, LocalRuleResult = GameSession.CopyCommandResult(value.LocalRuleResult) };
    }

    // 仅进程内的固定捕获；不是存档协议。后台可持有旧点，不依赖会话的三点保留窗口。
    internal sealed class SimulationPublishedCheckpoint
    {
        private readonly GameSessionRecoveryPoint point;
        private readonly SimulationRecoveryBaselineDto metadata;
        private readonly DeterministicSimulationSessionOptions options;
        internal SimulationPublishedCheckpoint(GameSessionRecoveryPoint point, SimulationRecoveryBaselineDto metadata, DeterministicSimulationSessionOptions options)
        { this.point = point; this.metadata = metadata; this.options = options.Snapshot(); }
        internal Task<SimulationRecoveryCheckpointDto> CreateAsync(long targetEpoch) => Task.Run(() =>
        {
            if (options.SimulationEpoch >= long.MaxValue || targetEpoch != options.SimulationEpoch + 1) throw new ArgumentException("recovery_epoch_invalid");
            var exported = point.ExportDetached();
            var baseline = OperationCommandCanonicalizer.SnapshotRecoveryBaseline(metadata); baseline.SimulationEpoch = targetEpoch;
            baseline.RuleStateDigest = RuleStateDigestService.Compute(exported.Archive.Snapshot.State);
            foreach (var fact in baseline.Windows)
            {
                fact.Window.SimulationEpoch = targetEpoch; fact.Window.BaseWindowSequence = fact.FinalCommandSequence;
                fact.Window.PrefixDigest = fact.WindowPrefixDigest; fact.IsNewWindow = false; fact.RequiresPermit = !fact.IsClosed;
            }
            baseline.MetadataDigest = OperationCommandCanonicalizer.CanonicalRecoveryMetadataDigest(baseline);
            baseline.CheckpointId = OperationCommandCanonicalizer.RecoveryCheckpointId(baseline);
            var target = options.Snapshot(); target.SimulationEpoch = targetEpoch;
            target.CheckpointGlobalSequence = baseline.ConfirmedGlobalSequence; target.CheckpointPrefixDigest = baseline.CommandPrefixDigest;
            OperationCommandCanonicalizer.ValidateRecoveryBaseline(target, baseline, exported.Archive.Snapshot.State);
            return new SimulationRecoveryCheckpointDto { Baseline = baseline, Archive = exported.Archive, Continuation = exported.Continuation };
        });
    }
}
