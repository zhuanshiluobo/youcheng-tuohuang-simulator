using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Domain.Scoring
{
    public static class ScoringModifierProtocol
    {
        public const string QueryKind = "scoringModifiers.v1";
        public const string Version = "2.0.0";
        public const string Influence = "influence_delta";
        public const string Tie = "tie_claim";
        public const string Value = "region_value_delta";
        public const string Resources = "resource_profile";
        public static string Permission(string kind) => "scoring." + kind + (kind == Value ? ":region" : ":self");
    }

    [Serializable] public sealed class ScoringSourceRecord
    {
        public AbilitySourceRef Source;
        public AbilityVersionRef Version;
    }
    [Serializable] public sealed class ScoringModifierRecord
    {
        public string QueryId = "", BasisDigest = "", Kind = "", RegionId = "", RuleId = "", ReasonRef = "", ProfileId = "";
        public int PlayerId = -1, Delta;
        public AbilitySourceRef Source;
        public AbilityVersionRef Version;
        public List<ResourceScoreEntry> Entries = new List<ResourceScoreEntry>();
    }
    [Serializable] public sealed class ResourceScoreEntry
    {
        public string ResourceType = "";
        public int Units, Points;
    }
    [Serializable] public sealed class PlayerResourceScoreProfile
    {
        public int PlayerId;
        public string ProfileId = "";
        public AbilitySourceRef Source;
        public AbilityVersionRef Version;
        public List<ResourceScoreEntry> Entries = new List<ResourceScoreEntry>();
    }
    [Serializable] public sealed class ScoringInfluenceFact
    {
        public string ItemId = "", TypeId = "", RegionId = "", Position = "", SubjectKind = "", CompanyId = "";
        public int PlayerId = -1;
    }
    [Serializable] public sealed class RegionControlClaim
    {
        public int PlayerId;
        public string SourceKey = "", RuleId = "";
    }
    [Serializable] public sealed class ScoringControlSnapshot
    {
        public int ProtocolVersion = 1;
        public string SnapshotId = "", SessionId = "", MapId = "", BasisDigest = "", ContentHash = "", RulesDigest = "";
        public List<FinalRegionControlSnapshot> Regions = new List<FinalRegionControlSnapshot>();
        public List<ScoringInfluenceFact> InfluenceFacts = new List<ScoringInfluenceFact>();
        public List<ScoringSourceRecord> Sources = new List<ScoringSourceRecord>();
    }

    /// <summary>持久层只保存有序计数事实；字典仅为临时只读查询投影。</summary>
    [Serializable] public sealed class FinalRegionControlSnapshot
    {
        public string RegionId = "", ControlReason = "uncontrolled";
        public int BaseScoreValue, ScoreValue;
        public int? ControllerPlayerId;
        public bool IsTie;
        public List<PlayerInfluenceCountState> InfluenceCountFacts = new List<PlayerInfluenceCountState>();
        public List<PlayerInfluenceCountState> PhysicalCountFacts = new List<PlayerInfluenceCountState>();
        public List<PlayerInfluenceCountState> CityCountFacts = new List<PlayerInfluenceCountState>();
        public List<int> PositiveHighestPlayerIds = new List<int>();
        public List<RegionControlClaim> ControlClaims = new List<RegionControlClaim>();
        public List<ScoringModifierRecord> InfluenceModifiers = new List<ScoringModifierRecord>();
        public List<ScoringModifierRecord> TieModifiers = new List<ScoringModifierRecord>();
        public List<ScoringModifierRecord> ValueModifiers = new List<ScoringModifierRecord>();
        [JsonIgnore] public Dictionary<int, int> InfluenceCounts => InfluenceCountFacts.ToDictionary(p => p.PlayerId, p => p.Count);
        [JsonIgnore] public Dictionary<int, int> PhysicalCounts => PhysicalCountFacts.ToDictionary(p => p.PlayerId, p => p.Count);
        [JsonIgnore] public Dictionary<int, int> CityCounts => CityCountFacts.ToDictionary(p => p.PlayerId, p => p.Count);
        public static FinalRegionControlSnapshot FromResult(RegionControlResult r) => new FinalRegionControlSnapshot {
            RegionId = r.RegionId, BaseScoreValue = r.BaseScoreValue, ScoreValue = r.ScoreValue,
            ControllerPlayerId = r.ControllerPlayerId, ControlReason = r.ControlReason, IsTie = r.IsTie,
            InfluenceCountFacts = Counts(r.InfluenceCounts), PhysicalCountFacts = Counts(r.PhysicalCounts), CityCountFacts = Counts(r.CityCounts),
            PositiveHighestPlayerIds = new List<int>(r.PositiveHighestPlayerIds),
            ControlClaims = GameStateCloneService.DeepClone(r.ControlClaims), InfluenceModifiers = GameStateCloneService.DeepClone(r.InfluenceModifiers),
            TieModifiers = GameStateCloneService.DeepClone(r.TieModifiers), ValueModifiers = GameStateCloneService.DeepClone(r.ValueModifiers) };
        private static List<PlayerInfluenceCountState> Counts(Dictionary<int, int> values) => values.OrderBy(p => p.Key)
            .Select(p => new PlayerInfluenceCountState { PlayerId = p.Key, Count = p.Value }).ToList();
        public RegionControlResult ToResult() => new RegionControlResult {
            RegionId = RegionId, BaseScoreValue = BaseScoreValue, ScoreValue = ScoreValue,
            ControllerPlayerId = ControllerPlayerId, ControlReason = ControlReason, IsTie = IsTie,
            InfluenceCounts = InfluenceCounts, PhysicalCounts = PhysicalCounts, CityCounts = CityCounts,
            PositiveHighestPlayerIds = new List<int>(PositiveHighestPlayerIds),
            ControlClaims = GameStateCloneService.DeepClone(ControlClaims), InfluenceModifiers = GameStateCloneService.DeepClone(InfluenceModifiers),
            TieModifiers = GameStateCloneService.DeepClone(TieModifiers), ValueModifiers = GameStateCloneService.DeepClone(ValueModifiers) };
    }

    // 本次查询的小型索引事实；完整地图来自同次Lua只读快照，避免挤入NormalizedValue的128元素预算。
    public sealed class ScoringQueryRequest
    {
        public string SessionId = "", Pass = "", RegionId = "", BasisDigest = "";
        public int PlayerId = -1;
        public NormalizedValue Facts;
    }
}
