using System;
using System.Collections.Generic;
using YC.Domain.Items;

namespace YC.Domain.Cards
{
    // 由主盖放业务任务持有，移动节点不持有选择或回收流程。
    [Serializable]
    public sealed class MainCoverFlowStateV1
    {
        public string ContractVersion="",OwnerEffectId="",TaskEffectId="",MainNodeId="",ArgumentsDigest="",HandZoneId="",DiscardZoneId="",CoveredZoneId="",ExistingItemId="",SelectedItemId="",SelectedSourceSlot="";
        public List<string> RecycledItemIds=new List<string>();
        public bool AllPlayersCoveredAtCommit;
        public TransferReceiptV4 NoChangeReceipt;
    }
}
