using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>编辑器资产化的城市样式预览固定引用；只管理静态壳和允许的动态模板。</summary>
    public sealed class CityStyleDeclarationPreviewView : MonoBehaviour
    {
        internal readonly struct BoardSlot
        {
            public BoardSlot(
                RectTransform root,
                Image image,
                Button button,
                Outline outline,
                CityStyleDeclarationSlotPointerHandler pointerHandler,
                RawImage facilityImage,
                GameObject usedBadge)
            {
                Root = root;
                Image = image;
                Button = button;
                Outline = outline;
                PointerHandler = pointerHandler;
                FacilityImage = facilityImage;
                UsedBadge = usedBadge;
            }

            public RectTransform Root { get; }
            public Image Image { get; }
            public Button Button { get; }
            public Outline Outline { get; }
            public CityStyleDeclarationSlotPointerHandler PointerHandler { get; }
            public RawImage FacilityImage { get; }
            public GameObject UsedBadge { get; }

            public bool IsValid => Root != null && Image != null && Button != null && Outline != null &&
                                   PointerHandler != null && FacilityImage != null && UsedBadge != null;
        }

        [SerializeField] private Canvas overlayCanvas;
        [SerializeField] private RectTransform rootRect;
        [SerializeField] private GameObject overlayObject;
        [SerializeField] private Image overlayImage;
        [SerializeField] private RectTransform panel;
        [SerializeField] private CityStyleDeclarationPreviewInputHandler inputHandler;
        [SerializeField] private Button closeButton;
        [SerializeField] private Text closeButtonLabel;
        [SerializeField] private CardBoardVisualLayout cardBoardVisualLayout;
        [SerializeField] private CardInteractionLayoutProfile cardInteractionLayoutProfile;
        [SerializeField] private RawImage cityStyleCardImage;
        [SerializeField] private RectTransform cityStyleInfluenceMarkerRoot;
        [SerializeField] private Text cityStyleCardPlaceholder;
        [SerializeField] private Text cityStyleTitleText;
        [SerializeField] private Text matchStatusText;
        [SerializeField] private Text specialActionHintText;
        [SerializeField] private Text boardTitleText;
        [SerializeField] private RectTransform cityBoardRect;
        [SerializeField] private RawImage cityBoardImage;
        [SerializeField] private Outline boardOutline;
        [SerializeField] private CityStyleDeclarationBoardPointerHandler boardPointerHandler;
        [SerializeField] private Button confirmDeclarationButton;
        [SerializeField] private Text confirmDeclarationButtonLabel;

        [Header("固定建设板槽位")]
        [SerializeField] private RectTransform[] cityBoardSlotRoots = Array.Empty<RectTransform>();
        [SerializeField] private Image[] cityBoardSlotImages = Array.Empty<Image>();
        [SerializeField] private Button[] cityBoardSlotButtons = Array.Empty<Button>();
        [SerializeField] private Outline[] cityBoardSlotOutlines = Array.Empty<Outline>();
        [SerializeField] private CityStyleDeclarationSlotPointerHandler[] cityBoardSlotPointerHandlers =
            Array.Empty<CityStyleDeclarationSlotPointerHandler>();
        [SerializeField] private RawImage[] cityBoardSlotFacilityImages = Array.Empty<RawImage>();
        [SerializeField] private GameObject[] cityBoardSlotUsedBadges = Array.Empty<GameObject>();

        [Header("固定警告二级弹窗")]
        [SerializeField] private GameObject specialActionWarningObject;
        [SerializeField] private Text specialActionWarningMessage;
        [SerializeField] private Button cancelSpecialActionWarningButton;
        [SerializeField] private Button confirmSpecialActionWarningButton;

        [Header("禁用动态模板")]
        [SerializeField] private RectTransform influenceMarkerTemplate;
        [SerializeField] private RectTransform specialActionDropTargetTemplate;

        [Header("样式列表与详情")]
        [SerializeField] private RectTransform optionsContent;
        [SerializeField] private BuildInfoItemView optionTemplate;
        [SerializeField] private GameObject listPage;
        [SerializeField] private GameObject detailPage;
        [SerializeField] private CityStyleCardGesture detailGesture;
        [SerializeField] private string viewStatus = "仅查看 · 宣告请从城市样式页的宣告入口开始";
        [SerializeField] private string emptyStatus = "当前没有可查看的城市样式";
        [SerializeField] private string selectedCountFormat = "已选 {0} / {1} 个设施";
        [SerializeField] private string validStatusFormat = "组合符合条件 · 方向 {0}°";
        [SerializeField] private string selectionUnavailable = "当前无法校验宣告条件";
        [SerializeField] private string specialActionReady = "拖动本方标记到高亮状态槽，单独发动特殊行动";
        [SerializeField] private string specialActionDrop = "在高亮区域松开以发动特殊行动";
        [SerializeField] private string specialActionCancelled = "已取消特殊行动，未提交命令";
        [SerializeField] private string warningSuffix = "\n仍要消耗主要行动与本次样式行动次数吗？";
        [Header("模式与摘要配置")]
        [SerializeField] private Text windowTitle, footerStatus;
        [SerializeField] private string viewTitle, declareTitle;
        [SerializeField] private string closeDetailsLabel, confirmLabel, footerView, footerDeclareFormat;
        [SerializeField] private string unavailableFormat, unmetCondition, specialUnavailableFormat;
        [SerializeField] private Color summaryColor = new Color(.25f, .23f, .18f);
        public Color SummaryColor => summaryColor;
        public string UnavailableFormat => unavailableFormat;
        public string UnmetCondition => unmetCondition;
        public string SpecialUnavailableFormat => specialUnavailableFormat;
        public void RenderMode(Workflows.CityStyleOptionsViewModel model, int selectedCount = 0)
        {
            windowTitle.text = model.DeclareMode ? declareTitle : viewTitle;
            footerStatus.text = model.DeclareMode ? string.Format(footerDeclareFormat, selectedCount, model.AvailableMarkers) : footerView;
            confirmDeclarationButtonLabel.text = model.DeclareMode ? confirmLabel : closeDetailsLabel;
        }
        public string ViewStatus => viewStatus;
        public string EmptyStatus => emptyStatus;
        public string SelectedCountFormat => selectedCountFormat;
        public string ValidStatusFormat => validStatusFormat;
        public string SelectionUnavailable => selectionUnavailable;
        public string SpecialActionReady => specialActionReady;
        public string SpecialActionDrop => specialActionDrop;
        public string SpecialActionCancelled => specialActionCancelled;
        public string WarningSuffix => warningSuffix;
        public bool IsDetail => detailPage != null && detailPage.activeSelf;
        private readonly List<BuildInfoItemView> options = new List<BuildInfoItemView>();
        public void ShowDetail(bool detail)
        {
            detailPage.SetActive(detail);
            listPage.SetActive(!detail);
        }
        public void RenderOptions(Workflows.CityStyleOptionsViewModel model, CardVisualCatalog catalog, Action<int> choose)
        {
            var reusable = new List<BuildInfoItemView>(options);
            options.Clear();
            for (var i = 0; i < model.Options.Count; i++)
            {
                var option = model.Options[i];
                if (option == null) continue;
                var item = reusable.Find(value => value != null && value.name == option.CityStyleId);
                if (item != null) reusable.Remove(item);
                else item = Instantiate(optionTemplate, optionsContent, false);
                var overlay = item.GetComponentInChildren<CityStyleTrackOverlay>(true);
                overlay.ClearOptionMarkers();
                item.transform.SetSiblingIndex(i);
                item.gameObject.SetActive(true);
                item.name = option.CityStyleId;
                item.RawImage.texture = catalog.GetCityStyle(option.CityStyleId);
                item.RawImage.gameObject.SetActive(item.RawImage.texture != null);
                item.FallbackText.text = option.Name;
                item.FallbackText.gameObject.SetActive(item.RawImage.texture == null);
                int index = i;
                item.Button.onClick.RemoveAllListeners();
                item.PointerInteraction.ConfigureClick(item.Button, () => choose(index), null);
                item.GetComponent<CityStyleCardStateView>().Bind(option.CanDeclare);
                item.GetComponent<CityStyleCardGesture>().Configure(() => { choose(index); ShowDetail(true); });
                var scroll = optionsContent.GetComponentInParent<ScrollRect>();
                item.PointerInteraction.ConfigureDrag(() => scroll != null, e => scroll.OnBeginDrag(e),
                    e => scroll.OnDrag(e), e => scroll.OnEndDrag(e), () => scroll?.StopMovement());
                var tracker = new CityStyleMarkerLayoutTracker(cardBoardVisualLayout);
                foreach (var marker in model.CityStyleMarkers)
                    if (marker.CityStyleId == option.CityStyleId) tracker.Register(marker.CityStyleId, marker.MarkerArea, marker.PlayerId);
                foreach (var marker in model.CityStyleMarkers)
                {
                    if (marker.CityStyleId != option.CityStyleId) continue;
                    var placement = tracker.Next(marker.CityStyleId, marker.MarkerArea, marker.PlayerId);
                    if (placement.PlayerMarkerIndex != 0) continue;
                    var block = Instantiate(influenceMarkerTemplate, item.MarkerRoot, false).GetComponent<Image>();
                    overlay.RegisterOptionMarker(block.gameObject);
                    CityStyleMarkerRenderer.Configure(cardBoardVisualLayout, block, "Player Marker Count",
                        cardBoardVisualLayout.CityStyleVisuals.PlayerColor((int)marker.PlayerColor, Color.white), null,
                        cardBoardVisualLayout.BuildInfoMarkerSize, marker.CityStyleId, placement);
                    block.GetComponent<Button>().interactable = false;
                    block.raycastTarget = false;
                }
                overlay.Render(option.CityStyleId, cardBoardVisualLayout.CityStyleVisuals, model.CityStyleMarkers);
                options.Add(item);
            }
            // 原生网格按列排列；先放Ⅰ级，再放Ⅱ级，正式候选索引与点击回调保持对应。
            var siblingIndex = 0;
            foreach (var item in options)
                if (cardBoardVisualLayout.CityStyleVisuals.Find(item.name)?.trackLayout != "limited")
                    item.transform.SetSiblingIndex(siblingIndex++);
            foreach (var item in options)
                if (cardBoardVisualLayout.CityStyleVisuals.Find(item.name)?.trackLayout == "limited")
                    item.transform.SetSiblingIndex(siblingIndex++);
            foreach (var item in reusable)
            {
                if (item == null) continue;
                item.Button.onClick.RemoveAllListeners();
                item.PointerInteraction.ConfigureClick(null, null, null);
                item.gameObject.SetActive(false);
                if (UnityEngine.Application.isPlaying) Destroy(item.gameObject); else DestroyImmediate(item.gameObject);
            }
        }
        public void SelectOption(string id)
        {
            foreach (var item in options)
                if (item != null)
                {
                    item.Outline.enabled = false;
                    item.GetComponent<CityStyleCardStateView>().SetSelected(item.name == id);
                }
        }

        private readonly List<GameObject> dynamicInstances = new List<GameObject>();

        public Canvas OverlayCanvas => overlayCanvas;
        public RectTransform RootRect => rootRect;
        public GameObject OverlayObject => overlayObject;
        public Image OverlayImage => overlayImage;
        public RectTransform Panel => panel;
        internal CityStyleDeclarationPreviewInputHandler InputHandler => inputHandler;
        public Button CloseButton => closeButton;
        public Text CloseButtonLabel => closeButtonLabel;
        public CardBoardVisualLayout CardBoardVisualLayout => cardBoardVisualLayout;
        public CardInteractionLayoutProfile CardInteractionLayoutProfile => cardInteractionLayoutProfile;
        public RawImage CityStyleCardImage => cityStyleCardImage;
        public RectTransform CityStyleInfluenceMarkerRoot => cityStyleInfluenceMarkerRoot;
        public Text CityStyleCardPlaceholder => cityStyleCardPlaceholder;
        public Text CityStyleTitleText => cityStyleTitleText;
        public Text MatchStatusText => matchStatusText;
        public Text SpecialActionHintText => specialActionHintText;
        public Text BoardTitleText => boardTitleText;
        public RectTransform CityBoardRect => cityBoardRect;
        public RawImage CityBoardImage => cityBoardImage;
        public Outline BoardOutline => boardOutline;
        internal CityStyleDeclarationBoardPointerHandler BoardPointerHandler => boardPointerHandler;
        public Button ConfirmDeclarationButton => confirmDeclarationButton;
        public Text ConfirmDeclarationButtonLabel => confirmDeclarationButtonLabel;
        public GameObject SpecialActionWarningObject => specialActionWarningObject;
        public Text SpecialActionWarningMessage => specialActionWarningMessage;
        public Button CancelSpecialActionWarningButton => cancelSpecialActionWarningButton;
        public Button ConfirmSpecialActionWarningButton => confirmSpecialActionWarningButton;
        public int CityBoardSlotCount => cityBoardSlotRoots == null ? 0 : cityBoardSlotRoots.Length;
        public static int RequiredCityBoardSlotCount => CityBoardSlotLayout.SlotCount;

        public void ApplyCityBoardSlotLayout(RectTransform rect, int slotIndex)
        {
            CityBoardSlotLayout.Apply(rect, cardBoardVisualLayout, slotIndex);
        }

        public bool TryValidateConfiguration(out string reason)
        {
            var missingReferences = new List<string>();
            if (optionsContent == null) missingReferences.Add(nameof(optionsContent));
            if (optionTemplate == null) missingReferences.Add(nameof(optionTemplate));
            else if (!optionTemplate.transform.IsChildOf(transform)) missingReferences.Add("optionTemplate（必须引用本窗口内的禁用模板）");
            else if (optionTemplate.GetComponent<CityStyleCardGesture>() == null) missingReferences.Add("optionTemplate.CityStyleCardGesture");
            if (listPage == null) missingReferences.Add(nameof(listPage));
            if (detailPage == null) missingReferences.Add(nameof(detailPage));
            if (detailGesture == null) missingReferences.Add(nameof(detailGesture));
            if (missingReferences.Count > 0)
            { reason = "城市样式列表与详情引用不完整：" + string.Join("、", missingReferences) + "。"; return false; }

            if (overlayCanvas == null || rootRect == null || overlayObject == null || overlayImage == null ||
                panel == null || inputHandler == null || closeButton == null || closeButtonLabel == null ||
                cardBoardVisualLayout == null || cardInteractionLayoutProfile == null ||
                cityStyleCardImage == null || cityStyleInfluenceMarkerRoot == null ||
                cityStyleCardPlaceholder == null || cityStyleTitleText == null || matchStatusText == null ||
                specialActionHintText == null || boardTitleText == null || cityBoardRect == null ||
                cityBoardImage == null || boardOutline == null || boardPointerHandler == null ||
                confirmDeclarationButton == null ||
                confirmDeclarationButtonLabel == null)
            {
                reason = "城市样式预览固定壳引用不完整。";
                return false;
            }

            if (!cardBoardVisualLayout.TryValidateConfiguration(out reason))
            {
                reason = "城市样式预览 CardBoardVisualLayout 无效：" + reason;
                return false;
            }

            if (!cardInteractionLayoutProfile.TryValidateConfiguration(out reason))
            {
                reason = "城市样式预览 CardInteractionLayoutProfile 无效：" + reason;
                return false;
            }

            if (CityBoardSlotCount != CityBoardSlotLayout.SlotCount ||
                cityBoardSlotImages == null || cityBoardSlotImages.Length != CityBoardSlotLayout.SlotCount ||
                cityBoardSlotButtons == null || cityBoardSlotButtons.Length != CityBoardSlotLayout.SlotCount ||
                cityBoardSlotOutlines == null || cityBoardSlotOutlines.Length != CityBoardSlotLayout.SlotCount ||
                cityBoardSlotPointerHandlers == null ||
                cityBoardSlotPointerHandlers.Length != CityBoardSlotLayout.SlotCount ||
                cityBoardSlotFacilityImages == null ||
                cityBoardSlotFacilityImages.Length != CityBoardSlotLayout.SlotCount ||
                cityBoardSlotUsedBadges == null ||
                cityBoardSlotUsedBadges.Length != CityBoardSlotLayout.SlotCount)
            {
                reason = "城市样式预览必须序列化与领域布局一致的固定槽位。";
                return false;
            }

            for (var slotIndex = 0; slotIndex < CityBoardSlotLayout.SlotCount; slotIndex++)
            {
                if (!GetCityBoardSlot(slotIndex).IsValid)
                {
                    reason = "城市样式预览槽位引用不完整：" + slotIndex;
                    return false;
                }
            }

            if (specialActionWarningObject == null || specialActionWarningMessage == null ||
                cancelSpecialActionWarningButton == null || confirmSpecialActionWarningButton == null)
            {
                reason = "城市样式预览警告二级弹窗引用不完整。";
                return false;
            }

            if (influenceMarkerTemplate == null ||
                influenceMarkerTemplate.GetComponent<Image>() == null ||
                influenceMarkerTemplate.GetComponent<Button>() == null ||
                influenceMarkerTemplate.GetComponent<CardPointerInteraction>() == null ||
                specialActionDropTargetTemplate == null ||
                specialActionDropTargetTemplate.GetComponent<Image>() == null ||
                specialActionDropTargetTemplate.GetComponent<Outline>() == null ||
                specialActionDropTargetTemplate.GetComponent<CityStyleSpecialActionDropTarget>() == null)
            {
                reason = "城市样式预览动态模板引用不完整。";
                return false;
            }

            if (influenceMarkerTemplate.gameObject.activeSelf ||
                specialActionDropTargetTemplate.gameObject.activeSelf ||
                specialActionWarningObject.activeSelf)
            {
                reason = "城市样式预览动态模板和警告壳必须默认禁用。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        internal BoardSlot GetCityBoardSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= CityBoardSlotCount)
            {
                return default;
            }

            return new BoardSlot(
                cityBoardSlotRoots[slotIndex],
                cityBoardSlotImages[slotIndex],
                cityBoardSlotButtons[slotIndex],
                cityBoardSlotOutlines[slotIndex],
                cityBoardSlotPointerHandlers[slotIndex],
                cityBoardSlotFacilityImages[slotIndex],
                cityBoardSlotUsedBadges[slotIndex]);
        }

        public Image CreateInfluenceMarker()
        {
            var root = CloneTemplate(influenceMarkerTemplate, cityStyleInfluenceMarkerRoot);
            return root == null ? null : root.GetComponent<Image>();
        }

        internal CityStyleSpecialActionDropTarget CreateSpecialActionDropTarget()
        {
            var root = CloneTemplate(specialActionDropTargetTemplate, cityStyleInfluenceMarkerRoot);
            return root == null ? null : root.GetComponent<CityStyleSpecialActionDropTarget>();
        }

        public void PrepareForUse()
        {
            ClearForReuse();
            detailGesture.Configure(null, () => ShowDetail(false));
            gameObject.name = "City Style Declaration Preview Canvas";
            overlayCanvas.overrideSorting = true;
            overlayCanvas.sortingOrder = GameplayUiLayers.Page;
            overlayObject.SetActive(true);
        }

        public void ClearForReuse()
        {
            ClearCallbacks();
            ShowDetail(false);
            DestroyDynamicInstances();
            cityStyleCardImage.texture = null;
            cityStyleCardImage.gameObject.SetActive(false);
            cityStyleCardPlaceholder.gameObject.SetActive(true);
            cityStyleTitleText.text = string.Empty;
            matchStatusText.text = string.Empty;
            specialActionHintText.text = string.Empty;
            specialActionWarningMessage.text = string.Empty;
            specialActionWarningObject.SetActive(false);

            for (var slotIndex = 0; slotIndex < CityBoardSlotCount; slotIndex++)
            {
                var slot = GetCityBoardSlot(slotIndex);
                slot.FacilityImage.texture = null;
                slot.FacilityImage.gameObject.SetActive(false);
                slot.UsedBadge.SetActive(false);
            }
        }

        public void ClearCallbacks()
        {
            detailGesture.Configure(null);
            closeButton.onClick.RemoveAllListeners();
            confirmDeclarationButton.onClick.RemoveAllListeners();
            cancelSpecialActionWarningButton.onClick.RemoveAllListeners();
            confirmSpecialActionWarningButton.onClick.RemoveAllListeners();
            inputHandler.Configure(null, null);
            boardPointerHandler.Configure(null, null, null);

            for (var slotIndex = 0; slotIndex < CityBoardSlotCount; slotIndex++)
            {
                var slot = GetCityBoardSlot(slotIndex);
                slot.Button.onClick.RemoveAllListeners();
                slot.PointerHandler.Configure(slotIndex, null, null, null, null, null, null);
            }

            for (var i = 0; i < dynamicInstances.Count; i++)
            {
                var instance = dynamicInstances[i];
                if (instance == null)
                {
                    continue;
                }

                var buttons = instance.GetComponentsInChildren<Button>(true);
                for (var buttonIndex = 0; buttonIndex < buttons.Length; buttonIndex++)
                {
                    buttons[buttonIndex].onClick.RemoveAllListeners();
                }

                var interactions = instance.GetComponentsInChildren<CardPointerInteraction>(true);
                for (var interactionIndex = 0; interactionIndex < interactions.Length; interactionIndex++)
                {
                    interactions[interactionIndex].ConfigureClick(null, null, null);
                    interactions[interactionIndex].ConfigureDrag(null, null, null, null, null);
                }

                var dropTargets = instance.GetComponentsInChildren<CityStyleSpecialActionDropTarget>(true);
                for (var targetIndex = 0; targetIndex < dropTargets.Length; targetIndex++)
                {
                    dropTargets[targetIndex].Configure(string.Empty);
                }
            }
        }

        public void DestroyDynamicInstances()
        {
            for (var i = dynamicInstances.Count - 1; i >= 0; i--)
            {
                if (dynamicInstances[i] == null)
                {
                    continue;
                }

                if (UnityEngine.Application.isPlaying)
                {
                    Destroy(dynamicInstances[i]);
                }
                else
                {
                    DestroyImmediate(dynamicInstances[i]);
                }
            }

            dynamicInstances.Clear();
        }

        private RectTransform CloneTemplate(RectTransform template, RectTransform parent)
        {
            if (template == null || parent == null)
            {
                return null;
            }

            var clone = Instantiate(template, parent, false);
            clone.gameObject.SetActive(true);
            dynamicInstances.Add(clone.gameObject);
            return clone;
        }
    }
}
