using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Presentation;
using YC.Presentation.Workflows;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed partial class SettlementEffectStripView
    {
        private readonly Dictionary<long,SettlementEffectChipView> allocationChips=new Dictionary<long,SettlementEffectChipView>();
        private string allocationKey="";
        private readonly List<long> allocationOrder=new List<long>();
        public int AllocationPreviewCount=>allocationChips.Count;
        public void ShowAllocationQueue(string key,IReadOnlyList<EffectAllocationDraftEntry> entries,int revision)
        {
            if(string.IsNullOrEmpty(key) || entries==null || entries.Count>99)throw new ArgumentException("分配展示队列身份或长度无效。");
            if(!ValidateReferences(out var reason) || resolveText==null || bindText==null || measureText==null || resolveIcon==null)throw new InvalidOperationException("分配展示队列缺少现有结算组件或展示端口："+reason);
            if(allocationKey!=key)ClearAllocationQueue(allocationKey);
            allocationKey=key;
            var live=new HashSet<long>();foreach(var entry in entries)if(entry==null || !live.Add(entry.Identity))throw new ArgumentException("分配条目的本地身份必须独立。");
            var removed=new List<long>();foreach(long id in allocationChips.Keys)if(!live.Contains(id))removed.Add(id);
            foreach(long id in removed)ReleaseAllocation(id);
            allocationOrder.Clear();
            foreach(var entry in entries)
            {
                if(!allocationChips.TryGetValue(entry.Identity,out var chip)) {chip=pool.Count>0?pool.Pop():Instantiate(chipTemplate,content,false);allocationChips.Add(entry.Identity,chip);}
                allocationOrder.Add(entry.Identity);
                // 仅本地显示DTO，不进入SettlementEffectStripBinding、来源目录或业务命令。
                var label=entry.Text;
                var option=new SettlementOptionView {OptionId=key+":"+entry.Identity,StateRevision=revision,IsEnabled=false,CanInspectSource=false,
                    Presentation=new SettlementPresentationDefinition{ContentMode="text",Label=label,Description=label.Clone(),AriaLabel=label.Clone()}};
                chip.Bind(option,false,false,0,entry.Identity,viewport.rect.width,resolveText,bindText,measureText,resolveIcon,null,null,scrollRect,ManualScroll);
                chip.SetDisplayOnly();chip.gameObject.SetActive(true);
            }
            PositionAllocationQueue();LayoutRebuilder.MarkLayoutForRebuild(content);NotifyButtonsVisibility();
        }
        public void ClearAllocationQueue(string key)
        {
            if(allocationKey!=key)return;
            foreach(long id in new List<long>(allocationChips.Keys))ReleaseAllocation(id);
            allocationOrder.Clear();allocationKey="";NotifyButtonsVisibility();
        }
        internal void ClearAllAllocationPreviews()=>ClearAllocationQueue(allocationKey);
        private void ReleaseAllocation(long id)
        {
            var chip=allocationChips[id];allocationChips.Remove(id);chip.ClearBinding();chip.gameObject.SetActive(false);pool.Push(chip);
        }
        private void PositionAllocationQueue()
        {
            int index=chips.Count+1;foreach(long id in allocationOrder)if(allocationChips.TryGetValue(id,out var chip))chip.transform.SetSiblingIndex(index++);
        }
        private void FitAllocationQueue()
        {
            foreach(var chip in allocationChips.Values)chip.FitWidth(viewport.rect.width);
        }
    }
}
