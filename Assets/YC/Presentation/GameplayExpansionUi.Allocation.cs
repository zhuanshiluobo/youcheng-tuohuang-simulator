using System;
using System.Collections.Generic;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    public sealed partial class GameplayExpansionUi
    {
        public void ShowAllocationQueue(string key,IReadOnlyList<EffectAllocationDraftEntry> entries,int revision)
        {
            var view=VisibleView();
            if(shuttingDown || settlementStrip==null || hud==null || !hud.Frame.GameplayPresentationReady || view==null ||
                view.ViewerRole!=GameStateViewerRole.Player || view.ViewerPlayerId!=getPlayer?.Invoke())throw new InvalidOperationException("当前玩家的分配展示端口尚未就绪。");
            settlementStrip.SetScope(view.GameId+":"+view.ContentPackHash,view.ViewerPlayerId);
            settlementStrip.ConfigurePresentation(hud.Frame.ResolveGameplayText,hud.Frame.RenderGameplayText,hud.Frame.MeasureGameplayText,hud.Frame.ResolveGameplayIcon);
            stripPresentationGeneration=hud.Frame.PresentationGeneration;
            if(entries.Count>0)settlementHost.gameObject.SetActive(true);
            settlementStrip.ShowAllocationQueue(key,entries,revision);RefreshSettlementHostVisibility();
        }
        public void ClearAllocationQueue(string key)
        {
            settlementStrip?.ClearAllocationQueue(key);RefreshSettlementHostVisibility();
        }
    }
}
