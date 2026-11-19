using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public enum PrivateCommandStatus { Committed, Duplicate, Rejected }

    /// <summary>客户端存在多个本人窗口时，请房主以严格当前版本解析非答复命令。</summary>
    public static class PrivateCommandWindowSelection
    {
        public const string HostResolvedId = "private.host-resolved-window.v1";
        public const int HostResolvedGeneration = 1;
    }

    /// <summary>只交给命令所属席位；重复结果不重新附带事件或再次执行规则。</summary>
    [Serializable]
    public sealed class PrivateCommandResult
    {
        public int ProtocolVersion = 1;
        public string GameId = string.Empty, CommandId = string.Empty;
        public long SessionEpoch;
        public int MembershipVersion, PlayerId;
        public PrivateCommandStatus Status;
        public string Reason = string.Empty;
        public CommandErrorCode ErrorCode;
        public long GlobalSequence, NextSubmissionSequence;
        public int StateRevision;
    }

    public sealed partial class GameSession
    {
        /// <summary>在开始私密联机或恢复已有房主账本时调用；必须在单一规则所有者线程执行。</summary>
        public void BeginPrivateCommandAuthority(bool resume)
        {
            EnsureExecutionOwner();
            EnsureSaveCaptureOutsideExecution();
            if (resume)
            {
                if (State.PrivateCommandLedger == null || !PrivateCommandLedgerPolicy.TryValidate(State, out _))
                    throw new InvalidOperationException("private_command_ledger_resume_invalid");
                return;
            }
            if (State.PrivateCommandLedger != null || confirmedRecoveryCaptureEnabled || State.SetupRecipe?.Preparation == null ||
                State.EffectRuntime == null || State.Players == null)
                throw new InvalidOperationException("private_command_ledger_initialization_invalid");
            var work = GameStateCloneService.DeepClone(State);
            work.PrivateCommandLedger = new PrivateCommandLedger { GameId = work.GameId, ContentPackHash = work.ContentPackHash,
                BaseRevision = work.EffectRuntime.StateRevision, PlayerIds = work.Players.Select(player => player.PlayerId).ToList() };
            if (!PrivateCommandLedgerPolicy.TryValidate(work, out string reason)) throw new InvalidOperationException(reason);
            GameStateCloneService.CopyTo(State, work);
            mutationGeneration++;
            if (Volatile.Read(ref backgroundBound) != 0) Interlocked.Increment(ref backgroundCommitVersion);
            else foreach (Action listener in StateCommitted?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { listener(); } catch (Exception) { }
        }

        /// <summary>
        /// verifiedPlayerId、epoch、membershipVersion 和内容身份必须来自经过认证的网络连接，
        /// 不能从未经验证的客户端载荷取值。本入口尚不接收或发送网络消息。
        /// </summary>
        public PrivateCommandResult SubmitAuthenticatedPrivateCommand(OperationCommandProposalDto proposal, int verifiedPlayerId,
            long epoch, int membershipVersion, string contentHash)
        {
            EnsureExecutionOwner();
            var ledger = State.PrivateCommandLedger;
            long Next(int player) => ledger?.Receipts == null ? 1 : ledger.Receipts.LongCount(item => item != null && item.PlayerId == player) + 1;
            PrivateCommandResult Bind(PrivateCommandResult result)
            {
                result.GameId = State.GameId ?? string.Empty;
                var id = proposal?.Command?.CommandId;
                result.CommandId = id != null && id.Length <= 256 ? id : string.Empty;
                result.SessionEpoch = epoch; result.MembershipVersion = membershipVersion; result.PlayerId = verifiedPlayerId;
                return result;
            }
            PrivateCommandResult Reject(CommandErrorCode code, string reason) => Bind(new PrivateCommandResult {
                Status = PrivateCommandStatus.Rejected, ErrorCode = code, Reason = reason,
                GlobalSequence = ledger?.Receipts?.Count ?? 0, StateRevision = GetStateRevision(State), NextSubmissionSequence = Next(verifiedPlayerId) });
            if (ledger == null || !PrivateCommandLedgerPolicy.TryValidate(State, out _) || epoch <= 0 || membershipVersion <= 0 ||
                contentHash != State.ContentPackHash || !ledger.PlayerIds.Contains(verifiedPlayerId))
                return Reject(CommandErrorCode.InvalidPlayer, "private_command_authority_invalid");

            OperationCommandProposalDto owned;
            string digest;
            try
            {
                owned = OperationCommandCanonicalizer.SnapshotProposal(proposal);
                if (owned.GameId != State.GameId || owned.SimulationEpoch != epoch || owned.MembershipVersion != membershipVersion ||
                    owned.Command.PlayerId != verifiedPlayerId)
                    return Reject(CommandErrorCode.InvalidPlayer, "private_command_connection_identity_mismatch");
                // 连接代次可在重连后变化；原命令和观察到的窗口、序号保持完全一致。
                var canonical = OperationCommandCanonicalizer.SnapshotProposal(owned);
                canonical.SimulationEpoch = 1; canonical.MembershipVersion = 1;
                digest = OperationCommandCanonicalizer.CanonicalProposalDigest(canonical);
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
            { return Reject(CommandErrorCode.InvalidTarget, "private_command_payload_invalid"); }

            var previous = ledger.Receipts.FirstOrDefault(item => item.CommandId == owned.Command.CommandId);
            if (previous != null)
            {
                if (previous.PlayerId != verifiedPlayerId || previous.ProposalDigest != digest)
                    return Reject(CommandErrorCode.InvalidTarget, "private_command_id_conflict");
                return Bind(new PrivateCommandResult { Status = PrivateCommandStatus.Duplicate, ErrorCode = CommandErrorCode.None,
                    GlobalSequence = previous.GlobalSequence, StateRevision = previous.StateRevision, NextSubmissionSequence = Next(verifiedPlayerId) });
            }
            long currentSequence = ledger.Receipts.Count, expected = Next(verifiedPlayerId);
            int revision = GetStateRevision(State);
            if (owned.ClientSubmissionSequence != expected || owned.ObservedGlobalSequence > currentSequence || owned.ObservedStateRevision > revision)
                return Reject(CommandErrorCode.StateRevisionMismatch, "private_command_sequence_invalid");
            var command = owned.Command.ToCommand();
            if (!InteractionRequestValidity.TryReadMarker(command, out bool hasMarker, out string requestDigest, out var markerValidation))
                return Reject(markerValidation.ErrorCode, markerValidation.Reason);
            if (!hasMarker && (owned.ObservedGlobalSequence != currentSequence || owned.ObservedStateRevision != revision))
                return Reject(CommandErrorCode.StateRevisionMismatch, "private_command_observation_stale");

            bool hostResolved = owned.OperationWindowId == PrivateCommandWindowSelection.HostResolvedId &&
                owned.WindowGeneration == PrivateCommandWindowSelection.HostResolvedGeneration;
            if (hostResolved && (hasMarker || command.Kind == YC.Domain.Rules.GameCommandKind.AnswerInteraction))
                return Reject(CommandErrorCode.InvalidTarget, "private_command_window_resolution_not_allowed");
            string resolvedWindowId = owned.OperationWindowId;
            int resolvedWindowGeneration = owned.WindowGeneration;
            try
            {
                // 窗口同步可能修改状态，只在隔离副本上预检；正式提交会重新核验。
                var probe = GameStateCloneService.DeepClone(State);
                OperationWindowService.Synchronize(probe);
                if (!OperationWindowService.TryResolveCommandContext(probe, command, out var context, out var validation))
                    return Reject(validation.ErrorCode, validation.Reason);
                if (!hostResolved && (context.OperationWindowId != owned.OperationWindowId || context.WindowGeneration != owned.WindowGeneration))
                    return Reject(CommandErrorCode.InvalidTarget, "private_command_window_mismatch");
                resolvedWindowId = context.OperationWindowId;
                resolvedWindowGeneration = context.WindowGeneration;
                if (hasMarker)
                {
                    if (!commandHandlers.Any(handler => handler.CanHandle(command) && handler is IOrderedInteractionCommandHandler))
                        return Reject(CommandErrorCode.InvalidTarget, "private_request_handler_unsupported");
                    if (!InteractionRequestValidity.TryReadRequestAuthority(probe, command, out var authority, out validation) ||
                        authority.OperationWindowId != owned.OperationWindowId || authority.WindowGeneration != owned.WindowGeneration ||
                        !InteractionRequestValidity.TryValidateAnswer(probe, command, requestDigest, out validation))
                        return Reject(validation?.IsValid == false ? validation.ErrorCode : CommandErrorCode.InvalidTarget,
                            validation?.IsValid == false ? validation.Reason : "private_request_contract_invalid");
                }
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            { return Reject(CommandErrorCode.InvalidTarget, "private_command_window_invalid"); }

            var receipt = new PrivateCommandReceipt { PlayerId = verifiedPlayerId, CommandId = command.CommandId,
                ProposalDigest = digest, OperationWindowId = resolvedWindowId, WindowGeneration = resolvedWindowGeneration,
                SubmissionSequence = expected, GlobalSequence = currentSequence + 1 };
            var result = SubmitCore(command, hasMarker ? requestDigest : null, finalState =>
            {
                if (!GameStateCloneService.AreEquivalentPersistent(ledger, finalState.PrivateCommandLedger))
                    throw new InvalidOperationException("private_command_ledger_mutated_during_execution");
                receipt.StateRevision = GetStateRevision(finalState);
                finalState.PrivateCommandLedger.Receipts.Add(receipt);
                finalState.PrivateCommandLedger.CommittedSequence = receipt.GlobalSequence;
                if (!PrivateCommandLedgerPolicy.TryValidate(finalState, out string reason)) throw new InvalidOperationException(reason);
            });
            if (!result.Succeeded) return Reject(result.Validation.ErrorCode, result.Validation.Reason);
            return Bind(new PrivateCommandResult { Status = PrivateCommandStatus.Committed, ErrorCode = CommandErrorCode.None,
                GlobalSequence = receipt.GlobalSequence, StateRevision = receipt.StateRevision, NextSubmissionSequence = expected + 1 });
        }
    }
}
