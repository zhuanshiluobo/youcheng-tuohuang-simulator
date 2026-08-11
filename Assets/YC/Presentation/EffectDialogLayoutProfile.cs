using System;
using UnityEngine;

namespace YC.Presentation
{
    [Serializable]
    public struct EffectDialogRectLayout
    {
        public Vector2 AnchorMin;
        public Vector2 AnchorMax;
        public Vector2 Pivot;
        public Vector2 SizeDelta;
        public Vector2 AnchoredPosition;

        public void ApplyTo(RectTransform target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.anchorMin = AnchorMin;
            target.anchorMax = AnchorMax;
            target.pivot = Pivot;
            target.sizeDelta = SizeDelta;
            target.anchoredPosition = AnchoredPosition;
        }

        internal bool TryValidate(bool requirePositiveSize, out string reason)
        {
            if (!EffectDialogLayoutProfile.IsNormalized(AnchorMin) ||
                !EffectDialogLayoutProfile.IsNormalized(AnchorMax) ||
                !EffectDialogLayoutProfile.IsNormalized(Pivot) ||
                AnchorMin.x > AnchorMax.x || AnchorMin.y > AnchorMax.y ||
                !EffectDialogLayoutProfile.IsFinite(SizeDelta) ||
                !EffectDialogLayoutProfile.IsFinite(AnchoredPosition))
            {
                reason = "效果对话框 RectTransform 布局包含越界、逆序或非有限数值。";
                return false;
            }

            if (requirePositiveSize && (SizeDelta.x <= 0f || SizeDelta.y <= 0f))
            {
                reason = "效果对话框固定模板尺寸必须为正值。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    [Serializable]
    public struct EffectDialogInsetLayout
    {
        public Vector2 AnchorMin;
        public Vector2 AnchorMax;
        public Vector2 Pivot;
        public Vector2 OffsetMin;
        public Vector2 OffsetMax;

        public void ApplyTo(RectTransform target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.anchorMin = AnchorMin;
            target.anchorMax = AnchorMax;
            target.pivot = Pivot;
            target.offsetMin = OffsetMin;
            target.offsetMax = OffsetMax;
        }

        internal bool TryValidate(out string reason)
        {
            if (!EffectDialogLayoutProfile.IsNormalized(AnchorMin) ||
                !EffectDialogLayoutProfile.IsNormalized(AnchorMax) ||
                !EffectDialogLayoutProfile.IsNormalized(Pivot) ||
                AnchorMin.x > AnchorMax.x || AnchorMin.y > AnchorMax.y ||
                !EffectDialogLayoutProfile.IsFinite(OffsetMin) ||
                !EffectDialogLayoutProfile.IsFinite(OffsetMax))
            {
                reason = "效果对话框 Insets 布局包含越界、逆序或非有限数值。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    [Serializable]
    public sealed class EffectDialogLayoutValues
    {
        public Color OverlayColor;
        public EffectDialogRectLayout PanelLayout;
        public Vector2 PanelOutlineDistance;
        public EffectDialogRectLayout TitleLayout;
        public EffectDialogRectLayout DescriptionLayout;
        public EffectDialogRectLayout CollapsedSummaryLayout;
        public EffectDialogRectLayout CollapsedToggleLayout;
        public EffectDialogRectLayout ExpandedToggleLayout;
        public EffectDialogRectLayout CollapseIconLayout;
        public float CollapsedHeight;
        public Color CollapsedOverlayColor;
        public bool CollapsedOverlayRaycastTarget;
        public EffectDialogInsetLayout OptionScrollLayout;
        public EffectDialogRectLayout ResourceSummaryLayout;
        public Vector2 ResourceRowAnchor;
        public Vector2 ResourceDecreaseButtonSize;
        public Vector2 ResourceValueSize;
        public Vector2 ResourceIncreaseButtonSize;
        public Vector2 BottomCenterAnchor;
        public Vector2 ResourceCancelButtonPosition;
        public Vector2 ActionButtonSize;

        public Vector2 CharacterOptionsPanelBaseSize;
        public float CharacterOptionsRowHeight;
        public float CharacterOptionsPanelMinHeight;
        public float CharacterOptionsPanelMaxHeight;
        public Vector2 CharacterPanelPosition;
        public float CharacterOptionsDescriptionHeight;
        public float CharacterOptionsScrollTop;
        public float CharacterOptionsScrollBottom;
        public float CharacterOptionsScrollBottomWithCancel;
        public EffectDialogRectLayout CharacterOptionsCancelButtonLayout;
        public Vector2 CharacterResourceSalePanelSize;
        public float CharacterResourceSaleRowStartY;

        public Vector2 SpecialActionPaymentPanelSize;
        public Vector2 SpecialActionPaymentPanelPosition;
        public Vector2 SpecialActionMapPromptPanelSize;

        public Vector2 ExtensionHubPanelSize;
        public float ExtensionHubDescriptionHeight;
        public Vector2 ExtensionHubCardAnchor;
        public Vector2 ExtensionHubCardSize;
        public float ExtensionHubCardGap;
        public float ExtensionHubCardY;
        public Vector2 FacilityCardOutlineDistance;
        public EffectDialogInsetLayout FacilityCardImageLayout;
        public EffectDialogInsetLayout FacilityCardFallbackLayout;
        public EffectDialogRectLayout ExtensionHubSkipButtonLayout;

        public Vector2 OptionsPanelSize;
        public float OptionsScrollTop;
        public float OptionsScrollBottom;
        public float OptionsScrollBottomWithBack;
        public float CollapsibleOptionsExtraBottom;
        public float CollapsibleOptionsExtraBottomWithBack;
        public Vector2 OptionsBackButtonSize;
        public float OptionsBackButtonNormalY;
        public float OptionsBackButtonCollapsibleY;

        public Vector2 ResourceAllocationPanelSize;
        public float ResourceLabelWidth;
        public float ResourceLabelHeight;
        public float ResourceLabelX;
        public float ResourceDecreaseX;
        public float ResourceValueX;
        public float ResourceIncreaseX;

        public float MapPromptPanelWidth;
        public float MapPromptPanelHeight;
        public float CollapsibleMapPromptPanelHeight;
        public Vector2 MapPromptPanelPosition;
        public float MapPromptDescriptionHeight;
        public Vector2 MapPrimaryButtonSize;
        public Vector2 MapBackButtonSize;
        public float MapPrimaryWithBackX;
        public float MapBackWithPrimaryX;
        public float MapButtonNormalY;
        public float MapButtonCollapsibleY;
    }

    [CreateAssetMenu(
        fileName = "EffectDialogLayoutProfile",
        menuName = "YC/Presentation/Effect Dialog Layout Profile")]
    public sealed class EffectDialogLayoutProfile : ScriptableObject
    {
        [SerializeField] private string sourceManifestSha256 = string.Empty;
        [SerializeField] private EffectDialogLayoutValues values = new EffectDialogLayoutValues();

        public string SourceManifestSha256 => sourceManifestSha256;
        public Color OverlayColor => values.OverlayColor;
        public EffectDialogRectLayout PanelLayout => values.PanelLayout;
        public Vector2 PanelOutlineDistance => values.PanelOutlineDistance;
        public EffectDialogRectLayout TitleLayout => values.TitleLayout;
        public EffectDialogRectLayout DescriptionLayout => values.DescriptionLayout;
        public EffectDialogRectLayout CollapsedSummaryLayout => values.CollapsedSummaryLayout;
        public EffectDialogRectLayout CollapsedToggleLayout => values.CollapsedToggleLayout;
        public EffectDialogRectLayout ExpandedToggleLayout => values.ExpandedToggleLayout;
        public EffectDialogRectLayout CollapseIconLayout => values.CollapseIconLayout;
        public float CollapsedHeight => values.CollapsedHeight;
        public Color CollapsedOverlayColor => values.CollapsedOverlayColor;
        public bool CollapsedOverlayRaycastTarget => values.CollapsedOverlayRaycastTarget;
        public EffectDialogInsetLayout OptionScrollLayout => values.OptionScrollLayout;
        public EffectDialogRectLayout ResourceSummaryLayout => values.ResourceSummaryLayout;
        public Vector2 ResourceRowAnchor => values.ResourceRowAnchor;
        public Vector2 ResourceDecreaseButtonSize => values.ResourceDecreaseButtonSize;
        public Vector2 ResourceValueSize => values.ResourceValueSize;
        public Vector2 ResourceIncreaseButtonSize => values.ResourceIncreaseButtonSize;
        public Vector2 BottomCenterAnchor => values.BottomCenterAnchor;
        public Vector2 ResourceCancelButtonPosition => values.ResourceCancelButtonPosition;
        public Vector2 ActionButtonSize => values.ActionButtonSize;
        public Vector2 CharacterOptionsPanelBaseSize => values.CharacterOptionsPanelBaseSize;
        public float CharacterOptionsRowHeight => values.CharacterOptionsRowHeight;
        public float CharacterOptionsPanelMinHeight => values.CharacterOptionsPanelMinHeight;
        public float CharacterOptionsPanelMaxHeight => values.CharacterOptionsPanelMaxHeight;
        public Vector2 CharacterPanelPosition => values.CharacterPanelPosition;
        public float CharacterOptionsDescriptionHeight => values.CharacterOptionsDescriptionHeight;
        public float CharacterOptionsScrollTop => values.CharacterOptionsScrollTop;
        public float CharacterOptionsScrollBottom => values.CharacterOptionsScrollBottom;
        public float CharacterOptionsScrollBottomWithCancel =>
            values.CharacterOptionsScrollBottomWithCancel;
        public EffectDialogRectLayout CharacterOptionsCancelButtonLayout =>
            values.CharacterOptionsCancelButtonLayout;
        public Vector2 CharacterResourceSalePanelSize => values.CharacterResourceSalePanelSize;
        public float CharacterResourceSaleRowStartY => values.CharacterResourceSaleRowStartY;
        public Vector2 SpecialActionPaymentPanelSize => values.SpecialActionPaymentPanelSize;
        public Vector2 SpecialActionPaymentPanelPosition => values.SpecialActionPaymentPanelPosition;
        public Vector2 SpecialActionMapPromptPanelSize => values.SpecialActionMapPromptPanelSize;
        public Vector2 ExtensionHubPanelSize => values.ExtensionHubPanelSize;
        public float ExtensionHubDescriptionHeight => values.ExtensionHubDescriptionHeight;
        public Vector2 ExtensionHubCardAnchor => values.ExtensionHubCardAnchor;
        public Vector2 ExtensionHubCardSize => values.ExtensionHubCardSize;
        public float ExtensionHubCardGap => values.ExtensionHubCardGap;
        public float ExtensionHubCardY => values.ExtensionHubCardY;
        public Vector2 FacilityCardOutlineDistance => values.FacilityCardOutlineDistance;
        public EffectDialogInsetLayout FacilityCardImageLayout => values.FacilityCardImageLayout;
        public EffectDialogInsetLayout FacilityCardFallbackLayout => values.FacilityCardFallbackLayout;
        public EffectDialogRectLayout ExtensionHubSkipButtonLayout => values.ExtensionHubSkipButtonLayout;
        public Vector2 OptionsPanelSize => values.OptionsPanelSize;
        public float OptionsScrollTop => values.OptionsScrollTop;
        public float OptionsScrollBottom => values.OptionsScrollBottom;
        public float OptionsScrollBottomWithBack => values.OptionsScrollBottomWithBack;
        public float CollapsibleOptionsExtraBottom => values.CollapsibleOptionsExtraBottom;
        public float CollapsibleOptionsExtraBottomWithBack => values.CollapsibleOptionsExtraBottomWithBack;
        public Vector2 OptionsBackButtonSize => values.OptionsBackButtonSize;
        public float OptionsBackButtonNormalY => values.OptionsBackButtonNormalY;
        public float OptionsBackButtonCollapsibleY => values.OptionsBackButtonCollapsibleY;
        public Vector2 ResourceAllocationPanelSize => values.ResourceAllocationPanelSize;
        public float ResourceLabelWidth => values.ResourceLabelWidth;
        public float ResourceLabelHeight => values.ResourceLabelHeight;
        public float ResourceLabelX => values.ResourceLabelX;
        public float ResourceDecreaseX => values.ResourceDecreaseX;
        public float ResourceValueX => values.ResourceValueX;
        public float ResourceIncreaseX => values.ResourceIncreaseX;
        public float MapPromptPanelWidth => values.MapPromptPanelWidth;
        public float MapPromptPanelHeight => values.MapPromptPanelHeight;
        public float CollapsibleMapPromptPanelHeight => values.CollapsibleMapPromptPanelHeight;
        public Vector2 MapPromptPanelPosition => values.MapPromptPanelPosition;
        public float MapPromptDescriptionHeight => values.MapPromptDescriptionHeight;
        public Vector2 MapPrimaryButtonSize => values.MapPrimaryButtonSize;
        public Vector2 MapBackButtonSize => values.MapBackButtonSize;
        public float MapPrimaryWithBackX => values.MapPrimaryWithBackX;
        public float MapBackWithPrimaryX => values.MapBackWithPrimaryX;
        public float MapButtonNormalY => values.MapButtonNormalY;
        public float MapButtonCollapsibleY => values.MapButtonCollapsibleY;

        public bool TryValidateConfiguration(out string reason)
        {
            if (values == null || string.IsNullOrEmpty(sourceManifestSha256) ||
                sourceManifestSha256.Length != 64)
            {
                reason = "EffectDialogLayoutProfile 缺少锁定 manifest 身份。";
                return false;
            }

            if (!PanelLayout.TryValidate(true, out reason) ||
                !TitleLayout.TryValidate(false, out reason) ||
                !DescriptionLayout.TryValidate(false, out reason) ||
                !CollapsedSummaryLayout.TryValidate(false, out reason) ||
                !CollapsedToggleLayout.TryValidate(true, out reason) ||
                !ExpandedToggleLayout.TryValidate(true, out reason) ||
                !CollapseIconLayout.TryValidate(true, out reason) ||
                !OptionScrollLayout.TryValidate(out reason) ||
                !ResourceSummaryLayout.TryValidate(false, out reason) ||
                !CharacterOptionsCancelButtonLayout.TryValidate(true, out reason) ||
                !FacilityCardImageLayout.TryValidate(out reason) ||
                !FacilityCardFallbackLayout.TryValidate(out reason) ||
                !ExtensionHubSkipButtonLayout.TryValidate(true, out reason))
            {
                return false;
            }

            if (!IsFinite(OverlayColor) || !IsFinite(CollapsedOverlayColor) ||
                !IsFinite(PanelOutlineDistance) || !IsNormalized(ResourceRowAnchor) ||
                !IsNormalized(BottomCenterAnchor) || !IsNormalized(ExtensionHubCardAnchor) ||
                !IsPositive(ResourceDecreaseButtonSize) || !IsPositive(ResourceValueSize) ||
                !IsPositive(ResourceIncreaseButtonSize) || !IsPositive(ActionButtonSize) ||
                !IsPositive(CharacterOptionsPanelBaseSize) ||
                !IsPositive(CharacterResourceSalePanelSize) ||
                !IsPositive(SpecialActionPaymentPanelSize) ||
                !IsPositive(SpecialActionMapPromptPanelSize) ||
                !IsPositive(ExtensionHubPanelSize) || !IsPositive(ExtensionHubCardSize) ||
                !IsPositive(OptionsPanelSize) || !IsPositive(OptionsBackButtonSize) ||
                !IsPositive(ResourceAllocationPanelSize) || !IsPositive(MapPrimaryButtonSize) ||
                !IsPositive(MapBackButtonSize) || !IsFinite(ResourceCancelButtonPosition) ||
                !IsFinite(CharacterPanelPosition) ||
                !IsFinite(CharacterResourceSaleRowStartY) ||
                !IsFinite(SpecialActionPaymentPanelPosition) ||
                !IsFinite(CharacterOptionsRowHeight) ||
                !IsFinite(CharacterOptionsPanelMinHeight) ||
                !IsFinite(CharacterOptionsPanelMaxHeight) ||
                !IsFinite(CharacterOptionsDescriptionHeight) ||
                !IsFinite(CharacterOptionsScrollTop) ||
                !IsFinite(CharacterOptionsScrollBottom) ||
                !IsFinite(CharacterOptionsScrollBottomWithCancel) ||
                !IsFinite(ResourceLabelWidth) || !IsFinite(ResourceLabelHeight) ||
                !IsFinite(ResourceLabelX) || !IsFinite(ResourceDecreaseX) ||
                !IsFinite(ResourceValueX) || !IsFinite(ResourceIncreaseX) ||
                !IsFinite(MapPromptDescriptionHeight) ||
                !IsFinite(FacilityCardOutlineDistance) || !IsFinite(MapPromptPanelPosition) ||
                CollapsedHeight <= 0f || ExtensionHubDescriptionHeight <= 0f ||
                CharacterOptionsRowHeight <= 0f ||
                CharacterOptionsPanelMinHeight <= 0f ||
                CharacterOptionsPanelMaxHeight < CharacterOptionsPanelMinHeight ||
                CharacterOptionsDescriptionHeight <= 0f ||
                CharacterOptionsScrollTop <= 0f ||
                CharacterOptionsScrollBottom < 0f ||
                CharacterOptionsScrollBottomWithCancel < CharacterOptionsScrollBottom ||
                ExtensionHubCardGap < 0f || OptionsScrollTop <= 0f ||
                OptionsScrollBottom < 0f || OptionsScrollBottomWithBack < 0f ||
                ResourceLabelWidth <= 0f || ResourceLabelHeight <= 0f ||
                MapPromptPanelWidth <= 0f || MapPromptPanelHeight <= 0f ||
                CollapsibleMapPromptPanelHeight <= 0f || MapPromptDescriptionHeight <= 0f)
            {
                reason = "EffectDialogLayoutProfile 包含越界或非有限固定布局值。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(string manifestSha256, EffectDialogLayoutValues configuredValues)
        {
            sourceManifestSha256 = manifestSha256 ?? string.Empty;
            values = configuredValues ?? new EffectDialogLayoutValues();
        }

        public bool MatchesValuesForEditor(EffectDialogLayoutValues expected)
        {
            return expected != null && string.Equals(
                JsonUtility.ToJson(values),
                JsonUtility.ToJson(expected),
                StringComparison.Ordinal);
        }
#endif

        internal static bool IsNormalized(Vector2 value)
        {
            return IsFinite(value) && value.x >= 0f && value.x <= 1f &&
                   value.y >= 0f && value.y <= 1f;
        }

        internal static bool IsPositive(Vector2 value)
        {
            return IsFinite(value) && value.x > 0f && value.y > 0f;
        }

        internal static bool IsFinite(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y);
        }

        private static bool IsFinite(Color value)
        {
            return IsFinite(value.r) && IsFinite(value.g) &&
                   IsFinite(value.b) && IsFinite(value.a);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
