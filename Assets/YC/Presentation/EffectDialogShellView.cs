using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.Presentation;

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
        [SerializeField] private RectTransform countedTitleRoot;
        [SerializeField] private Text countedTitlePrefix;
        [SerializeField] private Text countedTitleNumber;
        [SerializeField] private Text countedTitleSuffix;
        [SerializeField] private Text descriptionText;
        [SerializeField] private GameplayTextView descriptionDocument;
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
        [SerializeField] private bool useOptionScrollbarArtwork;
        [SerializeField] private RawImage optionScrollbarTrackArtwork;
        [SerializeField] private RawImage optionScrollbarThumbArtwork;
        private float? pendingScrollPosition;
        private Action dismissOnBackgroundClick;
        private readonly System.Collections.Generic.List<GameObject> dynamicRows = new System.Collections.Generic.List<GameObject>();
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
            if (useOptionScrollbarArtwork) Canvas.willRenderCanvases += RefreshOptionScrollbarArtwork;
            if (!pendingScrollPosition.HasValue) return;
            Canvas.willRenderCanvases -= ApplyPendingScroll;
            Canvas.willRenderCanvases += ApplyPendingScroll;
        }

        private void OnDisable()
        { Canvas.willRenderCanvases -= ApplyPendingScroll; Canvas.willRenderCanvases -= RefreshOptionScrollbarArtwork; }
        private void OnDestroy()
        { Canvas.willRenderCanvases -= ApplyPendingScroll; Canvas.willRenderCanvases -= RefreshOptionScrollbarArtwork; }

        private void RefreshOptionScrollbarArtwork()
        {
            if (!useOptionScrollbarArtwork || !isActiveAndEnabled) return;
            FitOptionScrollbarArtwork(optionScrollbarTrackArtwork);
            FitOptionScrollbarArtwork(optionScrollbarThumbArtwork);
        }

        private static void FitOptionScrollbarArtwork(RawImage artwork)
        {
            if (artwork == null || !(artwork.transform.parent is RectTransform target)) return;
            // 只让已裁切、旋转的装饰图适配原控件矩形，不改Scrollbar/Handle的布局或命中范围。
            var size = new Vector2(target.rect.height, target.rect.width);
            if (artwork.rectTransform.sizeDelta != size) artwork.rectTransform.sizeDelta = size;
            var targetGraphic = target.GetComponent<Image>();
            if (targetGraphic != null) artwork.color = targetGraphic.canvasRenderer.GetColor();
        }
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

        private string allocationInteractionId;
        private Button allocationConfirm;
        private IReadOnlyList<EffectDialogResourceRowView> allocationRows;
        private Func<bool> allocationIsCurrent;

        // 运行时绑定实际视图，不依赖用户可编辑的对象名称或文案。
        internal void BindResourceAllocation(string interactionId, Button confirm,
            IReadOnlyList<EffectDialogResourceRowView> rows, Func<bool> isCurrent)
        {
            allocationInteractionId = interactionId; allocationConfirm = confirm;
            allocationRows = rows; allocationIsCurrent = isCurrent;
        }

        internal bool TryGetResourceAllocation(string interactionId, out Button confirm,
            out IReadOnlyList<EffectDialogResourceRowView> rows)
        {
            confirm = null; rows = null;
            if (string.IsNullOrEmpty(interactionId) || allocationInteractionId != interactionId ||
                !isActiveAndEnabled || !gameObject.activeInHierarchy || allocationConfirm == null ||
                allocationIsCurrent?.Invoke() == false) return false;
            confirm = allocationConfirm; rows = allocationRows; return true;
        }

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
            bool IsCardOrControl(GameObject hit) => hit != null && (hit.GetComponentInParent<FacilityEffectCardView>() != null ||
                hit.GetComponentInParent<Selectable>() != null);
            if (IsCardOrControl(eventData.pointerPressRaycast.gameObject) ||
                IsCardOrControl(eventData.pointerCurrentRaycast.gameObject)) return;
            eventData.Use();
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
            if (useOptionScrollbarArtwork)
            {
                Require(optionScrollbarTrackArtwork, nameof(optionScrollbarTrackArtwork));
                Require(optionScrollbarThumbArtwork, nameof(optionScrollbarThumbArtwork));
            }
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
            ClearDynamicContent();
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
            if (descriptionDocument != null) descriptionDocument.ClearBinding();
            ResetCountedHeading();
            titleText.gameObject.name = titleName ?? string.Empty;
            titleText.text = title ?? string.Empty;
            titleText.fontStyle = FontStyle.Normal;

            descriptionText.gameObject.name = descriptionName ?? string.Empty;
            descriptionText.text = description ?? string.Empty;
            descriptionText.gameObject.SetActive(!string.IsNullOrEmpty(description));
            LayoutRebuilder.MarkLayoutForRebuild(expandedContent);
        }

        public bool TryRenderGameplayHeading(GameplayTextDocument document,
            Action<Component, GameplayTextDocument, bool> render, out string reason)
        {
            reason = descriptionDocument == null ? "正文GameplayTextView引用缺失。" :
                descriptionText == null || descriptionDocument.GetComponent<Text>() != descriptionText ? "正文GameplayTextView必须引用原Text同对象。" :
                render == null ? "正文渲染回调缺失。" : string.Empty;
            if (reason.Length > 0) return false;
            descriptionText.gameObject.SetActive(document != null && document.Runs.Count > 0);
            render(descriptionDocument, document, false);
            return true;
        }

        public void ConfigureCountedHeading(string format, int count, string description)
        {
            var title = format ?? string.Empty;
            try { title = string.Format(System.Globalization.CultureInfo.InvariantCulture, title, count); }
            catch (FormatException) { /* 自定义格式无效时保留原文，不生成替代静态标题。 */ }
            ConfigureHeading(title, description, 70f, "Title", "Description", 27);
            var placeholder = (format ?? string.Empty).IndexOf("{0}", StringComparison.Ordinal);
            if (placeholder < 0 || format.IndexOf('{') != placeholder || format.LastIndexOf('{') != placeholder ||
                format.IndexOf('}') != placeholder + 2 || format.LastIndexOf('}') != placeholder + 2)
            {
                Debug.LogWarning("牌堆计数标题需要一个明确的 {0} 占位符，当前保留普通标题显示。", this);
                return;
            }
            if (countedTitleRoot == null || countedTitlePrefix == null || countedTitleNumber == null || countedTitleSuffix == null)
            {
                Debug.LogWarning("牌堆计数标题缺少序列化的文字与数字控件，当前保留普通标题显示。", this);
                return;
            }
            countedTitlePrefix.text = format.Substring(0, placeholder);
            countedTitleNumber.text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            countedTitleSuffix.text = format.Substring(placeholder + 3);
            titleText.enabled = false;
            countedTitleRoot.gameObject.SetActive(true);
            LayoutRebuilder.MarkLayoutForRebuild(countedTitleRoot);
        }

        private void ResetCountedHeading()
        {
            if (titleText != null) titleText.enabled = true;
            if (countedTitleRoot != null) countedTitleRoot.gameObject.SetActive(false);
            if (countedTitlePrefix != null) countedTitlePrefix.text = string.Empty;
            if (countedTitleNumber != null) countedTitleNumber.text = string.Empty;
            if (countedTitleSuffix != null) countedTitleSuffix.text = string.Empty;
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
            dynamicRows.Add(row.gameObject);
            row.gameObject.SetActive(true);
            return row;
        }

        public EffectDialogOptionRowView CreateOptionRow(RectTransform parent, bool execution = false)
        {
            var row = Instantiate(execution ? executionRowTemplate : optionRowTemplate, parent == null ? OptionContent : parent, false);
            dynamicRows.Add(row.gameObject);
            row.gameObject.SetActive(true);
            return row;
        }

        public EffectDialogResourceRowView CreateResourceRow()
        {
            ConfigureResourceScroll();
            var row = Instantiate(resourceRowTemplate, OptionContent, false);
            dynamicRows.Add(row.gameObject);
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
            dynamicRows.Add(card.gameObject);
            card.gameObject.SetActive(true);
            return card;
        }

        public Text CreateDynamicText(Transform parent)
        {
            var text = Instantiate(descriptionText, parent, false);
            dynamicRows.Add(text.gameObject);
            return text;
        }

        public void ClearDynamicContent()
        {
            allocationInteractionId = null; allocationConfirm = null;
            allocationRows = null; allocationIsCurrent = null;
            ResetCountedHeading();
            foreach (var item in dynamicRows)
            {
                if (item == null) continue;
                item.SetActive(false);
                var row = item.GetComponent<UiEffectRowView>(); if (row != null) row.ClearBinding();
                foreach (var document in item.GetComponentsInChildren<GameplayTextView>(true)) document.ClearBinding();
                foreach (var text in item.GetComponentsInChildren<Text>(true)) text.text = string.Empty;
                if (UnityEngine.Application.isPlaying) Destroy(item); else DestroyImmediate(item);
            }
            dynamicRows.Clear();
            dismissOnBackgroundClick = null;
            if (titleText != null) titleText.text = string.Empty;
            if (descriptionDocument != null) descriptionDocument.ClearBinding();
            if (descriptionText != null) descriptionText.text = string.Empty;
            if (resourceSummaryText != null) resourceSummaryText.text = string.Empty;
            foreach (var action in actionButtons) if (action != null) { action.Button.onClick.RemoveAllListeners(); action.gameObject.SetActive(false); }
        }

    }
}
