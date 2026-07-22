using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>
    /// 特殊行动待结算专用弹窗。规则状态由命令处理器持有；这里仅收集支付并保留地图可交互提示。
    /// </summary>
    internal sealed class SpecialActionChoiceDialog
    {
        private readonly EffectDialogShell shell = new EffectDialogShell();
        private EffectDialogCollapsiblePanel collapsiblePanel;

        public bool IsShowing
        {
            get { return shell.IsShowing; }
        }

        public bool IsCollapsed
        {
            get { return collapsiblePanel != null && collapsiblePanel.IsCollapsed; }
        }

        public void ShowCompositePayment(
            RectTransform canvas,
            int maximumOriginium,
            int maximumIron,
            Action<IReadOnlyList<int>> confirm)
        {
            ShowCompositePayment(
                canvas,
                maximumOriginium,
                maximumIron,
                confirm,
                null);
        }

        public void ShowCompositePayment(
            RectTransform canvas,
            int maximumOriginium,
            int maximumIron,
            Action<IReadOnlyList<int>> confirm,
            Action cancel)
        {
            collapsiblePanel = null;
            var panel = shell.Rebuild(
                canvas,
                "Special Action Choice Overlay",
                "Special Action Choice Panel",
                new Vector2(650f, 500f),
                Vector2.zero,
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
                LabelWidth = 230f,
                LabelHeight = 48f,
                LabelX = 145f,
                DecreaseX = 320f,
                ValueX = 390f,
                IncreaseX = 460f,
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
            RectTransform canvas,
            string title,
            string description,
            string summary)
        {
            collapsiblePanel = null;
            var panel = shell.Rebuild(
                canvas,
                "Special Action Choice Overlay",
                "Special Action Choice Panel",
                new Vector2(650f, 260f),
                new Vector2(0f, 310f));
            if (panel == null)
            {
                return;
            }

            var collapsedSummary = EffectDialogShell.CreateText(
                panel,
                "Special Action Collapsed Summary",
                summary,
                18,
                TextAnchor.MiddleLeft);
            collapsedSummary.fontStyle = FontStyle.Bold;
            collapsedSummary.color = UiTheme.GoldText;
            EffectDialogShell.SetRect(
                collapsedSummary.rectTransform,
                new Vector2(0.06f, 1f),
                new Vector2(0.72f, 1f),
                new Vector2(0f, 42f),
                new Vector2(0f, -29f));

            var expandedContentObject = new GameObject("Special Action Expanded Content", typeof(RectTransform));
            expandedContentObject.transform.SetParent(panel, false);
            var expandedContent = expandedContentObject.GetComponent<RectTransform>();
            EffectDialogShell.Stretch(expandedContent, 0f);
            EffectDialogShell.AddHeading(
                expandedContent,
                title,
                description,
                74f,
                addDragHandle: false);

            var toggle = EffectDialogShell.CreateButton(
                panel,
                "Special Action Collapse Toggle",
                "收起卡片",
                14);
            var toggleRect = toggle.GetComponent<RectTransform>();
            var toggleText = toggle.GetComponentInChildren<Text>();
            var toggleIcon = UguiUtility.CreateTriangleIcon(
                toggleRect,
                "Special Action Collapse Triangle",
                true);

            collapsiblePanel = panel.gameObject.AddComponent<EffectDialogCollapsiblePanel>();
            collapsiblePanel.Configure(new EffectDialogCollapseSpec
            {
                Panel = panel,
                Canvas = canvas == null ? null : canvas.GetComponentInParent<Canvas>(),
                OverlayImage = panel.parent == null ? null : panel.parent.GetComponent<Image>(),
                ExpandedContent = expandedContentObject,
                CollapsedSummaryText = collapsedSummary,
                ToggleRect = toggleRect,
                ToggleText = toggleText,
                ToggleIcon = toggleIcon,
                ExpandedSize = new Vector2(650f, 260f),
                StartCollapsed = true
            });
            toggle.onClick.AddListener(collapsiblePanel.Toggle);
        }

        public void Hide()
        {
            shell.Hide();
            collapsiblePanel = null;
        }
    }
}
