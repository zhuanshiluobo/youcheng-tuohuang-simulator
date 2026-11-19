using System;
using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
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
        public Func<IDisposable> BeginReadScope { private get; set; }

        private void BindInput(Button button, UnityEngine.Events.UnityAction callback)
        {
            button.onClick.AddListener(() =>
            {
                using var readScope = BeginReadScope?.Invoke();
                callback();
            });
        }

        public EventChoiceDialog(GameplayDialogRegistry configuredRegistry, Func<RectTransform> configuredParent)
        {
            dialogRegistry = configuredRegistry ?? throw new ArgumentNullException(nameof(configuredRegistry));
            getParent = configuredParent ?? throw new ArgumentNullException(nameof(configuredParent));
        }

        public bool IsShowing
        {
            get { return view != null; }
        }

        public EventChoiceDialogView Copy => view != null ? view : dialogRegistry.EventChoiceDialogPrefab;

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
            if (card == null || card.EventChoiceSchemaVersion != 1)
                throw new ArgumentException("事件选择要求版本 1 的事件定义。", nameof(card));
            ShowEventCardOptionsCore(card, metadataLabel, paymentChoices, paymentRecipients, getPlayerDisplayName,
                (index, _) => onChoiceSelected?.Invoke(index), onPaymentRecipientSelected);
        }

        public void ShowEventCardOptionsWithStableIds(EventCardDefinition card, string metadataLabel,
            Action<string> onChoiceSelected)
        {
            if (card == null || card.EventChoiceSchemaVersion != 1)
                throw new ArgumentException("稳定事件选择要求版本 1 的事件定义。", nameof(card));
            ShowEventCardOptionsCore(card, metadataLabel, null, null, null,
                (_, choiceId) => onChoiceSelected?.Invoke(choiceId), null);
        }

        private void ShowEventCardOptionsCore(EventCardDefinition card, string metadataLabel,
            IReadOnlyList<ExplorePaymentChoice> paymentChoices, IReadOnlyDictionary<string, int> paymentRecipients,
            Func<int, string> getPlayerDisplayName, Action<int, string> onChoiceSelected,
            Action<string, int> onPaymentRecipientSelected)
        {
            var choiceCount = card.Choices.Count;
            if (choiceCount == 0)
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
                                   (choiceCount - 1) * layout.EventCardChoiceStep +
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

            for (var i = 0; i < choiceCount; i++)
            {
                var capturedIndex = i;
                var choiceId = card.Choices[i].ChoiceId;
                var row = view.CreateChoiceRow(view.EventChoiceHost);
                row.Root.gameObject.name = "Choice " + (i + 1);
                YC.PlayerJourney.PlayerAutomationId.Attach(row.Button.gameObject,
                    "event.choice." + choiceId);
                row.Label.text = card.Choices[i].Description;
                BindInput(row.Button,() => InvokeStep(
                    () => onChoiceSelected?.Invoke(capturedIndex, choiceId)));
            }

        }

        private void ShowAssetizedEventCardOptions(
            EventCardDefinition card,
            string metadataLabel,
            Sprite artwork,
            Action<int, string> onChoiceSelected)
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

            int choiceCount = card.Choices.Count;
            for (var i = 0; i < choiceCount; i++)
            {
                var capturedIndex = i;
                var choiceId = card.Choices[i].ChoiceId;
                var row = view.CreateArtworkChoiceRow(view.ArtworkChoiceHost);
                YC.PlayerJourney.PlayerAutomationId.Attach(row.Button.gameObject,
                    "event.choice." + choiceId);
                row.Root.gameObject.name = "Choice " + (i + 1);
                ConfigureChoicePercent(row, card.Choices[i].Button);
                var feedback = row.Root.GetComponent<ActionButtonPressFeedback>();
                if (feedback == null) throw new InvalidOperationException("资产化事件选项缺少行动按钮反馈组件。");
                feedback.Configure(row.Button, null, hoverColor);
                BindInput(row.Button,() => InvokeStep(
                    () => onChoiceSelected?.Invoke(capturedIndex, choiceId)));
            }

        }

        private static void ConfigureChoicePercent(EventChoiceDialogView.ButtonRow row, EventCardChoiceButton button)
        {
            if (button == null) throw new InvalidOperationException("事件选项缺少卡面按钮区域。");
            var rect = row.Root;
            var min = rect.anchorMin; var max = rect.anchorMax;
            min.y = 1f - (float)(button.BottomPercent / 100d);
            max.y = 1f - (float)(button.TopPercent / 100d);
            rect.anchorMin = min; rect.anchorMax = max;
            var offsetMin = rect.offsetMin; var offsetMax = rect.offsetMax;
            offsetMin.y = offsetMax.y = 0f;
            rect.offsetMin = offsetMin; rect.offsetMax = offsetMax;
            var boundary = rect.GetComponent<EventChoiceBoundaryFilter>();
            if (boundary == null) boundary = rect.gameObject.AddComponent<EventChoiceBoundaryFilter>();
            boundary.Configure(rect.parent as RectTransform, min.y, max.y);
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
                BindInput(row.Button,() => InvokeStep(
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

            BindInput(view.ExploreConfirmButton,() => InvokeStep(onConfirm));
        }

        public void ShowConditionPayment(InteractionRequestProjection request, GameplayTextDocument description,
            Action<Component, GameplayTextDocument, bool> renderText,
            Func<bool> isCurrent, Action execute, Action decline)
        {
            if (request == null || isCurrent == null || !isCurrent()) return;
            var copy = dialogRegistry.EventChoiceDialogPrefab;
            if (!PrepareView(EventChoiceDialogMode.ConditionPayment,
                    "Condition Payment Overlay", "Condition Payment Panel",
                    copy.ConditionPaymentPanelSize, copy.ConditionPaymentPanelPosition, false)) return;
            var kind = request.PromptParameters?.Properties.Find(entry => entry.Name == "sourceKind")?.Value;
            view.SetConditionPaymentTitle(kind?.StringValue == "condition");
            if (description == null || renderText == null)
                throw new InvalidOperationException("条件付款缺少正式提示文档或渲染入口。");
            view.DescriptionText.gameObject.SetActive(true);
            view.ConditionPaymentDescriptionDocument.enabled = true;
            // 两种付款文档共用已配置的紧凑正文尺度，避免短文沿事件正文BestFit上限放大。
            view.ConditionPaymentDescriptionDocument.SetFontSizeOverride(view.DescriptionText.resizeTextForBestFit
                ? view.DescriptionText.resizeTextMinSize : view.DescriptionText.fontSize);
            renderText(view.ConditionPaymentDescriptionDocument, description, false);
            var canPay = request.PromptParameters?.Properties.Find(entry => entry.Name == "canPay")?.Value;
            view.ConditionPaymentExecuteButton.interactable = execute != null &&
                (canPay?.Kind != NormalizedValueKind.Boolean || canPay.BooleanValue);
            view.ConditionPaymentDeclineButton.gameObject.SetActive(decline != null);
            BindInput(view.ConditionPaymentExecuteButton,() =>
            {
                if (isCurrent() && view != null && view.ConditionPaymentExecuteButton.IsInteractable())
                    InvokeTerminal(execute);
            });
            if (decline != null) BindInput(view.ConditionPaymentDeclineButton,() =>
            {
                if (isCurrent()) InvokeTerminal(decline);
            });
        }

        public void ShowRoutePaymentOptions(RoutePaymentOptionsViewModel model, Func<int, string> getPlayerDisplayName)
        {
            if (model?.Recipients == null || model.IsCurrent == null || !model.IsCurrent()) return;
            var layout = GetLayoutProfile();
            var buttonCount = model.Recipients.Count + (model.Remove == null ? 0 : 1);
            if (!PrepareView(EventChoiceDialogMode.ResourceCollectionPayment,
                    "Resource Collection Payment Overlay", "Resource Collection Payment Panel",
                    new Vector2(layout.ResourcePaymentPanelWidth,
                        layout.ResourcePaymentPanelBaseHeight + buttonCount * layout.ResourcePaymentPanelRowStep),
                    layout.ResourcePaymentPanelPosition, false)) return;
            var hasPlayers = false;
            view.ResourcePaymentBankButton.gameObject.SetActive(false);
            foreach (var recipient in model.Recipients)
            {
                var subjectKey = recipient.SubjectKey;
                var isBank = recipient.Kind == YC.Domain.Travel.RouteRecipientKind.Bank;
                var playerName = isBank ? view.ResourcePaymentBankName :
                    getPlayerDisplayName == null ? recipient.PlayerId.ToString() : getPlayerDisplayName(recipient.PlayerId.Value);
                Button button;
                if (isBank)
                {
                    button = view.ResourcePaymentBankButton;
                    button.gameObject.SetActive(true);
                    view.ResourcePaymentBankLabel.text = string.Format(view.ResourcePaymentBankFormat, model.GoldVoucherAmount);
                }
                else
                {
                    hasPlayers = true;
                    var row = view.CreateResourceCollectionRecipientButton(view.ResourcePaymentRecipientHost);
                    row.Root.gameObject.name = "Route Recipient " + subjectKey;
                    row.Label.text = string.Format(view.ResourcePaymentPlayerFormat, playerName, model.GoldVoucherAmount);
                    button = row.Button;
                }
                BindInput(button,() =>
                {
                    if (model.IsCurrent()) InvokeTerminal(() => model.SelectRecipient?.Invoke(subjectKey));
                });
                if (subjectKey == model.SelectedSubjectKey)
                {
                    view.ResourcePaymentReceiverText.text = string.Format(view.ResourcePaymentSelectedFormat, playerName, model.GoldVoucherAmount);
                    button.Select();
                }
            }
            view.ResourcePaymentRecipientHost.gameObject.SetActive(hasPlayers);
            if (string.IsNullOrEmpty(model.SelectedSubjectKey))
                view.ResourcePaymentReceiverText.text = hasPlayers ? view.ResourcePaymentPlayerPrompt : view.ResourcePaymentBankPrompt;
            view.ResourcePaymentCancelButton.gameObject.SetActive(model.Remove != null);
            if (model.Remove != null) BindInput(view.ResourcePaymentCancelButton,() =>
            {
                if (model.IsCurrent()) InvokeTerminal(model.Remove);
            });
            ConfigureClose(() => { if (model.IsCurrent()) model.Cancel?.Invoke(); }, true);
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
                BindInput(view.ResourcePaymentCancelButton,() => InvokeTerminal(onPaymentRemoved));
            }
            view.ResourcePaymentReceiverText.text = receiverLabel;
            ConfigureClose(onCancel, true);

            if (recipientCount <= 0)
            {
                view.ResourcePaymentBankButton.gameObject.SetActive(true);
                view.ResourcePaymentBankLabel.text = string.Format(view.ResourcePaymentBankFormat, amount);
                BindInput(view.ResourcePaymentBankButton,() => InvokeTerminal(onBankSelected));
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
                BindInput(row.Button,() => InvokeTerminal(
                    () => onRecipientSelected?.Invoke(recipientPlayerId)));
                if (selectedRecipientPlayerId == recipientPlayerId) row.Button.Select();
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

            view.DescriptionText.gameObject.SetActive(true);
            view.DescriptionText.text =
                (cardName ?? "角色牌") + "的第一个效果已结算。剩余：" +
                (remainingEffectName ?? string.Empty);
            view.CharacterContinueLabel.text = "发动" + (remainingEffectName ?? "第二效果");
            BindInput(view.CharacterContinueButton,() => InvokeTerminal(onContinue));
            BindInput(view.CharacterFinishButton,() => InvokeTerminal(onFinish));
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
            BindInput(view.CloseButton,() => request());
            view.CloseInputHandler.Configure(() => request());
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
                    BindInput(recipient.Button,() => InvokeStep(
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
