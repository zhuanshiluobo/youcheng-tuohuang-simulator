using System;
using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.Facilities;
using YC.Domain.State;
using YC.Presentation.Workflows;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    internal sealed class EventChoiceDialog
    {
        private readonly GameplayDialogRegistry dialogRegistry;
        private readonly EffectDialogLayoutProfile effectDialogLayoutProfile;
        private readonly Func<RectTransform> getParent;
        private EventChoiceDialogView view;
        private bool callbackDispatched;

        public EventChoiceDialog(GameplayDialogRegistry configuredRegistry, Func<RectTransform> configuredParent)
        {
            dialogRegistry = configuredRegistry ?? throw new ArgumentNullException(nameof(configuredRegistry));
            effectDialogLayoutProfile = dialogRegistry.EffectDialogLayoutProfile;
            var effectLayoutReason = string.Empty;
            if (effectDialogLayoutProfile == null ||
                !effectDialogLayoutProfile.TryValidateConfiguration(out effectLayoutReason))
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog 缺少有效的显式 Effect 布局 Profile：" +
                    effectLayoutReason);
            }
            getParent = configuredParent ?? throw new ArgumentNullException(nameof(configuredParent));
        }

        public bool IsShowing
        {
            get { return view != null; }
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
            view.DescriptionText.rectTransform.sizeDelta =
                new Vector2(layout.EventDescriptionLayout.SizeDelta.x, descriptionHeight);
            view.DescriptionText.rectTransform.anchoredPosition =
                new Vector2(layout.EventDescriptionLayout.AnchoredPosition.x, descriptionCenterY);

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
                row.Label.text = card.ChoiceDescriptions[i];
                row.Root.anchoredPosition = new Vector2(
                    layout.ChoiceRowTemplateLayout.AnchoredPosition.x,
                    -firstChoiceCenterOffset - i * layout.EventCardChoiceStep);
                row.Button.onClick.AddListener(() => InvokeStep(
                    () => onChoiceSelected?.Invoke(capturedIndex)));
            }

            view.CollapsedSummaryText.text = BuildCardSummary(card, metadataLabel);
            view.CollapseButton.gameObject.SetActive(true);
            view.DragHandle.enabled = true;
            view.DragHandle.Configure(view.Panel);
            view.CollapsiblePanel.Configure(new EffectDialogCollapseSpec(
                effectDialogLayoutProfile)
            {
                Panel = view.Panel,
                Canvas = view.OverlayCanvas,
                OverlayImage = view.OverlayImage,
                ExpandedContent = view.ExpandedContent.gameObject,
                CollapsedSummaryText = view.CollapsedSummaryText,
                ToggleRect = view.CollapseButton.GetComponent<RectTransform>(),
                ToggleText = view.CollapseButtonLabel,
                ToggleIcon = view.CollapseButtonIcon,
                ExpandedSize = eventCardExpandedSize,
                CollapsedHeight = layout.EventCardCollapsedHeight,
                StartCollapsed = false
            });
            view.CollapseButton.onClick.AddListener(view.CollapsiblePanel.Toggle);
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

            view.TitleText.text = "选择探索路线";

            for (var i = 0; i < pathChoiceCount; i++)
            {
                var capturedIndex = i;
                var row = view.CreatePathRow(view.ExplorePathHost);
                row.Root.gameObject.name = "Path Choice " + (i + 1);
                row.Label.text = pathChoices[i].Label;
                row.Root.anchoredPosition = new Vector2(
                    layout.PathRowTemplateLayout.AnchoredPosition.x,
                    -layout.ExplorePathFirstRowOffset -
                    i * layout.ExplorePathPanelRowStep);
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

            view.TitleText.text = "选择过路费接收者";

            CreateExplorePaymentRecipientControls(
                layout,
                view.ExplorePaymentRouteHost,
                paymentChoices,
                paymentRecipients,
                getPlayerDisplayName,
                onPaymentRecipientSelected,
                layout.ExplorePaymentFirstRouteOffset,
                layout.ExplorePaymentPanelRowStep);

            var confirmRect = view.ExploreConfirmButton.GetComponent<RectTransform>();
            confirmRect.anchoredPosition = new Vector2(
                layout.ExploreConfirmTemplateLayout.AnchoredPosition.x,
                -layout.ExplorePaymentConfirmBaseOffset -
                paymentChoiceCount * layout.ExplorePaymentPanelRowStep);
            view.ExploreConfirmLabel.text = "支付过路费并探索";
            view.ExploreConfirmButton.onClick.AddListener(() => InvokeStep(onConfirm));
        }

        public void ShowResourceCollectionPaymentOptions(
            string routeId,
            int amount,
            IReadOnlyList<int> recipientPlayerIds,
            Func<int, string> getPlayerDisplayName,
            Action<int> onRecipientSelected,
            Action onBankSelected,
            Action onCancel)
        {
            var layout = GetLayoutProfile();
            var recipientCount = recipientPlayerIds == null ? 0 : recipientPlayerIds.Count;
            var buttonCount = Math.Max(1, recipientCount);
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

            view.TitleText.text = "是否支付路费";

            var receiverLabel = recipientCount <= 0
                ? "航道 " + routeId + "：支付给银行"
                : "航道 " + routeId + "：选择路费接收玩家";
            view.ResourcePaymentReceiverText.text = receiverLabel;
            ConfigureClose(onCancel, true);

            if (recipientCount <= 0)
            {
                view.ResourcePaymentBankButton.gameObject.SetActive(true);
                view.ResourcePaymentBankLabel.text = "支付 " + amount + " 金券";
                view.ResourcePaymentBankButton.onClick.AddListener(() => InvokeTerminal(onBankSelected));
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
                row.Label.text = "向 " + playerName + " 支付 " + amount + " 金券";
                row.Root.anchoredPosition = new Vector2(
                    layout.ResourceRecipientTemplateLayout.AnchoredPosition.x,
                    -layout.ResourcePaymentFirstRowOffset -
                    i * layout.ResourcePaymentPanelRowStep);
                row.Button.onClick.AddListener(() => InvokeTerminal(
                    () => onRecipientSelected?.Invoke(recipientPlayerId)));
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

            view.TitleText.text = "最终确认建设";
            view.BuildConfirmationSummaryText.text =
                "设施：" + facility.Name + "\n" +
                "建设位置：第 " + (model.CityBoardSlotIndex + 1) + " 格\n" +
                "支付方式：" + paymentLabel + "\n" +
                "支付内容：" + paymentContent + "\n" +
                "获得分数：" + facility.Score + "\n" +
                "建成效果：" + FormatFacilityEffect(facility);
            view.BuildConfirmationErrorText.text = model.ErrorMessage ?? string.Empty;
            view.BuildConfirmationErrorText.gameObject.SetActive(!string.IsNullOrEmpty(model.ErrorMessage));
            view.BuildBackLabel.text = "返回修改";
            view.BuildBackButton.onClick.AddListener(() => InvokeStep(
                () => model.Dispatch(new BuildFacilityIntent.Back())));
            view.BuildConfirmLabel.text = "确认建设";
            view.BuildConfirmButton.onClick.AddListener(() => InvokeStep(
                () => model.Dispatch(new BuildFacilityIntent.Confirm())));
            ConfigureClose(
                () => model.Dispatch(new BuildFacilityIntent.Cancel()),
                false);
        }

        public void ShowCityStyleOptions(
            IReadOnlyList<CityStyleOptionViewModel> cityStyleOptions,
            Action<string> onCityStyleSelected,
            Action onCancel)
        {
            var layout = GetLayoutProfile();
            var optionCount = cityStyleOptions == null ? 0 : cityStyleOptions.Count;
            var rowCount = Math.Max(1, optionCount);
            if (!PrepareView(
                    EventChoiceDialogMode.LegacyCityStyleOptions,
                    "City Style Overlay",
                    "City Style Panel",
                    new Vector2(
                        layout.LegacyCityStylePanelWidth,
                        layout.LegacyCityStylePanelBaseHeight +
                        rowCount * layout.LegacyCityStylePanelRowStep),
                    layout.LegacyCityStylePanelPosition,
                    true))
            {
                return;
            }

            view.TitleText.text = "宣告城市样式";
            ConfigureClose(onCancel, true);

            if (optionCount <= 0)
            {
                view.LegacyCityStyleEmptyText.gameObject.SetActive(true);
                return;
            }

            for (var i = 0; i < optionCount; i++)
            {
                var option = cityStyleOptions[i];
                if (option == null)
                {
                    continue;
                }

                var rowY = -layout.LegacyCityStyleFirstRowOffset -
                           i * layout.LegacyCityStylePanelRowStep;
                var label = option.Name + "  分数 " + option.Score;
                if (!string.IsNullOrEmpty(option.Description))
                {
                    label += "  " + option.Description;
                }

                var row = view.CreateLegacyCityStyleRow(view.LegacyCityStyleHost);
                row.Root.gameObject.name = "City Style " + i;
                row.Summary.text = label;
                row.Reason.text = option.Reason ?? string.Empty;
                row.Root.anchoredPosition = new Vector2(
                    layout.LegacyCityStyleTemplateLayout.AnchoredPosition.x,
                    rowY);
                var cityStyleId = option.CityStyleId;
                row.DeclareButton.gameObject.name = "Declare City Style " + i;
                row.DeclareLabel.text = option.CanDeclare ? "宣告" : "不可宣告";
                SetBuildPaymentButtonState(row.DeclareButton, option.CanDeclare);
                row.DeclareButton.onClick.AddListener(() => InvokeTerminal(
                    () => onCityStyleSelected?.Invoke(cityStyleId)));
            }
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

            view.TitleText.text = "是否发动第二个效果？";
            view.DescriptionText.gameObject.SetActive(true);
            view.DescriptionText.text =
                (cardName ?? "角色牌") + "的第一个效果已结算。剩余：" +
                (remainingEffectName ?? string.Empty);
            view.CharacterContinueLabel.text = "发动" + (remainingEffectName ?? "第二效果");
            view.CharacterContinueButton.onClick.AddListener(() => InvokeTerminal(onContinue));
            view.CharacterFinishLabel.text = "不发动，结束使用";
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

        public void CollapseForMapInteraction()
        {
            if (view == null)
            {
                return;
            }

            view.CollapsiblePanel.SetCollapsed(true);
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
            var texture = dialogRegistry.CardVisualCatalog.GetFacility(facilityId);
            view.FacilityPreviewImage.texture = texture;
            view.FacilityPreviewImage.gameObject.SetActive(texture != null);
            view.FacilityPreviewFallback.text = texture == null ? fallbackLabel ?? facilityId ?? string.Empty : string.Empty;
            view.FacilityPreviewFallback.gameObject.SetActive(texture == null);
            if (texture != null)
            {
                view.FacilityPreviewAspect.aspectRatio = (float)texture.width / texture.height;
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
                route.Root.anchoredPosition = new Vector2(
                    layout.PaymentRouteTemplateLayout.AnchoredPosition.x,
                    rowY);

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
                    recipient.Root.anchoredPosition = new Vector2(
                        layout.PaymentRecipientFirstOffsetX +
                        ownerIndex * layout.PaymentRecipientStepX,
                        layout.PaymentRecipientTemplateLayout.AnchoredPosition.y);
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

        private static string BuildCardSummary(EventCardDefinition card, string metadataLabel)
        {
            var resourcePointLabel = GetResourcePointSummary(metadataLabel);
            return string.IsNullOrEmpty(resourcePointLabel)
                ? GetEventCardDisplayName(card)
                : GetEventCardDisplayName(card) + "    " + resourcePointLabel;
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
