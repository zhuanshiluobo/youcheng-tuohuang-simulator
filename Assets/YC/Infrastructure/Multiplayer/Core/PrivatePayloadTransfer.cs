using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YC.Application.Sessions;

namespace YC.Infrastructure.Multiplayer
{
    [Serializable]
    public sealed class PrivatePayloadPart
    {
        public int ProtocolVersion = 1;
        public string GameId = string.Empty, ContentPackHash = string.Empty, ConnectionToken = string.Empty, Purpose = string.Empty, Digest = string.Empty;
        public long SessionEpoch, TransferId;
        public int MembershipVersion, PlayerId, TotalBytes, Offset;
        public byte[] Payload;
    }

    [Serializable]
    public sealed class PrivatePayloadAcknowledgment
    {
        public int ProtocolVersion = 1;
        public string GameId = string.Empty, ContentPackHash = string.Empty, ConnectionToken = string.Empty, Purpose = string.Empty;
        public long SessionEpoch, TransferId;
        public int MembershipVersion, PlayerId, ReceivedBytes;
    }

    /// <summary>区控预览逐段请求，避免单包过大或一次填满传输缓冲。</summary>
    public sealed class PrivateRegionPreviewPayload
    {
        public const int PartBytes = 4 * 1024;
        private readonly byte[] bytes;
        public string Digest { get; }
        public int PartCount => (bytes.Length - 1) / PartBytes + 1;
        public PrivateRegionPreviewPayload(byte[] payload)
        {
            if (payload == null || payload.Length == 0) throw new ArgumentException("region_preview_payload_empty");
            bytes = (byte[])payload.Clone();
            Digest = PrivateViewHash.Compute(bytes);
        }
        public string ReadPart(int index)
        {
            if (index < 0 || index >= PartCount) throw new ArgumentOutOfRangeException(nameof(index));
            int offset = index * PartBytes;
            return Convert.ToBase64String(bytes, offset, Math.Min(PartBytes, bytes.Length - offset));
        }
    }

    /// <summary>只接受顺序完整的分段，合并后验证摘要；不截断任何来源。</summary>
    public sealed class PrivateRegionPreviewReceiver : IDisposable
    {
        private readonly MemoryStream buffer = new MemoryStream();
        private string digest;
        private int count = -1;
        private bool finished;
        public int NextPart { get; private set; }
        public bool TryAppend(int part, int total, string expectedDigest, string encoded, out byte[] complete)
        {
            complete = null;
            if (finished || part != NextPart || total <= 0 || part >= total || !PrivateViewHash.Valid(expectedDigest) ||
                string.IsNullOrEmpty(encoded) || encoded.Length > ((PrivateRegionPreviewPayload.PartBytes + 2) / 3) * 4 ||
                count >= 0 && (count != total || digest != expectedDigest)) return false;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException) { return false; }
            if (bytes.Length == 0 || bytes.Length > PrivateRegionPreviewPayload.PartBytes ||
                part + 1 < total && bytes.Length != PrivateRegionPreviewPayload.PartBytes) return false;
            if (count < 0) { count = total; digest = expectedDigest; }
            buffer.Write(bytes, 0, bytes.Length); NextPart++;
            if (NextPart < count) return true;
            finished = true;
            var result = buffer.ToArray();
            if (PrivateViewHash.Compute(result) != digest) return false;
            complete = result;
            return true;
        }
        public void Dispose() => buffer.Dispose();
    }

    /// <summary>命令、结果及分页数据共用的限流发送窗口；不解析或广播内容。</summary>
    public sealed class PrivatePayloadSendWindow
    {
        public const int MaximumInFlightBytes = 4 * GameStateViewTransfer.ChunkBytes;
        private readonly byte[] bytes;
        private readonly PrivatePayloadPart identity;
        public int SentBytes { get; private set; }
        public int AcknowledgedBytes { get; private set; }
        public int TotalBytes => bytes.Length;
        public long TransferId => identity.TransferId;

        internal static bool ValidPurpose(string value) => value == "command" || value == "result" ||
            value == "payment-query" || value == "payment-result";

        public PrivatePayloadSendWindow(string gameId, string contentHash, string token, long epoch, int membershipVersion,
            int playerId, string purpose, long transferId, byte[] payload)
        {
            if (string.IsNullOrWhiteSpace(gameId) || gameId.Length > 256 || string.IsNullOrWhiteSpace(contentHash) || contentHash.Length > 256 ||
                !PrivatePlayerViewPublisher.ValidToken(token) || epoch <= 0 || membershipVersion <= 0 || playerId <= 0 ||
                !ValidPurpose(purpose) || transferId <= 0 || transferId == long.MaxValue || payload == null ||
                payload.Length <= 0 || payload.Length > OperationCommandCanonicalizer.MaximumPayloadBytes)
                throw new ArgumentException("private_payload_identity_invalid");
            bytes = (byte[])payload.Clone();
            identity = new PrivatePayloadPart { GameId = gameId, ContentPackHash = contentHash, ConnectionToken = token,
                SessionEpoch = epoch, MembershipVersion = membershipVersion, PlayerId = playerId, Purpose = purpose,
                TransferId = transferId, TotalBytes = bytes.Length, Digest = PrivateViewHash.Compute(bytes) };
        }

        private PrivatePayloadPart PartAt(int offset)
        {
            var payload = new byte[Math.Min(GameStateViewTransfer.ChunkBytes, bytes.Length - offset)];
            Buffer.BlockCopy(bytes, offset, payload, 0, payload.Length);
            return new PrivatePayloadPart { GameId = identity.GameId, ContentPackHash = identity.ContentPackHash,
                ConnectionToken = identity.ConnectionToken, SessionEpoch = identity.SessionEpoch, MembershipVersion = identity.MembershipVersion,
                PlayerId = identity.PlayerId, Purpose = identity.Purpose, TransferId = identity.TransferId,
                TotalBytes = bytes.Length, Digest = identity.Digest, Offset = offset, Payload = payload };
        }

        public bool TryTake(out PrivatePayloadPart part)
        {
            part = null;
            if (SentBytes >= bytes.Length) return false;
            int count = Math.Min(GameStateViewTransfer.ChunkBytes, bytes.Length - SentBytes);
            if (SentBytes - AcknowledgedBytes + count > MaximumInFlightBytes) return false;
            part = PartAt(SentBytes); SentBytes += count; return true;
        }

        public bool TryAcknowledge(PrivatePayloadAcknowledgment acknowledgment)
        {
            if (acknowledgment == null || acknowledgment.ProtocolVersion != 1 || acknowledgment.GameId != identity.GameId ||
                acknowledgment.ContentPackHash != identity.ContentPackHash || acknowledgment.ConnectionToken != identity.ConnectionToken ||
                acknowledgment.SessionEpoch != identity.SessionEpoch || acknowledgment.MembershipVersion != identity.MembershipVersion ||
                acknowledgment.PlayerId != identity.PlayerId || acknowledgment.Purpose != identity.Purpose || acknowledgment.TransferId != identity.TransferId ||
                acknowledgment.ReceivedBytes < 0 || acknowledgment.ReceivedBytes > SentBytes ||
                acknowledgment.ReceivedBytes != bytes.Length && acknowledgment.ReceivedBytes % GameStateViewTransfer.ChunkBytes != 0) return false;
            if (acknowledgment.ReceivedBytes > AcknowledgedBytes) AcknowledgedBytes = acknowledgment.ReceivedBytes;
            return true;
        }

        public IEnumerable<PrivatePayloadPart> Unacknowledged()
        {
            for (int offset = AcknowledgedBytes; offset < SentBytes; offset += GameStateViewTransfer.ChunkBytes) yield return PartAt(offset);
        }
    }

    public enum PrivatePayloadReceiveStatus { Buffered, Completed, AlreadyCompleted, Rejected }

    /// <summary>每连接、每用途只组装一条消息；乱序只确认已收到的连续前缀。</summary>
    public sealed class PrivatePayloadAssembler
    {
        private readonly string gameId, contentHash, token, purpose;
        private readonly long epoch;
        private readonly int membershipVersion, playerId;
        private PrivatePayloadPart active, completed;
        private byte[] buffer;
        private int received;
        private List<string> completedChunkHashes;

        public PrivatePayloadAssembler(string gameId, string contentHash, string token, long epoch, int membershipVersion, int playerId, string purpose)
        {
            if (string.IsNullOrWhiteSpace(gameId) || gameId.Length > 256 || string.IsNullOrWhiteSpace(contentHash) || contentHash.Length > 256 ||
                !PrivatePlayerViewPublisher.ValidToken(token) || epoch <= 0 || membershipVersion <= 0 || playerId <= 0 ||
                !PrivatePayloadSendWindow.ValidPurpose(purpose)) throw new ArgumentException("private_payload_assembler_invalid");
            this.gameId = gameId; this.contentHash = contentHash; this.token = token; this.epoch = epoch;
            this.membershipVersion = membershipVersion; this.playerId = playerId; this.purpose = purpose;
        }

        private static PrivatePayloadPart Header(PrivatePayloadPart part) => new PrivatePayloadPart {
            GameId = part.GameId, ContentPackHash = part.ContentPackHash, ConnectionToken = part.ConnectionToken,
            SessionEpoch = part.SessionEpoch, MembershipVersion = part.MembershipVersion, PlayerId = part.PlayerId,
            Purpose = part.Purpose, TransferId = part.TransferId, TotalBytes = part.TotalBytes, Digest = part.Digest };
        private static bool Same(PrivatePayloadPart left, PrivatePayloadPart right) => left != null && right != null &&
            left.ProtocolVersion == right.ProtocolVersion && left.GameId == right.GameId && left.ContentPackHash == right.ContentPackHash &&
            left.ConnectionToken == right.ConnectionToken && left.SessionEpoch == right.SessionEpoch && left.MembershipVersion == right.MembershipVersion &&
            left.PlayerId == right.PlayerId && left.Purpose == right.Purpose && left.TransferId == right.TransferId &&
            left.TotalBytes == right.TotalBytes && left.Digest == right.Digest;
        private PrivatePayloadAcknowledgment Ack(PrivatePayloadPart part, int bytes) => new PrivatePayloadAcknowledgment {
            GameId = gameId, ContentPackHash = contentHash, ConnectionToken = token, SessionEpoch = epoch, MembershipVersion = membershipVersion,
            PlayerId = playerId, Purpose = purpose, TransferId = part.TransferId, ReceivedBytes = bytes };
        private void ClearActive() { active = null; buffer = null; received = 0; }

        public PrivatePayloadReceiveStatus Receive(PrivatePayloadPart part, out PrivatePayloadAcknowledgment acknowledgment, out byte[] payload)
        {
            acknowledgment = null; payload = null;
            if (part == null || part.ProtocolVersion != 1 || part.GameId != gameId || part.ContentPackHash != contentHash ||
                part.ConnectionToken != token || part.SessionEpoch != epoch || part.MembershipVersion != membershipVersion || part.PlayerId != playerId ||
                part.Purpose != purpose || part.TransferId <= 0 || part.TransferId == long.MaxValue || part.TotalBytes <= 0 ||
                part.TotalBytes > OperationCommandCanonicalizer.MaximumPayloadBytes || !PrivateViewHash.Valid(part.Digest) || part.Payload == null ||
                part.Offset < 0 || part.Offset >= part.TotalBytes || part.Offset % GameStateViewTransfer.ChunkBytes != 0 ||
                part.Payload.Length != Math.Min(GameStateViewTransfer.ChunkBytes, part.TotalBytes - part.Offset)) return PrivatePayloadReceiveStatus.Rejected;
            if (completed != null && part.TransferId <= completed.TransferId)
            {
                int index = part.Offset / GameStateViewTransfer.ChunkBytes;
                if (!Same(completed, part) || index >= completedChunkHashes.Count || completedChunkHashes[index] != PrivateViewHash.Compute(part.Payload))
                    return PrivatePayloadReceiveStatus.Rejected;
                acknowledgment = Ack(part, completed.TotalBytes); return PrivatePayloadReceiveStatus.AlreadyCompleted;
            }
            if (active == null)
            {
                if (part.Offset != 0) { acknowledgment = Ack(part, 0); return PrivatePayloadReceiveStatus.Buffered; }
                active = Header(part); buffer = new byte[part.TotalBytes]; received = 0;
            }
            if (!Same(active, part)) return PrivatePayloadReceiveStatus.Rejected;
            if (part.Offset > received) { acknowledgment = Ack(part, received); return PrivatePayloadReceiveStatus.Buffered; }
            if (part.Offset < received)
            {
                if (part.Offset + part.Payload.Length > received) return PrivatePayloadReceiveStatus.Rejected;
                for (int i = 0; i < part.Payload.Length; i++) if (buffer[part.Offset + i] != part.Payload[i]) return PrivatePayloadReceiveStatus.Rejected;
                acknowledgment = Ack(part, received); return PrivatePayloadReceiveStatus.Buffered;
            }
            Buffer.BlockCopy(part.Payload, 0, buffer, received, part.Payload.Length); received += part.Payload.Length;
            if (received != buffer.Length) { acknowledgment = Ack(part, received); return PrivatePayloadReceiveStatus.Buffered; }
            if (PrivateViewHash.Compute(buffer) != active.Digest) { ClearActive(); return PrivatePayloadReceiveStatus.Rejected; }
            completedChunkHashes = new List<string>();
            for (int offset = 0; offset < buffer.Length; offset += GameStateViewTransfer.ChunkBytes)
            {
                var chunk = new byte[Math.Min(GameStateViewTransfer.ChunkBytes, buffer.Length - offset)];
                Buffer.BlockCopy(buffer, offset, chunk, 0, chunk.Length); completedChunkHashes.Add(PrivateViewHash.Compute(chunk));
            }
            completed = active; payload = buffer; acknowledgment = Ack(part, received); ClearActive();
            return PrivatePayloadReceiveStatus.Completed;
        }
    }
}
