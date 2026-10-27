using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    // 本机固定保存句柄，不是网络/磁盘协议；不会引用后续活动状态。
    public sealed class SimulationWindowSavePoint
    {
        private readonly SimulationPublishedCheckpoint checkpoint;
        private readonly bool stable;
        private bool confirmed;
        public string Id { get; }
        public string GameId { get; }
        public string ContentHash { get; }
        public string MapId { get; }
        public int Round { get; }
        public int LocalPlayerId { get; }
        public long SimulationEpoch { get; }
        public long GlobalSequence { get; }
        public bool IsTerminal { get; }
        public IReadOnlyList<string> ClosedWindowIds { get; }
        internal SimulationWindowSavePoint(GameSessionRecoveryPoint point, SimulationRecoveryBaselineDto metadata,
            DeterministicSimulationSessionOptions options, IEnumerable<string> closed, bool terminal)
        {
            checkpoint = new SimulationPublishedCheckpoint(point, metadata, options);
            GameId = options.GameId; ContentHash = options.ContentPackHash; LocalPlayerId = options.LocalPlayerId;
            SimulationEpoch = options.SimulationEpoch; GlobalSequence = metadata.ConfirmedGlobalSequence;
            MapId = point.CapturedState.MapId; Round = point.CapturedState.Round; IsTerminal = terminal;
            ClosedWindowIds = closed.OrderBy(id => id, StringComparer.Ordinal).Distinct().ToList().AsReadOnly();
            Id = WindowExecutionCodec.Hash("yc.formal-window-save.v1", GameId, SimulationEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                GlobalSequence.ToString(System.Globalization.CultureInfo.InvariantCulture), metadata.CommandPrefixDigest, terminal ? "terminal" : "window_end", WindowExecutionCodec.Hash(ClosedWindowIds.ToArray()));
            stable = EffectTreeExecutor.IsStableForSave(point.CapturedState);
        }
        internal void Confirm() { confirmed = true; }
        public Task<SimulationRecoveryCheckpointDto> CreateCheckpointAsync()
        {
            if (!confirmed || !stable) return Task.FromException<SimulationRecoveryCheckpointDto>(new InvalidOperationException("window_save_point_not_confirmed_or_stable"));
            return checkpoint.CreateAsync(SimulationEpoch + 1);
        }
    }

    // 出队仅发生在写盘成功之后；失败和重复通知都不丢失原固定点。
    public sealed class WindowAutoSaveQueue
    {
        private readonly Queue<SimulationWindowSavePoint> pending = new Queue<SimulationWindowSavePoint>();
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        public int Count => pending.Count;
        public bool Enqueue(SimulationWindowSavePoint point)
        { if (point == null || !seen.Add(point.Id)) return false; pending.Enqueue(point); return true; }
        public bool TryPeek(out SimulationWindowSavePoint point) { point = pending.Count == 0 ? null : pending.Peek(); return point != null; }
        public void Complete(string id)
        { if (pending.Count == 0 || pending.Peek().Id != id) throw new InvalidOperationException("auto_save_completion_order_mismatch"); pending.Dequeue(); }
    }

    public sealed partial class DeterministicSimulationCoordinator
    {
        private readonly SortedDictionary<long, SimulationWindowSavePoint> pendingWindowSaves = new SortedDictionary<long, SimulationWindowSavePoint>();
        private readonly HashSet<long> publishedWindowSaves = new HashSet<long>();
        private bool saveTerminalObserved;
        public event Action<SimulationWindowSavePoint> AutomaticSavePointConfirmed;

        private static bool FormalSaveWindow(OperationWindowState window) => window != null && string.IsNullOrEmpty(window.ParentOperationWindowId) &&
            !string.IsNullOrEmpty(window.SourceEffectId) && (window.WindowKind == RoundMainlineNodeTypeIds.PlayerActionWindow ||
            window.WindowKind == RoundMainlineNodeTypeIds.PlayerCleanupWindow || window.WindowKind == RoundMainlineNodeTypeIds.CharacterCover ||
            window.WindowKind == RoundMainlineNodeTypeIds.Collection || window.WindowKind == "entrance_player" || window.WindowKind == "enterprise_selection");
        private static bool SaveTerminal(GameState state) => state.FinalScoring?.IsResolved == true && (state.Phase == GamePhase.GameOver || state.Phase == GamePhase.FinalScoring);
        private void CaptureWindowSaveCandidate(IReadOnlyList<string> closed, bool terminal)
        {
            if (!options.IsHost || LastAppliedSequence <= options.CheckpointGlobalSequence || closed.Count == 0 && !terminal) return;
            long fence = LastAppliedSequence;
            if (pendingWindowSaves.ContainsKey(fence) || publishedWindowSaves.Contains(fence)) return;
            var command = replica.GetAppliedCommand(fence);
            var frozen = session.ReadLatestBackgroundRecoveryPoint();
            if (command == null || frozen.CommandId != command.Command.CommandId)
                throw new InvalidOperationException("window_save_capture_missing");
            var prefix = replica.CreateValidationPrefix(0, fence);
            var metadata = new SimulationRecoveryBaselineDto { GameId = options.GameId, SourceSimulationEpoch = options.SimulationEpoch,
                MembershipVersion = options.MembershipVersion, ConfirmedGlobalSequence = fence, CommandPrefixDigest = prefix.PrefixDigest,
                ContentPackHash = options.ContentPackHash, RuleDigestVersion = RuleStateDigestService.DigestVersion,
                FrozenPlayerIds = new List<int>(options.FrozenPlayerIds), RequiredReplicaIds = new List<string>(options.RequiredReplicaIds),
                ConfirmedCommands = replica.ReadConfirmedReceipts(fence) };
            foreach (int player in options.FrozenPlayerIds) metadata.NextSubmissionSequences.Add(player, 1);
            foreach (var receipt in metadata.ConfirmedCommands) metadata.NextSubmissionSequences[receipt.Outcome.PlayerId] = receipt.ClientSubmissionSequence + 1;
            foreach (var w in windows.Values)
            {
                var progress = replica.ReadWindowProgress(w.Descriptor.OperationWindowId);
                metadata.Windows.Add(new SimulationWindowFactDto { Window = w.Descriptor, AvailablePlayerIds = new List<int>(w.Available), IsClosed = !w.Open,
                    FinalCommandSequence = progress.LastWindowCommandSequence, CommandCount = progress.CommandCount, WindowPrefixDigest = progress.PrefixDigest });
            }
            pendingWindowSaves.Add(fence, new SimulationWindowSavePoint(frozen, metadata, options, closed, terminal));
        }
        private void PublishWindowSavePoints()
        {
            if (!options.IsHost) return;
            // Ready可乱序抵达；较新边界先确认时先保留，防止旧点稍后覆盖同局的较新自动档。
            while (pendingWindowSaves.Count > 0)
            {
                var next = pendingWindowSaves.First();
                if (!permits.ContainsKey(next.Key)) return;
                pendingWindowSaves.Remove(next.Key);
                if (!publishedWindowSaves.Add(next.Key)) continue;
                var point = next.Value; point.Confirm(); Emit(() => Deliver(AutomaticSavePointConfirmed, point));
            }
        }
    }
}
