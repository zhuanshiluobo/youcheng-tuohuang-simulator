using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public enum EventChoiceDialogMode
    {
        EventCard,
        ExplorePath,
        ExplorePayment,
        ResourceCollectionPayment,
        BuildFacilityFocus,
        BuildFacilityConfirmation,
        CharacterSecondEffectDecision
    }

    /// <summary>编辑器资产化的事件选择窗口固定引用，只负责视图切换和模板实例化。</summary>
    public sealed class EventChoiceDialogView : MonoBehaviour
    {
        [Serializable]
        public sealed class ButtonRow
        {
            [SerializeField] private RectTransform root;
            [SerializeField] private Button button;
            [SerializeField] private Text label;

            public RectTransform Root => root;
            public Button Button => button;
            public Text Label => label;

            public bool IsValid => root != null && button != null && label != null;

            public ButtonRow()
            {
            }

            internal ButtonRow(RectTransform configuredRoot, Button configuredButton, Text configuredLabel)
            {
                root = configuredRoot;
                button = configuredButton;
                label = configuredLabel;
            }
        }

        [Serializable]
        public sealed class PaymentRouteRow
        {
            [SerializeField] private RectTransform root;
            [SerializeField] private Text label;
            [SerializeField] private RectTransform recipientHost;

            public RectTransform Root => root;
            public Text Label => label;
            public RectTransform RecipientHost => recipientHost;

            public bool IsValid => root != null && label != null && recipientHost != null;

            public PaymentRouteRow()
            {
            }

            internal PaymentRouteRow(RectTransform configuredRoot, Text configuredLabel, RectTransform host)
            {
                root = configuredRoot;
                label = configuredLabel;
                recipientHost = host;
            }
        }

        [SerializeField] private Canvas overlayCanvas;
        [SerializeField] private RectTransform overlayRect;
        [SerializeField] private Image overlayImage;
        [SerializeField] private RectTransform panel;
        [SerializeField] private RectTransform expandedContent;
        [SerializeField] private Text titleText;
        [SerializeField] private Text metadataText;
        [SerializeField] private Text descriptionText;
        [SerializeField] private RectTransform actionArea;
        [SerializeField] private Button closeButton;
        [SerializeField] private Text closeButtonLabel;
        [SerializeField] private Text resourcePaymentCloseLabel;
        [SerializeField] private WindowCloseInputHandler closeInputHandler;
        [SerializeField] private EventChoiceDialogLayoutProfile layoutProfile;
        [SerializeField] private UiWindowSizeInput boundedLayout;
        [SerializeField] private UiEventHeaderInput pageLayout;
        [SerializeField] private GameObject artworkLayoutRoot;
        [SerializeField] private RectTransform artworkChoiceHost;
        [SerializeField] private GameObject eventTextScroll;
        [SerializeField] private string[] modeTitles =
        {
            "", "选择探索路线", "选择过路费接收者", "是否支付路费", "", "最终确认建设", "是否发动第二个效果？"
        };

        [Header("互斥模式块")]
        [SerializeField] private GameObject eventCardMode;
        [SerializeField] private GameObject explorePathMode;
        [SerializeField] private GameObject explorePaymentMode;
        [SerializeField] private GameObject resourceCollectionPaymentMode;
        [SerializeField] private GameObject buildFacilityFocusMode;
        [SerializeField] private GameObject buildFacilityConfirmationMode;
        [SerializeField] private GameObject characterSecondEffectDecisionMode;

        [Header("模式内容")]
        [SerializeField] private RawImage eventCardArtworkImage;
        [SerializeField] private GameObject eventCardMetadataRibbon;
        [SerializeField] private Image eventCardMetadataRibbonImage;
        [SerializeField] private Text eventCardMetadataRibbonLabel;
        [SerializeField] private RectTransform eventChoiceHost;
        [SerializeField] private RectTransform eventPaymentRouteHost;
        [SerializeField] private RectTransform explorePathHost;
        [SerializeField] private RectTransform explorePaymentRouteHost;
        [SerializeField] private Button exploreConfirmButton;
        [SerializeField] private Text exploreConfirmLabel;
        [SerializeField] private Text resourcePaymentReceiverText;
        [SerializeField] private RectTransform resourcePaymentRecipientHost;
        [SerializeField] private Button resourcePaymentBankButton;
        [SerializeField] private Text resourcePaymentBankLabel;
        [SerializeField] private Button resourcePaymentCancelButton;
        [SerializeField] private string resourcePaymentBankPrompt = "支付给银行";
        [SerializeField] private string resourcePaymentPlayerPrompt = "选择路费接收玩家";
        [SerializeField] private string resourcePaymentSelectedFormat = "已选择支付给{0} {1}金券";
        [SerializeField] private string resourcePaymentBankName = "银行";
        [SerializeField] private string resourcePaymentBankFormat = "支付 {0}";
        [SerializeField] private string resourcePaymentPlayerFormat = "向 {0} 支付 {1}";
        [SerializeField] private RawImage facilityPreviewImage;
        [SerializeField] private AspectRatioFitter facilityPreviewAspect;
        [SerializeField] private Text facilityPreviewFallback;
        [SerializeField] private Text buildFocusDetailsText;
        [SerializeField] private Button buildResourceButton;
        [SerializeField] private Text buildResourceLabel;
        [SerializeField] private Text buildResourceReasonText;
        [SerializeField] private Button buildGoldButton;
        [SerializeField] private Text buildGoldLabel;
        [SerializeField] private Text buildGoldReasonText;
        [SerializeField] private Text buildFocusErrorText;
        [SerializeField] private Text buildConfirmationSummaryText;
        [SerializeField] private Text buildConfirmationErrorText;
        [SerializeField] private Button buildBackButton;
        [SerializeField] private Text buildBackLabel;
        [SerializeField] private Button buildConfirmButton;
        [SerializeField] private Text buildConfirmLabel;
        [SerializeField] private Button characterContinueButton;
        [SerializeField] private Text characterContinueLabel;
        [SerializeField] private Button characterFinishButton;
        [SerializeField] private Text characterFinishLabel;

        [Header("禁用动态模板")]
        [SerializeField] private ButtonRow choiceRowTemplate = new ButtonRow();
        [SerializeField] private ButtonRow artworkChoiceRowTemplate = new ButtonRow();
        [SerializeField] private ButtonRow pathRowTemplate = new ButtonRow();
        [SerializeField] private PaymentRouteRow paymentRouteRowTemplate = new PaymentRouteRow();
        [SerializeField] private ButtonRow paymentRecipientButtonTemplate = new ButtonRow();
        [SerializeField] private ButtonRow resourceCollectionRecipientButtonTemplate = new ButtonRow();

        private readonly List<GameObject> dynamicInstances = new List<GameObject>();

        public Canvas OverlayCanvas => overlayCanvas;
        public RectTransform OverlayRect => overlayRect;
        public Image OverlayImage => overlayImage;
        public RectTransform Panel => panel;
        public RectTransform ExpandedContent => expandedContent;
        public Text TitleText => titleText;
        public Text MetadataText => metadataText;
        public Text DescriptionText => descriptionText;
        public RectTransform ActionArea => actionArea;
        public Button CloseButton => closeButton;
        public Text CloseButtonLabel => resourcePaymentCloseLabel != null && resourcePaymentCloseLabel.gameObject.activeSelf
            ? resourcePaymentCloseLabel : closeButtonLabel;
        public WindowCloseInputHandler CloseInputHandler => closeInputHandler;
        public EventChoiceDialogLayoutProfile LayoutProfile => layoutProfile;
        public RectTransform ArtworkChoiceHost => artworkChoiceHost;
        public GameObject[] ModeBlocks => GetModeBlocks();
        public RawImage EventCardArtworkImage => eventCardArtworkImage;
        public GameObject EventCardMetadataRibbon => eventCardMetadataRibbon;
        public Image EventCardMetadataRibbonImage => eventCardMetadataRibbonImage;
        public Text EventCardMetadataRibbonLabel => eventCardMetadataRibbonLabel;
        public RectTransform EventChoiceHost => eventChoiceHost;
        public RectTransform EventPaymentRouteHost => eventPaymentRouteHost;
        public RectTransform ExplorePathHost => explorePathHost;
        public RectTransform ExplorePaymentRouteHost => explorePaymentRouteHost;
        public Button ExploreConfirmButton => exploreConfirmButton;
        public Text ExploreConfirmLabel => exploreConfirmLabel;
        public Text ResourcePaymentReceiverText => resourcePaymentReceiverText;
        public RectTransform ResourcePaymentRecipientHost => resourcePaymentRecipientHost;
        public Button ResourcePaymentBankButton => resourcePaymentBankButton;
        public Text ResourcePaymentBankLabel => resourcePaymentBankLabel;
        public Button ResourcePaymentCancelButton => resourcePaymentCancelButton;
        public string ResourcePaymentBankPrompt => resourcePaymentBankPrompt;
        public string ResourcePaymentPlayerPrompt => resourcePaymentPlayerPrompt;
        public string ResourcePaymentSelectedFormat => resourcePaymentSelectedFormat;
        public string ResourcePaymentBankName => resourcePaymentBankName;
        public string ResourcePaymentBankFormat => resourcePaymentBankFormat;
        public string ResourcePaymentPlayerFormat => resourcePaymentPlayerFormat;
        public RawImage FacilityPreviewImage => facilityPreviewImage;
        public AspectRatioFitter FacilityPreviewAspect => facilityPreviewAspect;
        public Text FacilityPreviewFallback => facilityPreviewFallback;
        public Text BuildFocusDetailsText => buildFocusDetailsText;
        public Button BuildResourceButton => buildResourceButton;
        public Text BuildResourceLabel => buildResourceLabel;
        public Text BuildResourceReasonText => buildResourceReasonText;
        public Button BuildGoldButton => buildGoldButton;
        public Text BuildGoldLabel => buildGoldLabel;
        public Text BuildGoldReasonText => buildGoldReasonText;
        public Text BuildFocusErrorText => buildFocusErrorText;
        public Text BuildConfirmationSummaryText => buildConfirmationSummaryText;
        public Text BuildConfirmationErrorText => buildConfirmationErrorText;
        public Button BuildBackButton => buildBackButton;
        public Text BuildBackLabel => buildBackLabel;
        public Button BuildConfirmButton => buildConfirmButton;
        public Text BuildConfirmLabel => buildConfirmLabel;
        public Button CharacterContinueButton => characterContinueButton;
        public Text CharacterContinueLabel => characterContinueLabel;
        public Button CharacterFinishButton => characterFinishButton;
        public Text CharacterFinishLabel => characterFinishLabel;

        public bool TryValidateConfiguration(out string reason)
        {
            if (overlayCanvas == null || overlayRect == null || overlayImage == null || panel == null ||
                expandedContent == null || titleText == null || metadataText == null || descriptionText == null ||
                actionArea == null || closeButton == null ||
                closeButtonLabel == null || resourcePaymentCloseLabel == null ||
                closeInputHandler == null || layoutProfile == null)
            {
                reason = "事件选择窗口固定壳引用不完整。";
                return false;
            }

            if (!layoutProfile.TryValidateConfiguration(out reason))
            {
                return false;
            }

            var modes = GetModeBlocks();
            for (var i = 0; i < modes.Length; i++)
            {
                if (modes[i] == null)
                {
                    reason = "事件选择窗口存在缺失的模式块。";
                    return false;
                }
            }

            if (eventCardArtworkImage == null || eventCardMetadataRibbon == null ||
                eventCardMetadataRibbonImage == null || eventCardMetadataRibbonLabel == null ||
                eventChoiceHost == null ||
                eventPaymentRouteHost == null || explorePathHost == null ||
                explorePaymentRouteHost == null || exploreConfirmButton == null || exploreConfirmLabel == null ||
                resourcePaymentReceiverText == null || resourcePaymentRecipientHost == null ||
                resourcePaymentBankButton == null || resourcePaymentBankLabel == null ||
                resourcePaymentCancelButton == null ||
                facilityPreviewImage == null || facilityPreviewAspect == null || facilityPreviewFallback == null ||
                buildFocusDetailsText == null || buildResourceButton == null || buildResourceLabel == null ||
                buildResourceReasonText == null || buildGoldButton == null || buildGoldLabel == null ||
                buildGoldReasonText == null || buildFocusErrorText == null ||
                buildConfirmationSummaryText == null || buildConfirmationErrorText == null ||
                buildBackButton == null || buildBackLabel == null || buildConfirmButton == null ||
                buildConfirmLabel == null ||
                characterContinueButton == null || characterContinueLabel == null ||
                characterFinishButton == null || characterFinishLabel == null)
            {
                reason = "事件选择窗口模式内容引用不完整。";
                return false;
            }

            if (choiceRowTemplate == null || !choiceRowTemplate.IsValid ||
                artworkChoiceRowTemplate == null || !artworkChoiceRowTemplate.IsValid ||
                pathRowTemplate == null || !pathRowTemplate.IsValid ||
                paymentRouteRowTemplate == null || !paymentRouteRowTemplate.IsValid ||
                paymentRecipientButtonTemplate == null || !paymentRecipientButtonTemplate.IsValid ||
                resourceCollectionRecipientButtonTemplate == null ||
                !resourceCollectionRecipientButtonTemplate.IsValid)
            {
                reason = "事件选择窗口动态模板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public void PrepareForUse(
            EventChoiceDialogMode mode,
            string overlayName,
            string panelName,
            Vector2 panelSize,
            Vector2 panelPosition,
            bool blockBackgroundInput)
        {
            ClearForReuse();
            gameObject.name = overlayName ?? string.Empty;
            panel.gameObject.name = panelName ?? string.Empty;
            if (boundedLayout != null) boundedLayout.Configure(panelSize, panelPosition);
            overlayCanvas.overrideSorting = true;
            overlayCanvas.sortingOrder = GameplayUiLayers.Page;
            overlayImage.color = new Color(0f, 0f, 0f, layoutProfile.OverlayAlpha);
            overlayImage.raycastTarget = GetOverlayRaycastTarget(mode);
            ConfigureModeFixedPresentation(mode);
            SetMode(mode);
        }

        public void SetMode(EventChoiceDialogMode mode)
        {
            closeButtonLabel.gameObject.SetActive(mode != EventChoiceDialogMode.ResourceCollectionPayment);
            resourcePaymentCloseLabel.gameObject.SetActive(mode == EventChoiceDialogMode.ResourceCollectionPayment);
            if (pageLayout != null) pageLayout.Configure(mode);
            if (artworkLayoutRoot != null) artworkLayoutRoot.SetActive(false);
            if (eventTextScroll != null) eventTextScroll.SetActive(true);
            var blocks = GetModeBlocks();
            var selected = (int)mode;
            for (var i = 0; i < blocks.Length; i++)
            {
                blocks[i].SetActive(i == selected);
            }
        }

        public void ConfigureArtworkMode()
        {
            if (pageLayout != null) pageLayout.Configure(EventChoiceDialogMode.EventCard, true);
            if (artworkLayoutRoot != null) artworkLayoutRoot.SetActive(true);
            if (eventTextScroll != null) eventTextScroll.SetActive(false);
            titleText.gameObject.SetActive(false);
        }

        public ButtonRow CreateChoiceRow(RectTransform parent) =>
            CloneButtonRow(choiceRowTemplate, parent);

        public ButtonRow CreateArtworkChoiceRow(RectTransform parent) =>
            CloneButtonRow(artworkChoiceRowTemplate, parent);

        public ButtonRow CreatePathRow(RectTransform parent) =>
            CloneButtonRow(pathRowTemplate, parent);

        public PaymentRouteRow CreatePaymentRouteRow(RectTransform parent)
        {
            var row = CloneRoot(paymentRouteRowTemplate.Root, parent);
            return new PaymentRouteRow(
                row,
                FindRequired<Text>(row, "Route Label"),
                FindRequired<RectTransform>(row, "Recipient Host"));
        }

        public ButtonRow CreatePaymentRecipientButton(RectTransform parent) =>
            CloneButtonRow(paymentRecipientButtonTemplate, parent);

        public ButtonRow CreateResourceCollectionRecipientButton(RectTransform parent) =>
            CloneButtonRow(resourceCollectionRecipientButtonTemplate, parent);

        public void ClearForReuse()
        {
            ClearCallbacks();
            for (var i = dynamicInstances.Count - 1; i >= 0; i--)
            {
                if (dynamicInstances[i] != null)
                {
                    if (UnityEngine.Application.isPlaying)
                    {
                        Destroy(dynamicInstances[i]);
                    }
                    else
                    {
                        DestroyImmediate(dynamicInstances[i]);
                    }
                }
            }

            dynamicInstances.Clear();
            titleText.text = string.Empty;
            titleText.gameObject.SetActive(true);
            metadataText.text = string.Empty;
            descriptionText.text = string.Empty;
            metadataText.gameObject.SetActive(false);
            descriptionText.gameObject.SetActive(false);
            closeButton.gameObject.SetActive(false);
            expandedContent.gameObject.SetActive(true);
            eventCardArtworkImage.texture = null;
            eventCardArtworkImage.gameObject.SetActive(false);
            eventCardMetadataRibbonLabel.text = string.Empty;
            eventCardMetadataRibbon.SetActive(false);
            facilityPreviewImage.texture = null;
            facilityPreviewImage.gameObject.SetActive(false);
            facilityPreviewFallback.text = string.Empty;
            facilityPreviewFallback.gameObject.SetActive(false);
            resourcePaymentBankButton.gameObject.SetActive(false);
            resourcePaymentCancelButton.gameObject.SetActive(false);
        }

        public void ClearCallbacks()
        {
            for (var i = 0; i < dynamicInstances.Count; i++)
            {
                if (dynamicInstances[i] == null)
                {
                    continue;
                }

                var buttons = dynamicInstances[i].GetComponentsInChildren<Button>(true);
                for (var buttonIndex = 0; buttonIndex < buttons.Length; buttonIndex++)
                {
                    buttons[buttonIndex].onClick.RemoveAllListeners();
                }
            }

            closeButton.onClick.RemoveAllListeners();
            exploreConfirmButton.onClick.RemoveAllListeners();
            resourcePaymentBankButton.onClick.RemoveAllListeners();
            resourcePaymentCancelButton.onClick.RemoveAllListeners();
            buildResourceButton.onClick.RemoveAllListeners();
            buildGoldButton.onClick.RemoveAllListeners();
            buildBackButton.onClick.RemoveAllListeners();
            buildConfirmButton.onClick.RemoveAllListeners();
            characterContinueButton.onClick.RemoveAllListeners();
            characterFinishButton.onClick.RemoveAllListeners();
            closeInputHandler.Configure(null);
        }

        private ButtonRow CloneButtonRow(ButtonRow template, RectTransform parent)
        {
            var row = CloneRoot(template.Root, parent);
            return new ButtonRow(row, row.GetComponent<Button>(), FindRequired<Text>(row, "Label"));
        }

        private RectTransform CloneRoot(RectTransform template, RectTransform parent)
        {
            var clone = Instantiate(template, parent, false);
            clone.gameObject.SetActive(true);
            dynamicInstances.Add(clone.gameObject);
            return clone;
        }

        private void ConfigureModeFixedPresentation(EventChoiceDialogMode mode)
        {
            titleText.text = modeTitles != null && (int)mode < modeTitles.Length ? modeTitles[(int)mode] : string.Empty;
            EventChoiceDialogRectLayout titleLayout;
            EventChoiceDialogTextStyle titleStyle;
            switch (mode)
            {
                case EventChoiceDialogMode.EventCard:
                    titleLayout = layoutProfile.EventTitleLayout;
                    titleStyle = layoutProfile.EventTitleTextStyle;
                    ApplyTextStyle(metadataText, layoutProfile.EventMetadataTextStyle);
                    metadataText.color = UiTheme.LabelText;
                    ApplyTextStyle(descriptionText, layoutProfile.EventDescriptionTextStyle);
                    descriptionText.color = UiTheme.ValueText;
                    titleText.gameObject.name = "Title";
                    descriptionText.gameObject.name = "Description";
                    break;
                case EventChoiceDialogMode.ExplorePath:
                    titleLayout = layoutProfile.ExplorePathTitleLayout;
                    titleStyle = layoutProfile.ExplorePathTitleTextStyle;
                    titleText.gameObject.name = "Title";
                    break;
                case EventChoiceDialogMode.ExplorePayment:
                    titleLayout = layoutProfile.ExplorePaymentTitleLayout;
                    titleStyle = layoutProfile.ExplorePaymentTitleTextStyle;
                    titleText.gameObject.name = "Title";
                    break;
                case EventChoiceDialogMode.ResourceCollectionPayment:
                    titleLayout = layoutProfile.ResourcePaymentTitleLayout;
                    titleStyle = layoutProfile.ResourcePaymentTitleTextStyle;
                    ConfigureClosePresentation(
                        "Close Payment",
                        "X",
                        layoutProfile.ManualCloseButtonLayout,
                        layoutProfile.ManualCloseButtonStyle);
                    titleText.gameObject.name = "Title";
                    break;
                case EventChoiceDialogMode.BuildFacilityFocus:
                    titleLayout = layoutProfile.BuildFocusTitleLayout;
                    titleStyle = layoutProfile.BuildFocusTitleTextStyle;
                    ConfigureClosePresentation(
                        "Close Build Facility Focus Button",
                        "×",
                        layoutProfile.BuildCloseButtonLayout,
                        layoutProfile.BuildCloseButtonStyle);
                    titleText.gameObject.name = "Title";
                    break;
                case EventChoiceDialogMode.BuildFacilityConfirmation:
                    titleLayout = layoutProfile.BuildConfirmationTitleLayout;
                    titleStyle = layoutProfile.BuildConfirmationTitleTextStyle;
                    ConfigureClosePresentation(
                        "Close Build Facility Confirmation Button",
                        "×",
                        layoutProfile.BuildCloseButtonLayout,
                        layoutProfile.BuildCloseButtonStyle);
                    titleText.gameObject.name = "Title";
                    break;
                case EventChoiceDialogMode.CharacterSecondEffectDecision:
                    titleLayout = layoutProfile.CharacterTitleLayout;
                    titleStyle = layoutProfile.CharacterTitleTextStyle;
                    ApplyTextStyle(descriptionText, layoutProfile.CharacterDescriptionTextStyle);
                    descriptionText.color = UiTheme.GoldText;
                    titleText.gameObject.name = "Character Second Effect Title";
                    descriptionText.gameObject.name = "Character Second Effect Description";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }

            ApplyTextStyle(titleText, titleStyle);
        }

        private bool GetOverlayRaycastTarget(EventChoiceDialogMode mode)
        {
            switch (mode)
            {
                case EventChoiceDialogMode.EventCard:
                    return layoutProfile.EventOverlayRaycastTarget;
                case EventChoiceDialogMode.ExplorePath:
                    return layoutProfile.ExplorePathOverlayRaycastTarget;
                case EventChoiceDialogMode.ExplorePayment:
                    return layoutProfile.ExplorePaymentOverlayRaycastTarget;
                case EventChoiceDialogMode.ResourceCollectionPayment:
                    return layoutProfile.ResourcePaymentOverlayRaycastTarget;
                case EventChoiceDialogMode.BuildFacilityFocus:
                    return layoutProfile.BuildFocusOverlayRaycastTarget;
                case EventChoiceDialogMode.BuildFacilityConfirmation:
                    return layoutProfile.BuildConfirmationOverlayRaycastTarget;
                case EventChoiceDialogMode.CharacterSecondEffectDecision:
                    return layoutProfile.CharacterOverlayRaycastTarget;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }

        private void ConfigureClosePresentation(
            string objectName,
            string label,
            EventChoiceDialogRectLayout buttonLayout,
            EventChoiceDialogButtonStyle buttonStyle)
        {
            closeButton.gameObject.name = objectName;
            // 关闭按钮的文字、位置和样式来自当前预制体，模式切换只控制显隐。
            closeButton.gameObject.SetActive(true);
            closeButton.transform.SetAsLastSibling();
        }

        private static void ApplyButtonStyle(
            Button button,
            Text label,
            EventChoiceDialogButtonStyle style)
        {
            var outline = button.GetComponent<Outline>();
            outline.effectDistance = style.OutlineDistance;
            var labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = style.LabelOffsetMin;
            labelRect.offsetMax = style.LabelOffsetMax;
            ApplyTextStyle(label, style.LabelStyle);

            var labelOutline = label.GetComponent<Outline>();
            if (labelOutline != null)
            {
                labelOutline.enabled = style.LabelHasOutline;
                labelOutline.effectDistance = style.LabelOutlineDistance;
            }
        }

        private static void ApplyTextStyle(Text text, EventChoiceDialogTextStyle style)
        {
            text.fontSize = style.FontSize;
            text.fontStyle = FontStyle.Normal;
            text.alignment = style.Alignment;
            text.horizontalOverflow = style.HorizontalOverflow;
            text.verticalOverflow = style.VerticalOverflow;
            text.resizeTextForBestFit = style.ResizeTextForBestFit;
            text.resizeTextMinSize = style.ResizeMinSize;
            text.resizeTextMaxSize = style.ResizeMaxSize;
            text.raycastTarget = style.RaycastTarget;
        }

        private static T FindRequired<T>(RectTransform root, string objectName) where T : Component
        {
            var children = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < children.Length; i++)
            {
                if (children[i].name == objectName)
                {
                    return children[i].GetComponent<T>();
                }
            }

            throw new InvalidOperationException("事件选择模板缺少子引用：" + objectName);
        }

        private GameObject[] GetModeBlocks()
        {
            return new[]
            {
                eventCardMode,
                explorePathMode,
                explorePaymentMode,
                resourceCollectionPaymentMode,
                buildFacilityFocusMode,
                buildFacilityConfirmationMode,
                characterSecondEffectDecisionMode
            };
        }
    }
}
