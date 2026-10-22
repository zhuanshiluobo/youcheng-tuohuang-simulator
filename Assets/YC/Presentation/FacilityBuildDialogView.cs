using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Domain.Facilities;
using YC.Domain.State;
using YC.Domain.Items;
using System.Linq;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>建设面板：选择与支付、补牌后的放置共用同一资产。</summary>
    public sealed class FacilityBuildDialogView : MonoBehaviour
    {
        [SerializeField] private RectTransform candidates;
        [SerializeField] private FacilityEffectCardView cardTemplate;
        [SerializeField] private RectTransform supplySlotTemplate;
        [SerializeField] private EffectDialogOptionRowView[] paymentRows;
        [SerializeField] private UiResourceCostView[] paymentCosts;
        [SerializeField] private UiResourceCostView originalCost;
        [SerializeField] private UiResourceCostView discountCost;
        [SerializeField] private GameObject costBreakdown;
        [SerializeField] private Button[] citySlots;
        [SerializeField] private RawImage[] cityCards;
        [SerializeField] private Image[] slotSelection;
        [SerializeField] private Text quote;
        [SerializeField] private Text reason;
        [SerializeField] private Button confirm;
        [SerializeField] private Button cancel;
        [SerializeField] private Text candidatesHeading;
        [SerializeField] private Text confirmLabel;
        [SerializeField] private string supplyHeading = "设施供应区";
        [SerializeField] private string alternateHeading = "可选设施牌";
        [SerializeField] private string payLabel = "确认支付";
        [SerializeField] private string placeLabel = "确认放置";
        [SerializeField] private string pendingQuote = "请选择设施牌和支付方式，支付后补牌，再放入城市面板。";
        [SerializeField] private string paidQuoteFormat = "已支付，请将 {0} 放入城市面板。";
        [SerializeField] private Color legalSlotColor = new Color(.25f, 1f, .45f, .8f);
        [SerializeField] private Color selectedSlotColor = new Color(1f, .8f, .3f, 1f);
        private readonly List<GameObject> generated = new List<GameObject>();
        private readonly List<RectTransform> fixedSupplySlots = new List<RectTransform>();
        private readonly List<string> supplyItemIds = new List<string>();
        private ItemZoneStateView visibleItems;
        private GameState definitionState;
        public IReadOnlyList<string> SupplyItemIds => supplyItemIds.AsReadOnly();
        public IReadOnlyList<RectTransform> SupplySlots => fixedSupplySlots.AsReadOnly();
        [SerializeField] private Text supplyDeckText;
        [SerializeField] private Text extensionHubsText;
        [SerializeField] private string supplyDeckFormat = "设施牌堆·{0}";
        [SerializeField] private string extensionHubFormat = "<color=#{0}>延伸枢纽</color>";
        [SerializeField] private string extensionHubSeparator = "·";
        [SerializeField] private UiResourceCostView ownedResources;
        [SerializeField] private Color[] availableHubColors;
        [SerializeField] private Color usedHubColor = Color.gray;
        [SerializeField] private GameObject buildHint;
        [SerializeField] private GameObject supplyInspectionHint;
        [SerializeField] private string inspectionQuote = "请选择设施牌查看费用。";
        private CardViewer viewer;

        public void RefreshSupplyStatus(GameState state, int playerId, int? visibleDeckCount = null)
        {
            definitionState = state;
            if (state == null) return;
            visibleItems = new ItemZoneQuery().Project(state, GameStateViewer.Player(playerId));
            supplyDeckText.text = string.Format(supplyDeckFormat, visibleDeckCount ?? state.Decks.FacilityDeck.Count);
            ownedResources.SetCost(state.FindPlayer(playerId)?.Resources);
            var ids = new[] { FacilityCardDatabase.ExtensionHubRed, FacilityCardDatabase.ExtensionHubYellow,
                FacilityCardDatabase.ExtensionHubBlue };
            var labels = new List<string>();
            for (var i = 0; i < ids.Length; i++)
            {
                var used = false;
                foreach (var facility in state.Map.Facilities)
                    if (facility.FacilityCardId == ids[i]) { used = true; break; }
                if (!used)
                    labels.Add(string.Format(extensionHubFormat, ColorUtility.ToHtmlStringRGB(availableHubColors[i])));
            }
            extensionHubsText.text = labels.Count == 0
                ? string.Format(extensionHubFormat, ColorUtility.ToHtmlStringRGB(usedHubColor))
                : string.Join(extensionHubSeparator, labels);
        }
        private bool submittingPayment;

        public void Show(BuildFacilityDraftViewModel model, IReadOnlyList<FacilityPlacement> facilities, int playerId, CardVisualCatalog catalog,
            EffectDialogLayoutProfile copy, string selectedId, string summary, Func<bool> isCurrent,
            Action<string> selectCard, Action<int> selectSlot, Action<string> selectPayment,
            Action commit, Action dismiss, bool inspectionOnly = false)
        {
            foreach (var item in generated) { if (item == null) continue; item.SetActive(false); Destroy(item); }
            generated.Clear();
            submittingPayment = false;
            var placing = model.Phase == BuildFacilityDraftPhase.Placing;
            buildHint.SetActive(!inspectionOnly);
            supplyInspectionHint.SetActive(inspectionOnly);
            bool Current() => this != null && gameObject.activeInHierarchy && !submittingPayment && isCurrent() &&
                !GameplayHudFrame.InformationInputBlockedThisFrame &&
                (!placing || !GameplayHudFrame.EffectInputSuspended);
            GameplayHudFrame.Active?.ShowPage(gameObject, placing || model.IsEffectBuild);
            if (GameplayHudFrame.Active == null) gameObject.SetActive(true);
            if (candidatesHeading != null) candidatesHeading.text = model.IsSupplySource ? supplyHeading : alternateHeading;
            if (confirmLabel != null) confirmLabel.text = placing ? placeLabel : payLabel;
            cancel.gameObject.SetActive(!placing);
            var selectedOption = model.SelectedOption;
            if (!placing && !string.IsNullOrEmpty(selectedId))
            {
                selectedOption = null;
                foreach (var option in model.Options)
                    if (option.FacilityId == selectedId) { selectedOption = option; break; }
            }
            var sameDraft = selectedOption != null && model.SelectedOption != null &&
                selectedOption.FacilityId == model.SelectedOption.FacilityId;
            var selectedDefinition = selectedOption == null ? null : FacilityCardDatabase.Get(definitionState,
                string.IsNullOrEmpty(selectedOption.DefinitionId) ? selectedOption.FacilityId : selectedOption.DefinitionId);
            if (!placing && selectedOption != null && selectedDefinition == null)
                throw new InvalidOperationException("所选设施缺少当前内容定义：" + selectedOption.DefinitionId + "（实例=" + selectedOption.FacilityId + "）。");
            FacilityEffectCardView Card(string id, string definitionId, Transform parent, bool selected, Action click)
            {
                var card = Instantiate(cardTemplate, parent, false);
                card.gameObject.SetActive(true);
                card.FitIntoCell();
                if (string.IsNullOrEmpty(definitionId)) definitionId = id;
                var sprite = catalog.GetFacility(definitionId);
                CardArtworkView.Set(card.CardImage, sprite);
                card.CardImage.color = sprite == null ? Color.clear : Color.white;
                card.FallbackLabel.gameObject.SetActive(sprite == null);
                card.FallbackLabel.text = FacilityCardDatabase.Get(definitionState, definitionId)?.Name ?? definitionId;
                if (card.SelectionImage != null) card.SelectionImage.enabled = selected;
                card.Button.interactable = click != null;
                card.Button.onClick.RemoveAllListeners();
                card.Button.onClick.AddListener(() => { if (Current()) click?.Invoke(); });
                card.ConfigureInspection(() =>
                {
                    if (!Current() || sprite == null) return;
                    if (viewer == null) viewer = CardViewer.InstantiateFor(transform);
                    viewer.OpenInspect(sprite, () =>
                    {
                        if (!isCurrent()) return;
                        if (placing || model.IsEffectBuild) GameplayHudFrame.Active?.ResumeEffectPage();
                        else GameplayHudFrame.Active?.ShowPage(gameObject, false);
                    }, isCurrent);
                }, Current);
                return card;
            }
            supplyItemIds.Clear();
            foreach (var slot in fixedSupplySlots) slot.gameObject.SetActive(model.IsSupplySource);
            if (model.IsSupplySource)
            {
                for (var index = 0; index < FacilitySupplyService.SupplySize; index++)
                {
                    if (fixedSupplySlots.Count <= index)
                    {
                        var slot = Instantiate(supplySlotTemplate, candidates, false); slot.name = "FacilitySupplySlot";
                        fixedSupplySlots.Add(slot);
                    }
                    var supplySlot = fixedSupplySlots[index]; supplySlot.gameObject.SetActive(true);
                    var zone = visibleItems?.Zones.Find(value => value.Zone.LocalZoneId == "facility_supply_" + index)?.Zone;
                    var item = visibleItems?.Items.Find(value => value.ZoneId == zone?.ZoneId);
                    supplyItemIds.Add(item?.ItemId ?? string.Empty);
                    string id = item?.ItemId;
                    // 旧无实体夹具保留固定索引兼容；生产始终使用真实槽区。
                    if (visibleItems?.Zones.Count == 0 && index < model.Options.Count) id = model.Options[index].FacilityId;
                    if (string.IsNullOrEmpty(id)) continue;
                    var option = model.Options.FirstOrDefault(value => value.FacilityId == id);
                    var card = Card(id, option?.DefinitionId ?? item?.DefinitionId, supplySlot, !placing && id == selectedId,
                        placing || option == null ? (Action)null : () => selectCard(id));
                    generated.Add(card.gameObject);
                    YC.PlayerJourney.PlayerAutomationId.Attach(card.Button.gameObject, "build.candidate." + (item?.ItemId ?? id));
                }
            }
            else foreach (var option in model.Options)
            {
                var id = option.FacilityId;
                var slot = Instantiate(supplySlotTemplate, candidates, false); slot.gameObject.SetActive(true);
                generated.Add(slot.gameObject);
                var card = Card(id, option.DefinitionId, slot, !placing && id == selectedId, placing ? (Action)null : () => selectCard(id));
                YC.PlayerJourney.PlayerAutomationId.Attach(card.Button.gameObject, "build.candidate." + id);
            }
            for (var i = 0; i < citySlots.Length; i++)
            {
                var index = i;
                FacilityPlacement placement = null;
                foreach (var item in facilities)
                    if (item.PlayerId == playerId && item.CityBoardSlotIndex == i) { placement = item; break; }

                var legal = false;
                if (placing)
                {
                    foreach (var slot in model.LegalSlotIndexes) if (slot == i) legal = true;
                }
                else if (!inspectionOnly && selectedOption != null && selectedOption.CanBuild)
                    foreach (var slot in selectedOption.SlotOptions)
                        if (slot.CityBoardSlotIndex == i) legal = slot.IsLegal;
                // 付款前只提示空间，付款并补牌后才能落位。
                citySlots[i].interactable = placing && legal;
                slotSelection[i].enabled = legal;
                slotSelection[i].color = placing && model.CityBoardSlotIndex == i ? selectedSlotColor : legalSlotColor;
                citySlots[i].onClick.RemoveAllListeners();
                citySlots[i].onClick.AddListener(() => { if (Current()) selectSlot(index); });
                YC.PlayerJourney.PlayerAutomationId.Attach(citySlots[i].gameObject, "build.slot." + i);
                var id = placement?.FacilityCardId;
                if (id == null && placing && model.CityBoardSlotIndex == i) id = model.Facility?.FacilityId;
                var sprite = string.IsNullOrEmpty(id) ? null : catalog.GetFacility(id);
                CardArtworkView.Set(cityCards[i], sprite);
                cityCards[i].enabled = sprite != null;
            }
            // 两个支付按钮由预制体固定提供；刷新选择只更新状态，不销毁重建按钮。
            for (var i = 0; i < paymentRows.Length; i++)
            {
                var row = paymentRows[i];
                var mode = i == 0 ? BuildFacilityService.PaymentModeResources : BuildFacilityService.PaymentModeGold;
                var payment = selectedOption == null ? null :
                    i == 0 ? selectedOption.ResourcesPayment : selectedOption.GoldPayment;
                var definition = selectedDefinition;
                var preparation = sameDraft && model.PaymentPreparation?.PaymentMode == mode ? model.PaymentPreparation : null;
                paymentCosts[i].SetCost(preparation?.RemainingCost ?? (definition == null ? null : i == 0
                    ? selectedOption.EffectiveResourceCost : new ResourceSet { GoldVoucher = definition.GoldVoucherCost }),
                    preparation?.OriginalCost ?? (definition == null ? null : i == 0 ? definition.ResourceCost :
                        new ResourceSet { GoldVoucher = definition.GoldVoucherCost }));
                row.SetSelected(false);
                row.Button.interactable = !inspectionOnly && !placing && payment != null && sameDraft &&
                    isCurrent() && CanUseBuildPayment(model, mode);
                row.Button.onClick.RemoveAllListeners();
                row.Button.onClick.AddListener(() =>
                {
                    if (!Current() || !row.Button.IsInteractable()) return;
                    submittingPayment = true;
                    foreach (var item in paymentRows) item.Button.interactable = false;
                    cancel.interactable = false;
                    try { selectPayment(mode); }
                    catch { submittingPayment = false; throw; }
                });
                YC.PlayerJourney.PlayerAutomationId.Attach(row.Button.gameObject, "build.payment." + mode);
            }
            cancel.interactable = model.CanCancel && (model.PaymentPreparation == null || model.PaymentPreparation.CanCancel);
            confirm.gameObject.SetActive(placing);
            costBreakdown.SetActive(!placing && selectedOption != null);
            quote.gameObject.SetActive(placing || selectedOption == null);
            if (placing) quote.text = string.Format(paidQuoteFormat, model.Facility?.Name ?? selectedId);
            else if (selectedOption == null) quote.text = inspectionOnly ? inspectionQuote : pendingQuote;
            else
            {
                var definition = selectedDefinition;
                var original = definition.ResourceCost;
                var effective = selectedOption.EffectiveResourceCost;
                originalCost.SetCost(new ResourceSet { Originium = original.Originium,
                    OriginiumShard = original.OriginiumShard, Iron = original.Iron,
                    PureOriginium = original.PureOriginium, GoldVoucher = definition.GoldVoucherCost });
                discountCost.SetCost(new ResourceSet {
                    Originium = Mathf.Max(0, original.Originium - effective.Originium),
                    OriginiumShard = Mathf.Max(0, original.OriginiumShard - effective.OriginiumShard),
                    Iron = Mathf.Max(0, original.Iron - effective.Iron),
                    PureOriginium = Mathf.Max(0, original.PureOriginium - effective.PureOriginium) });
            }
            reason.text = inspectionOnly ? string.Empty : selectedOption != null && !selectedOption.CanBuild && !placing &&
                model.PaymentPreparation?.CanConfirm != true ? selectedOption.Reason : model.ErrorMessage;
            reason.gameObject.SetActive(!string.IsNullOrEmpty(reason.text));
            var selectedLegal = false;
            foreach (var slot in model.LegalSlotIndexes) if (slot == model.CityBoardSlotIndex) selectedLegal = true;
            confirm.interactable = placing ? selectedLegal :
                model.Phase == BuildFacilityDraftPhase.Confirming && sameDraft && selectedOption.CanBuild;
            confirm.onClick.RemoveAllListeners(); confirm.onClick.AddListener(() => { if (Current()) commit(); });
            cancel.onClick.RemoveAllListeners(); cancel.onClick.AddListener(() => { if (Current()) dismiss(); });
            YC.PlayerJourney.PlayerAutomationId.Attach(confirm.gameObject, "build.confirm");
        }

        public void Hide()
        {
            if (viewer != null) viewer.Dismiss();
            GameplayHudFrame.Active?.ReleasePage(gameObject);
            gameObject.SetActive(false);
        }

        private static bool CanUseBuildPayment(BuildFacilityDraftViewModel model, string mode)
        {
            var option = model?.SelectedOption;
            if (option == null) return false;
            var payment = mode == BuildFacilityService.PaymentModeResources ? option.ResourcesPayment :
                mode == BuildFacilityService.PaymentModeGold ? option.GoldPayment : null;
            if (payment == null) return false;
            var preparation = model.PaymentPreparation;
            if (preparation != null && preparation.ActionKind == "build" &&
                preparation.TargetId == option.FacilityId && preparation.PaymentMode == mode &&
                !string.IsNullOrEmpty(preparation.PreparationOwnerEffectId))
                return preparation.IsReady && preparation.CanConfirm;
            return payment.IsAvailable;
        }
    }
}
