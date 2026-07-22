using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>效果弹窗共用的标题栏拖动手柄。</summary>
    internal sealed class EffectDialogDragHandle : MonoBehaviour,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        private RectTransform panel;
        private RectTransform parent;
        private Vector2 pointerOffset;
        private bool dragging;

        public void Configure(RectTransform configuredPanel)
        {
            panel = configuredPanel;
            parent = panel == null ? null : panel.parent as RectTransform;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            dragging = eventData != null &&
                       eventData.button == PointerEventData.InputButton.Left &&
                       panel != null &&
                       parent != null;
            if (!dragging)
            {
                return;
            }

            Vector2 localPoint;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    parent,
                    eventData.position,
                    eventData.pressEventCamera,
                    out localPoint))
            {
                dragging = false;
                return;
            }

            pointerOffset = panel.anchoredPosition - localPoint;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!dragging || eventData == null || panel == null || parent == null)
            {
                return;
            }

            Vector2 localPoint;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    parent,
                    eventData.position,
                    eventData.pressEventCamera,
                    out localPoint))
            {
                return;
            }

            panel.anchoredPosition = ClampToParent(localPoint + pointerOffset);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            dragging = false;
        }

        private void OnDisable()
        {
            dragging = false;
        }

        private Vector2 ClampToParent(Vector2 position)
        {
            var horizontalLimit = Mathf.Max(0f, (parent.rect.width - panel.rect.width) * 0.5f);
            var verticalLimit = Mathf.Max(0f, (parent.rect.height - panel.rect.height) * 0.5f);
            return new Vector2(
                Mathf.Clamp(position.x, -horizontalLimit, horizontalLimit),
                Mathf.Clamp(position.y, -verticalLimit, verticalLimit));
        }
    }

    internal struct EffectDialogToggleLayout
    {
        public EffectDialogToggleLayout(
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 size,
            Vector2 position)
        {
            AnchorMin = anchorMin;
            AnchorMax = anchorMax;
            Size = size;
            Position = position;
        }

        public Vector2 AnchorMin;
        public Vector2 AnchorMax;
        public Vector2 Size;
        public Vector2 Position;

        public void Apply(RectTransform target)
        {
            if (target == null)
            {
                return;
            }

            target.anchorMin = AnchorMin;
            target.anchorMax = AnchorMax;
            target.pivot = new Vector2(0.5f, 0.5f);
            target.sizeDelta = Size;
            target.anchoredPosition = Position;
        }
    }

    internal sealed class EffectDialogCollapseSpec
    {
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
        public Color CollapsedOverlayColor = Color.clear;
        public bool CollapsedOverlayRaycastTarget;
        public EffectDialogToggleLayout CollapsedToggleLayout = new EffectDialogToggleLayout(
            new Vector2(1f, 0.5f),
            new Vector2(1f, 0.5f),
            new Vector2(126f, 34f),
            new Vector2(-76f, 0f));
        public EffectDialogToggleLayout ExpandedToggleLayout = new EffectDialogToggleLayout(
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 0f),
            new Vector2(220f, 32f),
            new Vector2(0f, 22f));
    }

    /// <summary>Shared movement and collapse state for effect dialogs that must leave the map visible.</summary>
    internal sealed class EffectDialogCollapsiblePanel : MonoBehaviour,
        IBeginDragHandler,
        IDragHandler
    {
        private RectTransform panel;
        private Canvas canvas;
        private Image overlayImage;
        private GameObject expandedContent;
        private Text collapsedSummaryText;
        private RectTransform toggleRect;
        private Text toggleText;
        private Image toggleIcon;
        private Vector2 expandedSize;
        private float collapsedHeight;
        private string collapseLabel;
        private string expandLabel;
        private Color expandedOverlayColor;
        private Color collapsedOverlayColor;
        private bool expandedOverlayRaycastTarget;
        private bool collapsedOverlayRaycastTarget;
        private EffectDialogToggleLayout collapsedToggleLayout;
        private EffectDialogToggleLayout expandedToggleLayout;
        private bool configured;
        private bool collapsed;

        public bool IsCollapsed
        {
            get { return configured && collapsed; }
        }

        public void Configure(EffectDialogCollapseSpec spec)
        {
            if (spec == null)
            {
                configured = false;
                return;
            }

            panel = spec.Panel == null ? transform as RectTransform : spec.Panel;
            canvas = spec.Canvas == null && panel != null
                ? panel.GetComponentInParent<Canvas>()
                : spec.Canvas;
            overlayImage = spec.OverlayImage;
            expandedContent = spec.ExpandedContent;
            collapsedSummaryText = spec.CollapsedSummaryText;
            toggleRect = spec.ToggleRect;
            toggleText = spec.ToggleText;
            toggleIcon = spec.ToggleIcon;
            expandedSize = spec.ExpandedSize;
            collapsedHeight = spec.CollapsedHeight;
            collapseLabel = spec.CollapseLabel ?? string.Empty;
            expandLabel = spec.ExpandLabel ?? string.Empty;
            collapsedOverlayColor = spec.CollapsedOverlayColor;
            collapsedOverlayRaycastTarget = spec.CollapsedOverlayRaycastTarget;
            collapsedToggleLayout = spec.CollapsedToggleLayout;
            expandedToggleLayout = spec.ExpandedToggleLayout;
            expandedOverlayColor = overlayImage == null ? Color.clear : overlayImage.color;
            expandedOverlayRaycastTarget = overlayImage != null && overlayImage.raycastTarget;
            collapsed = spec.StartCollapsed;
            configured = panel != null;
            ApplyCollapseState();
        }

        public void Toggle()
        {
            SetCollapsed(!collapsed);
        }

        public void SetCollapsed(bool value)
        {
            if (!configured || collapsed == value)
            {
                return;
            }

            collapsed = value;
            ApplyCollapseState();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!configured || panel == null || eventData == null)
            {
                return;
            }

            var scaleFactor = canvas == null || canvas.scaleFactor <= 0f ? 1f : canvas.scaleFactor;
            panel.anchoredPosition += eventData.delta / scaleFactor;
        }

        private void ApplyCollapseState()
        {
            if (!configured || panel == null)
            {
                return;
            }

            var previousHeight = panel.sizeDelta.y;
            var targetSize = collapsed
                ? new Vector2(expandedSize.x, collapsedHeight)
                : expandedSize;
            panel.sizeDelta = targetSize;
            panel.anchoredPosition += new Vector2(0f, (previousHeight - targetSize.y) * 0.5f);

            if (expandedContent != null)
            {
                expandedContent.SetActive(!collapsed);
            }

            if (collapsedSummaryText != null)
            {
                collapsedSummaryText.gameObject.SetActive(collapsed);
            }

            if (toggleText != null)
            {
                toggleText.text = collapsed ? expandLabel : collapseLabel;
            }

            UguiUtility.SetTriangleIconDirection(toggleIcon, !collapsed);
            if (collapsed)
            {
                collapsedToggleLayout.Apply(toggleRect);
            }
            else
            {
                expandedToggleLayout.Apply(toggleRect);
            }

            if (overlayImage != null)
            {
                overlayImage.color = collapsed ? collapsedOverlayColor : expandedOverlayColor;
                overlayImage.raycastTarget = collapsed
                    ? collapsedOverlayRaycastTarget
                    : expandedOverlayRaycastTarget;
            }
        }
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
        private GameObject overlay;

        public bool IsShowing
        {
            get { return overlay != null; }
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

            overlay = new GameObject(overlayName, typeof(RectTransform), typeof(Image));
            overlay.transform.SetParent(canvas, false);
            var overlayRect = overlay.GetComponent<RectTransform>();
            Stretch(overlayRect, 0f);
            var overlayImage = overlay.GetComponent<Image>();
            overlayImage.color = new Color(0f, 0f, 0f, 0.22f);
            overlayImage.raycastTarget = blockBackgroundInput;

            var panelObject = new GameObject(panelName, typeof(RectTransform), typeof(Image), typeof(Outline));
            panelObject.transform.SetParent(overlayRect, false);
            var panel = panelObject.GetComponent<RectTransform>();
            SetRect(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), size, position);
            panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
            var outline = panelObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(2f, -2f);
            return panel;
        }

        public void Hide()
        {
            if (overlay == null)
            {
                return;
            }

            overlay.SetActive(false);
            if (UnityEngine.Application.isPlaying)
            {
                UnityEngine.Object.Destroy(overlay);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(overlay);
            }

            overlay = null;
        }

        public static RectTransform AddOptionScroll(
            RectTransform panel,
            string name,
            float bottom,
            float top)
        {
            var scrollObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollObject.transform.SetParent(panel, false);
            var scrollRectTransform = scrollObject.GetComponent<RectTransform>();
            scrollRectTransform.anchorMin = new Vector2(0.06f, 0f);
            scrollRectTransform.anchorMax = new Vector2(0.94f, 1f);
            scrollRectTransform.offsetMin = new Vector2(0f, bottom);
            scrollRectTransform.offsetMax = new Vector2(0f, -top);
            scrollObject.GetComponent<Image>().color = UiTheme.ScrollBackground;

            var viewportObject = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            viewportObject.transform.SetParent(scrollRectTransform, false);
            var viewport = viewportObject.GetComponent<RectTransform>();
            Stretch(viewport, 0f);
            viewportObject.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.01f);
            viewportObject.GetComponent<Mask>().showMaskGraphic = false;

            var contentObject = new GameObject(
                "Content",
                typeof(RectTransform),
                typeof(VerticalLayoutGroup),
                typeof(ContentSizeFitter));
            contentObject.transform.SetParent(viewport, false);
            var content = contentObject.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = Vector2.zero;
            var layout = contentObject.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 12, 12);
            layout.spacing = 9f;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            contentObject.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = scrollObject.GetComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28f;
            return content;
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
                var button = CreateButton(content, buttonNamePrefix + i, option.Label, 18);
                button.gameObject.AddComponent<LayoutElement>().preferredHeight = 54f;
                button.interactable = option.Enabled;
                button.GetComponent<Image>().color = option.Enabled
                    ? UiTheme.ButtonBackground
                    : UiTheme.DisabledButtonBackground;
                if (!option.Enabled)
                {
                    continue;
                }

                var select = option.Select;
                button.onClick.AddListener(() =>
                {
                    beforeSelect?.Invoke();
                    select?.Invoke();
                });
            }
        }

        public void AddResourceAllocation(RectTransform panel, ResourceAllocationSpec spec)
        {
            AddHeading(panel, spec.Title, spec.Description);
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
            var summaryText = string.IsNullOrEmpty(spec.SummaryName)
                ? null
                : CreateText(panel, spec.SummaryName, string.Empty, 18, TextAnchor.MiddleCenter);
            if (summaryText != null)
            {
                summaryText.color = UiTheme.GoldText;
                summaryText.fontStyle = FontStyle.Bold;
                SetRect(
                    summaryText.rectTransform,
                    new Vector2(0.08f, 0f),
                    new Vector2(0.92f, 0f),
                    new Vector2(0f, 42f),
                    new Vector2(0f, 94f));
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
                var labelValue = spec.FormatRowLabel == null
                    ? spec.Labels[i]
                    : spec.FormatRowLabel(
                        i,
                        spec.Labels[i],
                        spec.UnitPrices != null && i < spec.UnitPrices.Count ? spec.UnitPrices[i] : 0);
                var label = CreateText(
                    panel,
                    spec.LabelNamePrefix + i,
                    labelValue,
                    18,
                    TextAnchor.MiddleLeft);
                SetRect(
                    label.rectTransform,
                    new Vector2(0f, 1f),
                    new Vector2(0f, 1f),
                    new Vector2(spec.LabelWidth, spec.LabelHeight),
                    new Vector2(spec.LabelX, rowY));

                decreaseButtons[i] = CreateButton(panel, spec.DecreaseNamePrefix + i, "−", 24);
                SetRect(decreaseButtons[i].GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(58f, 44f), new Vector2(spec.DecreaseX, rowY));
                decreaseButtons[i].onClick.AddListener(() =>
                {
                    if (values[rowIndex] > 0)
                    {
                        values[rowIndex]--;
                        refresh();
                    }
                });

                valueTexts[i] = CreateText(panel, spec.ValueNamePrefix + i, "0", 22, TextAnchor.MiddleCenter);
                SetRect(valueTexts[i].rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(74f, 44f), new Vector2(spec.ValueX, rowY));

                increaseButtons[i] = CreateButton(panel, spec.IncreaseNamePrefix + i, "+", 24);
                SetRect(increaseButtons[i].GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(58f, 44f), new Vector2(spec.IncreaseX, rowY));
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
                new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(220f, 48f),
                new Vector2(spec.Cancel == null ? 0f : -125f, 34f));
            confirmButton.onClick.AddListener(() =>
            {
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
                    new Vector2(0.5f, 0f),
                    new Vector2(0.5f, 0f),
                    new Vector2(220f, 48f),
                    new Vector2(125f, 34f));
                cancelButton.onClick.AddListener(() =>
                {
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

            var titleText = CreateText(panel, titleName, title, titleSize, TextAnchor.MiddleCenter);
            titleText.fontStyle = FontStyle.Bold;
            titleText.color = UiTheme.GoldText;
            if (addDragHandle)
            {
                titleText.gameObject.AddComponent<EffectDialogDragHandle>().Configure(panel);
            }
            SetRect(titleText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(-40f, 52f), new Vector2(0f, -34f));

            var descriptionText = CreateText(panel, descriptionName, description, 16, TextAnchor.UpperLeft);
            descriptionText.color = UiTheme.ValueText;
            SetRect(descriptionText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(-70f, descriptionHeight), new Vector2(0f, -92f));
        }

        public static Text CreateText(Transform parent, string name, string value, int fontSize, TextAnchor alignment)
        {
            var textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);
            var text = textObject.GetComponent<Text>();
            text.text = value ?? string.Empty;
            text.font = FontUtility.GetCjkFont(fontSize);
            text.fontSize = fontSize;
            text.color = UiTheme.ValueText;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        public static Button CreateButton(Transform parent, string name, string label, int fontSize)
        {
            var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);
            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);
            var labelText = CreateText(buttonObject.GetComponent<RectTransform>(), "Label", label, fontSize, TextAnchor.MiddleCenter);
            labelText.color = UiTheme.GoldText;
            labelText.fontStyle = FontStyle.Bold;
            labelText.raycastTarget = false;
            Stretch(labelText.rectTransform, 8f);
            return buttonObject.GetComponent<Button>();
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
