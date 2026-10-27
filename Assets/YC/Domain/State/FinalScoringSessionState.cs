using System;
using System.Collections.Generic;
using YC.Domain.Effects;
using YC.Domain.Scoring;

namespace YC.Domain.State
{
    // 挂到原FinalScoringState.Session；不另存Stage，原末轮执行节点FlowStage是唯一推进权威。
    [Serializable] public sealed class FinalScoringSessionState
    {
        public int ProtocolVersion = 1;
        public string ExecutionVersion = ScoringModifierProtocol.Version, SessionId = "", OwnerEffectId = "", OwnerMainNodeId = "", ContentHash = "";
        public string StartedEventId = "", RegionCommitReceipt = "", ResourceCommitReceipt = "", CompletionReceipt = "";
        public ScoringControlSnapshot Control;
        public List<FinalScoringPlayerLedger> Players = new List<FinalScoringPlayerLedger>();
        public List<FinalScoringEffectReceipt> Effects = new List<FinalScoringEffectReceipt>();
        public List<FinalScoringFactReceipt> Facts = new List<FinalScoringFactReceipt>();
        public List<string> ProcessedSourceReceiptIds = new List<string>();
    }
    [Serializable] public sealed class FinalScoringPlayerLedger
    {
        public int PlayerId, StartScore, SpecialScore, RegionScore, ResourceScore, FacilityScore, CityStyleScore;
        public ResourceSet Resources;
        public PlayerResourceScoreProfile Profile;
    }
    [Serializable] public sealed class FinalScoringEffectReceipt
    {
        public string ReceiptId = "", EffectId = "", SourceRootEffectId = "", RegionId = "", SlotId = "";
        public AbilitySourceRef Source;
        public AbilityVersionRef Version;
        public int PlayerId, ScoreDelta, ResourceDelta;
        public string ResourceType = "";
        public NormalizedValue Evidence;
    }
    [Serializable] public sealed class FinalScoringFactReceipt
    {
        public string ReceiptId = "", EffectId = "", SourceRootEffectId = "";
        public AbilitySourceRef Source;
        public AbilityVersionRef Version;
        public NormalizedValue Evidence;
    }
}
