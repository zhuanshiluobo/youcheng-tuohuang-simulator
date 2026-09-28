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
        public Vector2 OptionsBackButtonSize;
        public float OptionsBackButtonNormalY;

        public Vector2 ResourceAllocationPanelSize;
        public float ResourceLabelWidth;
        public float ResourceLabelHeight;
        public float ResourceLabelX;
        public float ResourceDecreaseX;
        public float ResourceValueX;
        public float ResourceIncreaseX;

        public float MapPromptPanelWidth;
        public float MapPromptPanelHeight;
        public Vector2 MapPromptPanelPosition;
        public float MapPromptDescriptionHeight;
        public Vector2 MapPrimaryButtonSize;
        public Vector2 MapBackButtonSize;
        public float MapPrimaryWithBackX;
        public float MapBackWithPrimaryX;
        public float MapButtonNormalY;
    }

    [CreateAssetMenu(
        fileName = "EffectDialogLayoutProfile",
        menuName = "YC/Presentation/Effect Dialog Layout Profile")]
    public sealed class EffectDialogLayoutProfile : ScriptableObject
    {
        [SerializeField] private string characterUseTitle = "角色牌使用";
        public string CharacterUseTitle => characterUseTitle;

        [SerializeField] private string characterUseDescription = "请选择本次使用的角色牌效果。";
        public string CharacterUseDescription => characterUseDescription;

        [SerializeField] private string characterSecondEffectDescription = "第一个效果已结算，可继续使用第二个效果，或结束本次使用。";
        public string CharacterSecondEffectDescription => characterSecondEffectDescription;

        [SerializeField] private string characterStrategyLabel = "策略";
        public string CharacterStrategyLabel => characterStrategyLabel;

        [SerializeField] private string characterTacticLabel = "计谋";
        public string CharacterTacticLabel => characterTacticLabel;

        [SerializeField] private string characterFinishLabel = "结束使用";
        public string CharacterFinishLabel => characterFinishLabel;

        [SerializeField] private string discardListTitleFormat = "弃牌（{0}）";
        public string DiscardListTitleFormat => discardListTitleFormat;

        [SerializeField] private string discardListDescription = "此页面仅供查看，不能使用或移动弃牌。";
        public string DiscardListDescription => discardListDescription;

        [SerializeField] private string readOnlyCloseLabel = "关闭";
        public string ReadOnlyCloseLabel => readOnlyCloseLabel;

        [Header("通用选择与出售文案")]
        [SerializeField] private string selectionConfirmLabel = "确认选择";
        [SerializeField] private string selectionCancelLabel = "放弃选择";
        [SerializeField] private string selectionReturnLabel = "返回选择";
        [SerializeField] private string selectionSummaryFormat = "已选 {0} · 最少 {1} / 最多 {2}";
        [SerializeField] private string selectionEmptyText = "当前没有可选内容";
        [SerializeField] private string saleConfirmLabel = "确认出售";
        [SerializeField] private string saleFinishLabel = "结束出售";
        [SerializeField] private string saleDiscardDraftLabel = "放弃并结束";
        [SerializeField] private string saleSummaryFormat = "预计获得 {0} 金券";
        [SerializeField] private string saleReceiptSummaryFormat = "最近出售实际获得 {0} 金券";
        [SerializeField] private string saleInventoryFormat = "{0}（库存 {1}，单价 {2} 金券）";
        public string SelectionConfirmLabel => selectionConfirmLabel;
        public string SelectionCancelLabel => selectionCancelLabel;
        [SerializeField] private string specialActionTitle = "选择执行特殊行动";
        [SerializeField] private string specialActionDescription = "选择已解锁的特殊行动，再确认执行。";
        public string SpecialActionTitle => specialActionTitle;
        public string SpecialActionDescription => specialActionDescription;
        public string SelectionReturnLabel => selectionReturnLabel;
        public string SelectionSummaryFormat => selectionSummaryFormat;
        public string SelectionEmptyText => selectionEmptyText;
        public string SaleConfirmLabel => saleConfirmLabel;
        public string SaleFinishLabel => saleFinishLabel;
        public string SaleDiscardDraftLabel => saleDiscardDraftLabel;
        public string SaleSummaryFormat => saleSummaryFormat;
        public string SaleReceiptSummaryFormat => saleReceiptSummaryFormat;
        public string SaleInventoryFormat => saleInventoryFormat;
        [SerializeField] private string characterSelectionTitle = "角色能力";
        [SerializeField] private string characterCoverSelectionTitle = "盖放角色牌";
        [SerializeField] private string characterCoverSelectionDescription = "选择一张手牌并确认。";
        [SerializeField] private string characterCoverSelectionHint = "单击选择或取消；长按、双击或右键查看角色牌";
        [SerializeField] private string facilitySelectionTitle = "设施效果";
        [SerializeField] private string buildFacilitySelectionTitle = "设施建设";
        [SerializeField] private string buildFacilitySelectionDescription = "选择设施、城市目标和支付方式。";
        [SerializeField] private string buildSlotSelectionTitle = "选择建设位置";
        [SerializeField] private string buildPaymentSelectionTitle = "选择支付方式";
        [SerializeField] private string buildConfirmationTitle = "确认建设报价";
        [SerializeField] private string buildConfirmLabel = "确认建设";
        [SerializeField] private string buildBackLabel = "返回修改";
        [SerializeField] private string buildCancelLabel = "取消建设";
        [SerializeField] private string buildSlotLabelFormat = "城市槽位 {0}";
        [SerializeField] private string buildUnavailableReasonFormat = "不可选：{0}";
        [SerializeField] private string buildPaymentResourcesLabel = "使用资源支付";
        [SerializeField] private string buildPaymentGoldLabel = "使用金券支付";
        [SerializeField] private string buildResourceCostFormat = "源岩 {0} · 源石碎片 {1} · 异铁 {2} · 至纯源石 {3}";
        [SerializeField] private string buildPaymentQuoteFormat = "原价资源：{0}\n当前费用：{1}\n金券费用：{2}";
        [SerializeField] private string buildQuoteValidLabel = "报价有效";
        [SerializeField] private string buildQuotePendingLabel = "等待正式报价";
        [SerializeField] private string saleTitle = "资源出售";
        [SerializeField] private string saleDescription = "选择要出售的资源数量，然后统一确认结算。";
        [SerializeField] private string resourceAllocationTitle = "设施资源效果";
        [SerializeField] private string resourceAllocationDescriptionFormat = "分配总计 {0} 点资源。";
        [SerializeField] private string optionalEffectTitle = "可选效果";
        [SerializeField] private string executeLabel = "执行";
        [SerializeField] private string declineLabel = "放弃";
        public string CharacterSelectionTitle => characterSelectionTitle;
        public string CharacterCoverSelectionTitle => characterCoverSelectionTitle;
        public string CharacterCoverSelectionDescription => characterCoverSelectionDescription;
        public string CharacterCoverSelectionHint => characterCoverSelectionHint;
        public string FacilitySelectionTitle => facilitySelectionTitle;
        public string BuildFacilitySelectionTitle => buildFacilitySelectionTitle;
        public string BuildFacilitySelectionDescription => buildFacilitySelectionDescription;
        public string BuildSlotSelectionTitle => buildSlotSelectionTitle;
        public string BuildPaymentSelectionTitle => buildPaymentSelectionTitle;
        public string BuildConfirmationTitle => buildConfirmationTitle;
        public string BuildConfirmLabel => buildConfirmLabel;
        public string BuildBackLabel => buildBackLabel;
        public string BuildCancelLabel => buildCancelLabel;
        public string BuildSlotLabelFormat => buildSlotLabelFormat;
        public string BuildUnavailableReasonFormat => buildUnavailableReasonFormat;
        public string BuildPaymentResourcesLabel => buildPaymentResourcesLabel;
        public string BuildPaymentGoldLabel => buildPaymentGoldLabel;
        public string BuildPaymentQuoteFormat => buildPaymentQuoteFormat;
        public string BuildResourceCostFormat => buildResourceCostFormat;
        public string BuildQuoteValidLabel => buildQuoteValidLabel;
        public string BuildQuotePendingLabel => buildQuotePendingLabel;
        [SerializeField] private string buildSelectionPrompt = "建设：选择设施、城市目标和支付方式，再确认建设。";
        public string BuildSelectionPrompt => buildSelectionPrompt;
        public string SaleTitle => saleTitle;
        public string SaleDescription => saleDescription;
        public string ResourceAllocationTitle => resourceAllocationTitle;
        public string ResourceAllocationDescriptionFormat => resourceAllocationDescriptionFormat;
        public string OptionalEffectTitle => optionalEffectTitle;
        public string ExecuteLabel => executeLabel;
        public string DeclineLabel => declineLabel;
        [Header("通用选择自适应尺寸")]
        [SerializeField] private Vector2 selectionPanelSize = new Vector2(1320f, 820f);
        [SerializeField] private Vector2 selectionCardSize = new Vector2(240f, 358f);
        [SerializeField] private float selectionMinimumCardWidth = 180f;
        [SerializeField] private Vector2 salePanelSize = new Vector2(980f, 720f);
        public Vector2 SelectionPanelSize => selectionPanelSize;
        public Vector2 SelectionCardSize => selectionCardSize;
        public float SelectionMinimumCardWidth => selectionMinimumCardWidth;
        public Vector2 SalePanelSize => salePanelSize;
        [SerializeField] private string sourceManifestSha256 = string.Empty;
        [SerializeField] private EffectDialogLayoutValues values = new EffectDialogLayoutValues();

        public string SourceManifestSha256 => sourceManifestSha256;
        public Color OverlayColor => values.OverlayColor;
        public EffectDialogRectLayout PanelLayout => values.PanelLayout;
        public Vector2 PanelOutlineDistance => values.PanelOutlineDistance;
        public EffectDialogRectLayout TitleLayout => values.TitleLayout;
        public EffectDialogRectLayout DescriptionLayout => values.DescriptionLayout;
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
        public Vector2 OptionsBackButtonSize => values.OptionsBackButtonSize;
        public float OptionsBackButtonNormalY => values.OptionsBackButtonNormalY;
        public Vector2 ResourceAllocationPanelSize => values.ResourceAllocationPanelSize;
        public float ResourceLabelWidth => values.ResourceLabelWidth;
        public float ResourceLabelHeight => values.ResourceLabelHeight;
        public float ResourceLabelX => values.ResourceLabelX;
        public float ResourceDecreaseX => values.ResourceDecreaseX;
        public float ResourceValueX => values.ResourceValueX;
        public float ResourceIncreaseX => values.ResourceIncreaseX;
        public float MapPromptPanelWidth => values.MapPromptPanelWidth;
        public float MapPromptPanelHeight => values.MapPromptPanelHeight;
        public Vector2 MapPromptPanelPosition => values.MapPromptPanelPosition;
        public float MapPromptDescriptionHeight => values.MapPromptDescriptionHeight;
        public Vector2 MapPrimaryButtonSize => values.MapPrimaryButtonSize;
        public Vector2 MapBackButtonSize => values.MapBackButtonSize;
        public float MapPrimaryWithBackX => values.MapPrimaryWithBackX;
        public float MapBackWithPrimaryX => values.MapBackWithPrimaryX;
        public float MapButtonNormalY => values.MapButtonNormalY;

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
                !OptionScrollLayout.TryValidate(out reason) ||
                !ResourceSummaryLayout.TryValidate(false, out reason) ||
                !CharacterOptionsCancelButtonLayout.TryValidate(true, out reason) ||
                !FacilityCardImageLayout.TryValidate(out reason) ||
                !FacilityCardFallbackLayout.TryValidate(out reason) ||
                !ExtensionHubSkipButtonLayout.TryValidate(true, out reason))
            {
                return false;
            }

            if (!IsFinite(OverlayColor) ||
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
                ExtensionHubDescriptionHeight <= 0f ||
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
                MapPromptDescriptionHeight <= 0f)
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
