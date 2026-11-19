using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Rules;

namespace YC.Infrastructure.Multiplayer
{
    [Serializable]
    public sealed class PrivateCommandEnvelope
    {
        public int ProtocolVersion = 1;
        public string GameId = string.Empty, ContentPackHash = string.Empty, ConnectionToken = string.Empty;
        public long SessionEpoch;
        public int MembershipVersion, PlayerId;
        public OperationCommandProposalDto Proposal;
    }

    [Serializable]
    public sealed class PrivateCommandResultEnvelope
    {
        public int ProtocolVersion = 1;
        public string GameId = string.Empty, ContentPackHash = string.Empty, ConnectionToken = string.Empty;
        public long SessionEpoch;
        public int MembershipVersion, PlayerId;
        public PrivateCommandResult Result;
    }

    /// <summary>有界且严格的私密命令 JSON；分片、连接认证和投递顺序由传输层负责。</summary>
    public static class PrivateCommandWireProtocol
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings {
            TypeNameHandling = TypeNameHandling.None, MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 64 };
        private static readonly string[] HeaderFields = { "ProtocolVersion", "GameId", "ContentPackHash", "ConnectionToken", "SessionEpoch", "MembershipVersion", "PlayerId" };
        private static readonly string[] ProposalFields = { "ProtocolVersion", "DigestVersion", "GameId", "SimulationEpoch", "MembershipVersion",
            "ClientSubmissionSequence", "ObservedGlobalSequence", "ObservedStateRevision", "OperationWindowId", "WindowGeneration", "Command" };
        private static readonly string[] CommandFields = { "CommandId", "Kind", "PlayerId", "SourceId", "TargetId", "OptionIds", "Parameters" };
        private static readonly string[] ResultFields = { "ProtocolVersion", "GameId", "CommandId", "SessionEpoch", "MembershipVersion", "PlayerId",
            "Status", "Reason", "ErrorCode", "GlobalSequence", "NextSubmissionSequence", "StateRevision" };

        private static bool Fields(JObject obj, string[] fields) => obj != null && obj.Properties().Count() == fields.Length &&
            obj.Properties().All(property => fields.Contains(property.Name)) && fields.All(name => obj.Property(name) != null);
        private static bool Text(JObject obj, params string[] names) => names.All(name => obj[name]?.Type == JTokenType.String);
        private static bool Integer(JObject obj, params string[] names) => names.All(name => obj[name]?.Type == JTokenType.Integer);

        private static bool TryRoot(string json, string body, out JObject root)
        {
            root = null;
            if (string.IsNullOrEmpty(json) || Utf8.GetByteCount(json) > OperationCommandCanonicalizer.MaximumPayloadBytes) return false;
            using var text = new StringReader(json);
            using var reader = new JsonTextReader(text) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
            var parsed = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read() || !Fields(parsed, HeaderFields.Concat(new[] { body }).ToArray()) ||
                !Integer(parsed, "ProtocolVersion", "SessionEpoch", "MembershipVersion", "PlayerId") ||
                !Text(parsed, "GameId", "ContentPackHash", "ConnectionToken") ||
                (int)parsed["ProtocolVersion"] != 1 || (long)parsed["SessionEpoch"] <= 0 ||
                (int)parsed["MembershipVersion"] <= 0 || (int)parsed["PlayerId"] <= 0 ||
                string.IsNullOrWhiteSpace((string)parsed["GameId"]) || ((string)parsed["GameId"]).Length > 256 ||
                string.IsNullOrWhiteSpace((string)parsed["ContentPackHash"]) || ((string)parsed["ContentPackHash"]).Length > 256 ||
                !PrivatePlayerViewPublisher.ValidToken((string)parsed["ConnectionToken"])) return false;
            root = parsed; return true;
        }

        public static bool TryDecodeCommand(string json, out PrivateCommandEnvelope envelope)
        {
            envelope = null;
            try
            {
                if (!TryRoot(json, "Proposal", out var root) || !(root["Proposal"] is JObject proposal) || !Fields(proposal, ProposalFields) ||
                    !Integer(proposal, "ProtocolVersion", "SimulationEpoch", "MembershipVersion", "ClientSubmissionSequence", "ObservedGlobalSequence", "ObservedStateRevision", "WindowGeneration") ||
                    !Text(proposal, "DigestVersion", "GameId", "OperationWindowId") || !(proposal["Command"] is JObject command) || !Fields(command, CommandFields) ||
                    !Integer(command, "Kind", "PlayerId") || !Text(command, "CommandId", "SourceId", "TargetId") ||
                    !(command["OptionIds"] is JArray options) || options.Any(value => value.Type != JTokenType.String) ||
                    !(command["Parameters"] is JArray parameters) || parameters.Any(value => !(value is JObject item) ||
                        !Fields(item, new[] { "Key", "Value" }) || !Text(item, "Key", "Value"))) return false;
                var candidate = root.ToObject<PrivateCommandEnvelope>(JsonSerializer.Create(Settings));
                if (candidate?.Proposal?.Command == null || candidate.GameId != candidate.Proposal.GameId ||
                    candidate.SessionEpoch != candidate.Proposal.SimulationEpoch || candidate.MembershipVersion != candidate.Proposal.MembershipVersion ||
                    candidate.PlayerId != candidate.Proposal.Command.PlayerId) return false;
                OperationCommandCanonicalizer.CanonicalProposalDigest(candidate.Proposal);
                envelope = candidate; return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { envelope = null; return false; }
        }

        public static bool TryDecodeResult(string json, out PrivateCommandResultEnvelope envelope)
        {
            envelope = null;
            try
            {
                if (!TryRoot(json, "Result", out var root) || !(root["Result"] is JObject result) || !Fields(result, ResultFields) ||
                    !Text(result, "GameId", "CommandId", "Reason") || !Integer(result, "ProtocolVersion", "SessionEpoch", "MembershipVersion", "PlayerId",
                        "Status", "ErrorCode", "GlobalSequence", "NextSubmissionSequence", "StateRevision")) return false;
                var candidate = root.ToObject<PrivateCommandResultEnvelope>(JsonSerializer.Create(Settings));
                var value = candidate?.Result;
                if (value == null || value.ProtocolVersion != 1 || value.GameId != candidate.GameId || value.SessionEpoch != candidate.SessionEpoch ||
                    value.MembershipVersion != candidate.MembershipVersion || value.PlayerId != candidate.PlayerId ||
                    !Enum.IsDefined(typeof(PrivateCommandStatus), value.Status) || !Enum.IsDefined(typeof(CommandErrorCode), value.ErrorCode) ||
                    value.CommandId == null || value.CommandId.Length > 256 || value.Reason == null || value.Reason.Length > 4096 ||
                    value.GlobalSequence < 0 || value.NextSubmissionSequence < 1 || value.StateRevision < 0) return false;
                envelope = candidate; return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { envelope = null; return false; }
        }

        public static byte[] Encode(PrivateCommandEnvelope envelope)
        {
            string json = JsonConvert.SerializeObject(envelope, Settings);
            if (!TryDecodeCommand(json, out _)) throw new ArgumentException("private_command_wire_invalid");
            return Utf8.GetBytes(json);
        }

        public static byte[] Encode(PrivateCommandResultEnvelope envelope)
        {
            string json = JsonConvert.SerializeObject(envelope, Settings);
            if (!TryDecodeResult(json, out _)) throw new ArgumentException("private_result_wire_invalid");
            return Utf8.GetBytes(json);
        }
    }
}
