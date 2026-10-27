using System;
using System.Collections.Generic;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Domain.Facilities
{
    // 查询DTO均是当前真实ItemZones的副本，不是另一份库存或运行阶段。
    [Serializable] public sealed class FacilityCellLayerSnapshot
    {
        public int PlayerId, SlotIndex;
        public string CellZoneId = "", TopItemId = "", CoveredZoneId = "", OrderDigest = "";
        public int OrderRevision;
        public List<string> CoveredItemIds = new List<string>();
        public List<string> AllItemIds = new List<string>();
    }
    [Serializable] public sealed class FacilitySupplyLayerSnapshot
    {
        public string SlotZoneId = "", TopItemId = "", OrderDigest = "";
        public int SlotIndex, OrderRevision;
        public List<string> ItemIds = new List<string>();
    }
    [Serializable] public sealed class FacilityTowerAdjacency
    {
        public string TowerItemId = "";
        public int SlotIndex;
        public List<string> AdjacentTopItemIds = new List<string>();
    }
    [Serializable] public sealed class FacilityBuiltFacts
    {
        public string QueryId = "", FactsDigest = "", ContentHash = "";
        public int PlayerId, StateRevision;
        public List<string> ItemIds = new List<string>();
        public List<AbilityRuleQueryResult> SourceResults = new List<AbilityRuleQueryResult>();
    }
    [Serializable] public sealed class FacilityBuildSourceAuthorization
    {
        public int ProtocolVersion = 1, PlayerId;
        public string SourceKind = "supply", SourceKey = "", SourceZoneId = "", ItemId = "", ContentHash = "";
        public AbilitySourceRef SourceRef;
        public string DefinitionVersion = "", HandlerHash = "";
    }
    [Serializable] public sealed class FacilityBuildSlotCandidate
    {
        public int ProtocolVersion = 1, PlayerId, SlotIndex, StateRevision;
        public string CellZoneId = "", NewItemId = "", ExpectedTopItemId = "", OrderDigest = "", RequiredResponseAbilityId = "";
        public AbilitySourceRef OverlayAuthorization;
        public NormalizedValue RemovalPrecondition = NormalizedValue.CreateNull();
        public bool IsOverlay;
    }
    [Serializable] public sealed class FacilityBlueprintPair
    {
        public string SupplyItemId = "", SupplyZoneId = "", BlueprintItemId = "", BlueprintZoneId = "", Color = "", OrderDigest = "";
    }
    [Serializable] public sealed class FacilityPanelSlotCandidate
    {
        public int SlotIndex;
        public string CellZoneId = "", TopItemId = "", OrderDigest = "";
    }
    [Serializable] public sealed class FacilityPanelSelectionRequest
    {
        public const string InteractionType = "facility.panel.select";
        public int ProtocolVersion = 1, PlayerId, StateRevision, CandidateSetVersion;
        public string CandidateSetId = "", OwnerEffectId = "", OperationProfileId = "";
        public bool AllowDecline;
        public List<FacilityPanelSlotCandidate> Slots = new List<FacilityPanelSlotCandidate>();
    }
}

