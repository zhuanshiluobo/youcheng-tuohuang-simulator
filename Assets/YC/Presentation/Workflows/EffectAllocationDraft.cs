using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public sealed class EffectAllocationDraftEntry
    {
        public long Identity { get; }
        public string OptionId { get; }
        public GameplayTextRef Text => text.Clone();
        private readonly GameplayTextRef text;
        internal EffectAllocationDraftEntry(long identity,string id,GameplayTextRef label) { Identity=identity;OptionId=id;text=label.Clone(); }
    }
    /// <summary>API-38：队列是唯一草稿；数量只从队列重算，不从次数向量重建顺序。</summary>
    public sealed class EffectAllocationDraft
    {
        private readonly List<EffectAllocationDraftEntry> entries=new List<EffectAllocationDraftEntry>();
        private List<string> ids=new List<string>();
        private List<int> maximums=new List<int>();
        private List<GameplayTextRef> labels=new List<GameplayTextRef>();
        private string contract="";
        private long nextIdentity;
        private int revision=-1;
        public string RequestId { get; private set; }="";
        public string CandidateSetId { get; private set; }="";
        public int CandidateSetVersion { get; private set; }
        public int TotalExecutions { get; private set; }
        public bool AllowEndAction { get; private set; }
        public bool Pending { get; private set; }
        public bool ResetNotice { get; private set; }
        public IReadOnlyList<EffectAllocationDraftEntry> Entries=>entries.AsReadOnly();
        public IReadOnlyList<string> OptionIds=>ids.AsReadOnly();
        public IReadOnlyList<int> Maximums=>maximums.AsReadOnly();
        public IReadOnlyList<int> Counts=>ids.Select(id=>entries.Count(e=>e.OptionId==id)).ToList().AsReadOnly();
        public bool CanConfirm=>!Pending && entries.Count==TotalExecutions;
        public bool CanEnd=>!Pending && AllowEndAction;
        public bool Bind(string gameId,string contentHash,int viewer,InteractionRequestProjection request)
        {
            if(string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(contentHash) || request==null || !request.VisibleToViewer || request.Status!="open" || request.AnsweringPlayerId!=viewer ||
                request.InteractionTypeId!=AllocationEffects.InteractionType || request.AnswerSchema!=AllocationEffects.AnswerSchema || string.IsNullOrEmpty(request.InteractionId) || string.IsNullOrEmpty(request.OwnerEffectId) ||
                string.IsNullOrEmpty(request.CandidateSetId) || request.CandidateSetVersion<1 || request.CandidateIds==null || request.CandidateIds.Count<1 || request.CandidateIds.Count>16 || request.CandidateIds.Distinct(StringComparer.Ordinal).Count()!=request.CandidateIds.Count)
                throw new ArgumentException("allocation_ui_request_invalid");
            var total=ItemTransferEffectExecutor.Value(request.PromptParameters,"totalExecutions");var end=ItemTransferEffectExecutor.Value(request.PromptParameters,"allowEndAction");var limits=ItemTransferEffectExecutor.Value(request.PromptParameters,"rowMaximums");
            if(total?.Kind!=NormalizedValueKind.Integer || total.IntegerValue<1 || total.IntegerValue>99 || end?.Kind!=NormalizedValueKind.Boolean || limits?.Kind!=NormalizedValueKind.Object || limits.Properties.Count!=request.CandidateIds.Count || request.Presentation?.QuantityRows?.Count!=request.CandidateIds.Count)
                throw new ArgumentException("allocation_ui_limits_invalid");
            var nextMax=new List<int>();var nextLabels=new List<GameplayTextRef>();
            foreach(string id in request.CandidateIds)
            {
                var max=ItemTransferEffectExecutor.Value(limits,id);var rows=request.Presentation.QuantityRows.Where(r=>r.RowId==id).ToList();
                if(max?.Kind!=NormalizedValueKind.Integer || max.IntegerValue<0 || max.IntegerValue>99 || rows.Count!=1 || rows[0].Label==null)throw new ArgumentException("allocation_ui_row_invalid");
                GameplayPresentationNormalizer.ToNormalized(rows[0].Label);nextMax.Add((int)max.IntegerValue);nextLabels.Add(rows[0].Label.Clone());
            }
            string key=JsonConvert.SerializeObject(new object[]{gameId,contentHash,viewer,request.InteractionId,request.OwnerEffectId,request.CandidateSetId,request.CandidateSetVersion,request.CandidateIds,nextMax,nextLabels,total.IntegerValue,end.BooleanValue});
            if(contract==key)
            {
                if(request.StateRevision<revision)return false;
                revision=request.StateRevision;return false;
            }
            contract=key;RequestId=request.InteractionId;CandidateSetId=request.CandidateSetId;CandidateSetVersion=request.CandidateSetVersion;revision=request.StateRevision;
            ids=new List<string>(request.CandidateIds);maximums=nextMax;labels=nextLabels;TotalExecutions=(int)total.IntegerValue;AllowEndAction=end.BooleanValue;
            entries.Clear();Pending=false;ResetNotice=true;return true;
        }
        public bool TrySetAmount(int index,int amount)
        {
            if(Pending || index<0 || index>=ids.Count || amount<0 || amount>maximums[index])return false;
            string id=ids[index];int current=entries.Count(e=>e.OptionId==id),delta=amount-current;
            if(entries.Count+delta>TotalExecutions)return false;
            if(delta>0)for(int i=0;i<delta;i++)entries.Add(new EffectAllocationDraftEntry(++nextIdentity,id,labels[index]));
            else for(int i=entries.Count-1;delta<0 && i>=0;i--)if(entries[i].OptionId==id){entries.RemoveAt(i);delta++;}
            ResetNotice=false;return true;
        }
        public bool TrySeal(string submitKind,out NormalizedValue answer)
        {
            answer=null;if(submitKind=="confirm"?!CanConfirm:submitKind!="end_action" || !CanEnd)return false;
            answer=ItemTransferEffectExecutor.Object("sequence",ItemTransferEffectExecutor.Strings(entries.Select(e=>e.OptionId)),"submitKind",NormalizedValue.CreateString(submitKind),"candidateSetId",NormalizedValue.CreateString(CandidateSetId),"candidateSetVersion",NormalizedValue.CreateInteger(CandidateSetVersion));Pending=true;return true;
        }
        public void Reject() { Pending=false; }
        public void Clear() {contract="";RequestId="";entries.Clear();ids.Clear();maximums.Clear();labels.Clear();Pending=false;ResetNotice=false;revision=-1;}
    }
}
