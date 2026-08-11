using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    internal sealed class EffectDialogCollapseSpec
    {
        public EffectDialogCollapseSpec(EffectDialogLayoutProfile layoutProfile)
        {
            var reason = string.Empty;
            if (layoutProfile == null || !layoutProfile.TryValidateConfiguration(out reason))
            {
                throw new InvalidOperationException(
                    "EffectDialogCollapseSpec 缺少有效的显式布局 Profile：" + reason);
            }

            CollapsedHeight = layoutProfile.CollapsedHeight;
            CollapsedOverlayColor = layoutProfile.CollapsedOverlayColor;
            CollapsedOverlayRaycastTarget = layoutProfile.CollapsedOverlayRaycastTarget;
            CollapsedToggleLayout = layoutProfile.CollapsedToggleLayout;
            ExpandedToggleLayout = layoutProfile.ExpandedToggleLayout;
        }

        public RectTransform Panel;
        public Canvas Canvas;
        public Image OverlayImage;
        public GameObject ExpandedContent;
        public Text CollapsedSummaryText;
        public RectTransform ToggleRect;
        public Text ToggleText;
        public Image ToggleIcon;
        public Vector2 ExpandedSize;
        public float CollapsedHeight = 58f;
        public string CollapseLabel = "\u6536\u8d77\u5361\u7247";
        public string ExpandLabel = "\u5c55\u5f00\u5361\u7247";
        public bool StartCollapsed;
        public bool ClampToCanvasBounds;
        public Color CollapsedOverlayColor = Color.clear;
        public bool CollapsedOverlayRaycastTarget;
        public EffectDialogRectLayout CollapsedToggleLayout;
        public EffectDialogRectLayout ExpandedToggleLayout;
    }

    internal sealed class EffectDialogOption
    {
        public EffectDialogOption(string label, Action select, bool enabled = true)
        {
            Label = label ?? string.Empty;
            Select = select;
            Enabled = enabled;
        }

        public string Label { get; private set; }

        public Action Select { get; private set; }

        public bool Enabled { get; private set; }
    }

    internal sealed class ResourceAllocationSpec
    {
        public string Title = string.Empty;
        public string Description = string.Empty;
        public IReadOnlyList<string> Labels;
        public IReadOnlyList<int> Maximums;
        public IReadOnlyList<int> UnitPrices;
        public int ExactTotal = -1;
        public string LabelNamePrefix = "Resource Label ";
        public string DecreaseNamePrefix = "Decrease ";
        public string ValueNamePrefix = "Value ";
        public string IncreaseNamePrefix = "Increase ";
        public string ConfirmName = "Confirm";
        public string ConfirmLabel = "确认结算";
        public string CancelName = "Skip";
        public string CancelLabel = "跳过";
        public string SummaryName = string.Empty;
        public float RowStartY = -168f;
        public float RowSpacing = 68f;
        public float LabelWidth = 280f;
        public float LabelHeight = 46f;
        public float LabelX = 175f;
        public float DecreaseX = 366f;
        public float ValueX = 432f;
        public float IncreaseX = 498f;
        public Func<int, string, int, string> FormatRowLabel;
        public Func<IReadOnlyList<int>, string> FormatSummary;
        public Action<IReadOnlyList<int>> Confirm;
        public Action Cancel;
        public bool CloseBeforeConfirm;
        public bool CloseBeforeCancel;
    }

    /// <summary>运行时效果弹窗共享的无领域语义 UI 壳层。</summary>
    internal sealed class EffectDialogShell
    {
        // 游戏内效果弹窗统一位于设置/日志按钮（120）之下、其余常驻游戏 UI 之上。
        internal const int SortingOrder = 119;

        private readonly GameplayDialogRegistry registry;
        private EffectDialogShellView view;

        internal EffectDialogShell(GameplayDialogRegistry configuredRegistry)
        {
            registry = configuredRegistry ?? throw new ArgumentNullException(nameof(configuredRegistry));
        }

        public bool IsShowing
        {
            get { return view != null; }
        }

        public RectTransform Rebuild(
            RectTransform canvas,
            string overlayName,
            string panelName,
            Vector2 size,
            Vector2 position,
            bool blockBackgroundInput = false)
        {
            Hide();
            if (canvas == null)
            {
                return null;
            }

            if (registry == null)
            {
                throw new InvalidOperationException("EffectDialogShell 缺少显式 GameplayDialogRegistry 注入。");
            }

            view = registry.InstantiateEffectDialogShell(canvas);
            if (view == null)
            {
                return null;
            }

            if (!view.TryValidateConfiguration(out var reason))
            {
                var invalidView = view;
                view = null;
                DestroyView(invalidView);
                throw new InvalidOperationException(reason);
            }

            view.PrepareForUse(overlayName, panelName, size, position, blockBackgroundInput);
            return view.ExpandedContent;
        }

        public void Hide()
        {
            if (view == null)
            {
                return;
            }

            var releasedView = view;
            view = null;
            DestroyView(releasedView);
        }

        internal RectTransform ConfigureCollapsiblePanel(
            RectTransform canvas,
            Vector2 expandedSize,
            string summary,
            bool startCollapsed,
            string expandedContentName = null,
            string collapsedSummaryName = null,
            string toggleName = null,
            string toggleIconName = null)
        {
            if (view == null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(expandedContentName))
            {
                view.ExpandedContent.gameObject.name = expandedContentName;
            }

            if (!string.IsNullOrEmpty(collapsedSummaryName))
            {
                view.CollapsedSummaryText.gameObject.name = collapsedSummaryName;
            }

            if (!string.IsNullOrEmpty(toggleName))
            {
                view.CollapseButton.gameObject.name = toggleName;
            }

            if (!string.IsNullOrEmpty(toggleIconName))
            {
                view.CollapseButtonIcon.gameObject.name = toggleIconName;
            }

            view.CollapsedSummaryText.text = summary ?? string.Empty;
            view.CollapsedSummaryText.fontStyle = FontStyle.Bold;
            view.CollapsedSummaryText.color = UiTheme.GoldText;
            view.LayoutProfile.CollapsedSummaryLayout.ApplyTo(
                view.CollapsedSummaryText.rectTransform);
            view.CollapsedSummaryText.gameObject.SetActive(false);
            view.CollapseButton.gameObject.SetActive(true);
            view.DragHandle.enabled = false;
            view.CollapsiblePanel.Configure(new EffectDialogCollapseSpec(view.LayoutProfile)
            {
                Panel = view.Panel,
                Canvas = canvas == null ? null : canvas.GetComponentInParent<Canvas>(),
                OverlayImage = view.OverlayImage,
                ExpandedContent = view.ExpandedContent.gameObject,
                CollapsedSummaryText = view.CollapsedSummaryText,
                ToggleRect = view.CollapseButton.GetComponent<RectTransform>(),
                ToggleText = view.CollapseButtonText,
                ToggleIcon = view.CollapseButtonIcon,
                ExpandedSize = expandedSize,
                StartCollapsed = startCollapsed,
                ClampToCanvasBounds = true
            });
            view.CollapseButton.onClick.RemoveAllListeners();
            view.CollapseButton.onClick.AddListener(view.CollapsiblePanel.Toggle);
            return view.ExpandedContent;
        }

        internal EffectDialogCollapsiblePanel CollapsiblePanel =>
            view == null ? null : view.CollapsiblePanel;

        private static void DestroyView(EffectDialogShellView target)
        {
            if (target == null)
            {
                return;
            }

            target.gameObject.SetActive(false);
            if (UnityEngine.Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target.gameObject);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target.gameObject);
            }
        }

        public static RectTransform AddOptionScroll(
            RectTransform panel,
            string name,
            float bottom,
            float top)
        {
            var shellView = ResolveView(panel);
            return shellView.ConfigureOptionScroll(name, bottom, top);
        }

        public static void AddOptions(
            RectTransform content,
            IReadOnlyList<EffectDialogOption> options,
            string buttonNamePrefix,
            Action beforeSelect)
        {
            if (content == null || options == null)
            {
                return;
            }

            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                var row = ResolveView(content).CreateOptionRow(content);
                row.gameObject.name = buttonNamePrefix + i;
                row.Label.text = option.Label;
                row.Label.fontSize = 18;
                row.LayoutElement.preferredHeight = 54f;
                var button = row.Button;
                button.onClick.RemoveAllListeners();
                button.interactable = option.Enabled;
                row.Background.color = option.Enabled
                    ? UiTheme.ButtonBackground
                    : UiTheme.DisabledButtonBackground;
                if (!option.Enabled)
                {
                    continue;
                }

                var select = option.Select;
                var invoked = false;
                button.onClick.AddListener(() =>
                {
                    if (invoked)
                    {
                        return;
                    }

                    invoked = true;
                    beforeSelect?.Invoke();
                    select?.Invoke();
                });
            }
        }

        public void AddResourceAllocation(RectTransform panel, ResourceAllocationSpec spec)
        {
            AddHeading(panel, spec.Title, spec.Description);
            var shellView = ResolveView(panel);
            var count = spec.Labels == null ? 0 : spec.Labels.Count;
            var values = new int[count];
            if (spec.ExactTotal >= 0 && count > 0)
            {
                values[0] = Mathf.Min(
                    spec.ExactTotal,
                    spec.Maximums != null && spec.Maximums.Count > 0 ? spec.Maximums[0] : spec.ExactTotal);
            }

            var valueTexts = new Text[count];
            var decreaseButtons = new Button[count];
            var increaseButtons = new Button[count];
            Text summaryText = null;
            if (!string.IsNullOrEmpty(spec.SummaryName))
            {
                summaryText = shellView.ResourceSummaryText;
                summaryText.gameObject.name = spec.SummaryName;
                summaryText.gameObject.SetActive(true);
                summaryText.text = string.Empty;
                summaryText.color = UiTheme.GoldText;
                summaryText.fontStyle = FontStyle.Bold;
                shellView.LayoutProfile.ResourceSummaryLayout.ApplyTo(
                    summaryText.rectTransform);
            }

            Button confirmButton = null;
            Action refresh = () =>
            {
                var total = 0;
                for (var i = 0; i < count; i++)
                {
                    total += values[i];
                }

                for (var i = 0; i < count; i++)
                {
                    valueTexts[i].text = values[i].ToString();
                    decreaseButtons[i].interactable = values[i] > 0;
                    var maximum = spec.Maximums != null && i < spec.Maximums.Count
                        ? spec.Maximums[i]
                        : int.MaxValue;
                    increaseButtons[i].interactable =
                        values[i] < maximum &&
                        (spec.ExactTotal < 0 || total < spec.ExactTotal);
                }

                if (confirmButton != null)
                {
                    confirmButton.interactable = spec.ExactTotal < 0 || total == spec.ExactTotal;
                }

                if (summaryText != null && spec.FormatSummary != null)
                {
                    summaryText.text = spec.FormatSummary(new List<int>(values).AsReadOnly());
                }
            };

            for (var i = 0; i < count; i++)
            {
                var rowIndex = i;
                var rowY = spec.RowStartY - i * spec.RowSpacing;
                var row = shellView.CreateResourceRow();
                row.gameObject.name = "Resource Allocation Row " + i;
                var labelValue = spec.FormatRowLabel == null
                    ? spec.Labels[i]
                    : spec.FormatRowLabel(
                        i,
                        spec.Labels[i],
                        spec.UnitPrices != null && i < spec.UnitPrices.Count ? spec.UnitPrices[i] : 0);
                row.Label.gameObject.name = spec.LabelNamePrefix + i;
                row.Label.text = labelValue;
                SetRect(
                    row.Label.rectTransform,
                    shellView.LayoutProfile.ResourceRowAnchor,
                    shellView.LayoutProfile.ResourceRowAnchor,
                    new Vector2(spec.LabelWidth, spec.LabelHeight),
                    new Vector2(spec.LabelX, rowY));

                decreaseButtons[i] = row.DecreaseButton;
                decreaseButtons[i].gameObject.name = spec.DecreaseNamePrefix + i;
                decreaseButtons[i].onClick.RemoveAllListeners();
                SetRect(
                    decreaseButtons[i].GetComponent<RectTransform>(),
                    shellView.LayoutProfile.ResourceRowAnchor,
                    shellView.LayoutProfile.ResourceRowAnchor,
                    shellView.LayoutProfile.ResourceDecreaseButtonSize,
                    new Vector2(spec.DecreaseX, rowY));
                decreaseButtons[i].onClick.AddListener(() =>
                {
                    if (values[rowIndex] > 0)
                    {
                        values[rowIndex]--;
                        refresh();
                    }
                });

                valueTexts[i] = row.ValueText;
                valueTexts[i].gameObject.name = spec.ValueNamePrefix + i;
                valueTexts[i].text = "0";
                SetRect(
                    valueTexts[i].rectTransform,
                    shellView.LayoutProfile.ResourceRowAnchor,
                    shellView.LayoutProfile.ResourceRowAnchor,
                    shellView.LayoutProfile.ResourceValueSize,
                    new Vector2(spec.ValueX, rowY));

                increaseButtons[i] = row.IncreaseButton;
                increaseButtons[i].gameObject.name = spec.IncreaseNamePrefix + i;
                increaseButtons[i].onClick.RemoveAllListeners();
                SetRect(
                    increaseButtons[i].GetComponent<RectTransform>(),
                    shellView.LayoutProfile.ResourceRowAnchor,
                    shellView.LayoutProfile.ResourceRowAnchor,
                    shellView.LayoutProfile.ResourceIncreaseButtonSize,
                    new Vector2(spec.IncreaseX, rowY));
                increaseButtons[i].onClick.AddListener(() =>
                {
                    var maximum = spec.Maximums != null && rowIndex < spec.Maximums.Count
                        ? spec.Maximums[rowIndex]
                        : int.MaxValue;
                    var total = 0;
                    for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
                    {
                        total += values[valueIndex];
                    }

                    if (values[rowIndex] < maximum && (spec.ExactTotal < 0 || total < spec.ExactTotal))
                    {
                        values[rowIndex]++;
                        refresh();
                    }
                });
            }

            confirmButton = CreateButton(panel, spec.ConfirmName, spec.ConfirmLabel, 18);
            SetRect(
                confirmButton.GetComponent<RectTransform>(),
                shellView.LayoutProfile.BottomCenterAnchor,
                shellView.LayoutProfile.BottomCenterAnchor,
                UiTheme.DialogActionButtonSize,
                new Vector2(spec.Cancel == null ? 0f : -125f, 34f));
            var confirmInvoked = false;
            confirmButton.onClick.AddListener(() =>
            {
                if (confirmInvoked)
                {
                    return;
                }

                confirmInvoked = true;
                var result = new List<int>(values).AsReadOnly();
                if (spec.CloseBeforeConfirm)
                {
                    Hide();
                }

                spec.Confirm?.Invoke(result);
            });

            if (spec.Cancel != null)
            {
                var cancelButton = CreateButton(panel, spec.CancelName, spec.CancelLabel, 18);
                SetRect(
                    cancelButton.GetComponent<RectTransform>(),
                    shellView.LayoutProfile.BottomCenterAnchor,
                    shellView.LayoutProfile.BottomCenterAnchor,
                    UiTheme.DialogActionButtonSize,
                    shellView.LayoutProfile.ResourceCancelButtonPosition);
                var cancelInvoked = false;
                cancelButton.onClick.AddListener(() =>
                {
                    if (cancelInvoked)
                    {
                        return;
                    }

                    cancelInvoked = true;
                    if (spec.CloseBeforeCancel)
                    {
                        Hide();
                    }

                    spec.Cancel();
                });
            }

            refresh();
        }

        public static void AddHeading(
            RectTransform panel,
            string title,
            string description,
            float descriptionHeight = 70f,
            string titleName = "Title",
            string descriptionName = "Description",
            int titleSize = 27,
            bool addDragHandle = true)
        {
            if (panel == null)
            {
                return;
            }

            ResolveView(panel).ConfigureHeading(
                title,
                description,
                descriptionHeight,
                titleName,
                descriptionName,
                titleSize,
                addDragHandle);
        }

        public static Text CreateText(Transform parent, string name, string value, int fontSize, TextAnchor alignment)
        {
            var shellView = ResolveView(parent);
            var text = UnityEngine.Object.Instantiate(shellView.DescriptionText, parent, false);
            text.gameObject.name = name ?? string.Empty;
            text.gameObject.SetActive(true);
            text.text = value ?? string.Empty;
            text.fontSize = fontSize;
            text.color = UiTheme.ValueText;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        public static Button CreateButton(Transform parent, string name, string label, int fontSize)
        {
            var shellView = ResolveView(parent);
            var action = shellView.AcquireActionButton(parent as RectTransform);
            action.gameObject.name = name ?? string.Empty;
            action.Background.color = UiTheme.ButtonBackground;
            action.Label.text = label ?? string.Empty;
            action.Label.fontSize = fontSize;
            action.Label.color = UiTheme.GoldText;
            action.Label.fontStyle = FontStyle.Bold;
            action.Label.raycastTarget = false;
            action.Button.onClick.RemoveAllListeners();
            return action.Button;
        }

        internal static FacilityEffectCardView CreateFacilityCard(RectTransform parent)
        {
            var shellView = ResolveView(parent);
            var card = shellView.CreateFacilityCard();
            card.transform.SetParent(parent == null ? shellView.ExpandedContent : parent, false);
            return card;
        }

        private static EffectDialogShellView ResolveView(Transform source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var shellView = source.GetComponentInParent<EffectDialogShellView>();
            if (shellView == null)
            {
                throw new InvalidOperationException("Effect dialog content must belong to an EffectDialogShellView prefab instance.");
            }

            return shellView;
        }

        public static void Stretch(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        public static void SetRect(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 size,
            Vector2 position)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }
    }
}
