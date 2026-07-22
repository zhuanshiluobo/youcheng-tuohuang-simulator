using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    internal sealed class FacilityEffectCardOption
    {
        public FacilityEffectCardOption(string facilityId, string label, bool available)
        {
            FacilityId = facilityId ?? string.Empty;
            Label = label ?? string.Empty;
            Available = available;
        }

        public string FacilityId { get; private set; }

        public string Label { get; private set; }

        public bool Available { get; private set; }
    }

    /// <summary>设施入场待选专用弹窗；不读取或修改游戏规则状态。</summary>
    internal sealed class FacilityEffectChoiceDialog
    {
        private readonly EffectDialogShell shell = new EffectDialogShell();
        private EffectDialogCollapsiblePanel collapsiblePanel;
        private RectTransform facilityCardDragGhost;
        private ZoomableImageViewerController facilityCardImageViewer;

        public bool IsShowing
        {
            get { return shell.IsShowing; }
        }

        public void ShowOptions(
            RectTransform canvas,
            string title,
            string description,
            IReadOnlyList<EffectDialogOption> options,
            Action back = null)
        {
            ShowOptionsCore(canvas, title, description, options, back, string.Empty, false);
        }

        public void ShowCollapsibleOptions(
            RectTransform canvas,
            string title,
            string description,
            string summary,
            IReadOnlyList<EffectDialogOption> options,
            Action back = null,
            string backLabel = null)
        {
            ShowOptionsCore(canvas, title, description, options, back, summary, false, backLabel);
        }

        public void ShowExtensionHubOptions(
            RectTransform canvas,
            IReadOnlyList<FacilityEffectCardOption> options,
            Action beginDrag,
            Action<string, int> drop,
            Action cancelDrag,
            Action skip)
        {
            if (canvas == null)
            {
                return;
            }

            var panel = Rebuild(
                canvas,
                new Vector2(520f, 360f),
                Vector2.zero,
                "\u5ef6\u4f38\u67a2\u7ebd\uff1a\u62d6\u52a8\u5361\u7247\u5230\u57ce\u5e02\u9762\u677f\u7a7a\u69fd\u4f4d\u5efa\u8bbe\u3002",
                false);
            AddHeading(
                panel,
                "延伸枢纽",
                "选择一个尚未使用的延伸枢纽，拖动到城市面板空槽位进行建设。",
                58f);

            const float cardWidth = FacilityCardDragUtility.CardWidth;
            const float cardHeight = FacilityCardDragUtility.CardHeight;
            const float cardGap = 28f;
            var optionCount = options == null ? 0 : options.Count;
            var rowWidth = optionCount * cardWidth + Mathf.Max(0, optionCount - 1) * cardGap;
            var rowStartX = -rowWidth * 0.5f + cardWidth * 0.5f;
            for (var i = 0; i < optionCount; i++)
            {
                var option = options[i];
                var texture = BuildInfoPanel.TryLoadFacilityCardTexture(option.FacilityId);
                var cardObject = new GameObject(
                    "Extension Hub Card " + option.FacilityId,
                    typeof(RectTransform),
                    typeof(Image),
                    typeof(Button),
                    typeof(Outline),
                    typeof(CanvasGroup));
                cardObject.transform.SetParent(panel, false);
                var cardRect = cardObject.GetComponent<RectTransform>();
                SetRect(
                    cardRect,
                    new Vector2(0.5f, 1f),
                    new Vector2(0.5f, 1f),
                    new Vector2(cardWidth, cardHeight),
                    new Vector2(rowStartX + i * (cardWidth + cardGap), -205f));

                cardObject.GetComponent<Image>().color = UiTheme.ScrollBackground;
                var outline = cardObject.GetComponent<Outline>();
                outline.effectColor = option.Available ? UiTheme.GoldOutline : UiTheme.GoldOutlineThin;
                outline.effectDistance = new Vector2(2f, -2f);
                var button = cardObject.GetComponent<Button>();
                button.interactable = true;
                var canvasGroup = cardObject.GetComponent<CanvasGroup>();
                canvasGroup.alpha = option.Available ? 1f : 0.32f;
                canvasGroup.interactable = true;
                canvasGroup.blocksRaycasts = true;

                var imageObject = new GameObject("Card Image", typeof(RectTransform), typeof(RawImage));
                imageObject.transform.SetParent(cardRect, false);
                var imageRect = imageObject.GetComponent<RectTransform>();
                Stretch(imageRect, FacilityCardDragUtility.CardImageInset);
                var rawImage = imageObject.GetComponent<RawImage>();
                rawImage.texture = texture;
                rawImage.color = texture == null ? Color.clear : Color.white;
                rawImage.raycastTarget = false;

                var label = CreateText(cardRect, "Shared Name", option.Label, 18, TextAnchor.MiddleCenter);
                label.fontStyle = FontStyle.Bold;
                label.color = UiTheme.GoldText;
                label.gameObject.SetActive(texture == null);
                SetRect(
                    label.rectTransform,
                    new Vector2(0f, 0f),
                    new Vector2(1f, 1f),
                    Vector2.zero,
                    Vector2.zero);

                var facilityId = option.FacilityId;
                var cardName = option.Label;
                var available = option.Available;
                var interaction = cardObject.AddComponent<CardPointerInteraction>();
                interaction.ConfigureClick(
                    button,
                    () => CardImagePreviewUtility.Open(
                        ref facilityCardImageViewer,
                        canvas,
                        "Extension Hub Card Image Viewer",
                        "Extension Hub Card",
                        cardName,
                        texture),
                    null);
                interaction.ConfigureDrag(
                    () => available,
                    eventData =>
                    {
                        BeginFacilityCardDrag(canvas, cardRect, texture, cardName, eventData);
                        beginDrag?.Invoke();
                    },
                    MoveFacilityCardDrag,
                    eventData =>
                    {
                        var slotIndex = FacilityCardDragUtility.ResolveCityBoardSlotIndex(eventData);
                        DestroyFacilityCardDragGhost();
                        drop?.Invoke(facilityId, slotIndex);
                    },
                    () =>
                    {
                        DestroyFacilityCardDragGhost();
                        cancelDrag?.Invoke();
                    });
            }

            var skipButton = CreateButton(panel, "Skip Extension Hub", "不建设", 18);
            SetRect(
                skipButton.GetComponent<RectTransform>(),
                new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(220f, 48f),
                new Vector2(0f, 34f));
            skipButton.onClick.AddListener(() => skip?.Invoke());
        }

        private void ShowOptionsCore(
            RectTransform canvas,
            string title,
            string description,
            IReadOnlyList<EffectDialogOption> options,
            Action back,
            string summary,
            bool startCollapsed,
            string backLabel = null)
        {
            var isCollapsible = !string.IsNullOrEmpty(summary);
            var panel = Rebuild(canvas, new Vector2(660f, 600f), Vector2.zero, summary, startCollapsed);
            AddHeading(panel, title, description);

            var scrollBottom = back == null ? 34f : 82f;
            if (isCollapsible)
            {
                scrollBottom += back == null ? 20f : 54f;
            }

            var content = EffectDialogShell.AddOptionScroll(panel, "Options Scroll", scrollBottom, 142f);
            EffectDialogShell.AddOptions(content, options, "Option ", null);

            if (back != null)
            {
                var backButton = CreateButton(
                    panel,
                    "Back",
                    string.IsNullOrEmpty(backLabel) ? "\u8fd4\u56de" : backLabel,
                    17);
                SetRect(backButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(180f, 44f), new Vector2(0f, isCollapsible ? 76f : 28f));
                backButton.onClick.AddListener(() => back());
            }
        }

        public void ShowResourceAllocation(
            RectTransform canvas,
            string title,
            string description,
            IReadOnlyList<string> labels,
            IReadOnlyList<int> maximums,
            int exactTotal,
            Action<IReadOnlyList<int>> confirm,
            Action skip)
        {
            var panel = Rebuild(canvas, new Vector2(650f, 560f), Vector2.zero);
            shell.AddResourceAllocation(panel, new ResourceAllocationSpec
            {
                Title = title,
                Description = description,
                Labels = labels,
                Maximums = maximums,
                ExactTotal = exactTotal,
                LabelWidth = 230f,
                LabelHeight = 48f,
                LabelX = 145f,
                DecreaseX = 320f,
                ValueX = 390f,
                IncreaseX = 460f,
                Confirm = confirm,
                Cancel = skip
            });
        }

        public void ShowMapPrompt(
            RectTransform canvas,
            string title,
            string description,
            string primaryLabel,
            Action primary,
            Action back = null)
        {
            ShowMapPromptCore(canvas, title, description, primaryLabel, primary, back, string.Empty, false);
        }

        public void ShowCompactMapPrompt(RectTransform canvas, string title, string description)
        {
            var panel = Rebuild(canvas, new Vector2(520f, 140f), new Vector2(0f, 310f));
            AddHeading(panel, title, description, 40f);
        }

        public void ShowCollapsibleMapPrompt(
            RectTransform canvas,
            string title,
            string description,
            string summary,
            string primaryLabel,
            Action primary,
            Action back = null,
            bool startCollapsed = true)
        {
            ShowMapPromptCore(canvas, title, description, primaryLabel, primary, back, summary, startCollapsed);
        }

        private void ShowMapPromptCore(
            RectTransform canvas,
            string title,
            string description,
            string primaryLabel,
            Action primary,
            Action back,
            string summary,
            bool startCollapsed)
        {
            var isCollapsible = !string.IsNullOrEmpty(summary);
            var panelHeight = isCollapsible ? 260f : 210f;
            var panel = Rebuild(canvas, new Vector2(650f, panelHeight), new Vector2(0f, 310f), summary, startCollapsed);
            AddHeading(panel, title, description, 74f);
            if (primary != null)
            {
                var primaryButton = CreateButton(panel, "Primary", primaryLabel, 17);
                SetRect(primaryButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(220f, 42f), new Vector2(back == null ? 0f : -120f, isCollapsible ? 68f : 26f));
                primaryButton.onClick.AddListener(() => primary());
            }

            if (back != null)
            {
                var backButton = CreateButton(panel, "Back", "返回", 17);
                SetRect(backButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(180f, 42f), new Vector2(primary == null ? 0f : 130f, isCollapsible ? 68f : 26f));
                backButton.onClick.AddListener(() => back());
            }
        }

        public void Hide()
        {
            DestroyFacilityCardDragGhost();
            facilityCardImageViewer?.Close();
            shell.Hide();
            collapsiblePanel = null;
        }

        private RectTransform Rebuild(
            RectTransform canvas,
            Vector2 size,
            Vector2 position,
            string collapseSummary = null,
            bool startCollapsed = false)
        {
            facilityCardImageViewer?.Close();
            collapsiblePanel = null;
            var panel = shell.Rebuild(
                canvas,
                "Facility Effect Choice Overlay",
                "Facility Effect Choice Panel",
                size,
                position);
            if (panel == null || string.IsNullOrEmpty(collapseSummary))
            {
                return panel;
            }

            return ConfigureCollapsiblePanel(panel, canvas, size, collapseSummary, startCollapsed);
        }

        private RectTransform ConfigureCollapsiblePanel(
            RectTransform panel,
            RectTransform canvas,
            Vector2 expandedSize,
            string summary,
            bool startCollapsed)
        {
            var collapsedSummaryText = CreateText(panel, "Facility Collapsed Summary", summary, 18, TextAnchor.MiddleLeft);
            collapsedSummaryText.fontStyle = FontStyle.Bold;
            collapsedSummaryText.color = UiTheme.GoldText;
            SetRect(
                collapsedSummaryText.rectTransform,
                new Vector2(0.06f, 1f),
                new Vector2(0.72f, 1f),
                new Vector2(0f, 42f),
                new Vector2(0f, -29f));

            var expandedContent = new GameObject("Facility Expanded Content", typeof(RectTransform));
            expandedContent.transform.SetParent(panel, false);
            var contentRect = expandedContent.GetComponent<RectTransform>();
            Stretch(contentRect, 0f);

            var toggleButton = CreateButton(panel, "Facility Collapse Toggle", "收起卡片", 14);
            var collapseToggleRect = toggleButton.GetComponent<RectTransform>();
            var collapseToggleText = toggleButton.GetComponentInChildren<Text>();
            var collapseToggleIcon = UguiUtility.CreateTriangleIcon(
                collapseToggleRect,
                "Facility Collapse Triangle",
                true);
            collapsiblePanel = panel.gameObject.AddComponent<EffectDialogCollapsiblePanel>();
            collapsiblePanel.Configure(new EffectDialogCollapseSpec
            {
                Panel = panel,
                Canvas = canvas == null ? null : canvas.GetComponentInParent<Canvas>(),
                OverlayImage = panel.parent == null ? null : panel.parent.GetComponent<Image>(),
                ExpandedContent = expandedContent,
                CollapsedSummaryText = collapsedSummaryText,
                ToggleRect = collapseToggleRect,
                ToggleText = collapseToggleText,
                ToggleIcon = collapseToggleIcon,
                ExpandedSize = expandedSize,
                StartCollapsed = startCollapsed
            });
            toggleButton.onClick.AddListener(collapsiblePanel.Toggle);
            return contentRect;
        }

        private void BeginFacilityCardDrag(
            RectTransform canvas,
            RectTransform source,
            Texture2D texture,
            string fallbackLabel,
            PointerEventData eventData)
        {
            DestroyFacilityCardDragGhost();
            facilityCardDragGhost = FacilityCardDragUtility.CreateDragGhost(
                canvas,
                source,
                texture,
                fallbackLabel);
            MoveFacilityCardDrag(eventData);
        }

        private void MoveFacilityCardDrag(PointerEventData eventData)
        {
            FacilityCardDragUtility.MoveDragGhost(facilityCardDragGhost, eventData);
        }

        private void DestroyFacilityCardDragGhost()
        {
            FacilityCardDragUtility.DestroyDragGhost(ref facilityCardDragGhost);
        }

        private static void AddHeading(RectTransform panel, string title, string description, float descriptionHeight = 70f)
        {
            var addDragHandle = panel == null ||
                                panel.GetComponentInParent<EffectDialogCollapsiblePanel>() == null;
            EffectDialogShell.AddHeading(
                panel,
                title,
                description,
                descriptionHeight,
                addDragHandle: addDragHandle);
        }

        private static Text CreateText(RectTransform parent, string name, string value, int fontSize, TextAnchor alignment)
        {
            var text = EffectDialogShell.CreateText(parent, name, value, fontSize, alignment);
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static Button CreateButton(RectTransform parent, string name, string label, int fontSize)
        {
            return EffectDialogShell.CreateButton(parent, name, label, fontSize);
        }

        private static void Stretch(RectTransform rect, float inset)
        {
            EffectDialogShell.Stretch(rect, inset);
        }

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 size, Vector2 position)
        {
            EffectDialogShell.SetRect(rect, anchorMin, anchorMax, size, position);
        }

    }
}
