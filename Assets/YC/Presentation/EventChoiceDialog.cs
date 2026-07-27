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
        private const float EventCardWidth = 760f;
        private const float EventCardCollapsedHeight = 58f;
        private const float EventCardTopPadding = 8f;
        private const float EventCardTitleHeight = 60f;
        private const float EventCardMetadataHeight = 31f;
        private const float EventCardDescriptionMinHeight = 84f;
        private const float EventCardDescriptionWidth = EventCardWidth * 0.88f;
        private const float EventCardChoiceHeight = 62f;
        private const float EventCardChoiceStep = 72f;
        private const float EventCardPaymentRowHeight = 40f;
        private const float EventCardPaymentRowStep = 54f;
        private const float EventCardToggleButtonTopGap = 22f;
        private const float EventCardToggleButtonTopInset = 38f;
        private const int EventCardTitleFontSize = 31;
        private const int EventCardMetadataFontSize = 18;
        private const int EventCardDescriptionFontSize = 23;

        private GameObject overlay;
        private EffectDialogCollapsiblePanel eventCardCollapsiblePanel;

        public bool IsShowing
        {
            get { return overlay != null; }
        }

        public void ShowEventCardOptions(
            RectTransform canvasTransform,
            EventCardDefinition card,
            string metadataLabel,
            IReadOnlyList<ExplorePaymentChoice> paymentChoices,
            IReadOnlyDictionary<string, int> paymentRecipients,
            Func<int, string> getPlayerDisplayName,
            Action<int> onChoiceSelected,
            Action<string, int> onPaymentRecipientSelected)
        {
            if (canvasTransform == null || card == null || card.ChoiceRewards.Count == 0)
            {
                return;
            }

            DestroyOverlay();

            var paymentChoiceCount = paymentChoices == null ? 0 : paymentChoices.Count;
            overlay = CreateOverlay(canvasTransform, "Event Choice Overlay");
            var overlayImage = overlay.GetComponent<Image>();
            if (overlayImage != null)
            {
                overlayImage.raycastTarget = false;
            }

            var description = string.IsNullOrEmpty(card.Description) ? string.Empty : card.Description;
            var titleTop = EventCardTopPadding;
            var titleCenterY = -(titleTop + EventCardTitleHeight * 0.5f);
            var metadataTop = titleTop + EventCardTitleHeight + 2f;
            var metadataCenterY = -(metadataTop + EventCardMetadataHeight * 0.5f);
            var descriptionTop = metadataTop + EventCardMetadataHeight + 8f;
            var descriptionHeight = EstimateEventCardTextHeight(
                description,
                EventCardDescriptionFontSize,
                EventCardDescriptionWidth,
                EventCardDescriptionMinHeight);
            var descriptionCenterY = -(descriptionTop + descriptionHeight * 0.5f);
            var descriptionBottom = descriptionTop + descriptionHeight;
            var firstChoiceCenterOffset = CalculateFirstChoiceCenterOffset(descriptionBottom, paymentChoiceCount);
            var lastChoiceBottom = firstChoiceCenterOffset +
                                   (card.ChoiceRewards.Count - 1) * EventCardChoiceStep +
                                   EventCardChoiceHeight * 0.5f;
            var expandedHeight = lastChoiceBottom + EventCardToggleButtonTopGap + EventCardToggleButtonTopInset;
            var eventCardExpandedSize = new Vector2(EventCardWidth, expandedHeight);
            var panelRect = CreatePanel(
                overlay.GetComponent<RectTransform>(),
                "Choice Panel",
                eventCardExpandedSize,
                new Vector2(0f, -40f));
            eventCardCollapsiblePanel = panelRect.gameObject.AddComponent<EffectDialogCollapsiblePanel>();

            var eventCardSummaryText = CreateText(panelRect, "Collapsed Summary", BuildCardSummary(card, metadataLabel), 18, FontStyle.Bold, UiTheme.GoldText,
                new Vector2(0.06f, 1f), new Vector2(0.74f, 1f), new Vector2(0f, 42f), new Vector2(0f, -26f),
                TextAnchor.MiddleLeft, 12, 18);
            eventCardSummaryText.gameObject.SetActive(false);

            var eventCardExpandedContent = new GameObject("Expanded Content", typeof(RectTransform));
            eventCardExpandedContent.transform.SetParent(panelRect, false);
            var contentRect = eventCardExpandedContent.GetComponent<RectTransform>();
            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = Vector2.one;
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;

            CreateText(contentRect, "Title", GetEventCardDisplayName(card), EventCardTitleFontSize, FontStyle.Bold, UiTheme.GoldText,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, EventCardTitleHeight), new Vector2(0f, titleCenterY),
                TextAnchor.MiddleCenter, 24, EventCardTitleFontSize);

            CreateText(contentRect, "Resource Point", metadataLabel, EventCardMetadataFontSize, FontStyle.Bold, UiTheme.LabelText,
                new Vector2(0.06f, 1f), new Vector2(0.94f, 1f), new Vector2(0f, EventCardMetadataHeight), new Vector2(0f, metadataCenterY),
                TextAnchor.MiddleCenter, 14, EventCardMetadataFontSize);

            CreateText(contentRect, "Description", string.IsNullOrEmpty(card.Description) ? "暂无描述" : card.Description, 15, FontStyle.Normal, UiTheme.ValueText,
                new Vector2(0.06f, 1f), new Vector2(0.94f, 1f), new Vector2(0f, descriptionHeight), new Vector2(0f, descriptionCenterY),
                TextAnchor.UpperLeft, 15, EventCardDescriptionFontSize);
            var descriptionText = contentRect.Find("Description").GetComponent<Text>();
            descriptionText.fontSize = EventCardDescriptionFontSize;
            descriptionText.font = FontUtility.GetCjkFont(EventCardDescriptionFontSize);

            CreateExplorePaymentRecipientControls(
                contentRect,
                paymentChoices,
                paymentRecipients,
                getPlayerDisplayName,
                onPaymentRecipientSelected,
                descriptionBottom + 24f);

            for (var i = 0; i < card.ChoiceRewards.Count; i++)
            {
                var capturedIndex = i;
                var desc = card.ChoiceDescriptions[i];
                var button = CreateButton(
                    contentRect,
                    "Choice " + (i + 1),
                    desc,
                    new Vector2(0.05f, 1f),
                    new Vector2(0.95f, 1f),
                    new Vector2(0f, EventCardChoiceHeight),
                    new Vector2(0f, -firstChoiceCenterOffset - i * EventCardChoiceStep),
                    TextAnchor.MiddleLeft,
                    15,
                    12,
                    15);
                button.onClick.AddListener(() => onChoiceSelected(capturedIndex));
            }

            var toggleButton = CreateButton(
                panelRect,
                "Collapse Card",
                "收起卡片",
                new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(220f, 32f),
                new Vector2(0f, 22f),
                TextAnchor.MiddleCenter,
                14,
                12,
                14);
            var eventCardToggleRect = toggleButton.GetComponent<RectTransform>();
            var eventCardToggleText = toggleButton.GetComponentInChildren<Text>();
            var eventCardToggleIcon = UguiUtility.CreateTriangleIcon(
                eventCardToggleRect,
                "Event Card Collapse Triangle",
                true);
            eventCardCollapsiblePanel.Configure(new EffectDialogCollapseSpec
            {
                Panel = panelRect,
                Canvas = canvasTransform.GetComponentInParent<Canvas>(),
                OverlayImage = overlayImage,
                ExpandedContent = eventCardExpandedContent,
                CollapsedSummaryText = eventCardSummaryText,
                ToggleRect = eventCardToggleRect,
                ToggleText = eventCardToggleText,
                ToggleIcon = eventCardToggleIcon,
                ExpandedSize = eventCardExpandedSize,
                CollapsedHeight = EventCardCollapsedHeight,
                StartCollapsed = false
            });
            toggleButton.onClick.AddListener(eventCardCollapsiblePanel.Toggle);
        }

        public void ShowExplorePathOptions(
            RectTransform canvasTransform,
            IReadOnlyList<ExplorePathChoice> pathChoices,
            Action<int> onPathSelected)
        {
            if (canvasTransform == null)
            {
                return;
            }

            DestroyOverlay();
            overlay = CreateOverlay(canvasTransform, "Explore Path Overlay");

            var pathChoiceCount = pathChoices == null ? 0 : pathChoices.Count;
            var panelRect = CreatePanel(
                overlay.GetComponent<RectTransform>(),
                "Path Panel",
                new Vector2(820f, 126f + pathChoiceCount * 62f),
                new Vector2(0f, -30f));

            CreateText(panelRect, "Title", "选择探索路线", 22, FontStyle.Bold, UiTheme.GoldText,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 46f), new Vector2(0f, -30f),
                TextAnchor.MiddleCenter, 22, 22);

            for (var i = 0; i < pathChoiceCount; i++)
            {
                var capturedIndex = i;
                var button = CreateButton(
                    panelRect,
                    "Path Choice " + (i + 1),
                    pathChoices[i].Label,
                    new Vector2(0.05f, 1f),
                    new Vector2(0.95f, 1f),
                    new Vector2(0f, 50f),
                    new Vector2(0f, -88f - i * 62f),
                    TextAnchor.MiddleLeft,
                    14,
                    11,
                    14);
                button.onClick.AddListener(() => onPathSelected(capturedIndex));
            }
        }

        public void ShowExplorePaymentOptions(
            RectTransform canvasTransform,
            IReadOnlyList<ExplorePaymentChoice> paymentChoices,
            IReadOnlyDictionary<string, int> paymentRecipients,
            Func<int, string> getPlayerDisplayName,
            Action<string, int> onPaymentRecipientSelected,
            Action onConfirm)
        {
            if (canvasTransform == null)
            {
                return;
            }

            DestroyOverlay();
            overlay = CreateOverlay(canvasTransform, "Explore Payment Overlay");

            var paymentChoiceCount = paymentChoices == null ? 0 : paymentChoices.Count;
            var panelRect = CreatePanel(
                overlay.GetComponent<RectTransform>(),
                "Payment Panel",
                new Vector2(760f, 160f + paymentChoiceCount * 54f),
                new Vector2(0f, -30f));

            CreateText(panelRect, "Title", "选择过路费接收者", 22, FontStyle.Bold, UiTheme.GoldText,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 46f), new Vector2(0f, -30f),
                TextAnchor.MiddleCenter, 22, 22);

            CreateExplorePaymentRecipientControls(
                panelRect,
                paymentChoices,
                paymentRecipients,
                getPlayerDisplayName,
                onPaymentRecipientSelected,
                204f);

            var confirmButton = CreateButton(
                panelRect,
                "Confirm Explore",
                "支付过路费并探索",
                new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f),
                new Vector2(220f, 42f),
                new Vector2(0f, -114f - paymentChoiceCount * 54f),
                TextAnchor.MiddleCenter,
                15,
                15,
                15);
            confirmButton.onClick.AddListener(() => onConfirm());
        }

        public void ShowResourceCollectionPaymentOptions(
            RectTransform canvasTransform,
            string routeId,
            int amount,
            IReadOnlyList<int> recipientPlayerIds,
            Func<int, string> getPlayerDisplayName,
            Action<int> onRecipientSelected,
            Action onBankSelected,
            Action onCancel)
        {
            if (canvasTransform == null)
            {
                return;
            }

            DestroyOverlay();
            overlay = CreateOverlay(canvasTransform, "Resource Collection Payment Overlay");
            var overlayImage = overlay.GetComponent<Image>();
            if (overlayImage != null)
            {
                overlayImage.raycastTarget = false;
            }

            var recipientCount = recipientPlayerIds == null ? 0 : recipientPlayerIds.Count;
            var buttonCount = Math.Max(1, recipientCount);
            var panelRect = CreatePanel(
                overlay.GetComponent<RectTransform>(),
                "Resource Collection Payment Panel",
                new Vector2(620f, 144f + buttonCount * 56f),
                new Vector2(0f, -30f));

            CreateText(panelRect, "Title", "是否支付路费", 22, FontStyle.Bold, UiTheme.GoldText,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 46f), new Vector2(0f, -30f),
                TextAnchor.MiddleCenter, 18, 22);

            var receiverLabel = recipientCount <= 0
                ? "航道 " + routeId + "：支付给银行"
                : "航道 " + routeId + "：选择路费接收玩家";
            CreateText(panelRect, "Receiver", receiverLabel, 15, FontStyle.Bold, UiTheme.ValueText,
                new Vector2(0.06f, 1f), new Vector2(0.94f, 1f), new Vector2(0f, 34f), new Vector2(0f, -74f),
                TextAnchor.MiddleCenter, 12, 15);

            var closeButton = CreateButton(
                panelRect,
                "Close Payment",
                "X",
                new Vector2(1f, 1f),
                new Vector2(1f, 1f),
                new Vector2(40f, 34f),
                new Vector2(-28f, -26f),
                TextAnchor.MiddleCenter,
                16,
                12,
                16);
            closeButton.onClick.AddListener(() =>
            {
                Hide();
                if (onCancel != null)
                {
                    onCancel();
                }
            });

            if (recipientCount <= 0)
            {
                var bankButton = CreateButton(
                    panelRect,
                    "Pay Bank",
                    "支付 " + amount + " 金券",
                    new Vector2(0.12f, 1f),
                    new Vector2(0.88f, 1f),
                    new Vector2(0f, 42f),
                    new Vector2(0f, -120f),
                    TextAnchor.MiddleCenter,
                    16,
                    13,
                    16);
                bankButton.onClick.AddListener(() =>
                {
                    Hide();
                    if (onBankSelected != null)
                    {
                        onBankSelected();
                    }
                });
                return;
            }

            for (var i = 0; i < recipientCount; i++)
            {
                var recipientPlayerId = recipientPlayerIds[i];
                var playerName = getPlayerDisplayName == null
                    ? recipientPlayerId.ToString()
                    : getPlayerDisplayName(recipientPlayerId);
                var button = CreateButton(
                    panelRect,
                    "Pay Player " + recipientPlayerId,
                    "向 " + playerName + " 支付 " + amount + " 金券",
                    new Vector2(0.12f, 1f),
                    new Vector2(0.88f, 1f),
                    new Vector2(0f, 42f),
                    new Vector2(0f, -120f - i * 56f),
                    TextAnchor.MiddleCenter,
                    15,
                    12,
                    15);
                button.onClick.AddListener(() =>
                {
                    Hide();
                    if (onRecipientSelected != null)
                    {
                        onRecipientSelected(recipientPlayerId);
                    }
                });
            }
        }

        public void ShowBuildFacilityFocus(RectTransform canvasTransform, BuildFacilityDraftViewModel model)
        {
            if (canvasTransform == null || model == null || model.Facility == null)
            {
                return;
            }

            DestroyOverlay();
            overlay = CreateOverlay(canvasTransform, "Build Facility Focus Overlay");
            var panelRect = CreatePanel(
                overlay.GetComponent<RectTransform>(),
                "Build Facility Focus Panel",
                new Vector2(900f, 590f),
                new Vector2(0f, -20f));
            var facility = model.Facility;
            var effectiveResourceCost = model.SelectedOption == null
                ? facility.ResourceCost
                : model.SelectedOption.EffectiveResourceCost;
            CreateText(panelRect, "Title", facility.Name, 28, FontStyle.Bold, UiTheme.GoldText,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 54f), new Vector2(0f, -36f),
                TextAnchor.MiddleCenter, 20, 28);
            CreateFacilityCardPreview(panelRect, facility.FacilityId);

            CreateText(panelRect, "Build Details",
                "建设位置：第 " + (model.CityBoardSlotIndex + 1) + " 格\n" +
                "资源费用：" + FormatResourceCost(effectiveResourceCost) + "\n" +
                "金券费用：" + facility.GoldVoucherCost + "\n" +
                "获得分数：" + facility.Score + "\n" +
                "建成效果：" + FormatFacilityEffect(facility),
                18, FontStyle.Bold, UiTheme.ValueText,
                new Vector2(0.46f, 1f), new Vector2(0.94f, 1f), new Vector2(0f, 218f), new Vector2(0f, -186f),
                TextAnchor.UpperLeft, 14, 20);

            var resources = model.SelectedOption == null ? null : model.SelectedOption.ResourcesPayment;
            var resourceButton = CreateButton(panelRect, "Choose Resource Payment", "资源支付\n" + FormatResourceCost(effectiveResourceCost),
                new Vector2(0.48f, 1f), new Vector2(0.70f, 1f), new Vector2(0f, 66f), new Vector2(0f, -350f),
                TextAnchor.MiddleCenter, 16, 12, 18);
            SetBuildPaymentButtonState(resourceButton, resources != null && resources.IsAvailable);
            resourceButton.onClick.AddListener(() =>
                model.Dispatch(new BuildFacilityIntent.SelectPayment(
                    BuildFacilityService.PaymentModeResources)));
            CreateText(panelRect, "Resource Payment Reason", resources == null || resources.IsAvailable ? string.Empty : resources.Reason,
                13, FontStyle.Normal, UiTheme.LabelText,
                new Vector2(0.47f, 1f), new Vector2(0.71f, 1f), new Vector2(0f, 42f), new Vector2(0f, -405f),
                TextAnchor.UpperCenter, 11, 13);

            var gold = model.SelectedOption == null ? null : model.SelectedOption.GoldPayment;
            var goldButton = CreateButton(panelRect, "Choose Gold Payment", "金券支付\n" + facility.GoldVoucherCost,
                new Vector2(0.72f, 1f), new Vector2(0.94f, 1f), new Vector2(0f, 66f), new Vector2(0f, -350f),
                TextAnchor.MiddleCenter, 16, 12, 18);
            SetBuildPaymentButtonState(goldButton, gold != null && gold.IsAvailable);
            goldButton.onClick.AddListener(() =>
                model.Dispatch(new BuildFacilityIntent.SelectPayment(
                    BuildFacilityService.PaymentModeGold)));
            CreateText(panelRect, "Gold Payment Reason", gold == null || gold.IsAvailable ? string.Empty : gold.Reason,
                13, FontStyle.Normal, UiTheme.LabelText,
                new Vector2(0.71f, 1f), new Vector2(0.95f, 1f), new Vector2(0f, 42f), new Vector2(0f, -405f),
                TextAnchor.UpperCenter, 11, 13);

            if (!string.IsNullOrEmpty(model.ErrorMessage))
            {
                CreateText(panelRect, "Build Error", model.ErrorMessage, 15, FontStyle.Bold, new Color(1f, 0.45f, 0.32f),
                    new Vector2(0.46f, 0f), new Vector2(0.96f, 0f), new Vector2(0f, 44f), new Vector2(0f, 92f),
                    TextAnchor.MiddleCenter, 12, 15);
            }

            UguiUtility.CreateWindowCloseControls(
                overlay,
                panelRect,
                "Close Build Facility Focus Button",
                () => model.Dispatch(new BuildFacilityIntent.Cancel()));
        }

        public void ShowBuildFacilityConfirmation(RectTransform canvasTransform, BuildFacilityDraftViewModel model)
        {
            if (canvasTransform == null || model == null || model.Facility == null)
            {
                return;
            }

            DestroyOverlay();
            overlay = CreateOverlay(canvasTransform, "Build Facility Confirmation Overlay");
            var panelRect = CreatePanel(
                overlay.GetComponent<RectTransform>(),
                "Build Facility Confirmation Panel",
                new Vector2(720f, 500f),
                new Vector2(0f, -20f));
            var facility = model.Facility;
            var effectiveResourceCost = model.SelectedOption == null
                ? facility.ResourceCost
                : model.SelectedOption.EffectiveResourceCost;
            var resourcePayment = model.PaymentMode == BuildFacilityService.PaymentModeResources;
            var paymentLabel = resourcePayment ? "资源" : "金券";
            var paymentContent = resourcePayment ? FormatResourceCost(effectiveResourceCost) : facility.GoldVoucherCost + " 金券";

            CreateText(panelRect, "Title", "最终确认建设", 26, FontStyle.Bold, UiTheme.GoldText,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 56f), new Vector2(0f, -38f),
                TextAnchor.MiddleCenter, 20, 26);
            CreateText(panelRect, "Summary",
                "设施：" + facility.Name + "\n" +
                "建设位置：第 " + (model.CityBoardSlotIndex + 1) + " 格\n" +
                "支付方式：" + paymentLabel + "\n" +
                "支付内容：" + paymentContent + "\n" +
                "获得分数：" + facility.Score + "\n" +
                "建成效果：" + FormatFacilityEffect(facility),
                20, FontStyle.Bold, UiTheme.ValueText,
                new Vector2(0.10f, 1f), new Vector2(0.90f, 1f), new Vector2(0f, 270f), new Vector2(0f, -205f),
                TextAnchor.UpperLeft, 15, 21);

            if (!string.IsNullOrEmpty(model.ErrorMessage))
            {
                CreateText(panelRect, "Build Error", model.ErrorMessage, 16, FontStyle.Bold, new Color(1f, 0.45f, 0.32f),
                    new Vector2(0.10f, 0f), new Vector2(0.90f, 0f), new Vector2(0f, 54f), new Vector2(0f, 128f),
                    TextAnchor.MiddleCenter, 12, 16);
            }

            var backButton = CreateButton(panelRect, "Back To Build Payment", "返回修改",
                new Vector2(0.12f, 0f), new Vector2(0.42f, 0f), new Vector2(0f, 54f), new Vector2(0f, 54f),
                TextAnchor.MiddleCenter, 18, 14, 20);
            backButton.onClick.AddListener(() =>
                model.Dispatch(new BuildFacilityIntent.Back()));
            var confirmButton = CreateButton(panelRect, "Confirm Build Facility", "确认建设",
                new Vector2(0.58f, 0f), new Vector2(0.88f, 0f), new Vector2(0f, 54f), new Vector2(0f, 54f),
                TextAnchor.MiddleCenter, 18, 14, 20);
            confirmButton.onClick.AddListener(() =>
                model.Dispatch(new BuildFacilityIntent.Confirm()));

            UguiUtility.CreateWindowCloseControls(
                overlay,
                panelRect,
                "Close Build Facility Confirmation Button",
                () => model.Dispatch(new BuildFacilityIntent.Cancel()));
        }

        public void ShowCityStyleOptions(
            RectTransform canvasTransform,
            IReadOnlyList<CityStyleOptionViewModel> cityStyleOptions,
            Action<string> onCityStyleSelected,
            Action onCancel)
        {
            if (canvasTransform == null)
            {
                return;
            }

            DestroyOverlay();
            overlay = CreateOverlay(canvasTransform, "City Style Overlay");

            var optionCount = cityStyleOptions == null ? 0 : cityStyleOptions.Count;
            var rowCount = Math.Max(1, optionCount);
            var panelRect = CreatePanel(
                overlay.GetComponent<RectTransform>(),
                "City Style Panel",
                new Vector2(880f, 136f + rowCount * 70f),
                new Vector2(0f, -30f));

            CreateText(panelRect, "Title", "宣告城市样式", 22, FontStyle.Bold, UiTheme.GoldText,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 44f), new Vector2(0f, -30f),
                TextAnchor.MiddleCenter, 18, 22);

            var closeButton = CreateButton(
                panelRect,
                "Close City Style",
                "X",
                new Vector2(1f, 1f),
                new Vector2(1f, 1f),
                new Vector2(40f, 34f),
                new Vector2(-28f, -26f),
                TextAnchor.MiddleCenter,
                16,
                12,
                16);
            closeButton.onClick.AddListener(() =>
            {
                Hide();
                if (onCancel != null)
                {
                    onCancel();
                }
            });

            if (optionCount <= 0)
            {
                CreateText(panelRect, "Empty", "当前没有城市样式牌。", 16, FontStyle.Bold, UiTheme.ValueText,
                    new Vector2(0.08f, 1f), new Vector2(0.92f, 1f), new Vector2(0f, 42f), new Vector2(0f, -112f),
                    TextAnchor.MiddleCenter, 12, 16);
                return;
            }

            for (var i = 0; i < optionCount; i++)
            {
                var option = cityStyleOptions[i];
                if (option == null)
                {
                    continue;
                }

                var rowY = -106f - i * 70f;
                var label = option.Name + "  分数 " + option.Score;
                if (!string.IsNullOrEmpty(option.Description))
                {
                    label += "  " + option.Description;
                }

                CreateText(panelRect, "City Style " + i, label, 14, FontStyle.Bold, UiTheme.ValueText,
                    new Vector2(0.06f, 1f), new Vector2(0.64f, 1f), new Vector2(0f, 50f), new Vector2(0f, rowY),
                    TextAnchor.MiddleLeft, 10, 14);

                CreateText(panelRect, "City Style Reason " + i, option.Reason, 13, FontStyle.Normal, UiTheme.ValueText,
                    new Vector2(0.66f, 1f), new Vector2(0.82f, 1f), new Vector2(0f, 46f), new Vector2(0f, rowY),
                    TextAnchor.MiddleCenter, 10, 13);

                var cityStyleId = option.CityStyleId;
                var declareButton = CreateButton(
                    panelRect,
                    "Declare City Style " + i,
                    option.CanDeclare ? "宣告" : "不可宣告",
                    new Vector2(0.84f, 1f),
                    new Vector2(0.94f, 1f),
                    new Vector2(0f, 40f),
                    new Vector2(0f, rowY),
                    TextAnchor.MiddleCenter,
                    13,
                    10,
                    13);
                SetBuildPaymentButtonState(declareButton, option.CanDeclare);
                declareButton.onClick.AddListener(() =>
                {
                    Hide();
                    if (onCityStyleSelected != null)
                    {
                        onCityStyleSelected(cityStyleId);
                    }
                });
            }
        }

        public void ShowCharacterSecondEffectDecision(
            RectTransform canvasTransform,
            string cardName,
            string remainingEffectName,
            Action onContinue,
            Action onFinish)
        {
            if (canvasTransform == null)
            {
                return;
            }

            DestroyOverlay();
            overlay = CreateOverlay(canvasTransform, "Character Second Effect Overlay");
            var panel = CreatePanel(
                overlay.GetComponent<RectTransform>(),
                "Character Second Effect Dialog",
                new Vector2(620f, 290f),
                Vector2.zero);
            CreateText(
                panel,
                "Character Second Effect Title",
                "是否发动第二个效果？",
                30,
                FontStyle.Bold,
                UiTheme.GoldText,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(540f, 56f),
                new Vector2(0f, 82f),
                TextAnchor.MiddleCenter,
                20,
                30);
            CreateText(
                panel,
                "Character Second Effect Description",
                (cardName ?? "角色牌") + "的第一个效果已结算。剩余：" + (remainingEffectName ?? string.Empty),
                20,
                FontStyle.Normal,
                UiTheme.GoldText,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(530f, 70f),
                new Vector2(0f, 22f),
                TextAnchor.MiddleCenter,
                15,
                20);
            var continueButton = CreateButton(
                panel,
                "Continue Character Second Effect",
                "发动" + (remainingEffectName ?? "第二效果"),
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(230f, 58f),
                new Vector2(-132f, -78f),
                TextAnchor.MiddleCenter,
                20,
                14,
                20);
            continueButton.onClick.AddListener(() =>
            {
                Hide();
                onContinue?.Invoke();
            });
            var finishButton = CreateButton(
                panel,
                "Finish Character Use",
                "不发动，结束使用",
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(230f, 58f),
                new Vector2(132f, -78f),
                TextAnchor.MiddleCenter,
                20,
                14,
                20);
            finishButton.onClick.AddListener(() =>
            {
                Hide();
                onFinish?.Invoke();
            });
        }

        public void Hide()
        {
            DestroyOverlay();
        }

        public void CollapseForMapInteraction()
        {
            if (overlay == null)
            {
                return;
            }

            eventCardCollapsiblePanel?.SetCollapsed(true);
        }

        private static GameObject CreateOverlay(RectTransform canvasTransform, string name)
        {
            var overlayObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            overlayObject.transform.SetParent(canvasTransform, false);

            var overlayRect = overlayObject.GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;

            overlayObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);
            return overlayObject;
        }

        private static void CreateFacilityCardPreview(RectTransform panelRect, string facilityId)
        {
            string relativePath;
            if (!CardImagePathCatalog.TryGetFacilityImageRelativePath(facilityId, out relativePath))
            {
                return;
            }

            const string marker = "/Resources/";
            var markerIndex = relativePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return;
            }

            var resourcePath = relativePath.Substring(markerIndex + marker.Length);
            var extensionIndex = resourcePath.LastIndexOf('.');
            if (extensionIndex >= 0)
            {
                resourcePath = resourcePath.Substring(0, extensionIndex);
            }

            var texture = Resources.Load<Texture2D>(resourcePath);
            if (texture == null)
            {
                return;
            }

            var container = new GameObject("Facility Card Preview", typeof(RectTransform), typeof(Image));
            container.transform.SetParent(panelRect, false);
            var containerRect = container.GetComponent<RectTransform>();
            containerRect.anchorMin = new Vector2(0.05f, 0.08f);
            containerRect.anchorMax = new Vector2(0.43f, 0.88f);
            containerRect.offsetMin = Vector2.zero;
            containerRect.offsetMax = Vector2.zero;
            container.GetComponent<Image>().color = new Color(0.03f, 0.025f, 0.02f, 0.96f);

            var imageObject = new GameObject("Facility Card Image", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
            imageObject.transform.SetParent(containerRect, false);
            var imageRect = imageObject.GetComponent<RectTransform>();
            imageRect.anchorMin = Vector2.zero;
            imageRect.anchorMax = Vector2.one;
            imageRect.offsetMin = Vector2.zero;
            imageRect.offsetMax = Vector2.zero;
            var rawImage = imageObject.GetComponent<RawImage>();
            rawImage.texture = texture;
            rawImage.raycastTarget = false;
            var fitter = imageObject.GetComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = (float)texture.width / texture.height;
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

        private static RectTransform CreatePanel(RectTransform overlayRect, string name, Vector2 size, Vector2 position)
        {
            var panel = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Outline));
            panel.transform.SetParent(overlayRect, false);

            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = size;
            panelRect.anchoredPosition = position;

            panel.GetComponent<Image>().color = UiTheme.PanelBackground;
            panel.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
            panel.GetComponent<Outline>().effectDistance = new Vector2(3f, -3f);
            return panelRect;
        }

        private static Text CreateText(
            RectTransform parent,
            string name,
            string value,
            int fontSize,
            FontStyle style,
            Color color,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 size,
            Vector2 position,
            TextAnchor alignment,
            int resizeMinSize,
            int resizeMaxSize)
        {
            var textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);

            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            var text = textObject.GetComponent<Text>();
            text.text = value ?? string.Empty;
            text.alignment = alignment;
            text.color = color;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.font = FontUtility.GetCjkFont(fontSize);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = resizeMinSize;
            text.resizeTextMaxSize = resizeMaxSize;
            return text;
        }

        private static Button CreateButton(
            RectTransform parent,
            string name,
            string label,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 size,
            Vector2 position,
            TextAnchor alignment,
            int fontSize,
            int resizeMinSize,
            int resizeMaxSize)
        {
            var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var buttonRect = buttonObject.GetComponent<RectTransform>();
            buttonRect.anchorMin = anchorMin;
            buttonRect.anchorMax = anchorMax;
            buttonRect.sizeDelta = size;
            buttonRect.anchoredPosition = position;

            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);

            var labelObj = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObj.transform.SetParent(buttonRect, false);
            var labelRect = labelObj.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(12f, 0f);
            labelRect.offsetMax = new Vector2(-12f, 0f);

            var labelText = labelObj.GetComponent<Text>();
            labelText.text = label ?? string.Empty;
            labelText.alignment = alignment;
            labelText.color = UiTheme.GoldText;
            labelText.fontSize = fontSize;
            labelText.fontStyle = FontStyle.Bold;
            labelText.font = FontUtility.GetCjkFont(fontSize);
            labelText.horizontalOverflow = HorizontalWrapMode.Wrap;
            labelText.verticalOverflow = VerticalWrapMode.Truncate;
            labelText.resizeTextForBestFit = true;
            labelText.resizeTextMinSize = resizeMinSize;
            labelText.resizeTextMaxSize = resizeMaxSize;

            return buttonObject.GetComponent<Button>();
        }

        private static void CreateExplorePaymentRecipientControls(
            RectTransform panelRect,
            IReadOnlyList<ExplorePaymentChoice> paymentChoices,
            IReadOnlyDictionary<string, int> paymentRecipients,
            Func<int, string> getPlayerDisplayName,
            Action<string, int> onPaymentRecipientSelected,
            float firstRowOffset)
        {
            if (paymentChoices == null || paymentChoices.Count <= 0)
            {
                return;
            }

            for (var i = 0; i < paymentChoices.Count; i++)
            {
                var choice = paymentChoices[i];
                var rowY = -firstRowOffset - i * 54f;

                CreateText(panelRect, "Payment " + choice.RouteId, "过路费 " + choice.RouteId, 14, FontStyle.Normal, UiTheme.ValueText,
                    new Vector2(0.06f, 1f), new Vector2(0.34f, 1f), new Vector2(0f, 40f), new Vector2(0f, rowY),
                    TextAnchor.MiddleLeft, 11, 14);

                for (var ownerIndex = 0; ownerIndex < choice.RecipientPlayerIds.Count; ownerIndex++)
                {
                    var recipientPlayerId = choice.RecipientPlayerIds[ownerIndex];
                    var routeId = choice.RouteId;

                    var buttonObject = new GameObject("Payment Recipient " + routeId + " " + recipientPlayerId, typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
                    buttonObject.transform.SetParent(panelRect, false);

                    var buttonRect = buttonObject.GetComponent<RectTransform>();
                    buttonRect.anchorMin = new Vector2(0.36f, 1f);
                    buttonRect.anchorMax = new Vector2(0.94f, 1f);
                    buttonRect.sizeDelta = new Vector2(0f, 34f);
                    buttonRect.anchoredPosition = new Vector2(ownerIndex * 110f, rowY);

                    buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
                    var selectedRecipientId = 0;
                    if (paymentRecipients != null)
                    {
                        paymentRecipients.TryGetValue(routeId, out selectedRecipientId);
                    }

                    var outline = buttonObject.GetComponent<Outline>();
                    outline.effectColor = selectedRecipientId == recipientPlayerId
                        ? UiTheme.GoldOutline
                        : UiTheme.GoldOutlineThin;
                    outline.effectDistance = new Vector2(1f, -1f);

                    var textObj = new GameObject("Label", typeof(RectTransform), typeof(Text));
                    textObj.transform.SetParent(buttonRect, false);
                    var textRect = textObj.GetComponent<RectTransform>();
                    textRect.anchorMin = Vector2.zero;
                    textRect.anchorMax = Vector2.one;
                    textRect.offsetMin = new Vector2(8f, 0f);
                    textRect.offsetMax = new Vector2(-8f, 0f);

                    var text = textObj.GetComponent<Text>();
                    text.text = getPlayerDisplayName == null ? recipientPlayerId.ToString() : getPlayerDisplayName(recipientPlayerId);
                    text.alignment = TextAnchor.MiddleCenter;
                    text.color = UiTheme.GoldText;
                    text.fontSize = 13;
                    text.font = FontUtility.GetCjkFont(13);
                    text.resizeTextForBestFit = true;
                    text.resizeTextMinSize = 10;
                    text.resizeTextMaxSize = 13;

                    buttonObject.GetComponent<Button>().onClick.AddListener(() => onPaymentRecipientSelected(routeId, recipientPlayerId));
                }
            }
        }

        private void DestroyOverlay()
        {
            if (overlay != null)
            {
                UnityEngine.Object.Destroy(overlay);
                overlay = null;
            }

            eventCardCollapsiblePanel = null;
        }

        private static float CalculateFirstChoiceCenterOffset(float descriptionBottom, int paymentChoiceCount)
        {
            if (paymentChoiceCount <= 0)
            {
                return descriptionBottom + 22f + EventCardChoiceHeight * 0.5f;
            }

            var firstPaymentCenter = descriptionBottom + 24f;
            var lastPaymentBottom = firstPaymentCenter +
                                    (paymentChoiceCount - 1) * EventCardPaymentRowStep +
                                    EventCardPaymentRowHeight * 0.5f;
            return lastPaymentBottom + 10f + EventCardChoiceHeight * 0.5f;
        }

        private static float EstimateEventCardTextHeight(string value, int fontSize, float width, float minHeight)
        {
            var lineCapacity = Mathf.Max(1f, width / (fontSize * 0.95f));
            var weightedLength = CountWeightedTextLength(value);
            var lineCount = Mathf.Max(2, Mathf.CeilToInt(weightedLength / lineCapacity));
            var lineHeight = fontSize * 1.18f;
            return Mathf.Max(minHeight, lineCount * lineHeight + 8f);
        }

        private static float CountWeightedTextLength(string value)
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
                    length += 30f;
                }
                else if (char.IsWhiteSpace(c))
                {
                    length += 0.35f;
                }
                else
                {
                    length += c > 127 ? 1f : 0.55f;
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
