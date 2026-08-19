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
        [SerializeField] private Button previousButton;
        [SerializeField] private Text previousButtonLabel;
        [SerializeField] private Button nextButton;
        [SerializeField] private Text nextButtonLabel;
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
        public Button PreviousButton => previousButton;
        public Text PreviousButtonLabel => previousButtonLabel;
        public Button NextButton => nextButton;
        public Text NextButtonLabel => nextButtonLabel;
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
            if (overlayCanvas == null || rootRect == null || overlayObject == null || overlayImage == null ||
                panel == null || inputHandler == null || closeButton == null || closeButtonLabel == null ||
                cardBoardVisualLayout == null || cardInteractionLayoutProfile == null ||
                cityStyleCardImage == null || cityStyleInfluenceMarkerRoot == null ||
                cityStyleCardPlaceholder == null || cityStyleTitleText == null || matchStatusText == null ||
                specialActionHintText == null || boardTitleText == null || cityBoardRect == null ||
                cityBoardImage == null || boardOutline == null || boardPointerHandler == null ||
                previousButton == null || previousButtonLabel == null || nextButton == null ||
                nextButtonLabel == null || confirmDeclarationButton == null ||
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
            gameObject.name = "City Style Declaration Preview Canvas";
            overlayCanvas.overrideSorting = true;
            overlayCanvas.sortingOrder = 130;
            overlayObject.SetActive(true);
        }

        public void ClearForReuse()
        {
            ClearCallbacks();
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
            closeButton.onClick.RemoveAllListeners();
            previousButton.onClick.RemoveAllListeners();
            nextButton.onClick.RemoveAllListeners();
            confirmDeclarationButton.onClick.RemoveAllListeners();
            cancelSpecialActionWarningButton.onClick.RemoveAllListeners();
            confirmSpecialActionWarningButton.onClick.RemoveAllListeners();
            inputHandler.Configure(null, null, null);
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
