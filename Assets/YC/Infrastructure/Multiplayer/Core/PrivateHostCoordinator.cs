using System;
using System.Collections.Generic;
using System.Linq;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Scoring;

namespace YC.Infrastructure.Multiplayer
{
    /// <summary>终局先让所有原席位停止提交，再捕获房主检查点并逐席位确认可以离开。</summary>
    public sealed class PrivateTerminalAgreement
    {
        private readonly HashSet<int> players, prepared = new HashSet<int>(), released = new HashSet<int>();
        public PrivateTerminalAgreement(IEnumerable<int> playerIds)
        {
            var ids = playerIds?.ToArray();
            if (ids == null || ids.Length < 2 || ids.Length > 4 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length)
                throw new ArgumentException("private_terminal_players_invalid");
            players = new HashSet<int>(ids);
        }
        public long Sequence { get; private set; } = -1;
        public bool Captured { get; private set; }
        public bool ReadyToCapture => Sequence >= 0 && !Captured && prepared.SetEquals(players);
        public bool AllReleased => Captured && released.SetEquals(players);
        public bool Begin(long sequence)
        {
            if (sequence < 0 || Sequence >= 0) return false;
            Sequence = sequence; return true;
        }
        public bool AcknowledgePrepared(int player, long sequence)
        {
            if (Captured || sequence != Sequence || Sequence < 0 || !players.Contains(player)) return false;
            prepared.Add(player); return true;
        }
        public bool MarkCaptured(long sequence)
        {
            if (!ReadyToCapture || sequence != Sequence) return false;
            Captured = true; return true;
        }
        public bool AcknowledgeRelease(int player, long sequence)
        {
            if (!Captured || sequence != Sequence || !players.Contains(player)) return false;
            released.Add(player); return true;
        }
        public int StageFor(int player) => !players.Contains(player) || Sequence < 0 ? 0 :
            !Captured ? 1 : released.Contains(player) ? 3 : 2;
        public void CancelBeforeCapture()
        {
            if (Captured) return;
            Sequence = -1; prepared.Clear(); released.Clear();
        }
    }

    /// <summary>与分片确认分离的安装确认：表示客户端已完整校验并应用本人视图。</summary>
    [Serializable]
    public sealed class PrivateViewInstalledReceipt
    {
        public int ProtocolVersion = 1;
        public string GameId = string.Empty, ContentPackHash = string.Empty, ConnectionToken = string.Empty;
        public long SessionEpoch, TransferId, CommittedSequence;
        public int MembershipVersion, PlayerId, StateRevision;

        public static PrivateViewInstalledReceipt From(PrivatePlayerViewSnapshot snapshot, long transferId)
        {
            if (snapshot == null || transferId <= 0) throw new ArgumentException("private_view_installed_identity_invalid");
            return new PrivateViewInstalledReceipt { GameId = snapshot.GameId, ContentPackHash = snapshot.ContentPackHash,
                ConnectionToken = snapshot.ConnectionToken, SessionEpoch = snapshot.SessionEpoch, MembershipVersion = snapshot.MembershipVersion,
                PlayerId = snapshot.PlayerId, TransferId = transferId, CommittedSequence = snapshot.CommittedSequence, StateRevision = snapshot.StateRevision };
        }
    }

    /// <summary>
    /// 房主单线程协调器：每个席位独立限流、确认安装；断开时暂停接纳，重连只同步本人。
    /// 它不调用 Mirror，调度和实际收发必须在同一规则所有者线程上接入。
    /// </summary>
    public sealed class PrivateHostCoordinator
    {
        private sealed class Peer
        {
            internal int ConnectionId, PlayerId;
            internal string Token;
            internal bool Rejoining = true;
            internal long AppliedSequence = -1, LastTransferId;
            internal int AppliedRevision = -1;
            internal PrivatePlayerViewSnapshot Snapshot;
            internal PrivatePlayerViewSendWindow Sender;
        }

        private readonly GameSession session;
        private readonly SteamIdentityBindingRegistry bindings;
        private readonly PrivatePlayerViewPublisher publisher;
        private readonly string gameId, contentHash;
        private readonly long epoch;
        private readonly int membershipVersion;
        private readonly int[] players;
        private readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();
        private long transferId, committedSequence;
        private string stateDigest;
        private string[] logMessages;
        private HashSet<string> formalWindows;
        private long automaticSaveThrough = -1;
        private bool terminalObserved;
        private int reconnectConnection = -1;
        private string reconnectRequest;
        private RegionControlPreview cachedRegionPreview;

        public PrivateHostCoordinator(GameSession session, EffectRegistry registry, SteamIdentityBindingRegistry bindings, long epoch, int membershipVersion)
        {
            if (session == null || registry == null || bindings == null || epoch <= 0 || membershipVersion <= 0 ||
                session.State.PrivateCommandLedger == null || !PrivateCommandLedgerPolicy.TryValidate(session.State, out _))
                throw new ArgumentException("private_host_configuration_invalid");
            this.session = session; this.bindings = bindings; this.epoch = epoch; this.membershipVersion = membershipVersion;
            publisher = new PrivatePlayerViewPublisher(session, registry, bindings, epoch, membershipVersion);
            gameId = session.State.GameId; contentHash = session.State.ContentPackHash;
            players = session.State.PrivateCommandLedger.PlayerIds.ToArray();
            committedSequence = session.State.PrivateCommandLedger.Receipts.Count;
            stateDigest = RuleStateDigestService.Compute(session.State);
            logMessages = ReadLogMessages(session.State);
            formalWindows = ReadFormalWindows(session.State);
            terminalObserved = session.State.FinalScoring?.IsResolved == true;
        }

        public bool Stopped { get; private set; }
        public string Fault { get; private set; } = string.Empty;
        public long CommittedSequence => committedSequence;
        public int ReadyPlayers => players.Count(player => peers.Values.Any(peer => peer.PlayerId == player && !peer.Rejoining && IsBound(peer)));
        public bool Ready => !Stopped && reconnectConnection < 0 && ReadyPlayers == players.Length;
        public bool AtInstalledBoundary => Ready && peers.Count == players.Length && peers.Values.All(peer =>
            IsBound(peer) && !peer.Rejoining && peer.Sender == null && peer.AppliedSequence == committedSequence);
        public bool HasAutomaticSaveCandidate => automaticSaveThrough >= 0;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public string ValidationLastCaptureFailure { get; private set; } = string.Empty;
#endif
        public bool HasTerminalState => session.State.FinalScoring?.IsResolved == true &&
            (session.State.Phase == GamePhase.FinalScoring || session.State.Phase == GamePhase.GameOver);

        private static HashSet<string> ReadFormalWindows(GameState state)
        {
            var copy = GameStateCloneService.DeepClone(state);
            OperationWindowService.Synchronize(copy);
            return new HashSet<string>(OperationWindowService.ReadOpenWindows(copy).Where(window =>
                string.IsNullOrEmpty(window.ParentOperationWindowId) && !string.IsNullOrEmpty(window.SourceEffectId) &&
                (window.WindowKind == RoundMainlineNodeTypeIds.PlayerActionWindow || window.WindowKind == RoundMainlineNodeTypeIds.PlayerCleanupWindow ||
                 window.WindowKind == RoundMainlineNodeTypeIds.CharacterCover || window.WindowKind == RoundMainlineNodeTypeIds.Collection ||
                 window.WindowKind == "entrance_player" || window.WindowKind == "enterprise_selection"))
                .Select(window => window.OperationWindowId + "|" + window.WindowGeneration), StringComparer.Ordinal);
        }

        private bool IsBound(Peer peer) => peer != null && bindings.TryGetPlayer(peer.ConnectionId, out int player) && player == peer.PlayerId &&
            bindings.TryGetConnection(player, out int connection) && connection == peer.ConnectionId;

        // 常规规则摘要有意排除日志正文；私密同步还需要检测会改变玩家视图的正文变动。
        private static string[] ReadLogMessages(GameState state) => state.Logs?.Select(log => log?.Message).ToArray();

        private bool Stable()
        {
            if (Stopped) return false;
            try
            {
                if (session.State.GameId == gameId && session.State.ContentPackHash == contentHash &&
                    PrivateCommandLedgerPolicy.TryValidate(session.State, out _) &&
                    session.State.PrivateCommandLedger?.Receipts.Count == committedSequence && RuleStateDigestService.Compute(session.State) == stateDigest &&
                    ((logMessages == null && session.State.Logs == null) || logMessages != null && session.State.Logs != null &&
                        logMessages.SequenceEqual(session.State.Logs.Select(log => log?.Message)))) return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { }
            Stopped = true; Fault = "private_host_untracked_state_change"; return false;
        }

        private bool StartTransfer(Peer peer, bool initial)
        {
            if (!Stable() || transferId >= long.MaxValue - 1 ||
                !publisher.TryCreate(peer.ConnectionId, peer.Token, committedSequence, out var snapshot, out _)) return false;
            peer.Snapshot = snapshot;
            peer.Sender = new PrivatePlayerViewSendWindow(snapshot, ++transferId, initial);
            return true;
        }

        public bool TryBind(int connectionId, string token)
        {
            if (!Stable() || peers.ContainsKey(connectionId) || !bindings.TryGetPlayer(connectionId, out int player) ||
                !players.Contains(player) || peers.Values.Any(peer => peer.PlayerId == player) || !publisher.TryBind(connectionId, token)) return false;
            var peer = new Peer { ConnectionId = connectionId, PlayerId = player, Token = token };
            if (!StartTransfer(peer, true)) { publisher.Forget(connectionId); return false; }
            peers.Add(connectionId, peer);
            return true;
        }

        public void Disconnect(int connectionId)
        {
            if (reconnectConnection == connectionId) { reconnectConnection = -1; reconnectRequest = null; }
            if (!peers.Remove(connectionId)) return;
            publisher.Forget(connectionId);
            bindings.UnbindConnection(connectionId);
        }

        /// <summary>先暂停命令再许可原席位主动断开；重试使用同一请求，不改变其他玩家身份。</summary>
        public bool TryApproveReconnect(int connectionId, string token, string requestId)
        {
            if (!Stable() || !Guid.TryParseExact(requestId, "N", out _) || !peers.TryGetValue(connectionId, out var peer) ||
                !IsBound(peer) || peer.Rejoining || peer.Token != token) return false;
            if (reconnectConnection >= 0) return reconnectConnection == connectionId && reconnectRequest == requestId;
            reconnectConnection = connectionId; reconnectRequest = requestId; return true;
        }

        public bool CancelReconnect(int connectionId, string token, string requestId)
        {
            if (reconnectConnection != connectionId || reconnectRequest != requestId ||
                !peers.TryGetValue(connectionId, out var peer) || !IsBound(peer) || peer.Token != token) return false;
            reconnectConnection = -1; reconnectRequest = null; return true;
        }

        public bool TryTakePart(int connectionId, string token, out PrivatePlayerViewPart part)
        {
            part = null;
            return !Stopped && peers.TryGetValue(connectionId, out var peer) && IsBound(peer) && peer.Token == token &&
                peer.Sender != null && peer.Sender.TryTake(out part);
        }

        public IReadOnlyList<PrivatePlayerViewPart> ReadUnacknowledged(int connectionId, string token)
        {
            if (Stopped || !peers.TryGetValue(connectionId, out var peer) || !IsBound(peer) || peer.Token != token || peer.Sender == null)
                return Array.Empty<PrivatePlayerViewPart>();
            return peer.Sender.Unacknowledged().ToArray();
        }

        public PrivatePlayerViewPart ReadInstallationProbe(int connectionId, string token)
        {
            return !Stopped && peers.TryGetValue(connectionId, out var peer) && IsBound(peer) && peer.Token == token
                ? peer.Sender?.InstallationProbe() : null;
        }

        public bool TryAcknowledgePart(int connectionId, PrivatePlayerViewAcknowledgment acknowledgment)
        {
            return !Stopped && peers.TryGetValue(connectionId, out var peer) && IsBound(peer) && peer.Sender != null &&
                acknowledgment?.ConnectionToken == peer.Token && peer.Sender.TryAcknowledge(acknowledgment);
        }

        public bool TryConfirmInstalled(int connectionId, PrivateViewInstalledReceipt receipt)
        {
            if (Stopped || !peers.TryGetValue(connectionId, out var peer) || !IsBound(peer) || receipt == null ||
                receipt.ProtocolVersion != 1 || receipt.GameId != gameId || receipt.ContentPackHash != contentHash ||
                receipt.ConnectionToken != peer.Token || receipt.SessionEpoch != epoch || receipt.MembershipVersion != membershipVersion ||
                receipt.PlayerId != peer.PlayerId || receipt.TransferId <= 0) return false;
            if (receipt.TransferId == peer.LastTransferId)
                return receipt.CommittedSequence == peer.AppliedSequence && receipt.StateRevision == peer.AppliedRevision;
            if (peer.Sender == null || peer.Snapshot == null || receipt.TransferId != peer.Sender.TransferId ||
                peer.Sender.AcknowledgedBytes != peer.Sender.TotalBytes || receipt.CommittedSequence != peer.Snapshot.CommittedSequence ||
                receipt.StateRevision != peer.Snapshot.StateRevision || receipt.CommittedSequence < peer.AppliedSequence) return false;
            peer.LastTransferId = receipt.TransferId; peer.AppliedSequence = receipt.CommittedSequence; peer.AppliedRevision = receipt.StateRevision;
            peer.Sender = null; peer.Snapshot = null;
            if (peer.AppliedSequence == committedSequence) { peer.Rejoining = false; return true; }
            // 旧快照安装期间发生了提交；补发最新视图，并允许接收端越过已合并的中间帧。
            if (!StartTransfer(peer, true)) { Stopped = true; Fault = "private_view_resynchronization_failed"; return false; }
            return true;
        }

        public bool TrySubmit(int connectionId, string token, OperationCommandProposalDto proposal, out PrivateCommandResult result)
        {
            result = null;
            if (!Stable() || !Ready || !peers.TryGetValue(connectionId, out var peer) || !IsBound(peer) ||
                peer.Rejoining || peer.Token != token || !publisher.TrySubmit(connectionId, token, proposal, out result)) return false;
            if (result.Status == PrivateCommandStatus.Committed)
            {
                committedSequence = result.GlobalSequence;
                stateDigest = RuleStateDigestService.Compute(session.State);
                logMessages = ReadLogMessages(session.State);
                var nextWindows = ReadFormalWindows(session.State);
                bool terminal = session.State.FinalScoring?.IsResolved == true;
                if (formalWindows.Except(nextWindows).Any() || terminal && !terminalObserved) automaticSaveThrough = committedSequence;
                formalWindows = nextWindows; terminalObserved = terminal;
                foreach (var target in peers.Values.Where(value => value.Sender == null))
                    if (!StartTransfer(target, target.AppliedSequence != committedSequence - 1))
                    { Stopped = true; Fault = "private_view_publication_failed"; break; }
            }
            return true;
        }

        /// <summary>消息头必须与当前房主会话和认证连接同时一致，不能只相信命令正文的玩家字段。</summary>
        public bool TrySubmitEnvelope(int connectionId, PrivateCommandEnvelope envelope, out PrivateCommandResultEnvelope response)
        {
            response = null;
            if (envelope == null || envelope.ProtocolVersion != 1 || envelope.GameId != gameId || envelope.ContentPackHash != contentHash ||
                envelope.SessionEpoch != epoch || envelope.MembershipVersion != membershipVersion ||
                !peers.TryGetValue(connectionId, out var peer) || envelope.PlayerId != peer.PlayerId || envelope.ConnectionToken != peer.Token ||
                envelope.Proposal?.Command?.PlayerId != peer.PlayerId || !TrySubmit(connectionId, peer.Token, envelope.Proposal, out var result)) return false;
            response = new PrivateCommandResultEnvelope { GameId = gameId, ContentPackHash = contentHash, ConnectionToken = peer.Token,
                SessionEpoch = epoch, MembershipVersion = membershipVersion, PlayerId = peer.PlayerId, Result = result };
            return true;
        }

        public bool TryQueryPaymentSources(int connectionId, string token, PaymentSourcePageRequest request, out PaymentSourcePageResponse response)
        {
            response = null;
            if (!Stable() || !Ready || !peers.TryGetValue(connectionId, out var peer) || !IsBound(peer) ||
                peer.Rejoining || peer.Token != token || !publisher.TryQueryPaymentSources(connectionId, token, request, out response)) return false;
            if (Stable()) return true;
            response = null; return false;
        }

        public bool TryQueryRegionPreview(int connectionId, string token, int stateRevision, out RegionControlPreview preview)
        {
            preview = null;
            if (!Stable() || !Ready || !peers.TryGetValue(connectionId, out var peer) || !IsBound(peer) ||
                peer.Rejoining || peer.Token != token || session.State.EffectRuntime.StateRevision != stateRevision) return false;
            if (cachedRegionPreview != null && cachedRegionPreview.StateRevision == stateRevision)
                preview = GameStateCloneService.DeepClone(cachedRegionPreview);
            else if (!publisher.TryQueryRegionPreview(connectionId, token, stateRevision, out preview)) return false;
            if (Stable()) { cachedRegionPreview = GameStateCloneService.DeepClone(preview); return true; }
            preview = null; return false;
        }

        /// <summary>只在所有原席位安装同一版本后捕获本机房主存档；调用方仍需等待真实写盘结果。</summary>
        public bool TryCaptureInstalledBoundary(out GameSessionRecoveryExport capture, out string reason)
        {
            capture = null; reason = "private_save_boundary_not_ready";
            if (!Stable() || !AtInstalledBoundary || session.ReadSaveCaptureSafety() != SaveCaptureSafety.Safe) return false;
            try
            {
                var frozen = session.CaptureStableSavePoint();
                var state = frozen?.Archive?.Snapshot?.State;
                if (state == null || state.PrivateCommandLedger?.CommittedSequence != committedSequence ||
                    RuleStateDigestService.Compute(state) != stateDigest || !Stable())
                { reason = "private_save_capture_mismatch"; return false; }
                capture = frozen; reason = string.Empty;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                ValidationLastCaptureFailure = string.Empty;
#endif
                return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                ValidationLastCaptureFailure = error.ToString();
#endif
                reason = "private_save_capture_failed"; return false;
            }
        }

        // 写盘成功后才退役本候选；写盘期间产生的较新候选必须保留。
        public void ConfirmAutomaticSaveWritten(long capturedSequence)
        {
            if (capturedSequence >= 0 && automaticSaveThrough >= 0 && automaticSaveThrough <= capturedSequence)
                automaticSaveThrough = -1;
        }
    }
}
