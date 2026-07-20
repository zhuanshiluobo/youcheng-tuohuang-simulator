using System;
using System.Collections.Generic;
using UnityEngine;

namespace YC.Presentation
{
    /// <summary>角色牌非地图参数的专用结算弹窗；不读取或修改共享游戏状态。</summary>
    internal sealed class CharacterCardEffectChoiceDialog
    {
        private readonly EffectDialogShell shell = new EffectDialogShell();

        public bool IsShowing
        {
            get { return shell.IsShowing; }
        }

        public void ShowOptions(
            RectTransform canvas,
            string title,
            string description,
            IReadOnlyList<EffectDialogOption> options,
            Action cancel = null)
        {
            if (canvas == null)
            {
                return;
            }

            var optionCount = options == null ? 0 : options.Count;
            var panelHeight = Mathf.Clamp(218f + optionCount * 62f, 330f, 610f);
            var panel = Rebuild(canvas, new Vector2(680f, panelHeight));
            EffectDialogShell.AddHeading(
                panel,
                title,
                description,
                72f,
                "Character Effect Title",
                "Character Effect Description",
                24);
            var content = EffectDialogShell.AddOptionScroll(
                panel,
                "Character Effect Options Scroll",
                cancel == null ? 28f : 82f,
                142f);
            EffectDialogShell.AddOptions(content, options, "Character Effect Option ", Hide);

            if (cancel != null)
            {
                var cancelButton = EffectDialogShell.CreateButton(panel, "Cancel Character Effect", "取消", 17);
                EffectDialogShell.SetRect(
                    cancelButton.GetComponent<RectTransform>(),
                    new Vector2(0.5f, 0f),
                    new Vector2(0.5f, 0f),
                    new Vector2(180f, 44f),
                    new Vector2(0f, 28f));
                cancelButton.onClick.AddListener(() =>
                {
                    Hide();
                    cancel();
                });
            }
        }

        public void ShowResourceSale(
            RectTransform canvas,
            IReadOnlyList<string> labels,
            IReadOnlyList<int> maximums,
            IReadOnlyList<int> unitPrices,
            Action<IReadOnlyList<int>> confirm,
            Action cancel)
        {
            if (canvas == null)
            {
                return;
            }

            var panel = Rebuild(canvas, new Vector2(690f, 570f));
            shell.AddResourceAllocation(panel, new ResourceAllocationSpec
            {
                Title = "坎诺特策略：秘密渠道",
                Description = "选择要出售的资源数量，然后统一确认结算。",
                Labels = labels,
                Maximums = maximums,
                UnitPrices = unitPrices,
                LabelNamePrefix = "Character Sale Label ",
                DecreaseNamePrefix = "Character Sale Decrease ",
                ValueNamePrefix = "Character Sale Value ",
                IncreaseNamePrefix = "Character Sale Increase ",
                ConfirmName = "Confirm Character Effect",
                CancelName = "Cancel Character Effect",
                CancelLabel = "取消",
                SummaryName = "Character Sale Summary",
                RowStartY = -166f,
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

        public void Hide()
        {
            shell.Hide();
        }

        private RectTransform Rebuild(RectTransform canvas, Vector2 size)
        {
            return shell.Rebuild(
                canvas,
                "Character Card Effect Overlay",
                "Character Card Effect Panel",
                size,
                Vector2.zero);
        }
    }
}
