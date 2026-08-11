using System;
using System.Collections.Generic;
using UnityEngine;

namespace YC.Presentation
{
    /// <summary>
    /// 特殊行动待结算专用弹窗。规则状态由命令处理器持有；这里仅收集支付并保留地图可交互提示。
    /// </summary>
    internal sealed class SpecialActionChoiceDialog
    {
        private readonly Func<RectTransform> getCanvas;
        private readonly EffectDialogShell shell;
        private readonly EffectDialogLayoutProfile layoutProfile;
        private EffectDialogCollapsiblePanel collapsiblePanel;

        internal SpecialActionChoiceDialog(
            GameplayDialogRegistry dialogRegistry,
            Func<RectTransform> canvasProvider)
        {
            getCanvas = canvasProvider ?? throw new ArgumentNullException(nameof(canvasProvider));
            if (dialogRegistry == null) throw new ArgumentNullException(nameof(dialogRegistry));
            layoutProfile = dialogRegistry.EffectDialogLayoutProfile;
            var layoutReason = string.Empty;
            if (layoutProfile == null || !layoutProfile.TryValidateConfiguration(out layoutReason))
            {
                throw new InvalidOperationException(
                    "SpecialActionChoiceDialog 缺少有效的显式布局 Profile：" + layoutReason);
            }

            shell = new EffectDialogShell(dialogRegistry);
        }

        public bool IsShowing
        {
            get { return shell.IsShowing; }
        }

        public bool IsCollapsed
        {
            get { return collapsiblePanel != null && collapsiblePanel.IsCollapsed; }
        }

        public void ShowCompositePayment(
            int maximumOriginium,
            int maximumIron,
            Action<IReadOnlyList<int>> confirm)
        {
            ShowCompositePayment(
                maximumOriginium,
                maximumIron,
                confirm,
                null);
        }

        public void ShowCompositePayment(
            int maximumOriginium,
            int maximumIron,
            Action<IReadOnlyList<int>> confirm,
            Action cancel)
        {
            collapsiblePanel = null;
            var canvas = getCanvas();
            var panel = shell.Rebuild(
                canvas,
                "Special Action Choice Overlay",
                "Special Action Choice Panel",
                layoutProfile.SpecialActionPaymentPanelSize,
                layoutProfile.SpecialActionPaymentPanelPosition,
                true);
            if (panel == null)
            {
                return;
            }

            shell.AddResourceAllocation(panel, new ResourceAllocationSpec
            {
                Title = "复合动力系统 · 支付",
                Description = "固定支付 1 份源石碎片，并在源岩与异铁之间恰好支付合计 3 份材料。",
                Labels = new[] { "源岩", "异铁" },
                Maximums = new[] { Math.Max(0, maximumOriginium), Math.Max(0, maximumIron) },
                ExactTotal = 3,
                LabelWidth = layoutProfile.ResourceLabelWidth,
                LabelHeight = layoutProfile.ResourceLabelHeight,
                LabelX = layoutProfile.ResourceLabelX,
                DecreaseX = layoutProfile.ResourceDecreaseX,
                ValueX = layoutProfile.ResourceValueX,
                IncreaseX = layoutProfile.ResourceIncreaseX,
                SummaryName = "Special Action Payment Summary",
                FormatSummary = values =>
                {
                    var total = values == null || values.Count < 2 ? 0 : values[0] + values[1];
                    return "固定 1 份源石碎片；材料已选择 " + total + "/3";
                },
                ConfirmName = "Confirm Special Action Payment",
                ConfirmLabel = "确认支付",
                CancelName = "Cancel Special Action Payment",
                CancelLabel = "取消支付",
                Cancel = cancel,
                Confirm = confirm,
                CloseBeforeConfirm = true,
                CloseBeforeCancel = true
            });
        }

        public void ShowCollapsibleMapPrompt(
            string title,
            string description,
            string summary)
        {
            collapsiblePanel = null;
            var canvas = getCanvas();
            var panel = shell.Rebuild(
                canvas,
                "Special Action Choice Overlay",
                "Special Action Choice Panel",
                layoutProfile.SpecialActionMapPromptPanelSize,
                layoutProfile.MapPromptPanelPosition);
            if (panel == null)
            {
                return;
            }

            var expandedContent = shell.ConfigureCollapsiblePanel(
                canvas,
                layoutProfile.SpecialActionMapPromptPanelSize,
                summary,
                true,
                "Special Action Expanded Content",
                "Special Action Collapsed Summary",
                "Special Action Collapse Toggle",
                "Special Action Collapse Triangle");
            collapsiblePanel = shell.CollapsiblePanel;
            EffectDialogShell.AddHeading(
                expandedContent,
                title,
                description,
                layoutProfile.MapPromptDescriptionHeight,
                addDragHandle: false);

        }

        public void Hide()
        {
            shell.Hide();
            collapsiblePanel = null;
        }
    }
}
