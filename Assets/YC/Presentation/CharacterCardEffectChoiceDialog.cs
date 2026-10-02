using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.State;
using YC.Presentation.Workflows;
using UnityEngine;

namespace YC.Presentation
{
    /// <summary>角色牌非地图参数的专用结算弹窗；不读取或修改共享游戏状态。</summary>
    internal sealed class CharacterCardEffectChoiceDialog
    {
        private readonly RectTransform canvas;
        private readonly EffectDialogShell shell;
        private readonly EffectDialogLayoutProfile layoutProfile;
        private readonly CardVisualCatalog cards;
        private readonly GameplayDialogRegistry registry;
        private PlayerSelectionPageView players;

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
            get { return shell.IsShowing || players != null && players.gameObject.activeInHierarchy; }
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

        private void ShowOptionsCore(string title, string description, IReadOnlyList<EffectDialogOption> options,
            Action cancel, string cancelLabel, bool effectPage)
        {
            var optionCount = options == null ? 0 : options.Count;
            var panelSize = layoutProfile.CharacterOptionsPanelBaseSize;
            panelSize.y = Mathf.Clamp(
                panelSize.y + optionCount * layoutProfile.CharacterOptionsRowHeight,
                layoutProfile.CharacterOptionsPanelMinHeight,
                layoutProfile.CharacterOptionsPanelMaxHeight);
            var panel = Rebuild(panelSize, effectPage);
            EffectDialogShell.AddHeading(
                panel,
                title,
                description,
                layoutProfile.CharacterOptionsDescriptionHeight,
                "Character Effect Title",
                "Character Effect Description",
                24);
            var content = EffectDialogShell.AddOptionScroll(
                panel,
                "Character Effect Options Scroll",
                cancel == null
                    ? layoutProfile.CharacterOptionsScrollBottom
                    : layoutProfile.CharacterOptionsScrollBottomWithCancel,
                layoutProfile.CharacterOptionsScrollTop);
            EffectDialogShell.AddOptions(content, options, "Character Effect Option ", Hide);

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
            spec.CardSprite = id => cards.GetFacility(id) ?? cards.GetCharacterFront(id) ?? cards.GetEvent(id);
            var panel = Rebuild(layoutProfile.SelectionPanelSize, spec.IsEffectPage, spec.HasCardArtwork());
            EffectDialogShell.AddHeading(panel, title, description);
            shell.AddSelection(panel, spec);
            shell.RestoreScroll(scroll);
        }

        public void ShowExecutionChoices(string title, string description,
            IReadOnlyList<EffectDialogOption> options, Func<bool> isCurrent)
        {
            string selected = null;
            var candidates = new List<string>();
            var byId = new Dictionary<string, EffectDialogOption>();
            foreach (var option in options)
            {
                candidates.Add(option.StableId);
                byId.Add(option.StableId, option);
            }
            Action render = null;
            render = () => ShowSelection(title, description, new EffectDialogSelectionSpec
            {
                Request = new InteractionRequestProjection
                {
                    VisibleToViewer = true, CandidateIds = candidates,
                    MinSelections = 1, MaxSelections = 1, AllowDecline = true
                },
                SelectedIds = selected == null ? new string[0] : new[] { selected },
                Label = id => byId[id].Label,
                SourceLabel = id => byId[id].SourceLabel,
                DescriptionLabel = id => byId[id].DescriptionLabel,
                CanSelect = id => byId[id].Enabled,
                Select = id => { selected = id; render(); },
                Confirm = () =>
                {
                    if (selected == null || !byId[selected].Enabled || !isCurrent()) return;
                    var option = byId[selected];
                    Hide();
                    option.Select?.Invoke();
                },
                Cancel = Hide, IsCurrent = isCurrent, IsEffectPage = false,
                OptionNamePrefix = "Special Action Option "
            }, true);
            render();
        }

        public void ShowCoverSelection(
            IReadOnlyList<string> candidateIds,
            string selectedId,
            Func<string, bool> canCover,
            Action<string> select,
            Action confirm)
        {
            var candidates = candidateIds == null
                ? new List<string>()
                : new List<string>(candidateIds);
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
                    Label = CharacterCardPanelPresenter.ResolveCardDisplayName,
                    CardSprite = id => cards.GetCharacterFront(id),
                    IsCurrent = () => candidates.Exists(id => canCover == null || canCover(id)),
                    CanSelect = id => canCover == null || canCover(id),
                    OptionNamePrefix = "Character Cover Card ",
                    ConfirmLabel = layoutProfile.SelectionConfirmLabel,
                    CancelLabel = layoutProfile.SelectionCancelLabel,
                    Select = id => select?.Invoke(id),
                    Confirm = () =>
                    {
                        if (!string.IsNullOrEmpty(selectedId)) confirm?.Invoke();
                    }
                },
                true);
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
            IReadOnlyList<int> inventory = null)
        {
            var scroll = initialValues != null && initialValues.Count > 0 ? shell.ScrollPosition : 1f;
            var panel = Rebuild(layoutProfile.SalePanelSize);
            shell.AddResourceAllocation(panel, new ResourceAllocationSpec
            {
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
            string receiptSummary = null)
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
                initialValues, draftChanged, isCurrent, receiptSummary, liveMaximums);
        }

        public void Hide()
        {
            if (players != null) { players.Close(); UnityEngine.Object.Destroy(players.gameObject); players = null; }
            shell.Hide();
        }

        private RectTransform Rebuild(Vector2 size, bool effectPage = true, bool cardPicker = false)
        {
            return shell.Rebuild(
                canvas,
                "Character Card Effect Overlay",
                "Character Card Effect Panel",
                size,
                layoutProfile.CharacterPanelPosition, blockBackgroundInput: cardPicker,
                effectPage: effectPage, cardPicker: cardPicker);
        }
    }
}
