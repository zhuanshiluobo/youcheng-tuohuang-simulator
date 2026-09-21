using System;
using System.Collections.Generic;
using UnityEngine;

namespace YC.Presentation
{
    /// <summary>角色牌非地图参数的专用结算弹窗；不读取或修改共享游戏状态。</summary>
    internal sealed class CharacterCardEffectChoiceDialog
    {
        private readonly RectTransform canvas;
        private readonly EffectDialogShell shell;
        private readonly EffectDialogLayoutProfile layoutProfile;

        internal CharacterCardEffectChoiceDialog(
            GameplayDialogRegistry dialogRegistry,
            RectTransform configuredCanvas)
        {
            canvas = configuredCanvas ?? throw new ArgumentNullException(nameof(configuredCanvas));
            if (dialogRegistry == null) throw new ArgumentNullException(nameof(dialogRegistry));
            layoutProfile = dialogRegistry.EffectDialogLayoutProfile;
            var layoutReason = string.Empty;
            if (layoutProfile == null || !layoutProfile.TryValidateConfiguration(out layoutReason))
            {
                throw new InvalidOperationException(
                    "CharacterCardEffectChoiceDialog 缺少有效的显式布局 Profile：" + layoutReason);
            }

            shell = new EffectDialogShell(dialogRegistry);
        }

        public bool IsShowing
        {
            get { return shell.IsShowing; }
        }

        public void ShowOptions(
            string title,
            string description,
            IReadOnlyList<EffectDialogOption> options,
            Action cancel = null)
        {
            var optionCount = options == null ? 0 : options.Count;
            var panelSize = layoutProfile.CharacterOptionsPanelBaseSize;
            panelSize.y = Mathf.Clamp(
                panelSize.y + optionCount * layoutProfile.CharacterOptionsRowHeight,
                layoutProfile.CharacterOptionsPanelMinHeight,
                layoutProfile.CharacterOptionsPanelMaxHeight);
            var panel = Rebuild(panelSize);
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
                var cancelButton = EffectDialogShell.CreateButton(panel, "Cancel Character Effect", "取消", 17);
                layoutProfile.CharacterOptionsCancelButtonLayout.ApplyTo(
                    cancelButton.GetComponent<RectTransform>());
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

        public void ShowResourceSale(
            IReadOnlyList<string> labels,
            IReadOnlyList<int> maximums,
            IReadOnlyList<int> unitPrices,
            Action<IReadOnlyList<int>> confirm,
            Action cancel)
        {
            var panel = Rebuild(layoutProfile.CharacterResourceSalePanelSize);
            shell.AddResourceAllocation(panel, new ResourceAllocationSpec
            {
                Title = "坎诺特策略：秘密渠道",
                Description = "选择要出售的资源数量，然后统一确认结算。",
                Labels = labels,
                Maximums = maximums,
                UnitPrices = unitPrices,
                MinimumTotal = 0,
                LabelNamePrefix = "Character Sale Label ",
                DecreaseNamePrefix = "Character Sale Decrease ",
                ValueNamePrefix = "Character Sale Value ",
                IncreaseNamePrefix = "Character Sale Increase ",
                ConfirmName = "Confirm Character Effect",
                CancelName = "Cancel Character Effect",
                CancelLabel = "取消",
                SummaryName = "Character Sale Summary",
                RowStartY = layoutProfile.CharacterResourceSaleRowStartY,
                FormatRowLabel = (index, label, price) => label + "（每个 " + price + " 金券）",
                FormatSummary = values =>
                {
                    var totalGold = 0;
                    for (var i = 0; i < values.Count; i++)
                    {
                        var price = unitPrices != null && i < unitPrices.Count ? unitPrices[i] : 0;
                        totalGold += values[i] * price;
                    }

                    return "预计获得 " + totalGold + " 金券";
                },
                Confirm = confirm,
                Cancel = cancel,
                CloseBeforeConfirm = true,
                CloseBeforeCancel = true
            });
        }

        public void ShowResourceSaleCandidates(
            IReadOnlyList<string> candidateIds,
            Action<IReadOnlyList<string>> confirm,
            Action cancel)
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

            ShowResourceSale(
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

                    if (selected.Count == 0) selected.Add("sale|originium|0");
                    confirm(selected.AsReadOnly());
                },
                cancel);
        }

        public void Hide()
        {
            shell.Hide();
        }

        private RectTransform Rebuild(Vector2 size)
        {
            return shell.Rebuild(
                canvas,
                "Character Card Effect Overlay",
                "Character Card Effect Panel",
                size,
                layoutProfile.CharacterPanelPosition);
        }
    }
}
