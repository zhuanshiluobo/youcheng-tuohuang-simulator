using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mirror;
using Newtonsoft.Json;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Scoring;

namespace YC.Infrastructure.Multiplayer
{
    public struct PrivateHelloMessage : NetworkMessage
    { public string GameId, ContentHash, Token; public long Epoch; public int Membership, PlayerId; }
    public struct PrivateRoomStatusMessage : NetworkMessage
    { public string GameId, Token, Fault; public long Epoch, Generation, TerminalSequence; public int Membership, PlayerId, ReadyPlayers, TotalPlayers, TerminalStage; public bool Ready; }
    public struct PrivateReconnectMessage : NetworkMessage
    { public string GameId, Token, RequestId; public long Epoch; public int Membership, PlayerId, Action; }
    public struct PrivateTerminalAckMessage : NetworkMessage
    { public string GameId, Token; public long Epoch, Sequence; public int Membership, PlayerId, Stage; }
    public struct PrivateRegionPreviewRequestMessage : NetworkMessage
    { public string GameId, ContentHash, Token, RequestId; public long Epoch; public int Membership, PlayerId, Revision, Part; }
    public struct PrivateRegionPreviewResponseMessage : NetworkMessage
    { public string GameId, ContentHash, Token, RequestId, Payload, Failure, Digest; public long Epoch; public int Membership, PlayerId, Revision, Part, PartCount; }
    public struct PrivateViewPartMessage : NetworkMessage
    {
        public int ProtocolVersion, PlayerId, TotalBytes, Offset;
        public string GameId, ContentHash, Token, Digest;
        public long Epoch, TransferId;
        public bool IsInitial;
        public byte[] Payload;
        public static PrivateViewPartMessage From(PrivatePlayerViewPart p) => new PrivateViewPartMessage {
            ProtocolVersion = p.ProtocolVersion, PlayerId = p.PlayerId, TotalBytes = p.TotalBytes, Offset = p.Offset,
            GameId = p.GameId, ContentHash = p.ContentPackHash, Token = p.ConnectionToken, Digest = p.Digest,
            Epoch = p.SessionEpoch, TransferId = p.TransferId, IsInitial = p.IsInitial, Payload = p.Payload };
        public PrivatePlayerViewPart ToPart() => new PrivatePlayerViewPart { ProtocolVersion = ProtocolVersion, PlayerId = PlayerId,
            TotalBytes = TotalBytes, Offset = Offset, GameId = GameId, ContentPackHash = ContentHash, ConnectionToken = Token,
            Digest = Digest, SessionEpoch = Epoch, TransferId = TransferId, IsInitial = IsInitial, Payload = Payload };
    }
    public struct PrivateViewAckMessage : NetworkMessage
    {
        public int ProtocolVersion, PlayerId, ReceivedBytes;
        public string GameId, Token;
        public long Epoch, TransferId;
        public static PrivateViewAckMessage From(PrivatePlayerViewAcknowledgment a) => new PrivateViewAckMessage {
            ProtocolVersion = a.ProtocolVersion, PlayerId = a.PlayerId, ReceivedBytes = a.ReceivedBytes,
            GameId = a.GameId, Token = a.ConnectionToken, Epoch = a.SessionEpoch, TransferId = a.TransferId };
        public PrivatePlayerViewAcknowledgment ToAck() => new PrivatePlayerViewAcknowledgment { ProtocolVersion = ProtocolVersion,
            PlayerId = PlayerId, ReceivedBytes = ReceivedBytes, GameId = GameId, ConnectionToken = Token, SessionEpoch = Epoch, TransferId = TransferId };
    }
    public struct PrivateViewInstalledMessage : NetworkMessage
    {
        public int ProtocolVersion, Membership, PlayerId, Revision;
        public string GameId, ContentHash, Token;
        public long Epoch, TransferId, Sequence;
        public static PrivateViewInstalledMessage From(PrivateViewInstalledReceipt r) => new PrivateViewInstalledMessage {
            ProtocolVersion = r.ProtocolVersion, Membership = r.MembershipVersion, PlayerId = r.PlayerId, Revision = r.StateRevision,
            GameId = r.GameId, ContentHash = r.ContentPackHash, Token = r.ConnectionToken, Epoch = r.SessionEpoch,
            TransferId = r.TransferId, Sequence = r.CommittedSequence };
        public PrivateViewInstalledReceipt ToReceipt() => new PrivateViewInstalledReceipt { ProtocolVersion = ProtocolVersion,
            MembershipVersion = Membership, PlayerId = PlayerId, StateRevision = Revision, GameId = GameId, ContentPackHash = ContentHash,
            ConnectionToken = Token, SessionEpoch = Epoch, TransferId = TransferId, CommittedSequence = Sequence };
    }
    public struct PrivatePayloadPartMessage : NetworkMessage
    {
        public int ProtocolVersion, Membership, PlayerId, TotalBytes, Offset;
        public string GameId, ContentHash, Token, Purpose, Digest;
        public long Epoch, TransferId;
        public byte[] Payload;
        public static PrivatePayloadPartMessage From(PrivatePayloadPart p) => new PrivatePayloadPartMessage {
            ProtocolVersion = p.ProtocolVersion, Membership = p.MembershipVersion, PlayerId = p.PlayerId, TotalBytes = p.TotalBytes,
            Offset = p.Offset, GameId = p.GameId, ContentHash = p.ContentPackHash, Token = p.ConnectionToken, Purpose = p.Purpose,
            Digest = p.Digest, Epoch = p.SessionEpoch, TransferId = p.TransferId, Payload = p.Payload };
        public PrivatePayloadPart ToPart() => new PrivatePayloadPart { ProtocolVersion = ProtocolVersion, MembershipVersion = Membership,
            PlayerId = PlayerId, TotalBytes = TotalBytes, Offset = Offset, GameId = GameId, ContentPackHash = ContentHash,
            ConnectionToken = Token, Purpose = Purpose, Digest = Digest, SessionEpoch = Epoch, TransferId = TransferId, Payload = Payload };
    }
    public struct PrivatePayloadAckMessage : NetworkMessage
    {
        public int ProtocolVersion, Membership, PlayerId, ReceivedBytes;
        public string GameId, ContentHash, Token, Purpose;
        public long Epoch, TransferId;
        public static PrivatePayloadAckMessage From(PrivatePayloadAcknowledgment a) => new PrivatePayloadAckMessage {
            ProtocolVersion = a.ProtocolVersion, Membership = a.MembershipVersion, PlayerId = a.PlayerId, ReceivedBytes = a.ReceivedBytes,
            GameId = a.GameId, ContentHash = a.ContentPackHash, Token = a.ConnectionToken, Purpose = a.Purpose,
            Epoch = a.SessionEpoch, TransferId = a.TransferId };
        public PrivatePayloadAcknowledgment ToAck() => new PrivatePayloadAcknowledgment { ProtocolVersion = ProtocolVersion,
            MembershipVersion = Membership, PlayerId = PlayerId, ReceivedBytes = ReceivedBytes, GameId = GameId, ContentPackHash = ContentHash,
            ConnectionToken = Token, Purpose = Purpose, SessionEpoch = Epoch, TransferId = TransferId };
    }

    public sealed partial class MirrorCommandTransport
    {
        private sealed class PrivatePeer
        {
            internal int PlayerId;
            internal string Token;
            internal PrivateHostCommandChannel Commands;
            internal PrivateHostPaymentChannel Payments;
            internal float NextRetry;
            internal string ReconnectRequest, LastCancelledReconnect;
            internal float ReconnectDeadline;
        }
        private bool privateMode, privateRoomReady, privateRetryNeeded;
        private string privateGameId, privateContentHash;
        private long privateEpoch, privateStatusGeneration, privateReceivedStatusGeneration;
        private int privateMembership;
        private int[] privatePlayers = Array.Empty<int>();
        private GameRuleConfiguration privateConfiguration;
        private int[] privateLaunchOrder;
        private PrivateHostCoordinator privateHost;
        private PrivateTerminalAgreement privateTerminalAgreement;
        private GameSessionRecoveryExport privateTerminalCapture;
        private bool privateLocalFinal, privateTerminalFrozen, privateTerminalReleased;
        private PrivateClientEndpoint privateClient;
        private PrivateClientPaymentChannel privatePayments;
        private PrivateClientCommandTracker privatePendingTracker;
        private readonly Dictionary<int, PrivatePeer> privatePeers = new Dictionary<int, PrivatePeer>();
        private readonly Dictionary<string, float> privatePaymentRequests = new Dictionary<string, float>(StringComparer.Ordinal);
        private float nextPrivateHello, nextPrivateClientRetry, nextPrivateStatus;
        private string privateReconnectRequest;
        private RegionControlPreview privateRegionPreview;
        private PrivateRegionPreviewReceiver privateRegionReceiver;
        private PrivateRegionPreviewPayload privateHostRegionPayload;
        private int privateHostRegionPayloadRevision = -1;
        private string privateRegionRequest = "";
        private int privateRegionRevision = -1;
        private bool privateRegionUnavailable;
        private float nextPrivateRegionRequest;
        private bool privateReconnectApproved;
        private float nextPrivateReconnect, privateReconnectDeadline;
        private const float PrivateRetrySeconds = 2f;

        // 实际 UI 必须先安装并验证本人视图，才发送安装确认。
        public Func<GameStateView, bool> PrivateViewInstaller { private get; set; }
        public event Action<PrivateCommandResult> PrivateCommandCompleted;
        public event Action<string> PrivateTransportFaulted;
        public event Action PrivateStatusChanged;
        public event Action RegionControlPreviewAvailable;
        public event Action<long> PrivateHostCommitted;
        public event Action<long, GameStateView> PrivateViewInstalled;
        public bool UsesPrivateTransport => initialized && privateMode;
        public bool IsPrivateRoomReady => UsesPrivateTransport && privateRoomReady && NetworkClient.isConnected &&
            privateClient?.HasInstalledView == true && !privateClient.IsFaulted;
        public bool HasPendingPrivateCommand => privateClient?.HasPendingCommand == true || privatePendingTracker?.HasPending == true;
        public bool CanSubmitPrivateCommand => IsPrivateRoomReady && !privateTerminalFrozen && !HasPendingPrivateCommand && privateClient.HasUsableWindow;
        public int PrivateReadyPlayers { get; private set; }
        public string PrivateFault { get; private set; } = string.Empty;
        public long PrivateSessionEpoch => privateEpoch;
        public int PrivateMembershipVersion => privateMembership;
        private bool HasPrivateCompletedSettlement => UsesPrivateTransport && privateLocalFinal;
        private bool CanDetachPrivateCompletedSession => HasPrivateCompletedSettlement && (mode == LaunchMode.Host
            ? privateTerminalCapture != null && privateTerminalAgreement?.AllReleased == true : privateTerminalReleased);
        public bool TryReadPrivateTerminalCapture(out GameSessionRecoveryExport capture, out bool automaticPending)
        {
            capture = privateTerminalCapture; automaticPending = privateHost?.HasAutomaticSaveCandidate == true;
            return UsesPrivateTransport && mode == LaunchMode.Host && capture != null && privateTerminalAgreement?.AllReleased == true;
        }
        public bool PrivateAutomaticSaveReady => UsesPrivateTransport && mode == LaunchMode.Host && privateHost != null &&
            privateHost.HasAutomaticSaveCandidate && privateHost.AtInstalledBoundary && privatePeers.Values.All(peer => !peer.Commands.HasPendingCommand);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal object ValidationPrivateSaveStatus()
        {
            if (!UsesPrivateTransport || mode != LaunchMode.Host || privateHost == null) return null;
            return new { privateHost.CommittedSequence, privateHost.ReadyPlayers, privateHost.Ready,
                privateHost.AtInstalledBoundary, privateHost.HasAutomaticSaveCandidate, PrivateAutomaticSaveReady,
                PendingCommands = privatePeers.Values.Count(peer => peer.Commands.HasPendingCommand),
                Safety = session?.ReadSaveCaptureSafety().ToString(), privateHost.ValidationLastCaptureFailure };
        }
#endif

        public bool TryCapturePrivateHostSave(out GameSessionRecoveryExport capture, out string reason)
        {
            capture = null; reason = "private_save_boundary_not_ready";
            return UsesPrivateTransport && mode == LaunchMode.Host && string.IsNullOrEmpty(PrivateFault) && privateHost != null &&
                privatePeers.Values.All(peer => !peer.Commands.HasPendingCommand) && privateHost.TryCaptureInstalledBoundary(out capture, out reason);
        }

        public void ConfirmPrivateAutomaticSaveWritten(long sequence)
        { if (UsesPrivateTransport && mode == LaunchMode.Host) privateHost?.ConfirmAutomaticSaveWritten(sequence); }

        /// <summary>显式初始化新私密通道；正式入口在加载、保存和 UI 链完成前不得启用。</summary>
        public void InitializePrivateTransport(GameSession gameSession, EffectRegistry registry, LaunchMode launchMode, int playerId,
            IList<PlayerSeat> seats, long epoch, int membershipVersion, Func<GameStateView, bool> installView)
        {
            var state = gameSession?.State;
            var ids = seats?.Where(seat => seat != null).Select(seat => seat.PlayerId).ToArray();
            if (state == null || registry == null || installView == null || seats == null || ids.Length != seats.Count ||
                launchMode != LaunchMode.Host && launchMode != LaunchMode.Client || epoch <= 0 || membershipVersion <= 0 ||
                ids.Length < 2 || ids.Length > 4 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length || !ids.Contains(playerId) ||
                state.SetupRecipe?.Preparation == null || state.RuleConfiguration?.ContentPackHash != state.ContentPackHash ||
                state.LaunchPlayerOrder == null || !ids.OrderBy(id => id).SequenceEqual(state.LaunchPlayerOrder.OrderBy(id => id)) ||
                launchMode == LaunchMode.Host && (state.PrivateCommandLedger == null ||
                    state.RandomRuntime?.AlgorithmVersion != GameplayRandomRuntime.PrivateAlgorithmVersion ||
                    !PrivateCommandLedgerPolicy.TryValidate(state, out _)))
                throw new ArgumentException("private_transport_configuration_invalid");
            Shutdown();
            try
            {
                session = gameSession; mode = launchMode; localPlayerId = playerId; launchSeats = seats;
                privateGameId = state.GameId; privateContentHash = state.ContentPackHash; privateEpoch = epoch; privateMembership = membershipVersion;
                privatePlayers = ids; privateLaunchOrder = state.LaunchPlayerOrder.ToArray(); privateConfiguration = state.RuleConfiguration.Clone();
                PrivateViewInstaller = installView; privateMode = initialized = true; PrivateFault = string.Empty;
                foreach (var seat in seats) seat.GameStateSynchronized = false;
                if (launchMode == LaunchMode.Host)
                {
                    bindings.ReplaceLobbySeats(seats.Select(seat => new KeyValuePair<int, ulong>(seat.PlayerId, seat.SteamId)));
                    privateHost = new PrivateHostCoordinator(gameSession, registry, bindings, epoch, membershipVersion);
                    privateTerminalAgreement = new PrivateTerminalAgreement(ids);
                    NetworkServer.OnConnectedEvent += OnServerConnected; NetworkServer.OnDisconnectedEvent += OnServerDisconnected;
                    NetworkServer.ReplaceHandler<PrivateHelloMessage>(OnPrivateHello, false);
                    NetworkServer.RegisterHandler<PrivateViewAckMessage>(OnPrivateViewAck, false);
                    NetworkServer.RegisterHandler<PrivateViewInstalledMessage>(OnPrivateViewInstalled, false);
                    NetworkServer.RegisterHandler<PrivatePayloadPartMessage>(OnPrivateClientPayload, false);
                    NetworkServer.RegisterHandler<PrivatePayloadAckMessage>(OnPrivateClientPayloadAck, false);
                    NetworkServer.RegisterHandler<PrivateReconnectMessage>(OnPrivateReconnectRequest, false);
                    NetworkServer.RegisterHandler<PrivateTerminalAckMessage>(OnPrivateTerminalAck, false);
                    NetworkServer.RegisterHandler<PrivateRegionPreviewRequestMessage>(OnPrivateRegionPreviewRequest, false);
                    foreach (var connection in NetworkServer.connections.Values.ToArray()) OnServerConnected(connection);
                }
                if (MirrorNetworkRuntime.Instance != null) MirrorNetworkRuntime.Instance.LocalClientRestarted += RegisterPrivateClientMessages;
                RegisterPrivateClientMessages();
            }
            catch { Shutdown(); throw; }
        }

        private void RegisterPrivateClientMessages()
        {
            if (!privateMode) return;
            NetworkClient.RegisterHandler<PrivateViewPartMessage>(OnPrivateViewPart, false);
            NetworkClient.RegisterHandler<PrivateRoomStatusMessage>(OnPrivateRoomStatus, false);
            NetworkClient.RegisterHandler<PrivatePayloadPartMessage>(OnPrivateHostPayload, false);
            NetworkClient.RegisterHandler<PrivatePayloadAckMessage>(OnPrivateHostPayloadAck, false);
            NetworkClient.RegisterHandler<PrivateReconnectMessage>(OnPrivateReconnectReply, false);
            NetworkClient.RegisterHandler<PrivateRegionPreviewResponseMessage>(OnPrivateRegionPreviewResponse, false);
            NetworkClient.OnConnectedEvent -= OnPrivateLocalConnected; NetworkClient.OnConnectedEvent += OnPrivateLocalConnected;
            NetworkClient.OnDisconnectedEvent -= OnPrivateLocalDisconnected; NetworkClient.OnDisconnectedEvent += OnPrivateLocalDisconnected;
            if (NetworkClient.isConnected && privateClient == null) OnPrivateLocalConnected();
        }

        private void OnPrivateLocalConnected()
        {
            if (!privateMode || !NetworkClient.isConnected || privateClient != null) return;
            clientConnectionToken = Guid.NewGuid().ToString("N"); privateRoomReady = false; privateReceivedStatusGeneration = 0;
            try
            {
                privateClient = new PrivateClientEndpoint(privateGameId, privateContentHash, clientConnectionToken, privateEpoch,
                    privateMembership, localPlayerId, privateConfiguration, privateLaunchOrder, privatePendingTracker);
                privatePayments = new PrivateClientPaymentChannel(privateGameId, privateContentHash, clientConnectionToken,
                    privateEpoch, privateMembership, localPlayerId);
                privatePaymentRequests.Clear();
                privateRetryNeeded = privatePendingTracker?.HasPending == true; privatePendingTracker = null;
                nextPrivateHello = nextPrivateClientRetry = 0f;
            }
            catch (Exception error) { StopPrivateTransport("private_client_connection_invalid", error); }
        }

        private void OnPrivateLocalDisconnected()
        {
            if (!privateMode) return;
            privateRoomReady = false;
            if (privateClient != null) privatePendingTracker = privateClient.RetainPendingTracker();
            privateClient = null; privatePayments = null; clientConnectionToken = null; privateRetryNeeded = false;
            privateRegionReceiver?.Dispose(); privateRegionReceiver = null;
            privateRegionPreview = null; privateRegionRequest = ""; privateRegionRevision = -1; privateRegionUnavailable = false; nextPrivateRegionRequest = 0;
            privateTerminalFrozen = false;
            privateReconnectRequest = null; privateReconnectApproved = false;
            NotifyPrivateStatusChanged();
            if (mode == LaunchMode.Client && MirrorNetworkRuntime.Instance != null)
                MirrorNetworkRuntime.Instance.TryBeginClientReconnect();
        }

        private bool CurrentPrivateConnection(NetworkConnectionToClient connection) => connection != null && NetworkServer.active &&
            NetworkServer.connections.TryGetValue(connection.connectionId, out var current) && ReferenceEquals(current, connection);

        private void OnPrivateHello(NetworkConnectionToClient connection, PrivateHelloMessage message)
        {
            if (!privateMode || privateHost == null || !CurrentPrivateConnection(connection) || message.GameId != privateGameId ||
                message.ContentHash != privateContentHash || message.Epoch != privateEpoch || message.Membership != privateMembership ||
                !bindings.TryGetPlayer(connection.connectionId, out int player) || player != message.PlayerId) return;
            if (privatePeers.TryGetValue(connection.connectionId, out var existing))
            { if (existing.PlayerId != player || existing.Token != message.Token) RejectConnection(connection, "私密连接令牌不一致。"); return; }
            if (!privateHost.TryBind(connection.connectionId, message.Token)) return;
            privatePeers.Add(connection.connectionId, new PrivatePeer { PlayerId = player, Token = message.Token,
                Commands = new PrivateHostCommandChannel(privateHost, connection.connectionId, privateGameId, privateContentHash,
                    message.Token, privateEpoch, privateMembership, player),
                Payments = new PrivateHostPaymentChannel(privateHost, connection.connectionId, privateGameId, privateContentHash,
                    message.Token, privateEpoch, privateMembership, player), NextRetry = Time.unscaledTime + PrivateRetrySeconds });
            nextPrivateStatus = 0f;
        }

        private void ForgetPrivatePeer(int connectionId)
        {
            if (bindings.TryGetPlayer(connectionId, out int player)) MarkGameStateSynchronized(player, false);
            privateTerminalAgreement?.CancelBeforeCapture();
            privatePeers.Remove(connectionId); privateHost?.Disconnect(connectionId); nextPrivateStatus = 0f;
        }

        private void OnPrivateTerminalAck(NetworkConnectionToClient connection, PrivateTerminalAckMessage message)
        {
            if (privateTerminalAgreement == null || !CurrentPrivateConnection(connection) || !privatePeers.TryGetValue(connection.connectionId, out var peer) ||
                message.GameId != privateGameId || message.Token != peer.Token || message.Epoch != privateEpoch ||
                message.Membership != privateMembership || message.PlayerId != peer.PlayerId) return;
            bool accepted = message.Stage == 1 ? privateTerminalAgreement.AcknowledgePrepared(peer.PlayerId, message.Sequence) :
                message.Stage == 2 && privateTerminalAgreement.AcknowledgeRelease(peer.PlayerId, message.Sequence);
            if (accepted) nextPrivateStatus = 0f;
        }

        private void OnPrivateViewAck(NetworkConnectionToClient connection, PrivateViewAckMessage message)
        {
            if (privateHost != null && CurrentPrivateConnection(connection) && privatePeers.ContainsKey(connection.connectionId))
                privateHost.TryAcknowledgePart(connection.connectionId, message.ToAck());
        }
        private void OnPrivateViewInstalled(NetworkConnectionToClient connection, PrivateViewInstalledMessage message)
        {
            if (privateHost != null && CurrentPrivateConnection(connection) && privatePeers.TryGetValue(connection.connectionId, out var peer) &&
                privateHost.TryConfirmInstalled(connection.connectionId, message.ToReceipt()))
            { MarkGameStateSynchronized(peer.PlayerId, true); nextPrivateStatus = 0f; }
        }
        private void OnPrivateClientPayload(NetworkConnectionToClient connection, PrivatePayloadPartMessage message)
        {
            if (privateHost == null || !CurrentPrivateConnection(connection) || !privatePeers.TryGetValue(connection.connectionId, out var peer)) return;
            // 全体已确认终局后不再接受规则命令；先前的合法待决命令在捕获前必须完成。
            if (privateTerminalCapture != null && message.Purpose == "command") return;
            PrivatePayloadAcknowledgment ack = null;
            var status = message.Purpose == "payment-query" ? peer.Payments.Receive(message.ToPart(), out ack) :
                message.Purpose == "command" ? peer.Commands.Receive(message.ToPart(), out ack) : PrivateHostCommandReceiveStatus.Rejected;
            if (status == PrivateHostCommandReceiveStatus.Accepted && ack != null) connection.Send(PrivatePayloadAckMessage.From(ack));
            if (peer.Commands.Faulted || peer.Payments.Faulted) RejectConnection(connection, "私密连接消息无效。");
        }

        public RegionControlPreview ReadPrivateRegionPreview(string gameId, string contentHash, int revision)
        {
            if (!IsPrivateRoomReady || gameId != privateGameId || contentHash != privateContentHash || revision < 0) return null;
            if (privateRegionPreview != null && privateRegionPreview.GameId == gameId &&
                privateRegionPreview.ContentHash == contentHash && privateRegionPreview.StateRevision == revision)
                return GameStateCloneService.DeepClone(privateRegionPreview);
            if (privateRegionRevision == revision && privateRegionUnavailable) return null;
            if (privateRegionRevision != revision)
            {
                privateRegionReceiver?.Dispose(); privateRegionReceiver = new PrivateRegionPreviewReceiver();
                privateRegionPreview = null; privateRegionRevision = revision; privateRegionUnavailable = false;
                privateRegionRequest = Guid.NewGuid().ToString("N"); nextPrivateRegionRequest = 0;
            }
            float now = Time.unscaledTime;
            if (privateRegionReceiver != null && now >= nextPrivateRegionRequest)
            {
                nextPrivateRegionRequest = now + 3f;
                NetworkClient.Send(new PrivateRegionPreviewRequestMessage { GameId = gameId, ContentHash = contentHash,
                    Token = clientConnectionToken, RequestId = privateRegionRequest, Epoch = privateEpoch,
                    Membership = privateMembership, PlayerId = localPlayerId, Revision = revision, Part = privateRegionReceiver.NextPart });
            }
            return null;
        }

        private void OnPrivateRegionPreviewRequest(NetworkConnectionToClient connection, PrivateRegionPreviewRequestMessage message)
        {
            if (privateHost == null || !CurrentPrivateConnection(connection) || !privatePeers.TryGetValue(connection.connectionId, out var peer) ||
                message.GameId != privateGameId || message.ContentHash != privateContentHash || message.Token != peer.Token ||
                message.PlayerId != peer.PlayerId || message.Epoch != privateEpoch || message.Membership != privateMembership ||
                message.Revision < 0 || message.Part < 0 || !Guid.TryParseExact(message.RequestId, "N", out _)) return;
            try
            {
                if (!privateHost.TryQueryRegionPreview(connection.connectionId, peer.Token, message.Revision, out var preview)) return;
                if (privateHostRegionPayload == null || privateHostRegionPayloadRevision != message.Revision)
                {
                    privateHostRegionPayload = new PrivateRegionPreviewPayload(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(preview)));
                    privateHostRegionPayloadRevision = message.Revision;
                }
                var response = new PrivateRegionPreviewResponseMessage { GameId = privateGameId, ContentHash = privateContentHash,
                    Token = peer.Token, RequestId = message.RequestId, Epoch = privateEpoch, Membership = privateMembership,
                    PlayerId = peer.PlayerId, Revision = message.Revision, Part = message.Part,
                    PartCount = privateHostRegionPayload.PartCount, Digest = privateHostRegionPayload.Digest,
                    Payload = privateHostRegionPayload.ReadPart(message.Part), Failure = "" };
                // Mirror 对超出单包大小的消息只记录错误并丢弃；先检测并返回不可用，避免客户端持续重试。
                using (var writer = NetworkWriterPool.Get())
                {
                    NetworkMessages.Pack(response, writer);
                    if (writer.Position > NetworkMessages.MaxMessageSize(Channels.Reliable))
                        throw new InvalidOperationException("region_preview_transport_size_exceeded");
                }
                connection.Send(response);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                Debug.LogException(error, this);
                connection.Send(new PrivateRegionPreviewResponseMessage { GameId = privateGameId, ContentHash = privateContentHash,
                    Token = peer.Token, RequestId = message.RequestId, Epoch = privateEpoch, Membership = privateMembership,
                    PlayerId = peer.PlayerId, Revision = message.Revision, Part = message.Part, Digest = "", Payload = "", Failure = "unavailable" });
            }
        }

        private void OnPrivateRegionPreviewResponse(PrivateRegionPreviewResponseMessage message)
        {
            if (!IsPrivateRoomReady || message.GameId != privateGameId || message.ContentHash != privateContentHash ||
                message.Token != clientConnectionToken || message.RequestId != privateRegionRequest ||
                message.Epoch != privateEpoch || message.Membership != privateMembership || message.PlayerId != localPlayerId ||
                message.Revision != privateRegionRevision || privateRegionReceiver == null || message.Part != privateRegionReceiver.NextPart || message.Failure == null ||
                message.Failure != "" && message.Failure != "unavailable" ||
                (message.Failure == "" ? string.IsNullOrEmpty(message.Payload) : !string.IsNullOrEmpty(message.Payload))) return;
            if (message.Failure == "unavailable")
            { privateRegionUnavailable = true; privateRegionReceiver.Dispose(); privateRegionReceiver = null; return; }
            try
            {
                if (!privateRegionReceiver.TryAppend(message.Part, message.PartCount, message.Digest, message.Payload, out var bytes))
                { privateRegionUnavailable = true; privateRegionReceiver.Dispose(); privateRegionReceiver = null; return; }
                if (bytes == null)
                {
                    nextPrivateRegionRequest = 0;
                    ReadPrivateRegionPreview(privateGameId, privateContentHash, privateRegionRevision);
                    return;
                }
                privateRegionReceiver.Dispose(); privateRegionReceiver = null;
                var preview = JsonConvert.DeserializeObject<RegionControlPreview>(Encoding.UTF8.GetString(bytes),
                    new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 32 });
                if (preview == null || preview.GameId != privateGameId || preview.ContentHash != privateContentHash ||
                    preview.StateRevision != privateRegionRevision) { privateRegionUnavailable = true; return; }
                privateRegionPreview = preview;
                foreach (Action listener in RegionControlPreviewAvailable?.GetInvocationList() ?? Array.Empty<Delegate>())
                    try { listener(); } catch (Exception error) { Debug.LogException(error, this); }
            }
            catch (JsonException error) { privateRegionUnavailable = true; Debug.LogException(error, this); }
        }
        private void OnPrivateClientPayloadAck(NetworkConnectionToClient connection, PrivatePayloadAckMessage message)
        {
            if (privateHost != null && CurrentPrivateConnection(connection) && privatePeers.TryGetValue(connection.connectionId, out var peer))
            {
                if (message.Purpose == "result") peer.Commands.TryAcknowledgeResult(message.ToAck());
                else if (message.Purpose == "payment-result") peer.Payments.Acknowledge(message.ToAck());
            }
        }

        private PrivateReconnectMessage PrivateReconnect(int action, string token, int playerId, string request) => new PrivateReconnectMessage {
            GameId = privateGameId, Token = token, Epoch = privateEpoch, Membership = privateMembership,
            PlayerId = playerId, RequestId = request, Action = action };
        private void OnPrivateReconnectRequest(NetworkConnectionToClient connection, PrivateReconnectMessage message)
        {
            if (privateHost == null || !CurrentPrivateConnection(connection) || !privatePeers.TryGetValue(connection.connectionId, out var peer) ||
                message.GameId != privateGameId || message.Token != peer.Token || message.PlayerId != peer.PlayerId ||
                message.Epoch != privateEpoch || message.Membership != privateMembership || !Guid.TryParseExact(message.RequestId, "N", out _)) return;
            if (message.Action == 1)
            {
                bool accepted = peer.LastCancelledReconnect != message.RequestId &&
                    privateHost.TryApproveReconnect(connection.connectionId, peer.Token, message.RequestId);
                if (accepted && peer.ReconnectRequest != message.RequestId)
                { peer.ReconnectRequest = message.RequestId; peer.ReconnectDeadline = Time.unscaledTime + 60f; }
                connection.Send(PrivateReconnect(accepted ? 2 : 3, peer.Token, peer.PlayerId, message.RequestId));
                nextPrivateStatus = 0f;
            }
            else if (message.Action == 4 && privateHost.CancelReconnect(connection.connectionId, peer.Token, message.RequestId))
            { peer.ReconnectRequest = null; peer.LastCancelledReconnect = message.RequestId; nextPrivateStatus = 0f; }
        }
        private void OnPrivateReconnectReply(PrivateReconnectMessage message)
        {
            if (!privateMode || privateClient == null || message.GameId != privateGameId || message.Token != clientConnectionToken ||
                message.PlayerId != localPlayerId || message.Epoch != privateEpoch || message.Membership != privateMembership ||
                string.IsNullOrEmpty(privateReconnectRequest) || message.RequestId != privateReconnectRequest) return;
            if (message.Action == 2) privateReconnectApproved = true;
            else if (message.Action == 3) { privateReconnectRequest = null; privateReconnectApproved = false; NotifyPrivateStatusChanged(); }
        }

        private bool CanRequestPrivateReconnect()
        {
            var runtime = MirrorNetworkRuntime.Instance;
            return UsesPrivateTransport && mode == LaunchMode.Client && string.IsNullOrEmpty(PrivateFault) && runtime != null &&
                !NetworkServer.active && runtime.HasClientReconnectIdentity && (runtime.IsLocalTestMode || runtime.ClientReconnectGameId == privateGameId) &&
                (runtime.IsReconnectingClient || runtime.IsClientReconnectWaitingForRetry && !NetworkClient.isConnected ||
                 NetworkClient.isConnected && privateClient?.HasInstalledView == true && string.IsNullOrEmpty(privateReconnectRequest));
        }
        private bool TryRequestPrivateReconnect()
        {
            if (!CanRequestPrivateReconnect()) return false;
            var runtime = MirrorNetworkRuntime.Instance;
            if (!NetworkClient.isConnected || runtime.IsReconnectingClient) return runtime.TryRequestClientReconnect();
            privateReconnectRequest = Guid.NewGuid().ToString("N"); privateReconnectApproved = false;
            privateReconnectDeadline = Time.unscaledTime + 60f; nextPrivateReconnect = Time.unscaledTime + PrivateRetrySeconds;
            NetworkClient.Send(PrivateReconnect(1, clientConnectionToken, localPlayerId, privateReconnectRequest));
            NotifyPrivateStatusChanged(); return true;
        }

        private void OnPrivateViewPart(PrivateViewPartMessage message)
        {
            if (!privateMode || privateClient == null || !NetworkClient.isConnected) return;
            if (!privateClient.ReceiveView(message.ToPart(), out var ack, out var installation, out var reason))
            { if (privateClient.IsFaulted) StopPrivateTransport(reason); return; }
            if (installation != null)
            {
                try {
                    var view = privateClient.ReadView();
                    if (PrivateViewInstaller?.Invoke(view) != true) { StopPrivateTransport("private_view_install_failed"); return; }
                    privateLocalFinal = view.FinalScoring?.IsResolved == true && (view.Phase == GamePhase.FinalScoring || view.Phase == GamePhase.GameOver);
                    if (!privateLocalFinal) { privateTerminalFrozen = false; privateTerminalReleased = false; }
                    foreach (Action<long, GameStateView> listener in PrivateViewInstalled?.GetInvocationList() ?? Array.Empty<Delegate>())
                        try { listener(privateClient.CommittedSequence, view); } catch (Exception error) { Debug.LogException(error, this); }
                }
                catch (Exception error) { StopPrivateTransport("private_view_install_failed", error); return; }
            }
            if (ack != null) NetworkClient.Send(PrivateViewAckMessage.From(ack));
            if (installation != null) NetworkClient.Send(PrivateViewInstalledMessage.From(installation));
        }
        private void OnPrivateRoomStatus(PrivateRoomStatusMessage message)
        {
            if (!privateMode || privateClient == null || message.GameId != privateGameId || message.Token != clientConnectionToken ||
                message.Epoch != privateEpoch || message.Membership != privateMembership || message.PlayerId != localPlayerId ||
                message.TotalPlayers != privatePlayers.Length || message.ReadyPlayers < 0 || message.ReadyPlayers > message.TotalPlayers ||
                message.Ready && message.ReadyPlayers != message.TotalPlayers || message.Fault == null || message.Fault.Length > 128 ||
                message.TerminalStage < 0 || message.TerminalStage > 3 || message.TerminalStage > 0 && message.TerminalSequence < 0 ||
                message.Generation <= privateReceivedStatusGeneration) return;
            bool changed = privateRoomReady != message.Ready || PrivateReadyPlayers != message.ReadyPlayers;
            privateReceivedStatusGeneration = message.Generation; PrivateReadyPlayers = message.ReadyPlayers; privateRoomReady = message.Ready;
            if (!string.IsNullOrEmpty(message.Fault)) StopPrivateTransport(message.Fault);
            else
            {
                if (message.TerminalStage == 0 && !privateTerminalReleased) privateTerminalFrozen = false;
                else if (message.TerminalStage > 0 && privateLocalFinal && privateClient.CommittedSequence == message.TerminalSequence)
                {
                    privateTerminalFrozen = true;
                    if (!HasPendingPrivateCommand)
                    {
                        if (message.TerminalStage == 3) privateTerminalReleased = true;
                        else NetworkClient.Send(new PrivateTerminalAckMessage { GameId = privateGameId, Token = clientConnectionToken,
                            Epoch = privateEpoch, Membership = privateMembership, PlayerId = localPlayerId,
                            Sequence = message.TerminalSequence, Stage = message.TerminalStage });
                    }
                }
                if (changed) NotifyPrivateStatusChanged();
            }
        }
        private void OnPrivateHostPayload(PrivatePayloadPartMessage message)
        {
            if (!privateMode || privateClient == null || !NetworkClient.isConnected) return;
            if (message.Purpose == "payment-result")
            {
                if (privatePayments == null || !privatePayments.Receive(message.ToPart(), out var paymentAck, out bool changed))
                { if (privatePayments?.Faulted == true) StopPrivateTransport(privatePayments.Failure); return; }
                if (paymentAck != null) NetworkClient.Send(PrivatePayloadAckMessage.From(paymentAck));
                if (changed)
                    foreach (Action listener in PaymentSourcePageAvailable?.GetInvocationList() ?? Array.Empty<Delegate>())
                        try { listener(); } catch (Exception error) { Debug.LogException(error, this); }
            }
            else if (message.Purpose == "result")
            {
                if (!privateClient.ReceiveResult(message.ToPart(), out var ack, out var reason))
                { if (privateClient.IsFaulted) StopPrivateTransport(reason); return; }
                if (ack != null) NetworkClient.Send(PrivatePayloadAckMessage.From(ack));
            }
        }
        private void OnPrivateHostPayloadAck(PrivatePayloadAckMessage message)
        {
            if (!privateMode || privateClient == null) return;
            if (message.Purpose == "command") privateClient.TryAcknowledgeCommand(message.ToAck());
            else if (message.Purpose == "payment-query") privatePayments?.Acknowledge(message.ToAck());
        }

        private PaymentSourceProjection ReadPrivatePaymentSourcePage(PaymentSourceQuery query)
        {
            if (!IsPrivateRoomReady || privatePayments == null || query?.PlayerId != localPlayerId || !PaymentSourcePageProtocol.Valid(query)) return null;
            string id = PaymentSourcePageProtocol.Id(query);
            float now = Time.unscaledTime;
            bool allowStart = !privatePaymentRequests.TryGetValue(id, out float previous) || now - previous >= 3f;
            string before = privatePayments.PendingRequestId;
            var page = privatePayments.Read(query, allowStart);
            if (before != id && privatePayments.PendingRequestId == id)
            {
                if (privatePaymentRequests.Count >= 256)
                {
                    foreach (string old in privatePaymentRequests.Where(entry => now - entry.Value >= 3f).Select(entry => entry.Key).ToArray())
                        privatePaymentRequests.Remove(old);
                    if (privatePaymentRequests.Count >= 256)
                        privatePaymentRequests.Remove(privatePaymentRequests.OrderBy(entry => entry.Value).First().Key);
                }
                privatePaymentRequests[id] = now;
            }
            if (privatePayments.Faulted) StopPrivateTransport(privatePayments.Failure);
            return page;
        }

        private CommandResult SubmitPrivateCommand(GameCommand command)
        {
            if (!IsPrivateRoomReady || privateTerminalFrozen || HasPendingPrivateCommand) return Invalid("联机同步尚未就绪。");
            if (!privateClient.TryBeginCommand(command, null, out string reason)) return Invalid(reason);
            nextPrivateClientRetry = Time.unscaledTime + PrivateRetrySeconds;
            return CommandResult.SuccessResult(new List<YC.Domain.Events.GameEvent>(), "命令已发送，等待房主确认。");
        }

        private void UpdatePrivateTransport()
        {
            if (!initialized || !privateMode || !string.IsNullOrEmpty(PrivateFault)) return;
            try
            {
                float now = Time.unscaledTime;
                if (mode == LaunchMode.Host && privateHost != null)
                {
                    RefreshPendingLocalBindings();
                    foreach (var entry in privatePeers.ToArray())
                    {
                        if (!NetworkServer.connections.TryGetValue(entry.Key, out var connection)) continue;
                        var peer = entry.Value;
                        if (!string.IsNullOrEmpty(peer.ReconnectRequest) && now >= peer.ReconnectDeadline)
                        {
                            string expired = peer.ReconnectRequest;
                            if (privateHost.CancelReconnect(entry.Key, peer.Token, expired))
                            { peer.ReconnectRequest = null; peer.LastCancelledReconnect = expired;
                              connection.Send(PrivateReconnect(3, peer.Token, peer.PlayerId, expired)); nextPrivateStatus = 0f; }
                        }
                        long before = privateHost.CommittedSequence;
                        if (!peer.Commands.Pump()) { StopPrivateTransport(peer.Commands.Fault); return; }
                        if (privateHost.CommittedSequence > before)
                        {
                            privateTerminalAgreement.CancelBeforeCapture(); nextPrivateStatus = 0f;
                            foreach (Action<long> listener in PrivateHostCommitted?.GetInvocationList() ?? Array.Empty<Delegate>())
                                try { listener(privateHost.CommittedSequence); } catch (Exception error) { Debug.LogException(error, this); }
                        }
                        if (!peer.Payments.Pump()) { StopPrivateTransport(peer.Payments.Fault); return; }
                        while (privateHost.TryTakePart(entry.Key, peer.Token, out var view)) connection.Send(PrivateViewPartMessage.From(view));
                        while (peer.Commands.TryTakeResult(out var result)) connection.Send(PrivatePayloadPartMessage.From(result));
                        while (peer.Payments.TryTakeResult(out var payment)) connection.Send(PrivatePayloadPartMessage.From(payment));
                        if (now >= peer.NextRetry)
                        {
                            peer.NextRetry = now + PrivateRetrySeconds;
                            foreach (var view in privateHost.ReadUnacknowledged(entry.Key, peer.Token)) connection.Send(PrivateViewPartMessage.From(view));
                            var probe = privateHost.ReadInstallationProbe(entry.Key, peer.Token);
                            if (probe != null) connection.Send(PrivateViewPartMessage.From(probe));
                            foreach (var result in peer.Commands.ReadUnacknowledgedResult()) connection.Send(PrivatePayloadPartMessage.From(result));
                            foreach (var payment in peer.Payments.ReadUnacknowledged()) connection.Send(PrivatePayloadPartMessage.From(payment));
                        }
                    }
                    if (privateHost.Stopped) { StopPrivateTransport(privateHost.Fault); return; }
                    bool noPending = privatePeers.Values.All(peer => !peer.Commands.HasPendingCommand && !peer.Commands.HasUnacknowledgedResult);
                    if (privateTerminalAgreement.Sequence < 0 && privateHost.HasTerminalState && privateHost.AtInstalledBoundary && noPending &&
                        privateTerminalAgreement.Begin(privateHost.CommittedSequence)) nextPrivateStatus = 0f;
                    if (privateTerminalAgreement.ReadyToCapture && privateHost.AtInstalledBoundary && noPending &&
                        privateHost.CommittedSequence == privateTerminalAgreement.Sequence &&
                        privateHost.TryCaptureInstalledBoundary(out var finalCapture, out _) &&
                        privateTerminalAgreement.MarkCaptured(privateHost.CommittedSequence))
                    { privateTerminalCapture = finalCapture; nextPrivateStatus = 0f; }
                    if (now >= nextPrivateStatus)
                    {
                        nextPrivateStatus = now + 1f;
                        if (privateStatusGeneration >= long.MaxValue - 1) { StopPrivateTransport("private_status_generation_exhausted"); return; }
                        privateStatusGeneration++;
                        foreach (var entry in privatePeers)
                            if (NetworkServer.connections.TryGetValue(entry.Key, out var connection)) connection.Send(new PrivateRoomStatusMessage {
                                GameId = privateGameId, Token = entry.Value.Token, Epoch = privateEpoch, Membership = privateMembership,
                                PlayerId = entry.Value.PlayerId, Generation = privateStatusGeneration, ReadyPlayers = privateHost.ReadyPlayers,
                                TotalPlayers = privatePlayers.Length, Ready = privateHost.Ready, Fault = string.Empty,
                                TerminalStage = privateTerminalAgreement.StageFor(entry.Value.PlayerId), TerminalSequence = privateTerminalAgreement.Sequence });
                    }
                }
                if (NetworkClient.isConnected && privateClient != null)
                {
                    if (privateReconnectApproved)
                    {
                        string request = privateReconnectRequest; privateReconnectRequest = null; privateReconnectApproved = false;
                        if (!(MirrorNetworkRuntime.Instance?.TryReconnectAfterHostPause() ?? false) && NetworkClient.isConnected)
                            NetworkClient.Send(PrivateReconnect(4, clientConnectionToken, localPlayerId, request));
                        NotifyPrivateStatusChanged(); return;
                    }
                    if (!string.IsNullOrEmpty(privateReconnectRequest) && now >= privateReconnectDeadline)
                    {
                        NetworkClient.Send(PrivateReconnect(4, clientConnectionToken, localPlayerId, privateReconnectRequest));
                        privateReconnectRequest = null; NotifyPrivateStatusChanged();
                    }
                    else if (!string.IsNullOrEmpty(privateReconnectRequest) && now >= nextPrivateReconnect)
                    {
                        nextPrivateReconnect = now + PrivateRetrySeconds;
                        NetworkClient.Send(PrivateReconnect(1, clientConnectionToken, localPlayerId, privateReconnectRequest));
                    }
                    if (!privateClient.HasInstalledView && now >= nextPrivateHello)
                    {
                        nextPrivateHello = now + PrivateRetrySeconds;
                        NetworkClient.Send(new PrivateHelloMessage { GameId = privateGameId, ContentHash = privateContentHash,
                            Token = clientConnectionToken, Epoch = privateEpoch, Membership = privateMembership, PlayerId = localPlayerId });
                    }
                    if (privateRetryNeeded && IsPrivateRoomReady && privateClient.TryPrepareRetry(out _))
                    { privateRetryNeeded = false; nextPrivateClientRetry = now + PrivateRetrySeconds; }
                    while (privateClient.TryTakeCommandPart(out var command)) NetworkClient.Send(PrivatePayloadPartMessage.From(command));
                    while (privatePayments != null && privatePayments.TryTakeRequest(out var payment)) NetworkClient.Send(PrivatePayloadPartMessage.From(payment));
                    if (now >= nextPrivateClientRetry)
                    {
                        nextPrivateClientRetry = now + PrivateRetrySeconds;
                        foreach (var command in privateClient.ReadUnacknowledgedCommand()) NetworkClient.Send(PrivatePayloadPartMessage.From(command));
                        if (privatePayments != null) foreach (var payment in privatePayments.ReadUnacknowledged()) NetworkClient.Send(PrivatePayloadPartMessage.From(payment));
                    }
                    if (privateClient.TryTakeCompleted(out var completed))
                    {
                        foreach (Action<PrivateCommandResult> listener in PrivateCommandCompleted?.GetInvocationList() ?? Array.Empty<Delegate>())
                            try { listener(completed); } catch (Exception error) { Debug.LogException(error, this); }
                    }
                }
            }
            catch (Exception error) { StopPrivateTransport("private_transport_execution_failed", error); }
        }

        private void StopPrivateTransport(string reason, Exception error = null)
        {
            if (!string.IsNullOrEmpty(PrivateFault)) return;
            PrivateFault = string.IsNullOrEmpty(reason) ? "private_transport_failed" : reason; InitialLoadingFailure = PrivateFault; privateRoomReady = false;
            if (mode == LaunchMode.Host && privateHost != null && NetworkServer.active && privateStatusGeneration < long.MaxValue - 1)
            {
                privateStatusGeneration++;
                foreach (var entry in privatePeers)
                    if (NetworkServer.connections.TryGetValue(entry.Key, out var connection)) connection.Send(new PrivateRoomStatusMessage {
                        GameId = privateGameId, Token = entry.Value.Token, Epoch = privateEpoch, Membership = privateMembership,
                        PlayerId = entry.Value.PlayerId, Generation = privateStatusGeneration, ReadyPlayers = privateHost.ReadyPlayers,
                        TotalPlayers = privatePlayers.Length, Ready = false, Fault = PrivateFault.Length <= 128 ? PrivateFault : "private_transport_failed" });
            }
            if (error != null) Debug.LogException(error, this);
            foreach (Action<string> listener in PrivateTransportFaulted?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { listener(PrivateFault); } catch (Exception callback) { Debug.LogException(callback, this); }
        }

        private void NotifyPrivateStatusChanged()
        {
            foreach (Action listener in PrivateStatusChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { listener(); } catch (Exception error) { Debug.LogException(error, this); }
        }

        private void ShutdownPrivateTransport()
        {
            if (!privateMode) return;
            if (MirrorNetworkRuntime.Instance != null) MirrorNetworkRuntime.Instance.LocalClientRestarted -= RegisterPrivateClientMessages;
            NetworkClient.OnConnectedEvent -= OnPrivateLocalConnected; NetworkClient.OnDisconnectedEvent -= OnPrivateLocalDisconnected;
            // 初始化可发生在传输尚未连接时；即使当前 inactive，也必须撤销已注册的处理器。
            NetworkServer.UnregisterHandler<PrivateHelloMessage>(); NetworkServer.UnregisterHandler<PrivateViewAckMessage>();
            NetworkServer.UnregisterHandler<PrivateViewInstalledMessage>(); NetworkServer.UnregisterHandler<PrivatePayloadPartMessage>();
            NetworkServer.UnregisterHandler<PrivatePayloadAckMessage>(); NetworkServer.UnregisterHandler<PrivateReconnectMessage>();
            NetworkServer.UnregisterHandler<PrivateTerminalAckMessage>();
            NetworkServer.UnregisterHandler<PrivateRegionPreviewRequestMessage>();
            NetworkClient.UnregisterHandler<PrivateViewPartMessage>(); NetworkClient.UnregisterHandler<PrivateRoomStatusMessage>();
            NetworkClient.UnregisterHandler<PrivatePayloadPartMessage>(); NetworkClient.UnregisterHandler<PrivatePayloadAckMessage>();
            NetworkClient.UnregisterHandler<PrivateReconnectMessage>();
            NetworkClient.UnregisterHandler<PrivateRegionPreviewResponseMessage>();
            privatePeers.Clear(); privatePaymentRequests.Clear(); privateHost = null; privateClient = null; privatePayments = null; privatePendingTracker = null;
            privateTerminalAgreement = null; privateTerminalCapture = null; privateLocalFinal = privateTerminalFrozen = privateTerminalReleased = false;
            privatePlayers = Array.Empty<int>(); privateLaunchOrder = null; privateConfiguration = null; privateGameId = privateContentHash = null;
            privateMode = privateRoomReady = privateRetryNeeded = false; privateStatusGeneration = privateReceivedStatusGeneration = 0;
            privateReconnectRequest = null; privateReconnectApproved = false;
            privateRegionReceiver?.Dispose(); privateRegionReceiver = null; privateHostRegionPayload = null; privateHostRegionPayloadRevision = -1;
            privateRegionPreview = null; privateRegionRequest = ""; privateRegionRevision = -1; privateRegionUnavailable = false; nextPrivateRegionRequest = 0;
            PrivateReadyPlayers = 0; PrivateFault = string.Empty; PrivateViewInstaller = null;
        }

        internal static void RegisterEarlyPrivateHandler()
        {
            // 客户端可能先于房主进入游戏场景；丢弃早到握手，客户端会按当前连接令牌重试。
            NetworkServer.ReplaceHandler<PrivateHelloMessage>((_, __) => { }, false);
        }
    }
}
