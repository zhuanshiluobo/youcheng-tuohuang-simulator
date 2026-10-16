using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Rules;

namespace YC.Application.Sessions
{
    public static class OperationCommandProtocol
    {
        public const int Version = 1;
    }

    [Serializable]
    public class OperationCommandProposalDto
    {
        public int ProtocolVersion = OperationCommandProtocol.Version;
        public string DigestVersion = OperationCommandCanonicalizer.DigestVersion;
        public string GameId = string.Empty;
        public long SimulationEpoch;
        public int MembershipVersion;
        public long ClientSubmissionSequence;
        public long ObservedGlobalSequence;
        public int ObservedStateRevision;
        public string OperationWindowId = string.Empty;
        public int WindowGeneration;
        public GameCommandDto Command = new GameCommandDto();
    }

    /// <summary>继承唯一提案载荷，另加主机分配的两个序号；尚不代表业务已核验。</summary>
    [Serializable]
    public sealed class OrderedOperationCommandDto : OperationCommandProposalDto
    {
        public long GlobalCommandSequence;
        public long WindowCommandSequence;
        public string ProposalDigest = string.Empty;
        public string OrderedCommandDigest = string.Empty;
    }

    [Serializable]
    public sealed class OperationCommandOutcomeDto
    {
        public string CommandId = string.Empty;
        public int PlayerId;
        public long GlobalCommandSequence;
        public string OperationWindowId = string.Empty;
        public long WindowCommandSequence;
        public bool Succeeded;
        public CommandErrorCode ErrorCode;
        public string Reason = string.Empty;
        public int StateRevision;
    }

    /// <summary>本地同一次规则提交的连续成功前缀；只有 Host 消费它时才具有权威含义。</summary>
    [Serializable]
    public sealed class CommandValidationPrefixDto
    {
        public int ProtocolVersion = OperationCommandProtocol.Version;
        public string DigestVersion = OperationCommandCanonicalizer.DigestVersion;
        public string GameId = string.Empty;
        public long SimulationEpoch;
        public int MembershipVersion;
        public long ValidatedThroughSequence;
        public string PrefixDigest = string.Empty;
        public List<OperationCommandOutcomeDto> Outcomes = new List<OperationCommandOutcomeDto>();
    }

    [Serializable]
    public sealed class OperationCommandRangeDto
    {
        public string GameId = string.Empty;
        public long SimulationEpoch;
        public int MembershipVersion;
        public long BaseGlobalSequence;
        public long FromSequence;
        public long ThroughSequence;
        public string PrefixDigest = string.Empty;
        public string ErrorCode = string.Empty;
        public List<OrderedOperationCommandDto> Commands = new List<OrderedOperationCommandDto>();
    }

    public enum OperationAdmissionStatus { Accepted, Duplicate, MissingSubmission, Rejected, Backpressure }
    public sealed class OperationCommandAdmissionResult
    {
        public OperationAdmissionStatus Status { get; internal set; }
        public string ErrorCode { get; internal set; } = string.Empty;
        public string Reason { get; internal set; } = string.Empty;
        public long ExpectedClientSubmissionSequence { get; internal set; }
        public OrderedOperationCommandDto OrderedCommand { get; internal set; }
        public bool Accepted => Status == OperationAdmissionStatus.Accepted || Status == OperationAdmissionStatus.Duplicate;
    }

    public enum OperationReplicaStatus { Applied, Buffered, Duplicate, Rejected, Backpressure, Stopped }
    public sealed class OperationCommandReplicaResult
    {
        public OperationReplicaStatus Status { get; internal set; }
        public string ErrorCode { get; internal set; } = string.Empty;
        public string Reason { get; internal set; } = string.Empty;
        public long ExpectedGlobalSequence { get; internal set; }
        public CommandResult RuleResult { get; internal set; }
        public List<OperationCommandOutcomeDto> Outcomes { get; internal set; } = new List<OperationCommandOutcomeDto>();
    }
}
