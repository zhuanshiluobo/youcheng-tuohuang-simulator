using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    /// <summary>v1：严格 UTF-8、LE 整数、字节长度前缀；null 长度 -1，空长度 0。</summary>
    public static class OperationCommandCanonicalizer
    {
        public const string DigestVersion = "yc.operation-command.sha256.v1";
        public const int MaximumPayloadBytes = 1024 * 1024;
        private const int MaximumFieldBytes = 64 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static string EmptyPrefixDigest => Hash(Encode(writer => { Text(writer, DigestVersion); Text(writer, "prefix-base"); }));

        public static SimulationConfirmedCommandReceiptDto SnapshotConfirmedReceipt(SimulationConfirmedCommandReceiptDto source)
            => new SimulationConfirmedCommandReceiptDto { SourceSimulationEpoch = source.SourceSimulationEpoch,
                ClientSubmissionSequence = source.ClientSubmissionSequence, ProposalDigest = source.ProposalDigest,
                OrderedDigest = source.OrderedDigest, Outcome = SnapshotOutcome(source.Outcome) };

        public static SimulationWindowFactDto SnapshotRecoveryWindow(SimulationWindowFactDto source)
            => new SimulationWindowFactDto { Window = SnapshotWindow(source.Window, source.Window.GameId, source.Window.SimulationEpoch, source.Window.MembershipVersion),
                AvailablePlayerIds = source.AvailablePlayerIds.OrderBy(id => id).ToList(), IsClosed = source.IsClosed,
                IsNewWindow = source.IsNewWindow, RequiresPermit = source.RequiresPermit, FinalCommandSequence = source.FinalCommandSequence,
                CommandCount = source.CommandCount, WindowPrefixDigest = source.WindowPrefixDigest };

        public static SimulationRecoveryBaselineDto SnapshotRecoveryBaseline(SimulationRecoveryBaselineDto source)
            => new SimulationRecoveryBaselineDto { ProtocolVersion = source.ProtocolVersion, GameId = source.GameId,
                SimulationEpoch = source.SimulationEpoch, MembershipVersion = source.MembershipVersion, SourceSimulationEpoch = source.SourceSimulationEpoch,
                CheckpointId = source.CheckpointId, ConfirmedGlobalSequence = source.ConfirmedGlobalSequence, CommandPrefixDigest = source.CommandPrefixDigest,
                ContentPackHash = source.ContentPackHash, RuleDigestVersion = source.RuleDigestVersion, RuleStateDigest = source.RuleStateDigest,
                FrozenPlayerIds = new List<int>(source.FrozenPlayerIds), RequiredReplicaIds = new List<string>(source.RequiredReplicaIds),
                Windows = source.Windows.Select(SnapshotRecoveryWindow).ToList(), NextSubmissionSequences = new Dictionary<int, long>(source.NextSubmissionSequences),
                ConfirmedCommands = source.ConfirmedCommands.Select(SnapshotConfirmedReceipt).ToList(), MetadataDigest = source.MetadataDigest };

        // 独立、有界 metadata 编码；不借单命令的 1MiB 上限处理恢复账本。
        public static string CanonicalRecoveryMetadataDigest(SimulationRecoveryBaselineDto value)
        {
            if (value == null || value.Windows == null || value.Windows.Count > 4096 || value.ConfirmedCommands == null || value.ConfirmedCommands.Count > 4096 ||
                value.FrozenPlayerIds == null || value.FrozenPlayerIds.Count > 256 || value.RequiredReplicaIds == null || value.RequiredReplicaIds.Count > 256 ||
                value.NextSubmissionSequences == null || value.NextSubmissionSequences.Count > 256) throw new ArgumentException("recovery_metadata_capacity");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, true))
            {
                Text(writer, "yc.confirmed-recovery.metadata.v1"); writer.Write(value.ProtocolVersion); Text(writer, value.GameId);
                writer.Write(value.SourceSimulationEpoch); writer.Write(value.SimulationEpoch); writer.Write(value.MembershipVersion);
                writer.Write(value.ConfirmedGlobalSequence); writer.Write(DigestBytes(value.CommandPrefixDigest)); Text(writer, value.ContentPackHash);
                Text(writer, value.RuleDigestVersion); writer.Write(DigestBytes(value.RuleStateDigest));
                writer.Write(value.FrozenPlayerIds.Count); foreach (int id in value.FrozenPlayerIds.OrderBy(id => id)) writer.Write(id);
                writer.Write(value.RequiredReplicaIds.Count); foreach (string id in value.RequiredReplicaIds.OrderBy(id => id, StringComparer.Ordinal)) Text(writer, id);
                writer.Write(value.NextSubmissionSequences.Count); foreach (var pair in value.NextSubmissionSequences.OrderBy(pair => pair.Key)) { writer.Write(pair.Key); writer.Write(pair.Value); }
                writer.Write(value.Windows.Count);
                foreach (var fact in value.Windows.OrderBy(fact => fact.Window.OperationWindowId, StringComparer.Ordinal))
                {
                    var w = SnapshotWindow(fact.Window, value.GameId, value.SimulationEpoch, value.MembershipVersion);
                    Text(writer, w.OperationWindowId); writer.Write(w.WindowGeneration); writer.Write(w.OpenedAtGlobalSequence); writer.Write(w.BaseWindowSequence);
                    writer.Write(DigestBytes(w.PrefixDigest)); writer.Write(DigestBytes(w.WindowContractDigest));
                    writer.Write(w.AuthorizedPlayerIds.Count); foreach (int id in w.AuthorizedPlayerIds) writer.Write(id);
                    writer.Write(w.RequiredReplicaIds.Count); foreach (string id in w.RequiredReplicaIds) Text(writer, id);
                    writer.Write(fact.IsClosed); writer.Write(fact.IsNewWindow); writer.Write(fact.RequiresPermit);
                    writer.Write(fact.FinalCommandSequence); writer.Write(fact.CommandCount); writer.Write(DigestBytes(fact.WindowPrefixDigest));
                    if (fact.AvailablePlayerIds == null || fact.AvailablePlayerIds.Count > 256) throw new ArgumentException("recovery_window_capacity");
                    writer.Write(fact.AvailablePlayerIds.Count); foreach (int id in fact.AvailablePlayerIds.OrderBy(id => id)) writer.Write(id);
                    if (stream.Length > 8 * 1024 * 1024) throw new ArgumentException("recovery_metadata_capacity");
                }
                writer.Write(value.ConfirmedCommands.Count);
                foreach (var receipt in value.ConfirmedCommands.OrderBy(receipt => receipt.Outcome.GlobalCommandSequence))
                {
                    var o = receipt.Outcome; writer.Write(receipt.SourceSimulationEpoch); writer.Write(receipt.ClientSubmissionSequence);
                    writer.Write(DigestBytes(receipt.ProposalDigest)); writer.Write(DigestBytes(receipt.OrderedDigest));
                    Text(writer, o.CommandId); writer.Write(o.PlayerId); writer.Write(o.GlobalCommandSequence); Text(writer, o.OperationWindowId);
                    writer.Write(o.WindowCommandSequence); writer.Write(o.Succeeded); writer.Write((int)o.ErrorCode); Text(writer, o.Reason); writer.Write(o.StateRevision);
                    if (stream.Length > 8 * 1024 * 1024) throw new ArgumentException("recovery_metadata_capacity");
                }
                writer.Flush(); if (stream.Length > 8 * 1024 * 1024) throw new ArgumentException("recovery_metadata_capacity");
                return Hash(stream.ToArray());
            }
        }

        public static void ValidateRecoveryBaseline(DeterministicSimulationSessionOptions options, SimulationRecoveryBaselineDto value, GameState state = null)
        {
            if (options == null || value == null) throw new ArgumentException("recovery_baseline_missing");
            ValidateIdentity(value.GameId, value.SimulationEpoch, value.MembershipVersion);
            if (value.ProtocolVersion != OperationCommandProtocol.Version || value.SourceSimulationEpoch <= 0 || value.SourceSimulationEpoch >= long.MaxValue ||
                value.SimulationEpoch != value.SourceSimulationEpoch + 1 || value.GameId != options.GameId || value.ContentPackHash != options.ContentPackHash ||
                value.SimulationEpoch != options.SimulationEpoch || value.MembershipVersion != options.MembershipVersion ||
                value.ConfirmedGlobalSequence < 0 || value.ConfirmedGlobalSequence >= long.MaxValue - 1 ||
                value.ConfirmedGlobalSequence != options.CheckpointGlobalSequence || value.CommandPrefixDigest != options.CheckpointPrefixDigest ||
                value.ConfirmedGlobalSequence == 0 && value.CommandPrefixDigest != EmptyPrefixDigest ||
                value.RuleDigestVersion != RuleStateDigestService.DigestVersion || value.MetadataDigest != CanonicalRecoveryMetadataDigest(value) ||
                value.CheckpointId != RecoveryCheckpointId(value) || value.Windows.Count > options.MaximumRegisteredWindows ||
                value.ConfirmedCommands.Count > options.MaximumLogCommands || value.ConfirmedCommands.Count != value.ConfirmedGlobalSequence ||
                !value.FrozenPlayerIds.OrderBy(id => id).SequenceEqual(options.FrozenPlayerIds.OrderBy(id => id)) ||
                !value.RequiredReplicaIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(options.RequiredReplicaIds.OrderBy(id => id, StringComparer.Ordinal)) ||
                value.NextSubmissionSequences.Count != value.FrozenPlayerIds.Count) throw new ArgumentException("recovery_baseline_invalid");
            var ids = new HashSet<string>(StringComparer.Ordinal); var counts = value.FrozenPlayerIds.ToDictionary(id => id, id => 1L); long global = 0;
            foreach (var r in value.ConfirmedCommands.OrderBy(r => r.Outcome.GlobalCommandSequence))
            {
                var o = r.Outcome;
                if (r.SourceSimulationEpoch <= 0 || r.SourceSimulationEpoch > value.SourceSimulationEpoch || !o.Succeeded || o.ErrorCode != CommandErrorCode.None ||
                    o.GlobalCommandSequence != ++global || string.IsNullOrWhiteSpace(o.CommandId) || !ids.Add(o.CommandId) ||
                    !counts.TryGetValue(o.PlayerId, out var next) || r.ClientSubmissionSequence != next || o.StateRevision < 0) throw new ArgumentException("recovery_receipt_invalid");
                NormalizeDigest(r.ProposalDigest); NormalizeDigest(r.OrderedDigest); counts[o.PlayerId] = next + 1;
            }
            if (counts.Any(pair => !value.NextSubmissionSequences.TryGetValue(pair.Key, out var n) || n != pair.Value)) throw new ArgumentException("recovery_submission_baseline_invalid");
            var windows = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in value.Windows)
                if (!windows.Add(f.Window.OperationWindowId) || f.Window.OpenedAtGlobalSequence > global || f.FinalCommandSequence > global ||
                    f.FinalCommandSequence != f.CommandCount || f.Window.BaseWindowSequence != f.FinalCommandSequence || f.Window.PrefixDigest != f.WindowPrefixDigest ||
                    f.AvailablePlayerIds.Distinct().Count() != f.AvailablePlayerIds.Count || f.AvailablePlayerIds.Any(id => !f.Window.AuthorizedPlayerIds.Contains(id)) ||
                    f.IsClosed && f.AvailablePlayerIds.Count != 0 || f.Window.AuthorizedPlayerIds.Any(id => !value.FrozenPlayerIds.Contains(id)) ||
                    !f.Window.RequiredReplicaIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(value.RequiredReplicaIds.OrderBy(id => id, StringComparer.Ordinal)))
                    throw new ArgumentException("recovery_window_baseline_invalid");
            if (state != null)
            {
                if (state.GameId != value.GameId || state.ContentPackHash != value.ContentPackHash || RuleStateDigestService.Compute(state) != value.RuleStateDigest ||
                    !state.Players.Select(p => p.PlayerId).OrderBy(id => id).SequenceEqual(value.FrozenPlayerIds.OrderBy(id => id))) throw new ArgumentException("recovery_state_mismatch");
                var actual = OperationWindowService.ReadOpenWindows(state);
                if (actual.Count != value.Windows.Count(f => !f.IsClosed)) throw new ArgumentException("recovery_open_windows_mismatch");
                foreach (var w in actual)
                {
                    var f = value.Windows.SingleOrDefault(fact => fact.Window.OperationWindowId == w.OperationWindowId && !fact.IsClosed);
                    if (f == null || f.Window.WindowGeneration != w.WindowGeneration || f.Window.WindowContractDigest != CanonicalWindowContractDigest(w) ||
                        !f.AvailablePlayerIds.OrderBy(id => id).SequenceEqual(w.AvailablePlayerIds.OrderBy(id => id))) throw new ArgumentException("recovery_window_state_mismatch");
                }
            }
        }

        public static string RecoveryCheckpointId(SimulationRecoveryBaselineDto value)
            => "recovery." + value.MetadataDigest;

        public static string CanonicalProposalDigest(OperationCommandProposalDto proposal)
        {
            ValidateProposal(proposal);
            return Hash(Encode(writer =>
            {
                Text(writer, DigestVersion); Text(writer, "proposal");
                writer.Write(proposal.ProtocolVersion); Text(writer, proposal.DigestVersion);
                Text(writer, proposal.GameId); writer.Write(proposal.SimulationEpoch); writer.Write(proposal.MembershipVersion);
                writer.Write(proposal.ClientSubmissionSequence); writer.Write(proposal.ObservedGlobalSequence); writer.Write(proposal.ObservedStateRevision);
                Text(writer, proposal.OperationWindowId); writer.Write(proposal.WindowGeneration);
                var command = proposal.Command;
                Text(writer, command.CommandId); writer.Write((int)command.Kind); writer.Write(command.PlayerId);
                Text(writer, command.SourceId); Text(writer, command.TargetId);
                writer.Write(command.OptionIds == null ? -1 : command.OptionIds.Count);
                if (command.OptionIds != null) foreach (var option in command.OptionIds) Text(writer, option);
                writer.Write(command.Parameters == null ? -1 : command.Parameters.Count);
                if (command.Parameters != null)
                    foreach (var parameter in command.Parameters.OrderBy(value => value.Key, StringComparer.Ordinal))
                    { Text(writer, parameter.Key); Text(writer, parameter.Value); }
            }));
        }

        public static string CanonicalOrderedCommandDigest(OrderedOperationCommandDto ordered)
        {
            if (ordered == null || ordered.GlobalCommandSequence <= 0 || ordered.GlobalCommandSequence == long.MaxValue ||
                ordered.WindowCommandSequence <= 0 || ordered.WindowCommandSequence == long.MaxValue)
                throw new ArgumentException("ordered_sequence_invalid");
            string proposalDigest = CanonicalProposalDigest(ordered);
            if (!string.Equals(ordered.ProposalDigest, proposalDigest, StringComparison.Ordinal))
                throw new ArgumentException("proposal_digest_mismatch");
            return Hash(Encode(writer =>
            {
                Text(writer, DigestVersion); Text(writer, "ordered");
                writer.Write(ordered.GlobalCommandSequence); writer.Write(ordered.WindowCommandSequence);
                writer.Write(DigestBytes(proposalDigest));
            }));
        }

        public static string CanonicalLogPrefixDigest(string previousPrefixDigest, OrderedOperationCommandDto ordered)
        {
            string orderedDigest = CanonicalOrderedCommandDigest(ordered);
            if (!string.Equals(ordered.OrderedCommandDigest, orderedDigest, StringComparison.Ordinal))
                throw new ArgumentException("ordered_digest_mismatch");
            return Hash(Encode(writer =>
            {
                Text(writer, DigestVersion); Text(writer, "prefix");
                writer.Write(DigestBytes(previousPrefixDigest)); writer.Write(DigestBytes(orderedDigest));
            }));
        }

        internal static void ValidateIdentity(string gameId, long epoch, int membership)
        {
            if (string.IsNullOrWhiteSpace(gameId) || gameId.Length > 128 || epoch <= 0 || membership <= 0)
                throw new ArgumentException("operation_identity_invalid");
        }

        internal static OperationWindowDescriptorDto SnapshotWindow(OperationWindowDescriptorDto window, string gameId, long epoch, int membership)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            ValidateIdentity(window.GameId, window.SimulationEpoch, window.MembershipVersion);
            if (window.GameId != gameId || window.SimulationEpoch != epoch || window.MembershipVersion != membership ||
                window.ProtocolVersion != OperationCommandProtocol.Version || window.DigestVersion != DigestVersion ||
                string.IsNullOrWhiteSpace(window.OperationWindowId) || window.OperationWindowId.Length > 256 ||
                window.WindowGeneration <= 0 || window.OpenedAtGlobalSequence < 0 || window.BaseWindowSequence < 0 ||
                window.BaseWindowSequence == long.MaxValue || string.IsNullOrWhiteSpace(window.WindowContractDigest) || window.WindowContractDigest.Length > 256 ||
                window.AuthorizedPlayerIds == null || window.AuthorizedPlayerIds.Count == 0 || window.AuthorizedPlayerIds.Count > 256 ||
                window.AuthorizedPlayerIds.Any(id => id <= 0) || window.AuthorizedPlayerIds.Distinct().Count() != window.AuthorizedPlayerIds.Count ||
                window.RequiredReplicaIds == null || window.RequiredReplicaIds.Count == 0 || window.RequiredReplicaIds.Count > 256 ||
                window.RequiredReplicaIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256) ||
                window.RequiredReplicaIds.Distinct(StringComparer.Ordinal).Count() != window.RequiredReplicaIds.Count)
                throw new ArgumentException("window_fact_invalid");
            string prefix = window.PrefixDigest;
            if (string.IsNullOrEmpty(prefix))
            {
                if (window.BaseWindowSequence != 0) throw new ArgumentException("window_base_prefix_missing");
                prefix = EmptyPrefixDigest;
            }
            prefix = NormalizeDigest(prefix);
            if (window.BaseWindowSequence == 0 && prefix != EmptyPrefixDigest) throw new ArgumentException("window_base_prefix_conflict");
            return new OperationWindowDescriptorDto
            {
                ProtocolVersion = window.ProtocolVersion, DigestVersion = window.DigestVersion, GameId = window.GameId,
                SimulationEpoch = window.SimulationEpoch, MembershipVersion = window.MembershipVersion,
                OperationWindowId = window.OperationWindowId, WindowGeneration = window.WindowGeneration,
                OpenedAtGlobalSequence = window.OpenedAtGlobalSequence, BaseWindowSequence = window.BaseWindowSequence,
                AuthorizedPlayerIds = window.AuthorizedPlayerIds.OrderBy(id => id).ToList(),
                RequiredReplicaIds = window.RequiredReplicaIds.OrderBy(id => id, StringComparer.Ordinal).ToList(),
                PrefixDigest = prefix, WindowContractDigest = window.WindowContractDigest
            };
        }

        internal static bool SameWindow(OperationWindowDescriptorDto left, OperationWindowDescriptorDto right) =>
            left.OperationWindowId == right.OperationWindowId && left.WindowGeneration == right.WindowGeneration &&
            left.OpenedAtGlobalSequence == right.OpenedAtGlobalSequence && left.BaseWindowSequence == right.BaseWindowSequence &&
            left.PrefixDigest == right.PrefixDigest && left.WindowContractDigest == right.WindowContractDigest &&
            left.AuthorizedPlayerIds.SequenceEqual(right.AuthorizedPlayerIds) && left.RequiredReplicaIds.SequenceEqual(right.RequiredReplicaIds);

        internal static OperationCommandProposalDto SnapshotProposal(OperationCommandProposalDto source)
        {
            ValidateProposal(source);
            var result = new OperationCommandProposalDto(); CopyProposal(source, result); return result;
        }

        internal static OrderedOperationCommandDto SnapshotOrdered(OrderedOperationCommandDto source)
        {
            ValidateProposal(source);
            var result = new OrderedOperationCommandDto(); CopyProposal(source, result);
            result.GlobalCommandSequence = source.GlobalCommandSequence; result.WindowCommandSequence = source.WindowCommandSequence;
            result.ProposalDigest = source.ProposalDigest; result.OrderedCommandDigest = source.OrderedCommandDigest;
            return result;
        }

        internal static void CopyProposal(OperationCommandProposalDto source, OperationCommandProposalDto target)
        {
            target.ProtocolVersion = source.ProtocolVersion; target.DigestVersion = source.DigestVersion; target.GameId = source.GameId;
            target.SimulationEpoch = source.SimulationEpoch; target.MembershipVersion = source.MembershipVersion;
            target.ClientSubmissionSequence = source.ClientSubmissionSequence; target.ObservedGlobalSequence = source.ObservedGlobalSequence;
            target.ObservedStateRevision = source.ObservedStateRevision; target.OperationWindowId = source.OperationWindowId; target.WindowGeneration = source.WindowGeneration;
            var command = source.Command;
            target.Command = new GameCommandDto
            {
                CommandId = command.CommandId, Kind = command.Kind, PlayerId = command.PlayerId, SourceId = command.SourceId, TargetId = command.TargetId,
                OptionIds = command.OptionIds == null ? null : new List<string>(command.OptionIds),
                Parameters = command.Parameters == null ? null : command.Parameters.Select(parameter =>
                    new GameCommandParameterDto { Key = parameter.Key, Value = parameter.Value }).ToList()
            };
        }

        internal static OperationCommandOutcomeDto SnapshotOutcome(OperationCommandOutcomeDto source) => new OperationCommandOutcomeDto
        {
            CommandId = source.CommandId, PlayerId = source.PlayerId, GlobalCommandSequence = source.GlobalCommandSequence, OperationWindowId = source.OperationWindowId,
            WindowCommandSequence = source.WindowCommandSequence, Succeeded = source.Succeeded, ErrorCode = source.ErrorCode,
            Reason = source.Reason, StateRevision = source.StateRevision
        };

        public static string CanonicalWindowContractDigest(OperationWindowState window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            return Hash(Encode(writer => {
                Text(writer, DigestVersion); Text(writer, "window-contract"); Text(writer, window.OperationWindowId); writer.Write(window.WindowGeneration);
                Text(writer, window.WindowKind); Text(writer, window.SourceMainNodeId); Text(writer, window.SourceEffectId);
                Text(writer, window.SourceSchedulingGroupId); Text(writer, window.ParentOperationWindowId);
                writer.Write(window.OpenedOrdinal); writer.Write(window.OpenedAtStateRevision);
                var players = window.AuthorizedPlayerIds.OrderBy(id => id).ToList(); writer.Write(players.Count); foreach (int id in players) writer.Write(id);
            }));
        }

        public static string CanonicalBoundaryDigest(SimulationBoundaryDto boundary)
        {
            if (boundary == null || boundary.ProtocolVersion != OperationCommandProtocol.Version || boundary.GlobalFenceSequence < 0 || boundary.Windows == null || boundary.Windows.Count > 256)
                throw new ArgumentException("boundary_invalid");
            ValidateIdentity(boundary.GameId, boundary.SimulationEpoch, boundary.MembershipVersion);
            return Hash(Encode(writer => {
                Text(writer, DigestVersion); Text(writer, "simulation-boundary"); Text(writer, boundary.GameId); writer.Write(boundary.SimulationEpoch); writer.Write(boundary.MembershipVersion);
                writer.Write(boundary.GlobalFenceSequence); writer.Write(DigestBytes(boundary.CommandPrefixDigest));
                Text(writer, boundary.RuleStateDigestVersion); writer.Write(DigestBytes(boundary.RuleStateDigest));
                var ids = new HashSet<string>(StringComparer.Ordinal); writer.Write(boundary.Windows.Count);
                foreach (var fact in boundary.Windows.OrderBy(value => value.Window?.OperationWindowId, StringComparer.Ordinal))
                {
                    var window = SnapshotWindow(fact.Window, boundary.GameId, boundary.SimulationEpoch, boundary.MembershipVersion);
                    if (!ids.Add(window.OperationWindowId) || fact.CommandCount != fact.FinalCommandSequence || fact.FinalCommandSequence < window.BaseWindowSequence ||
                        fact.AvailablePlayerIds == null || fact.AvailablePlayerIds.Count > 256 || fact.AvailablePlayerIds.Distinct().Count() != fact.AvailablePlayerIds.Count ||
                        fact.AvailablePlayerIds.Any(id => !window.AuthorizedPlayerIds.Contains(id))) throw new ArgumentException("boundary_window_invalid");
                    Text(writer, window.OperationWindowId); writer.Write(window.WindowGeneration); Text(writer, window.WindowContractDigest);
                    writer.Write(window.OpenedAtGlobalSequence); writer.Write(window.BaseWindowSequence); Text(writer, window.PrefixDigest);
                    writer.Write(window.AuthorizedPlayerIds.Count); foreach (int id in window.AuthorizedPlayerIds) writer.Write(id);
                    writer.Write(window.RequiredReplicaIds.Count); foreach (string id in window.RequiredReplicaIds) Text(writer, id);
                    writer.Write(fact.IsClosed); writer.Write(fact.IsNewWindow); writer.Write(fact.RequiresPermit); writer.Write(fact.FinalCommandSequence); writer.Write(fact.CommandCount);
                    writer.Write(DigestBytes(fact.WindowPrefixDigest)); writer.Write(fact.AvailablePlayerIds.Count); foreach (int id in fact.AvailablePlayerIds.OrderBy(id => id)) writer.Write(id);
                }
            }));
        }

        internal static SimulationBoundaryDto SnapshotBoundary(SimulationBoundaryDto source)
        {
            string digest = CanonicalBoundaryDigest(source);
            if (source.FactDigest != digest || source.RuleStateDigestVersion != RuleStateDigestService.DigestVersion) throw new ArgumentException("boundary_digest_mismatch");
            return new SimulationBoundaryDto { GameId = source.GameId, SimulationEpoch = source.SimulationEpoch, MembershipVersion = source.MembershipVersion,
                GlobalFenceSequence = source.GlobalFenceSequence, CommandPrefixDigest = source.CommandPrefixDigest, RuleStateDigestVersion = source.RuleStateDigestVersion,
                RuleStateDigest = source.RuleStateDigest, FactDigest = digest, Windows = source.Windows.Select(fact => new SimulationWindowFactDto {
                    Window = SnapshotWindow(fact.Window, source.GameId, source.SimulationEpoch, source.MembershipVersion), AvailablePlayerIds = fact.AvailablePlayerIds.OrderBy(id => id).ToList(),
                    IsClosed = fact.IsClosed, IsNewWindow = fact.IsNewWindow, RequiresPermit = fact.RequiresPermit, FinalCommandSequence = fact.FinalCommandSequence, CommandCount = fact.CommandCount, WindowPrefixDigest = fact.WindowPrefixDigest }).ToList() };
        }

        internal static string NormalizeDigest(string digest)
        {
            DigestBytes(digest); return digest.ToLowerInvariant();
        }

        private static void ValidateProposal(OperationCommandProposalDto proposal)
        {
            if (proposal == null) throw new ArgumentNullException(nameof(proposal));
            ValidateIdentity(proposal.GameId, proposal.SimulationEpoch, proposal.MembershipVersion);
            var command = proposal.Command;
            if (proposal.ProtocolVersion != OperationCommandProtocol.Version || proposal.DigestVersion != DigestVersion ||
                proposal.ClientSubmissionSequence <= 0 || proposal.ClientSubmissionSequence == long.MaxValue ||
                proposal.ObservedGlobalSequence < 0 || proposal.ObservedStateRevision < 0 ||
                string.IsNullOrWhiteSpace(proposal.OperationWindowId) || proposal.OperationWindowId.Length > 256 || proposal.WindowGeneration <= 0 ||
                command == null || command.PlayerId <= 0 || string.IsNullOrWhiteSpace(command.CommandId) || command.CommandId.Length > 256 ||
                !Enum.IsDefined(typeof(GameCommandKind), command.Kind) || command.OptionIds != null &&
                (command.OptionIds.Count > 1024 || command.OptionIds.Any(string.IsNullOrWhiteSpace)) ||
                command.Parameters != null && command.Parameters.Count > 256)
                throw new ArgumentException("proposal_shape_invalid");
            if (command.Parameters != null)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parameter in command.Parameters)
                    if (parameter == null || string.IsNullOrWhiteSpace(parameter.Key) || !keys.Add(parameter.Key))
                        throw new ArgumentException("command_parameter_duplicate_or_invalid");
            }
            if (!InteractionRequestValidity.TryReadMarker(command.ToCommand(), out _, out _, out var markerValidation))
                throw new ArgumentException(markerValidation.Reason);
        }

        private static byte[] Encode(Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, true))
            { write(writer); writer.Flush(); if (stream.Length > MaximumPayloadBytes) throw new ArgumentException("command_payload_too_large"); return stream.ToArray(); }
        }

        private static void Text(BinaryWriter writer, string value)
        {
            if (value == null) { writer.Write(-1); return; }
            int count = Utf8.GetByteCount(value);
            if (count > MaximumFieldBytes || writer.BaseStream.Length + 4L + count > MaximumPayloadBytes)
                throw new ArgumentException("command_payload_too_large");
            writer.Write(count); writer.Write(Utf8.GetBytes(value));
        }

        private static byte[] DigestBytes(string digest)
        {
            if (digest == null || digest.Length != 64) throw new ArgumentException("command_digest_invalid");
            var bytes = new byte[32];
            for (int index = 0; index < bytes.Length; index++)
                if (!byte.TryParse(digest.Substring(index * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[index]))
                    throw new ArgumentException("command_digest_invalid");
            return bytes;
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(bytes); var result = new StringBuilder(64);
                foreach (byte value in digest) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }
    }
}
