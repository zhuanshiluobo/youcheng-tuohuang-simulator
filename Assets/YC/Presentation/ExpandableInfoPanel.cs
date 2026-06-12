using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ExpandableInfoPanel : MonoBehaviour
    {
        private const float ExpandedWidth = 504f;
        private const float CollapsedWidth = 65f;
        private const float SectionTitleHeight = 25f;
        private const float RowHeight = 18f;
        private const string ExpandedArrow = "◀";
        private const string CollapsedArrow = "▶";

        private readonly List<InfoModule> modules = new List<InfoModule>();

        private RectTransform panelTransform;
        private RectTransform contentArea;
        private RectTransform scrollContent;
        private Button toggleButton;
        private Text toggleButtonText;
        private bool isExpanded;

        private bool initialized;

        public bool IsExpanded => isExpanded;
        public IReadOnlyList<InfoModule> Modules => modules;

        private void Awake()
        {
            Initialize(transform);
        }

        public void Initialize(Transform parent)
        {
            if (initialized) return;
            initialized = true;

            var canvas = UguiUtility.CreateCanvas("Info Panel Canvas", 100);
            BuildPanel(canvas.transform);
            BuildDemoModules();
            SetExpanded(false);
            RebuildLayout();
        }

        public void SetRowValue(string moduleTitle, string label, string value)
        {
            for (var i = 0; i < modules.Count; i++)
            {
                if (modules[i].Title == moduleTitle)
                {
                    modules[i].SetRowValue(label, value);
                    RebuildLayout();
                    return;
                }
            }
        }

        public InfoModule AddModule(string title)
        {
            var module = new InfoModule(title);
            module.BuildUI(scrollContent);
            modules.Add(module);
            return module;
        }

        public void Toggle()
        {
            SetExpanded(!isExpanded);
        }

        private void SetExpanded(bool expand)
        {
            isExpanded = expand;
            var targetWidth = expand ? ExpandedWidth : CollapsedWidth;
            panelTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
            contentArea.gameObject.SetActive(expand);
            toggleButtonText.text = expand ? ExpandedArrow : CollapsedArrow;
            RebuildLayout();
        }

        private void BuildPanel(Transform parent)
        {
            var panelObject = new GameObject("Sidebar Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panelObject.transform.SetParent(parent, false);

            panelTransform = panelObject.GetComponent<RectTransform>();
            panelTransform.anchorMin = new Vector2(0f, 0f);
            panelTransform.anchorMax = new Vector2(0f, 1f);
            panelTransform.pivot = new Vector2(0f, 0.5f);
            panelTransform.sizeDelta = new Vector2(CollapsedWidth, 0f);
            panelTransform.anchoredPosition = Vector2.zero;

            var panelImage = panelObject.GetComponent<Image>();
            panelImage.color = UiTheme.PanelBackground;

            var panelOutline = panelObject.GetComponent<Outline>();
            panelOutline.effectColor = UiTheme.GoldOutline;
            panelOutline.effectDistance = new Vector2(2f, 0f);

            BuildToggleButton(panelTransform);
            BuildContentArea(panelTransform);
        }

        private void BuildToggleButton(RectTransform parent)
        {
            var buttonObject = new GameObject("Toggle Button", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);

            var buttonTransform = buttonObject.GetComponent<RectTransform>();
            buttonTransform.anchorMin = new Vector2(1f, 0.5f);
            buttonTransform.anchorMax = new Vector2(1f, 0.5f);
            buttonTransform.pivot = new Vector2(1f, 0.5f);
            buttonTransform.sizeDelta = new Vector2(47f, 90f);
            buttonTransform.anchoredPosition = Vector2.zero;

            var buttonImage = buttonObject.GetComponent<Image>();
            buttonImage.color = UiTheme.PanelBackgroundLighter;

            toggleButton = buttonObject.GetComponent<Button>();
            toggleButton.onClick.AddListener(Toggle);

            var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(buttonTransform, false);

            var textTransform = textObject.GetComponent<RectTransform>();
            textTransform.anchorMin = Vector2.zero;
            textTransform.anchorMax = Vector2.one;
            textTransform.offsetMin = Vector2.zero;
            textTransform.offsetMax = Vector2.zero;

            toggleButtonText = textObject.GetComponent<Text>();
            toggleButtonText.text = CollapsedArrow;
            toggleButtonText.alignment = TextAnchor.MiddleCenter;
            toggleButtonText.color = UiTheme.GoldText;
            toggleButtonText.fontSize = 22;
            toggleButtonText.fontStyle = FontStyle.Bold;
            toggleButtonText.font = FontUtility.GetLatinFont(22);
        }

        private void BuildContentArea(RectTransform parent)
        {
            contentArea = new GameObject("Content Area", typeof(RectTransform)).GetComponent<RectTransform>();
            contentArea.SetParent(parent, false);
            contentArea.anchorMin = new Vector2(0f, 0f);
            contentArea.anchorMax = new Vector2(1f, 1f);
            contentArea.pivot = new Vector2(0.5f, 0.5f);
            contentArea.offsetMin = new Vector2(11f, 11f);
            contentArea.offsetMax = new Vector2(-54f, -11f);
            contentArea.gameObject.SetActive(false);

            BuildHeader(contentArea);
            BuildScrollView(contentArea);
        }

        private void BuildHeader(RectTransform parent)
        {
            var headerObject = new GameObject("Header", typeof(RectTransform), typeof(Text), typeof(Outline));
            headerObject.transform.SetParent(parent, false);

            var headerTransform = headerObject.GetComponent<RectTransform>();
            headerTransform.anchorMin = new Vector2(0f, 1f);
            headerTransform.anchorMax = new Vector2(1f, 1f);
            headerTransform.pivot = new Vector2(0.5f, 1f);
            headerTransform.sizeDelta = new Vector2(0f, 48f);
            headerTransform.anchoredPosition = Vector2.zero;

            var headerText = headerObject.GetComponent<Text>();
            headerText.text = "信息面板";
            headerText.alignment = TextAnchor.MiddleCenter;
            headerText.color = UiTheme.GoldText;
            headerText.fontSize = 30;
            headerText.fontStyle = FontStyle.Bold;
            headerText.font = FontUtility.GetCjkFont(30);

            var headerOutline = headerObject.GetComponent<Outline>();
            headerOutline.effectColor = UiTheme.DarkShadowLight;
            headerOutline.effectDistance = new Vector2(1f, -1f);

            var separatorObject = new GameObject("Header Separator", typeof(RectTransform), typeof(Image));
            separatorObject.transform.SetParent(headerTransform, false);

            var separatorTransform = separatorObject.GetComponent<RectTransform>();
            separatorTransform.anchorMin = new Vector2(0f, 0f);
            separatorTransform.anchorMax = new Vector2(1f, 0f);
            separatorTransform.pivot = new Vector2(0.5f, 0f);
            separatorTransform.sizeDelta = new Vector2(0f, 2f);
            separatorTransform.anchoredPosition = Vector2.zero;

            separatorObject.GetComponent<Image>().color = UiTheme.GoldSeparator;
        }

        private void BuildScrollView(RectTransform parent)
        {
            var scrollObject = new GameObject("Scroll View", typeof(RectTransform), typeof(ScrollRect), typeof(Image));
            scrollObject.transform.SetParent(parent, false);

            var scrollTransform = scrollObject.GetComponent<RectTransform>();
            scrollTransform.anchorMin = new Vector2(0f, 0f);
            scrollTransform.anchorMax = new Vector2(1f, 1f);
            scrollTransform.pivot = new Vector2(0.5f, 0.5f);
            scrollTransform.offsetMin = new Vector2(0f, 0f);
            scrollTransform.offsetMax = new Vector2(0f, -48f);

            scrollObject.GetComponent<Image>().color = UiTheme.ScrollBackground;

            var scrollRect = scrollObject.GetComponent<ScrollRect>();

            var viewportObject = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewportObject.transform.SetParent(scrollTransform, false);

            var viewportTransform = viewportObject.GetComponent<RectTransform>();
            viewportTransform.anchorMin = Vector2.zero;
            viewportTransform.anchorMax = Vector2.one;
            viewportTransform.offsetMin = Vector2.zero;
            viewportTransform.offsetMax = Vector2.zero;

            scrollRect.viewport = viewportTransform;

            scrollContent = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter))
                .GetComponent<RectTransform>();
            scrollContent.SetParent(viewportTransform, false);
            scrollContent.anchorMin = new Vector2(0f, 1f);
            scrollContent.anchorMax = new Vector2(1f, 1f);
            scrollContent.pivot = new Vector2(0.5f, 1f);
            scrollContent.sizeDelta = new Vector2(0f, 0f);
            scrollContent.anchoredPosition = Vector2.zero;

            var layoutGroup = scrollContent.GetComponent<VerticalLayoutGroup>();
            layoutGroup.childAlignment = TextAnchor.UpperCenter;
            layoutGroup.childForceExpandWidth = true;
            layoutGroup.childForceExpandHeight = false;
            layoutGroup.spacing = 5f;
            layoutGroup.padding = new RectOffset(4, 4, 4, 4);

            var sizeFitter = scrollContent.GetComponent<ContentSizeFitter>();
            sizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.content = scrollContent;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
        }

        public InfoModule AddTextRow(InfoModule module, string label, string value)
        {
            if (module == null)
            {
                return module;
            }

            var content = module.ContentRect;
            var rowObject = new GameObject("Row: " + label, typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            rowObject.transform.SetParent(content, false);

            var rowRect = rowObject.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(0f, RowHeight);

            var rowElement = rowObject.GetComponent<LayoutElement>();
            rowElement.minHeight = RowHeight;
            rowElement.preferredHeight = RowHeight;
            rowElement.flexibleWidth = 1f;

            var rowLayout = rowObject.GetComponent<HorizontalLayoutGroup>();
            rowLayout.childAlignment = TextAnchor.MiddleLeft;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = false;
            rowLayout.spacing = 4f;

            var labelObj = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObj.transform.SetParent(rowRect, false);
            var labelRect = labelObj.GetComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(54f, 18f);
            var labelText = labelObj.GetComponent<Text>();
            labelText.text = label;
            labelText.alignment = TextAnchor.MiddleLeft;
            labelText.color = UiTheme.LabelText;
            labelText.fontSize = 13;
            labelText.font = FontUtility.GetCjkFont(13);

            var valueObj = new GameObject("Value", typeof(RectTransform), typeof(Text));
            valueObj.transform.SetParent(rowRect, false);
            var valueRect = valueObj.GetComponent<RectTransform>();
            valueRect.sizeDelta = new Vector2(90f, 18f);
            var valueText = valueObj.GetComponent<Text>();
            valueText.text = value;
            valueText.alignment = TextAnchor.MiddleLeft;
            valueText.color = UiTheme.ValueText;
            valueText.fontSize = 13;
            valueText.fontStyle = FontStyle.Bold;
            valueText.font = FontUtility.GetCjkFont(13);

            RebuildLayout();
            return module;
        }

        private void RebuildLayout()
        {
            if (scrollContent == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(scrollContent);
            LayoutRebuilder.ForceRebuildLayoutImmediate(panelTransform);
        }

        private void BuildDemoModules()
        {
            var overview = AddModule("玩家概览");
            AddTextRow(overview, "玩家", "Player 1");
            AddTextRow(overview, "颜色", "蓝色");
            AddTextRow(overview, "分数", "0");

            var resources = AddModule("资源状态");
            AddTextRow(resources, "源岩", "0");
            AddTextRow(resources, "源石", "0");
            AddTextRow(resources, "异铁", "0");
            AddTextRow(resources, "至纯源石", "0");
            AddTextRow(resources, "金券", "10");

            var city = AddModule("城市与行动");
            AddTextRow(city, "城市位置", "A-01");
            AddTextRow(city, "本回合", "1 / 8");
            AddTextRow(city, "行动轮", "1");
            AddTextRow(city, "已执行行动", "否");
            AddTextRow(city, "已移动城市", "否");
        }

        public sealed class InfoModule
        {
            private readonly string title;
            private RectTransform sectionTransform;
            private RectTransform contentTransform;
            private Text titleText;
            private bool isContentVisible = true;

            public string Title => title;
            public RectTransform ContentRect => contentTransform;

            internal InfoModule(string title)
            {
                this.title = title;
            }

            internal void BuildUI(RectTransform parent)
            {
                BuildSection(parent, out contentTransform, out titleText, out var button);
                var captured = this;
                button.onClick.AddListener(() => captured.ToggleContent());
            }

            public void SetContentVisible(bool visible)
            {
                isContentVisible = visible;
                contentTransform.gameObject.SetActive(visible);
                titleText.text = (visible ? "▼ " : "▶ ") + title;
            }

            public void ToggleContent()
            {
                SetContentVisible(!isContentVisible);
            }

            public void SetRowValue(string label, string value)
            {
                if (contentTransform == null)
                {
                    return;
                }

                for (var i = 0; i < contentTransform.childCount; i++)
                {
                    var row = contentTransform.GetChild(i);
                    if (!row.name.StartsWith("Row: " + label))
                    {
                        continue;
                    }

                    var valueTransform = row.Find("Value");
                    if (valueTransform == null)
                    {
                        return;
                    }

                    var text = valueTransform.GetComponent<Text>();
                    if (text != null)
                    {
                        text.text = value ?? string.Empty;
                    }

                    return;
                }
            }

            private void BuildSection(
                RectTransform parent,
                out RectTransform content,
                out Text titleTextRef,
                out Button titleButtonRef)
            {
                var sectionObject = new GameObject("Module: " + title, typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
                sectionObject.transform.SetParent(parent, false);

                sectionTransform = sectionObject.GetComponent<RectTransform>();
                sectionTransform.sizeDelta = new Vector2(0f, 0f);

                var sectionLayout = sectionObject.GetComponent<VerticalLayoutGroup>();
                sectionLayout.childAlignment = TextAnchor.UpperCenter;
                sectionLayout.childForceExpandWidth = true;
                sectionLayout.childForceExpandHeight = false;
                sectionLayout.spacing = 4f;
                sectionLayout.padding = new RectOffset(4, 4, 4, 4);

                var sectionFitter = sectionObject.GetComponent<ContentSizeFitter>();
                sectionFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                var titleObject = new GameObject("Section Title", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline), typeof(LayoutElement));
                titleObject.transform.SetParent(sectionTransform, false);

                var titleTransform = titleObject.GetComponent<RectTransform>();
                titleTransform.sizeDelta = new Vector2(0f, SectionTitleHeight);

                var titleElement = titleObject.GetComponent<LayoutElement>();
                titleElement.minHeight = SectionTitleHeight;
                titleElement.preferredHeight = SectionTitleHeight;
                titleElement.flexibleWidth = 1f;

                var titleBg = titleObject.GetComponent<Image>();
                titleBg.color = UiTheme.SectionTitleBackground;

                var titleOutline = titleObject.GetComponent<Outline>();
                titleOutline.effectColor = UiTheme.GoldOutlineThin;
                titleOutline.effectDistance = new Vector2(1f, -1f);

                var textObject = new GameObject("Title Text", typeof(RectTransform), typeof(Text));
                textObject.transform.SetParent(titleTransform, false);

                var textTransform = textObject.GetComponent<RectTransform>();
                textTransform.anchorMin = Vector2.zero;
                textTransform.anchorMax = Vector2.one;
                textTransform.offsetMin = new Vector2(6f, 0f);
                textTransform.offsetMax = new Vector2(-6f, 0f);

                titleTextRef = textObject.GetComponent<Text>();
                titleTextRef.text = "▼ " + title;
                titleTextRef.alignment = TextAnchor.MiddleLeft;
                titleTextRef.color = UiTheme.GoldText;
                titleTextRef.fontSize = 14;
                titleTextRef.fontStyle = FontStyle.Bold;
                titleTextRef.font = FontUtility.GetCjkFont(14);

                titleButtonRef = titleObject.GetComponent<Button>();
                titleText = titleTextRef;

                content = new GameObject("Section Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter))
                    .GetComponent<RectTransform>();
                content.SetParent(sectionTransform, false);
                content.sizeDelta = new Vector2(0f, 0f);

                var contentLayout = content.GetComponent<VerticalLayoutGroup>();
                contentLayout.childAlignment = TextAnchor.UpperLeft;
                contentLayout.childForceExpandWidth = true;
                contentLayout.childForceExpandHeight = false;
                contentLayout.spacing = 2f;
                contentLayout.padding = new RectOffset(7, 4, 2, 2);

                var contentFitter = content.GetComponent<ContentSizeFitter>();
                contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }
        }

    }
}
