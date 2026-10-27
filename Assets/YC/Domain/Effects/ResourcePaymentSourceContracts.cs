using YC.Domain.Rules;
using System;
using System.Collections.Generic;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    [Serializable] public sealed class PaymentItemCost
    {
        public string CardInstanceId = "", LocalZoneId = "", DestinationKind = "original_supply";
        // v1资源替代的itemCost按数量计价；完全相同事实可取等价实例。Lua可显式要求逐项选择。
        public string OperationProfileId = "core.cube_storage", SelectionPolicy = "equivalent";
        public int CountPerUnit;
    }
    [Serializable] public sealed class PaymentSourceRule
    {
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public PaymentSourceInsertion Insertion;
        public int ProtocolVersion = 1, MaxUnits, QuotaLimit;
        public string RuleId = "", QuotaGroupId = "", QuotaScope = "payment", PurposeKind = "";
        public AbilitySourceRef SourceRef;
        public AbilityVersionRef VersionRef;
        public List<ResourceType> AllowedResourceTypes = new List<ResourceType>();
        public ResourceSet UnitResourceCost = new ResourceSet();
        public PaymentItemCost ItemCost;
    }
    [Serializable] public sealed class PaymentSourceInsertion
    {
        public int ApiVersion = 1, MaxUses = 1;
        public YC.Domain.Presentation.SettlementPresentationDefinition Presentation;
    }
    [Serializable] public sealed class PaymentSourceDraft
    {
        public int ProtocolVersion = 1, PaymentGeneration, DraftRevision = 1;
        public string PaymentId = "", BuildIdentity = "", Status = "valid", InvalidReason = "";
        public PaymentSourceRule Rule;
        public ResourceSet Allocation = new ResourceSet();
        public List<string> SelectedItemIds = new List<string>();
    }
    [Serializable] public sealed class PaymentSourceEdit
    {
        public string EditId = "", SourceKey = "", RuleId = "", Status = "open";
        public int PaymentGeneration, EnteredDraftRevision;
        public PaymentSourceDraft PreviousDraft;
    }
    [Serializable] public sealed class PaymentBeforeReceipt
    {
        public string EventId = "";
        public int Generation;
        public ResourceSet GrossCost = new ResourceSet();
        public bool Ready;
    }
    [Serializable] public sealed class PaymentSourceReceipt
    {
        public int ProtocolVersion = 2, PaymentGeneration;
        public string ReceiptId = "", PaymentId = "", BuildIdentity = "";
        public PaymentSourceRule Rule;
        public ResourceSet Allocation = new ResourceSet(), ActualResourceCost = new ResourceSet();
        public List<string> ItemIds = new List<string>(), OriginalSupplyZoneIds = new List<string>();
        public List<ItemPositionChange> PositionChanges = new List<ItemPositionChange>();
        public NormalizedValue TokenArguments;
        public ItemTransferBatchCommit TokenTransfer;
    }
    [Serializable] public sealed class PaymentFinalReceipt
    {
        public int ProtocolVersion = 2;
        public string ReceiptId = "", ContentHash = "", DefinitionVersion = "";
        public string CostReceiptPolicyVersion = "";
        public ResourcePaymentDemandState Demand;
        public ResourceSet SelectedAllocation = new ResourceSet(), GrossCost = new ResourceSet(), FinalCost = new ResourceSet();
        public List<string> SourceReceiptIds = new List<string>();
    }
    public sealed class PaymentCommitPlan
    {
        public string PaymentId, BuildIdentity;
        public int Generation;
        public ResourceSet FinalCost;
        public List<PaymentSourceDraft> Drafts = new List<PaymentSourceDraft>();
        public List<PaymentTokenCostPlan> TokenPlans = new List<PaymentTokenCostPlan>();
        [Obsolete("请改读TokenPlans；此只读别名不调用退役Planner。")]
        public IReadOnlyList<PaymentTokenCostPlan> MarkerPlans => TokenPlans;
    }
    public sealed class PaymentTokenCostPlan
    {
        public string RuleKey;
        public NormalizedValue Arguments;
        public ItemTransferBatchRequest Request;
    }
    // 付款页的只读分页数据通道。明细不嵌入NormalizedValue请求；真实库存不截断、不复制成游戏库存。
    [Serializable] public sealed class PaymentSourceQuery
    {
        public int PlayerId, StateRevision, PaymentGeneration, SourceIndex, ItemOffset;
        public string OwnerEffectId = "", InteractionId = "", SnapshotId = "";
    }
    [Serializable] public sealed class PaymentSourceProjection
    {
        public int ProtocolVersion = 1, PlayerId, StateRevision, PaymentGeneration, SourceCount, SourceIndex, ItemOffset, NextSourceIndex = -1, NextItemOffset;
        public string GameId = "", ContentPackHash = "", OwnerEffectId = "", PaymentId = "", InteractionId = "", SnapshotId = "";
        public List<PaymentSourceRow> Sources = new List<PaymentSourceRow>();
        public PaymentSourceEditProjection Edit;
    }
    [Serializable] public sealed class PaymentSourceEditProjection
    {
        public string EditId = "", SourceKey = "", RuleId = "";
        public int PaymentGeneration, EnteredDraftRevision;
    }
    [Serializable] public sealed class PaymentSourceRow
    {
        public PaymentSourceRule Rule;
        public bool Eligible;
        public int MaxAmount, DraftRevision, AvailableItemCount;
        public string DraftStatus = "unselected", DisplayName = "";
        public ResourceSet RowMaximums = new ResourceSet(), Allocation = new ResourceSet();
        public List<string> SelectedItemIds = new List<string>();
        // 页内库存片段；AvailableItemCount和游标保证读取完整集合后才能供UI选择。
        // 完整集合中选恰好 Allocation合计×CountPerUnit 个互异实例，Host仍逐枚/整组验证。
        public List<PaymentSourceItemProjection> AvailableItems = new List<PaymentSourceItemProjection>();
    }
    [Serializable] public sealed class PaymentSourceItemProjection
    {
        public string ItemId = "", TypeId = "", OriginalSupplyZoneId = "", SelectionGroupId = "";
        public ItemSubjectRef OwnerSubject;
        public NormalizedValue LocalState;
    }
}
