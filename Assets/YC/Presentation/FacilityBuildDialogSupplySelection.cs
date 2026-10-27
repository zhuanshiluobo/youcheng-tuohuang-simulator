using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed partial class FacilityBuildDialogView
    {
        [SerializeField] private string supplySelectLabel;
        [SerializeField] private string supplySelectQuote;

        public void ShowSupplySelection(FacilitySupplySelectionModel model, CardVisualCatalog catalog,
            string selectedId, Func<bool> isCurrent, Action<string> select, Action confirmSelection,
            Action decline, Action<GameplayTextView> renderPrompt = null)
        {
            quoteDocumentView.ClearBinding();
            foreach (var item in generated) { if (item == null) continue; item.SetActive(false); Destroy(item); }
            generated.Clear(); supplyItemIds.Clear(); submittingPayment = false;
            bool Current() => this != null && gameObject.activeInHierarchy && !submittingPayment && isCurrent() &&
                !GameplayHudFrame.InformationInputBlockedThisFrame && !GameplayHudFrame.EffectInputSuspended;
            GameplayHudFrame.Active?.ShowPage(gameObject, true);
            if (GameplayHudFrame.Active == null) gameObject.SetActive(true);
            buildHint.SetActive(false); supplyInspectionHint.SetActive(false);
            foreach (var row in paymentRows) { row.Button.onClick.RemoveAllListeners(); row.Button.interactable = false; row.gameObject.SetActive(false); }
            costBreakdown.SetActive(false); reason.gameObject.SetActive(false);
            candidatesHeading.text = supplyHeading; confirmLabel.text = supplySelectLabel;
            quote.gameObject.SetActive(true); quote.text = supplySelectQuote; renderPrompt?.Invoke(quoteDocumentView);
            for (int i = 0; i < citySlots.Length; i++)
            {
                citySlots[i].interactable = false; citySlots[i].onClick.RemoveAllListeners(); slotSelection[i].enabled = false;
                // 纯选择不显示上一建设草稿的城市卡。
                cityCards[i].enabled = false;
            }
            foreach (var slot in fixedSupplySlots) slot.gameObject.SetActive(false);
            foreach (var slot in model.Slots)
            {
                while (fixedSupplySlots.Count <= slot.Index)
                    fixedSupplySlots.Add(Instantiate(supplySlotTemplate, candidates, false));
                var root = fixedSupplySlots[slot.Index]; root.gameObject.SetActive(true);
                supplyItemIds.Add(slot.TopId);
                var stackObject = new GameObject("FacilitySupplyLayers", typeof(RectTransform), typeof(FacilitySupplyStackLayout));
                var stack = stackObject.GetComponent<FacilitySupplyStackLayout>();
                stackObject.transform.SetParent(root, false); generated.Add(stackObject);
                var cards = new List<FacilityEffectCardView>();
                float artworkWidth = 0f;
                foreach (var item in slot.Layers)
                {
                    var id = item.ItemId;
                    var card = Instantiate(cardTemplate, stack.transform, false);
                    card.gameObject.SetActive(true); card.FitIntoCell();
                    var sprite = catalog.GetFacility(item.DefinitionId);
                    if (cards.Count == 0 && sprite != null) artworkWidth = sprite.rect.width;
                    CardArtworkView.Set(card.CardImage, sprite);
                    card.CardImage.color = sprite == null ? Color.clear : Color.white;
                    card.FallbackLabel.gameObject.SetActive(sprite == null);
                    card.FallbackLabel.text = item.DefinitionId;
                    if (card.SelectionImage != null) card.SelectionImage.enabled = id == selectedId && model.CanSelect(id);
                    card.Button.interactable = model.CanSelect(id);
                    card.Button.onClick.RemoveAllListeners();
                    card.Button.onClick.AddListener(() => { if (Current() && model.CanSelect(id)) select(id); });
                    card.ConfigureInspection(() =>
                    {
                        if (!Current() || sprite == null) return;
                        if (viewer == null) viewer = CardViewer.InstantiateFor(transform);
                        viewer.OpenInspect(sprite, () => { if (isCurrent()) GameplayHudFrame.Active?.ResumeEffectPage(); }, isCurrent);
                    }, Current);
                    YC.PlayerJourney.PlayerAutomationId.Attach(card.Button.gameObject, "supply.select." + id);
                    cards.Add(card);
                }
                // 先画深层，真实顶牌最后绘制；下层没有选择回调资格。
                foreach (var card in cards.AsEnumerable().Reverse()) card.transform.SetAsLastSibling();
                stack.Configure(cards, artworkWidth, supplySlotTemplate.rect.width);
            }
            confirm.gameObject.SetActive(true); confirm.interactable = model.CanSelect(selectedId);
            confirm.onClick.RemoveAllListeners();
            confirm.onClick.AddListener(() =>
            {
                if (!Current() || !model.CanSelect(selectedId)) return;
                submittingPayment = true; confirm.interactable = false;
                try { confirmSelection(); } catch { submittingPayment = false; throw; }
            });
            cancel.gameObject.SetActive(model.Request.AllowDecline); cancel.interactable = model.Request.AllowDecline;
            cancel.onClick.RemoveAllListeners(); cancel.onClick.AddListener(() => { if (Current() && model.Request.AllowDecline) decline(); });
            YC.PlayerJourney.PlayerAutomationId.Attach(confirm.gameObject, "supply.select.confirm");
        }
    }
}
