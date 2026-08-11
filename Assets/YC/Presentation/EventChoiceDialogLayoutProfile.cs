using System;
using UnityEngine;

namespace YC.Presentation
{
    [Serializable]
    public struct EventChoiceDialogRectLayout
    {
        public Vector2 AnchorMin;
        public Vector2 AnchorMax;
        public Vector2 Pivot;
        public Vector2 SizeDelta;
        public Vector2 AnchoredPosition;

        public void ApplyTo(RectTransform rect)
        {
            if (rect == null)
            {
                throw new ArgumentNullException(nameof(rect));
            }

            rect.anchorMin = AnchorMin;
            rect.anchorMax = AnchorMax;
            rect.pivot = Pivot;
            rect.sizeDelta = SizeDelta;
            rect.anchoredPosition = AnchoredPosition;
        }

        internal bool TryValidate(bool requirePositiveSize, out string reason)
        {
            if (!IsNormalized(AnchorMin) || !IsNormalized(AnchorMax) || !IsNormalized(Pivot) ||
                AnchorMin.x > AnchorMax.x || AnchorMin.y > AnchorMax.y ||
                !IsFinite(SizeDelta) || !IsFinite(AnchoredPosition))
            {
                reason = "RectTransform 布局包含越界、逆序或非有限数值。";
                return false;
            }

            if (requirePositiveSize && (SizeDelta.x <= 0f || SizeDelta.y <= 0f))
            {
                reason = "RectTransform 模板尺寸必须为正值。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool IsNormalized(Vector2 value)
        {
            return IsFinite(value) && value.x >= 0f && value.x <= 1f &&
                   value.y >= 0f && value.y <= 1f;
        }

        private static bool IsFinite(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    [Serializable]
    public struct EventChoiceDialogTextStyle
    {
        public int FontSize;
        public int ResizeMinSize;
        public int ResizeMaxSize;
        public FontStyle FontStyle;
        public TextAnchor Alignment;
        public HorizontalWrapMode HorizontalOverflow;
        public VerticalWrapMode VerticalOverflow;
        public bool ResizeTextForBestFit;
        public bool RaycastTarget;

        internal bool TryValidate(out string reason)
        {
            if (FontSize <= 0 || ResizeMinSize <= 0 || ResizeMaxSize < ResizeMinSize ||
                FontSize < ResizeMinSize || FontSize > ResizeMaxSize ||
                !Enum.IsDefined(typeof(FontStyle), FontStyle) ||
                !Enum.IsDefined(typeof(TextAnchor), Alignment) ||
                !Enum.IsDefined(typeof(HorizontalWrapMode), HorizontalOverflow) ||
                !Enum.IsDefined(typeof(VerticalWrapMode), VerticalOverflow))
            {
                reason = "文本样式包含无效字号、枚举或缩放范围。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    [Serializable]
    public struct EventChoiceDialogButtonStyle
    {
        public EventChoiceDialogTextStyle LabelStyle;
        public Vector2 LabelOffsetMin;
        public Vector2 LabelOffsetMax;
        public Vector2 OutlineDistance;
        public bool LabelHasOutline;
        public Vector2 LabelOutlineDistance;

        internal bool TryValidate(out string reason)
        {
            if (!LabelStyle.TryValidate(out reason) ||
                !IsFinite(LabelOffsetMin) || !IsFinite(LabelOffsetMax) ||
                !IsFinite(OutlineDistance) || !IsFinite(LabelOutlineDistance))
            {
                reason = string.IsNullOrEmpty(reason)
                    ? "按钮样式包含非有限布局或 Outline 数值。"
                    : reason;
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool IsFinite(Vector2 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y);
        }
    }

    [Serializable]
    public sealed class EventChoiceDialogLayoutValues
    {
        public EventChoiceDialogRectLayout PanelLayout;
        public Vector2 PanelOutlineDistance;
        public float OverlayAlpha;
        public bool EventOverlayRaycastTarget;
        public bool ExplorePathOverlayRaycastTarget;
        public bool ExplorePaymentOverlayRaycastTarget;
        public bool ResourcePaymentOverlayRaycastTarget;
        public bool BuildFocusOverlayRaycastTarget;
        public bool BuildConfirmationOverlayRaycastTarget;
        public bool LegacyCityStyleOverlayRaycastTarget;
        public bool CharacterOverlayRaycastTarget;

        public float EventCardPanelWidth;
        public Vector2 EventCardPanelPosition;
        public float EventCardCollapsedHeight;
        public float EventCardTopPadding;
        public float EventCardTitleMetadataGap;
        public float EventCardMetadataDescriptionGap;
        public float EventCardDescriptionWidthRatio;
        public float EventCardDescriptionMinHeight;
        public float EventCardChoiceStep;
        public float EventCardPaymentRowStep;
        public float EventCardPaymentFirstRowGap;
        public float EventCardNoPaymentChoiceGap;
        public float EventCardPaymentChoiceGap;
        public float EventCardToggleButtonTopGap;
        public float EventCardToggleButtonTopInset;
        public EventChoiceDialogRectLayout EventTitleLayout;
        public EventChoiceDialogTextStyle EventTitleTextStyle;
        public EventChoiceDialogRectLayout EventMetadataLayout;
        public EventChoiceDialogTextStyle EventMetadataTextStyle;
        public EventChoiceDialogRectLayout EventDescriptionLayout;
        public EventChoiceDialogTextStyle EventDescriptionTextStyle;
        public EventChoiceDialogRectLayout CollapsedSummaryLayout;
        public EventChoiceDialogTextStyle CollapsedSummaryTextStyle;
        public EventChoiceDialogRectLayout CollapseButtonLayout;
        public EventChoiceDialogButtonStyle CollapseButtonStyle;
        public EventChoiceDialogRectLayout CollapseIconLayout;

        public float ExplorePathPanelWidth;
        public float ExplorePathPanelBaseHeight;
        public float ExplorePathPanelRowStep;
        public Vector2 ExplorePathPanelPosition;
        public float ExplorePathFirstRowOffset;
        public EventChoiceDialogRectLayout ExplorePathTitleLayout;
        public EventChoiceDialogTextStyle ExplorePathTitleTextStyle;

        public float ExplorePaymentPanelWidth;
        public float ExplorePaymentPanelBaseHeight;
        public float ExplorePaymentPanelRowStep;
        public Vector2 ExplorePaymentPanelPosition;
        public float ExplorePaymentFirstRouteOffset;
        public float ExplorePaymentConfirmBaseOffset;
        public EventChoiceDialogRectLayout ExplorePaymentTitleLayout;
        public EventChoiceDialogTextStyle ExplorePaymentTitleTextStyle;

        public float ResourcePaymentPanelWidth;
        public float ResourcePaymentPanelBaseHeight;
        public float ResourcePaymentPanelRowStep;
        public Vector2 ResourcePaymentPanelPosition;
        public float ResourcePaymentFirstRowOffset;
        public EventChoiceDialogRectLayout ResourcePaymentTitleLayout;
        public EventChoiceDialogTextStyle ResourcePaymentTitleTextStyle;
        public EventChoiceDialogRectLayout ResourceReceiverLayout;
        public EventChoiceDialogTextStyle ResourceReceiverTextStyle;
        public EventChoiceDialogRectLayout ResourceBankButtonLayout;
        public EventChoiceDialogButtonStyle ResourceBankButtonStyle;
        public EventChoiceDialogRectLayout ManualCloseButtonLayout;
        public EventChoiceDialogButtonStyle ManualCloseButtonStyle;

        public Vector2 BuildFacilityFocusPanelSize;
        public Vector2 BuildFacilityFocusPanelPosition;
        public EventChoiceDialogRectLayout BuildFocusTitleLayout;
        public EventChoiceDialogTextStyle BuildFocusTitleTextStyle;
        public EventChoiceDialogRectLayout FacilityPreviewContainerLayout;
        public EventChoiceDialogRectLayout BuildFocusDetailsLayout;
        public EventChoiceDialogTextStyle BuildFocusDetailsTextStyle;
        public EventChoiceDialogRectLayout BuildResourceButtonLayout;
        public EventChoiceDialogButtonStyle BuildResourceButtonStyle;
        public EventChoiceDialogRectLayout BuildResourceReasonLayout;
        public EventChoiceDialogTextStyle BuildResourceReasonTextStyle;
        public EventChoiceDialogRectLayout BuildGoldButtonLayout;
        public EventChoiceDialogButtonStyle BuildGoldButtonStyle;
        public EventChoiceDialogRectLayout BuildGoldReasonLayout;
        public EventChoiceDialogTextStyle BuildGoldReasonTextStyle;
        public EventChoiceDialogRectLayout BuildFocusErrorLayout;
        public EventChoiceDialogTextStyle BuildFocusErrorTextStyle;
        public Vector2 BuildFacilityConfirmationPanelSize;
        public Vector2 BuildFacilityConfirmationPanelPosition;
        public EventChoiceDialogRectLayout BuildConfirmationTitleLayout;
        public EventChoiceDialogTextStyle BuildConfirmationTitleTextStyle;
        public EventChoiceDialogRectLayout BuildConfirmationSummaryLayout;
        public EventChoiceDialogTextStyle BuildConfirmationSummaryTextStyle;
        public EventChoiceDialogRectLayout BuildConfirmationErrorLayout;
        public EventChoiceDialogTextStyle BuildConfirmationErrorTextStyle;
        public EventChoiceDialogRectLayout BuildBackButtonLayout;
        public EventChoiceDialogButtonStyle BuildBackButtonStyle;
        public EventChoiceDialogRectLayout BuildConfirmButtonLayout;
        public EventChoiceDialogButtonStyle BuildConfirmButtonStyle;
        public EventChoiceDialogRectLayout BuildCloseButtonLayout;
        public EventChoiceDialogButtonStyle BuildCloseButtonStyle;

        public float LegacyCityStylePanelWidth;
        public float LegacyCityStylePanelBaseHeight;
        public float LegacyCityStylePanelRowStep;
        public Vector2 LegacyCityStylePanelPosition;
        public float LegacyCityStyleFirstRowOffset;
        public EventChoiceDialogRectLayout LegacyCityStyleTitleLayout;
        public EventChoiceDialogTextStyle LegacyCityStyleTitleTextStyle;
        public EventChoiceDialogRectLayout LegacyCityStyleEmptyLayout;
        public EventChoiceDialogTextStyle LegacyCityStyleEmptyTextStyle;

        public Vector2 CharacterSecondEffectPanelSize;
        public Vector2 CharacterSecondEffectPanelPosition;
        public EventChoiceDialogRectLayout CharacterTitleLayout;
        public EventChoiceDialogTextStyle CharacterTitleTextStyle;
        public EventChoiceDialogRectLayout CharacterDescriptionLayout;
        public EventChoiceDialogTextStyle CharacterDescriptionTextStyle;
        public EventChoiceDialogRectLayout CharacterContinueButtonLayout;
        public EventChoiceDialogButtonStyle CharacterContinueButtonStyle;
        public EventChoiceDialogRectLayout CharacterFinishButtonLayout;
        public EventChoiceDialogButtonStyle CharacterFinishButtonStyle;

        public EventChoiceDialogRectLayout ChoiceRowTemplateLayout;
        public EventChoiceDialogButtonStyle ChoiceRowButtonStyle;
        public EventChoiceDialogRectLayout PathRowTemplateLayout;
        public EventChoiceDialogButtonStyle PathRowButtonStyle;
        public EventChoiceDialogRectLayout ExploreConfirmTemplateLayout;
        public EventChoiceDialogButtonStyle ExploreConfirmButtonStyle;
        public EventChoiceDialogRectLayout ResourceRecipientTemplateLayout;
        public EventChoiceDialogButtonStyle ResourceRecipientButtonStyle;
        public EventChoiceDialogRectLayout LegacyCityStyleTemplateLayout;
        public EventChoiceDialogRectLayout LegacyCityStyleSummaryLayout;
        public EventChoiceDialogTextStyle LegacyCityStyleSummaryTextStyle;
        public EventChoiceDialogRectLayout LegacyCityStyleReasonLayout;
        public EventChoiceDialogTextStyle LegacyCityStyleReasonTextStyle;
        public EventChoiceDialogRectLayout LegacyCityStyleDeclareLayout;
        public EventChoiceDialogButtonStyle LegacyCityStyleDeclareButtonStyle;
        public EventChoiceDialogRectLayout PaymentRouteTemplateLayout;
        public EventChoiceDialogRectLayout PaymentRouteLabelLayout;
        public EventChoiceDialogTextStyle PaymentRouteLabelTextStyle;
        public EventChoiceDialogRectLayout PaymentRecipientTemplateLayout;
        public EventChoiceDialogButtonStyle PaymentRecipientButtonStyle;
        public float PaymentRecipientFirstOffsetX;
        public float PaymentRecipientStepX;

        public float TextCharacterWidthScale;
        public int TextMinimumLineCount;
        public float TextLineHeightScale;
        public float TextExtraHeight;
        public float TextNewlineWeight;
        public float TextWhitespaceWeight;
        public float TextAsciiWeight;
        public float TextNonAsciiWeight;
    }

    [CreateAssetMenu(
        fileName = "EventChoiceDialogLayoutProfile",
        menuName = "YC/Presentation/Event Choice Dialog Layout Profile")]
    public sealed class EventChoiceDialogLayoutProfile : ScriptableObject
    {
        [SerializeField] private string sourceManifestSha256 = string.Empty;
        [SerializeField] private EventChoiceDialogLayoutValues values =
            new EventChoiceDialogLayoutValues();

        public string SourceManifestSha256 => sourceManifestSha256;
        public EventChoiceDialogRectLayout PanelLayout => values.PanelLayout;
        public Vector2 PanelOutlineDistance => values.PanelOutlineDistance;
        public float OverlayAlpha => values.OverlayAlpha;
        public bool EventOverlayRaycastTarget => values.EventOverlayRaycastTarget;
        public bool ExplorePathOverlayRaycastTarget => values.ExplorePathOverlayRaycastTarget;
        public bool ExplorePaymentOverlayRaycastTarget => values.ExplorePaymentOverlayRaycastTarget;
        public bool ResourcePaymentOverlayRaycastTarget => values.ResourcePaymentOverlayRaycastTarget;
        public bool BuildFocusOverlayRaycastTarget => values.BuildFocusOverlayRaycastTarget;
        public bool BuildConfirmationOverlayRaycastTarget => values.BuildConfirmationOverlayRaycastTarget;
        public bool LegacyCityStyleOverlayRaycastTarget => values.LegacyCityStyleOverlayRaycastTarget;
        public bool CharacterOverlayRaycastTarget => values.CharacterOverlayRaycastTarget;
        public float EventCardPanelWidth => values.EventCardPanelWidth;
        public Vector2 EventCardPanelPosition => values.EventCardPanelPosition;
        public float EventCardCollapsedHeight => values.EventCardCollapsedHeight;
        public float EventCardTopPadding => values.EventCardTopPadding;
        public float EventCardTitleMetadataGap => values.EventCardTitleMetadataGap;
        public float EventCardMetadataDescriptionGap => values.EventCardMetadataDescriptionGap;
        public float EventCardDescriptionWidthRatio => values.EventCardDescriptionWidthRatio;
        public float EventCardDescriptionMinHeight => values.EventCardDescriptionMinHeight;
        public float EventCardChoiceStep => values.EventCardChoiceStep;
        public float EventCardPaymentRowStep => values.EventCardPaymentRowStep;
        public float EventCardPaymentFirstRowGap => values.EventCardPaymentFirstRowGap;
        public float EventCardNoPaymentChoiceGap => values.EventCardNoPaymentChoiceGap;
        public float EventCardPaymentChoiceGap => values.EventCardPaymentChoiceGap;
        public float EventCardToggleButtonTopGap => values.EventCardToggleButtonTopGap;
        public float EventCardToggleButtonTopInset => values.EventCardToggleButtonTopInset;
        public int EventCardDescriptionFontSize => values.EventDescriptionTextStyle.FontSize;
        public EventChoiceDialogRectLayout EventTitleLayout => values.EventTitleLayout;
        public EventChoiceDialogTextStyle EventTitleTextStyle => values.EventTitleTextStyle;
        public EventChoiceDialogRectLayout EventMetadataLayout => values.EventMetadataLayout;
        public EventChoiceDialogTextStyle EventMetadataTextStyle => values.EventMetadataTextStyle;
        public EventChoiceDialogRectLayout EventDescriptionLayout => values.EventDescriptionLayout;
        public EventChoiceDialogTextStyle EventDescriptionTextStyle => values.EventDescriptionTextStyle;
        public EventChoiceDialogRectLayout CollapsedSummaryLayout => values.CollapsedSummaryLayout;
        public EventChoiceDialogTextStyle CollapsedSummaryTextStyle => values.CollapsedSummaryTextStyle;
        public EventChoiceDialogRectLayout CollapseButtonLayout => values.CollapseButtonLayout;
        public EventChoiceDialogButtonStyle CollapseButtonStyle => values.CollapseButtonStyle;
        public EventChoiceDialogRectLayout CollapseIconLayout => values.CollapseIconLayout;
        public float ExplorePathPanelWidth => values.ExplorePathPanelWidth;
        public float ExplorePathPanelBaseHeight => values.ExplorePathPanelBaseHeight;
        public float ExplorePathPanelRowStep => values.ExplorePathPanelRowStep;
        public Vector2 ExplorePathPanelPosition => values.ExplorePathPanelPosition;
        public float ExplorePathFirstRowOffset => values.ExplorePathFirstRowOffset;
        public EventChoiceDialogRectLayout ExplorePathTitleLayout => values.ExplorePathTitleLayout;
        public EventChoiceDialogTextStyle ExplorePathTitleTextStyle => values.ExplorePathTitleTextStyle;
        public float ExplorePaymentPanelWidth => values.ExplorePaymentPanelWidth;
        public float ExplorePaymentPanelBaseHeight => values.ExplorePaymentPanelBaseHeight;
        public float ExplorePaymentPanelRowStep => values.ExplorePaymentPanelRowStep;
        public Vector2 ExplorePaymentPanelPosition => values.ExplorePaymentPanelPosition;
        public float ExplorePaymentFirstRouteOffset => values.ExplorePaymentFirstRouteOffset;
        public float ExplorePaymentConfirmBaseOffset => values.ExplorePaymentConfirmBaseOffset;
        public EventChoiceDialogRectLayout ExplorePaymentTitleLayout => values.ExplorePaymentTitleLayout;
        public EventChoiceDialogTextStyle ExplorePaymentTitleTextStyle => values.ExplorePaymentTitleTextStyle;
        public float ResourcePaymentPanelWidth => values.ResourcePaymentPanelWidth;
        public float ResourcePaymentPanelBaseHeight => values.ResourcePaymentPanelBaseHeight;
        public float ResourcePaymentPanelRowStep => values.ResourcePaymentPanelRowStep;
        public Vector2 ResourcePaymentPanelPosition => values.ResourcePaymentPanelPosition;
        public float ResourcePaymentFirstRowOffset => values.ResourcePaymentFirstRowOffset;
        public EventChoiceDialogRectLayout ResourcePaymentTitleLayout => values.ResourcePaymentTitleLayout;
        public EventChoiceDialogTextStyle ResourcePaymentTitleTextStyle => values.ResourcePaymentTitleTextStyle;
        public EventChoiceDialogRectLayout ResourceReceiverLayout => values.ResourceReceiverLayout;
        public EventChoiceDialogTextStyle ResourceReceiverTextStyle => values.ResourceReceiverTextStyle;
        public EventChoiceDialogRectLayout ResourceBankButtonLayout => values.ResourceBankButtonLayout;
        public EventChoiceDialogButtonStyle ResourceBankButtonStyle => values.ResourceBankButtonStyle;
        public EventChoiceDialogRectLayout ManualCloseButtonLayout => values.ManualCloseButtonLayout;
        public EventChoiceDialogButtonStyle ManualCloseButtonStyle => values.ManualCloseButtonStyle;
        public Vector2 BuildFacilityFocusPanelSize => values.BuildFacilityFocusPanelSize;
        public Vector2 BuildFacilityFocusPanelPosition => values.BuildFacilityFocusPanelPosition;
        public EventChoiceDialogRectLayout BuildFocusTitleLayout => values.BuildFocusTitleLayout;
        public EventChoiceDialogTextStyle BuildFocusTitleTextStyle => values.BuildFocusTitleTextStyle;
        public EventChoiceDialogRectLayout FacilityPreviewContainerLayout => values.FacilityPreviewContainerLayout;
        public EventChoiceDialogRectLayout BuildFocusDetailsLayout => values.BuildFocusDetailsLayout;
        public EventChoiceDialogTextStyle BuildFocusDetailsTextStyle => values.BuildFocusDetailsTextStyle;
        public EventChoiceDialogRectLayout BuildResourceButtonLayout => values.BuildResourceButtonLayout;
        public EventChoiceDialogButtonStyle BuildResourceButtonStyle => values.BuildResourceButtonStyle;
        public EventChoiceDialogRectLayout BuildResourceReasonLayout => values.BuildResourceReasonLayout;
        public EventChoiceDialogTextStyle BuildResourceReasonTextStyle => values.BuildResourceReasonTextStyle;
        public EventChoiceDialogRectLayout BuildGoldButtonLayout => values.BuildGoldButtonLayout;
        public EventChoiceDialogButtonStyle BuildGoldButtonStyle => values.BuildGoldButtonStyle;
        public EventChoiceDialogRectLayout BuildGoldReasonLayout => values.BuildGoldReasonLayout;
        public EventChoiceDialogTextStyle BuildGoldReasonTextStyle => values.BuildGoldReasonTextStyle;
        public EventChoiceDialogRectLayout BuildFocusErrorLayout => values.BuildFocusErrorLayout;
        public EventChoiceDialogTextStyle BuildFocusErrorTextStyle => values.BuildFocusErrorTextStyle;
        public Vector2 BuildFacilityConfirmationPanelSize => values.BuildFacilityConfirmationPanelSize;
        public Vector2 BuildFacilityConfirmationPanelPosition => values.BuildFacilityConfirmationPanelPosition;
        public EventChoiceDialogRectLayout BuildConfirmationTitleLayout => values.BuildConfirmationTitleLayout;
        public EventChoiceDialogTextStyle BuildConfirmationTitleTextStyle => values.BuildConfirmationTitleTextStyle;
        public EventChoiceDialogRectLayout BuildConfirmationSummaryLayout => values.BuildConfirmationSummaryLayout;
        public EventChoiceDialogTextStyle BuildConfirmationSummaryTextStyle => values.BuildConfirmationSummaryTextStyle;
        public EventChoiceDialogRectLayout BuildConfirmationErrorLayout => values.BuildConfirmationErrorLayout;
        public EventChoiceDialogTextStyle BuildConfirmationErrorTextStyle => values.BuildConfirmationErrorTextStyle;
        public EventChoiceDialogRectLayout BuildBackButtonLayout => values.BuildBackButtonLayout;
        public EventChoiceDialogButtonStyle BuildBackButtonStyle => values.BuildBackButtonStyle;
        public EventChoiceDialogRectLayout BuildConfirmButtonLayout => values.BuildConfirmButtonLayout;
        public EventChoiceDialogButtonStyle BuildConfirmButtonStyle => values.BuildConfirmButtonStyle;
        public EventChoiceDialogRectLayout BuildCloseButtonLayout => values.BuildCloseButtonLayout;
        public EventChoiceDialogButtonStyle BuildCloseButtonStyle => values.BuildCloseButtonStyle;
        public float LegacyCityStylePanelWidth => values.LegacyCityStylePanelWidth;
        public float LegacyCityStylePanelBaseHeight => values.LegacyCityStylePanelBaseHeight;
        public float LegacyCityStylePanelRowStep => values.LegacyCityStylePanelRowStep;
        public Vector2 LegacyCityStylePanelPosition => values.LegacyCityStylePanelPosition;
        public float LegacyCityStyleFirstRowOffset => values.LegacyCityStyleFirstRowOffset;
        public EventChoiceDialogRectLayout LegacyCityStyleTitleLayout => values.LegacyCityStyleTitleLayout;
        public EventChoiceDialogTextStyle LegacyCityStyleTitleTextStyle => values.LegacyCityStyleTitleTextStyle;
        public EventChoiceDialogRectLayout LegacyCityStyleEmptyLayout => values.LegacyCityStyleEmptyLayout;
        public EventChoiceDialogTextStyle LegacyCityStyleEmptyTextStyle => values.LegacyCityStyleEmptyTextStyle;
        public Vector2 CharacterSecondEffectPanelSize => values.CharacterSecondEffectPanelSize;
        public Vector2 CharacterSecondEffectPanelPosition => values.CharacterSecondEffectPanelPosition;
        public EventChoiceDialogRectLayout CharacterTitleLayout => values.CharacterTitleLayout;
        public EventChoiceDialogTextStyle CharacterTitleTextStyle => values.CharacterTitleTextStyle;
        public EventChoiceDialogRectLayout CharacterDescriptionLayout => values.CharacterDescriptionLayout;
        public EventChoiceDialogTextStyle CharacterDescriptionTextStyle => values.CharacterDescriptionTextStyle;
        public EventChoiceDialogRectLayout CharacterContinueButtonLayout => values.CharacterContinueButtonLayout;
        public EventChoiceDialogButtonStyle CharacterContinueButtonStyle => values.CharacterContinueButtonStyle;
        public EventChoiceDialogRectLayout CharacterFinishButtonLayout => values.CharacterFinishButtonLayout;
        public EventChoiceDialogButtonStyle CharacterFinishButtonStyle => values.CharacterFinishButtonStyle;
        public EventChoiceDialogRectLayout ChoiceRowTemplateLayout => values.ChoiceRowTemplateLayout;
        public EventChoiceDialogButtonStyle ChoiceRowButtonStyle => values.ChoiceRowButtonStyle;
        public EventChoiceDialogRectLayout PathRowTemplateLayout => values.PathRowTemplateLayout;
        public EventChoiceDialogButtonStyle PathRowButtonStyle => values.PathRowButtonStyle;
        public EventChoiceDialogRectLayout ExploreConfirmTemplateLayout => values.ExploreConfirmTemplateLayout;
        public EventChoiceDialogButtonStyle ExploreConfirmButtonStyle => values.ExploreConfirmButtonStyle;
        public EventChoiceDialogRectLayout ResourceRecipientTemplateLayout => values.ResourceRecipientTemplateLayout;
        public EventChoiceDialogButtonStyle ResourceRecipientButtonStyle => values.ResourceRecipientButtonStyle;
        public EventChoiceDialogRectLayout LegacyCityStyleTemplateLayout => values.LegacyCityStyleTemplateLayout;
        public EventChoiceDialogRectLayout LegacyCityStyleSummaryLayout => values.LegacyCityStyleSummaryLayout;
        public EventChoiceDialogTextStyle LegacyCityStyleSummaryTextStyle => values.LegacyCityStyleSummaryTextStyle;
        public EventChoiceDialogRectLayout LegacyCityStyleReasonLayout => values.LegacyCityStyleReasonLayout;
        public EventChoiceDialogTextStyle LegacyCityStyleReasonTextStyle => values.LegacyCityStyleReasonTextStyle;
        public EventChoiceDialogRectLayout LegacyCityStyleDeclareLayout => values.LegacyCityStyleDeclareLayout;
        public EventChoiceDialogButtonStyle LegacyCityStyleDeclareButtonStyle => values.LegacyCityStyleDeclareButtonStyle;
        public EventChoiceDialogRectLayout PaymentRouteTemplateLayout => values.PaymentRouteTemplateLayout;
        public EventChoiceDialogRectLayout PaymentRouteLabelLayout => values.PaymentRouteLabelLayout;
        public EventChoiceDialogTextStyle PaymentRouteLabelTextStyle => values.PaymentRouteLabelTextStyle;
        public EventChoiceDialogRectLayout PaymentRecipientTemplateLayout => values.PaymentRecipientTemplateLayout;
        public EventChoiceDialogButtonStyle PaymentRecipientButtonStyle => values.PaymentRecipientButtonStyle;
        public float PaymentRecipientFirstOffsetX => values.PaymentRecipientFirstOffsetX;
        public float PaymentRecipientStepX => values.PaymentRecipientStepX;
        public float TextCharacterWidthScale => values.TextCharacterWidthScale;
        public int TextMinimumLineCount => values.TextMinimumLineCount;
        public float TextLineHeightScale => values.TextLineHeightScale;
        public float TextExtraHeight => values.TextExtraHeight;
        public float TextNewlineWeight => values.TextNewlineWeight;
        public float TextWhitespaceWeight => values.TextWhitespaceWeight;
        public float TextAsciiWeight => values.TextAsciiWeight;
        public float TextNonAsciiWeight => values.TextNonAsciiWeight;

        public bool TryValidateConfiguration(out string reason)
        {
            if (!IsSha256(sourceManifestSha256) || values == null)
            {
                reason = "EventChoiceDialogLayoutProfile 缺少有效 manifest SHA-256 或载荷。";
                return false;
            }

            if (!TryValidateLayouts(values, out reason) ||
                !TryValidateTextStyles(values, out reason) ||
                !TryValidateButtonStyles(values, out reason))
            {
                return false;
            }

            if (!AllPositive(
                    values.EventCardPanelWidth,
                    values.EventCardCollapsedHeight,
                    values.EventCardDescriptionWidthRatio,
                    values.EventCardDescriptionMinHeight,
                    values.EventCardChoiceStep,
                    values.EventCardPaymentRowStep,
                    values.EventCardPaymentFirstRowGap,
                    values.EventCardNoPaymentChoiceGap,
                    values.EventCardPaymentChoiceGap,
                    values.EventCardToggleButtonTopGap,
                    values.EventCardToggleButtonTopInset,
                    values.ExplorePathPanelWidth,
                    values.ExplorePathPanelBaseHeight,
                    values.ExplorePathPanelRowStep,
                    values.ExplorePathFirstRowOffset,
                    values.ExplorePaymentPanelWidth,
                    values.ExplorePaymentPanelBaseHeight,
                    values.ExplorePaymentPanelRowStep,
                    values.ExplorePaymentFirstRouteOffset,
                    values.ExplorePaymentConfirmBaseOffset,
                    values.ResourcePaymentPanelWidth,
                    values.ResourcePaymentPanelBaseHeight,
                    values.ResourcePaymentPanelRowStep,
                    values.ResourcePaymentFirstRowOffset,
                    values.LegacyCityStylePanelWidth,
                    values.LegacyCityStylePanelBaseHeight,
                    values.LegacyCityStylePanelRowStep,
                    values.LegacyCityStyleFirstRowOffset,
                    values.PaymentRecipientStepX,
                    values.TextCharacterWidthScale,
                    values.TextLineHeightScale,
                    values.TextExtraHeight,
                    values.TextNewlineWeight,
                    values.TextWhitespaceWeight,
                    values.TextAsciiWeight,
                    values.TextNonAsciiWeight) ||
                values.TextMinimumLineCount <= 0 ||
                values.EventCardDescriptionWidthRatio > 1f ||
                !IsFinite(values.PaymentRecipientFirstOffsetX) ||
                values.PaymentRecipientFirstOffsetX < 0f ||
                !IsFinite(values.PanelOutlineDistance) ||
                !IsFinite(values.OverlayAlpha) || values.OverlayAlpha < 0f || values.OverlayAlpha > 1f ||
                !IsFinite(values.EventCardTopPadding) || values.EventCardTopPadding < 0f ||
                !IsFinite(values.EventCardTitleMetadataGap) || values.EventCardTitleMetadataGap < 0f ||
                !IsFinite(values.EventCardMetadataDescriptionGap) ||
                values.EventCardMetadataDescriptionGap < 0f ||
                !IsFinite(values.EventCardPanelPosition) ||
                !IsFinite(values.ExplorePathPanelPosition) ||
                !IsFinite(values.ExplorePaymentPanelPosition) ||
                !IsFinite(values.ResourcePaymentPanelPosition) ||
                !IsFinite(values.LegacyCityStylePanelPosition) ||
                !IsPositive(values.BuildFacilityFocusPanelSize) ||
                !IsPositive(values.BuildFacilityConfirmationPanelSize) ||
                !IsPositive(values.CharacterSecondEffectPanelSize) ||
                !IsFinite(values.BuildFacilityFocusPanelPosition) ||
                !IsFinite(values.BuildFacilityConfirmationPanelPosition) ||
                !IsFinite(values.CharacterSecondEffectPanelPosition))
            {
                reason = "EventChoiceDialogLayoutProfile 布局标量必须为有限且在合法范围内。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(string manifestSha256, EventChoiceDialogLayoutValues configuredValues)
        {
            sourceManifestSha256 = manifestSha256 ?? string.Empty;
            values = configuredValues ?? new EventChoiceDialogLayoutValues();
        }

        public bool MatchesValuesForEditor(EventChoiceDialogLayoutValues expected)
        {
            return expected != null && string.Equals(
                JsonUtility.ToJson(values),
                JsonUtility.ToJson(expected),
                StringComparison.Ordinal);
        }
#endif

        private static bool TryValidateLayouts(
            EventChoiceDialogLayoutValues candidate,
            out string reason)
        {
            var layouts = new[]
            {
                candidate.PanelLayout,
                candidate.EventTitleLayout,
                candidate.EventMetadataLayout,
                candidate.EventDescriptionLayout,
                candidate.CollapsedSummaryLayout,
                candidate.CollapseButtonLayout,
                candidate.CollapseIconLayout,
                candidate.ExplorePathTitleLayout,
                candidate.ExplorePaymentTitleLayout,
                candidate.ResourcePaymentTitleLayout,
                candidate.ResourceReceiverLayout,
                candidate.ResourceBankButtonLayout,
                candidate.ManualCloseButtonLayout,
                candidate.BuildFocusTitleLayout,
                candidate.FacilityPreviewContainerLayout,
                candidate.BuildFocusDetailsLayout,
                candidate.BuildResourceButtonLayout,
                candidate.BuildResourceReasonLayout,
                candidate.BuildGoldButtonLayout,
                candidate.BuildGoldReasonLayout,
                candidate.BuildFocusErrorLayout,
                candidate.BuildConfirmationTitleLayout,
                candidate.BuildConfirmationSummaryLayout,
                candidate.BuildConfirmationErrorLayout,
                candidate.BuildBackButtonLayout,
                candidate.BuildConfirmButtonLayout,
                candidate.BuildCloseButtonLayout,
                candidate.LegacyCityStyleTitleLayout,
                candidate.LegacyCityStyleEmptyLayout,
                candidate.CharacterTitleLayout,
                candidate.CharacterDescriptionLayout,
                candidate.CharacterContinueButtonLayout,
                candidate.CharacterFinishButtonLayout,
                candidate.ChoiceRowTemplateLayout,
                candidate.PathRowTemplateLayout,
                candidate.ExploreConfirmTemplateLayout,
                candidate.ResourceRecipientTemplateLayout,
                candidate.LegacyCityStyleTemplateLayout,
                candidate.LegacyCityStyleSummaryLayout,
                candidate.LegacyCityStyleReasonLayout,
                candidate.LegacyCityStyleDeclareLayout,
                candidate.PaymentRouteTemplateLayout,
                candidate.PaymentRouteLabelLayout,
                candidate.PaymentRecipientTemplateLayout
            };
            for (var i = 0; i < layouts.Length; i++)
            {
                if (!layouts[i].TryValidate(false, out reason))
                {
                    reason = "固定 Rect 合同 #" + i + " 无效：" + reason;
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        private static bool TryValidateTextStyles(
            EventChoiceDialogLayoutValues candidate,
            out string reason)
        {
            var styles = new[]
            {
                candidate.EventTitleTextStyle,
                candidate.EventMetadataTextStyle,
                candidate.EventDescriptionTextStyle,
                candidate.CollapsedSummaryTextStyle,
                candidate.ExplorePathTitleTextStyle,
                candidate.ExplorePaymentTitleTextStyle,
                candidate.ResourcePaymentTitleTextStyle,
                candidate.ResourceReceiverTextStyle,
                candidate.BuildFocusTitleTextStyle,
                candidate.BuildFocusDetailsTextStyle,
                candidate.BuildResourceReasonTextStyle,
                candidate.BuildGoldReasonTextStyle,
                candidate.BuildFocusErrorTextStyle,
                candidate.BuildConfirmationTitleTextStyle,
                candidate.BuildConfirmationSummaryTextStyle,
                candidate.BuildConfirmationErrorTextStyle,
                candidate.LegacyCityStyleTitleTextStyle,
                candidate.LegacyCityStyleEmptyTextStyle,
                candidate.CharacterTitleTextStyle,
                candidate.CharacterDescriptionTextStyle,
                candidate.LegacyCityStyleSummaryTextStyle,
                candidate.LegacyCityStyleReasonTextStyle,
                candidate.PaymentRouteLabelTextStyle
            };
            for (var i = 0; i < styles.Length; i++)
            {
                if (!styles[i].TryValidate(out reason))
                {
                    reason = "固定文本样式合同 #" + i + " 无效：" + reason;
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        private static bool TryValidateButtonStyles(
            EventChoiceDialogLayoutValues candidate,
            out string reason)
        {
            var styles = new[]
            {
                candidate.CollapseButtonStyle,
                candidate.ResourceBankButtonStyle,
                candidate.ManualCloseButtonStyle,
                candidate.BuildResourceButtonStyle,
                candidate.BuildGoldButtonStyle,
                candidate.BuildBackButtonStyle,
                candidate.BuildConfirmButtonStyle,
                candidate.BuildCloseButtonStyle,
                candidate.CharacterContinueButtonStyle,
                candidate.CharacterFinishButtonStyle,
                candidate.ChoiceRowButtonStyle,
                candidate.PathRowButtonStyle,
                candidate.ExploreConfirmButtonStyle,
                candidate.ResourceRecipientButtonStyle,
                candidate.LegacyCityStyleDeclareButtonStyle,
                candidate.PaymentRecipientButtonStyle
            };
            for (var i = 0; i < styles.Length; i++)
            {
                if (!styles[i].TryValidate(out reason))
                {
                    reason = "固定按钮样式合同 #" + i + " 无效：" + reason;
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        private static bool AllPositive(params float[] candidates)
        {
            for (var i = 0; i < candidates.Length; i++)
            {
                if (!IsFinite(candidates[i]) || candidates[i] <= 0f)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsPositive(Vector2 value)
        {
            return IsFinite(value) && value.x > 0f && value.y > 0f;
        }

        private static bool IsFinite(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var character = value[i];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'A' && character <= 'F') ||
                      (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
