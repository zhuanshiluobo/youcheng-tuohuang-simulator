using System;
using System.Collections.Generic;
using YC.Application.Interactions;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    internal sealed partial class CharacterAbilityInteractionUiCoordinator
    {
        private readonly EffectAllocationDraft allocationDraft=new EffectAllocationDraft();
        private string allocationQueueKey="";
        public Action<string,IReadOnlyList<EffectAllocationDraftEntry>,int> RenderAllocationQueue { private get;set; }
        public Action<string> ClearAllocationQueue { private get;set; }
        private void ShowAllocation(InteractionRequestProjection request)
        {
            var state=getState();var view=GetVisibleState?.Invoke();
            allocationDraft.Bind(state.GameId,view?.ContentPackHash ?? state.ContentPackHash,getLocalPlayerId(),request);
            allocationQueueKey=state.GameId+":"+getLocalPlayerId()+":"+request.InteractionId+":"+request.CandidateSetId+":"+request.CandidateSetVersion;
            if(ExpansionUi!=null)dialog.BindGameplayText(ExpansionUi.ResolveGameplayText,ExpansionUi.RenderEnterpriseDetails);
            void PresentQueue()
            {
                if(RenderAllocationQueue!=null)RenderAllocationQueue(allocationQueueKey,allocationDraft.Entries,request.StateRevision);
                else if(ExpansionUi!=null)ExpansionUi.ShowAllocationQueue(allocationQueueKey,allocationDraft.Entries,request.StateRevision);
                else throw new InvalidOperationException("有序分配缺少当前结算条显示端口。");
            }
            PresentQueue();
            dialog.ShowEffectAllocation(request,allocationDraft,()=>IsCurrent(request),PresentQueue,kind=>
            {
                if(GameplayHudFrame.EffectInputSuspended || !IsCurrent(request) || !allocationDraft.TrySeal(kind,out var answer))return;
                var command=EffectInteractionCommands.Answer(request,getLocalPlayerId(),null);
                command.Parameters[AllocationSequenceAnswerJson.Parameter]=AllocationSequenceAnswerJson.Encode(answer);
                try { DispatchCommand(command); }
                catch { allocationDraft.Reject();inFlightCommandId="";needsRefresh=true;throw; }
            });
        }
        private void ClearAllocationPresentation()
        {
            if(!string.IsNullOrEmpty(allocationQueueKey))
            {
                if(ClearAllocationQueue!=null)ClearAllocationQueue(allocationQueueKey);
                else ExpansionUi?.ClearAllocationQueue(allocationQueueKey);
            }
            allocationQueueKey="";allocationDraft.Clear();
        }
    }
}
