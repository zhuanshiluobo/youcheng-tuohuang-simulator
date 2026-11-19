using System;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Infrastructure.Multiplayer
{
    /// <summary>本人至多保留一条待确认命令；重连不能换 ID 或载荷重新执行。</summary>
    public sealed class PrivateClientCommandTracker
    {
        private PrivatePlayerViewReceiver receiver;
        private OperationCommandProposalDto pending;
        private PrivateCommandResult outcome;

        public PrivateClientCommandTracker(PrivatePlayerViewReceiver receiver)
        { this.receiver = receiver ?? throw new ArgumentNullException(nameof(receiver)); }

        public bool HasPending => pending != null;

        internal bool CancelUnsent(string commandId)
        {
            if (pending == null || outcome != null || pending.Command.CommandId != commandId) return false;
            pending = null;
            return true;
        }

        public bool TryBegin(GameCommand command, string windowId, out OperationCommandProposalDto proposal, out string reason)
        {
            proposal = null;
            if (pending != null) { reason = "private_command_already_pending"; return false; }
            if (!receiver.TryBuildProposal(command, windowId, out var built, out reason)) return false;
            pending = GameStateCloneService.DeepClone(built);
            proposal = GameStateCloneService.DeepClone(built);
            return true;
        }

        /// <summary>重连使用新令牌对应的接收器；只有对局、内容及本人身份一致时才保留待处理命令。</summary>
        public bool TryAttachReceiver(PrivatePlayerViewReceiver replacement)
        {
            if (replacement == null || replacement.GameId != receiver.GameId || replacement.ContentPackHash != receiver.ContentPackHash ||
                replacement.PlayerId != receiver.PlayerId || replacement.SessionEpoch < receiver.SessionEpoch ||
                replacement.SessionEpoch == receiver.SessionEpoch && replacement.MembershipVersion != receiver.MembershipVersion) return false;
            receiver = replacement;
            if (pending != null) { pending.SimulationEpoch = replacement.SessionEpoch; pending.MembershipVersion = replacement.MembershipVersion; }
            outcome = null; // 旧连接的响应不跨连接代次复用，重新查询同一命令。
            return true;
        }

        public OperationCommandProposalDto ReadPendingForRetry()
        {
            if (pending == null || receiver.CommittedSequence < 0) return null;
            return GameStateCloneService.DeepClone(pending);
        }

        public bool TryRecordResult(PrivateCommandResult result)
        {
            if (pending == null || result == null || result.ProtocolVersion != 1 || result.GameId != receiver.GameId ||
                result.CommandId != pending.Command.CommandId || result.PlayerId != receiver.PlayerId ||
                result.SessionEpoch != receiver.SessionEpoch || result.MembershipVersion != receiver.MembershipVersion ||
                !Enum.IsDefined(typeof(PrivateCommandStatus), result.Status) || result.GlobalSequence < 0 || result.StateRevision < 0 ||
                result.GlobalSequence < pending.ObservedGlobalSequence || result.StateRevision < pending.ObservedStateRevision ||
                result.NextSubmissionSequence < 1 || result.Reason == null || result.Reason.Length > 4096) return false;
            bool succeeded = result.Status == PrivateCommandStatus.Committed || result.Status == PrivateCommandStatus.Duplicate;
            if (succeeded ? result.ErrorCode != CommandErrorCode.None || result.GlobalSequence <= pending.ObservedGlobalSequence ||
                    result.StateRevision < pending.ObservedStateRevision || result.NextSubmissionSequence <= pending.ClientSubmissionSequence :
                result.ErrorCode == CommandErrorCode.None || result.NextSubmissionSequence != pending.ClientSubmissionSequence) return false;
            if (outcome != null)
            {
                bool priorSucceeded = outcome.Status != PrivateCommandStatus.Rejected;
                return succeeded == priorSucceeded && result.GlobalSequence == outcome.GlobalSequence &&
                    result.StateRevision == outcome.StateRevision && result.NextSubmissionSequence == outcome.NextSubmissionSequence &&
                    result.ErrorCode == outcome.ErrorCode && result.Reason == outcome.Reason;
            }
            outcome = GameStateCloneService.DeepClone(result);
            return true;
        }

        /// <summary>结果和足够新的本人视图同时到齐才返回并释放待处理命令。</summary>
        public bool TryTakeCompleted(out PrivateCommandResult result)
        {
            result = null;
            if (pending == null || outcome == null || receiver.CommittedSequence < outcome.GlobalSequence ||
                receiver.NextSubmissionSequence < outcome.NextSubmissionSequence) return false;
            result = GameStateCloneService.DeepClone(outcome);
            pending = null; outcome = null;
            return true;
        }
    }
}
