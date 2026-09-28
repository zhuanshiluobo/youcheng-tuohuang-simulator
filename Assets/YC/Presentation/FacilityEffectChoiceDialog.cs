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
        private readonly CardInteractionLayoutProfile cardInteractionLayoutProfile;
        private Action backAction;
        private RectTransform facilityCardDragGhost;
        private CardViewer facilityCardImageViewer;
        private int presentationRevision;

        internal FacilityEffectChoiceDialog(
            GameplayDialogRegistry dialogRegistry,
            RectTransform configuredCanvas)
        {
            canvas = configuredCanvas ?? throw new ArgumentNullException(nameof(configuredCanvas));
            if (dialogRegistry == null) throw new ArgumentNullException(nameof(dialogRegistry));
            cardVisualCatalog = dialogRegistry.CardVisualCatalog;
            cardInteractionLayoutProfile = dialogRegistry.CardInteractionLayoutProfile;
            layoutProfile = dialogRegistry.EffectDialogShellPrefab == null
                ? null
                : dialogRegistry.EffectDialogShellPrefab.LayoutProfile;
            var layoutReason = string.Empty;
            if (layoutProfile == null || !layoutProfile.TryValidateConfiguration(out layoutReason))
            {
                throw new InvalidOperationException(
                    "FacilityEffectChoiceDialog 缺少有效的显式布局 Profile：" + layoutReason);
            }
            if (cardInteractionLayoutProfile == null ||
                !cardInteractionLayoutProfile.TryValidateConfiguration(out layoutReason))
            {
                throw new InvalidOperationException(
                    "FacilityEffectChoiceDialog 缺少有效的 CardInteractionLayoutProfile：" + layoutReason);
            }
            shell = new EffectDialogShell(
                dialogRegistry);
        }

        internal EffectDialogLayoutProfile Copy => layoutProfile;

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
            ShowOptionsCore(title, description, options, back);
        }

        public void ShowSelection(string title, string description, EffectDialogSelectionSpec spec,
            bool preserveScroll)
        {
            var scroll = preserveScroll ? shell.ScrollPosition : 1f;
            spec.CardTexture = id => cardVisualCatalog.GetFacility(id);
            var panel = Rebuild(layoutProfile.SelectionPanelSize, Vector2.zero, cardPicker: spec.HasCardArtwork());
            AddHeading(panel, title, description);
            shell.AddSelection(panel, spec);
            shell.RestoreScroll(scroll);
        }

        public void ShowEffectOptions(
            string title,
            string description,
            IReadOnlyList<EffectDialogOption> options,
            Action back = null,
            string backLabel = null)
        {
            ShowOptionsCore(title, description, options, back, backLabel);
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

            EffectDialogShell.ConfigureCardScroll(panel, layoutProfile.ExtensionHubCardSize + new Vector2(36f, 68f));
            var displayedRevision = presentationRevision;
            var optionCount = options == null ? 0 : options.Count;
            for (var i = 0; i < optionCount; i++)
            {
                var option = options[i];
                var texture = cardVisualCatalog.GetFacility(option.FacilityId);
                var card = EffectDialogShell.CreateFacilityCard(panel);
                card.gameObject.name = "Extension Hub Card " + option.FacilityId;
                var cardRect = card.CardRect;
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
                label.fontStyle = FontStyle.Normal;
                label.color = UiTheme.GoldText;
                label.gameObject.SetActive(texture == null);
                layoutProfile.FacilityCardFallbackLayout.ApplyTo(label.rectTransform);

                var facilityId = option.FacilityId;
                var cardName = option.Label;
                var available = option.Available;
                var interaction = card.PointerInteraction;
                Action showDetails = () =>
                {
                    if (presentationRevision != displayedRevision || card == null ||
                        !card.gameObject.activeInHierarchy || GameplayHudFrame.EffectInputSuspended || texture == null) return;
                    CardImagePreviewUtility.Open(
                        ref facilityCardImageViewer,
                        canvas,
                        texture);
                    if (facilityCardImageViewer == null) return;
                    facilityCardImageViewer.SetReturn(() =>
                    {
                        if (presentationRevision != displayedRevision) return;
                        GameplayHudFrame.Active?.ResumeEffectPage();
                    }, () => presentationRevision == displayedRevision);
                };
                interaction.ConfigureClick(button, showDetails, null);
                if (card.DetailsButton != null)
                {
                    card.DetailsButton.gameObject.SetActive(texture != null);
                    card.DetailsButton.onClick.RemoveAllListeners();
                    card.DetailsButton.onClick.AddListener(() => showDetails());
                }
                interaction.ConfigureDrag(
                    () => available && presentationRevision == displayedRevision &&
                        card.gameObject.activeInHierarchy && !GameplayHudFrame.EffectInputSuspended,
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
            string backLabel = null)
        {
            var panel = Rebuild(layoutProfile.OptionsPanelSize, Vector2.zero);
            AddHeading(panel, title, description);

            var scrollBottom = back == null
                ? layoutProfile.OptionsScrollBottom
                : layoutProfile.OptionsScrollBottomWithBack;
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
                        layoutProfile.OptionsBackButtonNormalY));
                backAction = back;
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
            Action skip,
            IReadOnlyList<int> initialValues = null,
            Action<IReadOnlyList<int>> draftChanged = null,
            Func<bool> isCurrent = null,
            IReadOnlyList<int> unitPrices = null)
        {
            if (unitPrices == null && exactTotal < 0)
                unitPrices = new[]
                {
                    YC.Domain.Economy.ResourceSaleService.OriginiumUnitPrice,
                    YC.Domain.Economy.ResourceSaleService.OriginiumShardUnitPrice,
                    YC.Domain.Economy.ResourceSaleService.IronUnitPrice,
                    YC.Domain.Economy.ResourceSaleService.PureOriginiumUnitPrice
                };
            var scroll = initialValues != null && initialValues.Count > 0 ? shell.ScrollPosition : 1f;
            var panel = Rebuild(layoutProfile.ResourceAllocationPanelSize, Vector2.zero);
            shell.AddResourceAllocation(panel, new ResourceAllocationSpec
            {
                Title = title,
                Description = description,
                Labels = labels,
                Maximums = maximums,
                ExactTotal = exactTotal,
                MinimumTotal = unitPrices == null ? 0 : 1,
                InitialValues = initialValues,
                DraftChanged = draftChanged,
                IsCurrent = isCurrent,
                UnitPrices = unitPrices,
                ConfirmLabel = unitPrices == null ? layoutProfile.SelectionConfirmLabel : layoutProfile.SaleConfirmLabel,
                CancelLabel = unitPrices == null ? layoutProfile.SelectionCancelLabel : layoutProfile.SaleFinishLabel,
                SummaryName = unitPrices == null ? string.Empty : "Facility Sale Summary",
                FormatRowLabel = unitPrices == null ? null : (Func<int, string, int, string>)((index, label, price) =>
                    string.Format(layoutProfile.SaleInventoryFormat, label, maximums[index], price)),
                FormatSummary = unitPrices == null ? null : (Func<IReadOnlyList<int>, string>)(values =>
                {
                    var revenue = 0;
                    for (var i = 0; i < values.Count && i < unitPrices.Count; i++) revenue += values[i] * unitPrices[i];
                    return string.Format(layoutProfile.SaleSummaryFormat, revenue);
                }),
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
            shell.RestoreScroll(scroll);
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

        public void ShowMapSelection(
            string title,
            string description,
            string summary,
            string primaryLabel,
            Action primary,
            Action back = null,
            bool startOnMap = true)
        {
            ShowMapPromptCore(title, description, primaryLabel, primary, back, summary, startOnMap);
        }

        private void ShowMapPromptCore(
            string title,
            string description,
            string primaryLabel,
            Action primary,
            Action back,
            string summary,
            bool startOnMap)
        {
            var panelHeight = layoutProfile.MapPromptPanelHeight;
            var panel = Rebuild(
                new Vector2(layoutProfile.MapPromptPanelWidth, panelHeight),
                layoutProfile.MapPromptPanelPosition,
                summary,
                startOnMap);
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
                        layoutProfile.MapButtonNormalY));
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
                        layoutProfile.MapButtonNormalY));
                backAction = back;
                BindOnce(backButton, back);
            }
        }

        public bool TryGoBack()
        {
            if (!IsShowing || backAction == null) return false;
            var action = backAction;
            action();
            return true;
        }

        public void Hide()
        {
            presentationRevision++;
            backAction = null;
            DestroyFacilityCardDragGhost();
            facilityCardImageViewer?.Close();
            shell.Hide();
        }

        private RectTransform Rebuild(
            Vector2 size,
            Vector2 position,
            string mapMessage = null,
            bool startOnMap = false, bool cardPicker = false)
        {
            presentationRevision++;
            backAction = null;
            DestroyFacilityCardDragGhost();
            facilityCardImageViewer?.Close();
            var panel = shell.Rebuild(
                canvas,
                "Facility Effect Choice Overlay",
                "Facility Effect Choice Panel",
                size,
                position, blockBackgroundInput: cardPicker, cardPicker: cardPicker);
            if (panel == null || string.IsNullOrEmpty(mapMessage))
            {
                return panel;
            }

            if (startOnMap) shell.SuspendForMapInteraction(mapMessage);
            return panel;
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
                fallbackFont,
                cardInteractionLayoutProfile.DragGhostLayout);
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
                descriptionHeight);
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
