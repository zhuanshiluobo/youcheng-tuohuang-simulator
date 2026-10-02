using System;
using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    internal sealed class EventChoiceDialog
    {
        private readonly GameplayDialogRegistry dialogRegistry;
        private readonly Func<RectTransform> getParent;
        private EventChoiceDialogView view;
        private bool callbackDispatched;
        private Color choiceHighlightColor;

        public EventChoiceDialog(GameplayDialogRegistry configuredRegistry, Func<RectTransform> configuredParent)
        {
            dialogRegistry = configuredRegistry ?? throw new ArgumentNullException(nameof(configuredRegistry));
            getParent = configuredParent ?? throw new ArgumentNullException(nameof(configuredParent));
        }

        public bool IsShowing
        {
            get { return view != null; }
        }

        public void SetChoiceHighlightColor(Color color)
        {
            color.a = 1f;
            choiceHighlightColor = color;
        }

        private EventChoiceDialogLayoutProfile GetLayoutProfile()
        {
            var prefab = dialogRegistry.EventChoiceDialogPrefab;
            var profile = prefab == null ? null : prefab.LayoutProfile;
            if (profile == null)
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog 缺少编辑器资产化布局 Profile。");
            }

            if (!profile.TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog 缺少有效的编辑器资产化布局 Profile：" + reason);
            }

            return profile;
        }

        public void ShowEventCardOptions(
            EventCardDefinition card,
            string metadataLabel,
            IReadOnlyList<ExplorePaymentChoice> paymentChoices,
            IReadOnlyDictionary<string, int> paymentRecipients,
            Func<int, string> getPlayerDisplayName,
            Action<int> onChoiceSelected,
            Action<string, int> onPaymentRecipientSelected)
        {
            if (card == null || card.ChoiceRewards.Count == 0)
            {
                return;
            }

            var eventArtwork = dialogRegistry.CardVisualCatalog.GetEvent(card.CardId);
            if (eventArtwork != null)
            {
                ShowAssetizedEventCardOptions(
                    card,
                    metadataLabel,
                    eventArtwork,
                    onChoiceSelected);
                return;
            }

            var layout = GetLayoutProfile();
            var paymentChoiceCount = paymentChoices == null ? 0 : paymentChoices.Count;
            var description = string.IsNullOrEmpty(card.Description) ? string.Empty : card.Description;
            var titleTop = layout.EventCardTopPadding;
            var titleHeight = layout.EventTitleLayout.SizeDelta.y;
            var metadataHeight = layout.EventMetadataLayout.SizeDelta.y;
            var metadataTop = titleTop + titleHeight + layout.EventCardTitleMetadataGap;
            var descriptionTop = metadataTop + metadataHeight +
                                 layout.EventCardMetadataDescriptionGap;
            var descriptionHeight = EstimateEventCardTextHeight(
                layout,
                description,
                layout.EventCardDescriptionFontSize,
                layout.EventCardPanelWidth * layout.EventCardDescriptionWidthRatio,
                layout.EventCardDescriptionMinHeight);
            var descriptionCenterY = -(descriptionTop + descriptionHeight * 0.5f);
            var descriptionBottom = descriptionTop + descriptionHeight;
            var firstChoiceCenterOffset = CalculateFirstChoiceCenterOffset(
                layout,
                descriptionBottom,
                paymentChoiceCount);
            var lastChoiceBottom = firstChoiceCenterOffset +
                                   (card.ChoiceRewards.Count - 1) * layout.EventCardChoiceStep +
                                   layout.ChoiceRowTemplateLayout.SizeDelta.y * 0.5f;
            var expandedHeight = lastChoiceBottom + layout.EventCardToggleButtonTopGap +
                                 layout.EventCardToggleButtonTopInset;
            var eventCardExpandedSize = new Vector2(layout.EventCardPanelWidth, expandedHeight);
            if (!PrepareView(
                    EventChoiceDialogMode.EventCard,
                    "Event Choice Overlay",
                    "Choice Panel",
                    eventCardExpandedSize,
                    layout.EventCardPanelPosition,
                    false))
            {
                return;
            }

            view.TitleText.text = GetEventCardDisplayName(card);
            view.MetadataText.gameObject.SetActive(true);
            view.MetadataText.text = metadataLabel ?? string.Empty;
            view.DescriptionText.gameObject.SetActive(true);
            view.DescriptionText.text = string.IsNullOrEmpty(card.Description) ? "暂无描述" : card.Description;
            view.EventPaymentRouteHost.gameObject.SetActive(paymentChoiceCount > 0);

            CreateExplorePaymentRecipientControls(
                layout,
                view.EventPaymentRouteHost,
                paymentChoices,
                paymentRecipients,
                getPlayerDisplayName,
                onPaymentRecipientSelected,
                descriptionBottom + layout.EventCardPaymentFirstRowGap,
                layout.EventCardPaymentRowStep);

            for (var i = 0; i < card.ChoiceRewards.Count; i++)
            {
                var capturedIndex = i;
                var row = view.CreateChoiceRow(view.EventChoiceHost);
                  row.Root.gameObject.name = "Choice " + (i + 1);
                  YC.PlayerJourney.PlayerAutomationId.Attach(row.Button.gameObject, "event.option." + i);
                row.Label.text = card.ChoiceDescriptions[i];
                row.Button.onClick.AddListener(() => InvokeStep(
                    () => onChoiceSelected?.Invoke(capturedIndex)));
            }

        }

        private void ShowAssetizedEventCardOptions(
            EventCardDefinition card,
            string metadataLabel,
            Sprite artwork,
            Action<int> onChoiceSelected)
        {
            var layout = GetLayoutProfile();
            var eventCardExpandedSize = new Vector2(artwork.rect.width, artwork.rect.height);
            if (!PrepareView(
                    EventChoiceDialogMode.EventCard,
                    "Event Choice Overlay",
                    "Choice Panel",
                    eventCardExpandedSize,
                    layout.EventCardPanelPosition,
                    false))
            {
                return;
            }

            view.ConfigureArtworkMode();
            CardArtworkView.Set(view.EventCardArtworkImage, artwork);
            view.EventCardArtworkImage.gameObject.SetActive(true);
            view.EventCardArtworkImage.transform.SetAsFirstSibling();

            var resourcePointLabel = GetResourcePointSummary(metadataLabel);
            ConfigureEventCardMetadataRibbon(card.Color, resourcePointLabel);

            view.TitleText.text = string.Empty;
            view.TitleText.color = Color.clear;
            view.TitleText.raycastTarget = true;
            view.MetadataText.gameObject.SetActive(false);
            view.DescriptionText.gameObject.SetActive(false);
            view.EventPaymentRouteHost.gameObject.SetActive(false);
            var hoverColor = choiceHighlightColor.a > 0f
                ? choiceHighlightColor
                : UiTheme.CyanAccent;

            for (var i = 0; i < card.ChoiceRewards.Count; i++)
            {
                // 图片顺序不等于规则编号；仅转换图片按钮，保留原待选会话编号。
                var capturedIndex = GetArtworkChoiceIndex(card.CardId, i);
                var row = view.CreateArtworkChoiceRow(view.ArtworkChoiceHost);
                YC.PlayerJourney.PlayerAutomationId.Attach(row.Button.gameObject, "event.option." + i);
                row.Root.gameObject.name = "Choice " + (i + 1);
                ConfigureAssetizedChoiceRow(
                    row,
                    card.CardId,
                    i,
                    card.ChoiceRewards.Count,
                    hoverColor);
                row.Button.onClick.AddListener(() => InvokeStep(
                    () => onChoiceSelected?.Invoke(capturedIndex)));
            }

        }

        private static int GetArtworkChoiceIndex(string cardId, int visualIndex)
        {
            // 险中净土、采集平台残骸的前两项在原图中与目录顺序相反。
            if ((cardId == "event_red_01" || cardId == "event_red_03") && visualIndex < 2)
            {
                return 1 - visualIndex;
            }

            return visualIndex;
        }

        private static Vector4 GetArtworkChoiceBounds(string cardId, int choiceCount)
        {
            // 原图为 850×600，边界按图片顶部向下计量。
            // 多行描述和奖励归属同一框；分界位于选项之间，各框不重叠。
            switch (cardId)
            {
                case "event_green_06":
                case "event_red_03":
                    return new Vector4(395f, 455f, 565f, 0f);
                case "event_red_02":
                    return new Vector4(395f, 485f, 555f, 0f);
                case "event_red_06":
                    return new Vector4(395f, 475f, 555f, 0f);
                case "event_yellow_07":
                    return new Vector4(400f, 475f, 570f, 0f);
                case "event_red_01":
                    return new Vector4(390f, 438f, 483f, 580f);
                case "event_red_04":
                    return new Vector4(385f, 435f, 488f, 555f);
                case "event_red_05":
                    return new Vector4(395f, 448f, 495f, 570f);
                case "event_yellow_04":
                    return new Vector4(390f, 475f, 515f, 570f);
                case "event_yellow_05":
                    return new Vector4(400f, 455f, 505f, 560f);
                default:
                    return choiceCount >= 3
                        ? new Vector4(390f, 450f, 505f, 570f)
                        : new Vector4(400f, 475f, 555f, 0f);
            }
        }

        private static void ConfigureAssetizedChoiceRow(
            EventChoiceDialogView.ButtonRow row,
            string cardId,
            int choiceIndex,
            int choiceCount,
            Color hoverColor)
        {
            var rect = row.Root;
            var bounds = GetArtworkChoiceBounds(cardId, choiceCount);
            var choiceTop = bounds[choiceIndex];
            var choiceBottom = bounds[choiceIndex + 1];
            // 图片局部的归一化热点跟随最终等比显示区域，不再使用屏幕像素。
            rect.anchorMin = new Vector2(90f / 850f, 1f - choiceBottom / 600f);
            rect.anchorMax = new Vector2(810f / 850f, 1f - choiceTop / 600f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;

            var image = row.Root.GetComponent<Image>();
            image.color = Color.clear;
            image.raycastTarget = true;
            var outline = row.Root.GetComponent<Outline>();
            outline.enabled = false;
            outline.effectColor = Color.clear;
            row.Label.text = string.Empty;
            row.Label.gameObject.SetActive(false);
            row.Button.targetGraphic = image;
            row.Button.transition = Selectable.Transition.None;
            row.Button.navigation = new Navigation { mode = Navigation.Mode.None };
            var feedback = row.Root.GetComponent<ActionButtonPressFeedback>();
            if (feedback == null)
            {
                throw new InvalidOperationException("资产化事件选项缺少行动按钮反馈组件。");
            }
            feedback.Configure(row.Button, null, hoverColor);
        }

        private void ConfigureEventCardMetadataRibbon(
            EventColor eventColor,
            string resourcePointLabel)
        {
            var ribbonColor = GetEventCardMetadataRibbonColor(eventColor);
            view.EventCardMetadataRibbonImage.color = ribbonColor;
            view.EventCardMetadataRibbonLabel.text = resourcePointLabel;
            view.EventCardMetadataRibbon.SetActive(!string.IsNullOrEmpty(resourcePointLabel));
        }

        private static Color GetEventCardMetadataRibbonColor(EventColor eventColor)
        {
            switch (eventColor)
            {
                case EventColor.Green:
                    return new Color(0.31f, 0.62f, 0.2f, 0.98f);
                case EventColor.Red:
                    return new Color(0.65f, 0.15f, 0.1f, 0.98f);
                case EventColor.Yellow:
                    return new Color(0.82f, 0.61f, 0.12f, 0.98f);
                default:
                    throw new InvalidOperationException(
                        "事件卡资源点标牌缺少颜色映射：" + eventColor);
            }
        }



        public void ShowExplorePathOptions(
            IReadOnlyList<ExplorePathChoice> pathChoices,
            Action<int> onPathSelected)
        {
            var layout = GetLayoutProfile();
            var pathChoiceCount = pathChoices == null ? 0 : pathChoices.Count;
            if (!PrepareView(
                    EventChoiceDialogMode.ExplorePath,
                    "Explore Path Overlay",
                    "Path Panel",
                    new Vector2(
                        layout.ExplorePathPanelWidth,
                        layout.ExplorePathPanelBaseHeight +
                        pathChoiceCount * layout.ExplorePathPanelRowStep),
                    layout.ExplorePathPanelPosition,
                    true))
            {
                return;
            }


            for (var i = 0; i < pathChoiceCount; i++)
            {
                var capturedIndex = i;
                var row = view.CreatePathRow(view.ExplorePathHost);
                row.Root.gameObject.name = "Path Choice " + (i + 1);
                row.Label.text = pathChoices[i].Label;
                row.Button.onClick.AddListener(() => InvokeStep(
                    () => onPathSelected?.Invoke(capturedIndex)));
            }
        }

        public void ShowExplorePaymentOptions(
            IReadOnlyList<ExplorePaymentChoice> paymentChoices,
            IReadOnlyDictionary<string, int> paymentRecipients,
            Func<int, string> getPlayerDisplayName,
            Action<string, int> onPaymentRecipientSelected,
            Action onConfirm)
        {
            var layout = GetLayoutProfile();
            var paymentChoiceCount = paymentChoices == null ? 0 : paymentChoices.Count;
            if (!PrepareView(
                    EventChoiceDialogMode.ExplorePayment,
                    "Explore Payment Overlay",
                    "Payment Panel",
                    new Vector2(
                        layout.ExplorePaymentPanelWidth,
                        layout.ExplorePaymentPanelBaseHeight +
                        paymentChoiceCount * layout.ExplorePaymentPanelRowStep),
                    layout.ExplorePaymentPanelPosition,
                    true))
            {
                return;
            }


            CreateExplorePaymentRecipientControls(
                layout,
                view.ExplorePaymentRouteHost,
                paymentChoices,
                paymentRecipients,
                getPlayerDisplayName,
                onPaymentRecipientSelected,
                layout.ExplorePaymentFirstRouteOffset,
                layout.ExplorePaymentPanelRowStep);

            view.ExploreConfirmButton.onClick.AddListener(() => InvokeStep(onConfirm));
        }

        public void ShowResourceCollectionPaymentOptions(
            string routeId,
            int amount,
            IReadOnlyList<int> recipientPlayerIds,
            Func<int, string> getPlayerDisplayName,
            Action<int> onRecipientSelected,
            Action onBankSelected,
            Action onCancel,
            int? selectedRecipientPlayerId = null,
            Action onPaymentRemoved = null)
        {
            var layout = GetLayoutProfile();
            var recipientCount = recipientPlayerIds == null ? 0 : recipientPlayerIds.Count;
            var buttonCount = Math.Max(1, recipientCount) + (selectedRecipientPlayerId.HasValue ? 1 : 0);
            if (!PrepareView(
                    EventChoiceDialogMode.ResourceCollectionPayment,
                    "Resource Collection Payment Overlay",
                    "Resource Collection Payment Panel",
                    new Vector2(
                        layout.ResourcePaymentPanelWidth,
                        layout.ResourcePaymentPanelBaseHeight +
                        buttonCount * layout.ResourcePaymentPanelRowStep),
                    layout.ResourcePaymentPanelPosition,
                    false))
            {
                return;
            }

            view.ResourcePaymentRecipientHost.gameObject.SetActive(recipientCount > 0);

            var receiverLabel = recipientCount <= 0
                ? view.ResourcePaymentBankPrompt
                : view.ResourcePaymentPlayerPrompt;
            if (selectedRecipientPlayerId.HasValue)
            {
                var selectedName = selectedRecipientPlayerId.Value <= 0
                    ? view.ResourcePaymentBankName
                    : getPlayerDisplayName == null ? selectedRecipientPlayerId.Value.ToString()
                        : getPlayerDisplayName(selectedRecipientPlayerId.Value);
                receiverLabel = string.Format(view.ResourcePaymentSelectedFormat, selectedName, amount);
                view.ResourcePaymentCancelButton.gameObject.SetActive(onPaymentRemoved != null);
                view.ResourcePaymentCancelButton.onClick.AddListener(() => InvokeTerminal(onPaymentRemoved));
            }
            view.ResourcePaymentReceiverText.text = receiverLabel;
            ConfigureClose(onCancel, true);

            if (recipientCount <= 0)
            {
                view.ResourcePaymentBankButton.gameObject.SetActive(true);
                view.ResourcePaymentBankLabel.text = string.Format(view.ResourcePaymentBankFormat, amount);
                view.ResourcePaymentBankButton.onClick.AddListener(() => InvokeTerminal(onBankSelected));
                if (selectedRecipientPlayerId.HasValue) view.ResourcePaymentBankButton.Select();
                return;
            }

            for (var i = 0; i < recipientCount; i++)
            {
                var recipientPlayerId = recipientPlayerIds[i];
                var playerName = getPlayerDisplayName == null
                    ? recipientPlayerId.ToString()
                    : getPlayerDisplayName(recipientPlayerId);
                var row = view.CreateResourceCollectionRecipientButton(
                    view.ResourcePaymentRecipientHost);
                row.Root.gameObject.name = "Pay Player " + recipientPlayerId;
                row.Label.text = string.Format(view.ResourcePaymentPlayerFormat, playerName, amount);
                row.Button.onClick.AddListener(() => InvokeTerminal(
                    () => onRecipientSelected?.Invoke(recipientPlayerId)));
                if (selectedRecipientPlayerId == recipientPlayerId) row.Button.Select();
            }
        }

        public void ShowBuildFacilityFocus(BuildFacilityDraftViewModel model)
        {
            if (model == null || model.Facility == null)
            {
                return;
            }

            var layout = GetLayoutProfile();
            if (!PrepareView(
                    EventChoiceDialogMode.BuildFacilityFocus,
                    "Build Facility Focus Overlay",
                    "Build Facility Focus Panel",
                    layout.BuildFacilityFocusPanelSize,
                    layout.BuildFacilityFocusPanelPosition,
                    true))
            {
                return;
            }

            var facility = model.Facility;
            var effectiveResourceCost = model.SelectedOption == null
                ? facility.ResourceCost
                : model.SelectedOption.EffectiveResourceCost;
            view.TitleText.text = facility.Name;
            ConfigureFacilityPreview(facility.FacilityId, facility.Name);
            view.BuildFocusDetailsText.text =
                "建设位置：第 " + (model.CityBoardSlotIndex + 1) + " 格\n" +
                "资源费用：" + FormatResourceCost(effectiveResourceCost) + "\n" +
                "金券费用：" + facility.GoldVoucherCost + "\n" +
                "获得分数：" + facility.Score + "\n" +
                "建成效果：" + FormatFacilityEffect(facility);

            var resources = model.SelectedOption == null ? null : model.SelectedOption.ResourcesPayment;
            view.BuildResourceLabel.text = "资源支付\n" + FormatResourceCost(effectiveResourceCost);
            SetBuildPaymentButtonState(view.BuildResourceButton, resources != null && resources.IsAvailable);
            view.BuildResourceButton.onClick.AddListener(() => InvokeStep(
                () => model.Dispatch(new BuildFacilityIntent.SelectPayment(
                    BuildFacilityService.PaymentModeResources))));
            view.BuildResourceReasonText.text = resources == null || resources.IsAvailable
                ? string.Empty
                : resources.Reason;

            var gold = model.SelectedOption == null ? null : model.SelectedOption.GoldPayment;
            view.BuildGoldLabel.text = "金券支付\n" + facility.GoldVoucherCost;
            SetBuildPaymentButtonState(view.BuildGoldButton, gold != null && gold.IsAvailable);
            view.BuildGoldButton.onClick.AddListener(() => InvokeStep(
                () => model.Dispatch(new BuildFacilityIntent.SelectPayment(
                    BuildFacilityService.PaymentModeGold))));
            view.BuildGoldReasonText.text = gold == null || gold.IsAvailable ? string.Empty : gold.Reason;
            view.BuildFocusErrorText.text = model.ErrorMessage ?? string.Empty;
            view.BuildFocusErrorText.gameObject.SetActive(!string.IsNullOrEmpty(model.ErrorMessage));
            ConfigureClose(
                () => model.Dispatch(new BuildFacilityIntent.Cancel()),
                false);
        }

        public void ShowBuildFacilityConfirmation(BuildFacilityDraftViewModel model)
        {
            if (model == null || model.Facility == null)
            {
                return;
            }

            var layout = GetLayoutProfile();
            if (!PrepareView(
                    EventChoiceDialogMode.BuildFacilityConfirmation,
                    "Build Facility Confirmation Overlay",
                    "Build Facility Confirmation Panel",
                    layout.BuildFacilityConfirmationPanelSize,
                    layout.BuildFacilityConfirmationPanelPosition,
                    true))
            {
                return;
            }

            var facility = model.Facility;
            var effectiveResourceCost = model.SelectedOption == null
                ? facility.ResourceCost
                : model.SelectedOption.EffectiveResourceCost;
            var resourcePayment = model.PaymentMode == BuildFacilityService.PaymentModeResources;
            var paymentLabel = resourcePayment ? "资源" : "金券";
            var paymentContent = resourcePayment ? FormatResourceCost(effectiveResourceCost) : facility.GoldVoucherCost + " 金券";

            view.BuildConfirmationSummaryText.text =
                "设施：" + facility.Name + "\n" +
                "建设位置：第 " + (model.CityBoardSlotIndex + 1) + " 格\n" +
                "支付方式：" + paymentLabel + "\n" +
                "支付内容：" + paymentContent + "\n" +
                "获得分数：" + facility.Score + "\n" +
                "建成效果：" + FormatFacilityEffect(facility);
            view.BuildConfirmationErrorText.text = model.ErrorMessage ?? string.Empty;
            view.BuildConfirmationErrorText.gameObject.SetActive(!string.IsNullOrEmpty(model.ErrorMessage));
            view.BuildBackButton.onClick.AddListener(() => InvokeStep(
                () => model.Dispatch(new BuildFacilityIntent.Back())));
            view.BuildConfirmButton.onClick.AddListener(() => InvokeStep(
                () => model.Dispatch(new BuildFacilityIntent.Confirm())));
            ConfigureClose(
                () => model.Dispatch(new BuildFacilityIntent.Cancel()),
                false);
        }

        public void ShowCharacterSecondEffectDecision(
            string cardName,
            string remainingEffectName,
            Action onContinue,
            Action onFinish)
        {
            var layout = GetLayoutProfile();
            if (!PrepareView(
                    EventChoiceDialogMode.CharacterSecondEffectDecision,
                    "Character Second Effect Overlay",
                    "Character Second Effect Dialog",
                    layout.CharacterSecondEffectPanelSize,
                    layout.CharacterSecondEffectPanelPosition,
                    true))
            {
                return;
            }

            view.DescriptionText.gameObject.SetActive(true);
            view.DescriptionText.text =
                (cardName ?? "角色牌") + "的第一个效果已结算。剩余：" +
                (remainingEffectName ?? string.Empty);
            view.CharacterContinueLabel.text = "发动" + (remainingEffectName ?? "第二效果");
            view.CharacterContinueButton.onClick.AddListener(() => InvokeTerminal(onContinue));
            view.CharacterFinishButton.onClick.AddListener(() => InvokeTerminal(onFinish));
        }

        public void Hide()
        {
            if (view == null)
            {
                return;
            }

            var current = view;
            view = null;
            current.ClearCallbacks();
            current.gameObject.SetActive(false);
            if (UnityEngine.Application.isPlaying)
            {
                UnityEngine.Object.Destroy(current.gameObject);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(current.gameObject);
            }
        }

        public void SuspendForMapInteraction()
        {
            if (view == null)
            {
                return;
            }

            GameplayHudFrame.Active?.SuspendEffectForMapInteraction();
        }

        private bool PrepareView(
            EventChoiceDialogMode mode,
            string overlayName,
            string panelName,
            Vector2 panelSize,
            Vector2 panelPosition,
            bool blockBackgroundInput)
        {
            Hide();
            var parent = getParent();
            if (parent == null)
            {
                return false;
            }

            view = dialogRegistry.InstantiateEventChoiceDialog(parent);
            if (view == null)
            {
                return false;
            }

            callbackDispatched = false;
            view.PrepareForUse(mode, overlayName, panelName, panelSize, panelPosition, blockBackgroundInput);
            return true;
        }

        private void ConfigureClose(Action onClose, bool terminal)
        {
            Action request = terminal
                ? (Action)(() => InvokeTerminal(onClose))
                : () => InvokeStep(onClose);
            view.CloseButton.onClick.AddListener(() => request());
            view.CloseInputHandler.Configure(() => request());
        }

        private void ConfigureFacilityPreview(string facilityId, string fallbackLabel)
        {
            var sprite = dialogRegistry.CardVisualCatalog.GetFacility(facilityId);
            CardArtworkView.Set(view.FacilityPreviewImage, sprite);
            view.FacilityPreviewImage.gameObject.SetActive(sprite != null);
            view.FacilityPreviewFallback.text = sprite == null ? fallbackLabel ?? facilityId ?? string.Empty : string.Empty;
            view.FacilityPreviewFallback.gameObject.SetActive(sprite == null);
            if (sprite != null)
            {
                view.FacilityPreviewAspect.aspectRatio = CardArtworkView.AspectRatio(sprite);
            }
        }

        private void InvokeStep(Action callback)
        {
            if (callbackDispatched)
            {
                return;
            }

            callbackDispatched = true;
            callback?.Invoke();
        }

        private void InvokeTerminal(Action callback)
        {
            if (callbackDispatched)
            {
                return;
            }

            callbackDispatched = true;
            Hide();
            callback?.Invoke();
        }

        private static string FormatFacilityEffect(FacilityCardDefinition facility)
        {
            if (facility == null)
            {
                return "无";
            }

            return string.IsNullOrEmpty(facility.EffectType) ? "无" : facility.EffectType;
        }

        private static string FormatResourceCost(ResourceSet cost)
        {
            if (cost == null)
            {
                return "0";
            }

            var parts = new List<string>();
            AddResourceCostPart(parts, "源岩", cost.Originium);
            AddResourceCostPart(parts, "异铁", cost.Iron);
            AddResourceCostPart(parts, "源石", cost.OriginiumShard);
            AddResourceCostPart(parts, "至纯源石", cost.PureOriginium);
            return parts.Count == 0 ? "0" : string.Join(" ", parts.ToArray());
        }

        private static void AddResourceCostPart(List<string> parts, string label, int amount)
        {
            if (amount > 0)
            {
                parts.Add(label + amount);
            }
        }

        private static void SetBuildPaymentButtonState(Button button, bool enabled)
        {
            if (button == null)
            {
                return;
            }

            button.interactable = enabled;
            var image = button.GetComponent<Image>();
            if (image != null)
            {
                image.color = enabled ? UiTheme.ButtonBackground : UiTheme.DisabledButtonBackground;
            }
        }

        private void CreateExplorePaymentRecipientControls(
            EventChoiceDialogLayoutProfile layout,
            RectTransform parent,
            IReadOnlyList<ExplorePaymentChoice> paymentChoices,
            IReadOnlyDictionary<string, int> paymentRecipients,
            Func<int, string> getPlayerDisplayName,
            Action<string, int> onPaymentRecipientSelected,
            float firstRowOffset,
            float rowStep)
        {
            if (paymentChoices == null || paymentChoices.Count <= 0)
            {
                return;
            }

            for (var i = 0; i < paymentChoices.Count; i++)
            {
                var choice = paymentChoices[i];
                var rowY = -firstRowOffset - i * rowStep;
                var route = view.CreatePaymentRouteRow(parent);
                route.Root.gameObject.name = "Payment " + choice.RouteId;
                route.Label.text = "过路费 " + choice.RouteId;

                for (var ownerIndex = 0; ownerIndex < choice.RecipientPlayerIds.Count; ownerIndex++)
                {
                    var recipientPlayerId = choice.RecipientPlayerIds[ownerIndex];
                    var routeId = choice.RouteId;
                    var selectedRecipientId = 0;
                    if (paymentRecipients != null)
                    {
                        paymentRecipients.TryGetValue(routeId, out selectedRecipientId);
                    }

                    var recipient = view.CreatePaymentRecipientButton(route.RecipientHost);
                    recipient.Root.gameObject.name =
                        "Payment Recipient " + routeId + " " + recipientPlayerId;
                    var outline = recipient.Root.GetComponent<Outline>();
                    outline.effectColor = selectedRecipientId == recipientPlayerId
                        ? UiTheme.GoldOutline
                        : UiTheme.GoldOutlineThin;
                    recipient.Label.text = getPlayerDisplayName == null
                        ? recipientPlayerId.ToString()
                        : getPlayerDisplayName(recipientPlayerId);
                    recipient.Button.onClick.AddListener(() => InvokeStep(
                        () => onPaymentRecipientSelected?.Invoke(routeId, recipientPlayerId)));
                }
            }
        }

        private static float CalculateFirstChoiceCenterOffset(
            EventChoiceDialogLayoutProfile layout,
            float descriptionBottom,
            int paymentChoiceCount)
        {
            if (paymentChoiceCount <= 0)
            {
                return descriptionBottom + layout.EventCardNoPaymentChoiceGap +
                       layout.ChoiceRowTemplateLayout.SizeDelta.y * 0.5f;
            }

            var firstPaymentCenter = descriptionBottom + layout.EventCardPaymentFirstRowGap;
            var lastPaymentBottom = firstPaymentCenter +
                                    (paymentChoiceCount - 1) * layout.EventCardPaymentRowStep +
                                    layout.PaymentRouteTemplateLayout.SizeDelta.y * 0.5f;
            return lastPaymentBottom + layout.EventCardPaymentChoiceGap +
                   layout.ChoiceRowTemplateLayout.SizeDelta.y * 0.5f;
        }

        private static float EstimateEventCardTextHeight(
            EventChoiceDialogLayoutProfile layout,
            string value,
            int fontSize,
            float width,
            float minHeight)
        {
            var lineCapacity = Mathf.Max(
                1f,
                width / (fontSize * layout.TextCharacterWidthScale));
            var weightedLength = CountWeightedTextLength(layout, value);
            var lineCount = Mathf.Max(
                layout.TextMinimumLineCount,
                Mathf.CeilToInt(weightedLength / lineCapacity));
            var lineHeight = fontSize * layout.TextLineHeightScale;
            return Mathf.Max(minHeight, lineCount * lineHeight + layout.TextExtraHeight);
        }

        private static float CountWeightedTextLength(
            EventChoiceDialogLayoutProfile layout,
            string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 0f;
            }

            var length = 0f;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '\r')
                {
                    continue;
                }

                if (c == '\n')
                {
                    length += layout.TextNewlineWeight;
                }
                else if (char.IsWhiteSpace(c))
                {
                    length += layout.TextWhitespaceWeight;
                }
                else
                {
                    length += c > 127
                        ? layout.TextNonAsciiWeight
                        : layout.TextAsciiWeight;
                }
            }

            return length;
        }



        private static string GetResourcePointSummary(string metadataLabel)
        {
            if (string.IsNullOrEmpty(metadataLabel))
            {
                return string.Empty;
            }

            var separatorIndex = metadataLabel.IndexOf("    ", StringComparison.Ordinal);
            return separatorIndex < 0
                ? metadataLabel.Trim()
                : metadataLabel.Substring(0, separatorIndex).Trim();
        }

        private static string GetEventCardDisplayName(EventCardDefinition card)
        {
            if (card == null || string.IsNullOrEmpty(card.Name))
            {
                return "事件牌";
            }

            return card.Name
                .Replace("（4）", string.Empty)
                .Replace("(4)", string.Empty)
                .Trim();
        }
    }
}
