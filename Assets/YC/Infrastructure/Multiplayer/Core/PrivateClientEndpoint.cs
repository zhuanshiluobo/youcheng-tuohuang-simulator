using System;
using System.Collections.Generic;
using System.Text;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Infrastructure.Multiplayer
{
    /// <summary>本人视图、原命令及准确结果的单连接端点；发送和重连仍由外层网络负责。</summary>
    public sealed class PrivateClientEndpoint
    {
        private readonly string gameId, contentHash, token;
        private readonly long epoch;
        private readonly int membershipVersion, playerId;
        private readonly PrivatePlayerViewPartAssembler viewAssembler;
        private readonly PrivatePayloadAssembler resultAssembler;
        private readonly PrivatePlayerViewReceiver receiver;
        private readonly PrivateClientCommandTracker tracker;
        private PrivatePayloadSendWindow commandSender;
        private PrivateViewInstalledReceipt lastInstallation;
        private long nextTransferId;
        private bool faulted;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public PrivateClientEndpoint(string gameId, string contentHash, string token, long epoch, int membershipVersion, int playerId,
            GameRuleConfiguration configuration, IEnumerable<int> playerOrder, PrivateClientCommandTracker pendingTracker = null)
        {
            this.gameId = gameId; this.contentHash = contentHash; this.token = token; this.epoch = epoch;
            this.membershipVersion = membershipVersion; this.playerId = playerId;
            receiver = new PrivatePlayerViewReceiver(gameId, contentHash, epoch, playerId, token, configuration, playerOrder, membershipVersion);
            viewAssembler = new PrivatePlayerViewPartAssembler(gameId, contentHash, epoch, playerId, token);
            resultAssembler = new PrivatePayloadAssembler(gameId, contentHash, token, epoch, membershipVersion, playerId, "result");
            tracker = pendingTracker ?? new PrivateClientCommandTracker(receiver);
            if (pendingTracker != null && !tracker.TryAttachReceiver(receiver)) throw new ArgumentException("private_client_reconnect_identity_invalid");
        }

        public bool HasInstalledView => receiver.CommittedSequence >= 0;
        public bool HasPendingCommand => tracker.HasPending;
        public bool HasUsableWindow => receiver.HasWindows;
        public bool IsFaulted => faulted;
        public int ReceivedViewBytes => viewAssembler.ReceivedBytes;
        public int TotalViewBytes => viewAssembler.TotalBytes;
        public long CommittedSequence => receiver.CommittedSequence;
        public GameStateView ReadView() => receiver.ReadView();
        public IReadOnlyList<PrivateOperationWindowView> ReadWindows() => receiver.ReadWindows();
        public PrivateClientCommandTracker RetainPendingTracker() => tracker;

        /// <summary>完整接收和校验后才回安装确认；重复片段可重发上次安装确认。</summary>
        public bool ReceiveView(PrivatePlayerViewPart part, out PrivatePlayerViewAcknowledgment acknowledgment,
            out PrivateViewInstalledReceipt installation, out string reason)
        {
            installation = null;
            if (faulted) { acknowledgment = null; reason = "private_client_faulted"; return false; }
            var status = viewAssembler.Receive(part, out acknowledgment, out var snapshot, out reason);
            if (status == PrivatePlayerViewPartStatus.Rejected) return false;
            if (status == PrivatePlayerViewPartStatus.AlreadyCompleted)
            {
                if (lastInstallation == null || lastInstallation.TransferId != part.TransferId)
                { acknowledgment = null; reason = "private_client_installation_missing"; return false; }
                installation = GameStateCloneService.DeepClone(lastInstallation); return true;
            }
            if (status != PrivatePlayerViewPartStatus.Completed) return true;
            var applied = receiver.Receive(snapshot, part.IsInitial, out reason);
            if (applied != PrivatePlayerViewReceiveStatus.Applied && applied != PrivatePlayerViewReceiveStatus.Duplicate)
            { acknowledgment = null; faulted = applied == PrivatePlayerViewReceiveStatus.Rejected; return false; }
            lastInstallation = PrivateViewInstalledReceipt.From(snapshot, part.TransferId);
            installation = GameStateCloneService.DeepClone(lastInstallation);
            return true;
        }

        private bool PrepareSender(OperationCommandProposalDto proposal, out string reason)
        {
            reason = "private_client_command_transfer_invalid";
            try
            {
                if (proposal == null || nextTransferId >= long.MaxValue - 1) return false;
                var envelope = new PrivateCommandEnvelope { GameId = gameId, ContentPackHash = contentHash, ConnectionToken = token,
                    SessionEpoch = epoch, MembershipVersion = membershipVersion, PlayerId = playerId, Proposal = proposal };
                commandSender = new PrivatePayloadSendWindow(gameId, contentHash, token, epoch, membershipVersion, playerId,
                    "command", ++nextTransferId, PrivateCommandWireProtocol.Encode(envelope));
                reason = string.Empty; return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { commandSender = null; return false; }
        }

        public bool TryBeginCommand(GameCommand command, string windowId, out string reason)
        {
            if (faulted) { reason = "private_client_faulted"; return false; }
            if (!tracker.TryBegin(command, windowId, out var proposal, out reason)) return false;
            if (PrepareSender(proposal, out reason)) return true;
            // 尚未创建发送器，任何字节都未离开客户端；可退回明确失败。
            tracker.CancelUnsent(proposal.Command.CommandId);
            return false;
        }

        public bool TryPrepareRetry(out string reason)
        {
            if (faulted || !HasInstalledView || !tracker.HasPending ||
                commandSender != null && commandSender.AcknowledgedBytes != commandSender.TotalBytes)
            { reason = "private_client_retry_not_ready"; return false; }
            return PrepareSender(tracker.ReadPendingForRetry(), out reason);
        }

        public bool TryTakeCommandPart(out PrivatePayloadPart part)
        {
            part = null;
            return commandSender != null && commandSender.TryTake(out part);
        }
        public IEnumerable<PrivatePayloadPart> ReadUnacknowledgedCommand() => commandSender?.Unacknowledged() ?? Array.Empty<PrivatePayloadPart>();
        public bool TryAcknowledgeCommand(PrivatePayloadAcknowledgment acknowledgment) => commandSender?.TryAcknowledge(acknowledgment) == true;

        public bool ReceiveResult(PrivatePayloadPart part, out PrivatePayloadAcknowledgment acknowledgment, out string reason)
        {
            reason = "private_client_result_invalid";
            if (faulted) { acknowledgment = null; reason = "private_client_faulted"; return false; }
            var status = resultAssembler.Receive(part, out acknowledgment, out var bytes);
            if (status == PrivatePayloadReceiveStatus.Rejected) return false;
            if (status != PrivatePayloadReceiveStatus.Completed) { reason = string.Empty; return true; }
            try
            {
                if (!PrivateCommandWireProtocol.TryDecodeResult(Utf8.GetString(bytes), out var envelope) || envelope.GameId != gameId ||
                    envelope.ContentPackHash != contentHash || envelope.ConnectionToken != token || envelope.SessionEpoch != epoch ||
                    envelope.MembershipVersion != membershipVersion || envelope.PlayerId != playerId || !tracker.TryRecordResult(envelope.Result))
                { acknowledgment = null; faulted = true; return false; }
                reason = string.Empty; return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { acknowledgment = null; faulted = true; return false; }
        }

        public bool TryTakeCompleted(out PrivateCommandResult result)
        {
            if (faulted) { result = null; return false; }
            if (!tracker.TryTakeCompleted(out result)) return false;
            commandSender = null;
            return true;
        }
    }
}
