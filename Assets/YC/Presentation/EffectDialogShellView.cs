using System;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>编辑器资产化的效果对话框固定壳引用；不承载游戏规则或回调决策。</summary>
    public sealed class EffectDialogShellView : MonoBehaviour
    {
        [SerializeField] private EffectDialogLayoutProfile layoutProfile;
        [SerializeField] private Canvas overlayCanvas;
        [SerializeField] private Image overlayImage;
        [SerializeField] private RectTransform panel;
        [SerializeField] private RectTransform expandedContent;
        [SerializeField] private Text titleText;
        [SerializeField] private Text descriptionText;
        [SerializeField] private EffectDialogDragHandle dragHandle;
        [SerializeField] private Text collapsedSummaryText;
        [SerializeField] private Button collapseButton;
        [SerializeField] private Text collapseButtonText;
        [SerializeField] private Image collapseButtonIcon;
        [SerializeField] private EffectDialogCollapsiblePanel collapsiblePanel;
        [SerializeField] private ScrollRect optionScroll;
        [SerializeField] private RectTransform optionContent;
        [SerializeField] private EffectDialogOptionRowView optionRowTemplate;
        [SerializeField] private EffectDialogResourceRowView resourceRowTemplate;
        [SerializeField] private Text resourceSummaryText;
        [SerializeField] private EffectDialogActionButtonView[] actionButtons;
        [SerializeField] private FacilityEffectCardView facilityCardTemplate;

        public Canvas OverlayCanvas => overlayCanvas;
        public EffectDialogLayoutProfile LayoutProfile => layoutProfile;
        public Image OverlayImage => overlayImage;
        public RectTransform Panel => panel;
        public RectTransform ExpandedContent => expandedContent;
        public Text TitleText => titleText;
        public Text DescriptionText => descriptionText;
        public EffectDialogDragHandle DragHandle => dragHandle;
        public Text CollapsedSummaryText => collapsedSummaryText;
        public Button CollapseButton => collapseButton;
        public Text CollapseButtonText => collapseButtonText;
        public Image CollapseButtonIcon => collapseButtonIcon;
        public EffectDialogCollapsiblePanel CollapsiblePanel => collapsiblePanel;
        public ScrollRect OptionScroll => optionScroll;
        public RectTransform OptionContent => optionContent;
        public EffectDialogOptionRowView OptionRowTemplate => optionRowTemplate;
        public EffectDialogResourceRowView ResourceRowTemplate => resourceRowTemplate;
        public Text ResourceSummaryText => resourceSummaryText;
        public FacilityEffectCardView FacilityCardTemplate => facilityCardTemplate;

        public bool TryValidateConfiguration(out string reason)
        {
            reason = string.Empty;
            if (layoutProfile == null ||
                !layoutProfile.TryValidateConfiguration(out reason) ||
                overlayCanvas == null || overlayImage == null || panel == null || expandedContent == null ||
                titleText == null || descriptionText == null || dragHandle == null ||
                collapsedSummaryText == null || collapseButton == null || collapseButtonText == null ||
                collapseButtonIcon == null || collapsiblePanel == null || optionScroll == null ||
                optionContent == null || optionRowTemplate == null || resourceRowTemplate == null ||
                resourceSummaryText == null || actionButtons == null || actionButtons.Length < 2 ||
                facilityCardTemplate == null)
            {
                reason = string.IsNullOrEmpty(reason)
                    ? "效果对话框固定壳的序列化引用不完整。"
                    : reason;
                return false;
            }

            if (!collapsiblePanel.TryValidateVisualConfiguration(out reason) ||
                !optionRowTemplate.TryValidateConfiguration(out reason) ||
                !resourceRowTemplate.TryValidateConfiguration(out reason) ||
                !facilityCardTemplate.TryValidateConfiguration(out reason))
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
            gameObject.name = overlayName ?? string.Empty;
            panel.gameObject.name = panelName ?? string.Empty;
            layoutProfile.PanelLayout.ApplyTo(panel);
            panel.sizeDelta = panelSize;
            panel.anchoredPosition = panelPosition;
            overlayCanvas.overrideSorting = true;
            overlayCanvas.sortingOrder = EffectDialogShell.SortingOrder;
            overlayImage.color = layoutProfile.OverlayColor;
            overlayImage.raycastTarget = blockBackgroundInput;
            expandedContent.gameObject.SetActive(true);
            titleText.gameObject.SetActive(true);
            descriptionText.gameObject.SetActive(true);
            dragHandle.enabled = true;
            dragHandle.Configure(panel);
            collapsedSummaryText.gameObject.SetActive(false);
            collapseButton.gameObject.SetActive(false);
            collapseButton.onClick.RemoveAllListeners();
            collapsiblePanel.Configure(null);
            optionScroll.gameObject.SetActive(false);
            optionRowTemplate.gameObject.SetActive(false);
            resourceRowTemplate.gameObject.SetActive(false);
            resourceSummaryText.gameObject.SetActive(false);
            facilityCardTemplate.gameObject.SetActive(false);
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
            int titleSize,
            bool enableDrag)
        {
            titleText.gameObject.name = titleName ?? string.Empty;
            titleText.text = title ?? string.Empty;
            titleText.fontSize = titleSize;
            titleText.fontStyle = FontStyle.Bold;
            titleText.color = UiTheme.GoldText;
            layoutProfile.TitleLayout.ApplyTo(titleText.rectTransform);

            descriptionText.gameObject.name = descriptionName ?? string.Empty;
            descriptionText.text = description ?? string.Empty;
            descriptionText.fontSize = 16;
            descriptionText.color = UiTheme.ValueText;
            descriptionText.alignment = TextAnchor.UpperLeft;
            var descriptionLayout = layoutProfile.DescriptionLayout;
            descriptionLayout.SizeDelta = new Vector2(
                descriptionLayout.SizeDelta.x,
                descriptionHeight);
            descriptionLayout.ApplyTo(descriptionText.rectTransform);
            dragHandle.enabled = enableDrag;
            dragHandle.Configure(panel);
        }

        public RectTransform ConfigureOptionScroll(string objectName, float bottom, float top)
        {
            optionScroll.gameObject.name = objectName ?? string.Empty;
            optionScroll.gameObject.SetActive(true);
            var rect = optionScroll.GetComponent<RectTransform>();
            var scrollLayout = layoutProfile.OptionScrollLayout;
            rect.anchorMin = scrollLayout.AnchorMin;
            rect.anchorMax = scrollLayout.AnchorMax;
            rect.pivot = scrollLayout.Pivot;
            rect.offsetMin = new Vector2(scrollLayout.OffsetMin.x, bottom);
            rect.offsetMax = new Vector2(scrollLayout.OffsetMax.x, -top);
            return optionContent;
        }

        public EffectDialogOptionRowView CreateOptionRow(RectTransform parent)
        {
            var row = Instantiate(optionRowTemplate, parent == null ? optionContent : parent, false);
            row.gameObject.SetActive(true);
            return row;
        }

        public EffectDialogResourceRowView CreateResourceRow()
        {
            var row = Instantiate(resourceRowTemplate, expandedContent, false);
            row.gameObject.SetActive(true);
            return row;
        }

        public EffectDialogActionButtonView AcquireActionButton(RectTransform parent)
        {
            for (var i = 0; i < actionButtons.Length; i++)
            {
                var action = actionButtons[i];
                if (action.gameObject.activeSelf)
                {
                    continue;
                }

                action.transform.SetParent(parent == null ? expandedContent : parent, false);
                action.Button.onClick.RemoveAllListeners();
                action.gameObject.SetActive(true);
                return action;
            }

            throw new InvalidOperationException("效果对话框没有足够的固定动作按钮槽位。");
        }

        public FacilityEffectCardView CreateFacilityCard()
        {
            var card = Instantiate(facilityCardTemplate, expandedContent, false);
            card.gameObject.SetActive(true);
            return card;
        }

    }
}
