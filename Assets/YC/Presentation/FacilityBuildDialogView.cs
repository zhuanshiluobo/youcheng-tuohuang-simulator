using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Domain.Facilities;
using YC.Domain.State;
using YC.Domain.Items;
using YC.Domain.Presentation;
using System.Linq;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>建设面板：选择与支付、补牌后的放置共用同一资产。</summary>
    public sealed partial class FacilityBuildDialogView : MonoBehaviour
    {
        [SerializeField] private RectTransform verticalPagePanel;
        private RectTransform verticalPageHost;
        private Vector2 originalVerticalOffsets;
        private bool verticalOffsetsCaptured;
        private readonly Vector3[] verticalHostCorners = new Vector3[4];

        private void BindVerticalPageHost(RectTransform host)
        {
            verticalPageHost = host;
            if (verticalPagePanel == null) throw new InvalidOperationException("设施页面缺少竖直边界面板引用。");
            if (!verticalOffsetsCaptured)
            {
                originalVerticalOffsets = new Vector2(verticalPagePanel.offsetMin.y, verticalPagePanel.offsetMax.y);
                verticalOffsetsCaptured = true;
            }
            ApplyVerticalPageBounds();
        }
        private void OnEnable()
        {
            if (GameplayHudFrame.Active != null) BindVerticalPageHost(GameplayHudFrame.Active.ContentRect);
            if (supplyDeckDocument != null)
            {
                supplyDeckDocument.SetVisibilityBindingGuard(() => this != null);
                supplyDeckDocument.TextFragmentRendered -= RenderDeckCountFragment;
                supplyDeckDocument.TextFragmentRendered += RenderDeckCountFragment;
            }
            Canvas.willRenderCanvases += ApplyVerticalPageBounds;
            ApplyVerticalPageBounds();
        }
        private void OnDisable()
        {
            Canvas.willRenderCanvases -= ApplyVerticalPageBounds;
            if (supplyDeckDocument != null) supplyDeckDocument.TextFragmentRendered -= RenderDeckCountFragment;
        }
        private void OnDestroy()
        {
            Canvas.willRenderCanvases -= ApplyVerticalPageBounds;
            if (supplyDeckDocument != null) supplyDeckDocument.TextFragmentRendered -= RenderDeckCountFragment;
        }
        private void ApplyVerticalPageBounds()
        {
            if (!verticalOffsetsCaptured || verticalPageHost == null || verticalPagePanel == null ||
                !(verticalPagePanel.parent is RectTransform parent) || !gameObject.activeInHierarchy) return;
            // 可见面板直接消费最终PageHost的竖直边界；不依赖外层嵌套Canvas的早期高度。
            verticalPageHost.GetWorldCorners(verticalHostCorners);
            float lower = parent.InverseTransformPoint(verticalHostCorners[0]).y;
            float upper = parent.InverseTransformPoint(verticalHostCorners[2]).y;
            float height = upper - lower;
            if (!(height > 0f) || float.IsInfinity(height)) return;
            var min = verticalPagePanel.offsetMin;
            var max = verticalPagePanel.offsetMax;
            float targetMin = lower + height * verticalPagePanel.anchorMin.y + originalVerticalOffsets.x -
                (parent.rect.yMin + parent.rect.height * verticalPagePanel.anchorMin.y);
            float targetMax = lower + height * verticalPagePanel.anchorMax.y + originalVerticalOffsets.y -
                (parent.rect.yMin + parent.rect.height * verticalPagePanel.anchorMax.y);
            if (Mathf.Approximately(min.y, targetMin) && Mathf.Approximately(max.y, targetMax)) return;
            // 只写纵轴；水平锚点、位置、宽度及原三列资产不变。上下留白仍来自本预制体。
            min.y = targetMin; max.y = targetMax;
            verticalPagePanel.offsetMin = min; verticalPagePanel.offsetMax = max;
            UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(verticalPagePanel);
        }

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
        [SerializeField] private GameplayTextView quoteDocumentView;
        [SerializeField] private Text reason;
        [SerializeField] private Button confirm;
        [SerializeField] private Button cancel;
        [SerializeField] private Button viewCityStyles;
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
        [SerializeField] private GameplayTextView supplyDeckDocument;
        [SerializeField] private Text extensionHubsText;
        [SerializeField] private string supplyDeckFormat = "设施牌堆·{0}";
        [SerializeField] private string extensionHubFormat = "<color=#{0}>延伸枢纽</color>";
        [SerializeField] private string extensionHubSeparator = "·";
        [SerializeField] private UiResourceCostView ownedResources;
        [SerializeField] private Color[] availableHubColors;
        [SerializeField] private Color rainbowDeckCountColor;
        [SerializeField] private Color usedHubColor = Color.gray;
        [SerializeField] private GameObject buildHint;
        [SerializeField] private GameObject supplyInspectionHint;
        [SerializeField] private string inspectionQuote = "请选择设施牌查看费用。";
        private CardViewer viewer;

        public void RefreshSupplyStatus(GameState state, int playerId, PublicDeckStateView visibleDecks = null)
        {
            definitionState = state;
            if (state == null) return;
            visibleItems = new ItemZoneQuery().Project(state, GameStateViewer.Player(playerId));
            var deck = visibleDecks ?? GameStateViewProjector.ProjectForPlayer(state, playerId).Decks;
            RefreshSupplyStatusDisplay(deck, state.FindPlayer(playerId)?.Resources,
                state.Map.Facilities.Select(facility => facility.FacilityCardId));
        }

        // 效果中的供应选择只有玩家可见投影，不能沿用其他建设草稿的资源和牌堆显示。
        internal void RefreshVisibleSupplyStatus(GameStateView view, int playerId)
        {
            if (view == null || view.ViewerPlayerId != playerId) return;
            RefreshSupplyStatusDisplay(view.Decks, view.Players.FirstOrDefault(p => p.PlayerId == playerId)?.Resources,
                view.Map.Facilities.Select(facility => facility.FacilityCardId));
        }

        private void RefreshSupplyStatusDisplay(PublicDeckStateView deck, ResourceSet resources, IEnumerable<string> builtFacilities)
        {
            var counts = new[] { deck.FacilityDeckRedCount, deck.FacilityDeckYellowCount, deck.FacilityDeckBlueCount, deck.FacilityDeckRainbowCount };
            var marker = supplyDeckFormat.IndexOf("{0}", StringComparison.Ordinal);
            if (marker < 0 || supplyDeckDocument == null || availableHubColors == null || availableHubColors.Length < 3)
                throw new InvalidOperationException("设施牌堆数量需要已配置的混排文字、四色和数量占位符。");
            var runs = new List<GameplayTextRun> { new TextRun(supplyDeckFormat.Substring(0, marker)) };
            for (int i = 0; i < counts.Length; i++)
            {
                if (i != 0) runs.Add(new TextRun(extensionHubSeparator));
                var color = i < 3 ? availableHubColors[i] : rainbowDeckCountColor;
                runs.Add(new NumberRun(counts[i], new GameplayTextRunStyle("UiNumber", "Normal", Rgba(color))));
            }
            runs.Add(new TextRun(supplyDeckFormat.Substring(marker + 3)));
            var document = new GameplayTextDocument(runs);
            supplyDeckText.text = document.ToPlainText();
            supplyDeckDocument.Bind(document);
            ownedResources.SetCost(resources);
            var ids = new[] { FacilityCardDatabase.ExtensionHubRed, FacilityCardDatabase.ExtensionHubYellow,
                FacilityCardDatabase.ExtensionHubBlue };
            var labels = new List<string>();
            for (var i = 0; i < ids.Length; i++)
            {
                var used = false;
                foreach (var facilityId in builtFacilities)
                    if (facilityId == ids[i]) { used = true; break; }
                if (!used)
                    labels.Add(string.Format(extensionHubFormat, ColorUtility.ToHtmlStringRGB(availableHubColors[i])));
            }
            extensionHubsText.text = labels.Count == 0
                ? string.Format(extensionHubFormat, ColorUtility.ToHtmlStringRGB(usedHubColor))
                : string.Join(extensionHubSeparator, labels);
        }
        private static uint Rgba(Color color)
        {
            Color32 value = color;
            return ((uint)value.r << 24) | ((uint)value.g << 16) | ((uint)value.b << 8) | value.a;
        }

        private void RenderDeckCountFragment(Text text, GameplayTextRunStyle style)
        {
            var effect = text.GetComponent<RainbowCountTextEffect>();
            bool rainbow = style.SemanticRole == "UiNumber" && style.ColorRgba == Rgba(rainbowDeckCountColor);
            if (!rainbow) { if (effect != null) effect.enabled = false; return; }
            if (effect == null) effect = text.gameObject.AddComponent<RainbowCountTextEffect>();
            effect.enabled = true;
            effect.SetCountSpan(0, text.text.Length, availableHubColors[0], availableHubColors[1], availableHubColors[2], rainbowDeckCountColor);
        }
        private bool submittingPayment;
        public void Show(BuildFacilityDraftViewModel model, IReadOnlyList<FacilityPlacement> facilities, int playerId, CardVisualCatalog catalog,
            EffectDialogLayoutProfile copy, string selectedId, string summary, Func<bool> isCurrent,
            Action<string> selectCard, Action<int> selectSlot, Action<string> selectPayment,
            Action commit, Action dismiss, bool inspectionOnly = false)
        {
            quoteDocumentView.ClearBinding();
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
                card.SetSelectionEffect(click != null, click != null, selected);
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
            foreach (var slot in fixedSupplySlots) slot.gameObject.SetActive(model.IsSupplySource && string.IsNullOrEmpty(model.FixedFacilityId));
            if (!string.IsNullOrEmpty(model.FixedFacilityId))
            {
                var slot = Instantiate(supplySlotTemplate, candidates, false); slot.gameObject.SetActive(true);
                generated.Add(slot.gameObject);
                var card = Card(model.FixedFacilityId, model.Facility.FacilityId, slot, !placing, null);
                YC.PlayerJourney.PlayerAutomationId.Attach(card.Button.gameObject, "build.candidate." + model.FixedFacilityId);
            }
            else if (model.IsSupplySource)
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
                    var order = visibleItems?.Orders.FirstOrDefault(value => value.ZoneId == zone?.ZoneId);
                    var layers = order == null ? Array.Empty<ItemInstanceState>() : order.ItemIds
                        .Select(id => visibleItems.Items.FirstOrDefault(value => value.ItemId == id && value.ZoneId == zone.ZoneId)).Where(value => value != null).ToArray();
                    var item = layers.FirstOrDefault();
                    supplyItemIds.Add(item?.ItemId ?? string.Empty);
                    string id = item?.ItemId;
                    // 旧无实体夹具保留固定索引兼容；生产始终使用真实槽区。
                    if (visibleItems?.Zones.Count == 0 && index < model.Options.Count) id = model.Options[index].FacilityId;
                    if (string.IsNullOrEmpty(id)) continue;
                    var option = model.Options.FirstOrDefault(value => value.FacilityId == id);
                    var stackObject = new GameObject("FacilitySupplyLayers", typeof(RectTransform), typeof(FacilitySupplyStackLayout));
                    stackObject.transform.SetParent(supplySlot, false); generated.Add(stackObject);
                    var cards = new List<FacilityEffectCardView>();
                    if (layers.Length == 0)
                    {
                        cards.Add(Card(id, option?.DefinitionId, stackObject.transform, !placing && id == selectedId,
                            placing || option == null ? (Action)null : () => selectCard(id)));
                    }
                    else foreach (var layer in layers)
                    {
                        bool top = layer.ItemId == id;
                        cards.Add(Card(layer.ItemId, layer.DefinitionId, stackObject.transform, top && !placing && id == selectedId,
                            !top || placing || option == null ? (Action)null : () => selectCard(id)));
                    }
                    foreach (var card in cards.AsEnumerable().Reverse())
                    {
                        card.transform.SetAsLastSibling();
                        YC.PlayerJourney.PlayerAutomationId.Attach(card.Button.gameObject, "build.candidate." +
                            (layers.Length == 0 ? id : layers[cards.IndexOf(card)].ItemId));
                    }
                    var artwork = catalog.GetFacility(item?.DefinitionId ?? option?.DefinitionId ?? id);
                    stackObject.GetComponent<FacilitySupplyStackLayout>().Configure(cards, artwork == null ? 0f : artwork.rect.width, supplySlotTemplate.rect.width);
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
                row.gameObject.SetActive(true);
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
            if (viewCityStyles == null) throw new InvalidOperationException("设施页面缺少城市样式查看按钮。");
            viewCityStyles.onClick.RemoveAllListeners();
            viewCityStyles.onClick.AddListener(() => { if (Current()) GameplayHudFrame.Active?.InspectCityStylesRequested?.Invoke(this); });
            YC.PlayerJourney.PlayerAutomationId.Attach(confirm.gameObject, "build.confirm");
        }

        public void Hide()
        {
            if (this == null) return;
            if (viewer != null) viewer.Dismiss();
            var frame = GameplayHudFrame.Active;
            if (frame != null) frame.ReleasePage(gameObject);
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
