using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>编辑器资产化的效果对话框固定壳引用；不承载游戏规则或回调决策。</summary>
    public sealed class EffectDialogShellView : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField] private EffectDialogLayoutProfile layoutProfile;
        [SerializeField] private Canvas overlayCanvas;
        [SerializeField] private Image overlayImage;
        [SerializeField] private RectTransform panel;
        [SerializeField] private RectTransform expandedContent;
        [SerializeField] private Text titleText;
        [SerializeField] private Text descriptionText;
        [SerializeField] private ScrollRect optionScroll;
        [SerializeField] private RectTransform optionContent;
        [SerializeField] private EffectDialogOptionRowView optionRowTemplate;
        [SerializeField] private EffectDialogOptionRowView executionRowTemplate;
        [SerializeField] private EffectDialogResourceRowView resourceRowTemplate;
        [SerializeField] private Text resourceSummaryText;
        [SerializeField] private EffectDialogActionButtonView[] actionButtons;
        [SerializeField] private FacilityEffectCardView facilityCardTemplate;
        [SerializeField] private RectTransform footer;
        [SerializeField] private UiWindowSizeInput boundedLayout;
        [SerializeField] private UiEffectRowView effectRowTemplate;
        [SerializeField] private UiCardCollectionLayout cardGrid;
        [SerializeField] private CardPickerRowLayout cardPickerRow;
        [SerializeField] private Text cardPickerHint;
        [SerializeField] private string cardPickerSelectionHint;
        [SerializeField] private string cardPickerReadOnlyHint;
        private float? pendingScrollPosition;
        private Action dismissOnBackgroundClick;
        public bool IsCardPicker => cardPickerRow != null;
        // 保留既有“1 = 列表开头”的草稿协议；卡牌页将其映射到横向滚动。
        public float ScrollPosition => pendingScrollPosition ?? (optionScroll == null ? 1f :
            IsCardPicker ? 1f - optionScroll.horizontalNormalizedPosition : optionScroll.verticalNormalizedPosition);

        public void RestoreScrollPosition(float position)
        {
            pendingScrollPosition = Mathf.Clamp01(position);
            Canvas.willRenderCanvases -= ApplyPendingScroll;
            if (isActiveAndEnabled) Canvas.willRenderCanvases += ApplyPendingScroll;
        }

        private void OnEnable()
        {
            if (!pendingScrollPosition.HasValue) return;
            Canvas.willRenderCanvases -= ApplyPendingScroll;
            Canvas.willRenderCanvases += ApplyPendingScroll;
        }

        private void OnDisable() { Canvas.willRenderCanvases -= ApplyPendingScroll; }
        private void OnDestroy() { Canvas.willRenderCanvases -= ApplyPendingScroll; }
        private void ApplyPendingScroll()
        {
            if (this == null)
            {
                Canvas.willRenderCanvases -= ApplyPendingScroll;
                return;
            }
            if (!isActiveAndEnabled || !pendingScrollPosition.HasValue || optionScroll == null) return;
            LayoutRebuilder.ForceRebuildLayoutImmediate(expandedContent);
            if (IsCardPicker) optionScroll.horizontalNormalizedPosition = 1f - pendingScrollPosition.Value;
            else optionScroll.verticalNormalizedPosition = pendingScrollPosition.Value;
            pendingScrollPosition = null;
            Canvas.willRenderCanvases -= ApplyPendingScroll;
        }

        public Canvas OverlayCanvas => overlayCanvas;
        public EffectDialogLayoutProfile LayoutProfile => layoutProfile;
        public Image OverlayImage => overlayImage;
        public RectTransform Panel => panel;
        public RectTransform ExpandedContent => expandedContent;
        public Text TitleText => titleText;
        public Text DescriptionText => descriptionText;
        public ScrollRect OptionScroll => optionScroll;
        public RectTransform OptionContent => IsCardPicker || cardGrid == null ? optionContent : cardGrid.Content;
        public EffectDialogOptionRowView OptionRowTemplate => optionRowTemplate;
        public EffectDialogResourceRowView ResourceRowTemplate => resourceRowTemplate;
        public Text ResourceSummaryText => resourceSummaryText;
        public FacilityEffectCardView FacilityCardTemplate => facilityCardTemplate;

        public void ConfigureSelectionMode(bool readOnly, string hintOverride = null)
        {
            if (IsCardPicker && cardPickerHint != null)
            {
                cardPickerHint.text = hintOverride ?? (readOnly ? cardPickerReadOnlyHint : cardPickerSelectionHint);
                // 提示与动作按钮共用页脚；只读列表没有动作按钮，也必须显示提示。
                if (footer != null) footer.gameObject.SetActive(true);
            }
        }

        public void ConfigureBackgroundDismiss(Action dismiss) => dismissOnBackgroundClick = dismiss;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (dismissOnBackgroundClick == null || eventData == null ||
                eventData.button != PointerEventData.InputButton.Left || eventData.dragging) return;
            var threshold = EventSystem.current == null ? 10 : EventSystem.current.pixelDragThreshold;
            if ((eventData.position - eventData.pressPosition).sqrMagnitude > threshold * threshold) return;
            var hit = eventData.pointerPressRaycast.gameObject ?? eventData.pointerCurrentRaycast.gameObject;
            if (hit != null && (hit.GetComponentInParent<FacilityEffectCardView>() != null ||
                hit.GetComponentInParent<Selectable>() != null)) return;
            dismissOnBackgroundClick();
        }

        public bool TryValidateConfiguration(out string reason)
        {
            var missing = new System.Collections.Generic.List<string>();
            void Require(UnityEngine.Object value, string field)
            {
                if (value == null) missing.Add(field);
            }
            Require(layoutProfile, nameof(layoutProfile));
            Require(overlayCanvas, nameof(overlayCanvas));
            Require(overlayImage, nameof(overlayImage));
            Require(panel, nameof(panel));
            Require(expandedContent, nameof(expandedContent));
            Require(titleText, nameof(titleText));
            Require(descriptionText, nameof(descriptionText));
            Require(optionScroll, nameof(optionScroll));
            Require(optionContent, nameof(optionContent));
            Require(resourceSummaryText, nameof(resourceSummaryText));
            Require(facilityCardTemplate, nameof(facilityCardTemplate));
            Require(footer, nameof(footer));
            if (actionButtons == null || actionButtons.Length < 2) missing.Add(nameof(actionButtons));
            if (!IsCardPicker)
            {
                Require(optionRowTemplate, nameof(optionRowTemplate));
                Require(resourceRowTemplate, nameof(resourceRowTemplate));
                Require(boundedLayout, nameof(boundedLayout));
                Require(effectRowTemplate, nameof(effectRowTemplate));
                Require(cardGrid, nameof(cardGrid));
                // 卡牌页没有普通选项行；单独提示其模式组件断绑，避免误报为普通弹窗。
                if (cardPickerHint != null) Require(cardPickerRow, nameof(cardPickerRow));
            }
            if (missing.Count > 0)
            {
                reason = "效果对话框“" + name + "”缺少序列化引用：" + string.Join("、", missing) + "。";
                return false;
            }
            if (!layoutProfile.TryValidateConfiguration(out reason))
            {
                reason = "效果对话框“" + name + "”的布局配置无效：" + reason;
                return false;
            }

            if (!facilityCardTemplate.TryValidateConfiguration(out reason) ||
                (IsCardPicker && !cardPickerRow.TryValidateConfiguration(out reason)) ||
                (!IsCardPicker && (!optionRowTemplate.TryValidateConfiguration(out reason) ||
                !resourceRowTemplate.TryValidateConfiguration(out reason))))
            {
                return false;
            }

            for (var i = 0; i < actionButtons.Length; i++)
            {
                if (actionButtons[i] == null || !actionButtons[i].TryValidateConfiguration(out reason))
                {
                    reason = "效果对话框动作按钮数组存在无效引用。";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        public void PrepareForUse(string overlayName, string panelName, Vector2 panelSize, Vector2 panelPosition,
            bool blockBackgroundInput)
        {
            dismissOnBackgroundClick = null;
            gameObject.name = overlayName ?? string.Empty;
            panel.gameObject.name = panelName ?? string.Empty;
            // 窗口尺寸由实际预制体决定。旧调用方的候选数量不能缩小窗口、裁掉支付项。
            overlayCanvas.overrideSorting = true;
            overlayCanvas.sortingOrder = EffectDialogShell.SortingOrder;
            overlayImage.color = layoutProfile.OverlayColor;
            overlayImage.raycastTarget = blockBackgroundInput;
            expandedContent.gameObject.SetActive(true);
            titleText.gameObject.SetActive(true);
            descriptionText.gameObject.SetActive(true);
            optionScroll.gameObject.SetActive(false);
            if (optionRowTemplate != null) optionRowTemplate.gameObject.SetActive(false);
            if (executionRowTemplate != null) executionRowTemplate.gameObject.SetActive(false);
            if (resourceRowTemplate != null) resourceRowTemplate.gameObject.SetActive(false);
            resourceSummaryText.gameObject.SetActive(false);
            footer.gameObject.SetActive(false);
            if (facilityCardTemplate.gameObject.scene.IsValid()) facilityCardTemplate.gameObject.SetActive(false);
            for (var i = 0; i < actionButtons.Length; i++)
            {
                actionButtons[i].Button.onClick.RemoveAllListeners();
                actionButtons[i].gameObject.SetActive(false);
            }
        }

        public void ConfigureHeading(
            string title,
            string description,
            float descriptionHeight,
            string titleName,
            string descriptionName,
            int titleSize)
        {
            titleText.gameObject.name = titleName ?? string.Empty;
            titleText.text = title ?? string.Empty;
            titleText.fontStyle = FontStyle.Normal;

            descriptionText.gameObject.name = descriptionName ?? string.Empty;
            descriptionText.text = description ?? string.Empty;
            descriptionText.gameObject.SetActive(!string.IsNullOrEmpty(description));
            LayoutRebuilder.MarkLayoutForRebuild(expandedContent);
        }

        public RectTransform ConfigureOptionScroll(string objectName, float bottom, float top)
        {
            optionScroll.gameObject.name = objectName ?? string.Empty;
            optionScroll.gameObject.SetActive(true);
            if (cardGrid != null) cardGrid.UseList();
            LayoutRebuilder.MarkLayoutForRebuild(expandedContent);
            return OptionContent;
        }

        public RectTransform ConfigureResourceScroll() => ConfigureOptionScroll("Resource Scroll", 0, 0);

        public RectTransform ConfigureCardScroll(Vector2 preferredCardSize, float minimumCardWidth = 140f)
        {
            optionScroll.gameObject.SetActive(true);
            if (IsCardPicker)
            {
                optionScroll.horizontal = true;
                optionScroll.vertical = false;
                LayoutRebuilder.MarkLayoutForRebuild(optionContent);
            }
            else if (cardGrid != null)
            {
                cardGrid.Configure(preferredCardSize, minimumCardWidth);
                cardGrid.enabled = true;
            }
            LayoutRebuilder.MarkLayoutForRebuild(expandedContent);
            return OptionContent;
        }

        public UiEffectRowView CreateEffectRow()
        {
            ConfigureOptionScroll("Option Scroll", 0, 0);
            var row = Instantiate(effectRowTemplate, OptionContent, false);
            row.gameObject.SetActive(true);
            return row;
        }

        public EffectDialogOptionRowView CreateOptionRow(RectTransform parent, bool execution = false)
        {
            var row = Instantiate(execution ? executionRowTemplate : optionRowTemplate, parent == null ? OptionContent : parent, false);
            row.gameObject.SetActive(true);
            return row;
        }

        public EffectDialogResourceRowView CreateResourceRow()
        {
            ConfigureResourceScroll();
            var row = Instantiate(resourceRowTemplate, OptionContent, false);
            row.gameObject.SetActive(true);
            return row;
        }

        public EffectDialogActionButtonView AcquireActionButton(RectTransform parent, bool primary = true)
        {
            for (var i = primary ? 0 : 1; i < actionButtons.Length; i++)
            {
                var action = actionButtons[i];
                if (action.gameObject.activeSelf)
                {
                    continue;
                }

                if (!IsCardPicker) action.transform.SetParent(footer == null ? expandedContent : footer, false);
                if (footer != null) footer.gameObject.SetActive(true);
                action.Button.onClick.RemoveAllListeners();
                action.gameObject.SetActive(true);
                return action;
            }

            throw new InvalidOperationException("效果对话框没有足够的固定动作按钮槽位。");
        }

        public FacilityEffectCardView CreateFacilityCard()
        {
            var card = Instantiate(facilityCardTemplate, OptionContent, false);
            card.gameObject.SetActive(true);
            return card;
        }

    }
}
