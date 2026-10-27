using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Items
{
    [Serializable]
    public sealed class ItemTransferRequest
    {
        public List<string> ItemIds = new List<string>();
        public ItemPositionRef ExpectedSource = new ItemPositionRef();
        public ZoneRef Destination = new ZoneRef();
        public string InsertionKind = string.Empty, DestinationSlotId = string.Empty;
        public string OperationProfileId = string.Empty, CarrierPolicyId = "preserve";
        public int ExecutingPlayerId = -1, RequiredCount;
    }
    public sealed class ItemTransferValidationResult
    {
        public bool IsValid;
        public string ReasonCode = string.Empty;
    }
    [Serializable]
    public sealed class ItemPositionChange
    {
        public string ItemId = string.Empty;
        public ItemPositionRef From = new ItemPositionRef(), To = new ItemPositionRef();
    }
    [Serializable]
    public sealed class ItemTransferResult
    {
        public bool Changed;
        public int MovedCount;
        public List<string> MovedItemIds = new List<string>();
        public List<ItemPositionChange> PositionChanges = new List<ItemPositionChange>();
    }
}
