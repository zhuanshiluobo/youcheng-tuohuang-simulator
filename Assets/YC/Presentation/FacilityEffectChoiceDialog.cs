using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    internal sealed class FacilityEffectCardOption
    {
        public FacilityEffectCardOption(string facilityId, string label, bool available)
        {
            FacilityId = facilityId ?? string.Empty;
            Label = label ?? string.Empty;
            Available = available;
        }

        public string FacilityId { get; private set; }

        public string Label { get; private set; }

        public bool Available { get; private set; }
    }

    /// <summary>设施入场待选专用弹窗；不读取或修改游戏规则状态。</summary>
    internal sealed class FacilityEffectChoiceDialog
    {
        private readonly RectTransform canvas;
        private readonly EffectDialogShell shell;
        private readonly CardVisualCatalog cardVisualCatalog;
        private readonly EffectDialogLayoutProfile layoutProfile;
        private EffectDialogCollapsiblePanel collapsiblePanel;
        private RectTransform facilityCardDragGhost;
        private ZoomableImageViewerController facilityCardImageViewer;

        internal FacilityEffectChoiceDialog(
            GameplayDialogRegistry dialogRegistry,
            RectTransform configuredCanvas)
        {
            canvas = configuredCanvas ?? throw new ArgumentNullException(nameof(configuredCanvas));
            if (dialogRegistry == null) throw new ArgumentNullException(nameof(dialogRegistry));
            cardVisualCatalog = dialogRegistry.CardVisualCatalog;
            layoutProfile = dialogRegistry.EffectDialogShellPrefab == null
                ? null
                : dialogRegistry.EffectDialogShellPrefab.LayoutProfile;
            var layoutReason = string.Empty;
            if (layoutProfile == null || !layoutProfile.TryValidateConfiguration(out layoutReason))
            {
                throw new InvalidOperationException(
                    "FacilityEffectChoiceDialog 缺少有效的显式布局 Profile：" + layoutReason);
            }
            shell = new EffectDialogShell(
                dialogRegistry);
        }

        public bool IsShowing
        {
            get { return shell.IsShowing; }
        }

        public void ShowOptions(
            string title,
            string description,
            IReadOnlyList<EffectDialogOption> options,
            Action back = null)
        {
            ShowOptionsCore(title, description, options, back, string.Empty, false);
        }

        public void ShowCollapsibleOptions(
            string title,
            string description,
            string summary,
            IReadOnlyList<EffectDialogOption> options,
            Action back = null,
            string backLabel = null)
        {
            ShowOptionsCore(title, description, options, back, summary, false, backLabel);
        }

        public void ShowExtensionHubOptions(
            IReadOnlyList<FacilityEffectCardOption> options,
            Action beginDrag,
            Action<string, int> drop,
            Action cancelDrag,
            Action skip)
        {
            var panel = Rebuild(
                layoutProfile.ExtensionHubPanelSize,
                Vector2.zero,
                "\u5ef6\u4f38\u67a2\u7ebd\uff1a\u62d6\u52a8\u5361\u7247\u5230\u57ce\u5e02\u9762\u677f\u7a7a\u69fd\u4f4d\u5efa\u8bbe\u3002",
                false);
            AddHeading(
                panel,
                "延伸枢纽",
                "选择一个尚未使用的延伸枢纽，拖动到城市面板空槽位进行建设。",
                layoutProfile.ExtensionHubDescriptionHeight);

            var cardWidth = layoutProfile.ExtensionHubCardSize.x;
            var cardHeight = layoutProfile.ExtensionHubCardSize.y;
            var cardGap = layoutProfile.ExtensionHubCardGap;
            var optionCount = options == null ? 0 : options.Count;
            var rowWidth = optionCount * cardWidth + Mathf.Max(0, optionCount - 1) * cardGap;
            var rowStartX = -rowWidth * 0.5f + cardWidth * 0.5f;
            for (var i = 0; i < optionCount; i++)
            {
                var option = options[i];
                var texture = cardVisualCatalog.GetFacility(option.FacilityId);
                var card = EffectDialogShell.CreateFacilityCard(panel);
                card.gameObject.name = "Extension Hub Card " + option.FacilityId;
                var cardRect = card.CardRect;
                SetRect(
                    cardRect,
                    layoutProfile.ExtensionHubCardAnchor,
                    layoutProfile.ExtensionHubCardAnchor,
                    layoutProfile.ExtensionHubCardSize,
                    new Vector2(
                        rowStartX + i * (cardWidth + cardGap),
                        layoutProfile.ExtensionHubCardY));

                card.Background.color = UiTheme.ScrollBackground;
                card.Outline.effectColor = option.Available ? UiTheme.GoldOutline : UiTheme.GoldOutlineThin;
                card.Outline.effectDistance = layoutProfile.FacilityCardOutlineDistance;
                var button = card.Button;
                button.onClick.RemoveAllListeners();
                button.interactable = true;
                var canvasGroup = card.CanvasGroup;
                canvasGroup.alpha = option.Available ? 1f : 0.32f;
                canvasGroup.interactable = true;
                canvasGroup.blocksRaycasts = true;

                var imageRect = card.CardImage.rectTransform;
                layoutProfile.FacilityCardImageLayout.ApplyTo(imageRect);
                card.CardImage.texture = texture;
                card.CardImage.color = texture == null ? Color.clear : Color.white;
                card.CardImage.raycastTarget = false;

                var label = card.FallbackLabel;
                label.gameObject.name = "Shared Name";
                label.text = option.Label;
                label.fontSize = 18;
                label.alignment = TextAnchor.MiddleCenter;
                label.fontStyle = FontStyle.Bold;
                label.color = UiTheme.GoldText;
                label.gameObject.SetActive(texture == null);
                layoutProfile.FacilityCardFallbackLayout.ApplyTo(label.rectTransform);

                var facilityId = option.FacilityId;
                var cardName = option.Label;
                var available = option.Available;
                var interaction = card.PointerInteraction;
                interaction.ConfigureClick(
                    button,
                    () => CardImagePreviewUtility.Open(
                        ref facilityCardImageViewer,
                        canvas,
                        "Extension Hub Card Image Viewer",
                        "Extension Hub Card",
                        cardName,
                        texture),
                    null);
                interaction.ConfigureDrag(
                    () => available,
                    eventData =>
                    {
                        BeginFacilityCardDrag(cardRect, texture, cardName, card.FallbackLabel.font, eventData);
                        beginDrag?.Invoke();
                    },
                    MoveFacilityCardDrag,
                    eventData =>
                    {
                        var slotIndex = FacilityCardDragUtility.ResolveCityBoardSlotIndex(eventData);
                        DestroyFacilityCardDragGhost();
                        drop?.Invoke(facilityId, slotIndex);
                    },
                    () =>
                    {
                        DestroyFacilityCardDragGhost();
                        cancelDrag?.Invoke();
                    });
            }

            var skipButton = CreateButton(panel, "Skip Extension Hub", "不建设", 18);
            layoutProfile.ExtensionHubSkipButtonLayout.ApplyTo(
                skipButton.GetComponent<RectTransform>());
            BindOnce(skipButton, skip);
        }

        private void ShowOptionsCore(
            string title,
            string description,
            IReadOnlyList<EffectDialogOption> options,
            Action back,
            string summary,
            bool startCollapsed,
            string backLabel = null)
        {
            var isCollapsible = !string.IsNullOrEmpty(summary);
            var panel = Rebuild(layoutProfile.OptionsPanelSize, Vector2.zero, summary, startCollapsed);
            AddHeading(panel, title, description);

            var scrollBottom = back == null
                ? layoutProfile.OptionsScrollBottom
                : layoutProfile.OptionsScrollBottomWithBack;
            if (isCollapsible)
            {
                scrollBottom += back == null
                    ? layoutProfile.CollapsibleOptionsExtraBottom
                    : layoutProfile.CollapsibleOptionsExtraBottomWithBack;
            }

            var content = EffectDialogShell.AddOptionScroll(
                panel,
                "Options Scroll",
                scrollBottom,
                layoutProfile.OptionsScrollTop);
            EffectDialogShell.AddOptions(content, options, "Option ", Hide);

            if (back != null)
            {
                var backButton = CreateButton(
                    panel,
                    "Back",
                    string.IsNullOrEmpty(backLabel) ? "\u8fd4\u56de" : backLabel,
                    17);
                SetRect(
                    backButton.GetComponent<RectTransform>(),
                    layoutProfile.BottomCenterAnchor,
                    layoutProfile.BottomCenterAnchor,
                    layoutProfile.OptionsBackButtonSize,
                    new Vector2(
                        0f,
                        isCollapsible
                            ? layoutProfile.OptionsBackButtonCollapsibleY
                            : layoutProfile.OptionsBackButtonNormalY));
                BindOnce(backButton, back);
            }
        }

        public void ShowResourceAllocation(
            string title,
            string description,
            IReadOnlyList<string> labels,
            IReadOnlyList<int> maximums,
            int exactTotal,
            Action<IReadOnlyList<int>> confirm,
            Action skip)
        {
            var panel = Rebuild(layoutProfile.ResourceAllocationPanelSize, Vector2.zero);
            shell.AddResourceAllocation(panel, new ResourceAllocationSpec
            {
                Title = title,
                Description = description,
                Labels = labels,
                Maximums = maximums,
                ExactTotal = exactTotal,
                LabelWidth = layoutProfile.ResourceLabelWidth,
                LabelHeight = layoutProfile.ResourceLabelHeight,
                LabelX = layoutProfile.ResourceLabelX,
                DecreaseX = layoutProfile.ResourceDecreaseX,
                ValueX = layoutProfile.ResourceValueX,
                IncreaseX = layoutProfile.ResourceIncreaseX,
                Confirm = confirm,
                Cancel = skip,
                CloseBeforeConfirm = true,
                CloseBeforeCancel = true
            });
        }

        public void ShowMapPrompt(
            string title,
            string description,
            string primaryLabel,
            Action primary,
            Action back = null)
        {
            ShowMapPromptCore(title, description, primaryLabel, primary, back, string.Empty, false);
        }

        public void ShowCollapsibleMapPrompt(
            string title,
            string description,
            string summary,
            string primaryLabel,
            Action primary,
            Action back = null,
            bool startCollapsed = true)
        {
            ShowMapPromptCore(title, description, primaryLabel, primary, back, summary, startCollapsed);
        }

        private void ShowMapPromptCore(
            string title,
            string description,
            string primaryLabel,
            Action primary,
            Action back,
            string summary,
            bool startCollapsed)
        {
            var isCollapsible = !string.IsNullOrEmpty(summary);
            var panelHeight = isCollapsible
                ? layoutProfile.CollapsibleMapPromptPanelHeight
                : layoutProfile.MapPromptPanelHeight;
            var panel = Rebuild(
                new Vector2(layoutProfile.MapPromptPanelWidth, panelHeight),
                layoutProfile.MapPromptPanelPosition,
                summary,
                startCollapsed);
            AddHeading(panel, title, description, layoutProfile.MapPromptDescriptionHeight);
            if (primary != null)
            {
                var primaryButton = CreateButton(panel, "Primary", primaryLabel, 17);
                SetRect(
                    primaryButton.GetComponent<RectTransform>(),
                    layoutProfile.BottomCenterAnchor,
                    layoutProfile.BottomCenterAnchor,
                    layoutProfile.MapPrimaryButtonSize,
                    new Vector2(
                        back == null ? 0f : layoutProfile.MapPrimaryWithBackX,
                        isCollapsible
                            ? layoutProfile.MapButtonCollapsibleY
                            : layoutProfile.MapButtonNormalY));
                BindOnce(primaryButton, primary);
            }

            if (back != null)
            {
                var backButton = CreateButton(panel, "Back", "返回", 17);
                SetRect(
                    backButton.GetComponent<RectTransform>(),
                    layoutProfile.BottomCenterAnchor,
                    layoutProfile.BottomCenterAnchor,
                    layoutProfile.MapBackButtonSize,
                    new Vector2(
                        primary == null ? 0f : layoutProfile.MapBackWithPrimaryX,
                        isCollapsible
                            ? layoutProfile.MapButtonCollapsibleY
                            : layoutProfile.MapButtonNormalY));
                BindOnce(backButton, back);
            }
        }

        public void Hide()
        {
            DestroyFacilityCardDragGhost();
            facilityCardImageViewer?.Close();
            shell.Hide();
            collapsiblePanel = null;
        }

        private RectTransform Rebuild(
            Vector2 size,
            Vector2 position,
            string collapseSummary = null,
            bool startCollapsed = false)
        {
            DestroyFacilityCardDragGhost();
            facilityCardImageViewer?.Close();
            collapsiblePanel = null;
            var panel = shell.Rebuild(
                canvas,
                "Facility Effect Choice Overlay",
                "Facility Effect Choice Panel",
                size,
                position);
            if (panel == null || string.IsNullOrEmpty(collapseSummary))
            {
                return panel;
            }

            return ConfigureCollapsiblePanel(size, collapseSummary, startCollapsed);
        }

        private RectTransform ConfigureCollapsiblePanel(
            Vector2 expandedSize,
            string summary,
            bool startCollapsed)
        {
            var content = shell.ConfigureCollapsiblePanel(
                canvas,
                expandedSize,
                summary,
                startCollapsed,
                "Facility Expanded Content",
                "Facility Collapsed Summary",
                "Facility Collapse Toggle",
                "Facility Collapse Triangle");
            collapsiblePanel = shell.CollapsiblePanel;
            return content;
        }

        private void BeginFacilityCardDrag(
            RectTransform source,
            Texture2D texture,
            string fallbackLabel,
            Font fallbackFont,
            PointerEventData eventData)
        {
            DestroyFacilityCardDragGhost();
            facilityCardDragGhost = FacilityCardDragUtility.CreateDragGhost(
                canvas,
                source,
                texture,
                fallbackLabel,
                fallbackFont);
            MoveFacilityCardDrag(eventData);
        }

        private void MoveFacilityCardDrag(PointerEventData eventData)
        {
            FacilityCardDragUtility.MoveDragGhost(facilityCardDragGhost, eventData);
        }

        private void DestroyFacilityCardDragGhost()
        {
            FacilityCardDragUtility.DestroyDragGhost(ref facilityCardDragGhost);
        }

        private void AddHeading(RectTransform panel, string title, string description, float descriptionHeight = 70f)
        {
            EffectDialogShell.AddHeading(
                panel,
                title,
                description,
                descriptionHeight,
                addDragHandle: collapsiblePanel == null);
        }

        private static Button CreateButton(RectTransform parent, string name, string label, int fontSize)
        {
            return EffectDialogShell.CreateButton(parent, name, label, fontSize);
        }

        private void BindOnce(Button button, Action callback)
        {
            if (button == null)
            {
                return;
            }

            button.onClick.RemoveAllListeners();
            if (callback == null)
            {
                return;
            }

            var invoked = false;
            button.onClick.AddListener(() =>
            {
                if (invoked)
                {
                    return;
                }

                invoked = true;
                Hide();
                callback();
            });
        }

        private static void Stretch(RectTransform rect, float inset)
        {
            EffectDialogShell.Stretch(rect, inset);
        }

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 size, Vector2 position)
        {
            EffectDialogShell.SetRect(rect, anchorMin, anchorMax, size, position);
        }

    }
}
