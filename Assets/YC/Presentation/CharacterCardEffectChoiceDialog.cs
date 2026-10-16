using System;
using System.Collections.Generic;
using System.Linq;
using YC.Application.Gameplay;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.State;
using YC.Presentation.Workflows;
using UnityEngine;

namespace YC.Presentation
{
    /// <summary>角色牌非地图参数的专用结算弹窗；不读取或修改共享游戏状态。</summary>
    internal sealed class CharacterCardEffectChoiceDialog
    {
        public Func<string, string> ResolveCharacterDefinition { get; set; }
        public Action<InteractionRequestProjection, GameplayTextDocument, Action<Component, GameplayTextDocument, bool>, Func<bool>, Action, Action> RenderPaymentConfirmation { get; set; }
        public Func<bool> IsPaymentConfirmationShowing { get; set; }
        public Action HidePaymentConfirmation { get; set; }
        private bool ownsPaymentConfirmation;
        private readonly RectTransform canvas;
        private readonly EffectDialogShell shell;
        private readonly EffectDialogLayoutProfile layoutProfile;
        private readonly CardVisualCatalog cards;
        private readonly GameplayDialogRegistry registry;
        private PlayerSelectionPageView players;
        private Func<GameplayTextRef, GameplayTextDocument> resolveText;
        private Action<Component, GameplayTextDocument, bool> renderText;
        private RectTransform lastSelectionPanel;
        private EffectDialogShellView coverPage;
        private UnityEngine.UI.Button coverConfirm;
        private string coverPageKey, coverSelectedId;
        private List<string> coverPageCandidates;
        private readonly Dictionary<string, FacilityEffectCardView> coverRows = new Dictionary<string, FacilityEffectCardView>();

        public void BindGameplayText(Func<GameplayTextRef, GameplayTextDocument> resolve,
            Action<Component, GameplayTextDocument, bool> render)
        {
            resolveText = resolve;
            renderText = render;
        }

        internal CharacterCardEffectChoiceDialog(
            GameplayDialogRegistry dialogRegistry,
            RectTransform configuredCanvas)
        {
            canvas = configuredCanvas ?? throw new ArgumentNullException(nameof(configuredCanvas));
            if (dialogRegistry == null) throw new ArgumentNullException(nameof(dialogRegistry));
            layoutProfile = dialogRegistry.EffectDialogLayoutProfile;
            cards = dialogRegistry.CardVisualCatalog;
            registry = dialogRegistry;
            var layoutReason = string.Empty;
            if (layoutProfile == null || !layoutProfile.TryValidateConfiguration(out layoutReason))
            {
                throw new InvalidOperationException(
                    "CharacterCardEffectChoiceDialog 缺少有效的显式布局 Profile：" + layoutReason);
            }

            shell = new EffectDialogShell(dialogRegistry);
        }

        internal EffectDialogLayoutProfile Copy => layoutProfile;

        public bool IsShowing
        {
            get { return shell.IsShowing || players != null && players.gameObject.activeInHierarchy ||
                ownsPaymentConfirmation && IsPaymentConfirmationShowing?.Invoke() == true; }
        }

        public void ShowSecondEffectStep(CharacterCardPanelViewModel model, Action strategy, Action tactic,
            Action finish, Func<bool> isCurrent, Action cancel)
        {
            if (!model.IsSecondEffectDecision && !model.IsSecondEffectExecution)
                throw new InvalidOperationException("首次角色使用必须通过 CardViewer 的显式使用模式。");
            var options = new List<EffectDialogOption>
            {
                new EffectDialogOption(layoutProfile.CharacterStrategyLabel, strategy, model.CanUseStrategy)
                    { StableId = "action.character.strategy", IsCurrent = isCurrent },
                new EffectDialogOption(layoutProfile.CharacterTacticLabel, tactic, model.CanUseTactic)
                    { StableId = "action.character.tactic", IsCurrent = isCurrent }
            };
            if (model.IsSecondEffectDecision)
                options.Add(new EffectDialogOption(layoutProfile.CharacterFinishLabel, finish)
                    { StableId = "action.character.finish", IsCurrent = isCurrent });
            ShowOptionsCore(layoutProfile.CharacterUseTitle,
                model.IsSecondEffectDecision ? layoutProfile.CharacterSecondEffectDescription :
                    layoutProfile.CharacterUseDescription, options,
                model.IsSecondEffectDecision || model.IsSecondEffectExecution ? null : cancel, null, false);
        }

        public void ShowOptions(
            string title,
            string description,
            IReadOnlyList<EffectDialogOption> options,
            Action cancel = null,
            string cancelLabel = null)
        {
            ShowOptionsCore(title, description, options, cancel, cancelLabel, true);
        }

        public void ShowOptions(InteractionRequestProjection request, string title, string description,
            IReadOnlyList<EffectDialogOption> options, Action cancel = null, string cancelLabel = null)
        {
            ShowOptionsCore(title, description, options, cancel, cancelLabel, true, ResolveOptionsPrompt(request));
        }

        internal GameplayTextDocument ResolveOptionsPrompt(InteractionRequestProjection request) =>
            EffectDialogTextDocuments.ResolvePrompt(request, resolveText);

        internal GameplayTextDocument ResolveGameplayText(GameplayTextRef text) =>
            text == null ? null : resolveText?.Invoke(text);

        internal GameplayTextDocument ResolveLegacyOptionsPrompt(GameplayInteractionPresentation presentation) =>
            ResolveGameplayText(presentation?.Prompt);

        internal GameplayTextDocument ResolveLegacyOptionLabel(GameplayInteractionPresentation presentation, string candidateId) =>
            ResolveGameplayText(presentation?.Options.Find(option => option.CandidateId == candidateId)?.Label);

        internal int GameplayTextGeneration => GameplayHudFrame.Active?.PresentationGeneration ?? 0;

        public void ShowOptions(GameplayTextDocument promptDocument, string title, string description,
            IReadOnlyList<EffectDialogOption> options, Action cancel = null, string cancelLabel = null)
        {
            ShowOptionsCore(title, description, options, cancel, cancelLabel, true, promptDocument);
        }

        private void ShowOptionsCore(string title, string description, IReadOnlyList<EffectDialogOption> options,
            Action cancel, string cancelLabel, bool effectPage, GameplayTextDocument promptDocument = null)
        {
            var optionCount = options == null ? 0 : options.Count;
            var panelSize = layoutProfile.CharacterOptionsPanelBaseSize;
            panelSize.y = Mathf.Clamp(
                panelSize.y + optionCount * layoutProfile.CharacterOptionsRowHeight,
                layoutProfile.CharacterOptionsPanelMinHeight,
                layoutProfile.CharacterOptionsPanelMaxHeight);
            var panel = PreparePage(panelSize, effectPage);
            EffectDialogShell.AddHeading(
                panel,
                title,
                description,
                layoutProfile.CharacterOptionsDescriptionHeight,
                "Character Effect Title",
                "Character Effect Description",
                24);
            if (promptDocument != null)
            {
                var view = panel.GetComponentInParent<EffectDialogShellView>(true);
                var reason = "实际可选页面缺少EffectDialogShellView。";
                if (view == null || !view.TryRenderGameplayHeading(promptDocument, renderText, out reason))
                    Debug.LogWarning("可选效果正式提示未接入：" + reason, view);
            }
            var content = EffectDialogShell.AddOptionScroll(
                panel,
                "Character Effect Options Scroll",
                cancel == null
                    ? layoutProfile.CharacterOptionsScrollBottom
                    : layoutProfile.CharacterOptionsScrollBottomWithCancel,
                layoutProfile.CharacterOptionsScrollTop);
            EffectDialogShell.AddOptions(content, options, "Character Effect Option ", Hide, renderText);

            if (cancel != null)
            {
                var cancelButton = EffectDialogShell.CreateButton(panel, "Cancel Character Effect",
                    string.IsNullOrEmpty(cancelLabel) ? layoutProfile.SelectionCancelLabel : cancelLabel, 17);
                var cancelInvoked = false;
                cancelButton.onClick.AddListener(() =>
                {
                    if (cancelInvoked)
                    {
                        return;
                    }

                    cancelInvoked = true;
                    Hide();
                    cancel();
                });
            }
        }

        public void ShowSelection(string title, string description, EffectDialogSelectionSpec spec,
            bool preserveScroll)
        {
            if (spec.Players != null)
            {
                shell.Hide();
                if (players == null) players = registry.InstantiatePlayerSelection(canvas);
                spec.Players.Title = title; spec.Players.Description = description;
                spec.Players.AllowCancel &= spec.Cancel != null;
                players.Present(spec.Players,ids =>
                    { spec.PlayerSelectionChanged?.Invoke(ids); spec.Confirm?.Invoke(); },spec.Cancel,spec.IsCurrent,
                    spec.IsEffectPage,spec.PlayerSelectionChanged);
                return;
            }
            if (players != null) players.Close();
            var scroll = preserveScroll ? shell.ScrollPosition : 1f;
            if (spec.CardSprite == null) spec.CardSprite = id => cards.GetFacility(id) ?? cards.GetCharacterFront(id) ?? cards.GetEvent(id);
            var panel = PreparePage(layoutProfile.SelectionPanelSize, spec.IsEffectPage, spec.HasCardArtwork());
            EffectDialogShell.AddHeading(panel, title, description);
            if (spec.PromptDocument != null)
            {
                var view = panel.GetComponentInParent<EffectDialogShellView>(true);
                var reason = "实际选择页面缺少EffectDialogShellView。";
                if (view == null || !view.TryRenderGameplayHeading(spec.PromptDocument, spec.RenderText, out reason))
                {
                    Debug.LogWarning("选择正式提示未接入：" + reason, view);
                    EffectDialogShell.AddHeading(panel, title, spec.CompatibilityPrompt?.Invoke() ?? description);
                }
            }
            shell.AddSelection(panel, spec);
            lastSelectionPanel = panel;
            shell.RestoreScroll(scroll);
        }

        public void ShowQuantity(string label, int minimum, int maximum,
            IReadOnlyList<int> initialValues, Action<IReadOnlyList<int>> draftChanged,
            Func<bool> isCurrent, Action<int> confirm)
        {
            ShowQuantity(null, label, minimum, maximum, initialValues, draftChanged, isCurrent, confirm);
        }

        public void ShowQuantity(InteractionRequestProjection request, string label, int minimum, int maximum,
            IReadOnlyList<int> initialValues, Action<IReadOnlyList<int>> draftChanged,
            Func<bool> isCurrent, Action<int> confirm)
        {
            string[] rowIds = null;
            var declaredRows = request?.PromptParameters?.Properties?.FirstOrDefault(value => value.Name == "rowIds")?.Value;
            if (declaredRows != null)
            {
                if (declaredRows.Kind != NormalizedValueKind.Array || declaredRows.Items == null ||
                    declaredRows.Items.Count != 1 || declaredRows.Items[0]?.Kind != NormalizedValueKind.String ||
                    string.IsNullOrEmpty(declaredRows.Items[0].StringValue))
                    throw new InvalidOperationException("数量请求必须声明唯一的有效行标识。");
                rowIds = new[] { declaredRows.Items[0].StringValue };
            }
            else
            {
                var amountKind = request?.PromptParameters?.Properties?.FirstOrDefault(value => value.Name == "amountKind")?.Value;
                if (amountKind?.Kind == NormalizedValueKind.String && !string.IsNullOrEmpty(amountKind.StringValue))
                    rowIds = new[] { amountKind.StringValue };
            }
            if (players != null) players.Close();
            var panel = PreparePage(layoutProfile.SalePanelSize);
            shell.AddResourceAllocation(panel, new ResourceAllocationSpec
            {
                Request = request,
                RowIds = rowIds,
                ResolveText = resolveText,
                RenderText = renderText,
                Title = layoutProfile.QuantitySelectionTitle,
                Description = string.Format(layoutProfile.QuantitySelectionDescription, minimum, maximum),
                Labels = new[] { label }, Maximums = new[] { maximum }, MinimumTotal = minimum,
                InitialValues = initialValues, DraftChanged = draftChanged, IsCurrent = isCurrent,
                ShowInventoryPreview = false, ConfirmLabel = layoutProfile.SelectionConfirmLabel,
                Confirm = values => confirm(values[0])
            });
        }

        public void ShowPaymentConfirmation(string description, Func<bool> isCurrent, Action confirm)
        {
            if (players != null) players.Close();
            var panel = PreparePage(layoutProfile.ResourceAllocationPanelSize);
            if (panel == null) return;
            shell.AddResourceAllocation(panel, new ResourceAllocationSpec
            {
                Title = layoutProfile.ResourceAllocationTitle, Description = description,
                Labels = Array.Empty<string>(), IsCurrent = isCurrent, ShowInventoryPreview = false,
                ConfirmLabel = layoutProfile.SelectionConfirmLabel, Confirm = _ => confirm?.Invoke()
            });
        }

        public void ShowPaymentConfirmation(InteractionRequestProjection request, Func<bool> isCurrent,
            Action confirm, Action decline = null)
        {
            var document = EffectDialogTextDocuments.ResolvePrompt(request, resolveText);
            if (document == null || renderText == null || RenderPaymentConfirmation == null)
                throw new InvalidOperationException("付款宿主缺少正式文档、渲染合同或专用窗口接线。");
            Hide();
            ownsPaymentConfirmation = true;
            RenderPaymentConfirmation(request, document, renderText, isCurrent, confirm, decline);
        }

        public void ShowBoundedResourceAllocation(string description, IReadOnlyList<string> labels,
            IReadOnlyList<int> maximums, int minimumTotal, int maximumTotal,
            IReadOnlyList<int> initialValues, Action<IReadOnlyList<int>> draftChanged,
            Func<bool> isCurrent, Action<IReadOnlyList<int>> confirm, Action cancel)
        {
            ShowBoundedResourceAllocation(null, null, description, labels, maximums, minimumTotal, maximumTotal,
                initialValues, draftChanged, isCurrent, confirm, cancel);
        }

        public void ShowBoundedResourceAllocation(InteractionRequestProjection request, IReadOnlyList<string> rowIds,
            string description, IReadOnlyList<string> labels, IReadOnlyList<int> maximums, int minimumTotal, int maximumTotal,
            IReadOnlyList<int> initialValues, Action<IReadOnlyList<int>> draftChanged,
            Func<bool> isCurrent, Action<IReadOnlyList<int>> confirm, Action cancel)
        {
            if (players != null) players.Close();
            var scroll = initialValues != null && initialValues.Count > 0 ? shell.ScrollPosition : 1f;
            var panel = PreparePage(layoutProfile.ResourceAllocationPanelSize);
            shell.AddResourceAllocation(panel, new ResourceAllocationSpec
            {
                Request = request, RowIds = rowIds, ResolveText = resolveText, RenderText = renderText,
                Title = layoutProfile.ResourceAllocationTitle, Description = description,
                Labels = labels, Maximums = maximums, MinimumTotal = minimumTotal, MaximumTotal = maximumTotal,
                InitialValues = initialValues, DraftChanged = draftChanged, IsCurrent = isCurrent,
                ShowInventoryPreview = false, ConfirmLabel = layoutProfile.SelectionConfirmLabel,
                CancelLabel = layoutProfile.SelectionCancelLabel, Confirm = confirm, Cancel = cancel
            });
            shell.RestoreScroll(scroll);
        }

        public void ShowResourceType(IReadOnlyList<string> labels,
            IReadOnlyList<int> initialValues, Action<IReadOnlyList<int>> draftChanged,
            Func<bool> isCurrent, Action<int> confirm)
        {
            if (players != null) players.Close();
            var maximums = new int[labels.Count];
            for (var i = 0; i < maximums.Length; i++) maximums[i] = 1;
            var panel = PreparePage(layoutProfile.SalePanelSize);
            shell.AddResourceAllocation(panel, new ResourceAllocationSpec
            {
                Title = layoutProfile.ResourceTypeSelectionTitle,
                Description = layoutProfile.ResourceTypeSelectionDescription,
                Labels = labels, Maximums = maximums, ExactTotal = 1, ExclusiveChoice = true,
                InitialValues = initialValues != null && initialValues.Count == labels.Count ? initialValues : new int[labels.Count],
                DraftChanged = draftChanged, IsCurrent = isCurrent,
                ShowInventoryPreview = false, ConfirmLabel = layoutProfile.SelectionConfirmLabel,
                Confirm = values =>
                { for (var i = 0; i < values.Count; i++) if (values[i] == 1) { confirm(i); return; } }
            });
        }

        public void ShowExecutionChoices(string title, string description,
            IReadOnlyList<EffectDialogOption> options, Func<bool> isCurrent)
        {
            var candidates = new List<string>();
            var byId = new Dictionary<string, EffectDialogOption>();
            foreach (var option in options)
            {
                candidates.Add(option.StableId);
                byId.Add(option.StableId, option);
            }
            ShowSelection(title, description, new EffectDialogSelectionSpec
            {
                Request = new InteractionRequestProjection
                {
                    VisibleToViewer = true, CandidateIds = candidates,
                    MinSelections = 1, MaxSelections = 1, AllowDecline = true
                },
                ExecuteImmediately = true,
                Label = id => string.IsNullOrEmpty(byId[id].SourceLabel) ? byId[id].Label : byId[id].SourceLabel,
                DescriptionLabel = id => byId[id].DescriptionLabel,
                CanSelect = id => byId[id].Enabled,
                Select = id =>
                {
                    if (!byId.TryGetValue(id, out var option) || !option.Enabled || !isCurrent() ||
                        option.IsCurrent?.Invoke() == false) return;
                    Hide();
                    option.Select?.Invoke();
                },
                Cancel = Hide, IsCurrent = isCurrent, IsEffectPage = false,
                OptionNamePrefix = "Special Action Option "
            }, true);
        }

        public void ShowCoverSelection(
            IReadOnlyList<string> candidateIds,
            string selectedId,
            Func<string, bool> canCover,
            Action<string> select,
            Action confirm,
            string contextKey = null)
        {
            var candidates = candidateIds == null
                ? new List<string>()
                : new List<string>(candidateIds);
            coverSelectedId = selectedId;
            if (!string.IsNullOrEmpty(contextKey) && contextKey == coverPageKey && coverPage != null &&
                shell.IsVisible && shell.OwnsPage(coverPage.gameObject) && coverConfirm != null &&
                coverPageCandidates != null && coverPageCandidates.SequenceEqual(candidates) &&
                coverRows.Count == candidates.Count && coverRows.Values.All(row => row != null))
            {
                foreach (var pair in coverRows)
                {
                    bool selected = pair.Key == selectedId;
                    if (pair.Value.SelectionImage != null) pair.Value.SelectionImage.enabled = selected;
                    else pair.Value.Outline.enabled = selected;
                }
                int count = string.IsNullOrEmpty(selectedId) ? 0 : 1;
                coverPage.ResourceSummaryText.text = string.Format(layoutProfile.SelectionSummaryFormat, count, 1, 1);
                coverConfirm.interactable = count == 1;
                return;
            }
            var request = new InteractionRequestProjection
            {
                VisibleToViewer = true,
                InteractionId = "character-cover",
                CandidateIds = candidates,
                MinSelections = 1,
                MaxSelections = 1,
                AllowDecline = false,
                Status = "open"
            };
            ShowSelection(
                layoutProfile.CharacterCoverSelectionTitle,
                layoutProfile.CharacterCoverSelectionDescription,
                  new EffectDialogSelectionSpec
                  {
                      IsEffectPage = false,
                      AllowStageFolding = true,
                      CardPickerHint = layoutProfile.CharacterCoverSelectionHint,
                      Request = request,
                    SelectedIds = string.IsNullOrEmpty(selectedId)
                        ? new string[0]
                        : new[] { selectedId },
                    Label = id => CharacterCardPanelPresenter.ResolveCardDisplayName(ResolveCharacterDefinition?.Invoke(id) ?? id),
                    CardSprite = id => cards.GetCharacterFront(ResolveCharacterDefinition?.Invoke(id) ?? id),
                    IsCurrent = () => candidates.Exists(id => canCover == null || canCover(id)),
                    CanSelect = id => canCover == null || canCover(id),
                    OptionNamePrefix = "Character Cover Card ",
                    ConfirmLabel = layoutProfile.SelectionConfirmLabel,
                    CancelLabel = layoutProfile.SelectionCancelLabel,
                    Select = id => select?.Invoke(id),
                    Confirm = () =>
                    {
                        if (!string.IsNullOrEmpty(coverSelectedId)) confirm?.Invoke();
                    }
                },
                true);
            coverPageKey = contextKey;
            coverPageCandidates = candidates;
            coverRows.Clear();
            coverPage = lastSelectionPanel == null ? null : lastSelectionPanel.GetComponentInParent<EffectDialogShellView>();
            coverConfirm = lastSelectionPanel == null ? null : lastSelectionPanel.GetComponentsInChildren<UnityEngine.UI.Button>(true)
                .SingleOrDefault(button => button.gameObject.activeSelf && button.gameObject.name == "Confirm Selection");
            if (lastSelectionPanel != null)
                foreach (var row in lastSelectionPanel.GetComponentsInChildren<FacilityEffectCardView>(true))
                {
                    if (!row.gameObject.activeSelf) continue;
                    var marker = row.Button == null ? null : row.Button.GetComponent<YC.PlayerJourney.PlayerAutomationId>();
                    const string prefix = "selection.card.";
                    if (marker != null && marker.Id != null && marker.Id.StartsWith(prefix, StringComparison.Ordinal))
                        coverRows[marker.Id.Substring(prefix.Length)] = row;
                }
        }

        public void ShowBuildFacilities(
            IReadOnlyList<BuildFacilityOptionQueryResult> options,
            string selectedId,
            Func<bool> isCurrent,
            Action<string> select,
            Action confirm,
            Action cancel)
        {
            var ids = new List<string>();
            var buildable = new HashSet<string>(StringComparer.Ordinal);
            if (options != null)
                for (var i = 0; i < options.Count; i++)
                {
                    var option = options[i];
                    if (option == null || string.IsNullOrEmpty(option.FacilityId)) continue;
                    ids.Add(option.FacilityId);
                    if (option.CanBuild) buildable.Add(option.FacilityId);
                }

            var request = new InteractionRequestProjection
            {
                VisibleToViewer = true,
                InteractionId = "facility-build",
                CandidateIds = ids,
                MinSelections = 1,
                MaxSelections = 1,
                AllowDecline = true,
                Status = "open"
            };
            ShowSelection(
                layoutProfile.BuildFacilitySelectionTitle,
                layoutProfile.BuildFacilitySelectionDescription,
                new EffectDialogSelectionSpec
                {
                    Request = request,
                    SelectedIds = string.IsNullOrEmpty(selectedId) ? new string[0] : new[] { selectedId },
                    Label = id =>
                    {
                        var facility = FacilityCardDatabase.Get(id);
                        var option = FindBuildOption(options, id);
                        var name = facility == null ? id : facility.Name;
                        return option != null && !option.CanBuild &&
                               !string.IsNullOrEmpty(option.Reason)
                            ? name + "（" + string.Format(layoutProfile.BuildUnavailableReasonFormat,
                                option.Reason) + "）"
                            : name;
                    },
                    CardSprite = cards.GetFacility,
                    IsCurrent = isCurrent,
                    CanSelect = id => buildable.Contains(id),
                    OptionNamePrefix = "Build Facility Card ",
                    ConfirmLabel = layoutProfile.BuildConfirmLabel,
                    CancelLabel = layoutProfile.BuildCancelLabel,
                    Select = select,
                    Confirm = confirm,
                    Cancel = cancel
                },
                true);
        }

        public void ShowBuildSlots(
            IReadOnlyList<BuildFacilitySlotOptionResult> slots,
            Func<bool> isCurrent,
            Action<int> select,
            Action cancel)
        {
            var options = new List<EffectDialogOption>();
            if (slots != null)
                for (var i = 0; i < slots.Count; i++)
                {
                    var slot = slots[i];
                    if (slot == null) continue;
                    var label = string.Format(layoutProfile.BuildSlotLabelFormat,
                        slot.CityBoardSlotIndex + 1);
                    if (!slot.IsLegal && !string.IsNullOrEmpty(slot.Reason))
                        label += "（" + string.Format(layoutProfile.BuildUnavailableReasonFormat,
                            slot.Reason) + "）";
                    var slotIndex = slot.CityBoardSlotIndex;
                    options.Add(new EffectDialogOption(label,
                        () => select?.Invoke(slotIndex), slot.IsLegal)
                    {
                        IsCurrent = isCurrent
                    });
                }
            ShowOptions(layoutProfile.BuildSlotSelectionTitle,
                layoutProfile.BuildFacilitySelectionDescription, options, cancel,
                layoutProfile.BuildCancelLabel);
        }

        public void ShowBuildPayments(
            BuildFacilityOptionQueryResult option,
            Func<bool> isCurrent,
            Action<string> select,
            Action cancel)
        {
            if (option == null) return;
            var facility = FacilityCardDatabase.Get(option.FacilityId);
            var goldCost = facility == null ? 0 : facility.GoldVoucherCost;
            var quote = BuildPaymentQuote(option, facility, goldCost);
            var options = new List<EffectDialogOption>
            {
                new EffectDialogOption(layoutProfile.BuildPaymentResourcesLabel,
                    () => select?.Invoke("resources"), option.ResourcesPayment.IsAvailable)
                { IsCurrent = isCurrent },
                new EffectDialogOption(layoutProfile.BuildPaymentGoldLabel,
                    () => select?.Invoke("gold"), option.GoldPayment.IsAvailable)
                { IsCurrent = isCurrent }
            };
            ShowOptions(layoutProfile.BuildPaymentSelectionTitle, quote, options, cancel,
                layoutProfile.BuildCancelLabel);
        }

        private string BuildPaymentQuote(
            BuildFacilityOptionQueryResult option,
            FacilityCardDefinition facility,
            int goldCost)
        {
            var original = facility == null ? new ResourceSet() : facility.ResourceCost;
            var current = option.EffectiveResourceCost;
            var originalText = string.Format(layoutProfile.BuildResourceCostFormat,
                original.Originium, original.OriginiumShard, original.Iron, original.PureOriginium);
            var currentText = string.Format(layoutProfile.BuildResourceCostFormat,
                current.Originium, current.OriginiumShard, current.Iron, current.PureOriginium);
            return string.Format(layoutProfile.BuildPaymentQuoteFormat,
                originalText, currentText, goldCost);
        }

        private static BuildFacilityOptionQueryResult FindBuildOption(
            IReadOnlyList<BuildFacilityOptionQueryResult> options,
            string facilityId)
        {
            if (options == null) return null;
            for (var i = 0; i < options.Count; i++)
                if (options[i] != null && options[i].FacilityId == facilityId) return options[i];
            return null;
        }

        public void ShowBuildQuote(
            string summary,
            Func<bool> isCurrent,
            Action confirm,
            Action back,
            Action cancel)
        {
            var options = new List<EffectDialogOption>
            {
                new EffectDialogOption(layoutProfile.BuildConfirmLabel, confirm)
                { IsCurrent = isCurrent },
                new EffectDialogOption(layoutProfile.BuildBackLabel, back)
                { IsCurrent = isCurrent }
            };
            ShowOptions(layoutProfile.BuildConfirmationTitle, summary, options, cancel,
                layoutProfile.BuildCancelLabel);
        }

        public void ShowResourceSale(
            IReadOnlyList<string> labels,
            IReadOnlyList<int> maximums,
            IReadOnlyList<int> unitPrices,
            Action<IReadOnlyList<int>> confirm,
            Action cancel)
        {
            ShowResourceSaleCore(labels, maximums, unitPrices, confirm, cancel, null, null, null);
        }

        private void ShowResourceSaleCore(
            IReadOnlyList<string> labels, IReadOnlyList<int> maximums, IReadOnlyList<int> unitPrices,
            Action<IReadOnlyList<int>> confirm, Action cancel, IReadOnlyList<int> initialValues,
            Action<IReadOnlyList<int>> draftChanged, Func<bool> isCurrent, string receiptSummary = null,
            IReadOnlyList<int> inventory = null, InteractionRequestProjection request = null)
        {
            var scroll = initialValues != null && initialValues.Count > 0 ? shell.ScrollPosition : 1f;
            var panel = PreparePage(layoutProfile.SalePanelSize);
            shell.AddResourceAllocation(panel, new ResourceAllocationSpec
            {
                Request = request, ResolveText = resolveText, RenderText = renderText,
                Title = layoutProfile.SaleTitle,
                Description = string.IsNullOrEmpty(receiptSummary) ? layoutProfile.SaleDescription :
                    layoutProfile.SaleDescription + "\n" + receiptSummary,
                Labels = labels,
                Maximums = maximums,
                UnitPrices = unitPrices,
                MinimumTotal = 1,
                InitialValues = initialValues,
                DraftChanged = draftChanged,
                IsCurrent = isCurrent,
                LabelNamePrefix = "Character Sale Label ",
                DecreaseNamePrefix = "Character Sale Decrease ",
                ValueNamePrefix = "Character Sale Value ",
                IncreaseNamePrefix = "Character Sale Increase ",
                ConfirmName = "Confirm Character Effect",
                ConfirmLabel = layoutProfile.SaleConfirmLabel,
                CancelName = "Cancel Character Effect",
                CancelLabel = layoutProfile.SaleFinishLabel,
                CancelDraftLabel = layoutProfile.SaleDiscardDraftLabel,
                SummaryName = "Character Sale Summary",
                RowStartY = layoutProfile.CharacterResourceSaleRowStartY,
                FormatRowLabel = (index, label, price) => string.Format(layoutProfile.SaleInventoryFormat,
                    label, inventory != null && index < inventory.Count ? Mathf.Max(0, inventory[index]) :
                        maximums != null && index < maximums.Count ? maximums[index] : 0, price),
                FormatSummary = values =>
                {
                    var totalGold = 0;
                    for (var i = 0; i < values.Count; i++)
                    {
                        var price = unitPrices != null && i < unitPrices.Count ? unitPrices[i] : 0;
                        totalGold += values[i] * price;
                    }

                    return string.Format(layoutProfile.SaleSummaryFormat, totalGold);
                },
                Confirm = confirm,
                Cancel = cancel,
                CloseBeforeConfirm = true,
                CloseBeforeCancel = true
            });
            shell.RestoreScroll(scroll);
        }

        public void ShowResourceSaleCandidates(
            IReadOnlyList<string> candidateIds,
            Action<IReadOnlyList<string>> confirm,
            Action cancel,
            IReadOnlyList<int> initialValues = null,
            Action<IReadOnlyList<int>> draftChanged = null,
            Func<bool> isCurrent = null,
            IReadOnlyList<int> liveMaximums = null,
            string receiptSummary = null, InteractionRequestProjection request = null)
        {
            var resourceIds = new[] { "originium", "originium-shard", "iron", "pure-originium" };
            var labels = new[] { "源岩", "源石碎片", "异铁", "至纯源石" };
            var maximums = new int[resourceIds.Length];
            for (var i = 0; candidateIds != null && i < candidateIds.Count; i++)
            {
                var parts = (candidateIds[i] ?? string.Empty).Split('|');
                if (parts.Length != 3 || parts[0] != "sale")
                {
                    continue;
                }

                var resourceIndex = Array.IndexOf(resourceIds, parts[1]);
                int amount;
                if (resourceIndex >= 0 &&
                    int.TryParse(parts[2], out amount) &&
                    amount > maximums[resourceIndex])
                {
                    maximums[resourceIndex] = amount;
                }
            }

            for (var i = 0; i < maximums.Length; i++)
                if (liveMaximums != null && i < liveMaximums.Count)
                    maximums[i] = Mathf.Min(maximums[i], Mathf.Max(0, liveMaximums[i]));
            var canFinish = false;
            for (var i = 0; candidateIds != null && i < candidateIds.Count; i++)
                if (candidateIds[i] == "sale|originium|0") canFinish = true;
            ShowResourceSaleCore(
                labels,
                maximums,
                new[]
                {
                    YC.Domain.Economy.ResourceSaleService.OriginiumUnitPrice,
                    YC.Domain.Economy.ResourceSaleService.OriginiumShardUnitPrice,
                    YC.Domain.Economy.ResourceSaleService.IronUnitPrice,
                    YC.Domain.Economy.ResourceSaleService.PureOriginiumUnitPrice
                },
                values =>
                {
                    var selected = new List<string>();
                    for (var i = 0; i < values.Count && i < resourceIds.Length; i++)
                    {
                        if (values[i] > 0)
                        {
                            selected.Add("sale|" + resourceIds[i] + "|" + values[i]);
                        }
                    }

                    if (selected.Count == 0) return;
                    confirm(selected.AsReadOnly());
                },
                canFinish ? (Action)(() => confirm(new[] { "sale|originium|0" })) : cancel,
                initialValues, draftChanged, isCurrent, receiptSummary, liveMaximums, request);
        }

        public void Hide()
        {
            if (ownsPaymentConfirmation) { ownsPaymentConfirmation = false; HidePaymentConfirmation?.Invoke(); }
            coverPageKey = coverSelectedId = null;
            coverPage = null; coverConfirm = null; coverPageCandidates = null;
            coverRows.Clear(); lastSelectionPanel = null;
            if (players != null) { players.Close(); UnityEngine.Object.Destroy(players.gameObject); players = null; }
            shell.Hide();
        }

        private RectTransform PreparePage(Vector2 size, bool effectPage = true, bool cardPicker = false)
        {
            if (ownsPaymentConfirmation) { ownsPaymentConfirmation = false; HidePaymentConfirmation?.Invoke(); }
            return shell.PreparePage(
                canvas,
                "Character Card Effect Overlay",
                "Character Card Effect Panel",
                size,
                layoutProfile.CharacterPanelPosition, blockBackgroundInput: cardPicker,
                effectPage: effectPage, cardPicker: cardPicker);
        }
    }
}
