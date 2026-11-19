using System;
using System.Collections.Generic;
using System.Text;

namespace YC.Infrastructure.Multiplayer
{
    public enum PrivateHostCommandReceiveStatus { Accepted, Backpressured, Rejected }

    /// <summary>
    /// 单连接的命令与结果收发。断线导致房主暂停时，已收齐的命令保留在此连接，
    /// 待所有席位重新安装视图后才执行。字节确认不代表命令已经执行。
    /// 所有方法都必须由同一规则所有者线程调用。
    /// </summary>
    public sealed class PrivateHostCommandChannel
    {
        private readonly PrivateHostCoordinator host;
        private readonly int connectionId, playerId, membershipVersion;
        private readonly string gameId, contentHash, token;
        private readonly long epoch;
        private readonly PrivatePayloadAssembler assembler;
        private PrivateCommandEnvelope pending;
        private PrivatePayloadSendWindow result;
        private long lastReceivedTransfer, nextResultTransfer;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public PrivateHostCommandChannel(PrivateHostCoordinator host, int connectionId, string gameId, string contentHash,
            string token, long epoch, int membershipVersion, int playerId)
        {
            if (host == null || connectionId < 0) throw new ArgumentException("private_host_channel_invalid");
            this.host = host; this.connectionId = connectionId; this.gameId = gameId; this.contentHash = contentHash;
            this.token = token; this.epoch = epoch; this.membershipVersion = membershipVersion; this.playerId = playerId;
            assembler = new PrivatePayloadAssembler(gameId, contentHash, token, epoch, membershipVersion, playerId, "command");
        }

        public bool Faulted { get; private set; }
        public string Fault { get; private set; } = string.Empty;
        public bool HasPendingCommand => pending != null;
        public bool HasUnacknowledgedResult => result != null && result.AcknowledgedBytes != result.TotalBytes;

        private PrivateHostCommandReceiveStatus Fail(string reason)
        { Faulted = true; Fault = reason; return PrivateHostCommandReceiveStatus.Rejected; }

        public PrivateHostCommandReceiveStatus Receive(PrivatePayloadPart part, out PrivatePayloadAcknowledgment acknowledgment)
        {
            acknowledgment = null;
            if (Faulted || host.Stopped || part == null) return PrivateHostCommandReceiveStatus.Rejected;
            // 允许重发上一条的已收分片，但不能在结果尚未确认时接纳下一条。
            if (part.TransferId > lastReceivedTransfer && (pending != null || HasUnacknowledgedResult))
                return PrivateHostCommandReceiveStatus.Backpressured;
            var status = assembler.Receive(part, out acknowledgment, out var bytes);
            if (status == PrivatePayloadReceiveStatus.Rejected) { acknowledgment = null; return PrivateHostCommandReceiveStatus.Rejected; }
            if (status != PrivatePayloadReceiveStatus.Completed) return PrivateHostCommandReceiveStatus.Accepted;
            try
            {
                if (!PrivateCommandWireProtocol.TryDecodeCommand(Utf8.GetString(bytes), out var envelope) ||
                    envelope.GameId != gameId || envelope.ContentPackHash != contentHash || envelope.ConnectionToken != token ||
                    envelope.SessionEpoch != epoch || envelope.MembershipVersion != membershipVersion || envelope.PlayerId != playerId)
                { acknowledgment = null; return Fail("private_host_command_envelope_invalid"); }
                pending = envelope;
                lastReceivedTransfer = part.TransferId;
                return PrivateHostCommandReceiveStatus.Accepted;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            { acknowledgment = null; return Fail("private_host_command_envelope_invalid"); }
        }

        /// <summary>返回 true 只表示通道仍可工作，不表示命令已提交或已写盘。</summary>
        public bool Pump()
        {
            if (Faulted || host.Stopped) return false;
            if (pending == null || !host.Ready || HasUnacknowledgedResult) return true;
            if (!host.TrySubmitEnvelope(connectionId, pending, out var response))
            { Faulted = true; Fault = "private_host_command_admission_failed"; return false; }
            try
            {
                if (nextResultTransfer >= long.MaxValue - 1) throw new InvalidOperationException("private_result_transfer_exhausted");
                result = new PrivatePayloadSendWindow(gameId, contentHash, token, epoch, membershipVersion, playerId,
                    "result", ++nextResultTransfer, PrivateCommandWireProtocol.Encode(response));
                pending = null;
                return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            { Faulted = true; Fault = "private_host_result_encoding_failed"; return false; }
        }

        public bool TryTakeResult(out PrivatePayloadPart part)
        { part = null; return !Faulted && result != null && result.TryTake(out part); }

        public IEnumerable<PrivatePayloadPart> ReadUnacknowledgedResult() =>
            Faulted ? Array.Empty<PrivatePayloadPart>() : result?.Unacknowledged() ?? Array.Empty<PrivatePayloadPart>();

        public bool TryAcknowledgeResult(PrivatePayloadAcknowledgment acknowledgment) =>
            !Faulted && result?.TryAcknowledge(acknowledgment) == true;
    }
}
