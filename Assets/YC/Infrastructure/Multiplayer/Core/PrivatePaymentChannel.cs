using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Infrastructure.Multiplayer
{
    [Serializable] public sealed class PrivatePaymentRequestEnvelope
    {
        public int ProtocolVersion = 1, MembershipVersion, PlayerId;
        public string GameId = "", ContentPackHash = "", ConnectionToken = "";
        public long SessionEpoch;
        public PaymentSourcePageRequest Request;
    }
    [Serializable] public sealed class PrivatePaymentResponseEnvelope
    {
        public int ProtocolVersion = 1, MembershipVersion, PlayerId;
        public string GameId = "", ContentPackHash = "", ConnectionToken = "";
        public long SessionEpoch;
        public PaymentSourcePageResponse Response;
    }

    /// <summary>付款分页只发送本人查询和本人结果，严格校验连接、内容、快照及请求身份。</summary>
    public static class PrivatePaymentWireProtocol
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings {
            TypeNameHandling = TypeNameHandling.None, MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 64 };
        private static readonly string[] Header = { "ProtocolVersion", "MembershipVersion", "PlayerId", "GameId", "ContentPackHash", "ConnectionToken", "SessionEpoch" };
        private static readonly string[] Query = { "PlayerId", "StateRevision", "PaymentGeneration", "SourceIndex", "ItemOffset", "OwnerEffectId", "InteractionId", "SnapshotId" };
        private static bool Fields(JObject value, params string[] names) => value != null && value.Properties().Count() == names.Length &&
            value.Properties().All(p => names.Contains(p.Name)) && names.All(name => value.Property(name) != null);
        private static bool QueryShape(JToken value) => value is JObject q && Fields(q, Query) &&
            new[] { "PlayerId", "StateRevision", "PaymentGeneration", "SourceIndex", "ItemOffset" }.All(name => q[name].Type == JTokenType.Integer) &&
            new[] { "OwnerEffectId", "InteractionId", "SnapshotId" }.All(name => q[name].Type == JTokenType.String);
        private static bool Root(string json, string body, out JObject root)
        {
            root = null;
            if (string.IsNullOrEmpty(json) || Utf8.GetByteCount(json) > OperationCommandCanonicalizer.MaximumPayloadBytes) return false;
            using var input = new StringReader(json);
            using var reader = new JsonTextReader(input) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read() || !Fields(value, Header.Concat(new[] { body }).ToArray()) ||
                new[] { "ProtocolVersion", "MembershipVersion", "PlayerId", "SessionEpoch" }.Any(name => value[name].Type != JTokenType.Integer) ||
                new[] { "GameId", "ContentPackHash", "ConnectionToken" }.Any(name => value[name].Type != JTokenType.String) ||
                (int)value["ProtocolVersion"] != 1 || (int)value["MembershipVersion"] <= 0 || (int)value["PlayerId"] <= 0 ||
                (long)value["SessionEpoch"] <= 0 || string.IsNullOrWhiteSpace((string)value["GameId"]) || ((string)value["GameId"]).Length > 256 ||
                string.IsNullOrWhiteSpace((string)value["ContentPackHash"]) || ((string)value["ContentPackHash"]).Length > 256 ||
                !PrivatePlayerViewPublisher.ValidToken((string)value["ConnectionToken"])) return false;
            root = value; return true;
        }
        public static bool TryDecodeRequest(string json, out PrivatePaymentRequestEnvelope envelope)
        {
            envelope = null;
            try
            {
                if (!Root(json, "Request", out var root) || !(root["Request"] is JObject request) ||
                    !Fields(request, "ProtocolVersion", "RequestId", "Query") || request["ProtocolVersion"].Type != JTokenType.Integer ||
                    request["RequestId"].Type != JTokenType.String || !QueryShape(request["Query"])) return false;
                var value = root.ToObject<PrivatePaymentRequestEnvelope>(JsonSerializer.Create(Settings));
                if (value?.Request?.ProtocolVersion != 1 || !PaymentSourcePageProtocol.Valid(value.Request.Query) ||
                    value.Request.Query.PlayerId != value.PlayerId || value.Request.RequestId != PaymentSourcePageProtocol.Id(value.Request.Query)) return false;
                envelope = value; return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { return false; }
        }
        public static bool TryDecodeResponse(string json, out PrivatePaymentResponseEnvelope envelope)
        {
            envelope = null;
            try
            {
                if (!Root(json, "Response", out var root) || !(root["Response"] is JObject response) ||
                    !Fields(response, "ProtocolVersion", "RequestId", "Reason", "Query", "Page") ||
                    response["ProtocolVersion"].Type != JTokenType.Integer || response["RequestId"].Type != JTokenType.String ||
                    response["Reason"].Type != JTokenType.String || !QueryShape(response["Query"]) ||
                    response["Page"].Type != JTokenType.Null && response["Page"].Type != JTokenType.Object) return false;
                var value = root.ToObject<PrivatePaymentResponseEnvelope>(JsonSerializer.Create(Settings));
                var r = value?.Response; var q = r?.Query; var page = r?.Page;
                if (r?.ProtocolVersion != 1 || !PaymentSourcePageProtocol.Valid(q) || q.PlayerId != value.PlayerId ||
                    r.RequestId != PaymentSourcePageProtocol.Id(q) || r.Reason == null || r.Reason.Length > 2048 ||
                    (string.IsNullOrEmpty(r.Reason) != (page != null))) return false;
                if (page != null && (page.GameId != value.GameId || page.ContentPackHash != value.ContentPackHash ||
                    page.PlayerId != q.PlayerId || page.StateRevision != q.StateRevision || page.PaymentGeneration != q.PaymentGeneration ||
                    page.OwnerEffectId != q.OwnerEffectId || page.InteractionId != q.InteractionId || page.SnapshotId != q.SnapshotId ||
                    page.SourceIndex != q.SourceIndex || page.ItemOffset != q.ItemOffset)) return false;
                envelope = value; return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { return false; }
        }
        public static byte[] Encode(PrivatePaymentRequestEnvelope envelope)
        {
            string json = JsonConvert.SerializeObject(envelope, Settings);
            if (!TryDecodeRequest(json, out _)) throw new ArgumentException("private_payment_request_invalid");
            return Utf8.GetBytes(json);
        }
        public static byte[] Encode(PrivatePaymentResponseEnvelope envelope)
        {
            string json = JsonConvert.SerializeObject(envelope, Settings);
            if (!TryDecodeResponse(json, out _)) throw new ArgumentException("private_payment_response_invalid");
            return Utf8.GetBytes(json);
        }
    }

    /// <summary>每连接最多一个查询和一个未确认结果；房间暂停时等待，不更改规则状态。</summary>
    public sealed class PrivateHostPaymentChannel
    {
        private readonly PrivateHostCoordinator host;
        private readonly int connectionId, playerId, membership;
        private readonly string gameId, contentHash, token;
        private readonly long epoch;
        private readonly PrivatePayloadAssembler assembler;
        private PrivatePaymentRequestEnvelope pending;
        private PrivatePayloadSendWindow result;
        private long lastReceived, nextResult;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public bool Faulted { get; private set; }
        public string Fault { get; private set; } = "";
        public bool HasPending => pending != null;
        private bool HasUnacknowledged => result != null && result.AcknowledgedBytes != result.TotalBytes;
        public PrivateHostPaymentChannel(PrivateHostCoordinator host, int connectionId, string gameId, string contentHash,
            string token, long epoch, int membership, int playerId)
        {
            if (host == null || connectionId < 0) throw new ArgumentException("private_payment_host_invalid");
            this.host = host; this.connectionId = connectionId; this.gameId = gameId; this.contentHash = contentHash;
            this.token = token; this.epoch = epoch; this.membership = membership; this.playerId = playerId;
            assembler = new PrivatePayloadAssembler(gameId, contentHash, token, epoch, membership, playerId, "payment-query");
        }
        public PrivateHostCommandReceiveStatus Receive(PrivatePayloadPart part, out PrivatePayloadAcknowledgment ack)
        {
            ack = null;
            if (Faulted || host.Stopped || part == null) return PrivateHostCommandReceiveStatus.Rejected;
            if (part.TransferId > lastReceived && (pending != null || HasUnacknowledged)) return PrivateHostCommandReceiveStatus.Backpressured;
            var status = assembler.Receive(part, out ack, out var bytes);
            if (status == PrivatePayloadReceiveStatus.Rejected) { ack = null; return PrivateHostCommandReceiveStatus.Rejected; }
            if (status != PrivatePayloadReceiveStatus.Completed) return PrivateHostCommandReceiveStatus.Accepted;
            try
            {
                if (!PrivatePaymentWireProtocol.TryDecodeRequest(Utf8.GetString(bytes), out var value) || value.GameId != gameId ||
                    value.ContentPackHash != contentHash || value.ConnectionToken != token || value.SessionEpoch != epoch ||
                    value.MembershipVersion != membership || value.PlayerId != playerId) throw new ArgumentException();
                pending = value; lastReceived = part.TransferId; return PrivateHostCommandReceiveStatus.Accepted;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            { ack = null; Faulted = true; Fault = "private_payment_request_invalid"; return PrivateHostCommandReceiveStatus.Rejected; }
        }
        public bool Pump()
        {
            if (Faulted || host.Stopped) return false;
            if (pending == null || !host.Ready || HasUnacknowledged) return true;
            if (!host.TryQueryPaymentSources(connectionId, token, pending.Request, out var page))
            { Faulted = true; Fault = "private_payment_query_failed"; return false; }
            try
            {
                if (nextResult >= long.MaxValue - 1) throw new InvalidOperationException();
                var envelope = new PrivatePaymentResponseEnvelope { GameId = gameId, ContentPackHash = contentHash,
                    ConnectionToken = token, SessionEpoch = epoch, MembershipVersion = membership, PlayerId = playerId, Response = page };
                result = new PrivatePayloadSendWindow(gameId, contentHash, token, epoch, membership, playerId,
                    "payment-result", ++nextResult, PrivatePaymentWireProtocol.Encode(envelope));
                pending = null; return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            { Faulted = true; Fault = "private_payment_result_invalid"; return false; }
        }
        public bool TryTakeResult(out PrivatePayloadPart part) { part = null; return !Faulted && result != null && result.TryTake(out part); }
        public IEnumerable<PrivatePayloadPart> ReadUnacknowledged() => Faulted ? Array.Empty<PrivatePayloadPart>() : result?.Unacknowledged() ?? Array.Empty<PrivatePayloadPart>();
        public bool Acknowledge(PrivatePayloadAcknowledgment ack) => !Faulted && result?.TryAcknowledge(ack) == true;
    }

    /// <summary>按连接隔离缓存；过期页面不会覆盖当前付款快照。</summary>
    public sealed class PrivateClientPaymentChannel
    {
        private readonly string gameId, contentHash, token, scope;
        private readonly long epoch;
        private readonly int membership, playerId;
        private readonly PaymentSourcePageCache cache = new PaymentSourcePageCache();
        private readonly PrivatePayloadAssembler assembler;
        private PrivatePayloadSendWindow request;
        private string pendingId;
        private long nextRequest;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public bool Faulted { get; private set; }
        public string Failure { get; private set; } = "";
        public string PendingRequestId => pendingId;
        public PrivateClientPaymentChannel(string gameId, string contentHash, string token, long epoch, int membership, int playerId)
        {
            this.gameId = gameId; this.contentHash = contentHash; this.token = token; this.epoch = epoch;
            this.membership = membership; this.playerId = playerId;
            scope = gameId + "|" + epoch + "|" + membership + "|" + playerId + "|" + token;
            assembler = new PrivatePayloadAssembler(gameId, contentHash, token, epoch, membership, playerId, "payment-result");
        }
        public PaymentSourceProjection Read(PaymentSourceQuery query, bool allowStart = true)
        {
            if (Faulted || !PaymentSourcePageProtocol.Valid(query) || query.PlayerId != playerId) return null;
            var page = cache.Read(scope, query); if (page != null || pendingId != null || !allowStart) return page;
            if (nextRequest >= long.MaxValue - 1) { Faulted = true; Failure = "private_payment_transfer_exhausted"; return null; }
            try
            {
                var copy = GameStateCloneService.DeepClone(query);
                var body = new PaymentSourcePageRequest { RequestId = PaymentSourcePageProtocol.Id(copy), Query = copy };
                var envelope = new PrivatePaymentRequestEnvelope { GameId = gameId, ContentPackHash = contentHash, ConnectionToken = token,
                    SessionEpoch = epoch, MembershipVersion = membership, PlayerId = playerId, Request = body };
                var bytes = PrivatePaymentWireProtocol.Encode(envelope);
                if (!cache.Begin(scope, copy)) return null;
                pendingId = body.RequestId; Failure = "";
                request = new PrivatePayloadSendWindow(gameId, contentHash, token, epoch, membership, playerId, "payment-query", ++nextRequest, bytes);
                return null;
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { Faulted = true; Failure = "private_payment_request_invalid"; return null; }
        }
        public bool TryTakeRequest(out PrivatePayloadPart part) { part = null; return !Faulted && request != null && request.TryTake(out part); }
        public IEnumerable<PrivatePayloadPart> ReadUnacknowledged() => Faulted ? Array.Empty<PrivatePayloadPart>() : request?.Unacknowledged() ?? Array.Empty<PrivatePayloadPart>();
        public bool Acknowledge(PrivatePayloadAcknowledgment ack) => !Faulted && request?.TryAcknowledge(ack) == true;
        public bool Receive(PrivatePayloadPart part, out PrivatePayloadAcknowledgment ack, out bool changed)
        {
            changed = false; ack = null;
            if (Faulted) return false;
            var status = assembler.Receive(part, out ack, out var bytes);
            if (status == PrivatePayloadReceiveStatus.Rejected) return false;
            if (status != PrivatePayloadReceiveStatus.Completed) return true;
            try
            {
                if (!PrivatePaymentWireProtocol.TryDecodeResponse(Utf8.GetString(bytes), out var value) || value.GameId != gameId ||
                    value.ContentPackHash != contentHash || value.ConnectionToken != token || value.SessionEpoch != epoch ||
                    value.MembershipVersion != membership || value.PlayerId != playerId || value.Response.RequestId != pendingId)
                    throw new ArgumentException();
                if (!cache.Accept(scope, value.Response)) Failure = string.IsNullOrEmpty(value.Response.Reason) ? "private_payment_page_stale" : value.Response.Reason;
                pendingId = null; request = null; changed = true; return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            { ack = null; Faulted = true; Failure = "private_payment_response_invalid"; return false; }
        }
    }
}
