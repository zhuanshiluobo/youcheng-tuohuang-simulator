using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    /// <summary>仅保存在房主本机；不得把规则摘要或原包作为房间数据发送。</summary>
    [Serializable]
    public sealed class PrivateHostCheckpoint
    {
        public string Format = "YC.PrivateHostCheckpoint";
        public int Version = 1;
        public string GameId = "", ContentHash = "", CheckpointId = "", StateDigest = "", ArchiveDigest = "", ContinuationDigest = "";
        public long SessionEpoch, CommittedSequence;
        public int MembershipVersion, HostPlayerId, StateRevision;
        public List<int> PlayerIds = new List<int>();

        private static readonly string[] Fields = { "Format", "Version", "GameId", "ContentHash", "CheckpointId", "StateDigest", "ArchiveDigest",
            "ContinuationDigest", "SessionEpoch", "CommittedSequence", "MembershipVersion", "HostPlayerId", "StateRevision", "PlayerIds" };
        private static string Hash(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            using var hash = SHA256.Create();
            // 归档可达数十 MB；分块编码避免为每次保存与校验另建完整 UTF-8 副本。
            // 同一个 Encoder 跨块保留代理字符，替换无效字符的行为与 Encoding.UTF8.GetBytes 一致。
            const int chunkSize = 4096;
            var chars = new char[chunkSize];
            var bytes = new byte[Encoding.UTF8.GetMaxByteCount(chunkSize)];
            var encoder = Encoding.UTF8.GetEncoder();
            for (int offset = 0; offset < value.Length;)
            {
                int count = Math.Min(chunkSize, value.Length - offset);
                value.CopyTo(offset, chars, 0, count);
                offset += count;
                int written = encoder.GetBytes(chars, 0, count, bytes, 0, offset == value.Length);
                if (written > 0) hash.TransformBlock(bytes, 0, written, bytes, 0);
            }
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return string.Concat(hash.Hash.Select(valueByte => valueByte.ToString("x2")));
        }
        private static bool Digest(string value) => value != null && value.Length == 64 &&
            value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

        public static PrivateHostCheckpoint Create(GameState state, int hostPlayerId, long epoch, int membership, string archive, string continuation)
        {
            if (state?.PrivateCommandLedger == null || !PrivateCommandLedgerPolicy.TryValidate(state, out _) ||
                state.RandomRuntime?.AlgorithmVersion != GameplayRandomRuntime.PrivateAlgorithmVersion ||
                !state.PrivateCommandLedger.PlayerIds.Contains(hostPlayerId) || epoch <= 0 || epoch >= long.MaxValue - 1 || membership <= 0 ||
                string.IsNullOrEmpty(archive) || string.IsNullOrEmpty(continuation))
                throw new InvalidOperationException("private_checkpoint_state_invalid");
            return new PrivateHostCheckpoint { GameId = state.GameId, ContentHash = state.ContentPackHash,
                CheckpointId = "private." + Guid.NewGuid().ToString("N"), SessionEpoch = epoch, MembershipVersion = membership,
                HostPlayerId = hostPlayerId, StateRevision = state.EffectRuntime.StateRevision,
                CommittedSequence = state.PrivateCommandLedger.CommittedSequence,
                PlayerIds = state.PrivateCommandLedger.PlayerIds.OrderBy(id => id).ToList(), StateDigest = RuleStateDigestService.Compute(state),
                ArchiveDigest = Hash(archive), ContinuationDigest = Hash(continuation) };
        }

        public static PrivateHostCheckpoint Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 32768) throw new InvalidOperationException("private_checkpoint_invalid");
            var root = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (root.Properties().Count() != Fields.Length || root.Properties().Any(field => !Fields.Contains(field.Name)) ||
                Fields.Any(field => root[field] == null) ||
                new[] { "Format", "GameId", "ContentHash", "CheckpointId", "StateDigest", "ArchiveDigest", "ContinuationDigest" }.Any(field => root[field].Type != JTokenType.String) ||
                new[] { "Version", "SessionEpoch", "CommittedSequence", "MembershipVersion", "HostPlayerId", "StateRevision" }.Any(field => root[field].Type != JTokenType.Integer) ||
                !(root["PlayerIds"] is JArray ids) || ids.Any(id => id.Type != JTokenType.Integer))
                throw new InvalidOperationException("private_checkpoint_invalid");
            var value = root.ToObject<PrivateHostCheckpoint>();
            if (value == null || value.Format != "YC.PrivateHostCheckpoint" || value.Version != 1 ||
                string.IsNullOrWhiteSpace(value.GameId) || value.GameId.Length > 256 || string.IsNullOrWhiteSpace(value.ContentHash) || value.ContentHash.Length > 256 ||
                !Guid.TryParseExact(value.CheckpointId?.StartsWith("private.", StringComparison.Ordinal) == true ? value.CheckpointId.Substring(8) : "", "N", out _) ||
                value.SessionEpoch <= 0 || value.SessionEpoch >= long.MaxValue - 1 || value.MembershipVersion <= 0 || value.CommittedSequence < 0 ||
                value.StateRevision < 0 || value.PlayerIds == null || value.PlayerIds.Count < 2 || value.PlayerIds.Count > 4 ||
                value.PlayerIds.Any(id => id <= 0) || !value.PlayerIds.SequenceEqual(value.PlayerIds.Distinct().OrderBy(id => id)) ||
                !value.PlayerIds.Contains(value.HostPlayerId) || !Digest(value.StateDigest) || !Digest(value.ArchiveDigest) || !Digest(value.ContinuationDigest))
                throw new InvalidOperationException("private_checkpoint_invalid");
            return value;
        }

        public void Validate(MatchSaveData save, JObject rawState)
            => ValidateCore(save, rawState, () => HostRecoveryService.ReadRawStateForPrivateCheckpoint(rawState));

        // 投影已完整扫描并查重归档；权威状态直接从原JSON流式反序列化，不物化整份历史树。
        internal void ValidateProjected(MatchSaveData save, JObject rawState)
            => ValidateCore(save, rawState, () => HostRecoveryService.ReadPrivateCheckpointStateFromArchive(save.ArchiveJson));

        private void ValidateCore(MatchSaveData save, JObject rawState, Func<GameState> readState)
        {
            if (save == null || rawState == null || save.Mode != LaunchMode.Host || save.LocalPlayerId != HostPlayerId ||
                save.GameId != GameId || save.ContentHash != ContentHash || Hash(save.ArchiveJson) != ArchiveDigest ||
                Hash(save.ContinuationJson) != ContinuationDigest || !PrivateCommandLedgerPolicy.HasRawFields(rawState) ||
                rawState["RandomRuntime"]?["PrivateKeyHex"]?.Type != JTokenType.String ||
                !PlayerIds.SequenceEqual(save.Seats.Select(seat => seat.PlayerId).OrderBy(id => id)))
                throw new InvalidOperationException("private_checkpoint_archive_mismatch");
            var state = readState();
            if (state == null || state.RandomRuntime?.AlgorithmVersion != GameplayRandomRuntime.PrivateAlgorithmVersion ||
                state.PrivateCommandLedger == null || !PrivateCommandLedgerPolicy.TryValidate(state, out _) ||
                state.PrivateCommandLedger.CommittedSequence != CommittedSequence || state.EffectRuntime.StateRevision != StateRevision ||
                RuleStateDigestService.Compute(state) != StateDigest)
                throw new InvalidOperationException("private_checkpoint_state_invalid");
        }
    }
}
