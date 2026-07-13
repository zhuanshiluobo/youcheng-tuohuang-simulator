using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    internal sealed class FacilityEffectDialogOption
    {
        public FacilityEffectDialogOption(string label, Action select)
        {
            Label = label ?? string.Empty;
            Select = select;
        }

        public string Label { get; private set; }

        public Action Select { get; private set; }
    }

    /// <summary>设施入场待选专用弹窗；不读取或修改游戏规则状态。</summary>
    internal sealed class FacilityEffectChoiceDialog
    {
        private GameObject overlay;

        public bool IsShowing
        {
            get { return overlay != null; }
        }

        public void ShowOptions(
            RectTransform canvas,
            string title,
            string description,
            IReadOnlyList<FacilityEffectDialogOption> options,
            Action back = null)
        {
            var panel = Rebuild(canvas, new Vector2(660f, 600f), Vector2.zero);
            AddHeading(panel, title, description);

            var scrollObject = new GameObject("Options Scroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollObject.transform.SetParent(panel, false);
            var scrollRectTransform = scrollObject.GetComponent<RectTransform>();
            scrollRectTransform.anchorMin = new Vector2(0.06f, 0f);
            scrollRectTransform.anchorMax = new Vector2(0.94f, 1f);
            scrollRectTransform.offsetMin = new Vector2(0f, back == null ? 34f : 82f);
            scrollRectTransform.offsetMax = new Vector2(0f, -142f);
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

            if (options != null)
            {
                for (var i = 0; i < options.Count; i++)
                {
                    var option = options[i];
                    var button = CreateButton(content, "Option " + i, option.Label, 18);
                    var element = button.gameObject.AddComponent<LayoutElement>();
                    element.preferredHeight = 54f;
                    var select = option.Select;
                    button.onClick.AddListener(() => select?.Invoke());
                }
            }

            if (back != null)
            {
                var backButton = CreateButton(panel, "Back", "返回", 17);
                SetRect(backButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(180f, 44f), new Vector2(0f, 28f));
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
            AddHeading(panel, title, description);
            var count = labels == null ? 0 : labels.Count;
            var values = new int[count];
            if (exactTotal >= 0 && count > 0)
            {
                values[0] = exactTotal;
            }

            var valueTexts = new Text[count];
            var decreaseButtons = new Button[count];
            var increaseButtons = new Button[count];
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
                    var max = maximums != null && i < maximums.Count ? maximums[i] : int.MaxValue;
                    increaseButtons[i].interactable = values[i] < max && (exactTotal < 0 || total < exactTotal);
                }

                if (confirmButton != null)
                {
                    confirmButton.interactable = exactTotal < 0 || total == exactTotal;
                }
            };

            for (var i = 0; i < count; i++)
            {
                var rowIndex = i;
                var rowY = -168f - i * 68f;
                var label = CreateText(panel, "Resource Label " + i, labels[i], 19, TextAnchor.MiddleLeft);
                SetRect(label.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(230f, 48f), new Vector2(145f, rowY));

                decreaseButtons[i] = CreateButton(panel, "Decrease " + i, "−", 24);
                SetRect(decreaseButtons[i].GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(58f, 44f), new Vector2(320f, rowY));
                decreaseButtons[i].onClick.AddListener(() =>
                {
                    if (values[rowIndex] > 0)
                    {
                        values[rowIndex]--;
                        refresh();
                    }
                });

                valueTexts[i] = CreateText(panel, "Value " + i, "0", 22, TextAnchor.MiddleCenter);
                SetRect(valueTexts[i].rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(74f, 44f), new Vector2(390f, rowY));

                increaseButtons[i] = CreateButton(panel, "Increase " + i, "+", 24);
                SetRect(increaseButtons[i].GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(58f, 44f), new Vector2(460f, rowY));
                increaseButtons[i].onClick.AddListener(() =>
                {
                    var max = maximums != null && rowIndex < maximums.Count ? maximums[rowIndex] : int.MaxValue;
                    var total = 0;
                    for (var valueIndex = 0; valueIndex < values.Length; valueIndex++) total += values[valueIndex];
                    if (values[rowIndex] < max && (exactTotal < 0 || total < exactTotal))
                    {
                        values[rowIndex]++;
                        refresh();
                    }
                });
            }

            confirmButton = CreateButton(panel, "Confirm", "确认结算", 18);
            SetRect(confirmButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(220f, 48f), new Vector2(skip == null ? 0f : -125f, 36f));
            confirmButton.onClick.AddListener(() => confirm?.Invoke(new List<int>(values)));

            if (skip != null)
            {
                var skipButton = CreateButton(panel, "Skip", "跳过", 18);
                SetRect(skipButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(220f, 48f), new Vector2(125f, 36f));
                skipButton.onClick.AddListener(() => skip());
            }

            refresh();
        }

        public void ShowMapPrompt(
            RectTransform canvas,
            string title,
            string description,
            string primaryLabel,
            Action primary,
            Action back = null)
        {
            var panel = Rebuild(canvas, new Vector2(650f, 210f), new Vector2(0f, 310f));
            AddHeading(panel, title, description, 74f);
            if (primary != null)
            {
                var primaryButton = CreateButton(panel, "Primary", primaryLabel, 17);
                SetRect(primaryButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(220f, 42f), new Vector2(back == null ? 0f : -120f, 26f));
                primaryButton.onClick.AddListener(() => primary());
            }

            if (back != null)
            {
                var backButton = CreateButton(panel, "Back", "返回", 17);
                SetRect(backButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(180f, 42f), new Vector2(primary == null ? 0f : 130f, 26f));
                backButton.onClick.AddListener(() => back());
            }
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

        private RectTransform Rebuild(RectTransform canvas, Vector2 size, Vector2 position)
        {
            Hide();
            if (canvas == null)
            {
                return null;
            }

            overlay = new GameObject("Facility Effect Choice Overlay", typeof(RectTransform), typeof(Image));
            overlay.transform.SetParent(canvas, false);
            var overlayRect = overlay.GetComponent<RectTransform>();
            Stretch(overlayRect, 0f);
            var overlayImage = overlay.GetComponent<Image>();
            overlayImage.color = new Color(0f, 0f, 0f, 0.22f);
            overlayImage.raycastTarget = false;

            var panelObject = new GameObject("Facility Effect Choice Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panelObject.transform.SetParent(overlayRect, false);
            var panel = panelObject.GetComponent<RectTransform>();
            SetRect(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), size, position);
            panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
            var outline = panelObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(2f, -2f);
            return panel;
        }

        private static void AddHeading(RectTransform panel, string title, string description, float descriptionHeight = 70f)
        {
            if (panel == null)
            {
                return;
            }

            var titleText = CreateText(panel, "Title", title, 27, TextAnchor.MiddleCenter);
            titleText.fontStyle = FontStyle.Bold;
            titleText.color = UiTheme.GoldText;
            SetRect(titleText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(-40f, 52f), new Vector2(0f, -34f));

            var descriptionText = CreateText(panel, "Description", description, 16, TextAnchor.UpperLeft);
            descriptionText.color = UiTheme.ValueText;
            SetRect(descriptionText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(-70f, descriptionHeight), new Vector2(0f, -92f));
        }

        private static Text CreateText(RectTransform parent, string name, string value, int fontSize, TextAnchor alignment)
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
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static Button CreateButton(RectTransform parent, string name, string label, int fontSize)
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
            Stretch(labelText.rectTransform, 8f);
            return buttonObject.GetComponent<Button>();
        }

        private static void Stretch(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 size, Vector2 position)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }
    }
}
