using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Domain.Rules;

namespace YC.Infrastructure.Multiplayer
{
    [Serializable]
    public sealed class RoomConfigurationMessage
    {
        public int ProtocolVersion = RoomState.CurrentProtocolVersion;
        public string Kind = string.Empty;
        public string RequestJson = string.Empty;
        public string ContentPackHash = string.Empty;
        public string RuleContractVersion = GameRuleConfiguration.CurrentRuleContractVersion;

        public static RoomConfigurationMessage Create(RoomConfigurationRequest request, IRoomContentCapabilityProvider content, GameRuleConfiguration configuration) =>
            new RoomConfigurationMessage
            {
                Kind = request.GetType().Name, RequestJson = JsonConvert.SerializeObject(request),
                ContentPackHash = content?.GetContentPackHash(configuration) ?? string.Empty
            };

        public static bool TryDeserialize(string json, out RoomConfigurationMessage message)
        {
            message = null;
            try
            {
                if (string.IsNullOrEmpty(json) || json.Length > 262144) return false;
                var root = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (root["ProtocolVersion"]?.Type != JTokenType.Integer || (int?)root["ProtocolVersion"] != RoomState.CurrentProtocolVersion ||
                    root["Kind"]?.Type != JTokenType.String || root["RequestJson"]?.Type != JTokenType.String ||
                    root["ContentPackHash"]?.Type != JTokenType.String || root["RuleContractVersion"]?.Type != JTokenType.String) return false;
                var candidate = root.ToObject<RoomConfigurationMessage>();
                candidate.ReadRequest();
                message = candidate;
                return true;
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidCastException || ex is FormatException || ex is OverflowException)
            { return false; }
        }

        public RoomConfigurationRequest ReadRequest()
        {
            var input = JObject.Parse(RequestJson, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (input["CommandId"]?.Type != JTokenType.String || input["RoomId"]?.Type != JTokenType.String ||
                input["RequestingPlayerId"]?.Type != JTokenType.Integer || input["ExpectedConfigurationRevision"]?.Type != JTokenType.Integer)
                throw new JsonSerializationException("room_protocol_mismatch");
            switch (Kind)
            {
                case nameof(ChangeRoomConfigurationRequest):
                    Require(input, "MapId", JTokenType.String); Require(input, "RuleModeId", JTokenType.String); Require(input, "EnabledExpansionIds", JTokenType.Array);
                    return input.ToObject<ChangeRoomConfigurationRequest>();
                case nameof(SetRoomReadyRequest):
                    Require(input, "IsUserReady", JTokenType.Boolean); return input.ToObject<SetRoomReadyRequest>();
                case nameof(SetRoomSeatStatusRequest):
                    Require(input, "SeatIndex", JTokenType.Integer); Require(input, "Status", JTokenType.String); return input.ToObject<SetRoomSeatStatusRequest>();
                case nameof(RequestRoomSeatSwapRequest):
                    Require(input, "TargetSeatIndex", JTokenType.Integer); return input.ToObject<RequestRoomSeatSwapRequest>();
                case nameof(ConfirmRoomSeatSwapRequest):
                    Require(input, "SwapRequestId", JTokenType.String); Require(input, "Accepted", JTokenType.Boolean); return input.ToObject<ConfirmRoomSeatSwapRequest>();
                case nameof(StartConfiguredGameRequest): return JsonConvert.DeserializeObject<StartConfiguredGameRequest>(RequestJson);
                default: throw new JsonSerializationException("room_protocol_mismatch");
            }
        }

        private static void Require(JObject input, string name, JTokenType type)
        { if (input[name]?.Type != type) throw new JsonSerializationException("room_protocol_mismatch"); }

        public RoomConfigurationResult Execute(RoomState room, int authenticatedPlayerId, RoomConfigurationService service)
            => ExecuteCore(room, authenticatedPlayerId, service, false);

        public RoomConfigurationResult ValidateStart(RoomState room, int authenticatedPlayerId, RoomConfigurationService service)
            => ExecuteCore(room, authenticatedPlayerId, service, true);

        private RoomConfigurationResult ExecuteCore(RoomState room, int authenticatedPlayerId,
            RoomConfigurationService service, bool validateStartOnly)
        {
            if (ProtocolVersion != RoomState.CurrentProtocolVersion || room == null) return RoomConfigurationService.Result(room, "room_protocol_mismatch");
            RoomConfigurationRequest request;
            try { request = ReadRequest(); }
            catch (JsonException) { return RoomConfigurationService.Result(room, "room_protocol_mismatch"); }
            if (request == null || request.RequestingPlayerId != authenticatedPlayerId ||
                RoomConfigurationService.FindSlot(room, authenticatedPlayerId) == null)
                return RoomConfigurationService.Result(room, "room_protocol_mismatch");
            if (validateStartOnly)
                return request is StartConfiguredGameRequest startRequest
                    ? service.ValidateLaunchRequest(room, startRequest) : RoomConfigurationService.Result(room, "room_protocol_mismatch");
            if (request is SetRoomReadyRequest ready && ready.IsUserReady && request.ExpectedConfigurationRevision == room.Configuration.ConfigurationRevision &&
                (ContentPackHash != room.Configuration.ContentPackHash || RuleContractVersion != room.Configuration.RuleContractVersion))
                return RoomConfigurationService.Result(room, "content_version_mismatch");
            switch (request)
            {
                case ChangeRoomConfigurationRequest change: return service.ChangeConfiguration(room, change);
                case SetRoomReadyRequest readyRequest:
                    var player = RoomConfigurationService.FindSlot(room, authenticatedPlayerId).Occupant;
                    var oldHash = player.ContentPackHash; var oldVersion = player.RuleContractVersion;
                    if (readyRequest.IsUserReady && !room.HasStarted &&
                        request.ExpectedConfigurationRevision == room.Configuration.ConfigurationRevision &&
                        player.IsReady && player.TransportConnected && player.IdentityVerified)
                    { player.ContentPackHash = ContentPackHash; player.RuleContractVersion = RuleContractVersion; }
                    var result = service.SetReady(room, readyRequest);
                    if (!result.Success)
                    { player.ContentPackHash = oldHash; player.RuleContractVersion = oldVersion; result.Snapshot = room.Clone(); }
                    return result;
                case SetRoomSeatStatusRequest status: return service.SetSeatStatus(room, status);
                case RequestRoomSeatSwapRequest swap: return service.RequestSeatSwap(room, swap);
                case ConfirmRoomSeatSwapRequest confirm: return service.ConfirmSeatSwap(room, confirm);
                case StartConfiguredGameRequest start:
                    service.TryFreezeForLaunch(room, start, out _, out var reason);
                    return RoomConfigurationService.Result(room, reason);
                default: return RoomConfigurationService.Result(room, "room_protocol_mismatch");
            }
        }
    }
    [Serializable]
    public sealed class RoomConfigurationReply
    {
        public int ProtocolVersion = RoomState.CurrentProtocolVersion;
        public string CommandId = string.Empty;
        public RoomConfigurationResult Result;

        public static bool TryDeserialize(string json, out RoomConfigurationReply reply)
        {
            reply = null;
            try
            {
                if (string.IsNullOrEmpty(json) || json.Length > 262144) return false;
                var root = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (root["ProtocolVersion"]?.Type != JTokenType.Integer || (int?)root["ProtocolVersion"] != RoomState.CurrentProtocolVersion ||
                    root["CommandId"]?.Type != JTokenType.String || !(root["Result"] is JObject result) ||
                    result["Success"]?.Type != JTokenType.Boolean || result["ReasonCode"]?.Type != JTokenType.String ||
                    !(result["Snapshot"] is JObject snapshot) || !RoomStateSerializer.TryDeserialize(snapshot.ToString(), out var room, out _)) return false;
                reply = root.ToObject<RoomConfigurationReply>();
                reply.Result.Snapshot = room;
                return true;
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidCastException || ex is FormatException || ex is OverflowException)
            { return false; }
        }
    }
}
