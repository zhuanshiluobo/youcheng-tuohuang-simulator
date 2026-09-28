using YC.Domain.State;

namespace YC.Presentation
{
    public sealed partial class MobileCityInteractionController
    {
        private string lastPresentedSaleReceipt = string.Empty;

        private GameStateView VisibleSaleState()
        {
            if (session == null) return null;
            return session.View ?? GameStateViewProjector.ProjectForPlayer(session.State, localPlayerId);
        }

        private void PresentLatestResourceSaleReceipt()
        {
            var visible = VisibleSaleState();
            var receipt = ResourceSaleReceiptProjection.Latest(visible, localPlayerId);
            if (receipt == null) return;
            var key = visible.GameId + "|" + localPlayerId + "|" + receipt.EffectId;
            if (key == lastPresentedSaleReceipt) return;
            lastPresentedSaleReceipt = key;
            SetPrompt(string.Format(gameplayInteractionHud.DialogRegistry.EffectDialogLayoutProfile.SaleReceiptSummaryFormat,
                receipt.Revenue));
        }
    }
}
