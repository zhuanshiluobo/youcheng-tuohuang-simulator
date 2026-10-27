using System;
using System.Linq;
using UnityEngine;

namespace YC.Presentation
{
    public sealed partial class FacilityBuildDialogView
    {
        [SerializeField] private string panelSelectLabel;
        [SerializeField] private string panelSelectQuote;
        internal void ShowPanelSelection(FacilityPanelSelectionModel model, CardVisualCatalog catalog, int selected,
            Func<bool> isCurrent, Action<int> select, Action commit, Action decline)
        {
            foreach (var item in generated) { if (item == null) continue; item.SetActive(false); Destroy(item); }
            generated.Clear(); submittingPayment = false; supplyItemIds.Clear();
            foreach (var slot in fixedSupplySlots) slot.gameObject.SetActive(false);
            foreach (var row in paymentRows) { row.Button.onClick.RemoveAllListeners(); row.Button.interactable = false; row.gameObject.SetActive(false); }
            buildHint.SetActive(false); supplyInspectionHint.SetActive(false); costBreakdown.SetActive(false); reason.gameObject.SetActive(false);
            GameplayHudFrame.Active?.ShowPage(gameObject, true); if (GameplayHudFrame.Active == null) gameObject.SetActive(true);
            confirmLabel.text = panelSelectLabel; quote.gameObject.SetActive(true); quote.text = panelSelectQuote;
            bool Current() => gameObject.activeInHierarchy && !submittingPayment && isCurrent() &&
                !GameplayHudFrame.InformationInputBlockedThisFrame && !GameplayHudFrame.EffectInputSuspended;
            for (int i = 0; i < citySlots.Length; i++)
            {
                int index = i; bool legal = model.CanSelect(i);
                citySlots[i].interactable = legal; citySlots[i].onClick.RemoveAllListeners();
                citySlots[i].onClick.AddListener(() => { if (Current() && model.CanSelect(index)) select(index); });
                slotSelection[i].enabled = legal; slotSelection[i].color = i == selected ? selectedSlotColor : legalSlotColor;
                var top = model.Cells.FirstOrDefault(c => c.Index == i)?.Cards.FirstOrDefault();
                var sprite = top == null ? null : catalog.GetFacility(top.DefinitionId);
                CardArtworkView.Set(cityCards[i], sprite); cityCards[i].enabled = sprite != null;
                YC.PlayerJourney.PlayerAutomationId.Attach(citySlots[i].gameObject, "facility.panel.slot." + i);
            }
            confirm.gameObject.SetActive(true); confirm.interactable = model.CanSelect(selected);
            confirm.onClick.RemoveAllListeners(); confirm.onClick.AddListener(() => {
                if (!Current() || !model.CanSelect(selected)) return;
                submittingPayment = true; confirm.interactable = false;
                try { commit(); } catch { submittingPayment = false; throw; }
            });
            cancel.gameObject.SetActive(model.Request.AllowDecline); cancel.interactable = model.Request.AllowDecline;
            cancel.onClick.RemoveAllListeners(); cancel.onClick.AddListener(() => { if (Current() && model.Request.AllowDecline) decline(); });
            YC.PlayerJourney.PlayerAutomationId.Attach(confirm.gameObject, "facility.panel.confirm");
        }
    }
}
