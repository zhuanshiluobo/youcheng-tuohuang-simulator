using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Infrastructure.Multiplayer
{
    /// <summary>只承载一个已认证席位的玩家视图，不包含权威状态或其他玩家的命令。</summary>
    [Serializable]
    public sealed class PrivatePlayerViewSnapshot
    {
        public const int CurrentProtocolVersion = 1;
        public int ProtocolVersion = CurrentProtocolVersion;
        public string GameId = string.Empty, ContentPackHash = string.Empty, ConnectionToken = string.Empty;
        public long SessionEpoch, CommittedSequence, NextSubmissionSequence = 1;
        public int PlayerId, StateRevision, MembershipVersion = 1;
        public GameStateView View;
        public List<PrivateOperationWindowView> Windows = new List<PrivateOperationWindowView>();
    }

    [Serializable]
    public sealed class PrivateOperationWindowView
    {
        public string OperationWindowId = string.Empty, WindowKind = string.Empty;
        public int WindowGeneration;
        public List<PrivateRequestAuthorityView> Requests = new List<PrivateRequestAuthorityView>();
    }

    [Serializable]
    public sealed class PrivateRequestAuthorityView
    {
        public string InteractionId = string.Empty, RequestDigest = string.Empty;
        public int ContractVersion;
    }

    /// <summary>传输前只编码玩家视图；接收端必须再交给对应席位的接收器验证身份与序号。</summary>
    public static class PrivatePlayerViewWireProtocol
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly string[] EnvelopeFields = { "ProtocolVersion", "GameId", "ContentPackHash", "ConnectionToken",
            "SessionEpoch", "CommittedSequence", "NextSubmissionSequence", "MembershipVersion", "PlayerId", "StateRevision", "View", "Windows" };
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings {
            TypeNameHandling = TypeNameHandling.None, MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 128 };

        public static byte[] Encode(PrivatePlayerViewSnapshot snapshot)
        {
            if (snapshot?.View == null) throw new ArgumentException("private_view_snapshot_missing");
            return GameStateViewTransfer.Encode(JsonConvert.SerializeObject(snapshot, Settings));
        }

        public static bool TryDecode(string json, out PrivatePlayerViewSnapshot snapshot)
        {
            snapshot = null;
            try
            {
                if (string.IsNullOrEmpty(json) || Utf8.GetByteCount(json) > GameStateViewTransfer.MaximumBytes) return false;
                using var text = new StringReader(json);
                using var reader = new JsonTextReader(text) { MaxDepth = 128, DateParseHandling = DateParseHandling.None };
                var root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read() || root.Properties().Any(property => !EnvelopeFields.Contains(property.Name)) ||
                    EnvelopeFields.Any(name => root.Property(name) == null) ||
                    root["ProtocolVersion"]?.Type != JTokenType.Integer || root["GameId"]?.Type != JTokenType.String ||
                    root["ContentPackHash"]?.Type != JTokenType.String || root["ConnectionToken"]?.Type != JTokenType.String ||
                    root["SessionEpoch"]?.Type != JTokenType.Integer || root["CommittedSequence"]?.Type != JTokenType.Integer ||
                    root["NextSubmissionSequence"]?.Type != JTokenType.Integer || root["MembershipVersion"]?.Type != JTokenType.Integer ||
                    root["PlayerId"]?.Type != JTokenType.Integer || root["StateRevision"]?.Type != JTokenType.Integer ||
                    !(root["View"] is JObject view) || !(root["Windows"] is JArray windows) ||
                    windows.Any(window => !(window is JObject) || window["OperationWindowId"]?.Type != JTokenType.String ||
                        window["WindowKind"]?.Type != JTokenType.String || window["WindowGeneration"]?.Type != JTokenType.Integer ||
                        !(window["Requests"] is JArray requests) || requests.Any(request => !(request is JObject) ||
                            request["InteractionId"]?.Type != JTokenType.String || request["RequestDigest"]?.Type != JTokenType.String ||
                            request["ContractVersion"]?.Type != JTokenType.Integer))) return false;
                foreach (var field in typeof(GameStateView).GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (field.Name == nameof(GameStateView.Preparation)) continue; // 无中间请求时按合同省略。
                    var property = view.Property(field.Name);
                    if (property == null || typeof(IList).IsAssignableFrom(field.FieldType) && property.Value.Type != JTokenType.Array) return false;
                }
                if (view["SchemaVersion"]?.Type != JTokenType.Integer || view["ViewerRole"]?.Type != JTokenType.Integer ||
                    view["ViewerPlayerId"]?.Type != JTokenType.Integer || view["StateRevision"]?.Type != JTokenType.Integer ||
                    view["GameId"]?.Type != JTokenType.String || view["ContentPackHash"]?.Type != JTokenType.String ||
                    !(view["ItemZones"] is JObject zones) || !(zones["Items"] is JArray) || !(zones["Zones"] is JArray) ||
                    !(zones["Orders"] is JArray) || !(zones["Relations"] is JArray) || !(view["RuleConfiguration"] is JObject) ||
                    !(view["Map"] is JObject) || !(view["Decks"] is JObject) || !(view["GameBox"] is JObject)) return false;
                if (view.Property("Preparation") != null)
                {
                    if (!(view["Preparation"] is JObject preparation) || !(preparation["OwnEnterpriseCandidates"] is JArray candidates) ||
                        !(preparation["Requests"] is JArray requests) || candidates.Any(item => !(item is JObject) ||
                            item["ItemId"]?.Type != JTokenType.String || item["DefinitionId"]?.Type != JTokenType.String) ||
                        requests.Any(item => !(item is JObject) || item["InteractionId"]?.Type != JTokenType.String ||
                            item["OwnerEffectId"]?.Type != JTokenType.String || item["CandidateSetId"]?.Type != JTokenType.String ||
                            item["CandidateSetVersion"]?.Type != JTokenType.Integer || item["StateRevision"]?.Type != JTokenType.Integer)) return false;
                }
                snapshot = root.ToObject<PrivatePlayerViewSnapshot>(JsonSerializer.Create(Settings));
                return snapshot?.View != null;
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { snapshot = null; return false; }
        }
    }

    [Serializable]
    public sealed class PrivatePlayerViewPart
    {
        public const int CurrentProtocolVersion = 1;
        public int ProtocolVersion = CurrentProtocolVersion;
        public string GameId = string.Empty, ContentPackHash = string.Empty, ConnectionToken = string.Empty, Digest = string.Empty;
        public long SessionEpoch, TransferId;
        public int PlayerId, TotalBytes, Offset;
        public bool IsInitial;
        public byte[] Payload;
    }

    [Serializable]
    public sealed class PrivatePlayerViewAcknowledgment
    {
        public int ProtocolVersion = PrivatePlayerViewPart.CurrentProtocolVersion;
        public string GameId = string.Empty, ConnectionToken = string.Empty;
        public long SessionEpoch, TransferId;
        public int PlayerId, ReceivedBytes;
    }

    /// <summary>每个席位单独维护发送窗口，避免一次填满 Steam 发送缓冲。</summary>
    public sealed class PrivatePlayerViewSendWindow
    {
        public const int MaximumInFlightBytes = 4 * GameStateViewTransfer.ChunkBytes;
        private readonly byte[] bytes;
        private readonly PrivatePlayerViewPart identity;
        public int SentBytes { get; private set; }
        public int AcknowledgedBytes { get; private set; }
        public int TotalBytes => bytes.Length;
        public long TransferId => identity.TransferId;

        public PrivatePlayerViewSendWindow(PrivatePlayerViewSnapshot snapshot, long transferId, bool initial)
        {
            if (snapshot == null || transferId <= 0 || snapshot.SessionEpoch <= 0 || snapshot.PlayerId <= 0 ||
                string.IsNullOrWhiteSpace(snapshot.GameId) || string.IsNullOrWhiteSpace(snapshot.ContentPackHash) ||
                !PrivatePlayerViewPublisher.ValidToken(snapshot.ConnectionToken)) throw new ArgumentException("private_view_transfer_identity_invalid");
            bytes = PrivatePlayerViewWireProtocol.Encode(snapshot);
            identity = new PrivatePlayerViewPart { GameId = snapshot.GameId, ContentPackHash = snapshot.ContentPackHash,
                ConnectionToken = snapshot.ConnectionToken, SessionEpoch = snapshot.SessionEpoch, PlayerId = snapshot.PlayerId,
                TransferId = transferId, IsInitial = initial, TotalBytes = bytes.Length, Digest = PrivateViewHash.Compute(bytes) };
        }

        private PrivatePlayerViewPart PartAt(int offset)
        {
            var payload = new byte[Math.Min(GameStateViewTransfer.ChunkBytes, bytes.Length - offset)];
            Buffer.BlockCopy(bytes, offset, payload, 0, payload.Length);
            return new PrivatePlayerViewPart { GameId = identity.GameId, ContentPackHash = identity.ContentPackHash,
                ConnectionToken = identity.ConnectionToken, SessionEpoch = identity.SessionEpoch, PlayerId = identity.PlayerId,
                TransferId = identity.TransferId, IsInitial = identity.IsInitial, TotalBytes = bytes.Length,
                Digest = identity.Digest, Offset = offset, Payload = payload };
        }

        public bool TryTake(out PrivatePlayerViewPart part)
        {
            part = null;
            if (SentBytes >= bytes.Length) return false;
            int count = Math.Min(GameStateViewTransfer.ChunkBytes, bytes.Length - SentBytes);
            if (SentBytes - AcknowledgedBytes + count > MaximumInFlightBytes) return false;
            part = PartAt(SentBytes); SentBytes += count; return true;
        }

        public bool TryAcknowledge(PrivatePlayerViewAcknowledgment acknowledgment)
        {
            if (acknowledgment == null || acknowledgment.ProtocolVersion != PrivatePlayerViewPart.CurrentProtocolVersion ||
                acknowledgment.GameId != identity.GameId || acknowledgment.ConnectionToken != identity.ConnectionToken ||
                acknowledgment.SessionEpoch != identity.SessionEpoch || acknowledgment.TransferId != identity.TransferId ||
                acknowledgment.PlayerId != identity.PlayerId || acknowledgment.ReceivedBytes < 0 || acknowledgment.ReceivedBytes > SentBytes ||
                acknowledgment.ReceivedBytes != bytes.Length && acknowledgment.ReceivedBytes % GameStateViewTransfer.ChunkBytes != 0) return false;
            if (acknowledgment.ReceivedBytes > AcknowledgedBytes) AcknowledgedBytes = acknowledgment.ReceivedBytes;
            return true;
        }

        public IEnumerable<PrivatePlayerViewPart> Unacknowledged()
        {
            for (int offset = AcknowledgedBytes; offset < SentBytes; offset += GameStateViewTransfer.ChunkBytes) yield return PartAt(offset);
        }

        // 所有字节都已确认但安装确认丢失时，重发末片以便接收端重复确认安装。
        public PrivatePlayerViewPart InstallationProbe() => AcknowledgedBytes == bytes.Length
            ? PartAt((bytes.Length - 1) / GameStateViewTransfer.ChunkBytes * GameStateViewTransfer.ChunkBytes) : null;
    }

    public enum PrivatePlayerViewPartStatus { Buffered, Completed, AlreadyCompleted, Rejected }

    /// <summary>可靠分片按连接组装；乱序暂不推进，重发内容必须与已收字节一致。</summary>
    public sealed class PrivatePlayerViewPartAssembler
    {
        private readonly string gameId, contentHash, token;
        private readonly long epoch;
        private readonly int playerId;
        private PrivatePlayerViewPart active, completed;
        private byte[] buffer;
        private int received;
        private List<string> completedChunkHashes;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public int ReceivedBytes => active != null ? received : completed?.TotalBytes ?? 0;
        public int TotalBytes => active?.TotalBytes ?? completed?.TotalBytes ?? 0;

        public PrivatePlayerViewPartAssembler(string gameId, string contentHash, long epoch, int playerId, string token)
        {
            if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(contentHash) || epoch <= 0 || playerId <= 0 ||
                !PrivatePlayerViewPublisher.ValidToken(token)) throw new ArgumentException("private_view_assembler_configuration_invalid");
            this.gameId = gameId; this.contentHash = contentHash; this.epoch = epoch; this.playerId = playerId; this.token = token;
        }

        private static bool SameTransfer(PrivatePlayerViewPart left, PrivatePlayerViewPart right) => left != null && right != null &&
            left.ProtocolVersion == right.ProtocolVersion && left.GameId == right.GameId && left.ContentPackHash == right.ContentPackHash && left.ConnectionToken == right.ConnectionToken &&
            left.SessionEpoch == right.SessionEpoch && left.PlayerId == right.PlayerId && left.TransferId == right.TransferId &&
            left.TotalBytes == right.TotalBytes && left.IsInitial == right.IsInitial && left.Digest == right.Digest;
        private static PrivatePlayerViewPart Header(PrivatePlayerViewPart part) => new PrivatePlayerViewPart {
            GameId = part.GameId, ContentPackHash = part.ContentPackHash, ConnectionToken = part.ConnectionToken,
            SessionEpoch = part.SessionEpoch, PlayerId = part.PlayerId, TransferId = part.TransferId, TotalBytes = part.TotalBytes,
            IsInitial = part.IsInitial, Digest = part.Digest };
        private PrivatePlayerViewAcknowledgment Ack(PrivatePlayerViewPart part, int count) => new PrivatePlayerViewAcknowledgment {
            GameId = gameId, ConnectionToken = token, SessionEpoch = epoch, PlayerId = playerId, TransferId = part.TransferId, ReceivedBytes = count };
        private void ClearActive() { active = null; buffer = null; received = 0; }

        public PrivatePlayerViewPartStatus Receive(PrivatePlayerViewPart part, out PrivatePlayerViewAcknowledgment acknowledgment,
            out PrivatePlayerViewSnapshot snapshot, out string reason)
        {
            acknowledgment = null; snapshot = null; reason = "private_view_part_invalid";
            if (part == null || part.ProtocolVersion != PrivatePlayerViewPart.CurrentProtocolVersion ||
                part.GameId != gameId || part.ContentPackHash != contentHash || part.ConnectionToken != token ||
                part.SessionEpoch != epoch || part.PlayerId != playerId || part.TransferId <= 0 || part.TotalBytes <= 0 ||
                part.TotalBytes > GameStateViewTransfer.MaximumBytes || !PrivateViewHash.Valid(part.Digest) || part.Payload == null ||
                part.Offset < 0 || part.Offset >= part.TotalBytes || part.Offset % GameStateViewTransfer.ChunkBytes != 0 ||
                part.Payload.Length != Math.Min(GameStateViewTransfer.ChunkBytes, part.TotalBytes - part.Offset)) return PrivatePlayerViewPartStatus.Rejected;
            if (completed != null && part.TransferId <= completed.TransferId)
            {
                int index = part.Offset / GameStateViewTransfer.ChunkBytes;
                if (!SameTransfer(completed, part) || index >= completedChunkHashes.Count || completedChunkHashes[index] != PrivateViewHash.Compute(part.Payload))
                { reason = "private_view_completed_part_conflict"; return PrivatePlayerViewPartStatus.Rejected; }
                acknowledgment = Ack(part, completed.TotalBytes); reason = string.Empty; return PrivatePlayerViewPartStatus.AlreadyCompleted;
            }
            if (active == null)
            {
                // 暂时跳过乱序片段；只确认实际连续接收的前缀。
                if (part.Offset != 0) { acknowledgment = Ack(part, 0); reason = string.Empty; return PrivatePlayerViewPartStatus.Buffered; }
                active = Header(part); buffer = new byte[part.TotalBytes]; received = 0;
            }
            if (!SameTransfer(active, part)) { reason = "private_view_transfer_conflict"; return PrivatePlayerViewPartStatus.Rejected; }
            if (part.Offset > received) { acknowledgment = Ack(part, received); reason = string.Empty; return PrivatePlayerViewPartStatus.Buffered; }
            if (part.Offset < received)
            {
                if (part.Offset + part.Payload.Length > received) { reason = "private_view_part_overlap"; return PrivatePlayerViewPartStatus.Rejected; }
                for (int index = 0; index < part.Payload.Length; index++)
                    if (buffer[part.Offset + index] != part.Payload[index]) { reason = "private_view_part_conflict"; return PrivatePlayerViewPartStatus.Rejected; }
                acknowledgment = Ack(part, received); reason = string.Empty; return PrivatePlayerViewPartStatus.Buffered;
            }
            Buffer.BlockCopy(part.Payload, 0, buffer, received, part.Payload.Length); received += part.Payload.Length;
            if (received != buffer.Length) { acknowledgment = Ack(part, received); reason = string.Empty; return PrivatePlayerViewPartStatus.Buffered; }
            try
            {
                if (PrivateViewHash.Compute(buffer) != active.Digest || !PrivatePlayerViewWireProtocol.TryDecode(Utf8.GetString(buffer), out snapshot) ||
                    snapshot.GameId != gameId || snapshot.ContentPackHash != contentHash || snapshot.ConnectionToken != token ||
                    snapshot.SessionEpoch != epoch || snapshot.PlayerId != playerId)
                { snapshot = null; ClearActive(); reason = "private_view_payload_conflict"; return PrivatePlayerViewPartStatus.Rejected; }
                completedChunkHashes = new List<string>();
                for (int offset = 0; offset < buffer.Length; offset += GameStateViewTransfer.ChunkBytes)
                {
                    var chunk = new byte[Math.Min(GameStateViewTransfer.ChunkBytes, buffer.Length - offset)];
                    Buffer.BlockCopy(buffer, offset, chunk, 0, chunk.Length); completedChunkHashes.Add(PrivateViewHash.Compute(chunk));
                }
                completed = active; acknowledgment = Ack(part, received); ClearActive(); reason = string.Empty; return PrivatePlayerViewPartStatus.Completed;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            { snapshot = null; ClearActive(); reason = "private_view_payload_invalid"; return PrivatePlayerViewPartStatus.Rejected; }
        }
    }

    internal static class PrivateViewHash
    {
        internal static string Compute(byte[] bytes)
        {
            using var hash = SHA256.Create();
            var digest = hash.ComputeHash(bytes);
            return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
        }
        internal static bool Valid(string digest) => digest != null && digest.Length == 64 &&
            digest.All(character => character >= '0' && character <= '9' || character >= 'a' && character <= 'f');
    }

    /// <summary>由持有权威会话的执行线程调用；连接认证仍由 Steam 席位绑定负责。</summary>
    public sealed class PrivatePlayerViewPublisher
    {
        private readonly GameSession session;
        private readonly EffectRegistry registry;
        private readonly SteamIdentityBindingRegistry bindings;
        private readonly string gameId, contentHash;
        private readonly long epoch;
        private readonly int membershipVersion;
        private readonly Dictionary<int, KeyValuePair<int, string>> boundConnections = new Dictionary<int, KeyValuePair<int, string>>();

        public PrivatePlayerViewPublisher(GameSession session, EffectRegistry registry, SteamIdentityBindingRegistry bindings, long epoch, int membershipVersion = 1)
        {
            if (session == null || registry == null || bindings == null || epoch <= 0 || membershipVersion <= 0 ||
                string.IsNullOrWhiteSpace(session.State.GameId) || string.IsNullOrWhiteSpace(session.State.ContentPackHash) ||
                registry.ContentPackHash != session.State.ContentPackHash)
                throw new ArgumentException("private_view_publisher_configuration_invalid");
            this.session = session; this.registry = registry; this.bindings = bindings; this.epoch = epoch; this.membershipVersion = membershipVersion;
            gameId = session.State.GameId; contentHash = session.State.ContentPackHash;
        }

        public bool TryBind(int connectionId, string token)
        {
            if (connectionId < 0 || !ValidToken(token) || !bindings.TryGetPlayer(connectionId, out int player) ||
                session.State.FindPlayer(player) == null) return false;
            boundConnections[connectionId] = new KeyValuePair<int, string>(player, token);
            return true;
        }

        public void Forget(int connectionId) => boundConnections.Remove(connectionId);

        private bool TryResolve(int connectionId, string token, out int playerId)
        {
            playerId = -1;
            if (!boundConnections.TryGetValue(connectionId, out var bound) || bound.Value != token ||
                !bindings.TryGetPlayer(connectionId, out int actualPlayer) || actualPlayer != bound.Key ||
                !bindings.TryGetConnection(actualPlayer, out int actualConnection) || actualConnection != connectionId ||
                session.State.GameId != gameId || session.State.ContentPackHash != contentHash || registry.ContentPackHash != contentHash)
                return false;
            playerId = actualPlayer;
            return true;
        }

        /// <summary>只有当前认证连接能提交本人命令；调用者只能向该连接返回结果。</summary>
        public bool TrySubmit(int connectionId, string token, OperationCommandProposalDto proposal, out PrivateCommandResult result)
        {
            result = null;
            if (!TryResolve(connectionId, token, out int playerId)) return false;
            result = session.SubmitAuthenticatedPrivateCommand(proposal, playerId, epoch, membershipVersion, contentHash);
            return true;
        }

        /// <summary>分页查询只使用连接认证出的席位，返回值仅发给该连接。</summary>
        public bool TryQueryPaymentSources(int connectionId, string token, PaymentSourcePageRequest request, out PaymentSourcePageResponse response)
        {
            response = null;
            if (!TryResolve(connectionId, token, out int playerId)) return false;
            response = session.QueryPaymentSourcePage(playerId, request);
            return true;
        }

        public bool TryQueryRegionPreview(int connectionId, string token, int stateRevision, out RegionControlPreview preview)
        {
            preview = null;
            if (!TryResolve(connectionId, token, out int playerId)) return false;
            preview = session.QueryRegionControlPreview(playerId, gameId, contentHash, stateRevision, registry);
            return preview != null;
        }

        // committedSequence 必须来自房主正式命令结果，本类不自行产生业务序号。
        public bool TryCreate(int connectionId, string token, long committedSequence, out PrivatePlayerViewSnapshot snapshot, out string reason)
        {
            snapshot = null; reason = "private_view_connection_invalid";
            if (!TryResolve(connectionId, token, out int actualPlayer) || committedSequence < 0 || committedSequence == long.MaxValue ||
                session.State.PrivateCommandLedger != null && (!PrivateCommandLedgerPolicy.TryValidate(session.State, out _) ||
                    committedSequence != session.State.PrivateCommandLedger.Receipts.Count)) return false;
            try
            {
                var view = session.ProjectForPlayerWithContent(actualPlayer, registry);
                if (view == null || view.ViewerRole != GameStateViewerRole.Player || view.ViewerPlayerId != actualPlayer ||
                    view.GameId != gameId || view.ContentPackHash != contentHash || !view.HasValidPlayerOrderAndSlots())
                { reason = "private_view_projection_invalid"; return false; }
                snapshot = new PrivatePlayerViewSnapshot { GameId = gameId, ContentPackHash = contentHash,
                    ConnectionToken = token, SessionEpoch = epoch, CommittedSequence = committedSequence,
                    MembershipVersion = membershipVersion, PlayerId = actualPlayer, StateRevision = view.StateRevision, View = view };
                if (session.State.PrivateCommandLedger != null)
                {
                    snapshot.NextSubmissionSequence = session.State.PrivateCommandLedger.Receipts.LongCount(item => item.PlayerId == actualPlayer) + 1;
                    snapshot.Windows = CreateOwnWindows(session.State, view, actualPlayer);
                }
                reason = string.Empty; return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            { reason = error is InvalidOperationException ? error.Message : "private_view_projection_failed"; return false; }
        }

        private static List<PrivateOperationWindowView> CreateOwnWindows(GameState state, GameStateView view, int playerId)
        {
            var probe = GameStateCloneService.DeepClone(state);
            OperationWindowService.Synchronize(probe);
            var windows = OperationWindowService.ReadOpenWindows(probe).Where(window => window.AvailablePlayerIds.Contains(playerId))
                .Select(window => new PrivateOperationWindowView { OperationWindowId = window.OperationWindowId,
                    WindowKind = window.WindowKind, WindowGeneration = window.WindowGeneration }).ToList();
            foreach (var request in view.Interactions.Where(item => item != null && item.AnsweringPlayerId == playerId && item.Status == "open"))
            {
                var command = new GameCommand { PlayerId = playerId, Kind = GameCommandKind.AnswerInteraction, CommandId = "private-view-probe" };
                command.Parameters["interactionId"] = request.InteractionId;
                if (!OperationWindowService.TryResolveCommandContext(probe, command, out var context, out _)) continue;
                var window = windows.SingleOrDefault(item => item.OperationWindowId == context.OperationWindowId && item.WindowGeneration == context.WindowGeneration);
                if (window == null) throw new InvalidOperationException("private_view_request_window_invalid");
                var projected = new PrivateRequestAuthorityView { InteractionId = request.InteractionId };
                if (InteractionRequestValidity.TryReadRequestAuthority(probe, command, out var authority, out _))
                { projected.ContractVersion = InteractionRequestValidity.ContractVersion; projected.RequestDigest = authority.Digest; }
                window.Requests.Add(projected);
            }
            return windows;
        }

        internal static bool ValidToken(string token) => !string.IsNullOrWhiteSpace(token) && token.Length <= 64 && !token.Any(char.IsControl);
    }

    public enum PrivatePlayerViewReceiveStatus { Applied, Duplicate, NeedsResynchronization, Rejected }

    /// <summary>只接收目标本人、当前连接和版本的视图；缺序号时等待重新同步，不应用半份状态。</summary>
    public sealed class PrivatePlayerViewReceiver
    {
        private readonly string gameId, contentHash, token;
        private readonly long epoch;
        private readonly int playerId, membershipVersion;
        private readonly GameRuleConfiguration configuration;
        private readonly int[] playerOrder;
        private long sequence = -1;
        private int revision = -1;
        private GameStateView current;
        private long nextSubmissionSequence;
        private List<PrivateOperationWindowView> windows;

        public PrivatePlayerViewReceiver(string gameId, string contentHash, long epoch, int playerId, string token,
            GameRuleConfiguration configuration, IEnumerable<int> playerOrder, int membershipVersion = 1)
        {
            var order = playerOrder?.ToArray();
            if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(contentHash) || epoch <= 0 || playerId <= 0 ||
                membershipVersion <= 0 || !PrivatePlayerViewPublisher.ValidToken(token) || configuration == null || configuration.ContentPackHash != contentHash ||
                order == null || order.Length < 2 || order.Length > 4 || order.Any(id => id <= 0) ||
                order.Distinct().Count() != order.Length || !order.Contains(playerId) || configuration.PlayerCount != order.Length)
                throw new ArgumentException("private_view_receiver_configuration_invalid");
            this.gameId = gameId; this.contentHash = contentHash; this.epoch = epoch; this.playerId = playerId; this.token = token; this.membershipVersion = membershipVersion;
            this.configuration = configuration.Clone(); this.playerOrder = order;
        }

        public long CommittedSequence => sequence;
        public long NextSubmissionSequence => nextSubmissionSequence;
        public bool HasWindows => windows != null && windows.Count > 0;
        public string GameId => gameId;
        public string ContentPackHash => contentHash;
        public long SessionEpoch => epoch;
        public int MembershipVersion => membershipVersion;
        public int PlayerId => playerId;
        public GameStateView ReadView() => current == null ? null : GameStateCloneService.DeepClone(current);
        public IReadOnlyList<PrivateOperationWindowView> ReadWindows() => windows == null ? null : GameStateCloneService.DeepClone(windows);

        /// <summary>仅使用已安装的本人视图和窗口构造提案；业务许可仍由房主二次核验。</summary>
        public bool TryBuildProposal(GameCommand command, string requestedWindowId, out OperationCommandProposalDto proposal, out string reason)
        {
            proposal = null; reason = "private_command_view_not_ready";
            if (current == null || windows == null || command == null || command.PlayerId != playerId ||
                string.IsNullOrWhiteSpace(command.CommandId)) return false;
            try
            {
                var owned = GameCommandDto.FromCommand(command).ToCommand();
                PrivateOperationWindowView window;
                PrivateRequestAuthorityView request = null;
                if (owned.Kind == GameCommandKind.AnswerInteraction)
                {
                    string id = owned.Parameters.TryGetValue("interactionId", out var parameter) ? parameter : string.Empty;
                    if (string.IsNullOrEmpty(id)) id = !string.IsNullOrEmpty(owned.SourceId) ? owned.SourceId : owned.TargetId;
                    var matching = windows.Where(item => item.Requests.Any(entry => entry.InteractionId == id)).ToList();
                    if (matching.Count != 1) { reason = "private_command_request_not_visible"; return false; }
                    window = matching[0]; request = window.Requests.Single(item => item.InteractionId == id);
                    if (request.ContractVersion != 0)
                    {
                        if (!InteractionRequestValidity.TryReadMarker(owned, out bool present, out string supplied, out _) ||
                            present && supplied != request.RequestDigest)
                        { reason = "private_command_request_contract_conflict"; return false; }
                        owned.Parameters[InteractionRequestValidity.VersionParameter] = request.ContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        owned.Parameters[InteractionRequestValidity.DigestParameter] = request.RequestDigest;
                    }
                }
                else
                {
                    var matching = string.IsNullOrEmpty(requestedWindowId) ? windows : windows.Where(item => item.OperationWindowId == requestedWindowId).ToList();
                    if (matching.Count == 1) window = matching[0];
                    else if (matching.Count > 1 && string.IsNullOrEmpty(requestedWindowId) &&
                        InteractionRequestValidity.TryReadMarker(owned, out bool hasMarker, out _, out _) && !hasMarker)
                        window = new PrivateOperationWindowView { OperationWindowId = PrivateCommandWindowSelection.HostResolvedId,
                            WindowKind = "host_resolved", WindowGeneration = PrivateCommandWindowSelection.HostResolvedGeneration };
                    else { reason = "private_command_window_required"; return false; }
                }
                if (!string.IsNullOrEmpty(requestedWindowId) && requestedWindowId != window.OperationWindowId)
                { reason = "private_command_window_mismatch"; return false; }
                proposal = new OperationCommandProposalDto { GameId = gameId, SimulationEpoch = epoch, MembershipVersion = membershipVersion,
                    ClientSubmissionSequence = nextSubmissionSequence, ObservedGlobalSequence = sequence, ObservedStateRevision = revision,
                    OperationWindowId = window.OperationWindowId, WindowGeneration = window.WindowGeneration, Command = GameCommandDto.FromCommand(owned) };
                OperationCommandCanonicalizer.CanonicalProposalDigest(proposal);
                reason = string.Empty; return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            { proposal = null; reason = "private_command_payload_invalid"; return false; }
        }

        private bool ValidWindows(PrivatePlayerViewSnapshot snapshot)
        {
            if (snapshot.Windows == null || snapshot.Windows.Count > OperationWindowService.MaximumOpenWindows || snapshot.NextSubmissionSequence < 1 ||
                snapshot.NextSubmissionSequence > snapshot.CommittedSequence + 1) return false;
            var windowIds = new HashSet<string>(StringComparer.Ordinal);
            var requestIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var window in snapshot.Windows)
            {
                if (window == null || string.IsNullOrWhiteSpace(window.OperationWindowId) || window.OperationWindowId.Length > 256 ||
                    window.OperationWindowId == PrivateCommandWindowSelection.HostResolvedId ||
                    !windowIds.Add(window.OperationWindowId) || string.IsNullOrWhiteSpace(window.WindowKind) || window.WindowKind.Length > 128 ||
                    window.WindowGeneration <= 0 || window.Requests == null || window.Requests.Count > 1024) return false;
                foreach (var request in window.Requests)
                {
                    if (request == null || string.IsNullOrWhiteSpace(request.InteractionId) || request.InteractionId.Length > 256 ||
                        !requestIds.Add(request.InteractionId) || !snapshot.View.Interactions.Any(item => item != null && item.Status == "open" &&
                            item.AnsweringPlayerId == playerId && item.InteractionId == request.InteractionId)) return false;
                    bool hasDigest = request.ContractVersion == InteractionRequestValidity.ContractVersion && request.RequestDigest != null &&
                        request.RequestDigest.Length == 64 && request.RequestDigest.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
                    if (!hasDigest && !(request.ContractVersion == 0 && request.RequestDigest == string.Empty)) return false;
                }
            }
            return true;
        }

        public PrivatePlayerViewReceiveStatus Receive(PrivatePlayerViewSnapshot snapshot, bool initial, out string reason)
        {
            try { return ReceiveCore(snapshot, initial, out reason); }
            catch (Exception error) when (!(error is OutOfMemoryException))
            { reason = "private_view_payload_invalid"; return PrivatePlayerViewReceiveStatus.Rejected; }
        }

        private PrivatePlayerViewReceiveStatus ReceiveCore(PrivatePlayerViewSnapshot snapshot, bool initial, out string reason)
        {
            reason = "private_view_identity_invalid";
            var view = snapshot?.View;
            if (snapshot == null || snapshot.ProtocolVersion != PrivatePlayerViewSnapshot.CurrentProtocolVersion ||
                snapshot.GameId != gameId || snapshot.ContentPackHash != contentHash || snapshot.SessionEpoch != epoch ||
                snapshot.MembershipVersion != membershipVersion || snapshot.PlayerId != playerId || snapshot.ConnectionToken != token || snapshot.CommittedSequence < 0 ||
                snapshot.CommittedSequence == long.MaxValue || snapshot.StateRevision < 0 || view == null ||
                view.SchemaVersion != GameStateView.CurrentSchemaVersion || view.ViewerRole != GameStateViewerRole.Player ||
                view.ViewerPlayerId != playerId || view.GameId != gameId || view.ContentPackHash != contentHash ||
                view.StateRevision != snapshot.StateRevision || !view.HasValidPlayerOrderAndSlots() ||
                !playerOrder.SequenceEqual(view.LaunchPlayerOrder) || view.RuleConfiguration == null ||
                !GameStateCloneService.AreEquivalentPersistent(configuration, view.RuleConfiguration) || !ValidWindows(snapshot))
                return PrivatePlayerViewReceiveStatus.Rejected;
            if (current == null && !initial) { reason = "private_view_initial_required"; return PrivatePlayerViewReceiveStatus.NeedsResynchronization; }
            if (current != null && (snapshot.CommittedSequence < sequence || snapshot.StateRevision < revision))
            { reason = "private_view_stale"; return PrivatePlayerViewReceiveStatus.Rejected; }
            if (current != null && snapshot.CommittedSequence == sequence)
            {
                if (snapshot.StateRevision == revision && snapshot.NextSubmissionSequence == nextSubmissionSequence &&
                    GameStateCloneService.AreEquivalentPersistent(windows, snapshot.Windows) && GameStateCloneService.AreEquivalentPersistent(current, view))
                { reason = string.Empty; return PrivatePlayerViewReceiveStatus.Duplicate; }
                reason = "private_view_sequence_conflict"; return PrivatePlayerViewReceiveStatus.Rejected;
            }
            if (current != null && !initial && snapshot.CommittedSequence != sequence + 1)
            { reason = "private_view_sequence_gap"; return PrivatePlayerViewReceiveStatus.NeedsResynchronization; }
            if (current != null && (snapshot.NextSubmissionSequence < nextSubmissionSequence ||
                snapshot.NextSubmissionSequence - nextSubmissionSequence > snapshot.CommittedSequence - sequence))
            { reason = "private_view_submission_sequence_invalid"; return PrivatePlayerViewReceiveStatus.Rejected; }
            current = GameStateCloneService.DeepClone(view); sequence = snapshot.CommittedSequence; revision = snapshot.StateRevision;
            windows = GameStateCloneService.DeepClone(snapshot.Windows); nextSubmissionSequence = snapshot.NextSubmissionSequence;
            reason = string.Empty; return PrivatePlayerViewReceiveStatus.Applied;
        }
    }
}
