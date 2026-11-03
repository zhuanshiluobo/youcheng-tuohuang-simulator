using System;
using System.Linq;
using YC.Domain.Interactions;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    internal sealed partial class CharacterCardEffectChoiceDialog
    {
        private string allocationPageId="";
        public void ShowEffectAllocation(InteractionRequestProjection request,EffectAllocationDraft draft,Func<bool> isCurrent,Action changed,Action<string> submit)
        {
            var scroll=allocationPageId==request.InteractionId?shell.ScrollPosition:1f;allocationPageId=request.InteractionId;
            var labels=draft.OptionIds.Select(id=>ResolveGameplayText(request.Presentation.QuantityRows.Single(r=>r.RowId==id).Label)?.ToPlainText() ?? throw new InvalidOperationException("分配选项正式文案未接入。")).ToList();
            var panel=PreparePage(layoutProfile.SalePanelSize);
            shell.AddResourceAllocation(panel,new ResourceAllocationSpec {
                Request=request,RowIds=draft.OptionIds,ResolveText=resolveText,RenderText=renderText,
                Title=layoutProfile.AllocationTitle,Description=layoutProfile.AllocationDescription,Labels=labels,Maximums=draft.Maximums,InitialValues=draft.Counts,
                MinimumTotal=0,ExactTotal=draft.TotalExecutions,MaximumTotal=draft.TotalExecutions,ShowInventoryPreview=false,HideResourceIdentity=true,CountLimitFormat=layoutProfile.AllocationLimitFormat,
                IsCurrent=isCurrent,CanEdit=()=>!draft.Pending,CanConfirm=()=>draft.CanConfirm,CanCancel=()=>draft.CanEnd,TrySetAmount=draft.TrySetAmount,DraftChanged=_=>changed(),
                LabelNamePrefix="Effect Allocation Label ",DecreaseNamePrefix="Effect Allocation Decrease ",ValueNamePrefix="Effect Allocation Value ",IncreaseNamePrefix="Effect Allocation Increase ",
                ConfirmName="Confirm Effect Allocation",ConfirmLabel=layoutProfile.AllocationConfirmLabel,CancelName="End Effect Allocation",CancelLabel=layoutProfile.AllocationEndLabel,
                SummaryName="Effect Allocation Progress",FormatSummary=_=>string.Format(layoutProfile.AllocationProgressFormat,draft.Entries.Count,draft.TotalExecutions)+(draft.ResetNotice?"\n"+layoutProfile.AllocationResetNotice:""),
                RowStartY=layoutProfile.CharacterResourceSaleRowStartY,Confirm=_=>submit("confirm"),Cancel=draft.AllowEndAction?(Action)(()=>submit("end_action")):null,
                CloseBeforeConfirm=false,CloseBeforeCancel=false
            });
            shell.RestoreScroll(scroll);
        }
    }
}
