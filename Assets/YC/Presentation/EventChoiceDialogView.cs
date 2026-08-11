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
        LegacyCityStyleOptions,
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

        [Serializable]
        public sealed class LegacyCityStyleRow
        {
            [SerializeField] private RectTransform root;
            [SerializeField] private Text summary;
            [SerializeField] private Text reason;
            [SerializeField] private Button declareButton;
            [SerializeField] private Text declareLabel;

            public RectTransform Root => root;
            public Text Summary => summary;
            public Text Reason => reason;
            public Button DeclareButton => declareButton;
            public Text DeclareLabel => declareLabel;

            public bool IsValid => root != null && summary != null && reason != null &&
                                   declareButton != null && declareLabel != null;

            public LegacyCityStyleRow()
            {
            }

            internal LegacyCityStyleRow(
                RectTransform configuredRoot,
                Text configuredSummary,
                Text configuredReason,
                Button configuredDeclareButton,
                Text configuredDeclareLabel)
            {
                root = configuredRoot;
                summary = configuredSummary;
                reason = configuredReason;
                declareButton = configuredDeclareButton;
                declareLabel = configuredDeclareLabel;
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
        [SerializeField] private Text collapsedSummaryText;
        [SerializeField] private RectTransform actionArea;
        [SerializeField] private Button closeButton;
        [SerializeField] private Text closeButtonLabel;
        [SerializeField] private Button collapseButton;
        [SerializeField] private Text collapseButtonLabel;
        [SerializeField] private Image collapseButtonIcon;
        [SerializeField] private EffectDialogCollapsiblePanel collapsiblePanel;
        [SerializeField] private EffectDialogDragHandle dragHandle;
        [SerializeField] private WindowCloseInputHandler closeInputHandler;
        [SerializeField] private EventChoiceDialogLayoutProfile layoutProfile;

        [Header("互斥模式块")]
        [SerializeField] private GameObject eventCardMode;
        [SerializeField] private GameObject explorePathMode;
        [SerializeField] private GameObject explorePaymentMode;
        [SerializeField] private GameObject resourceCollectionPaymentMode;
        [SerializeField] private GameObject buildFacilityFocusMode;
        [SerializeField] private GameObject buildFacilityConfirmationMode;
        [SerializeField] private GameObject legacyCityStyleOptionsMode;
        [SerializeField] private GameObject characterSecondEffectDecisionMode;

        [Header("模式内容")]
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
        [SerializeField] private RectTransform legacyCityStyleHost;
        [SerializeField] private Text legacyCityStyleEmptyText;
        [SerializeField] private Button characterContinueButton;
        [SerializeField] private Text characterContinueLabel;
        [SerializeField] private Button characterFinishButton;
        [SerializeField] private Text characterFinishLabel;

        [Header("禁用动态模板")]
        [SerializeField] private ButtonRow choiceRowTemplate = new ButtonRow();
        [SerializeField] private ButtonRow pathRowTemplate = new ButtonRow();
        [SerializeField] private PaymentRouteRow paymentRouteRowTemplate = new PaymentRouteRow();
        [SerializeField] private ButtonRow paymentRecipientButtonTemplate = new ButtonRow();
        [SerializeField] private ButtonRow resourceCollectionRecipientButtonTemplate = new ButtonRow();
        [SerializeField] private LegacyCityStyleRow legacyCityStyleRowTemplate = new LegacyCityStyleRow();

        private readonly List<GameObject> dynamicInstances = new List<GameObject>();

        public Canvas OverlayCanvas => overlayCanvas;
        public RectTransform OverlayRect => overlayRect;
        public Image OverlayImage => overlayImage;
        public RectTransform Panel => panel;
        public RectTransform ExpandedContent => expandedContent;
        public Text TitleText => titleText;
        public Text MetadataText => metadataText;
        public Text DescriptionText => descriptionText;
        public Text CollapsedSummaryText => collapsedSummaryText;
        public RectTransform ActionArea => actionArea;
        public Button CloseButton => closeButton;
        public Text CloseButtonLabel => closeButtonLabel;
        public Button CollapseButton => collapseButton;
        public Text CollapseButtonLabel => collapseButtonLabel;
        public Image CollapseButtonIcon => collapseButtonIcon;
        public EffectDialogCollapsiblePanel CollapsiblePanel => collapsiblePanel;
        public EffectDialogDragHandle DragHandle => dragHandle;
        public WindowCloseInputHandler CloseInputHandler => closeInputHandler;
        public EventChoiceDialogLayoutProfile LayoutProfile => layoutProfile;
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
        public RectTransform LegacyCityStyleHost => legacyCityStyleHost;
        public Text LegacyCityStyleEmptyText => legacyCityStyleEmptyText;
        public Button CharacterContinueButton => characterContinueButton;
        public Text CharacterContinueLabel => characterContinueLabel;
        public Button CharacterFinishButton => characterFinishButton;
        public Text CharacterFinishLabel => characterFinishLabel;

        public bool TryValidateConfiguration(out string reason)
        {
            if (overlayCanvas == null || overlayRect == null || overlayImage == null || panel == null ||
                expandedContent == null || titleText == null || metadataText == null || descriptionText == null ||
                collapsedSummaryText == null || actionArea == null || closeButton == null ||
                closeButtonLabel == null || collapseButton == null || collapseButtonLabel == null ||
                collapseButtonIcon == null || collapsiblePanel == null || dragHandle == null ||
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

            if (eventChoiceHost == null || eventPaymentRouteHost == null || explorePathHost == null ||
                explorePaymentRouteHost == null || exploreConfirmButton == null || exploreConfirmLabel == null ||
                resourcePaymentReceiverText == null || resourcePaymentRecipientHost == null ||
                resourcePaymentBankButton == null || resourcePaymentBankLabel == null ||
                facilityPreviewImage == null || facilityPreviewAspect == null || facilityPreviewFallback == null ||
                buildFocusDetailsText == null || buildResourceButton == null || buildResourceLabel == null ||
                buildResourceReasonText == null || buildGoldButton == null || buildGoldLabel == null ||
                buildGoldReasonText == null || buildFocusErrorText == null ||
                buildConfirmationSummaryText == null || buildConfirmationErrorText == null ||
                buildBackButton == null || buildBackLabel == null || buildConfirmButton == null ||
                buildConfirmLabel == null || legacyCityStyleHost == null || legacyCityStyleEmptyText == null ||
                characterContinueButton == null || characterContinueLabel == null ||
                characterFinishButton == null || characterFinishLabel == null)
            {
                reason = "事件选择窗口模式内容引用不完整。";
                return false;
            }

            if (choiceRowTemplate == null || !choiceRowTemplate.IsValid ||
                pathRowTemplate == null || !pathRowTemplate.IsValid ||
                paymentRouteRowTemplate == null || !paymentRouteRowTemplate.IsValid ||
                paymentRecipientButtonTemplate == null || !paymentRecipientButtonTemplate.IsValid ||
                resourceCollectionRecipientButtonTemplate == null ||
                !resourceCollectionRecipientButtonTemplate.IsValid ||
                legacyCityStyleRowTemplate == null || !legacyCityStyleRowTemplate.IsValid)
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
            panel.sizeDelta = panelSize;
            panel.anchoredPosition = panelPosition;
            overlayCanvas.overrideSorting = true;
            overlayCanvas.sortingOrder = 118;
            overlayImage.color = new Color(0f, 0f, 0f, layoutProfile.OverlayAlpha);
            overlayImage.raycastTarget = GetOverlayRaycastTarget(mode);
            ConfigureModeFixedPresentation(mode);
            SetMode(mode);
        }

        public void SetMode(EventChoiceDialogMode mode)
        {
            var blocks = GetModeBlocks();
            var selected = (int)mode;
            for (var i = 0; i < blocks.Length; i++)
            {
                blocks[i].SetActive(i == selected);
            }
        }

        public ButtonRow CreateChoiceRow(RectTransform parent) =>
            CloneButtonRow(choiceRowTemplate, parent);

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

        public LegacyCityStyleRow CreateLegacyCityStyleRow(RectTransform parent)
        {
            var row = CloneRoot(legacyCityStyleRowTemplate.Root, parent);
            return new LegacyCityStyleRow(
                row,
                FindRequired<Text>(row, "Summary"),
                FindRequired<Text>(row, "Reason"),
                FindRequired<Button>(row, "Declare"),
                FindRequired<Text>(row, "Declare Label"));
        }

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
            metadataText.text = string.Empty;
            descriptionText.text = string.Empty;
            collapsedSummaryText.text = string.Empty;
            metadataText.gameObject.SetActive(false);
            descriptionText.gameObject.SetActive(false);
            collapsedSummaryText.gameObject.SetActive(false);
            closeButton.gameObject.SetActive(false);
            collapseButton.gameObject.SetActive(false);
            expandedContent.gameObject.SetActive(true);
            dragHandle.enabled = false;
            collapsiblePanel.Configure(null);
            facilityPreviewImage.texture = null;
            facilityPreviewImage.gameObject.SetActive(false);
            facilityPreviewFallback.text = string.Empty;
            facilityPreviewFallback.gameObject.SetActive(false);
            resourcePaymentBankButton.gameObject.SetActive(false);
            legacyCityStyleEmptyText.gameObject.SetActive(false);
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
            collapseButton.onClick.RemoveAllListeners();
            exploreConfirmButton.onClick.RemoveAllListeners();
            resourcePaymentBankButton.onClick.RemoveAllListeners();
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
            EventChoiceDialogRectLayout titleLayout;
            EventChoiceDialogTextStyle titleStyle;
            switch (mode)
            {
                case EventChoiceDialogMode.EventCard:
                    titleLayout = layoutProfile.EventTitleLayout;
                    titleStyle = layoutProfile.EventTitleTextStyle;
                    layoutProfile.EventMetadataLayout.ApplyTo(metadataText.rectTransform);
                    ApplyTextStyle(metadataText, layoutProfile.EventMetadataTextStyle);
                    metadataText.color = UiTheme.LabelText;
                    layoutProfile.EventDescriptionLayout.ApplyTo(descriptionText.rectTransform);
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
                case EventChoiceDialogMode.LegacyCityStyleOptions:
                    titleLayout = layoutProfile.LegacyCityStyleTitleLayout;
                    titleStyle = layoutProfile.LegacyCityStyleTitleTextStyle;
                    ConfigureClosePresentation(
                        "Close City Style",
                        "X",
                        layoutProfile.ManualCloseButtonLayout,
                        layoutProfile.ManualCloseButtonStyle);
                    titleText.gameObject.name = "Title";
                    break;
                case EventChoiceDialogMode.CharacterSecondEffectDecision:
                    titleLayout = layoutProfile.CharacterTitleLayout;
                    titleStyle = layoutProfile.CharacterTitleTextStyle;
                    layoutProfile.CharacterDescriptionLayout.ApplyTo(descriptionText.rectTransform);
                    ApplyTextStyle(descriptionText, layoutProfile.CharacterDescriptionTextStyle);
                    descriptionText.color = UiTheme.GoldText;
                    titleText.gameObject.name = "Character Second Effect Title";
                    descriptionText.gameObject.name = "Character Second Effect Description";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }

            titleLayout.ApplyTo(titleText.rectTransform);
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
                case EventChoiceDialogMode.LegacyCityStyleOptions:
                    return layoutProfile.LegacyCityStyleOverlayRaycastTarget;
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
            closeButtonLabel.text = label;
            buttonLayout.ApplyTo(closeButton.GetComponent<RectTransform>());
            ApplyButtonStyle(closeButton, closeButtonLabel, buttonStyle);
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
            text.fontStyle = style.FontStyle;
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
                legacyCityStyleOptionsMode,
                characterSecondEffectDecisionMode
            };
        }
    }
}
