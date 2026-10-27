using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    [Serializable] public sealed class CleanupSourceRequestRef
    {
        public string WindowId = "", OwnerEffectId = "", OptionId = "", InteractionId = "", FactsDigest = "";
        public int AnsweringPlayerId = -1, RequestGeneration, CandidateSetVersion;
    }

    [Serializable] public sealed class CleanupSourceEntryState
    {
        public int ProtocolVersion = 1, NextSequence = 1, RequestGeneration = 1;
        public string RepeatPolicy = "once", BodyHandlerRef = "", ActiveInvocationId = "";
        public string RegistrationEventId = "", RegistrationTargetId = "", RegistrationReceiptId = "";
        public bool Sealed;
        public AbilitySourceRef SourceRef;
        public AbilityVersionRef VersionRef;
        public List<CleanupSourceInvocationState> Invocations = new List<CleanupSourceInvocationState>();
    }

    [Serializable] public sealed class CleanupSourceInvocationState
    {
        public int ProtocolVersion = 1, Sequence;
        public string InvocationId = "", CommandId = "", CommandDigest = "", TargetEffectId = "";
        public string Status = "executing", Outcome = "", CompletionReceiptId = "";
        public AbilitySourceRef SourceRef;
        public AbilityVersionRef VersionRef;
        public CleanupSourceRequestRef RequestRef;
        public List<string> BodyEffectIds = new List<string>(), PaymentEffectIds = new List<string>();
    }

    public sealed class CleanupSourceAcceptance
    {
        public string WindowId, OptionId, InvocationId, CommandId, CommandDigest;
        public int Sequence;
        public CleanupSourceRequestRef RequestRef;
        public AbilitySourceRef SourceRef;
        public AbilityVersionRef VersionRef;
    }

    /// <summary>查询006已经保存的响应、保管和封口事实，不维护另一套依赖状态。</summary>
    public interface ICleanupInvocationDependencies
    {
        bool HasPendingForInvocation(GameState state, string targetEffectId, out string reason);
        bool HasPendingForWindow(GameState state, string ownerEffectId, out string reason);
    }
}
