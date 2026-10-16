using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Rules;

namespace YC.Application.Sessions
{
    [Serializable]
    public sealed class DeterministicSimulationSessionOptions
    {
        public bool IsHost;
        public int LocalPlayerId;
        public string GameId = string.Empty;
        public string ContentPackHash = string.Empty;
        public long SimulationEpoch = 1;
        public int MembershipVersion = 1;
        public List<int> FrozenPlayerIds = new List<int>();
        public List<string> RequiredReplicaIds = new List<string>();
        public long CheckpointGlobalSequence;
        public string CheckpointPrefixDigest = string.Empty;
        public int MaximumQueuedCommands = 256;
        public int MaximumLogCommands = 4096;
        public int MaximumRegisteredWindows = 4096;
        public int MaximumBoundaryFacts = 4096;
        public int PumpCommandsPerFrame = 8;

        public static string ReplicaIdForPlayer(int playerId) => "player." + playerId.ToString(CultureInfo.InvariantCulture);
        public static DeterministicSimulationSessionOptions CreateInitial(bool isHost, int localPlayerId, GameSession session, IEnumerable<int> frozenPlayerIds)
        {
            if (session == null || frozenPlayerIds == null) throw new ArgumentNullException(nameof(session));
            var players = frozenPlayerIds.OrderBy(player => player).ToList();
            return new DeterministicSimulationSessionOptions { IsHost = isHost, LocalPlayerId = localPlayerId,
                GameId = session.State.GameId, ContentPackHash = session.State.ContentPackHash, FrozenPlayerIds = players,
                RequiredReplicaIds = players.Select(ReplicaIdForPlayer).ToList(), CheckpointPrefixDigest = OperationCommandCanonicalizer.EmptyPrefixDigest };
        }
        public DeterministicSimulationSessionOptions Snapshot()
        {
            var copy = (DeterministicSimulationSessionOptions)MemberwiseClone();
            copy.FrozenPlayerIds = FrozenPlayerIds == null ? null : new List<int>(FrozenPlayerIds);
            copy.RequiredReplicaIds = RequiredReplicaIds == null ? null : new List<string>(RequiredReplicaIds); return copy;
        }
    }

    [Serializable]
    public class SimulationIdentityDto
    {
        public int ProtocolVersion = OperationCommandProtocol.Version;
        public string GameId = string.Empty;
        public long SimulationEpoch;
        public int MembershipVersion;
    }

    [Serializable]
    public sealed class SimulationProposalResultDto : SimulationIdentityDto
    {
        public OperationCommandProposalDto Proposal;
        public OperationAdmissionStatus Status;
        public CommandErrorCode ErrorCode;
        public string Reason = string.Empty;
        public bool RequiresRefresh;
        public long HostSequencedThrough;
        public long ExpectedClientSubmissionSequence;
        public OrderedOperationCommandDto OrderedCommand;
    }

    [Serializable]
    public sealed class SimulationWindowFactDto
    {
        public OperationWindowDescriptorDto Window;
        public List<int> AvailablePlayerIds = new List<int>();
        public bool IsClosed;
        public bool IsNewWindow;
        public bool RequiresPermit;
        public long FinalCommandSequence;
        public long CommandCount;
        public string WindowPrefixDigest = string.Empty;
    }

    [Serializable]
    public sealed class SimulationBoundaryDto : SimulationIdentityDto
    {
        public long GlobalFenceSequence;
        public string CommandPrefixDigest = string.Empty;
        public string RuleStateDigestVersion = string.Empty;
        public string RuleStateDigest = string.Empty;
        public string FactDigest = string.Empty;
        public List<SimulationWindowFactDto> Windows = new List<SimulationWindowFactDto>();
    }

    [Serializable]
    public sealed class SimulationWindowReadyDto : SimulationIdentityDto
    {
        public int PlayerId;
        public string ReplicaId = string.Empty;
        public SimulationBoundaryDto Boundary;
    }

    [Serializable]
    public sealed class SimulationWindowPermitDto : SimulationIdentityDto
    {
        public SimulationBoundaryDto Boundary;
        public long HostValidatedThroughSequence;
        public string HostValidationPrefixDigest = string.Empty;
        public List<string> ReadyReplicaIds = new List<string>();
    }

    [Serializable]
    public sealed class SimulationMissingRangeRequestDto : SimulationIdentityDto
    {
        public long FromSequence;
        public int MaximumCount = 4;
    }

    public sealed class SimulationCommandAppliedDto : SimulationIdentityDto
    {
        public GameCommandDto Command;
        public long GlobalCommandSequence;
        public string OperationWindowId;
        public int WindowGeneration;
        public long WindowCommandSequence;
        public CommandResult RuleResult;
    }

    public enum SimulationRecoveryPhase { Prepare, Prepared, Commit, Installed, Failed, Request }

    [Serializable]
    public sealed class SimulationConfirmedCommandReceiptDto
    {
        public long SourceSimulationEpoch;
        public long ClientSubmissionSequence;
        public string ProposalDigest = string.Empty;
        public string OrderedDigest = string.Empty;
        public OperationCommandOutcomeDto Outcome;
    }

    [Serializable]
    public sealed class SimulationRecoveryBaselineDto : SimulationIdentityDto
    {
        public long SourceSimulationEpoch;
        public string CheckpointId = string.Empty;
        public long ConfirmedGlobalSequence;
        public string CommandPrefixDigest = string.Empty;
        public string ContentPackHash = string.Empty;
        public string RuleDigestVersion = string.Empty;
        public string RuleStateDigest = string.Empty;
        public List<int> FrozenPlayerIds = new List<int>();
        public List<string> RequiredReplicaIds = new List<string>();
        public List<SimulationWindowFactDto> Windows = new List<SimulationWindowFactDto>();
        public Dictionary<int, long> NextSubmissionSequences = new Dictionary<int, long>();
        public List<SimulationConfirmedCommandReceiptDto> ConfirmedCommands = new List<SimulationConfirmedCommandReceiptDto>();
        public string MetadataDigest = string.Empty;
    }

    [Serializable]
    public sealed class SimulationRecoveryCheckpointDto
    {
        public SimulationRecoveryBaselineDto Baseline;
        public HostSessionArchiveDto Archive;
        public GameSessionLogContinuationDto Continuation;
    }

    [Serializable]
    public sealed class SimulationRecoveryNoticeDto : SimulationIdentityDto
    {
        public long SourceSimulationEpoch;
        public string CheckpointId = string.Empty;
        public long ConfirmedGlobalSequence;
        public string MetadataDigest = string.Empty;
        public SimulationRecoveryPhase Phase;
        public bool ResumeAllowed;
        public string ErrorCode = string.Empty;
        public string Reason = string.Empty;
    }

    [Serializable]
    public sealed class SimulationRecoveryResultDto
    {
        public bool Succeeded;
        public string CheckpointId = string.Empty;
        public string ErrorCode = string.Empty;
        public string Reason = string.Empty;
    }

    public sealed class SimulationWindowPermissionDto
    {
        public OperationWindowDescriptorDto Window;
        public List<int> AvailablePlayerIds = new List<int>();
        public bool IsOpen;
        public bool IsPermitted;
        public bool CanSubmitForLocalPlayer;
        public long GlobalFenceSequence;
    }

    [Serializable]
    public sealed class SimulationProtocolFaultDto : SimulationIdentityDto
    {
        public string FaultCode = string.Empty;
        public string Diagnostic = string.Empty;
        public long FailureGlobalSequence;
        public GameCommandDto Command;
        public CommandErrorCode ErrorCode;
        public bool RequiresRecovery;
        [Newtonsoft.Json.JsonIgnore] public CommandResult LocalRuleResult;
    }
}
